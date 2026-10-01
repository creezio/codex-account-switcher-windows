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
        public InstanceState Probe(DesktopInstance instance){return new InstanceState{Running=Open.Contains(instance.Id),Phase=Open.Contains(instance.Id)?"running":"stopped"};}
        public Task Start(DesktopInstance instance,CancellationToken token){Open.Add(instance.Id);return Task.FromResult(0);}
        public Task Stop(DesktopInstance instance,CancellationToken token){Open.Remove(instance.Id);return Task.FromResult(0);}
    }
    private sealed class Fixture
    {
        public readonly AccountService Service;public readonly FakeRuntime Runtime=new FakeRuntime();public readonly InstanceManager Manager;
        public readonly DesktopInstance One,Two;public readonly Profile Alice,Bob;
        public Fixture(string root,string a,string b)
        {
            Service=new AccountService(root);Service.Settings.CodexHome=Path.Combine(root,"untouched-local");Directory.CreateDirectory(Service.Settings.CodexHome);
            File.WriteAllText(Path.Combine(Service.Settings.CodexHome,"auth.json"),a);
            Manager=new InstanceManager(Service,Runtime);One=Manager.Create("Travail");Two=Manager.Create("Personnel");
            Alice=Service.Import(a,"Alice");Bob=Service.Import(b,"Bob");
        }
    }
    public static void RunAll(Action<string,Action> check,string root,string a,string b)
    {
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
        check("switching one closed instance preserves the local and other running instance",delegate {var f=fixture();f.Manager.Configure(f.One,f.Alice);f.Manager.Configure(f.Two,f.Bob);f.Runtime.Open.Add(f.Two.Id);f.Manager.Configure(f.One,f.Bob);Assert(File.ReadAllText(Path.Combine(f.Service.Settings.CodexHome,"auth.json"))==a,"primary changed");Assert(File.ReadAllText(Path.Combine(f.Manager.Home(f.Two),"auth.json"))==b&&f.Runtime.Open.Contains(f.Two.Id),"other instance touched");Assert(f.One.PreviousAuthJson==a&&f.Two.PreviousAuthJson==null,"backup leaked between instances");});
        check("running target blocks account writes",delegate {var f=fixture();f.Manager.Configure(f.One,f.Alice);f.Runtime.Open.Add(f.One.Id);Throws(()=>f.Manager.Configure(f.One,f.Bob));Assert(File.ReadAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"))==a,"running instance overwritten");});
        check("instance mutex blocks a second process from rewriting the same profile",delegate {
            var f=fixture();var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
            var worker=new Thread(()=>{using(var lease=new InstanceLease(InstancePaths.Folder(f.Service.Vault.Root,f.One.Id))){entered.Set();release.WaitOne();}});worker.Start();entered.WaitOne();
            try{Throws(()=>f.Manager.Configure(f.One,f.Alice));Assert(!File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json")),"write through live lease");}finally{release.Set();worker.Join();entered.Dispose();release.Dispose();}
        });
        check("archiving preserves account and conversation files and blocks opening",delegate {var f=fixture();f.Manager.Configure(f.One,f.Alice);string history=Path.Combine(f.Manager.Home(f.One),"history.jsonl");File.WriteAllText(history,"preserve");f.Manager.Archive(f.One,true);Throws(()=>InstanceRules.RequireAvailable(f.Alice,f.One));Assert(File.ReadAllText(history)=="preserve"&&File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json")),"archive deleted data");f.Manager.Archive(f.One,false);Assert(!f.One.Archived,"restore failed");});
        check("running instances and built-in local session cannot be archived",delegate {var f=fixture();f.Runtime.Open.Add(f.One.Id);Throws(()=>f.Manager.Archive(f.One,true));Throws(()=>f.Manager.Archive(f.Service.Data.Instances[0],true));});
        check("revoking a live association is refused atomically",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Runtime.Open.Add(f.One.Id);Throws(()=>f.Manager.SetScope(f.Bob,false,new[]{f.Two.Id}));Assert(f.Bob.AllInstances,"partly revoked permissions");});
        check("revoking a closed association preserves auth but prevents reopening",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Manager.SetScope(f.Bob,false,new[]{f.Two.Id});Throws(()=>f.Manager.Start(f.One,CancellationToken.None).GetAwaiter().GetResult());Assert(File.ReadAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"))==b&&!f.Runtime.Open.Contains(f.One.Id),"unauthorized reopening");});
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
        check("forgetting an account does not delete its instance data",delegate {var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Manager.Forget(f.Bob);Assert(File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json"))&&f.One.AccountKey==null,"forgotten profile data lost");});
        check("invalid and duplicate instance identifiers are rejected",delegate {Throws(()=>InstancePaths.Folder(root,".."));Throws(()=>InstancePaths.Folder(root,"C:\\foreign"));var data=new VaultData();data.Instances.Add(new DesktopInstance{Id="local"});data.Instances.Add(new DesktopInstance{Id="local"});Throws(()=>InstanceRules.Normalize(data));});
        check("new instance config preserves other settings and disables bulk dependency downloads",delegate {var f=fixture();string config=Path.Combine(f.Manager.Home(f.One),"config.toml");string before=File.ReadAllText(config);Assert(before.Contains("workspace_dependencies = false"),"heavy auto-download enabled");f.Manager.Configure(f.One,f.Alice);Assert(File.ReadAllText(config)==before,"account assignment changed config");});
        check("managed switching respects keyring refusal",delegate {var f=fixture();File.WriteAllText(Path.Combine(f.Manager.Home(f.One),"config.toml"),"cli_auth_credentials_store = 'keyring'\n");Throws(()=>f.Manager.Configure(f.One,f.Alice));Assert(!File.Exists(Path.Combine(f.Manager.Home(f.One),"auth.json")),"keyring shadow auth created");});
        check("launch environment isolates UI/auth/db and removes inherited authentication",delegate {
            string old=Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN");Environment.SetEnvironmentVariable("CODEX_ACCESS_TOKEN","fictional-parent");
            try{var f=fixture();var start=InstanceHost.BuildStart(new InstanceLaunch{Root=f.Service.Vault.Root,InstanceId=f.One.Id,DesktopExecutable="ChatGPT.exe"});Assert(start.EnvironmentVariables["CODEX_ACCESS_TOKEN"]==null&&Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN")=="fictional-parent","inherited authentication");Assert(start.EnvironmentVariables["CODEX_HOME"]==f.Manager.Home(f.One)&&start.EnvironmentVariables["CODEX_SQLITE_HOME"]==f.Manager.Home(f.One)&&start.Arguments.Contains(InstancePaths.UserData(f.Service.Vault.Root,f.One.Id)),"launch paths mismatch");}finally{Environment.SetEnvironmentVariable("CODEX_ACCESS_TOKEN",old);}
        });
        check("stale process identity cannot be mistaken for a live owned desktop",delegate {using(var p=Process.GetCurrentProcess()){Assert(DesktopRuntime.SameProcess(p.Id,p.StartTime.ToUniversalTime().Ticks),"live process not recognized");Assert(!DesktopRuntime.SameProcess(p.Id,p.StartTime.ToUniversalTime().Ticks-1),"reused PID trusted");}});
        check("opening a closed instance refreshes only its own stale auth from an active peer",delegate {
            var f=fixture();f.Manager.Configure(f.One,f.Bob);f.Manager.Configure(f.Two,f.Bob);
            var updated=Json.Read<Dictionary<string,object>>(b);updated["last_refresh"]=DateTime.UtcNow.AddMinutes(2).ToString("o");Json.Obj(updated["tokens"])["access_token"]="fictional-peer-refresh";
            string fresh=Json.Write(updated);File.WriteAllText(Path.Combine(f.Manager.Home(f.Two),"auth.json"),fresh);f.Runtime.Open.Add(f.Two.Id);
            f.Manager.Start(f.One,CancellationToken.None).GetAwaiter().GetResult();
            Assert(f.Runtime.Open.Contains(f.One.Id)&&f.Runtime.Open.Contains(f.Two.Id)&&File.ReadAllText(Path.Combine(f.Manager.Home(f.One),"auth.json"))==fresh&&File.ReadAllText(Path.Combine(f.Manager.Home(f.Two),"auth.json"))==fresh,"stale reopening or other instance changed");
        });
        check("empty archived instance cannot be launched",delegate {var f=fixture();f.Manager.Archive(f.One,true);Throws(()=>f.Manager.Start(f.One,CancellationToken.None).GetAwaiter().GetResult());Assert(!f.Runtime.Open.Contains(f.One.Id),"archived instance launched");});
    }
}
