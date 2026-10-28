using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Tappo {
    internal static class StartupChecks {
        internal const string ArgumentsVariable="WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
        internal const string DebugArguments="--remote-debugging-address=127.0.0.1 --remote-debugging-port=19222";
        public static int Run() {
            var original=Environment.GetEnvironmentVariable(ArgumentsVariable);
            int checks=0;
            try {
                foreach(var inherited in new[]{null,
                    "--proxy-server=\"http://127.0.0.1:7890\" --disable-gpu",
                    "--remote-debugging-address=0.0.0.0 --remote-debugging-port=9999",
                    "--disable-features=ExampleFeature --user-agent=\"custom agent\" --remote-debugging-port=9999"}) {
                    Environment.SetEnvironmentVariable(ArgumentsVariable,inherited);
                    foreach(var debug in new[]{true,false}) {
                        var info=Platform.AppStartInfo(Platform.Executable,debug);
                        if(info.UseShellExecute||info.WorkingDirectory!=Path.GetDirectoryName(Platform.Executable))throw new Exception("startup cannot use a separate child environment");
                        if(info.EnvironmentVariables[ArgumentsVariable]!=inherited)throw new Exception("startup discarded user browser arguments");
                        if(Environment.GetEnvironmentVariable(ArgumentsVariable)!=inherited)throw new Exception("startup changed the parent environment");
                        // A real child verifies Win32 quoting and argument lifetime without starting the pet.
                        info.Arguments+=" --startup-args-check "+(debug?"debug":"normal")+" "+
                            (inherited==null?"-":Convert.ToBase64String(Encoding.UTF8.GetBytes(inherited)));
                        using(var child=Process.Start(info)) {
                            if(!child.WaitForExit(10000)) {child.Kill();throw new Exception("startup environment child timed out");}
                            if(child.ExitCode!=0)throw new Exception("child did not receive the expected browser arguments");
                        }
                        checks+=4;
                    }
                }
            } finally {Environment.SetEnvironmentVariable(ArgumentsVariable,original);}
            return checks;
        }
    }
}
