using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class PortalPluginApp
    {
        public string Id {get;set;} public string Name {get;set;} public string InstallUrl {get;set;}
        public bool Accessible {get;set;} public bool Enabled {get;set;} public bool Callable {get;set;}
    }
    public sealed class PortalPluginChoice
    {
        public string Id {get;set;} public string Selector {get;set;} public string RemoteId {get;set;}
        public string Name {get;set;} public string Description {get;set;} public string Version {get;set;}
        public string InstallPolicy {get;set;} public string AuthPolicy {get;set;}
        public bool Installed {get;set;} public bool Enabled {get;set;} public bool CanInstall {get;set;}
        public bool NeedsAuth {get;set;} public bool Callable {get;set;}
        public List<PortalPluginApp> Apps {get;set;} public PortalPluginChoice(){Apps=new List<PortalPluginApp>();}
    }
    public sealed class PortalPluginTarget
    {
        public string InstanceId {get;set;} public string AccountKey {get;set;} public string AccountEmail {get;set;}
        public string Home {get;set;} public string Executable {get;set;} public long GrantRevision {get;set;}
        public long PolicyRevision {get;set;} public string SiteOrigin {get;set;}
    }
    public sealed class PortalPluginCatalog
    {
        public string Route {get;set;} public string Actor {get;set;} public PortalPluginTarget Target {get;set;}
        public string CreatedUtc {get;set;} public List<PortalPluginAdmission> Plugins {get;set;}
        public string[] Warnings {get;set;}
    }
    public sealed class PortalPluginAdmission
    {
        public string Id {get;set;} public string Selector {get;set;} public string RemoteId {get;set;}
        public bool Installed {get;set;} public bool CanInstall {get;set;} public bool NeedsAuth {get;set;}
        public List<PortalPluginApp> Apps {get;set;}
    }
    public sealed class PortalPluginOperation
    {
        public string Id {get;set;} public string Route {get;set;} public string Actor {get;set;}
        public PortalPluginTarget Target {get;set;} public string PluginId {get;set;} public string Selector {get;set;} public string RemoteId {get;set;}
        public string State {get;set;} public string Error {get;set;} public string UpdatedUtc {get;set;}
        public bool NativeStarted {get;set;} public int OwnerPid {get;set;} public long OwnerStarted {get;set;}
        public List<PortalPluginApp> AppsNeedingAuth {get;set;}
        public PortalPluginOperation(){AppsNeedingAuth=new List<PortalPluginApp>();}
    }
    public sealed class PortalPluginBinding
    {
        public string Selector {get;set;} public string RemoteId {get;set;} public string Version {get;set;}
        public List<PortalPluginApp> Apps {get;set;}
    }
    public sealed class PortalPluginBindings
    {
        public string Route {get;set;} public string Actor {get;set;} public PortalPluginTarget Target {get;set;}
        public List<PortalPluginBinding> Plugins {get;set;} public PortalPluginBindings(){Plugins=new List<PortalPluginBinding>();}
    }
    internal sealed class PortalPluginRuntime
    {
        internal List<PortalPluginApp> Apps=new List<PortalPluginApp>();
        internal List<string> Warnings=new List<string>();
    }
    internal sealed class PortalPluginDependencies
    {
        internal Func<string,string,string,PortalPluginTarget> Resolve=null;
        internal Func<PortalPluginTarget,CancellationToken,object> Catalog=PortalPluginCli.Catalog;
        internal Func<PortalPluginTarget,string,CancellationToken,object> Install=PortalPluginCli.Install;
        internal Func<PortalPluginTarget,CancellationToken,PortalPluginRuntime> Runtime=PortalPluginCli.Runtime;
        internal Func<PortalPluginTarget,string[],CancellationToken,IEnumerable<PortalPluginApp>> Metadata=PortalPluginCli.Metadata;
        internal Func<PortalPluginTarget,IDisposable> MutationLease=PortalPluginCli.MutationLease;
        internal Func<DateTime> Clock=()=>DateTime.UtcNow;
    }
    internal static class PortalPluginCli
    {
        private sealed class EmptyLease:IDisposable{public void Dispose(){}}
        internal static IDisposable MutationLease(PortalPluginTarget target)
        {
            string root=Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target.Home)));
            var accounts=new AccountService(root);var instance=NamedInstances.Resolve(accounts,target.InstanceId);
            NamedInstances.VerifyAccount(accounts,instance);var state=accounts.Instances.Runtime.Probe(instance);
            if(state.Phase=="unknown"||state.Phase=="starting"||state.Phase=="stopping")throw new InvalidOperationException("Cette instance est occupee ou son etat est inconnu.");
            // An open desktop owns this mutex already and cannot switch its
            // account. A closed profile must be protected from a concurrent start.
            return state.Running?(IDisposable)new EmptyLease():new InstanceLease(InstancePaths.Folder(root,instance.Id));
        }
        internal static ProcessStartInfo StartInfo(PortalPluginTarget target,string selector)
        {
            if(selector!=null&&!PortalPluginService.ValidSelector(selector))throw new InvalidOperationException("Selection de plugin invalide.");
            var start=HeadlessProcessWire.StartInfo(target.Executable,target.Home);
            start.Arguments="-c "+RelayWorker.Quote("cli_auth_credentials_store=\"file\"")+" plugin "+(selector==null?"list --available --json":"add "+RelayWorker.Quote(selector)+" --json");
            return start;
        }
        private static async Task<string> Output(StreamReader reader,bool retain)
        {
            var text=new StringBuilder();var buffer=new char[4096];int count,total=0;
            while((count=await reader.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false))>0){total+=count;if(retain){if(total>8*1024*1024)throw new IOException("plugin-output-limit");text.Append(buffer,0,count);}}
            return text.ToString();
        }
        private static object Run(PortalPluginTarget target,string selector,CancellationToken token)
        {
            HeadlessRpc.ReadProfile(target.Home,target.AccountKey);
            using(HeadlessProcessWire.AcquireProfileLease(target.Home,token))using(var process=new Process{StartInfo=StartInfo(target,selector)}){
                token.ThrowIfCancellationRequested();process.Start();
                using(var job=new ProcessJob(process)){
                    var output=Output(process.StandardOutput,true);var discard=Output(process.StandardError,false);var clock=Stopwatch.StartNew();
                    while(!process.WaitForExit(100)){token.ThrowIfCancellationRequested();if(clock.Elapsed>TimeSpan.FromSeconds(selector==null?35:120)||output.IsFaulted)throw new IOException("plugin-cli-incomplete");}
                    job.Dispose();string json=output.GetAwaiter().GetResult();discard.GetAwaiter().GetResult();
                    HeadlessRpc.ReadProfile(target.Home,target.AccountKey);
                    if(process.ExitCode!=0)throw new InvalidOperationException("Le CLI Codex n'a pas confirme cette operation. Aucun renvoi automatique.");
#if NETCOREAPP
                    return System.Text.Json.JsonSerializer.Deserialize<object>(json,Desktop.JsonCompatibility.Options);
#else
                    return new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=8*1024*1024}.DeserializeObject(json);
#endif
                }
            }
        }
        internal static object Catalog(PortalPluginTarget target,CancellationToken token)
        {var catalog=Run(target,null,token);ApplyCachedMetadata(target.Home,catalog,DateTime.UtcNow);return catalog;}
        internal static object Install(PortalPluginTarget target,string selector,CancellationToken token){return Run(target,selector,token);}
        internal static IEnumerable<PortalPluginApp> Metadata(PortalPluginTarget target,string[] ids,CancellationToken token)
        {
            var apps=new List<PortalPluginApp>();if(ids.Length==0)return apps;
            using(var rpc=new HeadlessRpc(target.Executable,target.Home,target.AccountKey,token)){
                rpc.Call("account/read",new{refreshToken=false},token).GetAwaiter().GetResult();
                using(var stop=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    stop.CancelAfter(TimeSpan.FromSeconds(15));try{
                        var result=rpc.Call("app/read",new{appIds=ids.Take(100).ToArray(),includeTools=false},stop.Token).GetAwaiter().GetResult();
                        foreach(var app in RelayEngine.Rows(Json.Get(result,"apps"))){string id=Json.Str(Json.Get(app,"id"));if(ids.Contains(id))apps.Add(new PortalPluginApp{Id=id,Name=PortalPluginService.Clip(Json.Get(app,"name"),160),InstallUrl=PortalPluginService.SafeUrl(Json.Str(Json.Get(app,"installUrl")))});}
                    }catch(HeadlessRpcException){}catch(OperationCanceledException){if(token.IsCancellationRequested)throw;}
                }
            }return apps;
        }
        // This optional cache supplies the official source-to-app association
        // missing from CLI list. It never supplies connection state or URLs.
        internal static void ApplyCachedMetadata(string home,object catalog,DateTime now)
        {
            string folder=Path.Combine(home,"cache","remote_plugin_catalog");if(!Directory.Exists(folder))return;
            try{
                SafeFiles.RejectLinks(folder);
                var file=new DirectoryInfo(folder).GetFiles("*.json").Where(item=>Regex.IsMatch(item.Name,@"\A[a-f0-9]{16}\.json\z")).OrderByDescending(item=>item.LastWriteTimeUtc).FirstOrDefault();
                if(file==null||file.Length>64*1024*1024||file.LastWriteTimeUtc<now.AddHours(-1)||file.LastWriteTimeUtc>now.AddMinutes(2))return;SafeFiles.RejectLinks(file.FullName);
                var rows=RelayEngine.Rows(Json.Get(catalog,"installed")).Concat(RelayEngine.Rows(Json.Get(catalog,"available"))).Where(row=>Json.Str(Json.Get(Json.Get(row,"source"),"source"))=="remote").GroupBy(row=>Json.Str(Json.Get(Json.Get(row,"source"),"id"))).Where(group=>group.Key.Length>0).ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.Ordinal);
                using(var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var text=new StreamReader(stream,Encoding.UTF8)){
                    var reader=new CacheReader(text);reader.Expect('{');bool schema=false,fresh=false;var annotations=new List<Action>();int count=0;
                    while(reader.Peek()!='}'){
                        string key=Json.Read<string>(reader.Value());reader.Expect(':');
                        if(key=="plugins"){
                            if(!schema||!fresh)return;reader.Expect('[');
                            while(reader.Peek()!=']'){
                                if(++count>10000)throw new IOException("plugin-cache-count");var item=Json.Read<object>(reader.Value());object[] matches;string id=Json.Str(Json.Get(item,"id"));
                                if(Json.Str(Json.Get(item,"scope"))=="GLOBAL"&&rows.TryGetValue(id,out matches)){
                                    var release=Json.Get(item,"release");string version=Json.Str(Json.Get(release,"version")),canonical=Json.Str(Json.Get(item,"canonical_app_id"));
                                    var ids=RelayEngine.Rows(Json.Get(release,"app_ids")).Select(Json.Str).Where(value=>Regex.IsMatch(value,@"\A(?:connector_|asdk_app_|app_)[A-Za-z0-9_]{1,280}\z")).Distinct().Take(20).ToArray();
                                    foreach(var row in matches.Where(row=>canonical.Length>0&&ids.Contains(canonical)&&Json.Str(Json.Get(row,"version"))==version)){
                                        var mutable=row as Dictionary<string,object>;if(mutable==null)continue;string name=PortalPluginService.Clip(Json.Get(release,"display_name"),160),description=PortalPluginService.Clip(Json.Get(Json.Get(release,"interface"),"short_description"),280);
                                        annotations.Add(()=>{mutable["__creezioAppIds"]=ids;mutable["__creezioDisplayName"]=name;mutable["__creezioDescription"]=description;});
                                    }
                                }
                                if(reader.Peek()==',')reader.Expect(',');else break;
                            }reader.Expect(']');
                        }else{string value=reader.Value();if(key=="schema_version")schema=value=="1";if(key=="fetched_at"){DateTime fetched;fresh=DateTime.TryParse(Json.Read<string>(value),out fetched)&&fetched.ToUniversalTime()>=now.AddHours(-1)&&fetched.ToUniversalTime()<=now.AddMinutes(2);}}
                        if(reader.Peek()==',')reader.Expect(',');else break;
                    }reader.Expect('}');foreach(var apply in annotations)apply();
                }
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
        }
        private sealed class CacheReader
        {
            private readonly TextReader reader;private long consumed;
            internal CacheReader(TextReader value){reader=value;}
            private int Read(){if(++consumed>64*1024*1024)throw new IOException("plugin-cache-limit");return reader.Read();}
            internal int Peek(){while(Char.IsWhiteSpace((char)reader.Peek()))Read();return reader.Peek();}
            internal void Expect(char expected){if(Peek()!=expected)throw new IOException("plugin-cache-contract");Read();}
            internal string Value(){Peek();var value=new StringBuilder();bool quoted=false,escape=false;int depth=0;do{int next=reader.Peek();if(next<0)throw new IOException("plugin-cache-contract");char c=(char)Read();value.Append(c);if(value.Length>256*1024)throw new IOException("plugin-cache-row-limit");if(quoted){if(escape)escape=false;else if(c=='\\')escape=true;else if(c=='"')quoted=false;}else if(c=='"')quoted=true;else if(c=='{'||c=='[')depth++;else if(c=='}'||c==']')depth--;if(depth<0)throw new IOException("plugin-cache-contract");if(!quoted&&depth==0){int following=reader.Peek();if(following<0||following==','||following==':'||following=='}'||following==']'||Char.IsWhiteSpace((char)following))break;}}while(true);return value.ToString();}
        }
        internal static PortalPluginRuntime Runtime(PortalPluginTarget target,CancellationToken token)
        {
            var result=new PortalPluginRuntime();
            using(var rpc=new HeadlessRpc(target.Executable,target.Home,target.AccountKey,token)){
                rpc.Call("account/read",new{refreshToken=false},token).GetAwaiter().GetResult();
                using(var metadataStop=CancellationTokenSource.CreateLinkedTokenSource(token)){
                metadataStop.CancelAfter(TimeSpan.FromSeconds(20));var metadataToken=metadataStop.Token;
                try{
                var callable=new Dictionary<string,object>(StringComparer.Ordinal);object installed=null;
                try{installed=rpc.Call("app/installed",new{forceRefresh=true},metadataToken).GetAwaiter().GetResult();}catch(HeadlessRpcException){result.Warnings.Add("L'etat natif des connexions n'est pas disponible. Aucun statut connecte n'est deduit.");}
                foreach(var app in RelayEngine.Rows(Json.Get(installed,"apps"))){string id=Json.Str(Json.Get(app,"id"));callable[id]=app;result.Apps.Add(new PortalPluginApp{Id=id,Name=PortalPluginService.Clip(Json.Get(app,"runtimeName"),160),Enabled=Object.Equals(Json.Get(app,"enabled"),true),Callable=Object.Equals(Json.Get(app,"callable"),true)});}
                // Installed runtime IDs can be enriched individually even when
                // the broad directory endpoint is unavailable or blocked.
                var installedIds=callable.Keys.Take(100).ToArray();
                for(int offset=0;offset<installedIds.Length;offset+=100)try{
                    var metadata=rpc.Call("app/read",new{appIds=installedIds.Skip(offset).Take(100).ToArray(),includeTools=false},metadataToken).GetAwaiter().GetResult();
                    foreach(var app in RelayEngine.Rows(Json.Get(metadata,"apps"))){var existing=result.Apps.FirstOrDefault(item=>item.Id==Json.Str(Json.Get(app,"id")));if(existing!=null){existing.Name=PortalPluginService.Clip(Json.Get(app,"name"),160);existing.InstallUrl=PortalPluginService.SafeUrl(Json.Str(Json.Get(app,"installUrl")));}}
                }catch(HeadlessRpcException){result.Warnings.Add("Certaines descriptions de connexions ne sont pas disponibles.");break;}
                string cursor=null;var seen=new HashSet<string>();
                try{do{
                    var page=rpc.Call("app/list",new{cursor=cursor,limit=100,forceRefetch=false},metadataToken).GetAwaiter().GetResult();
                    foreach(var app in RelayEngine.Rows(Json.Get(page,"data"))){string id=Json.Str(Json.Get(app,"id"));object runtime;callable.TryGetValue(id,out runtime);
                        if(id.Length==0||id.Length>300||result.Apps.Count>=10000)continue;
                        result.Apps.RemoveAll(item=>item.Id==id);result.Apps.Add(new PortalPluginApp{Id=id,Name=PortalPluginService.Clip(Json.Get(app,"name"),160),InstallUrl=PortalPluginService.SafeUrl(Json.Str(Json.Get(app,"installUrl"))),Accessible=Object.Equals(Json.Get(app,"isAccessible"),true),Enabled=Object.Equals(Json.Get(runtime,"enabled"),true),Callable=Object.Equals(Json.Get(runtime,"callable"),true)});
                    }
                    cursor=Json.Str(Json.Get(page,"nextCursor"));if(cursor.Length>0&&(!seen.Add(cursor)||seen.Count>100))throw new IOException("app-pagination");
                }while(cursor.Length>0);}catch(HeadlessRpcException){result.Warnings.Add("Le catalogue des connexions du compte n'est pas disponible. Reessayez son actualisation.");}
                }catch(OperationCanceledException){if(token.IsCancellationRequested)throw;result.Warnings.Add("Le delai des descriptions de connexions est depasse. Le catalogue CLI reste disponible.");}
                catch(ObjectDisposedException){result.Warnings.Add("Le moteur de descriptions de connexions n'est plus disponible. Aucun statut connecte n'est deduit.");}
                }
            }
            return result;
        }
    }
    internal sealed class PortalPluginService:IDisposable
    {
        private readonly RelayStore store;private readonly string root;
        private readonly Action<string,string> require;private readonly PortalPluginDependencies dependencies;
        private readonly int pid=Process.GetCurrentProcess().Id;private readonly long started=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private readonly ConcurrentDictionary<string,byte> inFlight=new ConcurrentDictionary<string,byte>();
        internal PortalPluginService(RelayStore value,Action<string,string> authorize=null,PortalPluginDependencies adapters=null)
        {store=value;root=Path.GetDirectoryName(store.Root);require=authorize??((route,actor)=>PortalAdministration.RequireActor(store,route,actor));dependencies=adapters??new PortalPluginDependencies();}
        internal static bool ValidSelector(string value){return Regex.IsMatch(value??"",@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,179}@[A-Za-z0-9][A-Za-z0-9._-]{0,119}\z");}
        internal static string Clip(object value,int length){return WebhookChats.Safe(Json.Str(value),length);}
        internal static string SafeUrl(string value)
        {
            Uri uri;if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="chatgpt.com"||!uri.IsDefaultPort||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||!(uri.AbsolutePath.StartsWith("/apps/",StringComparison.Ordinal)||uri.AbsolutePath.StartsWith("/plugins/",StringComparison.Ordinal)))return null;
            return uri.AbsoluteUri;
        }
        private PortalPluginTarget Resolve(string route,string actor,string instanceId)
        {
            var pending=Stopwatch.StartNew();bool waited=false;
            for(;;){
            // A headless history reader briefly owns InstanceLease too. Probe
            // reports that as starting even though no desktop was launched.
            // Reload every authorization/account pin after waiting; never turn
            // an unknown state or an actual launch into permission to proceed.
            require(route,actor);if(!Regex.IsMatch(instanceId??"",@"\A[a-f0-9]{32}\z"))throw new WebhookException(400,"Choisissez une instance isolee valide.");
            if(dependencies.Resolve!=null)return dependencies.Resolve(route,actor,instanceId);
            var policy=PortalAdministration.Policy(store);var config=Portal.Config(store,route);var grant=config.Instances.SingleOrDefault(item=>item.InstanceId==instanceId);
            if(grant==null)throw new WebhookException(404,"Cette instance n'est pas autorisee sur le portail.");
            var accounts=new AccountService(root);var instance=NamedInstances.Resolve(accounts,instanceId);NamedInstances.VerifyAccount(accounts,instance);
            if(instance.IsLocal||!instance.AccountLocked||instance.AccountKey!=grant.AccountKey||!RelayStore.SamePath(accounts.Instances.Home(instance),grant.Home))throw new WebhookException(409,"Le compte ou le dossier de cette instance a change.");
            var state=accounts.Instances.Runtime.Probe(instance);string folder=InstancePaths.Folder(root,instance.Id);
            bool launchEvidence=File.Exists(Path.Combine(folder,"state.json"))||File.Exists(Path.Combine(folder,"launch.json"));
            long remaining=750-pending.ElapsedMilliseconds;
            if(state.Phase=="starting"&&state.HostPid==0&&state.HostStartTicks==0&&state.DesktopPid==0&&state.DesktopStartTicks==0&&String.IsNullOrEmpty(state.LaunchId)&&!state.WindowReady&&!launchEvidence&&remaining>0){Thread.Sleep((int)Math.Min(40,remaining));waited=true;continue;}
            if(state.Phase=="unknown"||state.Phase=="starting"||state.Phase=="stopping"||waited&&launchEvidence)throw new WebhookException(409,"L'instance est occupee ou son etat est inconnu.");
            var profile=accounts.Data.Profiles.Single(item=>item.Key==instance.AccountKey);HeadlessRpc.ReadProfile(grant.Home,grant.AccountKey);
            return new PortalPluginTarget{InstanceId=instance.Id,AccountKey=instance.AccountKey,AccountEmail=Clip(profile.Email,160),Home=grant.Home,Executable=accounts.Settings.CodexExecutable,GrantRevision=grant.AuthorizationRevision,PolicyRevision=policy.Revision,SiteOrigin=policy.SiteOrigin};
            }
        }
        private void Verify(string route,string actor,PortalPluginTarget target)
        {var now=Resolve(route,actor,target.InstanceId);if(!SameTarget(now,target))throw new WebhookException(409,"L'autorisation ou le compte a change. Aucune autre instance n'a ete modifiee.");}
        private static bool SameTarget(PortalPluginTarget left,PortalPluginTarget right)
        {return left!=null&&right!=null&&left.InstanceId==right.InstanceId&&left.AccountKey==right.AccountKey&&RelayStore.SamePath(left.Home,right.Home)&&left.GrantRevision==right.GrantRevision&&left.PolicyRevision==right.PolicyRevision&&left.SiteOrigin==right.SiteOrigin;}
        private static string CatalogName(string route,string actor,string instance){return "portal-plugin-catalog-"+Webhooks.Hash(route+"\n"+actor+"\n"+instance).Substring(0,32)+".dpapi";}
        private static string OpName(string id){return "portal-plugin-install-"+id+".dpapi";}
        private static string BindingsName(string route,string actor,string instance){return "portal-plugin-links-"+Webhooks.Hash(route+"\n"+actor+"\n"+instance).Substring(0,32)+".dpapi";}
        private static string ActiveName(string home){return "portal-plugin-active-"+Webhooks.Hash(home.ToUpperInvariant()).Substring(0,32)+".dpapi";}
        private string Now(){return dependencies.Clock().ToUniversalTime().ToString("o");}
        internal static List<PortalPluginChoice> Choices(object raw,PortalPluginRuntime runtime,string binding,List<string> warnings)
        {
            if(!(raw is System.Collections.IDictionary))raw=Json.Read<object>(Json.Write(raw));
            if(Json.Get(raw,"installed")==null||Json.Get(raw,"available")==null)throw new InvalidOperationException("Le contrat du catalogue CLI Codex a change.");
            var choices=new Dictionary<string,PortalPluginChoice>(StringComparer.Ordinal);int bytes=0;
            var rows=RelayEngine.Rows(Json.Get(raw,"installed")).Concat(RelayEngine.Rows(Json.Get(raw,"available"))).OrderByDescending(row=>Object.Equals(Json.Get(row,"installed"),true)).ThenByDescending(row=>runtime.Apps.Any(app=>Json.Str(Json.Get(Json.Get(row,"source"),"id"))=="plugin_"+app.Id||Json.Str(Json.Get(Json.Get(row,"source"),"id"))=="plugin_asdk_"+app.Id));
            foreach(var row in rows){
                string selector=Json.Str(Json.Get(row,"pluginId")),name=Json.Str(Json.Get(row,"name")),market=Json.Str(Json.Get(row,"marketplaceName"));
                if(!ValidSelector(selector)||selector!=name+"@"+market||choices.ContainsKey(selector))continue;
                string remote=Json.Str(Json.Get(Json.Get(row,"source"),"id"));bool installed=Object.Equals(Json.Get(row,"installed"),true);
                string display=Clip(Json.Get(row,"__creezioDisplayName"),160);var ids=RelayEngine.Rows(Json.Get(row,"__creezioAppIds")).Select(Json.Str).ToArray();
                var apps=runtime.Apps.Where(app=>remote=="plugin_"+app.Id||remote=="plugin_asdk_"+app.Id||remote==app.Id||ids.Contains(app.Id)).ToList();
                if(installed)foreach(string id in ids.Where(id=>!apps.Any(app=>app.Id==id)))apps.Add(new PortalPluginApp{Id=id,Name=display.Length>0?display:Clip(name,160)});
                string policy=Clip(Json.Get(row,"installPolicy"),40);var choice=new PortalPluginChoice{Id=Webhooks.Hash("portal-plugin\n"+binding+"\n"+selector+"\n"+remote),Selector=selector,RemoteId=remote,Name=display.Length>0?display:apps.Count==1&&!String.IsNullOrWhiteSpace(apps[0].Name)?apps[0].Name:Clip(name,160),Description=Clip(Json.Get(row,"__creezioDescription"),280),Version=Clip(Json.Get(row,"version"),80),Installed=installed,Enabled=Object.Equals(Json.Get(row,"enabled"),true),InstallPolicy=policy,AuthPolicy=Clip(Json.Get(row,"authPolicy"),30),Apps=apps};
                choice.Callable=installed&&choice.Enabled&&apps.Count>0&&apps.All(app=>app.Callable&&app.Enabled);
                choice.NeedsAuth=installed&&(apps.Any(app=>!app.Enabled||!app.Callable)||apps.Count==0&&(remote.StartsWith("plugin_connector_1p_",StringComparison.Ordinal)||remote.StartsWith("plugin_asdk_app_",StringComparison.Ordinal)));
                choice.CanInstall=!installed&&policy=="AVAILABLE"&&Json.Str(Json.Get(Json.Get(row,"source"),"source"))=="remote"&&remote.Length>0;
                bytes+=Encoding.UTF8.GetByteCount(Json.Write(PublicChoice(choice)));if(choices.Count>=10000||bytes>3800000){warnings.Add("Catalogue partiel : la limite locale de presentation a ete atteinte.");break;}choices.Add(selector,choice);
            }
            return choices.Values.OrderByDescending(choice=>choice.Installed).ThenBy(choice=>choice.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        internal object Catalog(string route,string actor,string instanceId,CancellationToken token)
        {
            var target=Resolve(route,actor,instanceId);var warnings=new List<string>();var choices=Discover(route,actor,target,token,route+"\n"+actor+"\n"+target.InstanceId+"\n"+target.AccountKey+"\n"+target.GrantRevision,warnings);
            var catalog=new PortalPluginCatalog{Route=route,Actor=actor,Target=target,CreatedUtc=Now(),Plugins=choices.Select(choice=>new PortalPluginAdmission{Id=choice.Id,Selector=choice.Selector,RemoteId=choice.RemoteId,Installed=choice.Installed,CanInstall=choice.CanInstall,NeedsAuth=choice.NeedsAuth,Apps=choice.Apps}).ToList(),Warnings=warnings.Take(20).ToArray()};
            store.WriteRecord(CatalogName(route,actor,instanceId),catalog);
            Reconcile(route,actor,target,choices);
            return new{instanceId=instanceId,accountEmail=target.AccountEmail,plugins=choices.Select(PublicChoice).ToArray(),warnings=catalog.Warnings,generatedUtc=catalog.CreatedUtc};
        }
        private List<PortalPluginChoice> Discover(string route,string actor,PortalPluginTarget target,CancellationToken token,string binding,List<string> warnings)
        {
            var runtime=dependencies.Runtime(target,token);Verify(route,actor,target);var raw=dependencies.Catalog(target,token);Verify(route,actor,target);warnings.AddRange(runtime.Warnings);var choices=Choices(raw,runtime,binding,warnings);
            var missing=choices.Where(choice=>choice.Installed).SelectMany(choice=>choice.Apps).Where(app=>String.IsNullOrEmpty(app.InstallUrl)).Select(app=>app.Id).Distinct().Take(100).ToArray();
            if(missing.Length>0){foreach(var metadata in dependencies.Metadata(target,missing,token)){var app=runtime.Apps.FirstOrDefault(item=>item.Id==metadata.Id);if(app==null){app=new PortalPluginApp{Id=metadata.Id};runtime.Apps.Add(app);}app.Name=metadata.Name;app.InstallUrl=metadata.InstallUrl;}Verify(route,actor,target);choices=Choices(raw,runtime,binding,warnings);}
            ApplyBindings(route,actor,target,choices,runtime);Verify(route,actor,target);return choices;
        }
        private void SaveBinding(PortalPluginOperation operation,IEnumerable<PortalPluginApp> known,string version)
        {
            var apps=known.Take(20).ToList();if(apps.Count==0||String.IsNullOrEmpty(version))return;string file=BindingsName(operation.Route,operation.Actor,operation.Target.InstanceId);
            var bindings=store.ReadRecord<PortalPluginBindings>(file);if(bindings.Target==null||bindings.Route!=operation.Route||bindings.Actor!=operation.Actor||!SameTarget(bindings.Target,operation.Target))bindings=new PortalPluginBindings{Route=operation.Route,Actor=operation.Actor,Target=operation.Target};
            bindings.Plugins.RemoveAll(item=>item.Selector==operation.Selector);bindings.Plugins.Add(new PortalPluginBinding{Selector=operation.Selector,RemoteId=operation.RemoteId,Version=version,Apps=apps});if(bindings.Plugins.Count>200)bindings.Plugins.RemoveAt(0);store.WriteRecord(file,bindings);
        }
        private void ApplyBindings(string route,string actor,PortalPluginTarget target,IEnumerable<PortalPluginChoice> choices,PortalPluginRuntime runtime)
        {
            var bindings=store.ReadRecord<PortalPluginBindings>(BindingsName(route,actor,target.InstanceId));if(bindings.Route!=route||bindings.Actor!=actor||!SameTarget(bindings.Target,target))return;
            foreach(var choice in choices){var binding=bindings.Plugins.SingleOrDefault(item=>item.Selector==choice.Selector&&item.RemoteId==choice.RemoteId&&item.Version==choice.Version&&!String.IsNullOrEmpty(item.Version));if(binding==null||choice.Apps.Count>0)continue;
                choice.Apps=binding.Apps.Select(known=>{var observed=runtime.Apps.FirstOrDefault(app=>app.Id==known.Id);return new PortalPluginApp{Id=known.Id,Name=known.Name,InstallUrl=SafeUrl(known.InstallUrl),Enabled=observed!=null&&observed.Enabled,Callable=observed!=null&&observed.Callable,Accessible=observed!=null&&observed.Accessible};}).ToList();
                if(choice.Apps.Count==1&&!String.IsNullOrWhiteSpace(choice.Apps[0].Name))choice.Name=choice.Apps[0].Name;choice.Callable=choice.Installed&&choice.Enabled&&choice.Apps.Count>0&&choice.Apps.All(app=>app.Enabled&&app.Callable);choice.NeedsAuth=choice.Installed&&choice.Apps.Any(app=>!app.Enabled||!app.Callable);
            }
        }
        private void Reconcile(string route,string actor,PortalPluginTarget target,IEnumerable<PortalPluginChoice> choices)
        {
            var active=store.ReadRecord<PortalPluginOperation>(ActiveName(target.Home));if(active.Id==null)return;
            using(OperationLease(active.Id)){
                var operation=CurrentOperationLocked(active.Id);if(operation.Route!=route||operation.Actor!=actor||!SameTarget(operation.Target,target)||operation.State!="uncertain"&&operation.State!="needs-auth")return;
                var observed=choices.SingleOrDefault(choice=>choice.Selector==operation.Selector&&choice.RemoteId==operation.RemoteId);if(observed==null||!observed.Installed)return;
                Verify(route,actor,target);operation.State=observed.NeedsAuth?"needs-auth":"ready";operation.AppsNeedingAuth=observed.Apps.Where(app=>!app.Enabled||!app.Callable).ToList();operation.Error=null;operation.UpdatedUtc=Now();store.WriteRecord(OpName(operation.Id),operation);
            }
        }
        private static object PublicApp(PortalPluginApp app){return new{id=app.Id,name=app.Name,installUrl=SafeUrl(app.InstallUrl)};}
        private static object PublicChoice(PortalPluginChoice choice)
        {return new{id=choice.Id,name=choice.Name,description=choice.Description,version=choice.Version,installed=choice.Installed,enabled=choice.Enabled,callable=choice.Callable,needsAuth=choice.NeedsAuth,canInstall=choice.CanInstall,installPolicy=choice.InstallPolicy,installUrl=choice.Apps.Select(app=>SafeUrl(app.InstallUrl)).FirstOrDefault(url=>url!=null),appsNeedingAuth=choice.Apps.Where(app=>!app.Enabled||!app.Callable).Select(PublicApp).ToArray()};}
        private static object View(PortalPluginOperation operation)
        {return new{id=operation.Id,instanceId=operation.Target.InstanceId,pluginId=operation.PluginId,state=operation.State,error=operation.Error,appsNeedingAuth=operation.AppsNeedingAuth.Select(PublicApp).ToArray(),nextPollMs=operation.State=="installing"?1000:0};}
        internal object Start(string route,string actor,string instanceId,string pluginId,string key)
        {
            var target=Resolve(route,actor,instanceId);if(!Regex.IsMatch(pluginId??"",@"\A[a-f0-9]{64}\z")||String.IsNullOrEmpty(key)||key.Length>128||key.Any(value=>value<33||value>126))throw new WebhookException(400,"Selection et cle d'idempotence requises.");
            string id=Webhooks.Hash("portal-plugin-operation\n"+route+"\n"+actor+"\n"+instanceId+"\n"+key).Substring(0,32);
            using(store.Lease("portal-plugin-admission")){
                var previous=store.ReadRecord<PortalPluginOperation>(OpName(id));if(previous.Id!=null){if(previous.Route!=route||previous.Actor!=actor||previous.Target.InstanceId!=instanceId||previous.PluginId!=pluginId)throw new WebhookException(409,"Cette cle correspond deja a une autre installation.");Verify(route,actor,previous.Target);return Read(route,actor,instanceId,id);}
                var catalog=store.ReadRecord<PortalPluginCatalog>(CatalogName(route,actor,instanceId));DateTime at;
                if(catalog.Target==null||!DateTime.TryParse(catalog.CreatedUtc,out at)||dependencies.Clock().ToUniversalTime()-at.ToUniversalTime()>TimeSpan.FromMinutes(10))throw new WebhookException(409,"Actualisez le catalogue avant cette installation.");Verify(route,actor,catalog.Target);
                var choice=catalog.Plugins.SingleOrDefault(item=>item.Id==pluginId);if(choice==null||!ValidSelector(choice.Selector)||!choice.Installed&&!choice.CanInstall)throw new WebhookException(403,"Ce plugin n'est pas proposable pour cette instance.");
                var active=store.ReadRecord<PortalPluginOperation>(ActiveName(target.Home));if(active.Id!=null){var pending=store.ReadRecord<PortalPluginOperation>(OpName(active.Id));if(pending.State=="installing"||pending.State=="uncertain")throw new WebhookException(409,"Verifiez d'abord l'installation deja en cours ou incertaine sur cette instance.");}
                var operation=new PortalPluginOperation{Id=id,Route=route,Actor=actor,Target=target,PluginId=pluginId,Selector=choice.Selector,RemoteId=choice.RemoteId,State=choice.Installed?(choice.NeedsAuth?"needs-auth":"ready"):"installing",UpdatedUtc=Now(),OwnerPid=pid,OwnerStarted=started,AppsNeedingAuth=choice.Apps.Where(app=>!app.Callable).ToList()};
                using(OperationLease(id)){store.WriteRecord(OpName(id),operation);store.WriteRecord(ActiveName(target.Home),operation);if(operation.State=="installing")inFlight[id]=0;}
                if(operation.State=="installing")Task.Run(()=>Install(operation));return View(operation);
            }
        }
        private void Install(PortalPluginOperation operation)
        {
            using(var token=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))try{
                token.CancelAfter(TimeSpan.FromMinutes(3));
                Verify(operation.Route,operation.Actor,operation.Target);
                using(dependencies.MutationLease(operation.Target)){
                    Verify(operation.Route,operation.Actor,operation.Target);
                    // Validate the native account before the first CLI mutation.
                    var current=Discover(operation.Route,operation.Actor,operation.Target,token.Token,"preflight",new List<string>()).SingleOrDefault(choice=>choice.Selector==operation.Selector);
                    Verify(operation.Route,operation.Actor,operation.Target);
                    if(current==null||current.RemoteId!=operation.RemoteId||!current.Installed&&!current.CanInstall)throw new InvalidOperationException("La source ou l'autorisation du plugin a change.");
                    if(current.Installed){operation.State=current.NeedsAuth?"needs-auth":"ready";operation.AppsNeedingAuth=current.Apps.Where(app=>!app.Callable).ToList();return;}
                    operation.NativeStarted=true;operation.UpdatedUtc=Now();using(OperationLease(operation.Id))store.WriteRecord(OpName(operation.Id),operation);
                    var result=dependencies.Install(operation.Target,operation.Selector,token.Token);Verify(operation.Route,operation.Actor,operation.Target);
                    foreach(var app in RelayEngine.Rows(Json.Get(result,"appsNeedingAuth")))operation.AppsNeedingAuth.Add(new PortalPluginApp{Id=Clip(Json.Get(app,"id"),300),Name=Clip(Json.Get(app,"name"),160),InstallUrl=SafeUrl(Json.Str(Json.Get(app,"installUrl")))});
                    var observed=Discover(operation.Route,operation.Actor,operation.Target,token.Token,"observation",new List<string>()).SingleOrDefault(choice=>choice.Selector==operation.Selector&&choice.RemoteId==operation.RemoteId);Verify(operation.Route,operation.Actor,operation.Target);
                    if(observed==null||!observed.Installed)throw new InvalidOperationException("Installation non confirmee par le catalogue natif.");
                    SaveBinding(operation,observed.Apps,observed.Version);
                    operation.AppsNeedingAuth=observed.Apps.Where(app=>!app.Enabled||!app.Callable).ToList();
                    operation.State=observed.NeedsAuth||operation.AppsNeedingAuth.Count>0?"needs-auth":"ready";operation.Error=null;
                }
            }catch(Exception){operation.State=operation.NativeStarted?"uncertain":"failed";operation.Error=operation.NativeStarted?"L'installation a pu etre appliquee. Verifiez le catalogue avant toute nouvelle demande ; aucun renvoi automatique.":"L'installation n'a pas commence : compte, autorisation ou disponibilite de l'instance a verifier.";}
            finally{operation.UpdatedUtc=Now();try{using(OperationLease(operation.Id)){store.WriteRecord(OpName(operation.Id),operation);byte ignored;inFlight.TryRemove(operation.Id,out ignored);}}catch(IOException){}catch(InvalidOperationException){}finally{byte ignored;inFlight.TryRemove(operation.Id,out ignored);}}
        }
        internal object Read(string route,string actor,string instanceId,string id)
        {
            RelayStore.MessageId(id);var operation=store.ReadRecord<PortalPluginOperation>(OpName(id));if(operation.Id!=id||operation.Route!=route||operation.Actor!=actor||operation.Target==null||operation.Target.InstanceId!=instanceId)throw new WebhookException(404,"Installation introuvable.");Verify(route,actor,operation.Target);
            return View(CurrentOperation(id));
        }
        private PortalPluginOperation CurrentOperation(string id)
        {using(OperationLease(id))return CurrentOperationLocked(id);}
        private IDisposable OperationLease(string id)
        {
            var clock=Stopwatch.StartNew();for(;;)try{return store.Lease("portal-plugin-reconcile-"+id);}catch(IOException error){int code=error.HResult&65535;if(clock.Elapsed>TimeSpan.FromSeconds(2)||code!=32&&code!=33)throw;Thread.Sleep(10);}
        }
        private PortalPluginOperation CurrentOperationLocked(string id)
        {
            var operation=store.ReadRecord<PortalPluginOperation>(OpName(id));
            if(operation.State=="installing"&&(!DesktopRuntime.SameProcess(operation.OwnerPid,operation.OwnerStarted)||operation.OwnerPid==pid&&operation.OwnerStarted==started&&!inFlight.ContainsKey(id))){
                // The worker persists its final receipt before removing inFlight.
                // Re-read after observing removal, never overwrite that receipt.
                operation=store.ReadRecord<PortalPluginOperation>(OpName(id));if(operation.State=="installing"){
                    operation.State=operation.NativeStarted?"uncertain":"interrupted";operation.Error="Le moteur a ete interrompu. Cette installation ne sera pas relancee automatiquement.";operation.UpdatedUtc=Now();store.WriteRecord(OpName(id),operation);
                }
            }return operation;
        }
        public void Dispose(){lifetime.Cancel();}
    }
}
