using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    public sealed class CloudflareTunnelConfig
    {
        public bool Enabled {get;set;}
        public string Mode {get;set;}
        public string Executable {get;set;}
        public string Hostname {get;set;}
        public string Token {get;set;}
        public CloudflareTunnelConfig(){Mode="quick";}
    }
    public sealed class CloudflareTunnelStatus
    {
        public int Pid {get;set;}
        public long Started {get;set;}
        public string Url {get;set;}
        public string State {get;set;}
        public string Error {get;set;}
        public string Updated {get;set;}
    }
    internal sealed class CloudflareTunnelRecord
    {
        public int Pid {get;set;}
        public long Started {get;set;}
        public string Executable {get;set;}
        public string Arguments {get;set;}
        public int OwnerPid {get;set;}
        public long OwnerStarted {get;set;}
        public string Generation {get;set;}
        public string Fingerprint {get;set;}
        public string Url {get;set;}
        public string State {get;set;}
        public string Error {get;set;}
        public string Updated {get;set;}
        public string RetryAfter {get;set;}
        public int Failures {get;set;}
        public string ConnectingSince {get;set;}
        public List<int> Connections {get;set;}
    }
    internal interface ICloudflareTunnelProcess
    {
        CloudflareTunnelRecord Start(ProcessStartInfo start,Action<string> line);
        bool Matches(CloudflareTunnelRecord record);
        bool OwnerAlive(CloudflareTunnelRecord record);
        bool Stop(CloudflareTunnelRecord record);
    }
    internal sealed class CloudflareTunnelProcess : ICloudflareTunnelProcess
    {
        public CloudflareTunnelRecord Start(ProcessStartInfo start,Action<string> line)
        {
            var process=new Process{StartInfo=start,EnableRaisingEvents=false};
            process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)line(e.Data);};
            process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)line(e.Data);};
            // The handle remains alive while asynchronous stdout/stderr readers
            // are attached. Exited only disposes it; it never starts a new child.
            process.Exited+=(s,e)=>{try{process.Dispose();}catch(InvalidOperationException){}};
            try{
                if(!process.Start())throw new InvalidOperationException("Impossible de demarrer cloudflared.");
                var record=new CloudflareTunnelRecord{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Executable=start.FileName,Arguments=start.Arguments};
                using(var self=Process.GetCurrentProcess()){record.OwnerPid=self.Id;record.OwnerStarted=self.StartTime.ToUniversalTime().Ticks;}
                process.StandardInput.Close();process.BeginOutputReadLine();process.BeginErrorReadLine();process.EnableRaisingEvents=true;return record;
            }catch{try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}process.Dispose();throw;}
        }
        private static bool Pin(Process process,CloudflareTunnelRecord record)
        {
            return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==record.Started&&
                RelayStore.SamePath(process.MainModule.FileName,record.Executable)&&
                (ProcessResources.CommandLine(record.Pid)??"").EndsWith(" "+record.Arguments,StringComparison.Ordinal);
        }
        public bool Matches(CloudflareTunnelRecord record)
        {
            if(record==null||record.Pid<=0||record.Started<=0||String.IsNullOrEmpty(record.Executable)||String.IsNullOrEmpty(record.Arguments))return false;
            try{using(var process=Process.GetProcessById(record.Pid))return Pin(process,record);}catch(Exception e){if(e is ArgumentException||e is InvalidOperationException||e is System.ComponentModel.Win32Exception)return false;throw;}
        }
        public bool OwnerAlive(CloudflareTunnelRecord record){try{return DesktopRuntime.SameProcess(record.OwnerPid,record.OwnerStarted);}catch(System.ComponentModel.Win32Exception){return true;}}
        public bool Stop(CloudflareTunnelRecord record)
        {
            if(record==null||record.Pid<=0)return true;
            try{using(var process=Process.GetProcessById(record.Pid)){if(!Pin(process,record))return false;process.Kill();return process.WaitForExit(5000);}}
            catch(ArgumentException){return true;}catch(InvalidOperationException){return true;}catch(System.ComponentModel.Win32Exception){return false;}
        }
    }
    // Only the dedicated webhook worker calls Ensure. A protected receipt pins
    // the precise child; a different cloudflared process is never adopted/killed.
    internal static class CloudflareTunnel
    {
        internal const string ConfigRecord="webhook-cloudflare-config-v1.dpapi",RuntimeRecord="webhook-cloudflare-runtime-v1.dpapi";
        private static readonly ConcurrentDictionary<string,CloudflareTunnelManager> Managers=new ConcurrentDictionary<string,CloudflareTunnelManager>(StringComparer.OrdinalIgnoreCase);
        internal static CloudflareTunnelConfig Config(RelayStore store){var value=store.ReadRecord<CloudflareTunnelConfig>(ConfigRecord);if(String.IsNullOrEmpty(value.Executable))value.Executable=FindExecutable();return value;}
        internal static string FindExecutable()
        {
            var candidates=new List<string>{Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"cloudflared.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","tools","cloudflared.exe"),@"C:\navigat\cloudflared.exe"};
            foreach(string entry in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))if(!String.IsNullOrWhiteSpace(entry))try{candidates.Add(Path.Combine(entry.Trim('"'),"cloudflared.exe"));}catch(ArgumentException){}
            foreach(string value in candidates)try{if(File.Exists(value)){SafeFiles.RejectLinks(value);return Path.GetFullPath(value);}}catch(IOException){}return null;
        }
        internal static void SaveConfig(RelayStore store,CloudflareTunnelConfig config)
        {
            if(config==null)throw new InvalidOperationException("Configuration Cloudflare manquante.");
            using(store.Lease("webhook-cloudflare-control-v1")){
                var saved=Config(store);var next=Json.Read<CloudflareTunnelConfig>(Json.Write(config));
                if(next.Token==null&&next.Mode=="named")next.Token=saved.Token;
                Validate(next);if(next.Mode=="quick")next.Token=null;
                store.WriteRecord(ConfigRecord,next);
                // Configuration changes authorize a new bounded restart cycle.
                var runtime=store.ReadRecord<CloudflareTunnelRecord>(RuntimeRecord);runtime.Failures=0;runtime.RetryAfter=null;store.WriteRecord(RuntimeRecord,runtime);
            }
        }
        internal static void Validate(CloudflareTunnelConfig config)
        {
            if(config.Mode!="quick"&&config.Mode!="named")throw new InvalidOperationException("Choisissez un tunnel temporaire ou nomme.");
            if(!String.IsNullOrEmpty(config.Executable)){
                if(!Path.IsPathRooted(config.Executable)||!File.Exists(config.Executable)||!String.Equals(Path.GetExtension(config.Executable),".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Choisissez un executable cloudflared existant avec un chemin absolu.");
                SafeFiles.RejectLinks(config.Executable);config.Executable=Path.GetFullPath(config.Executable);
            }
            if(config.Enabled&&String.IsNullOrEmpty(config.Executable))throw new InvalidOperationException("Installez cloudflared puis choisissez son executable.");
            if(config.Mode=="named"){
                if(!String.IsNullOrEmpty(config.Hostname))config.Hostname=NamedUrl(config.Hostname);
                if(config.Enabled&&(String.IsNullOrEmpty(config.Hostname)||!Regex.IsMatch(config.Token??"","^[A-Za-z0-9_+/=-]{16,8192}$")))throw new InvalidOperationException("Indiquez le nom HTTPS et le jeton du tunnel Cloudflare nomme.");
            }else config.Hostname=null;
        }
        internal static string NamedUrl(string value)
        {
            Uri uri;if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.HostNameType!=UriHostNameType.Dns||!uri.Host.Contains(".")||uri.Host.EndsWith(".",StringComparison.Ordinal)||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!="/")throw new InvalidOperationException("Le nom public doit etre une URL HTTPS sans chemin, port ni identifiants.");
            return "https://"+uri.IdnHost.ToLowerInvariant();
        }
        internal static string QuickUrl(string line)
        {
            if(String.IsNullOrEmpty(line)||line.Length>16384)return null;
            var match=Regex.Match(line,@"https://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com(?![a-zA-Z0-9./:@?&#%_-])",RegexOptions.CultureInvariant);
            return match.Success?match.Value:null;
        }
        internal static ProcessStartInfo StartInfo(RelayStore store,CloudflareTunnelConfig config,int port)
        {
            if(port<1024||port>65535)throw new InvalidOperationException("Port du webhook invalide.");Validate(config);
            string directory=Path.Combine(store.Root,"cloudflare-webhooks");SafeFiles.PrivateDirectory(directory);
            string path=Path.Combine(directory,"empty-config.yml");SafeFiles.RejectLinks(path);
            // Explicit empty config isolates the connector from ~/.cloudflared.
            if(!File.Exists(path)||SafeFiles.ReadText(path)!="{}\n")SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes("{}\n"));
            string args="tunnel --config "+RelayWorker.Quote(path)+" --no-autoupdate --metrics 127.0.0.1:0 --loglevel info "+(config.Mode=="quick"?"--url http://127.0.0.1:"+port.ToString(System.Globalization.CultureInfo.InvariantCulture):"run");
            var start=new ProcessStartInfo(config.Executable,args){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=directory,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            // Never inherit tunnel credentials, log destinations or a user-owned
            // cloudflared configuration through environment variables.
            foreach(string key in start.EnvironmentVariables.Keys.Cast<string>().Where(key=>key.StartsWith("TUNNEL_",StringComparison.OrdinalIgnoreCase)||key.StartsWith("CLOUDFLARED_",StringComparison.OrdinalIgnoreCase)||new[]{"NO_AUTOUPDATE","NO_TLS_VERIFY","CODEX_THREAD_ID","CODEX_APP_TOOLS_PIPE_PATH"}.Contains(key,StringComparer.OrdinalIgnoreCase)).ToArray())start.EnvironmentVariables.Remove(key);
            if(config.Mode=="named")start.EnvironmentVariables["TUNNEL_TOKEN"]=config.Token;
            return start;
        }
        internal static string Fingerprint(CloudflareTunnelConfig config,int port)
        {using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(config)+"|"+port.ToString(System.Globalization.CultureInfo.InvariantCulture))));}
        private static CloudflareTunnelManager Manager(RelayStore store){return Managers.GetOrAdd(store.Root,key=>new CloudflareTunnelManager(store,new CloudflareTunnelProcess(),()=>DateTime.UtcNow));}
        internal static CloudflareTunnelStatus Ensure(RelayStore store,int port){return Manager(store).Ensure(port);}
        internal static CloudflareTunnelStatus StopOwned(RelayStore store){return Manager(store).StopOwned();}
        internal static CloudflareTunnelStatus Status(RelayStore store)
        {
            var value=store.ReadRecord<CloudflareTunnelRecord>(RuntimeRecord);var result=SafeStatus(value);
            if(value.Pid>0&&!new CloudflareTunnelProcess().Matches(value)){result.Pid=0;result.Started=0;result.Url=null;result.State="stopped";result.Error="Le connecteur Cloudflare enregistre ne fonctionne plus. Le moteur verifiera son redemarrage.";}
            return result;
        }
        internal static CloudflareTunnelStatus SafeStatus(CloudflareTunnelRecord value){return new CloudflareTunnelStatus{Pid=value.Pid,Started=value.Started,Url=value.Url,State=value.State??"disabled",Error=value.Error,Updated=value.Updated};}
    }
    internal sealed class CloudflareTunnelManager
    {
        private readonly RelayStore store;private readonly ICloudflareTunnelProcess process;private readonly Func<DateTime> clock;private readonly object gate=new object();
        private readonly ConcurrentQueue<KeyValuePair<string,string>> lines=new ConcurrentQueue<KeyValuePair<string,string>>();
        internal CloudflareTunnelManager(RelayStore store,ICloudflareTunnelProcess process,Func<DateTime> clock){this.store=store;this.process=process;this.clock=clock;}
        private string Now(){return clock().ToString("o");}
        private void Receive(string generation,string line){if(line==null||line.Length>16384)return;lines.Enqueue(new KeyValuePair<string,string>(generation,line));while(lines.Count>64){KeyValuePair<string,string> ignored;lines.TryDequeue(out ignored);}}
        private void Save(CloudflareTunnelRecord record){record.Updated=Now();store.WriteRecord(CloudflareTunnel.RuntimeRecord,record);}
        private void ClearProcess(CloudflareTunnelRecord record){record.Pid=0;record.Started=0;record.Url=null;record.Executable=null;record.Arguments=null;record.OwnerPid=0;record.OwnerStarted=0;record.Generation=null;record.ConnectingSince=null;record.Connections=null;}
        private void Failure(CloudflareTunnelRecord record,string error)
        {
            ClearProcess(record);record.Failures++;record.Error=error;
            record.State=record.Failures>=6?"failed":"retrying";
            record.RetryAfter=record.Failures>=6?null:clock().AddSeconds(Math.Min(120,5*(1<<Math.Min(record.Failures-1,5)))).ToString("o");Save(record);
        }
        private void Drain(CloudflareTunnelRecord record,CloudflareTunnelConfig config)
        {
            KeyValuePair<string,string> output;int count=0;
            while(count++<64&&lines.TryDequeue(out output)){
                if(output.Key!=record.Generation)continue;string text=output.Value;
                if(config.Mode=="quick"){string url=CloudflareTunnel.QuickUrl(text);if(url!=null)record.Url=url;}
                var connection=Regex.Match(text,@"(?:connIndex[=:]\s*|""connIndex""\s*:\s*)([0-9]{1,2})",RegexOptions.CultureInvariant);int index=connection.Success?Int32.Parse(connection.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):0;
                if(record.Connections==null)record.Connections=new List<int>();
                if(text.IndexOf("Registered tunnel connection",StringComparison.OrdinalIgnoreCase)>=0&&text.IndexOf("Unregistered tunnel connection",StringComparison.OrdinalIgnoreCase)<0){if(!record.Connections.Contains(index))record.Connections.Add(index);record.State="connected";record.ConnectingSince=null;record.Error=null;if(config.Mode=="named")record.Url=config.Hostname;}
                else if(text.IndexOf("Unregistered tunnel connection",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("Connection terminated",StringComparison.OrdinalIgnoreCase)>=0){record.Connections.Remove(index);if(record.Connections.Count==0){record.State="starting";if(record.ConnectingSince==null)record.ConnectingSince=Now();record.Error="La connexion Cloudflare est interrompue. Reconnexion en cours.";if(config.Mode=="named")record.Url=null;}}
                // Persist only fixed diagnostics: provider output can contain
                // credentials, request headers, prompt text or private URLs.
                if(text.IndexOf("invalid tunnel secret",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("Invalid tunnel token",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("Unauthorized",StringComparison.OrdinalIgnoreCase)>=0){record.Error="Cloudflare refuse le jeton. Verifiez ou renouvelez le jeton du tunnel.";}
                else if(text.IndexOf("Unable to reach the origin service",StringComparison.OrdinalIgnoreCase)>=0)record.Error="Cloudflare ne peut pas joindre le serveur webhook local. Verifiez son port et le routage du tunnel.";
            }
        }
        internal CloudflareTunnelStatus Ensure(int port)
        {
            lock(gate)using(store.Lease("webhook-cloudflare-control-v1")){
                var config=CloudflareTunnel.Config(store);var record=store.ReadRecord<CloudflareTunnelRecord>(CloudflareTunnel.RuntimeRecord);
                if(!config.Enabled)return StopCore(record);
                try{CloudflareTunnel.Validate(config);if(port<1024||port>65535)throw new InvalidOperationException();}
                catch(Exception e){if(!(e is InvalidOperationException)&&!(e is IOException)&&!(e is UnauthorizedAccessException))throw;record.State="failed";record.Error="La configuration Cloudflare est incomplete ou inaccessible. Verifiez executable, mode, nom HTTPS et jeton.";record.Url=null;Save(record);return CloudflareTunnel.SafeStatus(record);}
                string fingerprint=CloudflareTunnel.Fingerprint(config,port);
                bool matching=process.Matches(record);
                if(matching&&record.Fingerprint==fingerprint&&process.OwnerAlive(record)){
                    Drain(record,config);DateTime connecting;
                    if(record.State!="connected"&&DateTime.TryParse(record.ConnectingSince,null,System.Globalization.DateTimeStyles.RoundtripKind,out connecting)&&clock()-connecting>TimeSpan.FromSeconds(120)){
                        if(!process.Stop(record)){record.State="failed";record.Error="Le connecteur Cloudflare ne repond plus et son arret a echoue. Aucun autre connecteur n'a ete demarre.";Save(record);}
                        else Failure(record,"Le connecteur Cloudflare n'a pas etabli sa connexion dans les deux minutes. Verifiez le reseau et le jeton.");
                    }else Save(record);
                    return CloudflareTunnel.SafeStatus(record);
                }
                if(matching){if(!process.Stop(record)){record.State="failed";record.Error="Le connecteur possede par le Switcher ne peut pas etre arrete. Aucun autre processus n'a ete touche.";Save(record);return CloudflareTunnel.SafeStatus(record);}ClearProcess(record);record.Failures=0;record.RetryAfter=null;}
                else if(record.Pid>0){string error=record.Error??"Le connecteur Cloudflare s'est arrete. Nouvelle tentative avec attente progressive.";Failure(record,error);return CloudflareTunnel.SafeStatus(record);}
                DateTime after;if(record.Failures>=6||DateTime.TryParse(record.RetryAfter,null,System.Globalization.DateTimeStyles.RoundtripKind,out after)&&after>clock())return CloudflareTunnel.SafeStatus(record);
                string generation=Guid.NewGuid().ToString("N");
                try{
                    var started=process.Start(CloudflareTunnel.StartInfo(store,config,port),line=>Receive(generation,line));
                    started.Generation=generation;started.Fingerprint=fingerprint;started.Failures=record.Failures;started.State="starting";started.ConnectingSince=Now();started.Url=null;Save(started);Drain(started,config);Save(started);return CloudflareTunnel.SafeStatus(started);
                }catch(Exception e){if(!(e is InvalidOperationException)&&!(e is IOException)&&!(e is UnauthorizedAccessException)&&!(e is System.ComponentModel.Win32Exception))throw;Failure(record,"Le connecteur Cloudflare n'a pas pu demarrer. Verifiez le binaire et l'acces reseau.");return CloudflareTunnel.SafeStatus(record);}
            }
        }
        private CloudflareTunnelStatus StopCore(CloudflareTunnelRecord record)
        {
            bool matching=record.Pid>0&&process.Matches(record);
            if(matching&&!process.Stop(record)){record.State="failed";record.Error="Impossible d'arreter le connecteur Cloudflare du Switcher.";Save(record);return CloudflareTunnel.SafeStatus(record);}
            if(record.Pid>0&&!matching){record.Error="Le processus enregistre n'est plus le connecteur du Switcher. Aucun processus etranger n'a ete arrete.";}else record.Error=null;
            ClearProcess(record);record.State="disabled";record.Failures=0;record.RetryAfter=null;Save(record);return CloudflareTunnel.SafeStatus(record);
        }
        internal CloudflareTunnelStatus StopOwned(){lock(gate)using(store.Lease("webhook-cloudflare-control-v1"))return StopCore(store.ReadRecord<CloudflareTunnelRecord>(CloudflareTunnel.RuntimeRecord));}
    }
}
