using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tappo {
    internal sealed class ReleaseInfo { public string tag_name,html_url; public bool draft,prerelease; }
    internal sealed class UpdateInfo { public string version,url; public bool available; }
    internal static class Updates {
        internal const string IssuesUrl="https://github.com/kano0222/WuwaTappo-helper/issues";
        internal const string LatestUrl="https://api.github.com/repos/kano0222/WuwaTappo-helper/releases/latest";
        internal static string CurrentVersion {get{return Assembly.GetExecutingAssembly().GetName().Version.ToString(3);}}
        internal static string BuildVersion {
            get {var value=(AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(Assembly.GetExecutingAssembly(),typeof(AssemblyInformationalVersionAttribute));return value==null?CurrentVersion:value.InformationalVersion;}
        }
        internal static Version ParseVersion(string value) {
            Version version;
            if(value==null||!Regex.IsMatch(value,@"^v?\d+\.\d+\.\d+$")||!Version.TryParse(value.TrimStart('v'),out version))
                throw new InvalidOperationException("版本信息格式异常");
            return version;
        }
        internal static UpdateInfo Parse(string json,string current) {
            var release=Json.Decode<ReleaseInfo>(json);
            if(release==null||release.draft||release.prerelease)throw new InvalidOperationException("暂未找到正式版本");
            var version=ParseVersion(release.tag_name);var local=ParseVersion(current);
            var expected="https://github.com/kano0222/WuwaTappo-helper/releases/tag/"+release.tag_name;
            if(release.html_url!=expected)throw new InvalidOperationException("发布地址不匹配");
            return new UpdateInfo {version=version.ToString(3),url=expected,available=version>local};
        }
        internal static async Task<UpdateInfo> Check(HttpMessageHandler handler,string current,CancellationToken cancellation) {
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using(var http=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(8)})
            using(var request=new HttpRequestMessage(HttpMethod.Get,LatestUrl)) {
                deadline.CancelAfter(8000);
                request.Headers.UserAgent.ParseAdd("WuwaTappoHelper/"+current);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false)) {
                    if(response.StatusCode==HttpStatusCode.Forbidden||(int)response.StatusCode==429)throw new InvalidOperationException("GitHub 暂时限制查询，请稍后重试");
                    if(response.StatusCode==HttpStatusCode.NotFound)throw new InvalidOperationException("暂未找到正式版本");
                    response.EnsureSuccessStatusCode();
                    if(response.Content.Headers.ContentLength>262144)throw new InvalidOperationException("版本信息过大");
                    using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using(var output=new System.IO.MemoryStream()) {
                        var buffer=new byte[8192];int count;
                        while((count=await stream.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false))>0) {
                            if(output.Length+count>262144)throw new InvalidOperationException("版本信息过大");output.Write(buffer,0,count);
                        }
                        return Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()),current);
                    }
                }
            }
        }
        internal static Task<UpdateInfo> Check(CancellationToken cancellation) {return Check(new HttpClientHandler(),CurrentVersion,cancellation);}
    }
}
