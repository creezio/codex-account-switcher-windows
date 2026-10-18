using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class RelayReconnectTests
{
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private const string Pipe=@"\\.\pipe\codex-browser-use-11111111-2222-3333-4444-555555555555";
    private sealed class Runtime:IInstanceRuntime
    {
        internal readonly InstanceState State=new InstanceState{Running=true,Phase="running",DesktopPid=222,DesktopStartTicks=333};
        internal int Starts,Stops;
        public InstanceState Probe(DesktopInstance instance){return State;}
        public Task Start(DesktopInstance instance,CancellationToken token){Starts++;throw new Exception("Reconnect must not launch Codex");}
        public Task Stop(DesktopInstance instance,CancellationToken token){Stops++;throw new Exception("Reconnect must not stop Codex");}
    }
    private sealed class Fixture
    {
        internal readonly AccountService Accounts;
        internal readonly RelayStore Store;
        internal readonly Runtime Native=new Runtime();
        internal readonly DesktopInstance Instance;
        internal readonly string Home;
        internal int Discoveries;
        internal Fixture(string root,string auth,bool local,int number)
        {
            Accounts=new AccountService(Path.Combine(root,"reconnect-"+number),Native);
            Accounts.Settings.CodexHome=Path.Combine(Accounts.Vault.Root,"local-home");
            var profile=Accounts.Import(auth,"Fixture");
            Instance=local?Accounts.Data.Instances.Single(i=>i.IsLocal):new DesktopInstance{Id=Guid.NewGuid().ToString("N"),Name="Managed"};
            if(!local)Accounts.Data.Instances.Add(Instance);
            Instance.AccountKey=profile.Key;Instance.AccountLocked=true;Instance.HeadlessEnabled=true;Accounts.Save();
            Home=Accounts.Instances.Home(Instance);Directory.CreateDirectory(Home);File.WriteAllText(Path.Combine(Home,"auth.json"),auth);
            Store=new RelayStore(Path.Combine(Accounts.Vault.Root,"relay"));
            Store.Register(new RelayChannel{Id="custom-source",Name="Preserved label",Provider="codex",InstanceId=Instance.Id,Home=Home,AccountKey=profile.Key,Workspace=Accounts.Vault.Root,AnchorThreadId="strict-anchor",CodexProjectId="protected-project",RequireFullAccess=true,Enabled=true,ServerPid=111,ServerStartTicks=112,PipePath=@"\\.\pipe\old-endpoint"});
        }
        internal RelayReconnect.Endpoint Proof(){return new RelayReconnect.Endpoint{PipePath=Pipe,Pid=222,Started=333,ThreadId="strict-anchor"};}
        internal Task<RelayReconnect.Endpoint> Discover(RelayStore store,AccountService accounts,DesktopInstance instance,RelayChannel channel,CancellationToken token)
        {
            Discoveries++;Assert(instance.Id==Instance.Id&&channel.AnchorThreadId=="strict-anchor","discovery changed its instance or anchor");return Task.FromResult(Proof());
        }
        internal void Refresh(Func<RelayStore,AccountService,DesktopInstance,RelayChannel,CancellationToken,Task<RelayReconnect.Endpoint>> discover=null,Func<int,long,bool> alive=null)
        {RelayReconnect.RefreshCore(Store,()=>Accounts,discover??Discover,alive??((pid,started)=>pid==222&&started==333),CancellationToken.None).GetAwaiter().GetResult();Assert(Native.Starts==0&&Native.Stops==0,"reconnect changed an application lifecycle");}
    }
    internal static void RunAll(Action<string,Action> check,string root,string auth,string otherAuth)
    {
        int number=0;
        check("desktop reconnect supports primary and managed custom channels while preserving all policy fields",()=>{
            foreach(bool local in new[]{true,false}){
                var f=new Fixture(root,auth,local,++number);var original=f.Store.Channel("custom-source");f.Refresh();var updated=f.Store.Channel(original.Id);
                Assert(updated.PipePath==Pipe&&updated.ServerPid==222&&updated.ServerStartTicks==333&&updated.Diagnostic=="Reconnexion vérifiée","verified endpoint was not published");
                updated.PipePath=original.PipePath;updated.ServerPid=original.ServerPid;updated.ServerStartTicks=original.ServerStartTicks;updated.ConnectedUtc=original.ConnectedUtc;updated.Diagnostic=original.Diagnostic;
                Assert(Json.Write(updated)==Json.Write(original)&&f.Store.Channels().Count==1,"reconnection replaced a permission, anchor or channel");
            }
        });
        check("desktop reconnect recognizes legacy primary channels by their pinned home without inventing an instance id",()=>{
            var f=new Fixture(root,auth,true,++number);var channel=f.Store.Channel("custom-source");channel.InstanceId=null;f.Store.Register(channel);f.Refresh();
            Assert(f.Store.Channel(channel.Id).ServerPid==222&&f.Store.Channel(channel.Id).InstanceId==null&&f.Discoveries==1,"legacy primary channel was skipped or rewritten");
        });
        check("desktop reconnect refuses account home and instance drift before native discovery",()=>{
            foreach(string change in new[]{"account","home","instance","archive"}){
                var f=new Fixture(root,auth,true,++number);var channel=f.Store.Channel("custom-source");
                if(change=="account")File.WriteAllText(Path.Combine(f.Home,"auth.json"),otherAuth);
                if(change=="home")f.Accounts.Settings.CodexHome=Path.Combine(f.Accounts.Vault.Root,"other-home");
                if(change=="instance"){channel.InstanceId=Guid.NewGuid().ToString("N");f.Store.Register(channel);}
                if(change=="archive")f.Instance.Archived=true;
                f.Refresh();Assert(f.Discoveries==0&&f.Store.Channel(channel.Id).ServerPid==111,"unbound instance reached native discovery");
            }
        });
        check("desktop reconnect rechecks credentials endpoint lifetime anchor and runtime after discovery",()=>{
            foreach(string change in new[]{"account","home","anchor","pid","runtime"}){
                var f=new Fixture(root,auth,false,++number);
                f.Refresh((store,accounts,instance,channel,token)=>{
                    var proof=f.Proof();if(change=="account")File.WriteAllText(Path.Combine(f.Home,"auth.json"),otherAuth);
                    if(change=="home")instance.AccountKey="different-account";
                    if(change=="anchor")proof.ThreadId="unrelated-anchor";
                    if(change=="pid")proof.Pid=999;
                    if(change=="runtime")f.Native.State.DesktopStartTicks=334;
                    return Task.FromResult(proof);
                });
                Assert(f.Store.Channel("custom-source").ServerPid==111,"drifting proof replaced an endpoint");
            }
        });
        check("desktop reconnect compare and swap preserves concurrent user edits and another reconnect",()=>{
            foreach(string edit in new[]{"disabled","permissions","anchor","rename","endpoint"}){
                var f=new Fixture(root,auth,true,++number);
                f.Refresh((store,accounts,instance,channel,token)=>{
                    var current=store.Channel(channel.Id);
                    if(edit=="disabled")current.Enabled=false;if(edit=="permissions")current.RequireFullAccess=false;if(edit=="anchor")current.AnchorThreadId="new-user-anchor";if(edit=="rename")current.Name="New user label";if(edit=="endpoint"){current.ServerPid=444;current.ServerStartTicks=445;}
                    store.Register(current);return Task.FromResult(f.Proof());
                });
                var saved=f.Store.Channel("custom-source");Assert(saved.ServerPid==(edit=="endpoint"?444:111),"stale discovery overwrote a concurrent edit");
                Assert(edit!="disabled"||!saved.Enabled,"reconnection enabled a disabled channel");Assert(edit!="permissions"||!saved.RequireFullAccess,"reconnection overwrote permissions");
            }
        });
        check("desktop reconnect skips enabled live channels and all disabled or headless channels",()=>{
            foreach(string state in new[]{"live","disabled","headless"}){
                var f=new Fixture(root,auth,true,++number);var channel=f.Store.Channel("custom-source");if(state=="disabled")channel.Enabled=false;if(state=="headless")channel.Provider="codex-headless";f.Store.Register(channel);
                f.Refresh(null,(pid,started)=>state=="live");Assert(f.Discoveries==0,"reconnect read an ineligible channel");
            }
        });
        check("desktop endpoint discovery never starts a closed or ambiguous instance and honors cancellation",()=>{
            foreach(string state in new[]{"closed","ambiguous","cancelled"}){
                var f=new Fixture(root,auth,true,++number);if(state=="closed")f.Native.State.Running=false;if(state=="ambiguous")f.Native.State.Phase="unknown";
                using(var cancel=new CancellationTokenSource()){
                    if(state=="cancelled")cancel.Cancel();bool failed=false;
                    try{string pipe;using(NamedInstances.OpenDesktop(f.Store,f.Accounts,f.Instance,cancel.Token,out pipe)){} }catch(InvalidOperationException){failed=true;}catch(OperationCanceledException){failed=true;}
                    Assert(failed&&f.Native.Starts==0&&f.Native.Stops==0&&f.Store.Channel("custom-source").ServerPid==111,"read-only desktop discovery started or registered work");
                }
            }
        });
        check("desktop reconnect cancellation leaves the channel unchanged",()=>{
            var f=new Fixture(root,auth,true,++number);string before=Json.Write(f.Store.Channel("custom-source"));
            using(var cancel=new CancellationTokenSource()){
                bool cancelled=false;try{RelayReconnect.RefreshCore(f.Store,()=>f.Accounts,(store,accounts,instance,channel,token)=>{cancel.Cancel();return Task.FromResult(f.Proof());},(pid,started)=>false,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
                Assert(cancelled&&Json.Write(f.Store.Channel("custom-source"))==before,"cancelled discovery changed a channel");
            }
        });
    }
}
