using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class InstanceTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected refusal");}
    private sealed class FakeRuntime : IInstanceRuntime
    {
        public readonly HashSet<string> Open=new HashSet<string>();
        public bool RefuseStop;
        public InstanceState Probe(DesktopInstance instance){return new InstanceState{Running=Open.Contains(instance.Id),Phase=Open.Contains(instance.Id)?"running":"stopped"};}
        public Task Start(DesktopInstance instance,CancellationToken token){Open.Add(instance.Id);return Task.FromResult(0);}
        public Task Stop(DesktopInstance instance,CancellationToken token){if(!RefuseStop)Open.Remove(instance.Id);return Task.FromResult(0);}
    }
    private sealed class Fixture
    {
        public readonly AccountService Service;public readonly FakeRuntime Runtime=new FakeRuntime();public readonly InstanceManager Manager;
        public readonly DesktopInstance One,Two;public readonly Profile Alice,Bob;
        public Fixture(string root,string a,string b)
        {
            Service=new AccountService(root,Runtime);Service.Settings.CodexHome=Path.Combine(root,"untouched-local");Directory.CreateDirectory(Service.Settings.CodexHome);
            File.WriteAllText(Path.Combine(Service.Settings.CodexHome,"auth.json"),a);
            Manager=new InstanceManager(Service,Runtime);One=Manager.Create("Travail");Two=Manager.Create("Personnel");
            Alice=Service.Import(a,"Alice");Bob=Service.Import(b,"Bob");
        }
    }
    public static void RunAll(Action<string,Action> check,string root,string a,string b)
    {
        StartupRecoveryTests.RunAll(check,root);
        int counter=0;Func<Fixture> fixture=()=>new Fixture(Path.Combine(root,"instances-"+(counter++)),a,b);
        check("v1 migration preserves credentials, scopes and the encrypted original",delegate {
            string folder=Path.Combine(root,"legacy-instances");var vault=new Vault(folder);
            string legacy=Json.Write(new {Version=1,PreviousAuthJson=b,Profiles=new[]{new{Key=AuthIdentity.Parse(a).Key,Label="Legacy",AuthJson=a}}});
            byte[] encrypted=ProtectedData.Protect(Encoding.UTF8.GetBytes(legacy),Encoding.UTF8.GetBytes("Creezio.CodexAccountSwitcher.v1"),DataProtectionScope.CurrentUser);
            File.WriteAllBytes(vault.FilePath,encrypted);var data=vault.Load();
            Assert(data.Version==2 && data.Profiles[0].AllInstances && data.PreviousAuthJson==b && data.Instances.Single().IsLocal,"legacy data lost");
            Assert(File.ReadAllBytes(vault.FilePath).SequenceEqual(encrypted),"migration wrote before save");vault.Save(data);
            Assert(File.ReadAllBytes(Path.Combine(folder,"accounts-before-instances.dpapi")).SequenceEqual(encrypted),"encrypted backup missing");
        });
        check("all-instances association automatically includes future instances",delegate {var f=fixture();var third=f.Manager.Create("Future");Assert(f.Alice.Allows(third.Id),"future instance excluded");});
        check("restricted associations persist across vault reload",delegate {var f=fixture();f.Manager.SetScope(f.Bob,false,new[]{f.One.Id});var loaded=f.Service.Vault.Load().Profiles.Single(p=>p.Key==f.Bob.Key);Assert(loaded.Allows(f.One.Id)&&!loaded.Allows(f.Two.Id)&&!loaded.Allows("local"),"scope widened");});
        check("unassociated account cannot be configured and no backup changes",delegate {var f=fixture();f.Manager.SetScope(f.Bob,false,new[]{f.One.Id});Throws(()=>f.Manager.Configure(f.Two,f.Bob));Assert(!File.Exists(Path.Combine(f.Manager.Home(f.Two),"auth.json"))&&f.Two.PreviousAuthJson==null,"refused switch wrote data");});
        check("permanent binding rejects switching a closed instance and preserves all profiles",delegate {var f=fixture();f.Manager.Configure(f.One,f.Alice);f.Manager.Configure(f.Two,f.Bob);f.Runtime.Open.Add(f.Two.Id);Throws(()=>f.Manager.Configure(f.One,f.Bob));Assert(File.ReadAllText(Path.Combine(f.Service.Settings.CodexHome,"auth.json"))==a,"primary changed");Assert(File.ReadAllText(Path.Combine(f.Manager.Home(f.Two),"auth.json"))==b&&f.Runtime.Open.Contains(f.Two.Id),"other instance touched");Assert(f.One.AccountKey==f.Alice.Key&&f.Two.PreviousAuthJson==null,"backup leaked between instances");});
        check("running target blocks account writes",delegate {var f=fixture();f.Manager.Configure(f.One,f.Alice);f.Runtime.Open.Add(f.One.Id);Throws(()=>f.Manager.Configure(f.One,f.Bob));Assert(File.ReadAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"))==a,"running instance overwritten");});
        check("instance mutex blocks a second process from rewriting the same profile",delegate {
            var f=fixture();var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
            var worker=new Thread(()=>{using(var lease=new InstanceLease(InstancePaths.Folder(f.Service.Vault.Root,f.One.Id))){entered.Set();release.WaitOne();}});worker.Start();entered.WaitOne();
            try{Throws(()=>f.Manager.Configure(f.One,f.Alice));Assert(!File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json")),"write through live lease");}finally{release.Set();worker.Join();entered.Dispose();release.Dispose();}
        });
        check("archiving preserves account and conversation files and blocks opening",delegate {var f=fixture();f.Manager.Configure(f.One,f.Alice);string history=Path.Combine(f.Manager.Home(f.One),"history.jsonl");File.WriteAllText(history,"preserve");f.Manager.Archive(f.One,true);Throws(()=>InstanceRules.RequireAvailable(f.Alice,f.One));Assert(File.ReadAllText(history)=="preserve"&&File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json")),"archive deleted data");f.Manager.Archive(f.One,false);Assert(!f.One.Archived,"restore failed");});
        check("running instances and built-in local session cannot be archived",delegate {var f=fixture();f.Runtime.Open.Add(f.One.Id);Throws(()=>f.Manager.Archive(f.One,true));Throws(()=>f.Manager.Archive(f.Service.Data.Instances[0],true));});
        check("revoking a live association is refused atomically",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Runtime.Open.Add(f.One.Id);Throws(()=>f.Manager.SetScope(f.Bob,false,new[]{f.Two.Id}));Assert(f.Bob.AllInstances,"partly revoked permissions");});
        check("revoking a permanent association is refused even when closed",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);Throws(()=>f.Manager.SetScope(f.Bob,false,new[]{f.Two.Id}));Assert(File.ReadAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"))==b&&!f.Runtime.Open.Contains(f.One.Id),"unauthorized reopening");});
        check("same account in several active instances is polled only once",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Manager.Configure(f.Two,f.Bob);f.Runtime.Open.Add(f.One.Id);f.Runtime.Open.Add(f.Two.Id);Assert(f.Manager.ActiveProfiles().Count(p=>p.Key==f.Bob.Key)==1,"duplicate reset target");});
        check("closed managed account is not eligible for automatic reset",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);Assert(!f.Manager.IsResetActive(f.Bob),"closed account spends resets");f.Runtime.Open.Add(f.One.Id);Assert(f.Manager.IsResetActive(f.Bob),"open account omitted");});
        check("recommendation excludes restricted and stale accounts",delegate {var f=fixture();f.Manager.SetScope(f.Bob,false,new[]{f.Two.Id});foreach(var p in new[]{f.Alice,f.Bob}){p.QuotaTimeUtc=DateTime.UtcNow.ToString("o");p.Quotas=new List<QuotaBucket>{new QuotaBucket{Name="codex",Primary=new QuotaWindow{Remaining=p==f.Bob?99:40}}};}Assert(InstanceRules.Best(f.Service.Data,f.One)==f.Alice,"restricted recommendation");f.Alice.QuotaTimeUtc=DateTime.UtcNow.AddHours(-2).ToString("o");Assert(InstanceRules.Best(f.Service.Data,f.One)==null,"stale recommendation");});
        check("capturing a manually connected account restricts a new profile to its instance",delegate {var f=fixture();f.Service.Data.Profiles.Remove(f.Bob);File.WriteAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"),b);var captured=f.Manager.Capture(f.One);Assert(!captured.AllInstances&&captured.Allows(f.One.Id)&&!captured.Allows(f.Two.Id),"captured scope too broad");});
        check("newer auth for the same identity is synced without modifying any instance",delegate {
            var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Manager.Configure(f.Two,f.Bob);
            var updated=Json.Read<Dictionary<string,object>>(b);updated["last_refresh"]=DateTime.UtcNow.AddMinutes(2).ToString("o");Json.Obj(updated["tokens"])["access_token"]="fictional-new-token";
            string fresh=Json.Write(updated);File.WriteAllText(Path.Combine(f.Manager.Home(f.Two),"auth.json"),fresh);f.Manager.SyncFreshAuth(f.Bob);
            Assert(f.Bob.AuthJson==fresh&&File.ReadAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"))==b&&File.ReadAllText(Path.Combine(f.Service.Settings.CodexHome,"auth.json"))==a,"sync changed live files");
        });
        check("forgetting an account does not delete its instance data",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);Throws(()=>f.Manager.Forget(f.Bob));Assert(File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json"))&&f.One.AccountKey==f.Bob.Key,"forgotten profile data lost");});
        check("invalid and duplicate instance identifiers are rejected",delegate {Throws(()=>InstancePaths.Folder(root,".."));Throws(()=>InstancePaths.Folder(root,"C:\\foreign"));var data=new VaultData();data.Instances.Add(new DesktopInstance{Id="local"});data.Instances.Add(new DesktopInstance{Id="local"});Throws(()=>InstanceRules.Normalize(data));});
        check("new instance config preserves other settings and disables bulk dependency downloads",delegate {var f=fixture();string config=Path.Combine(f.Manager.Home(f.One),"config.toml");string before=File.ReadAllText(config);Assert(before.Contains("workspace_dependencies = false"),"heavy auto-download enabled");f.Manager.Configure(f.One,f.Alice);Assert(File.ReadAllText(config)==before,"account assignment changed config");});
        check("managed switching respects keyring refusal",delegate {var f=fixture();File.WriteAllText(Path.Combine(f.Manager.Home(f.One),"config.toml"),"cli_auth_credentials_store = 'keyring'\n");Throws(()=>f.Manager.Configure(f.One,f.Alice));Assert(!File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json")),"keyring shadow auth created");});
        check("launch environment isolates UI/auth/db and removes inherited authentication",delegate {
            string old=Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN");Environment.SetEnvironmentVariable("CODEX_ACCESS_TOKEN","fictional-parent");
            try{var f=fixture();var start=InstanceHost.BuildStart(new InstanceLaunch{Root=f.Service.Vault.Root,InstanceId=f.One.Id,DesktopExecutable="ChatGPT.exe"});Assert(!start.EnvironmentVariables.ContainsKey("CODEX_ACCESS_TOKEN")&&Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN")=="fictional-parent","inherited authentication");Assert(start.EnvironmentVariables["CODEX_HOME"]==f.Manager.Home(f.One)&&start.EnvironmentVariables["CODEX_SQLITE_HOME"]==f.Manager.Home(f.One)&&start.Arguments.Contains(InstancePaths.UserData(f.Service.Vault.Root,f.One.Id)),"launch paths mismatch");}finally{Environment.SetEnvironmentVariable("CODEX_ACCESS_TOKEN",old);}
        });
        check("stale process identity cannot be mistaken for a live owned desktop",delegate {using(var p=Process.GetCurrentProcess()){Assert(DesktopRuntime.SameProcess(p.Id,p.StartTime.ToUniversalTime().Ticks),"live process not recognized");Assert(!DesktopRuntime.SameProcess(p.Id,p.StartTime.ToUniversalTime().Ticks-1),"reused PID trusted");}});
        check("closed window on a legacy live host becomes background, not open or starting",delegate{var s=new InstanceState{Running=true,WindowReady=true,Phase="starting",DesktopStartTicks=DateTime.UtcNow.AddMinutes(-2).Ticks};DesktopRuntime.Reconcile(s,true,true,false);Assert(s.Running&&!s.WindowReady&&s.Phase=="background"&&s.Caption=="En arrière-plan","background state incorrect");});
        check("dead desktop clears stale readiness and private IPC endpoint",delegate{var s=new InstanceState{Running=true,WindowReady=true,Phase="running",AppToolsPipe="old"};DesktopRuntime.Reconcile(s,false,false,false);Assert(!s.Running&&!s.WindowReady&&s.Phase=="stopped"&&s.AppToolsPipe==null,"stale live state survived");});
        check("start stop and reopen are distinct lifecycle states",delegate{var s=new InstanceState{Phase="starting",DesktopStartTicks=DateTime.UtcNow.Ticks};DesktopRuntime.Reconcile(s,true,true,false);Assert(s.Phase=="starting","startup mistaken for background");DesktopRuntime.Reconcile(s,true,true,true);Assert(s.WindowReady&&s.Phase=="running","window not detected");DesktopRuntime.Reconcile(s,true,true,false);Assert(s.Phase=="background","closed window not detected");s.Phase="stopping";DesktopRuntime.Reconcile(s,true,true,true);Assert(s.Phase=="stopping","stop phase overwritten");});
        check("close confirmation cannot act on a replacement process or launch",delegate{var expected=new InstanceState{DesktopPid=4,DesktopStartTicks=8,LaunchId="old"};Throws(()=>DesktopWindows.RequireSameLaunch(expected,new InstanceState{DesktopPid=4,DesktopStartTicks=9,LaunchId="old"}));Throws(()=>DesktopWindows.RequireSameLaunch(expected,new InstanceState{DesktopPid=4,DesktopStartTicks=8,LaunchId="new"}));Throws(()=>DesktopWindows.RequireSameLaunch(expected,new InstanceState{DesktopPid=4,DesktopStartTicks=8,LaunchId="old",Phase="unknown"}));});
        check("targeted close preserves other live instances",delegate{var f=fixture();f.Runtime.Open.Add(f.One.Id);f.Runtime.Open.Add(f.Two.Id);DesktopWindows.CloseOrRestart(f.Service,f.One.Id,false,f.Runtime.Probe(f.One),CancellationToken.None).GetAwaiter().GetResult();Assert(!f.Runtime.Open.Contains(f.One.Id)&&f.Runtime.Open.Contains(f.Two.Id),"wrong instance stopped");});
        check("restart never launches when stopping did not complete",delegate{var f=fixture();f.Runtime.Open.Add(f.One.Id);f.Runtime.RefuseStop=true;Throws(()=>DesktopWindows.CloseOrRestart(f.Service,f.One.Id,true,f.Runtime.Probe(f.One),CancellationToken.None).GetAwaiter().GetResult());Assert(f.Runtime.Open.Contains(f.One.Id),"existing desktop lost");});
        check("desktop metadata bridge is confined to its own launch without inheriting another instance",delegate{
            var f=fixture();string inherited=Environment.GetEnvironmentVariable("CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH");
            Environment.SetEnvironmentVariable("CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH","fictional-other-instance");
            try{
                var request=new InstanceLaunch{Root=f.Service.Vault.Root,InstanceId=f.One.Id,LaunchId=Guid.NewGuid().ToString("N"),AccountKey=f.Bob.Key,DesktopExecutable="ChatGPT.exe",NativeCodexExecutable=Path.Combine(f.Service.Vault.Root,"native","codex.exe"),MetadataBridgeExecutable=Path.Combine(f.Service.Vault.Root,"payload","SwitcherCodexBridge.exe")};
                var start=InstanceHost.BuildStart(request);
                Assert(start.EnvironmentVariables["CODEX_CLI_PATH"]==request.MetadataBridgeExecutable&&start.EnvironmentVariables["CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH"]==Path.Combine(InstancePaths.Folder(request.Root,request.InstanceId),"launch.json"),"Bridge escaped its own launch or did not override the native launcher");
                Assert(Environment.GetEnvironmentVariable("CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH")=="fictional-other-instance","Child setup modified the parent environment");
                request.MetadataBridgeExecutable=null;Throws(()=>InstanceHost.BuildStart(request));request.MetadataBridgeExecutable="SwitcherCodexBridge.exe";Throws(()=>InstanceHost.BuildStart(request));
                var legacy=InstanceHost.BuildStart(new InstanceLaunch{Root=f.Service.Vault.Root,InstanceId=f.One.Id,DesktopExecutable="ChatGPT.exe"});
                Assert(!legacy.EnvironmentVariables.ContainsKey("CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH")&&!legacy.EnvironmentVariables.ContainsKey("CODEX_CLI_PATH"),"Legacy activation inherited another bridge");
            }finally{Environment.SetEnvironmentVariable("CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH",inherited);}
        });
        check("activation checks the current host desktop and launch before starting a helper",delegate{using(var p=Process.GetCurrentProcess()){string id=Guid.NewGuid().ToString("N");var request=new InstanceLaunch{LaunchId=id};var state=new InstanceState{Running=true,LaunchId=id,HostPid=p.Id,DesktopPid=p.Id,HostStartTicks=p.StartTime.ToUniversalTime().Ticks,DesktopStartTicks=p.StartTime.ToUniversalTime().Ticks};InstanceHost.RequireActivation(request,state,id);Throws(()=>InstanceHost.RequireActivation(request,state,Guid.NewGuid().ToString("N")));state.Phase="stopping";Throws(()=>InstanceHost.RequireActivation(request,state,id));state.Phase="background";state.DesktopStartTicks--;Throws(()=>InstanceHost.RequireActivation(request,state,id));}});
        check("primary window excludes all managed instances",delegate{var main=new DesktopWindow{Pid=1,Started=10,Handle=new IntPtr(1)};var other=new DesktopWindow{Pid=2,Started=20,Handle=new IntPtr(2)};var owned=new[]{new InstanceState{DesktopPid=2,DesktopStartTicks=20}};Assert(DesktopWindows.SelectLocal(new[]{main,other},owned)==main,"wrong window selected");Assert(DesktopWindows.SelectLocal(new[]{other},owned)==null,"managed desktop mistaken for closed primary");});
        check("primary window ambiguity and unidentified managed launch are refused",delegate{var windows=new[]{new DesktopWindow{Pid=1,Started=10},new DesktopWindow{Pid=2,Started=20}};Throws(()=>DesktopWindows.SelectLocal(windows,new InstanceState[0]));Throws(()=>DesktopWindows.SelectLocal(windows.Take(1),new[]{new InstanceState{Running=true}}));});
        check("window focus refuses stale process without activating a window",delegate{using(var p=Process.GetCurrentProcess())Throws(()=>DesktopWindows.Focus(new DesktopWindow{Pid=p.Id,Started=p.StartTime.ToUniversalTime().Ticks-1,Handle=IntPtr.Zero}));});
        check("opening a closed instance refreshes only its own stale auth from an active peer",delegate {
            var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Manager.Configure(f.Two,f.Bob);
            var updated=Json.Read<Dictionary<string,object>>(b);updated["last_refresh"]=DateTime.UtcNow.AddMinutes(2).ToString("o");Json.Obj(updated["tokens"])["access_token"]="fictional-peer-refresh";
            string fresh=Json.Write(updated);File.WriteAllText(Path.Combine(f.Manager.Home(f.Two),"auth.json"),fresh);f.Runtime.Open.Add(f.Two.Id);
            f.Manager.Start(f.One,CancellationToken.None).GetAwaiter().GetResult();
            Assert(f.Runtime.Open.Contains(f.One.Id)&&f.Runtime.Open.Contains(f.Two.Id)&&File.ReadAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"))==fresh&&File.ReadAllText(Path.Combine(f.Manager.Home(f.Two),"auth.json"))==fresh,"stale reopening or other instance changed");
        });
        check("empty archived instance cannot be launched",delegate {var f=fixture();f.Manager.Archive(f.One,true);Throws(()=>f.Manager.Start(f.One,CancellationToken.None).GetAwaiter().GetResult());Assert(!f.Runtime.Open.Contains(f.One.Id),"archived instance launched");});
        check("restored legacy instance adopts its actual account before opening",delegate {var f=fixture();f.Manager.Configure(f.One,f.Alice);f.One.AccountLocked=false;f.One.AccountKey=f.Bob.Key;f.Manager.Archive(f.One,true);f.Manager.Archive(f.One,false);f.Manager.Start(f.One,CancellationToken.None).GetAwaiter().GetResult();Assert(f.One.AccountLocked&&f.One.AccountKey==f.Alice.Key&&f.Runtime.Open.Contains(f.One.Id),"restored legacy binding remained stale");});
    }
}

internal static class StartupRecoveryTests
{
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static int Crash(string path)
    {
        using(var p=Process.GetCurrentProcess()){string nonce=Guid.NewGuid().ToString("N");long ticks=p.StartTime.ToUniversalTime().Ticks;File.WriteAllText(path+".switcher-tmp",AtomicFiles.Reservation(p.Id,ticks,nonce));File.WriteAllText(AtomicFiles.Temporary(path,p.Id,ticks,nonce),"unfinished");}
        return 23;
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        int index=0;Func<string> folder=()=>{string path=Path.Combine(root,"startup-recovery-"+(index++));Directory.CreateDirectory(path);return path;};
        check("unowned legacy temporary is preserved instead of racing an older writer",()=>{
            string path=Path.Combine(folder(),"settings.json");File.WriteAllText(path,"old");File.WriteAllText(path+".switcher-tmp","legacy");try{SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes("new"));throw new Exception("Expected refusal");}catch(IOException e){Assert((e.HResult&0xffff)==80,"lost collision code");}Assert(File.ReadAllText(path)=="old"&&File.ReadAllText(path+".switcher-tmp")=="legacy","unsafe legacy cleanup");
        });
        check("dead owner temporary recovered immediately without an age heuristic",()=>{
            string path=Path.Combine(folder(),"state.json"),temp=AtomicFiles.Temporary(path,123,1,Guid.NewGuid().ToString("N"));File.WriteAllText(path,"valid");File.WriteAllText(temp,"partial");Assert(AtomicFiles.RecoverOwned(path,(pid,ticks)=>false)==1&&!File.Exists(temp)&&File.ReadAllText(path)=="valid","partial promoted or orphan kept");
        });
        check("live writer and unrecognized temporary names are preserved",()=>{
            string path=Path.Combine(folder(),"state.json");using(var self=Process.GetCurrentProcess()){
                string live=AtomicFiles.Temporary(path,self.Id,self.StartTime.ToUniversalTime().Ticks,Guid.NewGuid().ToString("N")),unknown=path+".switcher-tmp-unknown";
                File.WriteAllText(live,"live");File.WriteAllText(unknown,"keep");SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes("valid"));Assert(File.ReadAllText(live)=="live"&&File.ReadAllText(unknown)=="keep","active or ambiguous file removed");
            }
        });
        check("PID reuse does not make a dead owner's temporary live",()=>{
            string path=Path.Combine(folder(),"state.json");using(var self=Process.GetCurrentProcess()){
                string temp=AtomicFiles.Temporary(path,self.Id,1,Guid.NewGuid().ToString("N"));File.WriteAllText(temp,"orphan");Assert(AtomicFiles.RecoverOwned(path)==1&&!File.Exists(temp),"PID-only attribution");
            }
        });
        check("open temporary is never deleted even when ownership is reported dead",()=>{
            string path=Path.Combine(folder(),"state.json"),temp=AtomicFiles.Temporary(path,123,1,Guid.NewGuid().ToString("N"));File.WriteAllText(temp,"open");using(var stream=File.Open(temp,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Assert(AtomicFiles.RecoverOwned(path,(pid,ticks)=>false)==0&&File.Exists(temp),"open file removed");}
        });
        check("instance lease migration removes legacy temporaries without promoting partial data",()=>{
            string path=folder(),target=Path.Combine(path,"state.json");File.WriteAllText(target,"valid");foreach(string name in new[]{"state.json","launch.json","stop.json"})File.WriteAllText(Path.Combine(path,name)+".switcher-tmp","partial");using(var lease=new InstanceLease(path))AtomicFiles.RecoverLegacyInstance(path);Assert(File.ReadAllText(target)=="valid"&&!Directory.GetFiles(path,"*.switcher-tmp").Any(),"legacy migration changed final data");
        });
        check("locked legacy temporary reports sharing violation and stays intact",()=>{
            string path=folder(),temp=Path.Combine(path,"state.json.switcher-tmp");File.WriteAllText(temp,"open");using(var locked=File.Open(temp,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){try{using(var lease=new InstanceLease(path))AtomicFiles.RecoverLegacyInstance(path);throw new Exception("Expected refusal");}catch(IOException e){Assert((e.HResult&0xffff)==32,"real sharing violation lost");}}Assert(File.ReadAllText(temp)=="open","legacy writer touched");
        });
        check("failed replacement preserves valid target and removes only its own temporary",()=>{
            string path=Path.Combine(folder(),"state.json");File.WriteAllText(path,"old");File.SetAttributes(path,FileAttributes.ReadOnly);try{try{SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes("new"));throw new Exception("Expected refusal");}catch(UnauthorizedAccessException){}Assert(File.ReadAllText(path)=="old"&&!Directory.GetFiles(Path.GetDirectoryName(path),".swt-*").Any(),"failure changed final data or leaked temp");}finally{File.SetAttributes(path,FileAttributes.Normal);}
        });
        check("concurrent atomic writers produce complete files and no leftovers",()=>{
            string path=Path.Combine(folder(),"state.json");SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes("initial"));Task.WaitAll(Enumerable.Range(0,8).Select(n=>Task.Run(()=>{for(int j=0;j<12;j++)SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes(new string((char)('a'+n),2048)));})).ToArray());string final=File.ReadAllText(path);Assert(final.Length==2048&&final.All(c=>c==final[0])&&!Directory.GetFiles(Path.GetDirectoryName(path),".swt-*").Any(),"torn output or leftover");
        });
        check("crashed child writer is recovered across process boundaries",()=>{
            string path=Path.Combine(folder(),"state.json");File.WriteAllText(path,"valid");string assembly=System.Reflection.Assembly.GetExecutingAssembly().Location;
#if NETCOREAPP
            string runtime=Directory.GetParent(Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(typeof(object).Assembly.Location)).FullName).FullName).FullName;
            var start=new ProcessStartInfo(Path.Combine(runtime,"dotnet.exe"),"\""+assembly+"\" --atomic-crash \""+path+"\""){UseShellExecute=false,CreateNoWindow=true};
#else
            var start=new ProcessStartInfo(assembly,"--atomic-crash \""+path+"\""){UseShellExecute=false,CreateNoWindow=true};
#endif
            using(var child=Process.Start(start)){Assert(child.WaitForExit(15000)&&child.ExitCode==23,"crash fixture failed");}Assert(Directory.GetFiles(Path.GetDirectoryName(path),".swt-*").Length==1,"missing crash artifact");SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes("recovered"));Assert(File.ReadAllText(path)=="recovered"&&!Directory.GetFiles(Path.GetDirectoryName(path),".swt-*").Any(),"real orphan blocked write");
        });
        Func<string,InstanceLaunch> request=exe=>{string instance=Guid.NewGuid().ToString("N"),baseRoot=folder(),path=InstancePaths.Folder(baseRoot,instance);Directory.CreateDirectory(path);Directory.CreateDirectory(InstancePaths.Home(baseRoot,instance));Directory.CreateDirectory(InstancePaths.UserData(baseRoot,instance));return new InstanceLaunch{Root=baseRoot,InstanceId=instance,LaunchId=Guid.NewGuid().ToString("N"),DesktopExecutable=exe,Name="Fixture"};};
        check("host publishes the actual missing executable error before state creation",()=>{
            var launch=request(Path.Combine(root,"absent","ChatGPT.exe"));string path=InstancePaths.Folder(launch.Root,launch.InstanceId),file=Path.Combine(path,"launch.json");SafeFiles.AtomicWrite(file,Encoding.UTF8.GetBytes(Json.Write(launch)));Assert(InstanceHost.Run(file)==1,"missing executable accepted");var error=InstanceStartup.Read(path,launch.LaunchId);Assert(error!=null&&error.ErrorType=="FileNotFoundException"&&error.ErrorCode=="0x80070002"&&error.Stage=="prepare-profile"&&InstanceStartup.Read(path,Guid.NewGuid().ToString("N"))==null,"diagnostic missing or stale");
        });
        check("host records state access failure even when state.json cannot be replaced",()=>{
            string fake=Path.Combine(folder(),"ChatGPT.exe");File.WriteAllText(fake,"not executable");var launch=request(fake);string path=InstancePaths.Folder(launch.Root,launch.InstanceId),file=Path.Combine(path,"launch.json"),state=Path.Combine(path,"state.json");SafeFiles.AtomicWrite(file,Encoding.UTF8.GetBytes(Json.Write(launch)));File.WriteAllText(state,"preserve");File.SetAttributes(state,FileAttributes.ReadOnly);try{Assert(InstanceHost.Run(file)==1,"state failure accepted");var failure=InstanceStartup.Read(path,launch.LaunchId);Assert(failure!=null&&failure.Stage=="write-state"&&failure.ErrorCode=="0x80070005"&&File.ReadAllText(state)=="preserve","state failure lost or clobbered");var shown=new DesktopRuntime(launch.Root).Probe(new DesktopInstance{Id=launch.InstanceId});Assert(shown.Phase=="error"&&!shown.Running&&shown.Message.Contains("0x80070005"),"actual failure hidden by malformed status");}finally{File.SetAttributes(state,FileAttributes.Normal);}
        });
        check("host recovers legacy state orphan then surfaces native executable error",()=>{
            string fake=Path.Combine(folder(),"ChatGPT.exe");File.WriteAllText(fake,"not executable");var launch=request(fake);string path=InstancePaths.Folder(launch.Root,launch.InstanceId),file=Path.Combine(path,"launch.json");SafeFiles.AtomicWrite(file,Encoding.UTF8.GetBytes(Json.Write(launch)));File.WriteAllText(Path.Combine(path,"state.json.switcher-tmp"),"orphan");Assert(InstanceHost.Run(file)==1&&!File.Exists(Path.Combine(path,"state.json.switcher-tmp")),"recovery blocked");var failure=InstanceStartup.Read(path,launch.LaunchId);Assert(failure!=null&&failure.Stage=="start-desktop"&&failure.ErrorType=="Win32Exception"&&failure.NativeErrorCode.HasValue&&failure.Message.Contains(failure.NativeErrorCode.Value.ToString()),"native CreateProcess error not reported");var state=Json.Read<InstanceState>(SafeFiles.ReadText(Path.Combine(path,"state.json")));Assert(!state.Running&&state.Phase=="error"&&state.Caption=="Échec du lancement","failure shown as stopped or starting");
        });
        check("launch diagnostics use trusted labels and codes without leaking exception contents",()=>{
            var error=InstanceStartup.Failure(Guid.NewGuid().ToString("N"),"bootstrap",new InvalidOperationException("Bearer private-token user@example.com"));Assert(!Json.Write(error).Contains("private-token")&&!Json.Write(error).Contains("user@example.com")&&error.Message.Contains("InvalidOperationException"),"exception body exposed");
        });
        check("Windows launcher reports actual native error without stderr secrets",()=>{
            try{DesktopRuntime.RunPowerShell("throw (New-Object ComponentModel.Win32Exception(5,'private-token user@example.com'))",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Expected refusal");}
            catch(InstanceStartupException e){Assert(e.Result.NativeErrorCode==5&&e.Result.ErrorCode=="0x80004005"&&e.Result.Stage=="windows-launcher"&&e.Result.ExitCode==1&&!e.Message.Contains("private-token")&&!e.Message.Contains("user@example.com")&&Program.SafeError(e)==e.Message,"launcher detail lost or exposed");}
        });
        check("persistent launch failure is correlated to the latest request and retry clears it",()=>{
            var launch=request(Path.Combine(root,"absent","ChatGPT.exe"));string path=InstancePaths.Folder(launch.Root,launch.InstanceId),file=Path.Combine(path,"launch.json");SafeFiles.AtomicWrite(file,Encoding.UTF8.GetBytes(Json.Write(launch)));InstanceHost.Run(file);
            var runtime=new DesktopRuntime(launch.Root);var instance=new DesktopInstance{Id=launch.InstanceId};var failed=runtime.Probe(instance);Assert(!failed.Running&&failed.Phase=="error"&&failed.Message.Contains("0x80070002"),"failure hidden when state never created");
            SafeFiles.AtomicWrite(Path.Combine(path,"state.json"),Encoding.UTF8.GetBytes(Json.Write(failed)));
            launch.LaunchId=Guid.NewGuid().ToString("N");SafeFiles.AtomicWrite(file,Encoding.UTF8.GetBytes(Json.Write(launch)));var fresh=runtime.Probe(instance);Assert(!fresh.Running&&fresh.Phase=="stopped"&&fresh.Message==null&&fresh.LaunchId!=launch.LaunchId,"stale failure blocks retry or request mistaken for a host status");
        });
        check("timeout only says starting when the matching host is actually alive",()=>{
            string launch=Guid.NewGuid().ToString("N");Assert(InstanceStartup.Timeout(new InstanceState{LaunchId="old",Running=true,Phase="starting"},launch).StartsWith("Échec")&&InstanceStartup.Timeout(new InstanceState{LaunchId=launch,Running=false,Phase="stopped"},launch).StartsWith("Échec")&&InstanceStartup.Timeout(new InstanceState{LaunchId=launch,Running=true,Phase="starting"},launch).Contains("L’hôte répond"),"false startup timeout");
        });
        check("early Codex exit records its code and diagnostics are bounded to one record",()=>{
            string path=folder(),launch=Guid.NewGuid().ToString("N");InstanceStartup.Save(path,InstanceStartup.DesktopExit(launch,27));InstanceStartup.Save(path,InstanceStartup.Bootstrap(launch));Assert(Directory.GetFiles(path).Length==1&&InstanceStartup.Read(path,launch).ErrorType=="CodexBootstrapFailed"&&InstanceStartup.DesktopExit(launch,27).Message.Contains("27"),"unbounded history or exit code lost");
        });
    }
}
