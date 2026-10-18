using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal sealed class HeadlessTransportFixture:IDisposable
{
    internal const string Secret="PRIVATE_HEADLESS_MISSION_MUST_NOT_BE_PLAIN_TEXT";
    internal readonly AccountService Accounts;
    internal readonly DesktopInstance Instance;
    internal readonly RelayStore Store;
    internal readonly RelayChannel Channel;
    internal readonly HeadlessRelayTransport Transport;
    internal readonly List<Fake> Connections=new List<Fake>();
    internal readonly List<KeyValuePair<string,object>> Calls=new List<KeyValuePair<string,object>>();
    internal string ThreadId=Guid.NewGuid().ToString(),TurnId=Guid.NewGuid().ToString(),Status="inProgress",NativeThreadId;
    internal string NativeSandbox,NativeApproval,NativeCwd;
    internal string[] ActiveFlags=new string[0];
    internal bool ThrowTurn;
    internal Action<string> Hook;
    internal HeadlessTransportFixture(string root,int number,string auth)
    {
        Accounts=new AccountService(Path.Combine(root,"headless-transport-"+number));var profile=Accounts.Import(auth,"Fixture");Instance=Accounts.Instances.Create("Headless fixture");Accounts.Instances.BindAccount(Instance,profile);
        Instance.HeadlessEnabled=true;Instance.HeadlessSandbox="read-only";Instance.HeadlessApprovalPolicy="on-request";Accounts.Save();
        Accounts.Settings.CodexExecutable=Process.GetCurrentProcess().MainModule.FileName;Accounts.Vault.SaveSettings(Accounts.Settings);
        string workspace=Path.Combine(Accounts.Vault.Root,"workspace");Directory.CreateDirectory(workspace);Store=new RelayStore(Path.Combine(Accounts.Vault.Root,"relay"));
        Channel=new RelayChannel{Id="headless",Provider="codex-headless",InstanceId=Instance.Id,AccountKey=Instance.AccountKey,Home=Accounts.Instances.Home(Instance),Workspace=workspace,Enabled=true,Executable=Accounts.Settings.CodexExecutable,HeadlessSandbox=Instance.HeadlessSandbox,HeadlessApprovalPolicy=Instance.HeadlessApprovalPolicy};Store.Register(Channel);
        Transport=new HeadlessRelayTransport(Store,c=>{var rpc=new Fake(this);Connections.Add(rpc);return rpc;});
    }
    internal static object Obj(object value){return Json.Read<object>(Json.Write(value));}
    internal int Count(string method){return Calls.Count(p=>p.Key==method);}
    internal object Args(string method){return Calls.Last(p=>p.Key==method).Value;}
    internal object Call(string tool,object args){return Transport.Call(Channel,tool,args,CancellationToken.None).GetAwaiter().GetResult();}
    internal object Create(){return Call("create_thread",new{title="Fixture mission",prompt=Secret});}
    internal HeadlessThreadReceipt Receipt(){return Store.ReadRecord<HeadlessThreadReceipt>(HeadlessRelayTransport.ReceiptName(ThreadId));}
    internal void SaveReceipt(HeadlessThreadReceipt receipt){Store.WriteRecord(HeadlessRelayTransport.ReceiptName(ThreadId),receipt);}
    internal HeadlessThreadReceipt Own(string state="created")
    {
        var receipt=new HeadlessThreadReceipt{ThreadId=ThreadId,TurnId=TurnId,ChannelId=Channel.Id,AccountKey=Channel.AccountKey,Home=Channel.Home,Workspace=Channel.Workspace,Sandbox=Channel.HeadlessSandbox,ApprovalPolicy=Channel.HeadlessApprovalPolicy,Title="Fixture",State=state,RuntimeId="old-runtime"};SaveReceipt(receipt);return receipt;
    }
    public void Dispose(){HeadlessRelayTransport.Shutdown(Store);}
    internal sealed class Fake:IHeadlessRpc
    {
        private readonly HeadlessTransportFixture fixture;
        internal bool Disposed;
        internal Fake(HeadlessTransportFixture fixture){this.fixture=fixture;}
        public Func<string,object,CancellationToken,Task<object>> RequestHandler{get;set;}
        public int ProcessId{get{return 0;}}
        public long ProcessStartedTicks{get{return 0;}}
        public event Action<string,object> Notification{add{}remove{}}
        public event Action<HeadlessInteractionException> InteractionRequired{add{}remove{}}
        public Task<object> Call(string method,object arguments,CancellationToken token)
        {
            if(Disposed)throw new ObjectDisposedException("fixture");token.ThrowIfCancellationRequested();var args=Obj(arguments);fixture.Calls.Add(new KeyValuePair<string,object>(method,args));if(fixture.Hook!=null)fixture.Hook(method);
            string kind=fixture.NativeSandbox??(fixture.Channel.HeadlessSandbox=="read-only"?"readOnly":fixture.Channel.HeadlessSandbox=="workspace-write"?"workspaceWrite":"dangerFullAccess");
            if(method=="thread/start"||method=="thread/resume")return Task.FromResult(Obj(new{thread=new{id=fixture.ThreadId,status=new{type="idle"}},cwd=fixture.NativeCwd??fixture.Channel.Workspace,sandbox=new{type=kind},approvalPolicy=fixture.NativeApproval??fixture.Channel.HeadlessApprovalPolicy}));
            if(method=="thread/name/set"||method=="turn/interrupt"||method=="thread/settings/update")return Task.FromResult(Obj(new{}));
            if(method=="turn/start"){if(fixture.ThrowTurn)throw new IOException(Secret);return Task.FromResult(Obj(new{turn=new{id=fixture.TurnId,status="inProgress"}}));}
            if(method=="thread/read")return Task.FromResult(Obj(new{thread=new{id=fixture.NativeThreadId??fixture.ThreadId,status=new{type=fixture.Status=="inProgress"?"active":"idle",activeFlags=fixture.ActiveFlags},turns=new[]{new{id=fixture.TurnId,status=fixture.Status,items=new[]{new{type="agentMessage",text="Verified final fixture",phase=fixture.Status=="completed"?"":"commentary"}}}}}}));
            throw new InvalidOperationException("Unexpected native fixture operation: "+method);
        }
        public void Dispose(){Disposed=true;}
    }
}

