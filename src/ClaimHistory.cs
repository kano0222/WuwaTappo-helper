using System;
using System.Linq;

namespace Tappo {
    internal sealed class ClaimHistory {
        internal sealed class Ledger { public string[] operations; }
        readonly RuntimeStore store;
        readonly string file;
        public ClaimHistory(RuntimeStore store,string appPath) {
            this.store=store.BaseRoot==store.Root?store:new RuntimeStore(store.BaseRoot);
            file="claims-"+(appPath==null?"default":Instances.Key(appPath))+".json";
        }
        string[] Read() {
            var ledger=store.Read<Ledger>(file);
            if(ledger==null) {
                if(store.Exists(file))throw new InvalidOperationException("领取统计无法读取。");
                return new string[0];
            }
            if(ledger.operations==null||ledger.operations.Any(string.IsNullOrEmpty)||ledger.operations.Distinct().Count()!=ledger.operations.Length)
                throw new InvalidOperationException("领取统计格式异常。");
            return ledger.operations;
        }
        public int Total {get {return Read().Length;}}
        // The worker owns the instance lock. Save before clearing pending.json so recovery is idempotent.
        public void Record(string operationId) {
            if(string.IsNullOrEmpty(operationId))throw new InvalidOperationException("领取记录缺少操作标识。");
            var operations=Read();
            if(operations.Contains(operationId))return;
            store.Write(file,new Ledger {operations=operations.Concat(new[]{operationId}).ToArray()});
        }
    }
}
