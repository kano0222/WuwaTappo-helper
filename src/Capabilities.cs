using System;
using System.Threading.Tasks;

namespace Tappo {
    internal sealed class Capabilities {
        readonly Func<string,int,Task<object>> evaluate;
        readonly Func<long> now;
        long nextCheck;
        public AppProfile Profile {get;private set;}
        public Capabilities(Func<string,int,Task<object>> evaluate,Func<long> now) {this.evaluate=evaluate;this.now=now;}
        public async Task Ensure(bool force=false) {
            if(!force&&Profile!=null&&now()<nextCheck) return;
            var version=await evaluate("window.__TAURI_INTERNALS__.invoke('plugin:app|version')",8000).ConfigureAwait(false) as string;
            if(string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("无法确认桌宠版本，等待重新检查。");
            if(Profile==null||Profile.version!=version) {
                var found=Json.Convert<DiscoveryResult>(await evaluate(Resources.Text("bridge.discover.js"),15000).ConfigureAwait(false));
                if(found==null||found.profile==null) throw new InvalidOperationException(found==null?"兼容性检查失败":found.error);
                // Publish the new mapping only after its complete capability validation succeeds.
                found.profile.version=version;Profile=found.profile;
            }
            nextCheck=now()+60000;
        }
    }
}
