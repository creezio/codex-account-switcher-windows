using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class RelayIntegrationState
    {
        public string Home {get;set;}
        public string Fingerprint {get;set;}
        public string Marketplace {get;set;}
        public string Status {get;set;}
        public string Updated {get;set;}
        public bool Healthy {get;set;}
        public string InstalledVersion {get;set;}
    }
    internal static class RelayIntegration
    {
        public const string Name="creezio-relay",Market="creezio-switcher";
        internal static string HomeKey(string home){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(home).ToUpperInvariant()))).Replace("-","").ToLowerInvariant().Substring(0,24);}
        internal static async Task<object> Cli(string executable,string home,string arguments,CancellationToken token)
        {
            var start=new ProcessStartInfo(executable,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=home};
            start.EnvironmentVariables["CODEX_HOME"]=home;start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;
            foreach(string key in new[]{"CODEX_THREAD_ID","CODEX_APP_TOOLS_PIPE_PATH","OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_IDENTITY_TOKEN","OPENAI_IDENTITY_TOKEN_FILE","OPENAI_IDENTITY_PROVIDER_ID","OPENAI_ORGANIZATION","CHATGPT_BASE_URL","CODEX_CHATGPT_BASE_URL"})start.EnvironmentVariables.Remove(key);
            using(var process=Process.Start(start)){
                var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();DateTime deadline=DateTime.UtcNow.AddSeconds(50);
                try{while(!process.HasExited){if(DateTime.UtcNow>deadline)throw new InvalidOperationException("La commande Codex n'a pas répondu dans le délai prévu.");await Task.Delay(100,token);}}
                catch{try{if(!process.HasExited)process.Kill();}catch{}throw;}
                string text=await output;await errors;
                if(process.ExitCode!=0)throw new InvalidOperationException("Codex a refusé l'installation ou l'inventaire du plugin. Vérifiez sa version et la marketplace configurée.");
                return Json.Read<object>(text);
            }
        }
        internal static IEnumerable<object> Objects(object value)
        {
            if(value is System.Collections.IDictionary){yield return value;foreach(var pair in Json.Obj(value))foreach(var child in Objects(pair.Value))yield return child;}
            else foreach(var row in RelayEngine.Rows(value))foreach(var child in Objects(row))yield return child;
        }
        internal static object FindPlugin(object value){return Objects(value).FirstOrDefault(o=>Json.Str(Json.Get(o,"name"))==Name||Json.Str(Json.Get(o,"id"))==Name+"@"+Market);}
        public static RelayIntegrationState Status(RelayStore store,string home){return store.ReadRecord<RelayIntegrationState>("integration-"+HomeKey(home)+".dpapi");}
        internal static bool FilesMatch(string source,string plugin,string exe,string home,string root,string version)
        {
            try{
                foreach(string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){
                    SafeFiles.RejectLinks(file);string relative=file.Substring(source.Length).TrimStart('\\','/'),other=Path.Combine(plugin,relative);SafeFiles.RejectLinks(other);
                    if(!File.Exists(other))return false;
                    if(relative==".mcp.json"||relative==".codex-plugin\\plugin.json")continue;
                    if(!File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(other)))return false;
                }
                var manifest=Json.Read<object>(SafeFiles.ReadText(Path.Combine(plugin,".codex-plugin","plugin.json")));
                var connection=Json.Read<object>(SafeFiles.ReadText(Path.Combine(plugin,"connection.json")));
                var mcp=Json.Get(Json.Get(Json.Read<object>(SafeFiles.ReadText(Path.Combine(plugin,".mcp.json"))),"mcpServers"),Name);
                return Json.Str(Json.Get(manifest,"version"))==version&&Json.Str(Json.Get(connection,"Executable"))==exe&&RelayStore.SamePath(Json.Str(Json.Get(connection,"Home")),home)&&RelayStore.SamePath(Json.Str(Json.Get(connection,"Root")),root)&&Json.Str(Json.Get(mcp,"command"))==exe&&RelayEngine.Rows(Json.Get(mcp,"args")).Select(Json.Str).SequenceEqual(new[]{"--mcp",root,home});
            }catch(IOException){return false;}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
        }
        public static async Task<RelayIntegrationState> Install(RelayStore store,string home,string codex,bool explicitInstall,CancellationToken token)
        {
            home=Path.GetFullPath(home);SafeFiles.RejectLinks(home);if(!Directory.Exists(home)||!File.Exists(codex))throw new InvalidOperationException("Profil Codex ou exécutable absent.");
            string source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugins",Name),exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");
            if(!File.Exists(Path.Combine(source,".codex-plugin","plugin.json"))||!File.Exists(exe))throw new InvalidOperationException("Le dossier plugins et CreezioRelay.exe doivent accompagner le switcher.");
            string key=HomeKey(home),basePath=Path.Combine(Path.GetDirectoryName(store.Root),"integrations",key),plugin=Path.Combine(basePath,"plugins",Name);
            var old=Status(store,home);
            var fingerprintInput=new StringBuilder(RelayWorker.Version+"|"+exe+"|"+home+"|"+store.Root);
            foreach(string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories).OrderBy(x=>x,StringComparer.Ordinal)){SafeFiles.RejectLinks(file);fingerprintInput.Append(Path.GetFileName(file)).Append(SafeFiles.ReadText(file));}
            string fingerprint;using(var sha=SHA256.Create())fingerprint=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprintInput.ToString()))).Replace("-","").ToLowerInvariant().Substring(0,24);
            string version=old.InstalledVersion??(RelayWorker.Version+"+codex."+fingerprint);
            using(store.Lease("install-"+key)){
                if(!String.IsNullOrEmpty(old.Fingerprint)){
                    object listing=await Cli(codex,home,"plugin list --marketplace "+Market+" --json",token),entry=FindPlugin(listing);
                    if(!explicitInstall&&entry!=null&&Object.Equals(Json.Get(entry,"enabled"),false)){old.Status="Intégration désactivée dans Codex · utilisez Réparer pour la réactiver";old.Healthy=false;old.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("integration-"+key+".dpapi",old);return old;}
                    string cache=Path.Combine(home,"plugins","cache",Market,Name,version);
                    if(old.Fingerprint==fingerprint&&entry!=null&&Object.Equals(Json.Get(entry,"enabled"),true)&&Json.Str(Json.Get(entry,"version"))==version&&FilesMatch(source,plugin,exe,home,store.Root,version)&&FilesMatch(source,cache,exe,home,store.Root,version)){old.Status="Intégration installée et vérifiée";old.Healthy=true;old.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("integration-"+key+".dpapi",old);return old;}
                }
                // A new cache version repairs corrupted files without editing a cache in use by a chat.
                version=RelayWorker.Version+"+codex."+fingerprint+"."+Guid.NewGuid().ToString("N").Substring(0,8);
                SafeFiles.PrivateDirectory(basePath);Directory.CreateDirectory(plugin);
                foreach(string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){
                    SafeFiles.RejectLinks(file);string relative=file.Substring(source.Length).TrimStart('\\','/');string destination=Path.Combine(plugin,relative);SafeFiles.RejectLinks(destination);Directory.CreateDirectory(Path.GetDirectoryName(destination));SafeFiles.AtomicWrite(destination,File.ReadAllBytes(file));
                }
                SafeFiles.AtomicWrite(Path.Combine(plugin,"connection.json"),Encoding.UTF8.GetBytes(Json.Write(new{Executable=exe,Root=store.Root,Home=home})));
                var mcp=new{mcpServers=new Dictionary<string,object>{{Name,new{command=exe,args=new[]{"--mcp",store.Root,home}}}}};
                SafeFiles.AtomicWrite(Path.Combine(plugin,".mcp.json"),Encoding.UTF8.GetBytes(Json.Write(mcp)));
                var manifest=Json.Obj(Json.Read<object>(SafeFiles.ReadText(Path.Combine(plugin,".codex-plugin","plugin.json"))));manifest["version"]=version;
                SafeFiles.AtomicWrite(Path.Combine(plugin,".codex-plugin","plugin.json"),Encoding.UTF8.GetBytes(Json.Write(manifest)));
                string marketplace=Path.Combine(basePath,".agents","plugins","marketplace.json");Directory.CreateDirectory(Path.GetDirectoryName(marketplace));
                if(!File.Exists(marketplace))SafeFiles.AtomicWrite(marketplace,Encoding.UTF8.GetBytes(Json.Write(new{name=Market,@interface=new{displayName="Codex Account Switcher"},plugins=new[]{new{name=Name,source=new{source="local",path="./plugins/"+Name},policy=new{installation="AVAILABLE",authentication="ON_INSTALL"},category="Productivity"}}})));
                await Cli(codex,home,"plugin marketplace add "+RelayWorker.Quote(basePath)+" --json",token);
                await Cli(codex,home,"plugin add "+Name+"@"+Market+" --json",token);
                object verified=await Cli(codex,home,"plugin list --marketplace "+Market+" --json",token);
                if(FindPlugin(verified)==null||Object.Equals(Json.Get(FindPlugin(verified),"enabled"),false))throw new InvalidOperationException("Installation non confirmée par Codex.");
                if(Json.Str(Json.Get(FindPlugin(verified),"version"))!=version||!FilesMatch(source,Path.Combine(home,"plugins","cache",Market,Name,version),exe,home,store.Root,version))throw new InvalidOperationException("Les fichiers installés ne correspondent pas au plugin attendu. Vérification à reprendre.");
                var state=new RelayIntegrationState{Home=home,Fingerprint=fingerprint,InstalledVersion=version,Marketplace=marketplace,Status="Intégration mise à jour · ouvrez un nouveau chat pour la charger",Healthy=true,Updated=DateTime.UtcNow.ToString("o")};store.WriteRecord("integration-"+key+".dpapi",state);return state;
            }
        }
        public static async Task<object> Inventory(string codex,string home,CancellationToken token)
        {
            var listing=await Cli(codex,home,"plugin list --json",token);
            return Objects(listing).Where(x=>Json.Get(x,"name")!=null&&(Json.Get(x,"enabled")!=null||Json.Get(x,"installed")!=null)).Select(x=>new{name=Json.Str(Json.Get(x,"name")),enabled=Json.Get(x,"enabled"),installed=Json.Get(x,"installed"),evidence="installed metadata; private resource access not verified"}).ToArray();
        }
    }
}
