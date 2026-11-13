using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Tappo {
    internal static class WebViewExecutable {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct FileInfo {public uint size;[MarshalAs(UnmanagedType.LPWStr)]public string path;public IntPtr file,subject;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct TrustData {
            public uint size;public IntPtr policy,sip;public uint ui,revocation,choice;public IntPtr file;
            public uint action;public IntPtr state,url;public uint flags,context;public IntPtr signature;
        }
        [DllImport("wintrust.dll",ExactSpelling=true)] static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
        internal static bool IsTrusted(string path) {
            if(!string.Equals(Path.GetFileName(path),"msedgewebview2.exe",StringComparison.OrdinalIgnoreCase))return false;
            try {
                // Lock the file against writes/replacement while checking its
                // product metadata, Authenticode signature and signer together.
                using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    var version=FileVersionInfo.GetVersionInfo(path);
                    if(version.ProductName!="Microsoft Edge WebView2"||version.OriginalFilename!="msedgewebview2.exe")return false;
                    var info=new FileInfo {size=(uint)Marshal.SizeOf(typeof(FileInfo)),path=path,file=file.SafeFileHandle.DangerousGetHandle()};
                    var pointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
                    var action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                    var data=new TrustData {size=(uint)Marshal.SizeOf(typeof(TrustData)),ui=2,choice=1,file=pointer,
                        action=1,flags=0x1010}; // No UI/network; cache-only URL retrieval and no revocation fetch.
                    bool initialized=false;
                    try {
                        Marshal.StructureToPtr(info,pointer,false);
                        initialized=true;
                        if(WinVerifyTrust(new IntPtr(-1),ref action,ref data)!=0)return false;
                        using(var certificate=X509Certificate.CreateFromSignedFile(path))
                        using(var signer=new X509Certificate2(certificate))
                            return signer.GetNameInfo(X509NameType.SimpleName,false)=="Microsoft Corporation";
                    } finally {
                        data.action=2;WinVerifyTrust(new IntPtr(-1),ref action,ref data);
                        if(initialized)Marshal.DestroyStructure(pointer,typeof(FileInfo));
                        Marshal.FreeHGlobal(pointer);
                    }
                }
            } catch(IOException) {return false;} catch(UnauthorizedAccessException) {return false;} catch(CryptographicException) {return false;}
        }
    }
}
