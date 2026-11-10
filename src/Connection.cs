using System;

namespace Tappo {
    internal sealed class ConnectionFailureException : InvalidOperationException {
        public readonly string Kind;
        public ConnectionFailureException(string kind,string message) : base(message) {Kind=kind;}
    }
    internal sealed class MainWindowNotReadyException : TimeoutException {
        public MainWindowNotReadyException() : base("桌宠还未启动完成，请稍候。") {}
    }
    internal static class Connection {
        public static string RetryMessage(string stage,string kind=null) {
            if(kind=="incompatible")return "暂不支持当前桌宠版本";
            if(kind=="main-not-ready")return "正在等待桌宠启动";
            if(kind!=null||stage=="connect")return "正在重新连接桌宠";
            if(stage=="rejected")return "暂时无法领取，稍后重试";
            if(stage=="local-save"||stage=="boost-save")return "次数未能保存，稍后重试";
            if(stage=="verify"||stage=="reward-verify"||stage=="reward-receipt")return "正在检查宝箱是否领取成功";
            if(stage=="boost-receipt")return "正在检查次数是否补充成功";
            return "暂时无法获取桌宠信息，稍后重试";
        }
        public static string Classify(Exception error) {
            while(error is ReadQueryException&&error.InnerException!=null) error=error.InnerException;
            var failure=error as ConnectionFailureException;
            return failure!=null?failure.Kind:error is MainWindowNotReadyException?"main-not-ready":"connection-failed";
        }
        public static string Hint(Status status,long now) {
            if(status.connectionFailure=="app-mismatch") return "请退出其他国服或国际服桌宠，确认助手选择的程序路径后重试。";
            if(status.connectionFailure=="incompatible") return "请检查助手是否有新版本。";
            if(status.connected!=false&&status.state!=null&&status.state.locked) return "更新完成后自动继续。";
            if(status.disconnectedSince.HasValue) {
                if(status.connectionFailure=="debug-unavailable"&&now-status.disconnectedSince.Value>60000)
                    return "暂时无法连接桌宠。请先停止运行；若桌宠仍开着，请正常退出，再点击“启动后台”。";
                return status.connectionFailure=="main-not-ready"&&now-status.disconnectedSince.Value>60000?
                    "桌宠还未启动完成，请稍候。":"正在重新连接桌宠";
            }
            return null;
        }
    }
}
