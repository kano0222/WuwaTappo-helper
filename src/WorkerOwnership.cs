using System;
using System.IO;
using System.Threading;

namespace Tappo {
    internal static class WorkerOwnership {
        const string GuardFile="worker.guard";

        static bool SharingViolation(IOException error) {return error.HResult==unchecked((int)0x80070020);}

        internal static FileStream Acquire(RuntimeStore store) {
            for(int attempt=0;;attempt++) {
                try {return new FileStream(store.FilePath(GuardFile),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException error) {
                    if(!SharingViolation(error))throw;
                    // A read-only observation can briefly overlap an exclusive open.
                    if(attempt==3)throw new InvalidOperationException("自动化已运行或正在启动，请稍后重试。",error);
                    Thread.Sleep(10);
                }
            }
        }

        enum GuardState { Free, Held, Unknown }

        static GuardState ObserveGuard(RuntimeStore store) {
            try {
                using(new FileStream(store.FilePath(GuardFile),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) return GuardState.Free;
            } catch(FileNotFoundException) {return GuardState.Free;}
            catch(IOException error) {return SharingViolation(error)?GuardState.Held:GuardState.Unknown;}
            catch(UnauthorizedAccessException) {return GuardState.Unknown;}
        }

        static bool CompleteIdentity(RuntimeStore store,Status status) {
            var owner=status.identity;
            return owner!=null&&owner.role=="worker"&&owner.pid==status.pid&&owner.pid>0&&owner.processCreated>0&&
                !string.IsNullOrEmpty(owner.session)&&owner.dataDirectory==Instances.Key(store.Runtime);
        }

        static bool LegacyReservationActive(RuntimeStore store) {
            var owner=store.ReadWorkerStatus("lock.json");
            return owner!=null&&owner.identity==null&&RuntimeStore.Alive(owner.pid);
        }

        internal static bool CanStart(RuntimeStore store) {return ObserveGuard(store)==GuardState.Free&&!LegacyReservationActive(store);}

        // Call only while holding the guard. Legacy Node workers did not take it.
        internal static void CheckReservation(RuntimeStore store) {
            if(LegacyReservationActive(store))
                throw new InvalidOperationException("自动化已运行，请先停止现有后台。");
        }

        internal static string Inspect(RuntimeStore store,Status status,Func<RuntimeIdentity,string> inspect=null) {
            if(status==null)return "no-status";
            if(status.identity!=null) {
                if(ObserveGuard(store)==GuardState.Free)return "process-exited";
                if(!CompleteIdentity(store,status))return "identity-unverifiable";
                // An occupied guard can belong to a new worker. Still reject a known stale PID.
                return (inspect??RuntimeIdentity.Inspect)(status.identity);
            }
            if(!RuntimeStore.Alive(status.pid))return "process-exited";
            return "identity-unavailable";
        }

        internal static bool Exited(string identity) {return identity=="process-exited"||identity=="pid-reused";}

        internal static bool MayBeRunning(RuntimeStore store,Status status,Func<RuntimeIdentity,string> inspect=null) {
            return status!=null&&status.running&&!Exited(Inspect(store,status,inspect));
        }

        internal static void BindControl(RuntimeStore store,Status status,Command command,Func<RuntimeIdentity,string> inspect=null) {
            if(status==null||!status.running)throw new InvalidOperationException("后台未运行。");
            var guard=ObserveGuard(store);
            var reservation=store.ReadWorkerStatus("lock.json");
            if(status.identity==null) {
                if(guard==GuardState.Free&&reservation!=null&&reservation.identity==null&&reservation.pid==status.pid&&RuntimeStore.Alive(status.pid))return;
            } else if(guard==GuardState.Held&&CompleteIdentity(store,status)&&reservation!=null&&CompleteIdentity(store,reservation)&&
                reservation.identity.session==status.identity.session&&reservation.pid==status.pid&&
                reservation.identity.processCreated==status.identity.processCreated&&
                !Exited((inspect??RuntimeIdentity.Inspect)(status.identity))) {
                command.targetSession=status.identity.session;
                return;
            }
            throw new InvalidOperationException("无法确认当前后台会话，请导出日志核对。");
        }
    }
}
