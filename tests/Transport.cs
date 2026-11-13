using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tappo {
    internal static class TransportChecks {
        public static async Task<int> Run() {
            var checks=await RunCase(false).ConfigureAwait(false)+await RunCase(true).ConfigureAwait(false);
            try {await RunCase(false,15000,true).ConfigureAwait(false);}
            catch(Exception error) {
                if(error.Message.Contains("deadline expired: True")&&error.Message.Contains("delayed handshake"))return checks+1;
                throw;
            }
            throw new Exception("stalled handshake did not fail the fixture deadline");
        }
        static async Task<int> RunCase(bool disposeWhileReading,int fixtureTimeout=15000,bool stallHandshake=false) {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var scenario=disposeWhileReading?"dispose while receiving":"peer closes pending evaluation";
            var handshakeWritten=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string peerPhase="accept",clientPhase="connect";
            // This bounds fixture I/O on a busy runner; Evaluate keeps its own production timeout.
            using(var timeout=new CancellationTokenSource(fixtureTimeout))
            using(var socket=new ClientWebSocket()) {
                TcpClient accepted=null;Task peer=null;Exception failure=null;int checks=0;
                using(var cancellation=timeout.Token.Register(()=>{listener.Stop();try {socket.Abort();}catch(ObjectDisposedException) { }if(accepted!=null)accepted.Close();}))
                try {
                    // Register accept before connecting, without depending on a queued Task.Run.
                    peer=Serve(listener,timeout.Token,disposeWhileReading,stallHandshake,handshakeWritten,
                        tcp=>accepted=tcp,phase=>{peerPhase=phase;if(stallHandshake&&phase=="delayed handshake")timeout.CancelAfter(100);});
                    socket.Options.Proxy=null;
                    await socket.ConnectAsync(new Uri("ws://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/"),timeout.Token).ConfigureAwait(false);
                    clientPhase="wait for handshake write";
                    await handshakeWritten.Task.ConfigureAwait(false);
                    // Give the connection and close assertions their own fixture budget.
                    timeout.CancelAfter(fixtureTimeout);
                    var constructor=typeof(AppClient).GetConstructor(BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(ClientWebSocket)},null);
                    using(var client=(AppClient)constructor.Invoke(new object[]{socket})) {
                        typeof(AppClient).GetMethod("StartReceive",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(client,null);
                        if(!client.Connected)throw new Exception("new test transport is not connected");
                        if(disposeWhileReading) {
                            clientPhase="dispose pending receive";
                            client.Dispose();
                            if(client.Connected)throw new Exception("disposed transport still connected");
                        } else {
                            clientPhase="evaluate across peer close";
                            bool failed=false;try{await client.Evaluate("1",1000).ConfigureAwait(false);}catch(IOException){failed=true;}catch(WebSocketException){failed=true;}catch(ObjectDisposedException){failed=true;}
                            if(!failed||client.Connected)throw new Exception("peer close did not fail pending evaluation and invalidate connection");
                            failed=false;try{await client.Evaluate("1",1000).ConfigureAwait(false);}catch(IOException){failed=true;}
                            if(!failed)throw new Exception("dead transport accepted another evaluation");
                        }
                    }
                    clientPhase="wait for peer completion";
                    await peer.ConfigureAwait(false);checks=disposeWhileReading?2:3;
                } catch(Exception error) {
                    failure=new Exception("Transport fixture failed ("+scenario+", client: "+clientPhase+", peer: "+peerPhase+", deadline expired: "+timeout.IsCancellationRequested+")",error);
                } finally {
                    timeout.Cancel();
                    listener.Stop();
                }
                // The .NET Framework compiler does not support await in finally.
                if(peer!=null)try {await peer.ConfigureAwait(false);}catch { } // The original failure above remains authoritative.
                if(failure!=null)throw failure;
                return checks;
            }
        }
        static async Task Serve(TcpListener listener,CancellationToken token,bool disposeWhileReading,bool stallHandshake,TaskCompletionSource<bool> handshakeWritten,Action<TcpClient> accepted,Action<string> phase) {
            try {
                using(var tcp=await listener.AcceptTcpClientAsync().ConfigureAwait(false)) {
                    accepted(tcp);
                    using(var stream=tcp.GetStream()) {
                        phase("read handshake");
                        var bytes=new byte[8192];int count=0;
                        while(!Encoding.ASCII.GetString(bytes,0,count).Contains("\r\n\r\n")) {
                            var read=await stream.ReadAsync(bytes,count,bytes.Length-count,token).ConfigureAwait(false);
                            if(read==0||count+read>=bytes.Length)throw new IOException("invalid test handshake");count+=read;
                        }
                        var key=Encoding.ASCII.GetString(bytes,0,count).Split(new[]{"\r\n"},StringSplitOptions.None).Single(l=>l.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                        string accept;using(var sha=SHA1.Create())accept=Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        var header=Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\n\r\n");
                        if(stallHandshake) {phase("delayed handshake");await Task.Delay(Timeout.Infinite,token).ConfigureAwait(false);}
                        phase("write handshake");
                        await stream.WriteAsync(header,0,header.Length,token).ConfigureAwait(false);
                        handshakeWritten.TrySetResult(true);
                        phase(disposeWhileReading?"observe disposed connection":"read evaluation request");
                        int received;
                        try {received=await stream.ReadAsync(bytes,0,bytes.Length,token).ConfigureAwait(false);}
                        catch(IOException) {if(!disposeWhileReading||token.IsCancellationRequested)throw;return;}
                        if(disposeWhileReading) {
                            if(received!=0)throw new Exception("disposed transport sent unexpected data");return;
                        }
                        if(received==0)throw new Exception("transport closed before sending the evaluation request");
                        // Close without executing or replying to the evaluation.
                        phase("write close frame");
                        var close=new byte[]{0x88,0};await stream.WriteAsync(close,0,close.Length,token).ConfigureAwait(false);
                    }
                }
            } catch(Exception error) {
                handshakeWritten.TrySetException(error);throw;
            }
        }
    }
}