internal static class HeadlessTransportTests
{
    private static void Assert(bool value,string why){if(!value)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception e){return e;}throw new Exception("Expected headless rejection");}
    internal static void RunAll(Action<string,Action> check,string root,string auth)
    {
        int number=0;
        check("headless registry reads retry Windows replacement disappearance using a fresh snapshot",()=>{
            string folder=Path.Combine(root,"headless-registry-"+(++number));Directory.CreateDirectory(folder);var store=new RelayStore(folder);store.Register(new RelayChannel{Id="preserved",Home=folder,AccountKey="fixture",Workspace=folder,Enabled=false});string path=Path.Combine(folder,"channels.dpapi");
            foreach(int code in new[]{2,3,32,33,1175}){
                int attempts=0;var waits=new List<int>();var rows=RelayStore.ReadFile<List<RelayChannel>>(path,true,true,p=>{if(++attempts<3)throw new IOException("simulated native replacement",unchecked((int)(0x80070000u+(uint)code)));return new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);},waits.Add);
                Assert(attempts==3&&waits.SequenceEqual(new[]{20,40})&&rows.Count==1&&rows[0].Id=="preserved"&&!rows[0].Enabled,"replacement became an empty registry or changed a disabled channel");
            }
        });
        check("headless registry absence is bounded and only initial optional records can default",()=>{
            string folder=Path.Combine(root,"headless-registry-"+(++number));Directory.CreateDirectory(folder);string path=Path.Combine(folder,"channels.dpapi");
            int attempts=0;var waits=new List<int>();Func<string,Stream> missing=p=>{attempts++;throw new FileNotFoundException("fixture absent");};
            var initial=RelayStore.ReadFile<List<RelayChannel>>(path,true,false,missing,waits.Add);Assert(initial.Count==0&&attempts==6&&waits.Sum()==300,"initial absence was unbounded or skipped the registry stabilization window");
            attempts=0;waits.Clear();Assert(Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,true,missing,waits.Add)) is FileNotFoundException&&attempts==6&&waits.Sum()==300,"required registry returned an invented empty snapshot");
            attempts=0;waits.Clear();var receipt=RelayStore.ReadFile<List<RelayChannel>>(path,false,false,missing,waits.Add);Assert(receipt.Count==0&&attempts==1&&waits.Count==0,"normal absent optional receipt incurred a retry delay");
            attempts=0;waits.Clear();Assert(Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,false,p=>{attempts++;if(attempts==1)throw new IOException("observed existing locked registry",unchecked((int)0x80070020));throw new FileNotFoundException("replacement temporarily hides the same file");},waits.Add)) is FileNotFoundException&&attempts==6,"previously observed registry silently became an initial empty installation");
            File.WriteAllText(path+".switcher-tmp","active fixture writer");attempts=0;waits.Clear();Assert(Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,false,missing,waits.Add)) is FileNotFoundException&&attempts==6,"active atomic writer was mistaken for an initial empty registry");
            attempts=0;waits.Clear();Assert(Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,false,p=>{attempts++;throw new IOException("persistent sharing",unchecked((int)0x80070020));},waits.Add)) is IOException&&attempts==6&&waits.Sum()==300,"persistent sharing yielded empty data or exceeded its bound");
        });
        check("headless registry corruption and nontransient errors never become initial empty data",()=>{
            string folder=Path.Combine(root,"headless-registry-"+(++number));Directory.CreateDirectory(folder);string path=Path.Combine(folder,"channels.dpapi");int attempts=0,waits=0;
            Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,false,p=>{attempts++;throw new UnauthorizedAccessException("fixture inaccessible");},ms=>waits++));Assert(attempts==1&&waits==0,"permanent access refusal was retried or hidden");
            attempts=0;Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,false,p=>{attempts++;throw new IOException("foreign HRESULT",unchecked((int)0x80130002));},ms=>waits++));Assert(attempts==1&&waits==0,"non-Win32 low error bits were mistaken for a missing file");
            attempts=0;Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,false,p=>{attempts++;return new MemoryStream(new byte[48]);},ms=>waits++));Assert(attempts==1&&waits==0,"corrupt DPAPI snapshot was retried or replaced by defaults");
            attempts=0;Reject(()=>RelayStore.ReadFile<List<RelayChannel>>(path,true,false,p=>{attempts++;return new MemoryStream(new byte[4194305]);},ms=>waits++));Assert(attempts==1&&waits==0,"oversized registry was retried or treated as empty");
            var store=new RelayStore(folder);store.WriteRecord("channels.dpapi",(object)null);Reject(()=>store.Channels());Assert(File.Exists(path),"null registry was recreated as empty");
        });
        check("headless concurrent registry replacement preserves every channel and disabled grant",()=>{
            string folder=Path.Combine(root,"headless-registry-"+(++number));Directory.CreateDirectory(folder);var store=new RelayStore(folder);var current=new RelayChannel{Id="writer",Home=folder,AccountKey="fixture",Workspace=folder,Enabled=true};store.Register(current);store.Register(new RelayChannel{Id="preserved-disabled",Home=folder,AccountKey="fixture",Workspace=folder,Enabled=false});
            var started=new ManualResetEventSlim(false);int writes=0;var writing=Task.Run(()=>{started.Set();for(int i=0;i<100;i++){current.Name="revision-"+i;store.Register(current);Interlocked.Increment(ref writes);}});
            try{Assert(started.Wait(5000),"fixture writer did not start");for(int i=0;i<300;i++){var rows=store.Channels();Assert(rows.Count==2&&rows.Any(c=>c.Id=="writer")&&rows.Any(c=>c.Id=="preserved-disabled"&&!c.Enabled),"atomic replacement exposed an empty or incomplete registry");}}finally{writing.GetAwaiter().GetResult();started.Dispose();}
            Assert(writes==100&&store.Channels().Count==2&&!store.Channel("preserved-disabled").Enabled,"registry update lost another channel or re-enabled it");
        });
        check("headless running turn survives a transient required registry read without retransmission",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();var held=new FileStream(Path.Combine(f.Store.Root,"channels.dpapi"),FileMode.Open,FileAccess.Read,FileShare.None);var released=Task.Run(()=>{Thread.Sleep(80);held.Dispose();});
                try{f.Call("read_thread",new{threadId=f.ThreadId});}finally{released.GetAwaiter().GetResult();held.Dispose();}
                Assert(f.Count("turn/start")==1&&f.Count("thread/start")==1&&f.Count("thread/read")==1&&!f.Connections[0].Disposed&&f.Receipt().State=="running","registry contention revoked a running turn or sent it again");
            }
        });
        check("headless valid revocation mismatch and unreadable registry still revoke without replay",()=>{
            foreach(string changed in new[]{"missing-file","missing-id","disabled","account","home","permissions","corrupt","null"})using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();string path=Path.Combine(f.Store.Root,"channels.dpapi");var channel=f.Store.Channel(f.Channel.Id);
                if(changed=="missing-file")File.Delete(path);else if(changed=="corrupt")File.WriteAllBytes(path,new byte[48]);else if(changed=="null")f.Store.WriteRecord("channels.dpapi",(object)null);else if(changed=="missing-id")f.Store.WriteRecord("channels.dpapi",new RelayChannel[0]);else{if(changed=="disabled")channel.Enabled=false;else if(changed=="account")channel.AccountKey="foreign";else if(changed=="home")channel.Home=root;else channel.HeadlessSandbox="danger-full-access";f.Store.WriteRecord("channels.dpapi",new[]{channel});}
                Assert(Reject(()=>f.Call("read_thread",new{threadId=f.ThreadId})) is HeadlessRuntimeLostException&&f.Connections[0].Disposed&&f.Count("turn/start")==1&&f.Count("thread/read")==0&&f.Receipt().State=="uncertain","invalid registry was tolerated or caused a new native send");
            }
        });
        check("headless explicit full access remains chat scoped and inherit preserves it",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.NativeSandbox="dangerFullAccess";f.NativeApproval="never";var composer=new PortalResolvedComposer{PermissionProfile="full-access",InstanceId=f.Channel.InstanceId,AccountKey=f.Channel.AccountKey,Home=f.Channel.Home,InputItems=new object[0]};
                f.Call("create_thread",new{title="Fictional explicit permissions",prompt="Fixture",creezioComposer=composer});Assert(Json.Str(Json.Get(f.Args("thread/start"),"sandbox"))=="danger-full-access"&&Json.Str(Json.Get(f.Args("turn/start"),"approvalPolicy"))=="never"&&f.Receipt().PermissionProfile=="full-access","Explicit full access not applied natively");Assert(f.Channel.HeadlessSandbox=="read-only"&&f.Accounts.Data.Instances.Single(i=>i.Id==f.Instance.Id).HeadlessSandbox=="read-only","Chat choice changed instance defaults");
                f.Status="completed";f.Call("read_thread",new{threadId=f.ThreadId});f.Status="inProgress";f.Call("send_message_to_thread",new{threadId=f.ThreadId,prompt="Inherit fixture"});Assert(Json.Str(Json.Get(f.Args("thread/resume"),"sandbox"))=="danger-full-access"&&Json.Str(Json.Get(f.Args("turn/start"),"approvalPolicy"))=="never"&&f.Count("thread/settings/update")==1,"Inherit silently reverted selected chat permissions");
            }
        });
        check("headless explicit readonly overrides an implicit full default without changing it",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Instance.HeadlessSandbox="danger-full-access";f.Instance.HeadlessApprovalPolicy="never";f.Accounts.Save();f.Channel.HeadlessSandbox="danger-full-access";f.Channel.HeadlessApprovalPolicy="never";f.Channel.RequireFullAccess=true;f.Store.Register(f.Channel);f.NativeSandbox="readOnly";f.NativeApproval="on-request";
                var composer=new PortalResolvedComposer{PermissionProfile="read-only",InstanceId=f.Channel.InstanceId,AccountKey=f.Channel.AccountKey,Home=f.Channel.Home,InputItems=new object[0]};f.Call("create_thread",new{title="Fictional readonly",prompt="Fixture",creezioComposer=composer});Assert(Json.Str(Json.Get(f.Args("thread/start"),"sandbox"))=="read-only"&&Json.Str(Json.Get(f.Args("turn/start"),"approvalPolicy"))=="on-request","Read-only overridden by old full default");
                f.Status="completed";f.Call("read_thread",new{threadId=f.ThreadId});f.Status="inProgress";f.Call("send_message_to_thread",new{threadId=f.ThreadId,prompt="Preserve readonly"});Assert(Json.Str(Json.Get(f.Args("thread/resume"),"sandbox"))=="read-only"&&Json.Str(Json.Get(f.Args("turn/start"),"approvalPolicy"))=="on-request"&&f.Count("thread/settings/update")==1,"Inheritance blocked or expanded a prior explicit restricted chat");Assert(f.Channel.HeadlessSandbox=="danger-full-access"&&f.Channel.HeadlessApprovalPolicy=="never","Instance configuration modified");
            }
        });
        check("headless local applies the selected webhook model and effort to the native turn",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Call("create_thread",new{title="Configured execution",prompt="Fictional mission",model="gpt-6.1-sol",thinking="ultra"});
                Assert(Json.Str(Json.Get(f.Args("thread/start"),"model"))=="gpt-6.1-sol"&&Json.Str(Json.Get(f.Args("turn/start"),"model"))=="gpt-6.1-sol"&&Json.Str(Json.Get(f.Args("turn/start"),"effort"))=="ultra","selected model or effort lost");
                Assert(Json.Str(Json.Get(f.Args("thread/start"),"sandbox"))==f.Channel.HeadlessSandbox&&Json.Str(Json.Get(f.Args("turn/start"),"approvalPolicy"))==f.Channel.HeadlessApprovalPolicy,"execution selection changed native permissions");
            }
        });
        check("headless local missions validate opt-in, provider, identity and executable before connecting",()=>{
            foreach(string changed in new[]{"disabled","provider","account","executable","permissions"})using(var f=new HeadlessTransportFixture(root,++number,auth)){
                if(changed=="disabled"){f.Instance.HeadlessEnabled=false;f.Accounts.Save();}else if(changed=="provider")f.Channel.Provider="codex";else if(changed=="account")File.WriteAllText(Path.Combine(f.Channel.Home,"auth.json"),"{}");else if(changed=="executable")f.Channel.Executable=Path.Combine(root,"missing.exe");else f.Channel.HeadlessSandbox="danger-full-access";
                Reject(()=>f.Create());Assert(f.Connections.Count==0&&f.Count("turn/start")==0,"invalid destination started a native service or mission");
            }
        });
        check("headless named routing tolerates an uninspectable stale desktop PID and keeps UI-required transfers separate",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                var legacy=new RelayChannel{Id=NamedInstances.ChannelId(f.Channel.Home,f.Channel.Workspace),Provider="codex",InstanceId=f.Instance.Id,Name="Stale desktop fixture",AccountKey=f.Channel.AccountKey,Home=f.Channel.Home,Workspace=f.Channel.Workspace,Enabled=true,ServerPid=4,ServerStartTicks=1};f.Store.Register(legacy);
                var channel=NamedInstances.Connect(f.Store,f.Accounts,f.Instance,f.Channel.Workspace,CancellationToken.None,null,true).GetAwaiter().GetResult();
                Assert(channel.Provider=="codex-headless"&&channel.Id==NamedInstances.HeadlessChannelId(f.Channel.Home,f.Channel.Workspace)&&f.Calls.Count==0,"stale unrelated PID blocked closed-profile routing or started a model");
                Reject(()=>NamedInstances.Connect(f.Store,f.Accounts,f.Instance,f.Channel.Workspace,CancellationToken.None,null,false).GetAwaiter().GetResult());Assert(f.Calls.Count==0,"a UI-required operation silently selected headless execution");
            }
        });
        check("headless local start pins workspace and permissions and persists only an encrypted owned receipt",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                var created=f.Create();var receipt=f.Receipt();Assert(Json.Str(Json.Get(created,"threadId"))==f.ThreadId&&receipt.State=="running"&&receipt.TurnId==f.TurnId,"native thread and turn were not bound");
                var args=f.Args("thread/start");Assert(Json.Str(Json.Get(args,"cwd"))==f.Channel.Workspace&&Json.Str(Json.Get(args,"sandbox"))=="read-only"&&Json.Str(Json.Get(args,"approvalPolicy"))=="on-request"&&Object.Equals(Json.Get(args,"ephemeral"),false),"requested permissions or history mode changed");
                string raw=System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,HeadlessRelayTransport.ReceiptName(f.ThreadId))));Assert(!raw.Contains(HeadlessTransportFixture.Secret)&&receipt.PromptHash==RemotePeers.Hash(HeadlessTransportFixture.Secret),"receipt retains the raw mission");
                Assert(f.Connections.Count==1&&!f.Connections[0].Disposed,"a running turn lost its native process");f.Status="completed";f.Call("read_thread",new{threadId=f.ThreadId});Assert(f.Connections[0].Disposed&&!f.Store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(f.Channel.AccountKey)).Running,"completed task retained its idle service");
            }
        });
        check("headless local refuses native contexts with changed sandbox, approval policy or folder before a prompt",()=>{
            foreach(string changed in new[]{"sandbox","approval","cwd"})using(var f=new HeadlessTransportFixture(root,++number,auth)){
                if(changed=="sandbox")f.NativeSandbox="dangerFullAccess";else if(changed=="approval")f.NativeApproval="never";else f.NativeCwd=Path.GetDirectoryName(f.Channel.Workspace);
                Reject(()=>f.Create());Assert(f.Count("turn/start")==0&&f.Connections.All(c=>c.Disposed),"unconfirmed native context received a mission");
            }
        });
        check("headless local cannot read, append to or interrupt a user or foreign conversation",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                foreach(string operation in new[]{"read_thread","send_message_to_thread","interrupt_thread"})Reject(()=>f.Call(operation,new{threadId=Guid.NewGuid().ToString(),prompt="Unauthorised"}));
                Assert(f.Connections.Count==0&&f.Calls.Count==0,"foreign chat reached the native server");
                var own=f.Own();own.AccountKey="different";f.SaveReceipt(own);Reject(()=>f.Call("send_message_to_thread",new{threadId=f.ThreadId,prompt="Wrong account"}));Assert(f.Connections.Count==0,"tampered account receipt was accepted");
            }
        });
        check("headless local rejects desktop UI and arbitrary native methods without starting a service",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                foreach(string operation in new[]{"open_in_codex","navigate_to_codex_page","mcpServer/tool/call","turn/start"})Reject(()=>f.Call(operation,new{}));Assert(f.Calls.Count==0,"headless adapter exposed unapproved methods");
            }
        });
        check("headless local uncertain first turn stays uncertain and is never resent",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.ThrowTurn=true;Reject(()=>f.Create());Assert(f.Receipt().State=="uncertain"&&f.Count("turn/start")==1,"lost native response was not retained uncertain");
                Reject(()=>f.Call("send_message_to_thread",new{threadId=f.ThreadId,prompt=HeadlessTransportFixture.Secret}));Assert(f.Count("turn/start")==1,"uncertain native mission was repeated");
            }
        });
        check("headless local verifies account, folder and permission ownership on every existing receipt",()=>{
            foreach(string changed in new[]{"home","workspace","sandbox","approval","channel"})using(var f=new HeadlessTransportFixture(root,++number,auth)){
                var own=f.Own();if(changed=="home")own.Home=Path.GetDirectoryName(own.Home);else if(changed=="workspace")own.Workspace=Path.GetDirectoryName(own.Workspace);else if(changed=="sandbox")own.Sandbox="danger-full-access";else if(changed=="approval")own.ApprovalPolicy="never";else own.ChannelId="other";f.SaveReceipt(own);
                Reject(()=>f.Call("read_thread",new{threadId=f.ThreadId}));Assert(f.Connections.Count==0,"mismatched owned receipt reached a server");
            }
        });
        check("headless local opt-out during native thread preparation prevents the first mandate",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Hook=method=>{if(method=="thread/start"){f.Instance.HeadlessEnabled=false;f.Accounts.Save();}};
                Reject(()=>f.Create());Assert(f.Count("turn/start")==0,"instance opt-out during native preparation was ignored");
            }
        });
        check("headless local channel revocation during preparation is read from the persisted configuration",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Hook=method=>{if(method=="thread/start")f.Store.SetEnabled(f.Channel.Id,false);};Reject(()=>f.Create());Assert(f.Count("turn/start")==0,"stale channel snapshot bypassed a persisted revocation");
            }
        });
        check("headless local revocation closes only its owned runtime and retains an uncertain execution receipt",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();f.Store.SetEnabled(f.Channel.Id,false);Reject(()=>f.Call("read_thread",new{threadId=f.ThreadId}));
                Assert(f.Connections.Count==1&&f.Connections[0].Disposed&&f.Receipt().State=="uncertain","revocation retained its owned runtime or invented a completed result");
                Assert(f.Count("turn/start")==1&&f.Count("turn/interrupt")==0&&!f.Store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(f.Channel.AccountKey)).Running,"revocation resent the work or claimed a native interruption");
            }
        });
        check("headless local interruption is bound to the exact native turn and confirmed only by a subsequent read",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();Reject(()=>f.Call("interrupt_thread",new{threadId=f.ThreadId,turnId="wrong-turn"}));Assert(f.Count("turn/interrupt")==0,"different native turn interrupted");
                f.Call("interrupt_thread",new{threadId=f.ThreadId,turnId=f.TurnId});f.Call("interrupt_thread",new{threadId=f.ThreadId,turnId=f.TurnId});Assert(f.Count("turn/interrupt")==1&&f.Receipt().State=="running","interruption duplicated or falsely reported complete");
                f.Status="interrupted";f.Call("read_thread",new{threadId=f.ThreadId});Assert(f.Receipt().State=="interrupted"&&f.Connections.All(c=>c.Disposed),"native interruption was not reconciled");
            }
        });
        check("headless local read rejects substituted threads and preserves final response phase",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();f.NativeThreadId=Guid.NewGuid().ToString();Reject(()=>f.Call("read_thread",new{threadId=f.ThreadId}));f.NativeThreadId=null;f.Status="completed";
                var read=f.Call("read_thread",new{threadId=f.ThreadId});var item=RelayEngine.Rows(Json.Get(RelayEngine.Rows(Json.Get(read,"turns")).Single(),"items")).Single();Assert(Json.Str(Json.Get(item,"phase"))=="final_answer","verified final text was not adapted to the relay");
            }
        });
        check("headless local lost runtime is reconciled without automatically restarting its running mission",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own("running");Reject(()=>f.Call("read_thread",new{threadId=f.ThreadId}));Assert(f.Count("turn/start")==0&&f.Count("thread/resume")==0,"lost execution was silently resumed or resent");
                Assert(f.Receipt().State=="uncertain","lost native runtime did not require explicit reconciliation");f.Status="completed";var read=f.Call("read_thread",new{threadId=f.ThreadId});Assert(f.Receipt().State=="completed"&&Json.Get(read,"thread")!=null&&f.Count("turn/start")==0,"native completion could not be recovered read-only");
            }
        });
        check("headless worker pump retains a running service and releases it after native completion without resubmission",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();Assert(HeadlessRelayTransport.HasOwnedWork(f.Store),"running local work did not keep its owning worker alive");
                HeadlessRelayTransport.PumpOwned(f.Store,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Receipt().State=="running"&&!f.Connections[0].Disposed&&f.Count("thread/read")==1&&f.Count("turn/start")==1,"worker polling replayed the mission or stopped a live turn");
                f.Status="completed";HeadlessRelayTransport.PumpOwned(f.Store,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Receipt().State=="completed"&&f.Connections[0].Disposed&&!HeadlessRelayTransport.HasOwnedWork(f.Store),"completed local work retained its inference service");
            }
        });
        check("headless worker pump cancellation never invents a completed result and native failure ends only its own service",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();using(var stop=new CancellationTokenSource()){stop.Cancel();var error=Reject(()=>HeadlessRelayTransport.PumpOwned(f.Store,stop.Token).GetAwaiter().GetResult());Assert(error is OperationCanceledException&&f.Count("thread/read")==0&&f.Receipt().State=="running"&&HeadlessRelayTransport.HasOwnedWork(f.Store),"poll cancellation changed the native execution outcome");}
                f.Status="failed";HeadlessRelayTransport.PumpOwned(f.Store,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Receipt().State=="failed"&&!HeadlessRelayTransport.HasOwnedWork(f.Store)&&f.Count("turn/start")==1&&f.Count("turn/interrupt")==0,"native failure became a false success or automatic retry");
            }
        });
        check("headless lost runtime keeps the safe native method and exit diagnostic while refusing automatic replay",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Create();f.Hook=method=>{if(method=="thread/read")throw new HeadlessRpcException("transport","thread/read",true,"process-exit-0");};
                var lost=Reject(()=>f.Call("read_thread",new{threadId=f.ThreadId})) as HeadlessRuntimeLostException;
                Assert(lost!=null&&lost.Code=="transport"&&lost.Method=="thread/read"&&lost.Diagnostic=="process-exit-0"&&lost.InnerException is HeadlessRpcException,"safe native failure details were discarded");
                var state=f.Store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(f.Channel.AccountKey));Assert(f.Receipt().State=="uncertain"&&f.Connections.All(value=>value.Disposed)&&state.Diagnostic.Contains("process-exit-0")&&!state.Diagnostic.Contains(HeadlessTransportFixture.Secret)&&f.Count("turn/start")==1,"lost service was replayed or its redacted diagnostic was not persisted");
            }
        });
    }
}
