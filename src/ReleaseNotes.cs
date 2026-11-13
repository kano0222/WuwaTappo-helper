using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tappo {
    internal sealed class ReleaseNote { public string version,body; }
    internal sealed class ReleaseNotesRead { public string version; }
    internal sealed class ReleaseNotes : IDisposable {
        readonly string statePath;
        FileStream lease;
        public readonly string Version;
        public readonly ReleaseNote[] Entries;
        ReleaseNotes(string path,FileStream lease,string version,ReleaseNote[] entries) {
            statePath=path;this.lease=lease;Version=version;Entries=entries;
        }
        static bool Valid(string version) {
            if(version==null||!Regex.IsMatch(version,@"^\d+\.\d+\.\d+(-beta\.[1-9]\d*)?$"))return false;
            var parts=version.Split('-');Version parsed;long beta;
            return System.Version.TryParse(parts[0],out parsed)&&(parts.Length==1||long.TryParse(parts[1].Substring(5),out beta));
        }
        internal static int Compare(string left,string right) {
            var a=left.Split('-');var b=right.Split('-');
            var result=Updates.ParseVersion(a[0]).CompareTo(Updates.ParseVersion(b[0]));
            if(result!=0)return result;
            if(a.Length==1)return b.Length==1?0:1;
            if(b.Length==1)return -1;
            return long.Parse(a[1].Substring(5)).CompareTo(long.Parse(b[1].Substring(5)));
        }
        internal static ReleaseNote[] Select(IEnumerable<ReleaseNote> entries,string current,string read) {
            if(!Valid(current))return new ReleaseNote[0]; // Development builds have no published release notes.
            var notes=entries.Where(n=>n!=null&&Valid(n.version)&&!string.IsNullOrWhiteSpace(n.body)).ToArray();
            if(!notes.Any(n=>n.version==current))return new ReleaseNote[0];
            if(!Valid(read))return notes.Where(n=>n.version==current).ToArray();
            return notes.Where(n=>Compare(n.version,read)>0&&Compare(n.version,current)<=0)
                .OrderByDescending(n=>n.version,Comparer<string>.Create(Compare)).ToArray();
        }
        internal static ReleaseNotes Open(string baseRoot,string current,ReleaseNote[] entries) {
            var runtime=Path.Combine(baseRoot,"runtime");Directory.CreateDirectory(runtime);
            FileStream lease;
            try {lease=new FileStream(Path.Combine(runtime,"release-notes.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){return null;} // Another panel is already showing the shared notice.
            try {
                var path=Path.Combine(runtime,"release-notes-read.json");ReleaseNotesRead state=null;
                if(File.Exists(path)) {
                    var text=RuntimeStore.ReadText(path);
                    try {state=Json.Decode<ReleaseNotesRead>(text);}
                    catch(ArgumentException) { }
                    catch(InvalidOperationException) { }
                }
                var selected=Select(entries,current,state==null?null:state.version);
                if(selected.Length==0) {lease.Dispose();return null;}
                return new ReleaseNotes(path,lease,current,selected);
            } catch {lease.Dispose();throw;}
        }
        public void MarkRead() {RuntimeStore.AtomicWrite(statePath,new ReleaseNotesRead {version=Version});}
        public void Dispose() {if(lease!=null){lease.Dispose();lease=null;}}
    }
}
