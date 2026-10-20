using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class PortalPluginTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static object Normal(object value){return Json.Read<object>(Json.Write(value));}
    private static string Value(object value,string key){return Json.Str(Json.Get(Normal(value),key));}
    private static void Reject(Action call){bool failed=false;try{call();}catch(WebhookException){failed=true;}Assert(failed,"Expected scoped refusal");}
    private sealed class Lease:IDisposable{public void Dispose(){}}
    private sealed class HistoryReader:IDisposable
    {
        private readonly ManualResetEventSlim entered=new ManualResetEventSlim(),release=new ManualResetEventSlim();private readonly Task work;
        internal HistoryReader(string folder){work=Task.Factory.StartNew(()=>{using(new InstanceLease(folder)){entered.Set();if(!release.Wait(5000))throw new Exception("Fictional history reader timed out");}},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);Assert(entered.Wait(2000),"Fictional history reader did not acquire InstanceLease");}
        internal void Release(){release.Set();}
        public void Dispose(){release.Set();work.GetAwaiter().GetResult();entered.Dispose();release.Dispose();}
    }
    private sealed class Fixture:IDisposable
    {
        internal readonly string Route=new string('a',32),Actor=new string('b',64),Instance=new string('c',32),Root;
        internal RelayStore Store;internal PortalPluginService Service;internal PortalPluginDependencies Dependencies;
        internal string Account="fictional-account",Remote="plugin_gmail",Policy="AVAILABLE";
        internal long Revision=1;internal bool Allowed=true,Installed,Connected,FailInstall,GuiOpen;
        internal int Installs,CatalogCalls,RuntimeCalls,ScopeCount;internal Action DuringRuntime=null;
        internal int Authorizations;internal Action DuringAuthorize=null;
        internal ManualResetEventSlim Held=null;internal DateTime Clock=DateTime.UtcNow;
        internal Fixture(string sandbox){Root=Path.Combine(sandbox,"p"+Guid.NewGuid().ToString("N").Substring(0,6));Directory.CreateDirectory(Root);Store=new RelayStore(Path.Combine(Root,"r"));
            Dependencies=new PortalPluginDependencies{Clock=()=>Clock,Resolve=(route,actor,instance)=>new PortalPluginTarget{InstanceId=instance,AccountKey=Account,AccountEmail="fictional@example.invalid",Home=Path.Combine(Root,"h"),Executable="fictional-codex.exe",GrantRevision=Revision,PolicyRevision=1,SiteOrigin="https://portal.example.invalid"},ProfileScope=target=>{Interlocked.Increment(ref ScopeCount);return new PortalPluginProfileScope(GuiOpen?null:new Lease(),!GuiOpen);},Catalog=(target,token)=>{CatalogCalls++;object row=new{pluginId="gmail@openai-curated-remote",name="gmail",marketplaceName="openai-curated-remote",installed=Installed,enabled=Installed,source=new{source="remote",id=Remote},installPolicy=Policy,authPolicy="ON_INSTALL",version="test"};return new{installed=Installed?new[]{row}:new object[0],available=Installed?new object[0]:new[]{row}};},Runtime=(target,token)=>{RuntimeCalls++;if(DuringRuntime!=null)DuringRuntime();return new PortalPluginRuntime{Apps=new List<PortalPluginApp>{new PortalPluginApp{Id="gmail",Name="Gmail",InstallUrl="https://chatgpt.com/apps/gmail/gmail",Accessible=Connected,Enabled=Connected,Callable=Installed&&Connected}}};},Install=(target,selector,token)=>{Assert(selector=="gmail@openai-curated-remote","Free selector reached native command");Interlocked.Increment(ref Installs);if(Held!=null&&!Held.Wait(5000))throw new Exception("Fictional install barrier timeout");if(FailInstall)throw new IOException("fictional credential must never appear in a receipt");Installed=true;return new{appsNeedingAuth=new object[0]};}};
            Service=new PortalPluginService(Store,(route,actor)=>{Interlocked.Increment(ref Authorizations);if(DuringAuthorize!=null)DuringAuthorize();if(!Allowed||route!=Route||actor!=Actor)throw new WebhookException(403,"Fictional revoked actor");},Dependencies);
        }
        internal string UseRealResolve()
        {
            string local=Path.Combine(Root,"local"),executable=Path.Combine(Root,"fictional.exe"),folder=InstancePaths.Folder(Root,Instance),home=InstancePaths.Home(Root,Instance),workspace=Path.Combine(folder,"workspace");Directory.CreateDirectory(local);Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);File.WriteAllText(executable,"fictional binary never started");new Vault(Root).SaveSettings(new Settings{CodexHome=local,CodexExecutable=executable});
            string claims=Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string,object>{{"sub","fictional-plugin-user"},{"email","fictional@example.invalid"},{"https://api.openai.com/auth",new{chatgpt_account_id="fictional-plugin-account",chatgpt_plan_type="plus"}}}))).TrimEnd('=').Replace('+','-').Replace('/','_');
            string auth=Json.Write(new{tokens=new{access_token="fictional-unused-access",refresh_token="fictional-unused-refresh",account_id="fictional-plugin-account",id_token="e30."+claims+".fixture"}});var identity=AuthIdentity.Parse(auth);Account=identity.Key;File.WriteAllText(Path.Combine(home,"auth.json"),auth);File.WriteAllText(Path.Combine(home,"config.toml"),"cli_auth_credentials_store = \"file\"\n");
            var accounts=new AccountService(Root);accounts.Data.Profiles.Add(new Profile{Key=identity.Key,Email=identity.Email,Label="Fictional account",AuthJson=auth,AllInstances=false,InstanceIds=new List<string>{Instance}});accounts.Data.Instances.Add(new DesktopInstance{Id=Instance,Name="Fictional closed instance",AccountKey=identity.Key,AccountLocked=true,HeadlessEnabled=true});accounts.Save();
            Store.WriteRecord("portal-"+Route+"-v1.dpapi",new PortalConfig{RootRouteId=Route,Enabled=true,Revision=1,Instances=new List<PortalInstanceGrant>{new PortalInstanceGrant{InstanceId=Instance,AccountKey=identity.Key,Home=home,Workspace=workspace,Access="headless",AuthorizationRevision=1}}});Dependencies.Resolve=null;return folder;
        }
        internal object Catalog(){return Normal(Service.Catalog(Route,Actor,Instance,CancellationToken.None));}
        internal string Choice(){return Value(RelayEngine.Rows(Json.Get(Catalog(),"plugins")).Single(),"id");}
        internal object Start(string choice,string key="same-request"){return Normal(Service.Start(Route,Actor,Instance,choice,key));}
        internal object Wait(string id){DateTime end=DateTime.UtcNow.AddSeconds(8);object value;do{value=Normal(Service.Read(Route,Actor,Instance,id));if(Value(value,"state")!="installing")return value;Thread.Sleep(5);}while(DateTime.UtcNow<end);throw new Exception("Fictional plugin operation did not finish");}
        public void Dispose(){if(Held!=null)Held.Set();Service.Dispose();}
    }
    private static void AssertOtherThreadExcluded(string folder)
    {var probe=Task.Run(()=>InstanceLease.Busy(folder));Assert(probe.Wait(2000)&&probe.Result,"A concurrent desktop start could acquire the inspected profile");}
    private static void FictionalDesktop(string folder,string launch)
    {using(var process=Process.GetCurrentProcess())File.WriteAllText(Path.Combine(folder,"state.json"),Json.Write(new InstanceState{Phase="running",Running=true,LaunchId=launch,HostPid=process.Id,HostStartTicks=process.StartTime.ToUniversalTime().Ticks,DesktopPid=process.Id,DesktopStartTicks=process.StartTime.ToUniversalTime().Ticks}));}
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("plugin GUI catalog uses only the read-only CLI and never infers connected or installable status",()=>{
            using(var f=new Fixture(sandbox)){
                f.GuiOpen=true;f.Connected=true;f.Dependencies.Runtime=(target,token)=>{throw new Exception("An open GUI must not start a second app-server");};f.Dependencies.Metadata=(target,ids,token)=>{throw new Exception("An open GUI must not request native metadata");};
                var available=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();Assert(!Object.Equals(Json.Get(available,"canInstall"),true)&&!Object.Equals(Json.Get(available,"callable"),true),"GUI catalog retained native mutation admission");
                f.Installed=true;f.Dependencies.Catalog=(target,token)=>{f.CatalogCalls++;return Normal(new{installed=new[]{new{pluginId="gmail@openai-curated-remote",name="gmail",marketplaceName="openai-curated-remote",installed=true,enabled=true,source=new{source="remote",id="plugin_connector_1p_fixture"},__creezioAppIds=new[]{"connector_fixture"},version="test",installPolicy="AVAILABLE"}},available=new object[0]});};
                var catalog=f.Catalog();var row=RelayEngine.Rows(Json.Get(catalog,"plugins")).Single();Assert(Object.Equals(Json.Get(row,"installed"),true)&&!Object.Equals(Json.Get(row,"callable"),true)&&!Object.Equals(Json.Get(row,"canInstall"),true)&&f.CatalogCalls==2&&f.RuntimeCalls==0&&Json.Write(Json.Get(catalog,"warnings")).Contains("Fermez cette instance"),"Read-only GUI catalog lost its rows or hid the close-instance warning");
            }
        });
        check("plugin GUI transition refuses a previously installable catalog before allocating a receipt",()=>{
            using(var f=new Fixture(sandbox)){string choice=f.Choice();int reads=f.RuntimeCalls;f.GuiOpen=true;Reject(()=>f.Start(choice));Assert(f.Installs==0&&f.RuntimeCalls==reads&&Directory.GetFiles(f.Store.Root,"portal-plugin-install-*.dpapi").Length==0,"A stale closed-profile catalog allocated or started a GUI installation");}
        });
        check("plugin GUI launch between admission and its worker fails before any native inspection or mutation",()=>{
            using(var f=new Fixture(sandbox)){
                f.Dependencies.ProfileScope=target=>{int count=Interlocked.Increment(ref f.ScopeCount);return new PortalPluginProfileScope(count>=3?null:new Lease(),count<3);};
                string id=Value(f.Start(f.Choice()),"id");Assert(Value(f.Wait(id),"state")=="failed"&&f.RuntimeCalls==1&&f.CatalogCalls==1&&f.Installs==0&&!f.Store.ReadRecord<PortalPluginOperation>("portal-plugin-install-"+id+".dpapi").NativeStarted,"A concurrent GUI launch reached native preflight or CLI add");
            }
        });
        check("plugin GUI catalog cannot reconcile an uncertain installation into a confirmed connection",()=>{
            using(var f=new Fixture(sandbox)){f.FailInstall=true;string id=Value(f.Start(f.Choice()),"id");Assert(Value(f.Wait(id),"state")=="uncertain","Fictional mutation did not reach an uncertain receipt");f.Installed=true;f.Connected=true;f.GuiOpen=true;int reads=f.RuntimeCalls;f.Catalog();Assert(Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="uncertain"&&f.RuntimeCalls==reads&&f.Installs==1,"CLI-only GUI observation falsely confirmed a native connection");}
        });
        check("plugin closed catalog and install keep one real instance lease across every native stage",()=>{
            using(var f=new Fixture(sandbox)){
                string folder=f.UseRealResolve();int scopes=0,metadata=0;f.Dependencies.ProfileScope=target=>{Interlocked.Increment(ref scopes);return PortalPluginCli.OpenProfileScope(target);};
                var runtime=f.Dependencies.Runtime;var catalog=f.Dependencies.Catalog;var install=f.Dependencies.Install;
                f.Dependencies.Runtime=(target,token)=>{AssertOtherThreadExcluded(folder);var result=runtime(target,token);foreach(var app in result.Apps)app.InstallUrl=null;return result;};
                f.Dependencies.Catalog=(target,token)=>{AssertOtherThreadExcluded(folder);return catalog(target,token);};
                f.Dependencies.Install=(target,selector,token)=>{AssertOtherThreadExcluded(folder);return install(target,selector,token);};
                f.Dependencies.Metadata=(target,ids,token)=>{AssertOtherThreadExcluded(folder);metadata++;return new[]{new PortalPluginApp{Id="gmail",Name="Gmail",InstallUrl="https://chatgpt.com/apps/gmail/gmail"}};};
                string choice=f.Choice();Assert(scopes==1,"A catalog acquired nested profile scopes");string id=Value(f.Start(choice),"id");var done=f.Wait(id);Assert(Value(done,"state")=="needs-auth"&&scopes==3&&metadata==1&&f.Installs==1,"Install acquired a nested scope or lost its metadata stage");
                var released=Task.Run(()=>InstanceLease.Busy(folder));Assert(released.Wait(2000)&&!released.Result,"The completed operation retained its profile lease");
            }
        });
        check("plugin receipts and exact retries stay readable during a locked install while fresh mutations and revoked scope remain refused",()=>{
            using(var f=new Fixture(sandbox))using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim()){
                string folder=f.UseRealResolve();File.WriteAllText(Path.Combine(folder,"state.json"),Json.Write(new InstanceState{Phase="stopped",LaunchId="previous-launch"}));File.WriteAllText(Path.Combine(folder,"launch.json"),Json.Write(new InstanceLaunch{Root=f.Root,InstanceId=f.Instance,LaunchId="previous-launch"}));
                f.Dependencies.ProfileScope=PortalPluginCli.OpenProfileScope;f.Dependencies.Install=(target,selector,token)=>{Interlocked.Increment(ref f.Installs);entered.Set();if(!release.Wait(10000))throw new Exception("Fictional install receipt barrier timed out");f.Installed=true;return new{appsNeedingAuth=new object[0]};};
                string choice=f.Choice(),id=Value(f.Start(choice),"id");try{
                    Assert(entered.Wait(5000),"The fictional worker did not enter its leased mutation");Thread.Sleep(1100);
                    var observed=new DesktopRuntime(f.Root).Probe(new AccountService(f.Root).Data.Instances.Single(item=>item.Id==f.Instance));Assert(observed.Phase=="starting"&&observed.Running,"Persistent closed state did not reproduce a concurrent worker lease");int runtime=f.RuntimeCalls,catalog=f.CatalogCalls;
                    var read=Task.Run(()=>Normal(f.Service.Read(f.Route,f.Actor,f.Instance,id)));Assert(read.Wait(2000)&&Value(read.Result,"state")=="installing","A receipt poll incorrectly required a free instance runtime");
                    var retry=Task.Run(()=>f.Start(choice));Assert(retry.Wait(2000)&&Value(retry.Result,"id")==id&&Value(retry.Result,"state")=="installing","An exact idempotent retry did not return its durable in-flight receipt");
                    Reject(()=>f.Start(choice,"different-mutation"));Assert(f.Installs==1&&f.RuntimeCalls==runtime&&f.CatalogCalls==catalog&&Directory.GetFiles(f.Store.Root,"portal-plugin-install-*.dpapi").Length==1,"Receipt reads started native work or a parallel mutation");
                    var original=Portal.Config(f.Store,f.Route);foreach(string changed in new[]{"actor","grant","account","home"}){
                        var config=Json.Read<PortalConfig>(Json.Write(original));if(changed=="actor")f.Allowed=false;else if(changed=="grant")config.Instances.Single().AuthorizationRevision++;else if(changed=="account")config.Instances.Single().AccountKey="foreign-account";else config.Instances.Single().Home=Path.Combine(f.Root,"foreign-home");f.Store.WriteRecord("portal-"+f.Route+"-v1.dpapi",config);
                        try{Reject(()=>f.Service.Read(f.Route,f.Actor,f.Instance,id));Reject(()=>f.Start(choice));}finally{f.Allowed=true;f.Store.WriteRecord("portal-"+f.Route+"-v1.dpapi",original);}
                    }
                }finally{release.Set();}
                Assert(Value(f.Wait(id),"state")=="needs-auth"&&f.Installs==1,"Receipt polling changed the single native outcome");
            }
        });
        check("plugin real GUI scope pins its owner and rejects replacement after the CLI read",()=>{
            using(var f=new Fixture(sandbox)){
                string folder=f.UseRealResolve();FictionalDesktop(folder,"fixture-owner");f.Dependencies.ProfileScope=PortalPluginCli.OpenProfileScope;
                f.Dependencies.Runtime=(target,token)=>{throw new Exception("GUI owner unexpectedly used a headless runtime");};f.Dependencies.Metadata=(target,ids,token)=>{throw new Exception("GUI owner unexpectedly used native metadata");};
                var catalog=f.Dependencies.Catalog;f.Dependencies.Catalog=(target,token)=>{var result=catalog(target,token);FictionalDesktop(folder,"replacement-owner");return result;};
                Reject(()=>f.Catalog());Assert(f.CatalogCalls==1&&f.Installs==0&&Directory.GetFiles(f.Store.Root,"portal-plugin-catalog-*.dpapi").Length==0,"A replaced GUI owner published a catalog from an earlier scope");
            }
        });
        check("plugin profile revalidation rejects account and grant drift immediately after scope acquisition",()=>{
            foreach(string changed in new[]{"account","grant","actor"})using(var f=new Fixture(sandbox)){
                f.Dependencies.ProfileScope=target=>{if(changed=="account")f.Account="replacement-account";else if(changed=="grant")f.Revision++;else f.Allowed=false;return new PortalPluginProfileScope(new Lease(),true);};
                Reject(()=>f.Catalog());Assert(f.CatalogCalls==0&&f.RuntimeCalls==0&&f.Installs==0,"Scope acquisition concealed an authorization or account change");
            }
        });
        check("plugin owned profile lease never bypasses a proven launch or corrupt state during inspection",()=>{
            foreach(string changed in new[]{"launch","unknown"})using(var f=new Fixture(sandbox)){
                string folder=f.UseRealResolve();f.Dependencies.ProfileScope=PortalPluginCli.OpenProfileScope;f.DuringRuntime=()=>{if(changed=="launch")FictionalDesktop(folder,"unexpected-launch");else File.WriteAllText(Path.Combine(folder,"state.json"),"fictional corrupt state");};
                Reject(()=>f.Catalog());Assert(f.RuntimeCalls==1&&f.CatalogCalls==0&&f.Installs==0,"An owned mutex bypassed actual launch evidence or an unknown state");
            }
        });
        check("plugin resolve waits for a brief concurrent headless history lease and then revalidates the real closed profile",()=>{
            using(var f=new Fixture(sandbox)){string folder=f.UseRealResolve();using(var reader=new HistoryReader(folder))using(var retried=new ManualResetEventSlim()){
                var snapshot=new DesktopRuntime(f.Root).Probe(new AccountService(f.Root).Data.Instances.Single(instance=>instance.Id==f.Instance));Assert(snapshot.Phase=="starting"&&snapshot.HostPid==0&&snapshot.DesktopPid==0&&!File.Exists(Path.Combine(folder,"state.json"))&&!File.Exists(Path.Combine(folder,"launch.json")),"History lease did not reproduce the actual transient starting state");
                f.DuringAuthorize=()=>{if(f.Authorizations>=2)retried.Set();};var read=Task.Run(()=>f.Catalog());Assert(retried.Wait(2000)&&!read.IsCompleted,"Resolve did not wait for and revalidate a transient history reader");reader.Release();Assert(read.Wait(3000)&&Value(read.Result,"instanceId")==f.Instance&&f.CatalogCalls==1&&f.RuntimeCalls==1&&f.Installs==0,"Released read lease failed or launched a native mutation");
            }}
        });
        check("plugin resolve keeps persistent history busy unknown and proven launch states closed",()=>{
            foreach(string kind in new[]{"busy","unknown","launch"})using(var f=new Fixture(sandbox)){string folder=f.UseRealResolve();if(kind=="unknown")File.WriteAllText(Path.Combine(folder,"state.json"),"fictional corrupt state");if(kind=="launch")using(var process=Process.GetCurrentProcess())File.WriteAllText(Path.Combine(folder,"state.json"),Json.Write(new InstanceState{Phase="starting",Running=true,LaunchId=Guid.NewGuid().ToString("N"),HostPid=process.Id,HostStartTicks=process.StartTime.ToUniversalTime().Ticks}));
                using(var reader=new HistoryReader(folder)){var clock=Stopwatch.StartNew();Reject(()=>f.Catalog());Assert(clock.Elapsed<TimeSpan.FromSeconds(2)&&f.CatalogCalls==0&&f.RuntimeCalls==0&&f.Installs==0,"Busy or unknown state reached a native adapter or exceeded its bounded wait");if(kind=="busy")Assert(f.Authorizations>1&&clock.ElapsedMilliseconds>=650,"Persistent busy was bypassed without the bounded recheck");else Assert(f.Authorizations==1,"Unknown or proven launch state was retried");}
            }
        });
        check("plugin resolve rechecks actor grant and account identity after waiting on a history reader",()=>{
            foreach(string change in new[]{"actor","grant","account"})using(var f=new Fixture(sandbox)){string folder=f.UseRealResolve();using(var reader=new HistoryReader(folder)){
                f.DuringAuthorize=()=>{if(f.Authorizations!=2)return;if(change=="actor")f.Allowed=false;else{var config=Portal.Config(f.Store,f.Route);if(change=="grant")config.Instances.Clear();else config.Instances.Single().AccountKey="fictional-other-account";f.Store.WriteRecord("portal-"+f.Route+"-v1.dpapi",config);}};
                Reject(()=>f.Catalog());Assert(f.Authorizations==2&&f.CatalogCalls==0&&f.RuntimeCalls==0&&f.Installs==0,"A changed authorization or account survived the transient wait");
            }}
        });
        check("plugin resolve refuses launch evidence that appears while a history lease is being released",()=>{
            using(var f=new Fixture(sandbox)){string folder=f.UseRealResolve();using(var reader=new HistoryReader(folder)){
                f.DuringAuthorize=()=>{if(f.Authorizations==2){File.WriteAllText(Path.Combine(folder,"launch.json"),Json.Write(new InstanceLaunch{Root=f.Root,InstanceId=f.Instance,LaunchId=Guid.NewGuid().ToString("N")}));reader.Release();}};
                Reject(()=>f.Catalog());Assert(f.Authorizations==2&&f.CatalogCalls==0&&f.RuntimeCalls==0&&f.Installs==0,"A real launch transition was treated as a released history reader");
            }}
        });
        check("plugin CLI uses exact supported list add arguments and isolates inherited account credentials",()=>{
            string previous=Environment.GetEnvironmentVariable("OPENAI_API_KEY");try{Environment.SetEnvironmentVariable("OPENAI_API_KEY","fictional-secret");var target=new PortalPluginTarget{Executable="fictional.exe",Home=Path.GetFullPath(sandbox)};var list=PortalPluginCli.StartInfo(target,null);var add=PortalPluginCli.StartInfo(target,"gmail@openai-curated-remote");Assert(list.Arguments.EndsWith("plugin list --available --json")&&add.Arguments.Contains("plugin add")&&add.Arguments.Contains("gmail@openai-curated-remote")&&!add.Arguments.Contains("turn/start")&&!add.UseShellExecute&&add.CreateNoWindow,"Unsupported native operation or visible shell");Assert(add.EnvironmentVariables["CODEX_HOME"]==target.Home&&add.EnvironmentVariables["CODEX_SQLITE_HOME"]==target.Home&&!add.EnvironmentVariables.ContainsKey("OPENAI_API_KEY"),"Foreign auth crossed instance boundary");bool rejected=false;try{PortalPluginCli.StartInfo(target,"gmail@remote --config evil=true");}catch(InvalidOperationException){rejected=true;}Assert(rejected,"CLI argument injection accepted");}finally{Environment.SetEnvironmentVariable("OPENAI_API_KEY",previous);}
        });
        check("plugin catalog projects opaque account bound choices without local paths selectors or auth values",()=>{
            using(var f=new Fixture(sandbox)){var catalog=f.Catalog();var row=RelayEngine.Rows(Json.Get(catalog,"plugins")).Single();string text=Json.Write(catalog);Assert(Value(row,"id").Length==64&&Value(row,"name")=="Gmail"&&Object.Equals(Json.Get(row,"canInstall"),true)&&!text.Contains(f.Root.Replace("\\","\\\\"))&&!text.Contains(f.Account)&&!text.Contains("gmail@openai-curated-remote"),"Private install mapping leaked");Assert(f.Installs==0&&f.RuntimeCalls==1,"Catalog installed something or started duplicate discovery");}
        });
        check("plugin installation retries reuse one durable operation and never run the native mutation twice",()=>{
            using(var f=new Fixture(sandbox))using(var held=new ManualResetEventSlim()){f.Held=held;string choice=f.Choice();var first=f.Start(choice);string id=Value(first,"id");DateTime until=DateTime.UtcNow.AddSeconds(3);while(f.Installs==0&&DateTime.UtcNow<until)Thread.Sleep(5);var retry=f.Start(choice);Assert(Value(retry,"id")==id&&f.Installs==1,"Lost response replayed native installation");held.Set();var done=f.Wait(id);Assert(Value(done,"state")=="needs-auth"&&f.Installs==1,"Install was falsely connected or duplicated");f.Start(choice);Assert(f.Installs==1,"Completed retry installed again");}
        });
        check("plugin install pins actor instance source policy and rejects stale or invented choices before mutation",()=>{
            foreach(string change in new[]{"account","grant","expired","invented","revoked"})using(var f=new Fixture(sandbox)){string choice=f.Choice();if(change=="account")f.Account="other-account";if(change=="grant")f.Revision++;if(change=="expired")f.Clock=f.Clock.AddMinutes(11);if(change=="invented")choice=new string('d',64);if(change=="revoked")f.Allowed=false;Reject(()=>f.Start(choice));Assert(f.Installs==0,"Invalid catalog scope reached native installation");}
        });
        check("plugin queued install rechecks native source and policy immediately before any mutation",()=>{
            foreach(string change in new[]{"source","policy","revoked"})using(var f=new Fixture(sandbox)){string choice=f.Choice();f.DuringRuntime=()=>{if(change=="source")f.Remote="replacement-plugin";if(change=="policy")f.Policy="NOT_AVAILABLE";if(change=="revoked")f.Allowed=false;};var operation=f.Start(choice);if(change=="revoked"){DateTime end=DateTime.UtcNow.AddSeconds(4);while(f.Store.ReadRecord<PortalPluginOperation>("portal-plugin-install-"+Value(operation,"id")+".dpapi").State=="installing"&&DateTime.UtcNow<end)Thread.Sleep(5);f.Allowed=true;f.DuringRuntime=null;}var done=f.Wait(Value(operation,"id"));Assert(Value(done,"state")=="failed"&&f.Installs==0,"Revoked or substituted native plugin installed");}
        });
        check("plugin uncertain mutations retain their receipt and block automatic duplicate or parallel installs",()=>{
            using(var f=new Fixture(sandbox)){f.FailInstall=true;string choice=f.Choice();string id=Value(f.Start(choice),"id");var done=f.Wait(id);Assert(Value(done,"state")=="uncertain"&&f.Installs==1&&!Json.Write(done).Contains("fictional credential"),"Uncertain native error leaked secrets or was labelled safe retry");Assert(Value(f.Start(choice),"id")==id&&f.Installs==1,"Uncertain retry replayed mutation");Reject(()=>f.Start(choice,"new-key"));}
        });
        check("plugin installed skill is ready without claiming a hosted connector and unavailable policy cannot install",()=>{
            using(var f=new Fixture(sandbox)){f.Installed=true;f.Remote="plugin_skill_only";f.Dependencies.Runtime=(target,token)=>new PortalPluginRuntime();var row=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();var done=f.Start(Value(row,"id"));Assert(Value(done,"state")=="ready"&&!Object.Equals(Json.Get(row,"callable"),true)&&f.Installs==0,"Skill only plugin required fabricated connection");}
            using(var f=new Fixture(sandbox)){f.Policy="NOT_AVAILABLE";string choice=f.Choice();Reject(()=>f.Start(choice));Assert(f.Installs==0,"Provider policy bypassed");}
        });
        check("plugin cloud connection is verified in the selected account runtime rather than inferred from enablement",()=>{
            using(var f=new Fixture(sandbox)){f.Installed=true;var disconnected=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();Assert(Object.Equals(Json.Get(disconnected,"enabled"),true)&&!Object.Equals(Json.Get(disconnected,"callable"),true)&&Object.Equals(Json.Get(disconnected,"needsAuth"),true),"Enabled plugin falsely labelled connected");f.Connected=true;var connected=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();Assert(Object.Equals(Json.Get(connected,"callable"),true)&&!Object.Equals(Json.Get(connected,"needsAuth"),true)&&f.Installs==0,"Read only refresh did not verify connection");}
        });
        check("plugin same name cannot borrow an unrelated hosted connector identity or connected status",()=>{
            using(var f=new Fixture(sandbox)){f.Installed=true;f.Connected=true;f.Remote="plugin_different_source";var row=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();Assert(!Object.Equals(Json.Get(row,"callable"),true)&&Json.Get(row,"installUrl")==null,"Same display name impersonated the connected Gmail app");}
        });
        check("plugin complete catalog keeps services beyond the former two thousand row search limit",()=>{
            var raw=Normal(new{installed=new object[0],available=Enumerable.Range(0,2501).Select(index=>new{pluginId="service"+index+"@openai-curated-remote",name="service"+index,marketplaceName="openai-curated-remote",source=new{source="remote",id="plugin_"+index},installPolicy="AVAILABLE"}).ToArray()});var warnings=new List<string>();var choices=PortalPluginService.Choices(raw,new PortalPluginRuntime(),"fictional",warnings);Assert(choices.Count==2501&&choices.Any(choice=>choice.Selector=="service2500@openai-curated-remote")&&warnings.Count==0,"A valid service disappeared from client search");
        });
        check("plugin unsupported local git npm sources cannot be installed with an empty source identity",()=>{
            foreach(string kind in new[]{"local","git","git-subdir","npm"}){var raw=Normal(new{installed=new object[0],available=new[]{new{pluginId="service@remote",name="service",marketplaceName="remote",source=new{source=kind,path="fictional",url="https://example.invalid/repository",package="fictional"},installPolicy="AVAILABLE"}}});var choices=PortalPluginService.Choices(raw,new PortalPluginRuntime(),"fictional",new List<string>());Assert(choices.Count==1&&!choices[0].CanInstall,"An unpinned nonremote source could be substituted before installation");}
        });
        check("plugin official cache binds exact global source and version and rejects stale or foreign metadata",()=>{
            using(var f=new Fixture(sandbox)){
                string home=Path.Combine(f.Root,"cache-home"),folder=Path.Combine(home,"cache","remote_plugin_catalog");Directory.CreateDirectory(folder);string file=Path.Combine(folder,"0123456789abcdef.json");
                foreach(string variant in new[]{"valid","version","scope","expired","future","source"}){
                    var raw=Normal(new{installed=new[]{new{pluginId="gmail@openai-curated-remote",name="gmail",marketplaceName="openai-curated-remote",installed=true,enabled=true,version="1.0",source=new{source="remote",id="plugin_fixture_gmail"},installPolicy="AVAILABLE"}},available=new object[0]});
                    object metadata=new{schema_version=1,fetched_at=(variant=="expired"?DateTime.UtcNow.AddHours(-2):variant=="future"?DateTime.UtcNow.AddMinutes(3):DateTime.UtcNow).ToString("o"),plugins=new[]{new{id=variant=="source"?"plugin_other":"plugin_fixture_gmail",scope=variant=="scope"?"PRIVATE":"GLOBAL",canonical_app_id="connector_fixture_exact",release=new{version=variant=="version"?"2.0":"1.0",display_name="Official Gmail with spaces",app_ids=new[]{"connector_fixture_exact"},@interface=new{short_description="A description with \"quotes\" and spaces"}}}}};SafeFiles.AtomicWrite(file,System.Text.Encoding.UTF8.GetBytes(Json.Write(metadata)));PortalPluginCli.ApplyCachedMetadata(home,raw,DateTime.UtcNow);
                    var runtime=new PortalPluginRuntime{Apps=new List<PortalPluginApp>{new PortalPluginApp{Id="connector_fixture_exact",Name="Same name is irrelevant",Enabled=true,Callable=true}}};var choice=PortalPluginService.Choices(raw,runtime,"fictional",new List<string>()).Single();Assert(choice.Callable==(variant=="valid"),"Stale private substituted source or version borrowed connected app identity");if(variant=="valid")Assert(choice.Name=="Official Gmail with spaces"&&choice.Description.Contains("\"quotes\" and spaces"),"Streaming metadata lost JSON text or whitespace");
                }
            }
        });
        check("plugin CLI add without native appsNeedingAuth still gets an official link and persists exact association",()=>{
            using(var f=new Fixture(sandbox)){
                bool omitMapping=false;string version="test",mapped="connector_fixture_exact";f.Remote="plugin_connector_1p_fixture";f.Dependencies.Catalog=(target,token)=>Normal(new{installed=f.Installed?new[]{new{pluginId="gmail@openai-curated-remote",name="gmail",marketplaceName="openai-curated-remote",installed=true,enabled=true,version=version,source=new{source="remote",id=f.Remote},installPolicy="AVAILABLE",__creezioAppIds=omitMapping?new string[0]:new[]{mapped},__creezioDisplayName="Gmail"}}:new object[0],available=f.Installed?new object[0]:new[]{new{pluginId="gmail@openai-curated-remote",name="gmail",marketplaceName="openai-curated-remote",source=new{source="remote",id=f.Remote},installPolicy="AVAILABLE"}}});
                f.Dependencies.Runtime=(target,token)=>new PortalPluginRuntime{Apps=f.Connected?new List<PortalPluginApp>{new PortalPluginApp{Id="connector_fixture_exact",Enabled=true,Callable=true}}:new List<PortalPluginApp>()};f.Dependencies.Metadata=(target,ids,token)=>new[]{new PortalPluginApp{Id="connector_fixture_exact",Name="Gmail",InstallUrl="https://chatgpt.com/apps/gmail/connector_fixture_exact"}};f.Dependencies.Install=(target,selector,token)=>{f.Installs++;f.Installed=true;return new{pluginId=selector,version="test",authPolicy="ON_INSTALL"};};
                string id=Value(f.Start(f.Choice()),"id");var operation=f.Wait(id);Assert(Value(operation,"state")=="needs-auth"&&Value(RelayEngine.Rows(Json.Get(operation,"appsNeedingAuth")).Single(),"installUrl")=="https://chatgpt.com/apps/gmail/connector_fixture_exact","Official CLI output without native-only field lost its link");omitMapping=true;f.Connected=true;var row=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();Assert(Object.Equals(Json.Get(row,"callable"),true)&&Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="ready"&&f.Installs==1,"A later read lost the authoritative association or replayed installation");
                omitMapping=false;version="next";mapped="connector_replacement";row=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();Assert(!Object.Equals(Json.Get(row,"callable"),true)&&Object.Equals(Json.Get(row,"needsAuth"),true),"An old persisted binding overrode a new version's required app");omitMapping=true;row=RelayEngine.Rows(Json.Get(f.Catalog(),"plugins")).Single();Assert(!Object.Equals(Json.Get(row,"callable"),true),"An older version binding authorized a later release when metadata disappeared");
            }
        });
        check("plugin multiple required apps must all be callable before the plugin is connected",()=>{
            var raw=Normal(new{installed=new[]{new{pluginId="multi@remote",name="multi",marketplaceName="remote",installed=true,enabled=true,source=new{source="remote",id="plugin_multi"},__creezioAppIds=new[]{"connector_one","connector_two"}}},available=new object[0]});var runtime=new PortalPluginRuntime{Apps=new List<PortalPluginApp>{new PortalPluginApp{Id="connector_one",Enabled=true,Callable=true},new PortalPluginApp{Id="connector_two",Enabled=false,Callable=false}}};var choice=PortalPluginService.Choices(raw,runtime,"fictional",new List<string>()).Single();Assert(!choice.Callable&&choice.NeedsAuth,"One callable app hid the remaining required authorization");runtime.Apps[1].Enabled=true;runtime.Apps[1].Callable=true;Assert(PortalPluginService.Choices(raw,runtime,"fictional",new List<string>()).Single().Callable,"All required apps were connected but plugin remained unavailable");
        });
        check("plugin explicit native catalog reconciliation resolves uncertain installation without replaying it",()=>{
            using(var f=new Fixture(sandbox)){f.FailInstall=true;string choice=f.Choice(),id=Value(f.Start(choice),"id");Assert(Value(f.Wait(id),"state")=="uncertain","Fictional native outcome was certain");f.Installed=true;f.Catalog();Assert(Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="needs-auth"&&f.Installs==1,"Read only observation failed to resolve installed outcome");f.Connected=true;f.Catalog();Assert(Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="ready"&&f.Installs==1,"Connection refresh did not finish the existing receipt");}
        });
        check("plugin reconciliation refuses a same named substituted source and retains uncertainty",()=>{
            using(var f=new Fixture(sandbox)){f.FailInstall=true;string choice=f.Choice(),id=Value(f.Start(choice),"id");f.Wait(id);f.Installed=true;f.Connected=true;f.Remote="plugin_substitute";f.Catalog();Assert(Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="uncertain"&&f.Installs==1,"Substituted source reconciled another plugin receipt");}
        });
        check("plugin links allow only official hosted apps pages and cannot expose callbacks credentials or external hosts",()=>{
            Assert(PortalPluginService.SafeUrl("https://chatgpt.com/apps/gmail/gmail")!=null,"Official app link refused");foreach(string url in new[]{"https://evil.example/apps/gmail","http://chatgpt.com/apps/gmail","https://user:pass@chatgpt.com/apps/gmail","https://chatgpt.com:8443/apps/gmail","https://chatgpt.com/apps/gmail?code=secret","https://chatgpt.com/connector/oauth/callback","http://127.0.0.1/callback","https://chatgpt.com.evil.example/apps/gmail"})Assert(PortalPluginService.SafeUrl(url)==null,"Untrusted auth link exposed");
        });
        check("plugin operation recovery records interruption without executing an orphaned installation",()=>{
            using(var f=new Fixture(sandbox)){f.Installed=true;string choice=f.Choice(),id=Value(f.Start(choice),"id");string file="portal-plugin-install-"+id+".dpapi";var record=f.Store.ReadRecord<PortalPluginOperation>(file);record.State="installing";record.NativeStarted=true;record.OwnerPid=Int32.MaxValue;f.Store.WriteRecord(file,record);Assert(Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="uncertain"&&f.Installs==0,"Recovered receipt replayed an unknown native send");}
        });
        check("plugin explicit catalog recovers an installed orphan without a remembered receipt or another mutation",()=>{
            using(var f=new Fixture(sandbox)){f.Installed=true;f.Connected=true;string id=Value(f.Start(f.Choice()),"id");string file="portal-plugin-install-"+id+".dpapi";var record=f.Store.ReadRecord<PortalPluginOperation>(file);record.State="installing";record.NativeStarted=true;record.OwnerPid=Int32.MaxValue;f.Store.WriteRecord(file,record);f.Catalog();Assert(Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="ready"&&f.Installs==0,"Read only catalog left a confirmed orphan permanently blocking installation");}
        });
        check("plugin receipt read cannot overwrite a worker that completed during scope verification",()=>{
            using(var f=new Fixture(sandbox))using(var held=new ManualResetEventSlim()){f.Held=held;f.Connected=true;string id=Value(f.Start(f.Choice()),"id");DateTime end=DateTime.UtcNow.AddSeconds(3);while(f.Installs==0&&DateTime.UtcNow<end)Thread.Sleep(5);var resolve=f.Dependencies.Resolve;bool release=true;f.Dependencies.Resolve=(route,actor,instance)=>{if(release){release=false;held.Set();DateTime until=DateTime.UtcNow.AddSeconds(3);while(f.Store.ReadRecord<PortalPluginOperation>("portal-plugin-install-"+id+".dpapi").State=="installing"&&DateTime.UtcNow<until)Thread.Sleep(5);}return resolve(route,actor,instance);};Assert(Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="ready"&&f.Installs==1,"A stale reader replaced a successful final receipt with interruption");}
        });
        check("plugin orphan readers and catalog reconciliation share one receipt transition lease",()=>{
            using(var f=new Fixture(sandbox))using(var entered=new ManualResetEventSlim()){
                f.Installed=true;f.Connected=true;string id=Value(f.Start(f.Choice()),"id"),file="portal-plugin-install-"+id+".dpapi";var record=f.Store.ReadRecord<PortalPluginOperation>(file);record.State="installing";record.NativeStarted=true;record.OwnerPid=Int32.MaxValue;f.Store.WriteRecord(file,record);
                Task<object> read,catalog;using(f.Store.Lease("portal-plugin-reconcile-"+id)){
                    read=Task.Run(()=>{entered.Set();return f.Service.Read(f.Route,f.Actor,f.Instance,id);});Assert(entered.Wait(1000)&&!read.Wait(50),"An orphan reader bypassed the common transition lease");catalog=Task.Run(()=>f.Catalog());
                }Assert(Task.WaitAll(new Task[]{read,catalog},5000)&&Value(f.Service.Read(f.Route,f.Actor,f.Instance,id),"state")=="ready"&&f.Installs==0,"Concurrent recovery overwrote native confirmation or replayed installation");
            }
        });
        check("plugin administration routes keep existing scope groups and reject query strings wrong verbs or unbound instances",()=>{
            string route=new string('a',32),instance=new string('c',32),id=new string('d',32);var catalog=PortalAdministration.Path("/v1/admin/"+route+"/instances/"+instance+"/plugins");var operation=PortalAdministration.Path("/v1/admin/"+route+"/instances/"+instance+"/plugin-installs/"+id);Assert(catalog.Success&&catalog.Groups[1].Value==route&&catalog.Groups[5].Value==instance&&catalog.Groups[6].Value=="plugins"&&operation.Groups[7].Value==id,"Signed path group contract changed");Assert(!PortalAdministration.Path("/v1/admin/"+route+"/instances/local/plugins").Success&&!PortalAdministration.Path("/v1/admin/"+route+"/instances/"+instance+"/plugins?q=gmail").Success,"Unbound route allowed");
        });
    }
}
