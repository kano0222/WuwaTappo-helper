using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Tappo {
    internal static class Json {
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 8000000 }; }
        public static string Encode(object value) { return Serializer().Serialize(value); }
        public static T Decode<T>(string text) { return Serializer().Deserialize<T>(text.TrimStart('\uFEFF')); }
        public static T Convert<T>(object value) { return Serializer().ConvertToType<T>(value); }
    }
    internal static class Resources {
        public static string Text(string name) {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) {
                if (stream == null) throw new InvalidOperationException("缺少内嵌资源：" + name);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
    }
    internal sealed class Snapshot {
        public long time; public bool loggedIn, locked, claimBusy, skipped, rejected;
        public long? available, cumulative, local, consumed, serverTotal, left, right;
        public int code; public string reason;
        public Dictionary<string, bool> buttons = new Dictionary<string, bool>();
        public long? Slot(string side) { return side == "left" ? left : right; }
        public bool Button(string side) { return buttons != null && buttons.ContainsKey(side) && buttons[side]; }
    }
    internal sealed class ServerState {
        public long? total, consumed, left, right;
        public long? Slot(string side) { return side == "left" ? left : right; }
        public void Validate() {
            if (!new[] {total,consumed,left,right}.All(v => v.HasValue && v >= 0 && v <= 9007199254740991L)) throw new InvalidOperationException("服务端状态格式异常");
        }
    }
    internal sealed class ChestTimes { public long? updatedAt, left, right; public string error; }
    internal sealed class Status {
        public string at, fault, mode, message; public int pid, claimsThisRun;
        public bool running, paused, normalStartupRestored, automationDisconnected;
        public Snapshot state; public ChestTimes chestTimes;
    }
    internal sealed class Preference { public string mode; }
    internal sealed class Settings { public string appPath; }
    internal sealed class Command { public string id, action; }
    internal sealed class LauncherStatus { public string failedAt, error, requestedAt; public int runnerId; }
    internal sealed class Journal { public string at; public Plan action; public Snapshot before; public ServerState server; }
    internal sealed class RuntimeStore {
        public readonly string Root, Runtime;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public RuntimeStore(string root) { Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); Runtime = Path.Combine(Root,"runtime"); Directory.CreateDirectory(Runtime); }
        public string FilePath(string name) { return Path.Combine(Runtime,name); }
        public bool Exists(string name) { return File.Exists(FilePath(name)); }
        public static string ReadText(string path) {
            using (var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using (var reader = new StreamReader(stream,Encoding.UTF8,true)) return reader.ReadToEnd();
        }
        public T Read<T>(string name) { return File.Exists(FilePath(name)) ? Json.Decode<T>(ReadText(FilePath(name))) : default(T); }
        public static void AtomicWrite(string path, object value) {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    var bytes=Utf8.GetBytes(Json.Encode(value)+"\n"); stream.Write(bytes,0,bytes.Length); stream.Flush(true);
                }
                for (int attempt=0;;attempt++) {
                    try { if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path); break; }
                    catch(IOException) { if(attempt>=6) throw; Thread.Sleep(4<<attempt); }
                }
            } finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        public void Write(string name, object value) { AtomicWrite(FilePath(name),value); }
        public void Begin(Plan action, Snapshot before, ServerState server) {
            // CreateNew protects an existing journal, including records from the previous implementation.
            using(var stream=new FileStream(FilePath("pending.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {
                var bytes=Utf8.GetBytes(Json.Encode(new Journal {at=DateTimeOffset.UtcNow.ToString("o"),action=action,before=before,server=server})+"\n");
                stream.Write(bytes,0,bytes.Length); stream.Flush(true);
            }
        }
        public void ClearPending() { File.Delete(FilePath("pending.json")); }
        public void Log(string name, object data = null) {
            var entry=new Dictionary<string,object> { {"at",DateTimeOffset.UtcNow.ToString("o")},{"event",name} };
            if(data!=null) foreach(var pair in Json.Decode<Dictionary<string,object>>(Json.Encode(data))) entry[pair.Key]=pair.Value;
            Append(FilePath("events.jsonl"),Json.Encode(entry)+"\n");
        }
        public static void Append(string path,string text) {
            using(var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) {
                var bytes=Utf8.GetBytes(text); stream.Write(bytes,0,bytes.Length);
            }
        }
        public static bool Alive(int pid) {
            try { using(var p=Process.GetProcessById(pid)) return !p.HasExited; } catch(ArgumentException) { return false; }
        }
        public static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
    }
}
