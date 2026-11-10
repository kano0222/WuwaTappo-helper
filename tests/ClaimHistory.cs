using System;
using System.IO;

namespace Tappo {
    internal static class ClaimHistoryChecks {
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        public static int Run() {
            checks=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-claim-history-"+Guid.NewGuid().ToString("N"));
            try {
                var store=new RuntimeStore(root);var first=Path.Combine(root,"one","WuwaTappo.exe");var second=Path.Combine(root,"two","WuwaTappo.exe");
                var history=new ClaimHistory(store,first);
                Check(history.Total==0,"new history is not empty");
                history.Record("confirmed-one");history.Record("confirmed-one");
                Check(history.Total==1,"duplicate recovery counted twice");
                var reopened=new ClaimHistory(new RuntimeStore(root),first.ToUpperInvariant());
                Check(reopened.Total==1,"history lost after reopening or path casing changed");
                reopened.Record("confirmed-two");
                Check(history.Total==2,"history missing new confirmed operation");
                var other=new ClaimHistory(store,second);
                Check(other.Total==0,"different pet shares total");other.Record("confirmed-one");
                Check(other.Total==1&&history.Total==2,"pet totals overlap");
                var secondary=Instances.Open(root,Instances.Key(first));
                var samePet=new ClaimHistory(secondary,first);
                Check(samePet.Total==2,"moving pet to independent panel lost history");
                samePet.Record("confirmed-two");
                Check(history.Total==2,"independent panel recovery counted twice");
                var file=store.FilePath("claims-"+Instances.Key(first)+".json");
                File.WriteAllText(file,"{\"operations\":[\"confirmed-one\",\"confirmed-one\"]}");
                var rejected=false;try {history.Record("confirmed-three");}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&File.ReadAllText(file).Contains("confirmed-one"),"corrupt history was silently reset");
                File.Delete(file);Directory.CreateDirectory(file);
                rejected=false;try {history.Record("confirmed-three");}catch(IOException){rejected=true;}
                Check(rejected,"failed durable write accepted");Directory.Delete(file);
                history.Record("confirmed-three");history.Record("confirmed-three");
                Check(new ClaimHistory(store,first).Total==1,"failed save recovery counted twice");
                return checks;
            } finally {Directory.Delete(root,true);}
        }
    }
}
