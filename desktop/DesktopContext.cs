using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher.Desktop
{
    internal sealed class DesktopContext
    {
        internal readonly string Root;
        internal readonly RelayStore Store;
        internal readonly bool Fixture;
        internal string InitialPage="Accueil";
        internal Func<StorageScope[]> StorageScopesFixture;
        // Read the existing project index only. Inventory must never start a
        // Codex instance, fetch a repository or inspect conversation contents.
        internal Task<StorageScope[]> StorageScopes()
        {
            if(Fixture)return Task.FromResult(StorageScopesFixture==null?new StorageScope[0]:StorageScopesFixture());
            return Read(a=>{
                var scopes=new System.Collections.Generic.List<StorageScope>();
                var projects=new System.Collections.Generic.Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                Action<string,string,string,string,bool> add=(id,name,path,kind,protect)=>{
                    if(String.IsNullOrWhiteSpace(path))return;
                    path=Path.GetFullPath(path);
                    if(scopes.Any(s=>RelayStore.SamePath(s.Path,path)))return;
                    scopes.Add(new StorageScope{Id=id,Name=name,Path=path,Kind=kind,Protected=protect});
                };
                foreach(var instance in a.Data.Instances){
                    string home=a.Instances.Home(instance);
                    add("instance:"+instance.Id,instance.Name+(instance.Archived?" · archivée":""),instance.IsLocal?home:InstancePaths.Folder(Root,instance.Id),"instance",true);
                    try{
                        string index=Path.Combine(home,".codex-global-state.json");
                        if(File.Exists(index)){
                            var state=Json.Read<object>(SafeFiles.ReadText(index));
                            foreach(var pair in Json.Obj(Json.Get(state,"local-projects")))
                                foreach(var root in RelayEngine.Rows(Json.Get(pair.Value,"rootPaths"))){
                                    string path=Json.Str(root);if(Path.IsPathRooted(path))projects[Path.GetFullPath(path)]=Json.Str(Json.Get(pair.Value,"name"));
                                }
                        }
                    }catch(System.IO.IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
                }
                foreach(var channel in Store.Channels())if(!String.IsNullOrWhiteSpace(channel.Workspace)&&Path.IsPathRooted(channel.Workspace))projects[Path.GetFullPath(channel.Workspace)]=Path.GetFileName(channel.Workspace.TrimEnd('\\'));
                foreach(string folder in new[]{"runtime","temporary","relay","integrations"})add("switcher:"+folder,"Switcher · "+folder,Path.Combine(Root,folder),"switcher",folder!="runtime"&&folder!="temporary");
                // Nested project roots share their physical files. Keep one
                // inventory, and keep the instance data outside project cleanup.
                foreach(var project in projects.OrderBy(p=>p.Key.Length)){
                    string path=project.Key.TrimEnd('\\');
                    if(scopes.Any(s=>RelayStore.SamePath(s.Path,path)||path.StartsWith(s.Path.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||s.Path.StartsWith(path+"\\",StringComparison.OrdinalIgnoreCase)))continue;
                    add("project:"+path,String.IsNullOrWhiteSpace(project.Value)?Path.GetFileName(path):project.Value,path,"project",true);
                }
                add("system:temp","Windows · temporaires",Path.GetTempPath(),"system",true);
                add("system:pagefile","Windows · fichier d’échange",Path.Combine(Path.GetPathRoot(Root),"pagefile.sys"),"system",true);
                return scopes.ToArray();
            });
        }
        internal LoginBrowser[] LoginBrowsersFixture;
        internal Func<Action<string>,CancellationToken,Task<string>> LoginAuthFixture;
        internal Action<LoginBrowserTarget,string> LoginOpenFixture;
        internal Func<string,Task<string>> HeadlessAccountFixture;
        internal async Task<string> VerifyHeadlessAccount(string id)
        {
            if(Fixture)return await (HeadlessAccountFixture?.Invoke(id)??Task.FromResult("Connexion de test vérifiée."));
            var target=await Read(a=>new{Accounts=a,Instance=a.Data.Instances.Single(i=>i.Id==id&&!i.Archived)});
            return await Task.Run(async()=>{
                using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                using(var client=CloudInstanceConnection.Open(target.Accounts,target.Instance,timeout.Token)){
                    await client.List("user",null,timeout.Token).ConfigureAwait(false);
                    return "Connexion Cloud vérifiée auprès du serveur pour "+target.Accounts.Data.Profiles.Single(p=>p.Key==target.Instance.AccountKey).Email+". Aucun outil de plugin n’a été exécuté.";
                }
            });
        }
        private ResourceLinkServer resourceLinks;
        internal void StopResourceLinks(){resourceLinks?.Dispose();resourceLinks=null;}
        internal Func<DesktopInstance,InstanceState> InstanceStateFixture;
        internal Func<string,Task<string>> InstanceWindowFixture;
        internal Func<string,bool,InstanceState,Task<string>> InstanceLifecycleFixture;
        internal Func<ResourceSnapshot> ResourceSnapshotFixture;
        internal Func<string,Task<ResourceActivity>> ResourceActivityFixture;
        private readonly ProcessResources resourceMonitor=new ProcessResources();
        internal async Task<ResourceSnapshot> Resources()
        {
            if(Fixture)return ResourceSnapshotFixture?.Invoke()??new ResourceSnapshot{Captured=DateTime.UtcNow,Groups=new ResourceGroup[0],Unassigned=new ResourceProcess[0]};
            return await Read(a=>{
                var scopes=a.Data.Instances.Where(i=>!i.Archived).Select(i=>new ResourceScope{Id=i.Id,Name=i.Name,Primary=i.IsLocal,Managed=!i.IsLocal,Home=a.Instances.Home(i),Background=HeadlessProcesses.Roots(Store,a.Instances.Home(i),i.AccountKey),Account=a.Data.Profiles.Where(p=>p.Key==i.AccountKey).Select(p=>p.Email).FirstOrDefault()??"Compte non associé",State=InstanceState(a,i)}).ToList();
                using(var self=System.Diagnostics.Process.GetCurrentProcess())scopes.Add(new ResourceScope{Id="switcher",Name="Account Switcher",Account="Application de gestion",Background=HeadlessProcesses.Controllers(Store),State=new InstanceState{Running=true,DesktopPid=self.Id,DesktopStartTicks=self.StartTime.ToUniversalTime().Ticks,Phase="running"}});
                return resourceMonitor.Capture(scopes.ToArray());
            });
        }
        internal async Task<ResourceActivity> ResourceActivity(string id)
        {
            if(Fixture)return await (ResourceActivityFixture?.Invoke(id)??Task.FromResult(new ResourceActivity()));
            try{
                var target=await Read(a=>new {Accounts=a,Instance=a.Data.Instances.Single(i=>i.Id==id&&!i.Archived)});
                using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5))){
                    var channel=await NamedInstances.Connect(Store,target.Accounts,target.Instance,Environment.CurrentDirectory,deadline.Token);
                    var listed=await new DesktopRelayTransport().Call(channel,"list_threads",new{limit=50},deadline.Token);
                    return Creezio.Switcher.ResourceActivity.FromSidebar(listed);
                }
            }catch(Exception e){return new ResourceActivity{Checked=DateTime.UtcNow,Message="Activité non vérifiable : "+Program.SafeError(e)};}
        }
        internal async Task RequireIdleResourceInstance(string id)
        {
            if(Fixture){var activity=await ResourceActivity(id);if(activity.State!="visible_idle")throw new InvalidOperationException("Un chat travaille ou son activité n’est pas vérifiable.");return;}
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30)))await new TransferBackend(Store).RequireIdleInstance(id,deadline.Token);
        }
        internal async Task<string> CloseOrRestartInstance(string id,bool restart,InstanceState expected)
        {
            if(Fixture)return await (InstanceLifecycleFixture?.Invoke(id,restart,expected)??Task.FromException<string>(new InvalidOperationException("Gestion non configurée dans cette recette.")));
            await gate.WaitAsync();
            try{return await DesktopWindows.CloseOrRestart(Accounts(),id,restart,expected,CancellationToken.None);}finally{gate.Release();}
        }
        internal InstanceState InstanceState(AccountService accounts,DesktopInstance instance)=>Fixture?(InstanceStateFixture?.Invoke(instance)??new InstanceState{Running=false,Phase="stopped"}):accounts.Instances.Runtime.Probe(instance);
        internal async Task<string> OpenOrShowInstance(string id)
        {
            if(Fixture)return await (InstanceWindowFixture?.Invoke(id)??Task.FromException<string>(new InvalidOperationException("Activation non configurée dans cette recette.")));
            await gate.WaitAsync();
            try{return await DesktopWindows.OpenOrShow(Accounts(),id,CancellationToken.None);}finally{gate.Release();}
        }
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private DateTime lastFullRefresh = DateTime.MinValue;
        private DateTime lastStorageMaintenance = DateTime.MinValue;
        private readonly SemaphoreSlim storageGate=new SemaphoreSlim(1,1);
        internal async Task MaintainStorage()
        {
            if(Fixture||DateTime.UtcNow-lastStorageMaintenance<TimeSpan.FromMinutes(10)||!await storageGate.WaitAsync(0))return;
            try{await Task.Run(()=>StorageSupervision.Run(Store,Root,CancellationToken.None));lastStorageMaintenance=DateTime.UtcNow;}
            finally{storageGate.Release();}
        }
        private readonly SemaphoreSlim integrationGate = new SemaphoreSlim(1, 1);
        internal Func<string, Settings, Task> VerifyIntegrationFixture;
        internal async Task<RelayIntegrationState> VerifyIntegration(string instanceId)
        {
            await integrationGate.WaitAsync();
            try {
                var target = await Read(a => {
                    var instance = a.Data.Instances.Single(i => i.Id == instanceId);
                    if (instance.Archived) throw new InvalidOperationException("Restaurez cette instance avant de vérifier son intégration.");
                    return new { a.Settings, Home = a.Instances.Home(instance) };
                });
                if (Fixture) {
                    if (VerifyIntegrationFixture == null) throw new InvalidOperationException("Vérification non configurée dans cette recette.");
                    await VerifyIntegrationFixture(target.Home, target.Settings);
                } else await IntegrationMaintenance.Check(Store, target.Home, target.Settings, true, true, CancellationToken.None);
                // Maintenance persists failures as well as success. Completing the call alone is not success.
                return RelayIntegration.Status(Store, target.Home);
            } finally { integrationGate.Release(); }
        }
        internal async Task MaintainIntegrations(bool force = false, string instanceId = null, bool repair = false)
        {
            if (Fixture) return;
            await integrationGate.WaitAsync();
            try {
                var data = await Read(a => new { a.Settings, Homes = a.Data.Instances.Where(i => !i.Archived && (instanceId == null || i.Id == instanceId)).Select(a.Instances.Home).ToArray() });
                foreach (var home in data.Homes) await IntegrationMaintenance.Check(Store, home, data.Settings, force, repair, CancellationToken.None);
            } finally { integrationGate.Release(); }
        }
        internal DesktopContext(string root, bool fixture = false)
        {
            Root = root;
            Store = new RelayStore(Path.Combine(root, "relay"));
            Fixture = fixture;
        }
        internal AccountService Accounts()
        {
            var s = new AccountService(Root);
            foreach (var p in s.Data.Profiles)
                UsageCoordinator.Merge(p, Store.ReadRecord<Profile>(RelayQuota.Name(p.Key)));
            return s;
        }
        internal async Task<T> Read<T>(Func<AccountService, T> action)
        {
            await gate.WaitAsync();
            try
            {
                return await Task.Run(() => action(Accounts()));
            }
            finally { gate.Release(); }
        }
        internal async Task Mutate(Func<AccountService, Task> action)
        {
            await gate.WaitAsync();
            try
            {
                await action(Accounts());
            }
            finally { gate.Release(); }
        }
        internal async Task Supervise()
        {
            if (Fixture)
                return;
            await MaintainStorage();
            await MaintainIntegrations();
            await gate.WaitAsync();
            try
            {
                await Task.Run(async delegate { var s = Accounts(); if (UsageCoordinator.Policies(Store).Background) { UsageCoordinator.Ensure(Store); return; } bool full = s.Settings.AutoRefresh && DateTime.UtcNow - lastFullRefresh >= TimeSpan.FromMinutes(5); var keys = (full ? s.Data.Profiles : s.Instances.ActiveProfiles()).Select(p => p.Key).ToArray(); foreach (string key in keys) if (full || UsageCoordinator.Automatic(UsageCoordinator.For(Store, key), s.Settings.AutoResetCredits)) await UsageCoordinator.Refresh(Store, key, true, CancellationToken.None); if (full) lastFullRefresh = DateTime.UtcNow; });
            }
            finally { gate.Release(); }
        }
        internal async Task Initialize()
        {
            if (Fixture)
                return;
            if(resourceLinks==null)resourceLinks=ResourceLinkServer.Start(Store);
            if (RemotePeers.Config(Store).Enabled) RemoteGateway.Start(Store);
            await Mutate(a => { a.Instances.AdoptAccounts(); var policy = RelayPolicies.Load(Store); if (policy.KeepWorkerRunning || Store.ActiveMessages().Any() || new Assistance(Store).List().Any(t=>t.State=="answered"||t.State=="delivering")) RelayWorker.Ensure(Store); return Task.CompletedTask; });
            if (await Read(a=>a.Settings.MaintainIntegration)) await MaintainIntegrations(true);
        }
    }
}
