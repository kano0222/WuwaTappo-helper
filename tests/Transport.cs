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
            return await RunCase(false).ConfigureAwait(false)+await RunCase(true).ConfigureAwait(false);
        }
        static async Task<int> RunCase(bool disposeWhileReading) {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            using(var timeout=new CancellationTokenSource(5000)) {
                try {
                    var peer=Task.Run(async()=> {
                        using(var tcp=await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using(var stream=tcp.GetStream()) {
                            var bytes=new byte[8192];int count=0;
                            while(!Encoding.ASCII.GetString(bytes,0,count).Contains("\r\n\r\n")) {
                                var read=await stream.ReadAsync(bytes,count,bytes.Length-count,timeout.Token).ConfigureAwait(false);
                                if(read==0||count+read>=bytes.Length)throw new IOException("invalid test handshake");count+=read;
                            }
                            var key=Encoding.ASCII.GetString(bytes,0,count).Split(new[]{"\r\n"},StringSplitOptions.None).Single(l=>l.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                            string accept;using(var sha=SHA1.Create())accept=Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                            var header=Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\n\r\n");
                            await stream.WriteAsync(header,0,header.Length,timeout.Token).ConfigureAwait(false);
                            // Receive one evaluation request, then close without executing or replying.
                            try {await stream.ReadAsync(bytes,0,bytes.Length,timeout.Token).ConfigureAwait(false);}
                            catch(IOException) {if(!disposeWhileReading)throw;}
                            if(disposeWhileReading)return;
                            var close=new byte[]{0x88,0};await stream.WriteAsync(close,0,close.Length,timeout.Token).ConfigureAwait(false);
                        }
                    });
                    var socket=new ClientWebSocket();socket.Options.Proxy=null;
                    await socket.ConnectAsync(new Uri("ws://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/"),timeout.Token).ConfigureAwait(false);
                    var constructor=typeof(AppClient).GetConstructor(BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(ClientWebSocket)},null);
                    using(var client=(AppClient)constructor.Invoke(new object[]{socket})) {
                        typeof(AppClient).GetMethod("StartReceive",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(client,null);
                        if(!client.Connected)throw new Exception("new test transport is not connected");
                        if(disposeWhileReading) {
                            client.Dispose();await Task.Delay(50).ConfigureAwait(false);
                            if(client.Connected)throw new Exception("disposed transport still connected");
                        } else {
                            bool failed=false;try{await client.Evaluate("1",1000).ConfigureAwait(false);}catch(IOException){failed=true;}catch(WebSocketException){failed=true;}
                            if(!failed||client.Connected)throw new Exception("peer close did not fail pending evaluation and invalidate connection");
                            failed=false;try{await client.Evaluate("1",1000).ConfigureAwait(false);}catch(IOException){failed=true;}
                            if(!failed)throw new Exception("dead transport accepted another evaluation");
                        }
                    }
                    await peer.ConfigureAwait(false);return disposeWhileReading?1:3;
                } finally {listener.Stop();}
            }
        }
    }
}
