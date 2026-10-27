using System;
using System.Linq;
using System.Threading.Tasks;

namespace Tappo {
    internal sealed class Plan { public string kind, side, reason; public int delta, delayMs; }
    internal static class Policy {
        public static readonly string[] Modes={"brute","clicker","human"};
        public static Plan Choose(Snapshot s,string mode,bool boostDue,Func<double> random) {
            if(!Modes.Contains(mode)) throw new InvalidOperationException("未知增加模式");
            if(!s.loggedIn||s.locked) return Wait("等待登录或更新完成");
            if(!new[]{s.available,s.cumulative,s.consumed}.All(v=>v.HasValue&&v>=0&&v<=9007199254740991L)) throw new InvalidOperationException("计数格式异常");
            if(!new[]{s.left,s.right}.All(v=>v.HasValue&&v>=0&&v<=9007199254740991L)) throw new InvalidOperationException("宝箱时间尚未就绪");
            if(s.claimBusy) return Wait("等待当前宝箱领取完成");
            var ready=new[]{"left","right"}.FirstOrDefault(side=>s.Slot(side)<=s.time&&s.Button(side));
            if(ready!=null&&s.available>=1000) return new Plan {kind="claim",side=ready};
            if(s.available<2000&&(mode!="brute"||ready!=null)) {
                if(!boostDue) return Wait("按所选速率补充次数");
                var delta=(int)(2000-s.available.Value);
                if(mode=="brute") return new Plan {kind="boost",delta=delta};
                if(mode=="clicker") return new Plan {kind="boost",delta=Math.Min(100,delta),delayMs=1000};
                if(random()<0.15) return new Plan {kind="wait",reason="波动模式短暂间歇",delayMs=2000+(int)(random()*2000)};
                return new Plan {kind="boost",delta=Math.Min(delta,2+(int)(random()*6)),delayMs=1000+(int)(random()*500)};
            }
            return Wait("等待原有活跃计时及宝箱入口");
        }
        static Plan Wait(string reason) { return new Plan {kind="wait",reason=reason}; }
        public static bool BoostContextMatches(Snapshot before,Snapshot after) {
            return after.loggedIn&&!after.locked&&!after.claimBusy&&after.consumed==before.consumed&&after.left==before.left&&after.right==before.right;
        }
        public static bool BoostConfirmed(int delta,Snapshot before,Snapshot after) {
            return BoostContextMatches(before,after)&&after.cumulative.HasValue&&after.available.HasValue&&
                after.cumulative>=before.cumulative+delta&&after.available>=before.available+delta;
        }
        public static bool BoostOverwritten(int delta,Snapshot before,Snapshot after) {
            return before.cumulative.HasValue&&before.available.HasValue&&after.boostOverwritten&&BoostContextMatches(before,after)&&after.boostAppliedTotal==before.cumulative+delta&&
                after.boostReportTotal==before.cumulative&&after.cumulative==before.cumulative&&after.available==before.available&&
                after.serverTotal==before.cumulative&&after.local==0;
        }
        public static void Confirm(string side,Snapshot before,Snapshot after,ServerState a,ServerState b) {
            if(side!="left"&&side!="right") throw new InvalidOperationException("领取槽位无效");
            a.Validate(); b.Validate();
            var other=side=="left"?"right":"left";
            if(after.consumed!=before.consumed+1000||b.consumed!=a.consumed+1000||b.Slot(side)<=a.Slot(side)||
                after.Slot(side)!=b.Slot(side)||after.Slot(other)!=before.Slot(other)||b.Slot(other)!=a.Slot(other)||after.claimBusy)
                throw new InvalidOperationException("领取结果未能按槽位确认，已暂停且不会自动重发");
        }
        public static string ConfirmRejected(int code,Snapshot before,Snapshot after,ServerState a,ServerState b) {
            a.Validate(); b.Validate();
            if(!new[]{70004,70005,70006}.Contains(code)||after.consumed!=before.consumed||b.consumed!=a.consumed||
                after.left!=before.left||after.right!=before.right||b.left!=a.left||b.right!=a.right)
                throw new InvalidOperationException("服务器拒绝后的状态仍不确定，保留记录且不自动重试");
            return code==70006?"服务器仍判定宝箱冷却中，已暂停；稍后可继续":code==70005?"服务器判定次数不足，已暂停":"服务器判定暂无可领取宝箱，已暂停";
        }
    }
    internal sealed class ChestClock {
        readonly Func<long> now;
        public ChestTimes Value=new ChestTimes(); public long NextPoll;
        public ChestClock(Func<long> now) { this.now=now; }
        public void Reset() { Value=new ChestTimes(); NextPoll=0; }
        public void Accept(ServerState s) {
            if(!new[]{s.left,s.right}.All(v=>v.HasValue&&v>=0&&v<=9007199254740991L)) throw new InvalidOperationException("宝箱时间格式异常");
            Value=new ChestTimes {updatedAt=now(),left=s.left,right=s.right}; NextPoll=now()+30000;
        }
        public async Task Poll(Func<Task<ServerState>> read) {
            if(now()<NextPoll) return;
            try { Accept(await read()); } catch { Value.error="宝箱时间查询失败，等待重新同步"; }
            finally { NextPoll=now()+30000; }
        }
    }
}
