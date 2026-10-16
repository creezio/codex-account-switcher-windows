using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class HeadlessRuntimeTests
{
    private const string Secret="PRIVATE_HEADLESS_ERROR_NOT_FOR_LOGS";
    private static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected headless refusal");}
    private static string Encode(object value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static AuthIdentity Identity(int expires=3600,string revision="old")
    {return new AuthIdentity{Key="fixture-account-key",AccountId="fixture-account",UserId="fixture-user",Email="fixture@example.test",AccessToken="e30."+Encode(new Dictionary<string,object>{{"exp",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+expires},{"https://api.openai.com/auth",new{chatgpt_account_id="fixture-account",chatgpt_user_id="fixture-user"}},{"revision",revision}})+".fixture"};}
    private sealed class Wire:IHeadlessWire,IHeadlessWireDiagnostic
    {
        public event Action<string> Message;
        public event Action Closed;
        private readonly object sync=new object();
        private readonly List<object> sent=new List<object>();
        private bool serverReplied;
        internal List<object> Sent{get{lock(sync)return new List<object>(sent);}}
        internal string Email="fixture@example.test",ErrorMethod,HoldMethod,InteractionMethod,CloseMethod,MalformedMethod,EmptyMethod;
        internal bool NullResult;
        public string FailureCode{get;private set;}
        internal volatile bool Disposed;
        internal int DisposeCalls;
        internal Action<string> Hook;
        private object waiting;
        public void Send(string frame){
            var value=Json.Read<object>(frame);string method=Json.Str(Json.Get(value,"method"));object responseTo=null;
            lock(sync){sent.Add(value);if(method.Length==0){responseTo=waiting;waiting=null;serverReplied=true;Monitor.PulseAll(sync);}}
            if(method.Length==0){if(responseTo!=null)Reply(Json.Get(responseTo,"id"),new{ok=true});return;}
            if(method=="initialized")return;
            if(Hook!=null)Hook(method);
            if(method==CloseMethod){FailureCode="process-exit-7";var close=Closed;if(close!=null)close();return;}
            if(method==MalformedMethod){var raw=Message;if(raw!=null)raw("{"+Secret);return;}
            if(method==EmptyMethod){Emit(new{method="thread/name/updated",@params=new{threadId="thread-fixture",threadName="Fixture"}});Reply(Json.Get(value,"id"),NullResult?null:new object());return;}
            if(method==HoldMethod)return;
            if(method==InteractionMethod){lock(sync)waiting=value;Emit(new{id="approval-fixture",method="item/commandExecution/requestApproval",@params=new{threadId="thread-fixture",command=Secret}});return;}
            if(method==ErrorMethod){Emit(new{id=Json.Get(value,"id"),error=new{code=-32001,message=Secret,data=new{token=Secret}}});return;}
            Reply(Json.Get(value,"id"),method=="account/read"?(object)new{account=new{type="chatgpt",email=Email,planType="plus"}}:new{ok=true});
        }
        private void Reply(object id,object result){Emit(new{id=id,result=result});}
        private void Emit(object value){var handler=Message;if(handler!=null)handler(Json.Write(value));}
        public void Dispose(){Interlocked.Increment(ref DisposeCalls);Disposed=true;}
        internal Action ClosedCallback{get{return Closed;}}
        internal int Count(string method){return Sent.Count(value=>Json.Str(Json.Get(value,"method"))==method);}
        internal bool WaitForServerReply(int milliseconds){
            var deadline=DateTime.UtcNow.AddMilliseconds(milliseconds);
            lock(sync){while(!serverReplied){int remaining=(int)Math.Ceiling((deadline-DateTime.UtcNow).TotalMilliseconds);if(remaining<=0)return false;Monitor.Wait(sync,remaining);}return true;}
        }
    }
    private sealed class Lease:IDisposable{internal Action Release;public void Dispose(){if(Release!=null)Release();}}
    private sealed class Refresh:IHeadlessRpc
    {
        internal Action Renew;internal bool Disposed;internal int Calls;
        public Func<string,object,CancellationToken,Task<object>> RequestHandler{get;set;}
        public int ProcessId {get{return 0;}}
        public long ProcessStartedTicks {get{return 0;}}
        public event Action<string,object> Notification{add{}remove{}}
        public event Action<HeadlessInteractionException> InteractionRequired{add{}remove{}}
        public Task<object> Call(string method,object arguments,CancellationToken token){Calls++;Assert(method=="account/read"&&Object.Equals(Json.Get(Json.Read<object>(Json.Write(arguments)),"refreshToken"),true),"refresh used wrong native operation");token.ThrowIfCancellationRequested();Renew();return Task.FromResult<object>(new{});}
        public void Dispose(){Disposed=true;}
    }
    private sealed class HttpWire:HttpMessageHandler
    {
        internal int Calls;
        internal bool AlwaysUnauthorized;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return Task.FromResult(new HttpResponseMessage(Calls==1||AlwaysUnauthorized?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=new StringContent("{\"data\":[],\"next_cursor\":null}",Encoding.UTF8,"application/json")});}
    }
    private sealed class CloudWire:ICloudTasksTransport
    {
        internal int Connects,FailConnections=1,Messages;
        private string result;
        public bool IsOpen{get;private set;}
        public Task Connect(string access,string account,CancellationToken token){Connects++;if(Connects<=FailConnections)throw new CloudBackendException("connection-authentication","Fixture refusal");IsOpen=true;return Task.FromResult(0);}
        public Task Send(string text,CancellationToken token){Messages++;var value=Json.Read<object>(text);result=Json.Write(new{id=Json.Get(value,"id"),result=new{userAgent="fixture"}});return Task.FromResult(0);}
        public Task<string> Receive(CancellationToken token){return Task.FromResult(result);}
        public void Abort(){IsOpen=false;}
        public void Dispose(){Abort();}
    }
    internal static void RunAll(Action<string,Action> check,string root,string auth)
    {
        int number=0;
        Func<string> lockPath=()=>Path.Combine(root,"headless-runtime-"+(++number)+".lock");
        check("headless isolates inherited identity and desktop bridge variables",()=>{
            string prior=Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");Environment.SetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH",Secret);
            try{var info=HeadlessProcessWire.StartInfo("codex.exe",root);Assert(!info.EnvironmentVariables.ContainsKey("CODEX_APP_TOOLS_PIPE_PATH")&&!info.EnvironmentVariables.ContainsKey("CODEX_THREAD_ID")&&!info.EnvironmentVariables.ContainsKey("OPENAI_API_KEY"),"foreign identity inherited");Assert(info.EnvironmentVariables["CODEX_HOME"]==root&&info.EnvironmentVariables["CODEX_SQLITE_HOME"]==root&&!info.UseShellExecute&&info.CreateNoWindow,"profile/process not isolated");}finally{Environment.SetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH",prior);}
        });
        check("headless writes native JSON frames as UTF8 without BOM regardless of console encoding",()=>{
            string frame="{\"name\":\"fenêtre — français 日本語 😀\"}";
            using(var bytes=new MemoryStream())using(var input=HeadlessProcessWire.Utf8Input(bytes)){
                input.WriteLine(frame);var encoded=bytes.ToArray();
                Assert(encoded.SequenceEqual(new UTF8Encoding(false,true).GetBytes(frame+Environment.NewLine)),"native input used the Windows OEM code page or emitted a BOM");
                Assert(new UTF8Encoding(false,true).GetString(encoded)==frame+Environment.NewLine,"native JSON changed non-ASCII text");
            }
        });
        check("headless engines cannot share a managed profile and acquiring the lease can be cancelled",()=>{
            using(HeadlessProcessWire.AcquireProfileLease(root,CancellationToken.None))using(var cancel=new CancellationTokenSource()){
                cancel.CancelAfter(30);Assert(Reject(()=>HeadlessProcessWire.AcquireProfileLease(root,cancel.Token)) is OperationCanceledException,"profile lease ignored cancellation");
                var busy=Reject(()=>HeadlessProcessWire.AcquireProfileLease(root,CancellationToken.None)) as HeadlessRpcException;Assert(busy!=null&&busy.Code=="engine-busy","busy engine looked like revoked authentication");
            }
            using(HeadlessProcessWire.AcquireProfileLease(root,CancellationToken.None)){}
        });
        check("headless initializes once and checks the profile and server before business calls",()=>{
            var wire=new Wire();var current=Identity();using(var rpc=new HeadlessRpc(wire,()=>current,current.Key)){
                rpc.Call("thread/read",new{},CancellationToken.None).GetAwaiter().GetResult();rpc.Call("thread/list",new{},CancellationToken.None).GetAwaiter().GetResult();
                Assert(wire.Count("initialize")==1&&wire.Count("initialized")==1&&wire.Count("account/read")==2,"initialization/account checks missing");
                Assert(!Json.Write(wire.Sent).Contains(current.AccessToken),"bearer entered RPC bodies");
            }Assert(wire.Disposed,"helper survived disposal");
        });
        check("headless rejects a foreign server account before business dispatch",()=>{
            var wire=new Wire{Email="foreign@example.test"};var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){Reject(()=>rpc.Call("turn/start",new{},CancellationToken.None).GetAwaiter().GetResult());Assert(wire.Count("turn/start")==0,"foreign account received work");}
        });
        check("headless detects profile rotation after mutation and never replays",()=>{
            var wire=new Wire();var identity=Identity();wire.Hook=method=>{if(method=="turn/start")identity=new AuthIdentity{Key="foreign",AccountId="other"};};using(var rpc=new HeadlessRpc(wire,()=>identity,"fixture-account-key")){
                var error=Reject(()=>rpc.Call("turn/start",new{},CancellationToken.None).GetAwaiter().GetResult()) as HeadlessRpcException;
                Assert(error!=null&&error.RequestMayHaveBeenSent&&wire.Count("turn/start")==1,"post-send rotation replayed or not uncertain");
            }
        });
        check("headless redacts server error text and metadata",()=>{
            var wire=new Wire{ErrorMethod="thread/read"};var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){var error=Reject(()=>rpc.Call("thread/read",new{},CancellationToken.None).GetAwaiter().GetResult()) as HeadlessRpcException;Assert(error!=null&&error.Method=="thread/read"&&!error.ToString().Contains(Secret),"raw server error exposed or method lost");}
        });
        check("headless cancellation kills its helper and does not repeat a sent turn",()=>{
            var wire=new Wire{HoldMethod="turn/start"};var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key))using(var cancel=new CancellationTokenSource()){
                cancel.CancelAfter(30);var error=Reject(()=>rpc.Call("turn/start",new{},cancel.Token).GetAwaiter().GetResult()) as HeadlessRpcException;
                Assert(error!=null&&error.RequestMayHaveBeenSent&&wire.Disposed&&wire.Count("turn/start")==1,"cancelled mutation duplicated or helper leaked");
            }
        });
        check("headless process loss after a mutation is uncertain and disposes its helper",()=>{
            var wire=new Wire{CloseMethod="turn/start"};var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){
                var error=Reject(()=>rpc.Call("turn/start",new{},CancellationToken.None).GetAwaiter().GetResult()) as HeadlessRpcException;
                Assert(error!=null&&error.Method=="turn/start"&&error.Diagnostic=="process-exit-7"&&error.RequestMayHaveBeenSent&&wire.Disposed&&wire.Count("turn/start")==1,"disconnected mutation replayed, diagnostic lost or helper survived");
            }
        });
        check("headless protocol failures keep a safe category and method without retaining the native frame",()=>{
            var wire=new Wire{MalformedMethod="turn/start"};var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){
                var error=Reject(()=>rpc.Call("turn/start",new{},CancellationToken.None).GetAwaiter().GetResult()) as HeadlessRpcException;
                Assert(error!=null&&error.Code=="uncertain"&&error.Method=="turn/start"&&error.Diagnostic.StartsWith("protocol-",StringComparison.Ordinal)&&!error.ToString().Contains(Secret)&&!error.Diagnostic.Contains(Secret)&&wire.Count("turn/start")==1,"protocol diagnostic leaked native data or replayed the mutation");
            }
        });
        check("headless accepts empty and null naming results alongside native naming notifications",()=>{
            foreach(bool empty in new[]{false,true}){var wire=new Wire{EmptyMethod="thread/name/set",NullResult=empty};var identity=Identity();int notices=0;using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){
                rpc.Notification+=(method,args)=>{if(method=="thread/name/updated")notices++;};rpc.Call("thread/name/set",new{threadId="thread-fixture",name="Fixture"},CancellationToken.None).GetAwaiter().GetResult();Assert(wire.Count("thread/name/set")==1&&notices==1,"empty naming result or native notification rejected");
            }}
        });
        check("headless releases its helper once when callers and close notifications race",()=>{
            for(int repeat=0;repeat<40;repeat++){
                var wire=new Wire();var identity=Identity();var rpc=new HeadlessRpc(wire,()=>identity,identity.Key);var close=wire.ClosedCallback;
                using(var start=new ManualResetEventSlim()){
                    var tasks=Enumerable.Range(0,8).Select(index=>Task.Run(()=>{start.Wait();if(index%2==0)rpc.Dispose();else close();})).ToArray();
                    start.Set();Task.WaitAll(tasks);
                }
                rpc.Dispose();Assert(wire.Disposed&&wire.DisposeCalls==1,"concurrent close released its helper more than once");
            }
        });
        check("headless background refuses native approval without leaking command and can still read",()=>{
            var wire=new Wire{InteractionMethod="turn/start"};var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){
                var error=Reject(()=>rpc.Call("turn/start",new{},CancellationToken.None).GetAwaiter().GetResult()) as HeadlessInteractionException;
                Assert(error!=null&&error.RequestId=="approval-fixture"&&error.ThreadId=="thread-fixture"&&!error.ToString().Contains(Secret),"interaction was not safely surfaced");
                Assert(wire.WaitForServerReply(3000),"background refusal response was not sent");
                Assert(wire.Sent.Any(value=>Json.Str(Json.Get(value,"id"))=="approval-fixture"&&Json.Get(value,"error")!=null),"background did not refuse request");
                rpc.Call("thread/read",new{},CancellationToken.None).GetAwaiter().GetResult();Reject(()=>rpc.Call("turn/start",new{},CancellationToken.None).GetAwaiter().GetResult());Assert(wire.Count("turn/start")==1,"declined work replayed");
            }
        });
        check("headless preserves the server request id for an explicit response handler",()=>{
            var wire=new Wire{InteractionMethod="turn/start"};var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){
                rpc.RequestHandler=(method,value,token)=>Task.FromResult<object>(new{decision="decline"});rpc.Call("turn/start",new{},CancellationToken.None).GetAwaiter().GetResult();
                Assert(wire.Sent.Any(value=>Json.Str(Json.Get(value,"id"))=="approval-fixture"&&Json.Str(Json.Get(Json.Get(value,"result"),"decision"))=="decline"),"native request id/decision lost");
            }
        });
        check("headless refuses uncoordinated forced authentication refresh",()=>{
            var wire=new Wire();var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){Reject(()=>rpc.Call("account/read",new{refreshToken=true},CancellationToken.None).GetAwaiter().GetResult());Assert(wire.Sent.Count==0,"uncoordinated refresh sent");}
        });
        check("headless cannot change or remove the permanent account through business RPC",()=>{
            var wire=new Wire();var identity=Identity();using(var rpc=new HeadlessRpc(wire,()=>identity,identity.Key)){foreach(var method in new[]{"account/login/start","account/login/cancel","account/logout"})Reject(()=>rpc.Call(method,new{},CancellationToken.None).GetAwaiter().GetResult());Assert(wire.Sent.Count==0,"permanent authentication mutation dispatched");}
        });
        check("headless accounts do not start a process for a valid token",()=>{
            var identity=Identity();int starts=0;var actual=HeadlessAccounts.ReadCore(lockPath(),identity.Key,()=>identity,()=>false,()=>new Lease(),()=>{starts++;return new Refresh();},false,CancellationToken.None);Assert(actual==identity&&starts==0,"valid token caused a refresh process");
        });
        check("headless accounts keep an open GUI usable while its valid token approaches expiry",()=>{
            var identity=Identity(30);int starts=0;var actual=HeadlessAccounts.ReadCore(lockPath(),identity.Key,()=>identity,()=>true,()=>new Lease(),()=>{starts++;return new Refresh();},false,CancellationToken.None);Assert(actual==identity&&starts==0,"valid GUI token was refused or rotated");
        });
        check("headless accounts never refresh an expired token in a live GUI",()=>{
            var identity=Identity(-10);int starts=0;Reject(()=>HeadlessAccounts.ReadCore(lockPath(),identity.Key,()=>identity,()=>true,()=>new Lease(),()=>{starts++;return new Refresh();},false,CancellationToken.None));Assert(starts==0,"live GUI refresh token rotated");
        });
        check("headless accounts do not force a rejected but locally unexpired token through a live GUI",()=>{
            var identity=Identity();int starts=0;Reject(()=>HeadlessAccounts.ReadCore(lockPath(),identity.Key,()=>identity,()=>true,()=>new Lease(),()=>{starts++;return new Refresh();},true,CancellationToken.None));Assert(starts==0,"forced read-auth failure rotated live GUI credentials");
        });
        check("headless accounts refresh a closed expired profile under its lease and dispose the helper",()=>{
            var identity=Identity(-1);bool held=false;var rpc=new Refresh{Renew=()=>{Assert(held,"native refresh ran without profile lease");identity=Identity(3600,"fresh");}};
            var actual=HeadlessAccounts.ReadCore(lockPath(),identity.Key,()=>identity,()=>false,()=>{held=true;return new Lease{Release=()=>held=false};},()=>rpc,false,CancellationToken.None);
            Assert(actual==identity&&rpc.Calls==1&&rpc.Disposed&&!held&&HeadlessAccounts.Remaining(actual)>0,"refresh lifecycle failed");
        });
        check("headless accounts reject a different identity after refresh without restoring old credentials",()=>{
            var identity=Identity(-1);var rpc=new Refresh{Renew=()=>identity=new AuthIdentity{Key="foreign",AccountId="foreign",AccessToken=Secret}};
            var error=Reject(()=>HeadlessAccounts.ReadCore(lockPath(),"fixture-account-key",()=>identity,()=>false,()=>new Lease(),()=>rpc,false,CancellationToken.None));Assert(identity.Key=="foreign"&&rpc.Disposed&&!error.ToString().Contains(Secret),"foreign credentials overwritten or leaked");
        });
        check("headless accounts coalesce concurrent expired-token refreshes",()=>{
            var identity=Identity(-1);int starts=0;string path=lockPath();var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();
            Func<IHeadlessRpc> connect=()=>{Interlocked.Increment(ref starts);return new Refresh{Renew=()=>{entered.Set();Assert(release.Wait(3000),"fixture release timed out");identity=Identity(3600,"rotated");}};};
            var first=Task.Run(()=>HeadlessAccounts.ReadCore(path,"fixture-account-key",()=>identity,()=>false,()=>new Lease(),connect,false,CancellationToken.None));Assert(entered.Wait(3000),"refresh did not start");
            var second=Task.Run(()=>HeadlessAccounts.ReadCore(path,"fixture-account-key",()=>identity,()=>false,()=>new Lease(),connect,false,CancellationToken.None));release.Set();Task.WaitAll(first,second);Assert(starts==1,"concurrent refresh duplicated");entered.Dispose();release.Dispose();
        });
        check("headless accounts cancel while another refresh owns the profile mutex",()=>{
            var identity=Identity(-1);string path=lockPath();using(var lease=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))using(var cancel=new CancellationTokenSource()){
                cancel.CancelAfter(30);var error=Reject(()=>HeadlessAccounts.ReadCore(path,identity.Key,()=>identity,()=>false,()=>new Lease(),()=>new Refresh(),false,cancel.Token));Assert(error is OperationCanceledException,"waiting refresh ignored cancellation");
            }
        });
        check("headless accounts serve valid tokens when the existing engine owns refresh and diagnose expired busy profiles",()=>{
            var identity=Identity(30);Func<IHeadlessRpc> busy=()=>{throw new HeadlessRpcException("engine-busy",null,false);};
            Assert(HeadlessAccounts.ReadCore(lockPath(),identity.Key,()=>identity,()=>false,()=>new Lease(),busy,false,CancellationToken.None)==identity,"busy engine blocked valid credentials");
            identity=Identity(-1);var error=Reject(()=>HeadlessAccounts.ReadCore(lockPath(),identity.Key,()=>identity,()=>false,()=>new Lease(),busy,false,CancellationToken.None)) as CloudBackendException;Assert(error!=null&&error.Code=="authentication-busy","expired busy profile was misdiagnosed");
        });
        check("cloud retries a rejected authentication read once and never retries a mutation",()=>{
            var identity=Identity();int refreshes=0;var handler=new HttpWire();using(var http=new HttpClient(handler))using(var backend=new CloudBackend(http,()=>identity,CloudBackend.Capture(identity))){
                backend.RefreshReadAuthentication=token=>{refreshes++;identity=Identity(3600,"fresh");};backend.List("user",null,CancellationToken.None).GetAwaiter().GetResult();Assert(handler.Calls==2&&refreshes==1,"safe read did not refresh once");
            }
            handler=new HttpWire{AlwaysUnauthorized=true};refreshes=0;using(var http=new HttpClient(handler))using(var backend=new CloudBackend(http,()=>identity,CloudBackend.Capture(identity))){
                backend.RefreshReadAuthentication=token=>refreshes++;Reject(()=>backend.Rename("11111111-2222-3333-4444-555555555555~asenvcfg_fixture","Allowed rename",CancellationToken.None).GetAwaiter().GetResult());Assert(handler.Calls==1&&refreshes==0,"mutation was replayed after 401");
            }
            handler=new HttpWire{AlwaysUnauthorized=true};refreshes=0;using(var http=new HttpClient(handler))using(var backend=new CloudBackend(http,()=>identity,CloudBackend.Capture(identity))){backend.RefreshReadAuthentication=token=>refreshes++;Reject(()=>backend.List("user",null,CancellationToken.None).GetAwaiter().GetResult());Assert(handler.Calls==2&&refreshes==1,"read retry was unbounded");}
        });
        check("cloud refreshes a rejected websocket handshake before any RPC at most once",()=>{
            var identity=Identity();var wire=new CloudWire();int refreshes=0;using(var backend=new CloudTasksBackend(wire,()=>identity,CloudBackend.Capture(identity),null)){
                backend.RefreshConnectionAuthentication=token=>{refreshes++;identity=Identity(3600,"fresh");};backend.Connect(CancellationToken.None).GetAwaiter().GetResult();Assert(wire.Connects==2&&refreshes==1&&wire.Messages==2,"handshake renewal sent business RPCs or repeated");
            }
            wire=new CloudWire{FailConnections=2};refreshes=0;using(var backend=new CloudTasksBackend(wire,()=>identity,CloudBackend.Capture(identity),null)){backend.RefreshConnectionAuthentication=token=>refreshes++;Reject(()=>backend.Connect(CancellationToken.None).GetAwaiter().GetResult());Assert(wire.Connects==2&&refreshes==1&&wire.Messages==0,"failed handshake initialized or retried indefinitely");}
        });
        check("cloud never reconnects with an identity changed during handshake renewal",()=>{
            var identity=Identity();var wire=new CloudWire();using(var backend=new CloudTasksBackend(wire,()=>identity,CloudBackend.Capture(identity),null)){
                backend.RefreshConnectionAuthentication=token=>identity=new AuthIdentity{Key="foreign",AccountId="foreign",AccessToken=Secret};var error=Reject(()=>backend.Connect(CancellationToken.None).GetAwaiter().GetResult());Assert(wire.Connects==1&&wire.Messages==0&&!error.ToString().Contains(Secret),"renewal reconnected another account or exposed a token");
            }
        });
        check("relay worker upgrades a legacy process cooperatively after its lease is released",()=>{
            var store=new RelayStore(Path.Combine(root,"worker-upgrade-"+(++number)));bool alive=true;int starts=0;DateTime began=DateTime.UtcNow.AddMinutes(-1);
            store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=123,Started=1,Version="0.17.2-beta.1"});store.WriteRecord("worker-stop.dpapi",new RelayWorkerState());RelayDispatch.Set(store,"paused");
            var owned=store.Lease("worker");
            try{RelayWorker.EnsureCore(store,"fixture-next",state=>alive,()=>{using(store.Lease("worker")){}starts++;},delay=>{var stop=store.ReadRecord<RelayWorkerState>("worker-stop.dpapi");DateTime updated;Assert(DateTime.TryParse(stop.Updated,out updated)&&updated.ToUniversalTime()>began&&!stop.Paused,"legacy heartbeat did not receive the cooperative stop protocol");alive=false;owned.Dispose();},5000);}
            finally{owned.Dispose();}
            Assert(starts==1&&RelayDispatch.Read(store).Mode=="paused"&&!RelayWorker.Paused(store),"upgrade changed dispatch policy or started before legacy shutdown");
        });
        check("relay worker upgrade preserves user pause and refuses a second updater",()=>{
            var store=new RelayStore(Path.Combine(root,"worker-upgrade-"+(++number)));int starts=0;store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Paused=true});
            RelayWorker.EnsureCore(store,"fixture-next",state=>true,()=>starts++,delay=>{},0);Assert(starts==0&&RelayWorker.Paused(store),"upgrade resumed a user pause");
            store.WriteRecord("worker-stop.dpapi",new RelayWorkerState());using(store.Lease("worker-update"))Reject(()=>RelayWorker.EnsureCore(store,"fixture-next",state=>false,()=>starts++,delay=>{},0));Assert(starts==0,"competing updater started another worker");
        });
        check("relay worker upgrade timeout never kills or replaces a live owner",()=>{
            var store=new RelayStore(Path.Combine(root,"worker-upgrade-"+(++number)));int starts=0;store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=123,Started=1,Version="legacy"});store.WriteRecord("worker-stop.dpapi",new RelayWorkerState());
            using(store.Lease("worker")){
                var error=Reject(()=>RelayWorker.EnsureCore(store,"fixture-next",state=>true,()=>starts++,delay=>{},0));Assert(starts==0&&error.Message.Contains("coopérative")&&store.ReadRecord<RelayWorkerState>("worker.dpapi").Pid==123,"timeout replaced or changed live worker ownership");
            }
        });
        check("relay worker upgrade observes a user pause arriving during cooperative shutdown",()=>{
            var store=new RelayStore(Path.Combine(root,"worker-upgrade-"+(++number)));int starts=0;store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=123,Started=1,Version="legacy"});store.WriteRecord("worker-stop.dpapi",new RelayWorkerState());
            RelayWorker.EnsureCore(store,"fixture-next",state=>true,()=>starts++,delay=>store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Paused=true,Updated=DateTime.UtcNow.ToString("o")}),5000);Assert(starts==0&&RelayWorker.Paused(store),"upgrade discarded a concurrent pause");
        });
        check("relay worker upgrade keeps the original process pinned after its heartbeat clears the PID",()=>{
            var store=new RelayStore(Path.Combine(root,"worker-upgrade-"+(++number)));bool alive=true;int waits=0,starts=0;store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=123,Started=1,Version="legacy"});store.WriteRecord("worker-stop.dpapi",new RelayWorkerState());
            RelayWorker.EnsureCore(store,"fixture-next",state=>state.Pid==123&&alive,()=>{Assert(!alive&&waits==2,"new worker started while the original process was still alive");starts++;},delay=>{waits++;store.WriteRecord("worker.dpapi",new RelayWorkerState());if(waits==2)alive=false;},5000);Assert(starts==1,"replacement worker not started after actual exit");
        });
        check("headless source ancestry pins creation times and refuses reused PIDs and cycles",()=>{
            var parents=new Dictionary<int,int>{{3,2},{2,1},{1,0}};var starts=new Dictionary<int,long>{{1,100},{2,200},{3,300}};
            Assert(HeadlessSourceBroker.Descends(3,1,100,pid=>parents[pid],pid=>starts[pid]),"owned descendant was refused");
            Assert(!HeadlessSourceBroker.Descends(3,1,99,pid=>parents[pid],pid=>starts[pid]),"reused root PID accepted");
            starts[2]=400;Assert(!HeadlessSourceBroker.Descends(3,1,100,pid=>parents[pid],pid=>starts[pid]),"parent created after its child accepted");
            starts[2]=200;parents[2]=3;Assert(!HeadlessSourceBroker.Descends(3,1,100,pid=>parents[pid],pid=>starts[pid]),"cyclic ancestry accepted");
        });
        check("headless worker lifecycle classification requires a live pinned runtime and profile binding",()=>{
            var runtime=new HeadlessRuntimeState{Running=true,AccountKey="fixture-account",Home=root,RuntimeId="fixture-runtime",WorkerPid=10,WorkerStarted=100,ProcessId=20,ProcessStartedTicks=200};
            Func<int,long,bool> alive=(pid,started)=>pid==10&&started==100||pid==20&&started==200;
            Assert(HeadlessSourceBroker.IsOwnedChildCore(new[]{runtime},alive,value=>true),"owned native child not classified");
            Assert(!HeadlessSourceBroker.IsOwnedChildCore(new[]{runtime},alive,value=>false),"unrelated process treated as owned");
            runtime.ProcessStartedTicks=201;Assert(!HeadlessSourceBroker.IsOwnedChildCore(new[]{runtime},alive,value=>true),"reused native PID treated as owned");
            runtime.ProcessStartedTicks=200;runtime.Running=false;Assert(!HeadlessSourceBroker.IsOwnedChildCore(new[]{runtime},alive,value=>true),"stopped runtime treated as owned");
            runtime.Running=true;runtime.AccountKey=null;Assert(!HeadlessSourceBroker.IsOwnedChildCore(new[]{runtime},alive,value=>true),"unbound runtime classified as owned");
        });
        check("headless lifecycle skips an inaccessible stale runtime before a valid native owner",()=>{
            var stale=new HeadlessRuntimeState{Running=true,AccountKey="stale-account",Home=root,RuntimeId="stale-runtime",WorkerPid=99,WorkerStarted=100,ProcessId=98,ProcessStartedTicks=200};
            var valid=new HeadlessRuntimeState{Running=true,AccountKey="fixture-account",Home=root,RuntimeId="fixture-runtime",WorkerPid=10,WorkerStarted=100,ProcessId=20,ProcessStartedTicks=200};
            Assert(HeadlessSourceBroker.IsOwnedChildCore(new[]{stale,valid},(pid,started)=>{if(pid==99)throw new System.ComponentModel.Win32Exception(5);return pid==10&&started==100||pid==20&&started==200;},value=>Object.ReferenceEquals(value,valid)),"inaccessible stale process hid the real owned native job");
        });
        check("cloud worker spawn is deferred from native job children and performed only by an outside owner",()=>{
            int starts=0,reads=0;CloudJobWorker.EnsureCore(()=>true,()=>{reads++;return false;},()=>starts++);Assert(starts==0&&reads==0,"native child spawned a worker that would die with its parent");
            CloudJobWorker.EnsureCore(()=>false,()=>true,()=>starts++);Assert(starts==0,"existing cloud worker duplicated");
            CloudJobWorker.EnsureCore(()=>false,()=>false,()=>starts++);Assert(starts==1,"outside queue owner did not start the cloud worker");
        });
        check("cloud durable backlog keeps the outside owner alive without spawning a host for finished local returns",()=>{
            var queued=new CloudJob{Id="queued",State="queued",SourceChannel="headless"};var running=new CloudJob{Id="running",State="running",SourceChannel="headless"};var localReturn=new CloudJob{Id="return",State="completed",SourceChannel="headless",ReturnState="pending"};var desktopReturn=new CloudJob{Id="desktop",State="completed",SourceChannel="desktop",ReturnState="pending"};
            var selected=CloudJobWorker.SelectRemotePending(new[]{queued,running,localReturn,desktopReturn},job=>job.SourceChannel=="headless");
            Assert(selected.Any(job=>job.Id==queued.Id)&&selected.Any(job=>job.Id==running.Id)&&selected.Any(job=>job.Id==desktopReturn.Id)&&!selected.Any(job=>job.Id==localReturn.Id),"durable remote backlog was dropped or a finished local return retained a cloud host");
            Assert(CloudJobWorker.SelectRemotePending(new[]{localReturn},job=>true).Length==0,"completed headless return spawned a cloud host instead of local return processing");
        });
    }
}
