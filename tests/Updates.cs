using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tappo {
    internal sealed class ReleaseHandler : HttpMessageHandler {
        public HttpStatusCode Code=HttpStatusCode.OK; public string Body; public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            token.ThrowIfCancellationRequested();Requests++;
            if(request.RequestUri.ToString()!=Updates.LatestUrl||request.Method!=HttpMethod.Get||request.Headers.Authorization!=null||request.Headers.UserAgent.Count==0)
                throw new Exception("unexpected update request");
            return Task.FromResult(new HttpResponseMessage(Code) {Content=new StringContent(Body??"")});
        }
    }
    internal static class UpdateChecks {
        static void Check(bool condition) {if(!condition)throw new Exception("update check failed");}
        static string Release(string version) {return "{\"tag_name\":\""+version+"\",\"html_url\":\"https://github.com/kano0222/WuwaTappo-helper/releases/tag/"+version+"\",\"draft\":false,\"prerelease\":false}";}
        public static async Task<int> Run() {
            int checks=0;
            Check(Updates.Parse(Release("v0.1.2"),"0.1.1").available);checks++;
            Check(!Updates.Parse(Release("v0.1.1"),"0.1.1").available);checks++;
            Check(!Updates.Parse(Release("v0.1.0"),"0.1.1").available);checks++;
            Check(Updates.Parse(Release("v1.10.0"),"1.2.0").available);checks++;
            Check(Updates.ParseVersion(Updates.CurrentVersion)!=null&&Updates.BuildVersion.StartsWith(Updates.CurrentVersion));checks++;
            var framework=(System.Runtime.Versioning.TargetFrameworkAttribute)Attribute.GetCustomAttribute(typeof(Updates).Assembly,typeof(System.Runtime.Versioning.TargetFrameworkAttribute));
            Check(framework!=null&&framework.FrameworkName==".NETFramework,Version=v4.8");checks++;
            foreach(var json in new[]{"{}",Release("v0.1.2").Replace("false","true"),Release("v0.1.2").Replace("https://github.com/","https://other.invalid/"),Release("v0.1.2-beta"),Release("v0.1.2").Replace("https://github.com/","http://github.com/")}) {
                bool failed=false;try{Updates.Parse(json,"0.1.1");}catch{failed=true;}Check(failed);checks++;
            }
            var handler=new ReleaseHandler {Body=Release("v0.1.2")};
            var value=await Updates.Check(handler,"0.1.1",CancellationToken.None);
            Check(value.available&&handler.Requests==1);checks++;
            foreach(var code in new[]{HttpStatusCode.Forbidden,HttpStatusCode.NotFound,(HttpStatusCode)429,HttpStatusCode.ServiceUnavailable}) {
                bool failed=false;try{await Updates.Check(new ReleaseHandler {Code=code},"0.1.1",CancellationToken.None);}catch{failed=true;}Check(failed);checks++;
            }
            bool oversized=false;try{await Updates.Check(new ReleaseHandler {Body=new string('x',262145)},"0.1.1",CancellationToken.None);}catch(InvalidOperationException){oversized=true;}Check(oversized);checks++;
            using(var cancelled=new CancellationTokenSource()) {
                cancelled.Cancel();bool failed=false;try{await Updates.Check(new ReleaseHandler(),"0.1.1",cancelled.Token);}catch(OperationCanceledException){failed=true;}Check(failed);checks++;
            }
            return checks;
        }
    }
}
