using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

// Isolated fictional accounts and fake native RPC: no windows or model turns.
internal static class PortalNativeHeadlessTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static object Shape(object value){return Json.Read<object>(Json.Write(value));}
    private static string Value(object value,string key){return Json.Str(Json.Get(value,key));}
    private static void Invalid(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected safe headless rejection");}
    private static string Encode(object value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static string Auth(string account){return Json.Write(new{auth_mode="chatgpt",tokens=new{access_token="fictional-access",refresh_token="fictional-refresh",account_id=account,id_token="e30."+Encode(new Dictionary<string,object>{{"sub","fictional-user"},{"email","headless@example.invalid"},{"https://api.openai.com/auth",new{chatgpt_account_id=account,chatgpt_plan_type="plus"}}})+".fixture"}});}
    private sealed class Rpc : IHeadlessRpc
    {
        internal readonly Fixture Fixture;internal readonly List<string> Methods=new List<string>();internal bool WrongThread,WrongWorkspace,MissingWorkspace;internal Action DuringRead;
        internal Rpc(Fixture fixture){Fixture=fixture;}
        public Func<string,object,CancellationToken,Task<object>> RequestHandler{get;set;}
        public int ProcessId{get{return 0;}}public long ProcessStartedTicks{get{return 0;}}
        public event Action<string,object> Notification{add{}remove{}}
        public event Action<HeadlessInteractionException> InteractionRequired{add{}remove{}}
        public Task<object> Call(string method,object arguments,CancellationToken token)
        {
            Methods.Add(method);Assert(method=="thread/read","Metadata attempted a model turn or resume");if(DuringRead!=null)DuringRead();
            return Task.FromResult(Shape(new{thread=new{id=WrongThread?Guid.NewGuid().ToString("D"):Fixture.Thread,cwd=MissingWorkspace?null:WrongWorkspace?Fixture.OtherWorkspace:Fixture.Grant.Workspace,status=new{type="idle"},turns=new object[0]}}));
        }
        public void Dispose(){}
    }
    private sealed class Fixture
    {
        internal readonly string Root,Folder,OtherWorkspace,Thread="01900000-0000-7000-8000-"+Guid.NewGuid().ToString("N").Substring(0,12);
        internal readonly RelayStore Store;internal readonly PortalInstanceGrant Grant;internal readonly PortalNative Native;internal readonly Rpc Rpc;
        internal InstanceState State=new InstanceState{Phase="stopped"};internal int Factories,Preparations,DesktopReads;internal RelayChannel Channel;
        internal Fixture(string sandbox)
        {
            Root=Path.Combine(sandbox,"pnh-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(Root);string local=Path.Combine(Root,"local"),exe=Path.Combine(Root,"codex.exe");Directory.CreateDirectory(local);File.WriteAllText(exe,"fictional executable never started");new Vault(Root).SaveSettings(new Settings{CodexHome=local,CodexExecutable=exe});
            string id=Guid.NewGuid().ToString("N"),auth=Auth("fixture-"+id),home=InstancePaths.Home(Root,id);Folder=InstancePaths.Folder(Root,id);Directory.CreateDirectory(home);string workspace=Path.Combine(Folder,"workspace");Directory.CreateDirectory(workspace);OtherWorkspace=Path.Combine(Root,"other-workspace");Directory.CreateDirectory(OtherWorkspace);File.WriteAllText(Path.Combine(home,"auth.json"),auth);
            var accounts=new AccountService(Root);var identity=AuthIdentity.Parse(auth);accounts.Data.Profiles.Add(new Profile{Key=identity.Key,Email=identity.Email,AuthJson=auth,Label="Fictional account",AllInstances=false,InstanceIds=new List<string>{id}});accounts.Data.Instances.Add(new DesktopInstance{Id=id,Name="Fictional headless",AccountKey=identity.Key,AccountLocked=true});accounts.Save();Store=new RelayStore(Path.Combine(Root,"relay"));
            Grant=new PortalInstanceGrant{InstanceId=id,AccountKey=identity.Key,Home=home,Workspace=workspace,Access="headless",PermissionProfile="inherit"};Rpc=new Rpc(this);
            Native=new PortalNative(Store,new PortalNativeDependencies{Runtime=(a,i)=>State,DesktopRead=(store,a,i,pin,thread,token)=>{DesktopReads++;Assert(pin.DesktopPid==State.DesktopPid&&pin.DesktopStartTicks==State.DesktopStartTicks,"Desktop metadata lost its native PID pin");return Rpc.Call("thread/read",new{threadId=thread,includeTurns=false},token).GetAwaiter().GetResult();},Headless=store=>{Factories++;return new HeadlessRelayTransport(store,c=>Rpc,(c,token)=>{
                Preparations++;using(new InstanceLease(Folder)){bool blocked=false;Task.Run(()=>{try{using(new InstanceLease(Folder)){} }catch(InvalidOperationException){blocked=true;}}).GetAwaiter().GetResult();Assert(blocked,"Metadata lease did not exclude a GUI launcher on another thread");}
            });}});
        }
        internal void Register()
        {
            var accounts=new AccountService(Root);var instance=accounts.Data.Instances.Single(value=>value.Id==Grant.InstanceId);
            Channel=new RelayChannel{Id=NamedInstances.HeadlessChannelId(Grant.Home,Grant.Workspace),Provider="codex-headless",InstanceId=Grant.InstanceId,AccountKey=Grant.AccountKey,Home=Grant.Home,Workspace=Grant.Workspace,Enabled=true,Executable=accounts.Settings.CodexExecutable,HeadlessSandbox=instance.HeadlessSandbox,HeadlessApprovalPolicy=instance.HeadlessApprovalPolicy};Store.Register(Channel);
        }
        internal HeadlessThreadReceipt Receipt(string thread=null)
        {var receipt=new HeadlessThreadReceipt{ThreadId=thread??Thread,ChannelId=Channel.Id,AccountKey=Grant.AccountKey,Home=Grant.Home,Workspace=Grant.Workspace,Sandbox=Channel.HeadlessSandbox,ApprovalPolicy=Channel.HeadlessApprovalPolicy,Title="Owned fictional chat",State="completed",UpdatedUtc=DateTime.UtcNow.ToString("o")};Save(receipt);return receipt;}
        internal void Save(HeadlessThreadReceipt receipt){Store.WriteRecord(HeadlessRelayTransport.ReceiptName(receipt.ThreadId),receipt);}
        internal void OpenDesktop(){State=new InstanceState{Running=true,Phase="running",LaunchId=Guid.NewGuid().ToString("N"),HostPid=101,HostStartTicks=1001,DesktopPid=102,DesktopStartTicks=1002};}
    }
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("portal native closed headless instance is available without desktop or RPC",()=>{
            var f=new Fixture(sandbox);f.Native.Verify(f.Grant);var view=Shape(f.Native.Describe(f.Grant));Assert(Object.Equals(Json.Get(view,"online"),true)&&Object.Equals(Json.Get(view,"desktopOpen"),false)&&Value(view,"executionMode")=="headless"&&Value(view,"account")=="headless@example.invalid","Ready headless instance was reported unavailable");Assert(f.Factories==0&&f.Store.Channels().Count==0,"Describe created a native connection");
        });
        check("portal native empty headless history needs neither Anchor nor engine",()=>{
            var f=new Fixture(sandbox);var list=f.Native.List(f.Grant);Assert(!RelayEngine.Rows(Json.Get(list,"threads")).Any()&&!RelayEngine.Rows(Json.Get(list,"pinnedThreads")).Any()&&f.Factories==0&&f.Store.Channels().Count==0,"Empty list opened a desktop or registered a channel");f.Register();Assert(!RelayEngine.Rows(Json.Get(f.Native.List(f.Grant),"threads")).Any()&&f.Factories==0,"Empty registered headless list opened an engine");
        });
        check("portal native headless list only exposes receipts owned by exact grant",()=>{
            var f=new Fixture(sandbox);f.Register();f.Receipt();foreach(string scope in new[]{"account","home","workspace","channel","permissions"}){var foreign=f.Receipt(Guid.NewGuid().ToString("D"));foreign.Title="FOREIGN "+scope;if(scope=="account")foreign.AccountKey="foreign";if(scope=="home")foreign.Home=Path.Combine(f.Root,"foreign-home");if(scope=="workspace")foreign.Workspace=f.OtherWorkspace;if(scope=="channel")foreign.ChannelId="foreign";if(scope=="permissions")foreign.Sandbox="danger-full-access";f.Save(foreign);}
            var list=f.Native.List(f.Grant);var rows=RelayEngine.Rows(Json.Get(list,"threads")).ToArray();Assert(rows.Length==1&&Value(rows[0],"threadId")==f.Thread&&!Json.Write(list).Contains("FOREIGN")&&f.Factories==0,"Headless list leaked foreign receipts or opened an engine");
        });
        check("portal native headless read uses only thread/read with reentrant preparation lease",()=>{
            var f=new Fixture(sandbox);f.Register();f.Receipt();var view=f.Native.Read(f.Grant,f.Thread);DirectMessages.RequireThread(view,f.Thread);Assert(f.Factories==1&&f.Preparations==1&&f.Rpc.Methods.SequenceEqual(new[]{"thread/read"}),"Read did not preserve metadata-only transport and preparation");Assert(!InstanceLease.Busy(f.Folder),"Metadata retained its instance lease");
        });
        check("portal native headless read rejects all foreign receipts before transport",()=>{
            foreach(string scope in new[]{"missing","account","home","workspace","channel","permissions"}){var f=new Fixture(sandbox);f.Register();var receipt=f.Receipt();if(scope=="missing")receipt.ThreadId=Guid.NewGuid().ToString("D");if(scope=="account")receipt.AccountKey="foreign";if(scope=="home")receipt.Home=Path.Combine(f.Root,"foreign-home");if(scope=="workspace")receipt.Workspace=f.OtherWorkspace;if(scope=="channel")receipt.ChannelId="foreign";if(scope=="permissions")receipt.ApprovalPolicy="never";if(scope!="missing")f.Save(receipt);string thread=scope=="missing"?receipt.ThreadId:f.Thread;Invalid(()=>f.Native.Read(f.Grant,thread));Assert(f.Factories==0,"Foreign receipt reached the engine");}
        });
        check("portal native headless GUI open unknown or disabled never starts second engine",()=>{
            foreach(string state in new[]{"open","unknown","starting","disabled"}){var f=new Fixture(sandbox);f.Register();f.Receipt();if(state=="disabled"){var a=new AccountService(f.Root);a.Data.Instances.Single(i=>i.Id==f.Grant.InstanceId).HeadlessEnabled=false;a.Save();}else f.State=new InstanceState{Running=state=="open",Phase=state=="open"?"running":state};var view=Shape(f.Native.Describe(f.Grant));Assert(!Object.Equals(Json.Get(view,"online"),true)&&Value(view,"executionMode")=="unavailable","Unsafe headless instance reported ready");Invalid(()=>f.Native.List(f.Grant));Invalid(()=>f.Native.Read(f.Grant,f.Thread));Assert(f.Factories==0,"Unsafe desktop state opened a second engine");}
        });
        check("portal native GUI metadata lists owned receipts while headless send remains unavailable",()=>{
            var f=new Fixture(sandbox);f.Register();f.Receipt();var foreign=f.Receipt(Guid.NewGuid().ToString("D"));foreign.Home=f.OtherWorkspace;foreign.Title="FOREIGN GUI CHAT";f.Save(foreign);f.OpenDesktop();
            f.Native.Verify(f.Grant);var list=f.Native.List(f.Grant);var rows=RelayEngine.Rows(Json.Get(list,"threads")).ToArray();var view=Shape(f.Native.Describe(f.Grant));
            Assert(rows.Length==1&&Value(rows[0],"threadId")==f.Thread&&!Json.Write(list).Contains("FOREIGN"),"GUI metadata exposed a thread outside the owned headless receipts");
            Assert(!Object.Equals(Json.Get(view,"online"),true)&&!Object.Equals(Json.Get(view,"headlessAvailable"),true)&&Object.Equals(Json.Get(view,"desktopOpen"),true),"Metadata access silently enabled desktop sends");
            Invalid(()=>f.Native.VerifySend(f.Grant));Assert(f.Factories==0&&f.DesktopReads==0,"Listing opened another engine or called native list_threads");
        });
        check("portal native GUI owned read avoids profile lease and uses only native read",()=>{
            var f=new Fixture(sandbox);f.Register();f.Receipt();f.OpenDesktop();
            using(var entered=new ManualResetEventSlim(false))using(var release=new ManualResetEventSlim(false)){
                var held=Task.Run(()=>{using(new InstanceLease(f.Folder)){entered.Set();release.Wait();}});Assert(entered.Wait(TimeSpan.FromSeconds(20)),"Fictional GUI lease was not acquired");
                try{DirectMessages.RequireThread(f.Native.Read(f.Grant,f.Thread),f.Thread);Assert(RelayEngine.Rows(Json.Get(f.Native.List(f.Grant),"threads")).Count()==1,"Owned GUI metadata was blocked by its launcher lease");}
                finally{release.Set();held.GetAwaiter().GetResult();}
            }
            Assert(f.DesktopReads==1&&f.Factories==0&&f.Preparations==0&&f.Rpc.Methods.SequenceEqual(new[]{"thread/read"}),"GUI metadata resumed a thread or opened a headless app-server");
        });
        check("portal native GUI metadata rejects foreign receipts before touching desktop",()=>{
            foreach(string scope in new[]{"account","home","workspace","channel","permissions"}){var f=new Fixture(sandbox);f.Register();var receipt=f.Receipt();f.OpenDesktop();if(scope=="account")receipt.AccountKey="foreign";if(scope=="home")receipt.Home=f.OtherWorkspace;if(scope=="workspace")receipt.Workspace=f.OtherWorkspace;if(scope=="channel")receipt.ChannelId="foreign";if(scope=="permissions")receipt.ApprovalPolicy="never";f.Save(receipt);Invalid(()=>f.Native.Read(f.Grant,f.Thread));Assert(f.Factories==0&&f.DesktopReads==0,"Foreign receipt reached a native desktop");}
        });
        check("portal native GUI metadata revalidates PID launch account channel and workspace after read",()=>{
            foreach(string mismatch in new[]{"pid","start","host","launch","closed","account","channel","workspace","thread"}){
                var f=new Fixture(sandbox);f.Register();f.Receipt();f.OpenDesktop();f.Rpc.WrongWorkspace=mismatch=="workspace";f.Rpc.WrongThread=mismatch=="thread";
                f.Rpc.DuringRead=()=>{if(mismatch=="pid")f.State.DesktopPid++;if(mismatch=="start")f.State.DesktopStartTicks++;if(mismatch=="host")f.State.HostStartTicks++;if(mismatch=="launch")f.State.LaunchId=Guid.NewGuid().ToString("N");if(mismatch=="closed")f.State=new InstanceState{Phase="stopped"};if(mismatch=="account")File.WriteAllText(Path.Combine(f.Grant.Home,"auth.json"),Auth("foreign"));if(mismatch=="channel")f.Store.SetEnabled(f.Channel.Id,false);};
                Invalid(()=>f.Native.Read(f.Grant,f.Thread));Assert(f.DesktopReads==1&&f.Factories==0&&f.Rpc.Methods.SequenceEqual(new[]{"thread/read"}),"Changed owner caused a fallback or replay");
            }
        });
        check("portal native metadata receipt scan bounds before opening encrypted records",()=>{
            int observed=0;IEnumerable<string> paths=Enumerable.Range(0,100000).Select(i=>{observed++;return "never-opened-"+i;});Invalid(()=>PortalNative.BoundedReceiptPaths(paths));Assert(observed==4097,"Receipt discovery enumerated an unbounded directory");Assert(PortalNative.BoundedReceiptPaths(Enumerable.Repeat("fictional",4096)).Length==4096,"Valid metadata boundary was rejected");
        });
        check("portal native headless account and channel changes fail before native RPC",()=>{
            foreach(string scope in new[]{"auth","grant-home","grant-workspace","channel-disabled","channel-home","channel-workspace","channel-policy"}){var f=new Fixture(sandbox);f.Register();f.Receipt();if(scope=="auth")File.WriteAllText(Path.Combine(f.Grant.Home,"auth.json"),Auth("foreign"));if(scope=="grant-home")f.Grant.Home=f.OtherWorkspace;if(scope=="grant-workspace")f.Grant.Workspace=Path.Combine(f.Root,"absent");if(scope=="channel-disabled")f.Store.SetEnabled(f.Channel.Id,false);if(scope=="channel-home"||scope=="channel-workspace"||scope=="channel-policy"){var channel=f.Store.Channel(f.Channel.Id);if(scope=="channel-home")channel.Home=f.OtherWorkspace;if(scope=="channel-workspace")channel.Workspace=f.OtherWorkspace;if(scope=="channel-policy")channel.HeadlessSandbox="read-only";f.Store.WriteRecord("channels.dpapi",new[]{channel});}Invalid(()=>f.Native.Read(f.Grant,f.Thread));Assert(f.Factories==0,"Changed binding reached native RPC");}
        });
        check("portal native headless native thread cwd and post-read GUI checks fail closed",()=>{
            foreach(string mismatch in new[]{"thread","workspace","missing-cwd","gui"}){var f=new Fixture(sandbox);f.Register();f.Receipt();f.Rpc.WrongThread=mismatch=="thread";f.Rpc.WrongWorkspace=mismatch=="workspace";f.Rpc.MissingWorkspace=mismatch=="missing-cwd";if(mismatch=="gui")f.Rpc.DuringRead=()=>f.State=new InstanceState{Running=true,Phase="running"};Invalid(()=>f.Native.Read(f.Grant,f.Thread));Assert(f.Rpc.Methods.SequenceEqual(new[]{"thread/read"}),"Mismatched read caused a resume or model turn");}
        });
        check("portal native desktop description retains desktop access mode",()=>{
            var f=new Fixture(sandbox);f.Grant.Access="desktop";f.State=new InstanceState{Running=true,Phase="running"};var view=Shape(f.Native.Describe(f.Grant));Assert(Value(view,"executionMode")=="desktop"&&Object.Equals(Json.Get(view,"desktopOpen"),true)&&f.Factories==0,"Desktop metadata semantics changed");
        });
    }
}
