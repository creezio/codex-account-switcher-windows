using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // Only the official native login server exchanges the authorization code
    // and owns PKCE. These values exist solely in the live enrollment session.
    internal sealed class PortalEnrollmentBrowserChallenge
    {
        internal string AuthUrl,State;
        internal int Port;
        internal string Origin {get{return "http://127.0.0.1:"+Port;}}
    }
    internal sealed class PortalEnrollmentBrowserResponse
    {internal int Status;internal string Location;}

    internal static class PortalEnrollmentBrowserAuth
    {
        private static WebhookException Bad(){return new WebhookException(400,"Copiez l'adresse de retour complète de cette connexion OpenAI, sans la modifier.");}
        private static Dictionary<string,string> Query(string query)
        {
            var values=new Dictionary<string,string>(StringComparer.Ordinal);if(query.Length<2||query[0]!='?'||query[1]=='?')throw Bad();
            foreach(string part in query.Substring(1).Split('&')){
                int separator=part.IndexOf('=');if(separator<=0||Regex.IsMatch(part,@"%(?![a-fA-F0-9]{2})"))throw Bad();
                string key=Uri.UnescapeDataString(part.Substring(0,separator).Replace('+',' ')),value=Uri.UnescapeDataString(part.Substring(separator+1).Replace('+',' '));
                if(!Regex.IsMatch(key,@"\A[A-Za-z_][A-Za-z0-9_]*\z")||value.Any(Char.IsControl)||values.ContainsKey(key))throw Bad();values.Add(key,value);
            }return values;
        }
        private static string Required(Dictionary<string,string> values,string key)
        {string value;if(!values.TryGetValue(key,out value)||String.IsNullOrEmpty(value))throw Bad();return value;}
        internal static PortalEnrollmentBrowserChallenge Pin(string authUrl)
        {
            Uri uri;if(String.IsNullOrEmpty(authUrl)||authUrl.Length>8192||authUrl.Any(Char.IsControl)||!authUrl.StartsWith("https://auth.openai.com/oauth/authorize?",StringComparison.Ordinal)||!Uri.TryCreate(authUrl,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="auth.openai.com"||uri.Port!=443||uri.AbsolutePath!="/oauth/authorize"||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw Bad();
            var query=Query(authUrl.Substring(authUrl.IndexOf('?')));string state=Required(query,"state"),redirect=Required(query,"redirect_uri");Uri callback;
            if(Required(query,"client_id")!="app_EMoamEEZ73f0CkXaXp7hrann"||Required(query,"response_type")!="code"||Required(query,"code_challenge_method")!="S256"||!Regex.IsMatch(Required(query,"code_challenge"),@"\A[A-Za-z0-9_-]{43}\z")||!Regex.IsMatch(state,@"\A[A-Za-z0-9_-]{32,128}\z")||!Uri.TryCreate(redirect,UriKind.Absolute,out callback)||(callback.Port!=1455&&callback.Port!=1457)||redirect!="http://127.0.0.1:"+callback.Port+"/auth/callback")throw Bad();
            return new PortalEnrollmentBrowserChallenge{AuthUrl=authUrl,State=state,Port=callback.Port};
        }
        internal static Uri Callback(PortalEnrollmentBrowserChallenge challenge,string callbackUrl)
        {
            Uri uri;string prefix=challenge.Origin+"/auth/callback?";
            if(String.IsNullOrEmpty(callbackUrl)||callbackUrl.Length>4096||callbackUrl.Any(Char.IsControl)||!callbackUrl.StartsWith(prefix,StringComparison.Ordinal)||!Uri.TryCreate(callbackUrl,UriKind.Absolute,out uri)||uri.AbsolutePath!="/auth/callback"||uri.Fragment.Length!=0||uri.UserInfo.Length!=0||uri.Host!="127.0.0.1"||uri.Port!=challenge.Port||uri.Scheme!="http")throw Bad();
            // Parse the original query: System.Uri can otherwise repair a bad
            // percent escape before duplicate and grammar validation see it.
            var query=Query(callbackUrl.Substring(prefix.Length-1));if(query.Keys.Any(key=>key!="code"&&key!="state"&&key!="scope"))throw Bad();
            string code=Required(query,"code");if(code.Length>2048||Required(query,"state")!=challenge.State)throw Bad();
            // OAuth's optional scope response is metadata. Never forward a
            // supplied target, path, error, or any other query parameter.
            return new Uri(prefix+"code="+Uri.EscapeDataString(code)+"&state="+Uri.EscapeDataString(challenge.State));
        }
        internal static Uri Success(PortalEnrollmentBrowserChallenge challenge,PortalEnrollmentBrowserResponse response)
        {
            if(response.Status!=302||String.IsNullOrEmpty(response.Location)||response.Location.Length>16384||response.Location.Any(Char.IsControl))throw new InvalidOperationException("native-browser-callback-response");
            Uri uri;if(!Uri.TryCreate(response.Location,UriKind.Absolute,out uri)||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new InvalidOperationException("native-browser-callback-response");
            if(uri.Scheme=="https"&&uri.Host=="chatgpt.com"&&uri.Port==443&&uri.AbsolutePath=="/codex/open-app")return null;
            if(uri.Scheme!="http"||uri.Host!="127.0.0.1"||uri.Port!=challenge.Port||uri.AbsolutePath!="/success"||!response.Location.StartsWith(challenge.Origin+"/success?",StringComparison.Ordinal))throw new InvalidOperationException("native-browser-callback-response");
            // Native Local success URLs can contain an ID JWT. Discard their
            // entire query in memory and never expose or follow that Location.
            return new Uri(challenge.Origin+"/success");
        }
        internal static IDisposable Acquire()
        {
            string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher");
            return Acquire(directory,()=>!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint=>endpoint.Port==1455||endpoint.Port==1457));
        }
        internal static void RequirePortsFree()
        {if(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint=>endpoint.Port==1455||endpoint.Port==1457))throw new WebhookException(409,"Un port de connexion Codex (1455 ou 1457) est déjà utilisé. Terminez la connexion en cours sur le PC avant de recommencer.");}
        internal static IDisposable Acquire(string directory,Func<bool> portsFree)
        {
            SafeFiles.PrivateDirectory(directory);FileStream lease;
            try{lease=new FileStream(Path.Combine(directory,"portal-browser-login.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){throw new WebhookException(409,"Une connexion par navigateur est déjà en cours sur ce PC. Terminez-la ou annulez-la avant de recommencer.");}
            try{if(!portsFree())throw new WebhookException(409,"Un port de connexion Codex (1455 ou 1457) est déjà utilisé. Terminez la connexion en cours sur le PC avant de recommencer.");return lease;}
            catch{lease.Dispose();throw;}
        }
        [DllImport("iphlpapi.dll",SetLastError=true)]
        private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,[MarshalAs(UnmanagedType.Bool)]bool ordered,int family,int tableClass,uint reserved);
        internal static void RequireOwner(int port,int pid,long started)
        {
            if(pid<=0||started<=0||!DesktopRuntime.SameProcess(pid,started))throw new InvalidOperationException("native-browser-listener-owner");
            int size=0;uint status=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);if(status!=122||size<4||size>1048576)throw new InvalidOperationException("native-browser-listener-owner");
            IntPtr table=Marshal.AllocHGlobal(size);try{
                status=GetExtendedTcpTable(table,ref size,false,2,3,0);if(status!=0)throw new InvalidOperationException("native-browser-listener-owner");
                int count=Marshal.ReadInt32(table),matches=0;if(count<0||count>(size-4)/24)throw new InvalidOperationException("native-browser-listener-owner");
                for(int index=0;index<count;index++){
                    IntPtr row=IntPtr.Add(table,4+index*24);int raw=Marshal.ReadInt32(row,8),localPort=((raw&255)<<8)|((raw>>8)&255);
                    if(localPort==port&&Marshal.ReadInt32(row)==2&&new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row,4))).Equals(IPAddress.Loopback)){if(Marshal.ReadInt32(row,20)!=pid)throw new InvalidOperationException("native-browser-listener-owner");matches++;}
                }
                if(matches!=1||!DesktopRuntime.SameProcess(pid,started))throw new InvalidOperationException("native-browser-listener-owner");
            }finally{Marshal.FreeHGlobal(table);}
        }
        internal static async Task<PortalEnrollmentBrowserResponse> Send(Uri target,CancellationToken token)
        {
            if(target.Scheme!="http"||target.Host!="127.0.0.1"||target.UserInfo.Length!=0||target.Fragment.Length!=0||(target.AbsolutePath!="/auth/callback"&&target.AbsolutePath!="/success"))throw new InvalidOperationException("native-browser-target");
            using(var handler=new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false})
            using(var client=new HttpClient(handler)){client.Timeout=TimeSpan.FromSeconds(45);
                using(var request=new HttpRequestMessage(HttpMethod.Get,target))
                using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false))
                    return new PortalEnrollmentBrowserResponse{Status=(int)response.StatusCode,Location=response.Headers.Location==null?null:response.Headers.Location.OriginalString};
            }
        }
    }
}
