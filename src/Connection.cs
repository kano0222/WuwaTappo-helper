using System;

namespace Tappo {
    internal sealed class ConnectionFailureException : InvalidOperationException {
        public readonly string Kind;
        public ConnectionFailureException(string kind,string message) : base(message) {Kind=kind;}
    }
    internal sealed class MainWindowNotReadyException : TimeoutException {
        public MainWindowNotReadyException() : base("桌宠主窗口尚未就绪，请等待桌宠完成启动后再试。") {}
    }
    internal static class Connection {
        public static string Classify(Exception error) {
            while(error is ReadQueryException&&error.InnerException!=null) error=error.InnerException;
            var failure=error as ConnectionFailureException;
            return failure!=null?failure.Kind:error is MainWindowNotReadyException?"main-not-ready":"connection-failed";
        }
        public static string Hint(Status status,long now) {
            if(status.connectionFailure=="incompatible") return "当前桌宠版本暂不兼容，请检查助手更新。";
            if(status.connected!=false&&status.state!=null&&status.state.locked) return "桌宠正在更新，自动操作已暂停。";
            if(status.disconnectedSince.HasValue) {
                if(status.connectionFailure=="debug-unavailable"&&now-status.disconnectedSince.Value>60000)
                    return "暂时无法连接桌宠。如果已完成升级，请先点击“停止后台”，再从桌宠菜单正常退出，最后点击“启动后台”。";
                return status.connectionFailure=="main-not-ready"&&now-status.disconnectedSince.Value>60000?
                    "桌宠主窗口尚未就绪，正在等待桌宠完成启动。":"正在等待桌宠重新连接";
            }
            return null;
        }
    }
}
