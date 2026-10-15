using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudTasksBackendTests
{
    private const string Config="configuration_fixture~asenvcfg_ws";
    private const string Secret="PRIVATE_RPC_ERROR_MUST_NOT_BE_EXPOSED";
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected native Cloud refusal");}
    private static string Encode(object value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static AuthIdentity Identity(string account="account-a")
    {
        return new AuthIdentity{Key=account+":user-a",AccountId=account,AccessToken="e30."+Encode(new Dictionary<string,object>{{"exp",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600},{"https://api.openai.com/auth",new{chatgpt_account_id=account,chatgpt_user_id="user-a"}}})+".fixture"};
    }
    private sealed class Wire:ICloudTasksTransport
    {
        internal readonly List<object> Messages=new List<object>();
        private readonly Queue<string> replies=new Queue<string>();
        internal string Token,Account,Status="idle",Environment=Config,LatestStatus="completed",Final="Final answer",ErrorMethod,TimeoutMethod,CancelMethod,ConnectError;
        internal bool Truncated,HasMore,Approval,Input,ServerApproval,InventoryCycle;
        internal object InventoryHint=true;
        internal int ErrorCode=500;
        internal Action<string> ReceiveHook;
        internal int Aborts;
        public bool IsOpen {get;private set;}
        private string method;
        public Task Connect(string accessToken,string accountId,CancellationToken token){Token=accessToken;Account=accountId;if(ConnectError!=null)throw new ArgumentException(ConnectError);IsOpen=true;return Task.FromResult(0);}
        private object Thread()
        {
            return new{id="thread_fixture",name="Fixture",status=new{type=Status,activeFlags=Approval?new[]{"waitingOnApproval"}:Input?new[]{"waitingOnUserInput"}:new string[0]},environments=Environment==null?new object[0]:new object[]{new{environmentId="environment_fixture",environmentConfigId=Environment}}};
        }
        public Task Send(string message,CancellationToken token)
        {
            var value=Json.Read<object>(message);Messages.Add(value);method=Json.Str(Json.Get(value,"method"));object result;
            if(method=="initialized")return Task.FromResult(0);
            if(method=="thread/read"&&ServerApproval)replies.Enqueue(Json.Write(new{id="native-approval-request",method="item/commandExecution/requestApproval",@params=new{threadId="thread_fixture",command=Secret}}));
            if(method==ErrorMethod){replies.Enqueue(Json.Write(new{id=Json.Get(value,"id"),error=new{code=ErrorCode,message=Secret,data=new{token=Secret}}}));return Task.FromResult(0);}
            if(method=="initialize")result=new{userAgent="fixture-native-cloud"};
            else if(method=="thread/start"||method=="thread/read")result=new{thread=Thread()};
            else if(method=="thread/list")result=new{data=new[]{Thread()},nextCursor=(string)null};
            else if(method=="thread/turns/list")result=new{data=new[]{new{id="turn_fixture",status=LatestStatus}},nextCursor=(string)null};
            else if(method=="thread/items/list")result=new{data=new object[]{new{turnId="turn_fixture",item=new{type="agentMessage",phase="final",text=Final,truncated=Truncated}},new{turnId="turn_fixture",item=new{type="agentMessage",phase="commentary",text="PRIVATE_INTERMEDIATE_TEXT"}},new{turnId="turn_fixture",item=new{type="functionCallOutput",output="PRIVATE_TOOL_OUTPUT"}}},nextCursor=HasMore?"more-items":null};
            else if(method=="mcpServerStatus/list")result=new{data=new[]{new{name=InventoryCycle?"fixture_server_"+Count("mcpServerStatus/list"):"fixture_server",tools=new Dictionary<string,object>{{"read_data",new{description="Read permitted data",annotations=new{readOnlyHint=InventoryHint},inputSchema=new{properties=new Dictionary<string,object>{{"resource_id",new{type="string",@default=Secret}}},required=new[]{"resource_id"}},_meta=new{access_token=Secret}}},{"write_data",new{description="Modify permitted data",inputSchema=new{properties=new{},required=new string[0]}}}}}},nextCursor=InventoryCycle?"cursor-repeated":null};
            else if(method=="turn/start")result=new{turn=new{id="started_turn_fixture",status="inProgress"}};
            else throw new InvalidOperationException("Unexpected native method "+method);
            replies.Enqueue(Json.Write(new{id=Json.Get(value,"id"),result=result}));return Task.FromResult(0);
        }
        public Task<string> Receive(CancellationToken token){if(ReceiveHook!=null)ReceiveHook(method);if(method==TimeoutMethod)throw new IOException(Secret);if(method==CancelMethod)throw new OperationCanceledException();return Task.FromResult(replies.Dequeue());}
        public void Abort(){Aborts++;IsOpen=false;replies.Clear();}
        public void Dispose(){Abort();}
        internal int Count(string name){return Messages.Count(m=>Json.Str(Json.Get(m,"method"))==name);}
        internal object Last(string name){return Messages.Last(m=>Json.Str(Json.Get(m,"method"))==name);}
    }
    private sealed class Fixture:IDisposable
    {
        internal AuthIdentity Current=Identity();
        internal readonly Wire Transport=new Wire();
        internal readonly CloudTasksBackend Backend;
        internal CloudConfiguration Configuration=new CloudConfiguration{Id=Config,VersionRevision=2,VersionId="published",LatestReadyVersionId="published",Status="ready"};
        internal Fixture(){Backend=new CloudTasksBackend(Transport,()=>Current,CloudBackend.Capture(Current),(id,token)=>Task.FromResult(Configuration));}
        public void Dispose(){Backend.Dispose();}
        internal CloudTaskTurn Turn(){return Backend.StartTurn("thread_fixture","Allowed task","stable-message-fixture",CancellationToken.None).GetAwaiter().GetResult();}
    }
    internal static void RunAll(Action<string,Action> check)
    {
        check("native Cloud websocket uses the bound account and never places a bearer in RPC JSON",()=>{
            using(var f=new Fixture()){
                f.Backend.Connect(CancellationToken.None).GetAwaiter().GetResult();
                Assert(f.Transport.Account==f.Current.AccountId&&f.Transport.Token==f.Current.AccessToken&&f.Transport.Count("initialize")==1,"websocket account binding missing");
                Assert(!Json.Write(f.Transport.Messages).Contains(f.Current.AccessToken),"bearer appears in RPC messages");
            }
        });
        check("native Cloud validates publication before a regular thread start",()=>{
            using(var f=new Fixture()){
                f.Configuration.VersionRevision=1;Reject(()=>f.Backend.Start(Config,null,CancellationToken.None).GetAwaiter().GetResult());
                f.Configuration.VersionRevision=2;f.Configuration.Id="different";Reject(()=>f.Backend.Start(Config,null,CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Transport.Count("thread/start")==0,"unpublished or different environment started");
            }
        });
        check("native Cloud starts a chat without sending the mission and with one explicit environment",()=>{
            using(var f=new Fixture()){
                var thread=f.Backend.Start(Config,null,CancellationToken.None).GetAwaiter().GetResult();var arguments=Json.Get(f.Transport.Last("thread/start"),"params");
                var environments=RelayEngine.Rows(Json.Get(arguments,"environments")).ToArray();
                Assert(thread.VerifiedEnvironmentConfigId==Config&&environments.Length==1&&Json.Str(Json.Get(environments[0],"environmentConfigId"))==Config,"thread start environment differs");
                Assert(f.Transport.Count("turn/start")==0&&!Json.Write(arguments).Contains("Allowed task"),"thread creation implicitly sent a mission");
            }
        });
        check("native Cloud refuses turn submission without verified environment or explicit idle state",()=>{
            foreach(var reason in new[]{"environment","busy","unknown","approval","input"})using(var f=new Fixture()){
                if(reason=="environment")f.Transport.Environment=null;if(reason=="busy")f.Transport.Status="active";if(reason=="unknown")f.Transport.Status="unknown";if(reason=="approval")f.Transport.Approval=true;if(reason=="input")f.Transport.Input=true;
                Reject(()=>f.Turn());Assert(f.Transport.Count("turn/start")==0,"unverified or busy task received prompt");
            }
        });
        check("native Cloud preserves explicit message idempotency and submits exactly one turn",()=>{
            using(var f=new Fixture()){
                Assert(f.Turn().Id=="started_turn_fixture"&&f.Transport.Count("turn/start")==1,"turn not confirmed once");var arguments=Json.Get(f.Transport.Last("turn/start"),"params");
                Assert(Json.Str(Json.Get(arguments,"clientUserMessageId"))=="stable-message-fixture"&&Json.Str(Json.Get(arguments,"threadId"))=="thread_fixture","client or thread correlation lost");
            }
        });
        check("native Cloud extracts only final agent text, redacts credentials and flags incomplete pages",()=>{
            using(var f=new Fixture()){
                f.Transport.Final="Result Bearer SUPER_PRIVATE_TOKEN and ghp_PrivateFixtureSecret0123456789";
                var thread=f.Backend.Read("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();
                Assert(thread.LatestTurn.TextComplete&&!thread.LatestTurn.Text.Contains("SUPER_PRIVATE_TOKEN")&&!thread.LatestTurn.Text.Contains("ghp_")&&!thread.LatestTurn.Text.Contains("PRIVATE_INTERMEDIATE_TEXT")&&!thread.LatestTurn.Text.Contains("PRIVATE_TOOL_OUTPUT"),"non-final or secret text returned");
                f.Transport.HasMore=true;Assert(!f.Backend.Read("thread_fixture",CancellationToken.None).GetAwaiter().GetResult().LatestTurn.TextComplete,"paginated final falsely complete");
                f.Transport.HasMore=false;f.Transport.Truncated=true;Assert(!f.Backend.Read("thread_fixture",CancellationToken.None).GetAwaiter().GetResult().LatestTurn.TextComplete,"truncated final falsely complete");
            }
        });
        check("native Cloud uncertain websocket mutation is surfaced without automatic resend",()=>{
            using(var f=new Fixture()){
                f.Transport.TimeoutMethod="turn/start";var error=Reject(()=>f.Turn());
                Assert(error is CloudBackendUncertainException&&f.Transport.Count("turn/start")==1&&f.Transport.Aborts>0&&!error.ToString().Contains(Secret),"lost turn receipt retried or leaked server text");
            }
        });
        check("native Cloud RPC errors redact raw messages and data",()=>{
            using(var f=new Fixture()){
                f.Transport.ErrorMethod="thread/list";var error=Reject(()=>f.Backend.List(null,10,CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Code=="rpc-500"&&!error.ToString().Contains(Secret)&&f.Transport.Count("thread/list")==1,"raw server RPC error escaped");
            }
        });
        check("native Cloud checks account drift before connection and after mutation response",()=>{
            using(var f=new Fixture()){
                f.Current=Identity("account-b");Reject(()=>f.Backend.Connect(CancellationToken.None).GetAwaiter().GetResult());Assert(f.Transport.Messages.Count==0,"changed account initialized connection");
            }
            using(var f=new Fixture()){
                f.Transport.ReceiveHook=method=>{if(method=="turn/start")f.Current=Identity("account-b");};
                Assert(Reject(()=>f.Turn()) is CloudBackendUncertainException&&f.Transport.Count("turn/start")==1,"post-send identity drift not marked uncertain");
            }
        });
        check("native Cloud never answers server approval requests on behalf of the user",()=>{
            using(var f=new Fixture()){
                f.Transport.ServerApproval=true;f.Transport.Status="active";f.Transport.LatestStatus="inProgress";
                Reject(()=>f.Turn());
                Assert(f.Transport.Count("turn/start")==0&&!f.Transport.Messages.Any(m=>Json.Str(Json.Get(m,"id"))=="native-approval-request"),"native approval was answered or mission dispatched");
            }
        });
        check("native Cloud cancellation after a write stays uncertain and is never retried",()=>{
            using(var f=new Fixture()){
                f.Transport.CancelMethod="turn/start";
                Assert(Reject(()=>f.Turn()) is CloudBackendUncertainException&&f.Transport.Count("turn/start")==1,"cancelled write retried or reported as unsent");
            }
            using(var f=new Fixture())using(var cancel=new CancellationTokenSource()){
                cancel.Cancel();Reject(()=>f.Backend.StartTurn("thread_fixture","Allowed task",cancel.Token).GetAwaiter().GetResult());
                Assert(f.Transport.Messages.Count==0,"pre-cancelled operation reached transport");
            }
        });
        check("native Cloud lists tasks read-only and validates pagination before transport",()=>{
            using(var f=new Fixture()){
                Reject(()=>f.Backend.List(null,101,CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>f.Backend.List("bad\ncontrol",10,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Transport.Messages.Count==0,"invalid pagination reached transport");
                var page=f.Backend.List("opaque cursor +/",10,CancellationToken.None).GetAwaiter().GetResult();var arguments=Json.Get(f.Transport.Last("thread/list"),"params");
                Assert(page.Data.Count==1&&Json.Str(Json.Get(arguments,"cursor"))=="opaque cursor +/"&&f.Transport.Count("thread/start")==0&&f.Transport.Count("turn/start")==0,"listing mutated tasks or changed cursor");
            }
        });
        check("native Cloud onboarding requires confirmed ownership and uses a distinct native context",()=>{
            using(var f=new Fixture()){
                f.Configuration.VersionRevision=1;f.Configuration.LatestReadyVersionId=null;f.Configuration.IsOwner=null;
                Reject(()=>f.Backend.StartOnboarding(Config,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Transport.Count("thread/start")==0,"unknown ownership allowed setup thread");
                f.Configuration.IsOwner=true;f.Backend.StartOnboarding(Config,CancellationToken.None).GetAwaiter().GetResult();
                var arguments=Json.Get(f.Transport.Last("thread/start"),"params");var environment=RelayEngine.Rows(Json.Get(arguments,"environments")).Single();
                Assert(Json.Str(Json.Get(environment,"onboardingConfigId"))==Config&&Json.Get(environment,"environmentConfigId")==null&&f.Transport.Count("turn/start")==0,"onboarding used a normal worker environment or implicitly sent a prompt");
            }
        });
        check("native Cloud onboarding binds verified repository names and requests the official setup skill",()=>{
            using(var f=new Fixture()){
                f.Configuration.VersionRevision=1;f.Configuration.LatestReadyVersionId=null;f.Configuration.IsOwner=true;f.Configuration.Repositories=new List<CloudRepository>{new CloudRepository{RepositoryId="github/123",Ref="main"}};
                var invalid=new Dictionary<string,string>{{"github/other","fixture/lab"}};
                Reject(()=>f.Backend.StartOnboardingTurn("thread_fixture","Prepare only","setup-id",invalid,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Transport.Count("turn/start")==0,"incorrect repository map submitted");
                var map=new Dictionary<string,string>{{"github/123","fixture/lab"}};
                f.Backend.StartOnboardingTurn("thread_fixture","Prepare only","setup-id",map,CancellationToken.None).GetAwaiter().GetResult();
                var arguments=Json.Get(f.Transport.Last("turn/start"),"params");var inputs=RelayEngine.Rows(Json.Get(arguments,"input")).ToArray();
                Assert(Json.Str(Json.Get(arguments,"turnTrigger"))=="environment_onboarding"&&inputs.Length==2&&Json.Write(inputs).Contains("fixture/lab")&&Json.Write(inputs).Contains("$cloud-environment-onboarding:setup"),"native setup contract or official skill missing");
            }
        });
        check("native Cloud connection diagnostics retain exception types without exposing their messages",()=>{
            using(var f=new Fixture()){
                f.Transport.ConnectError=Secret;var error=Reject(()=>f.Backend.Connect(CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Message.Contains("ArgumentException")&&!error.ToString().Contains(Secret)&&!error.ToString().Contains(f.Current.AccessToken),"connection diagnostic lost safe cause or leaked private message");
            }
        });
        check("native Cloud tool inventory binds metadata to its account, chat and verified environment",()=>{
            using(var f=new Fixture()){
                var inventory=f.Backend.InventoryTools("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();var read=inventory.Tools.Single(t=>t.ToolName=="read_data");
                Assert(inventory.AccountKey==f.Current.Key&&inventory.ThreadId=="thread_fixture"&&inventory.ConfigurationId==Config&&read.ReadOnly==true&&read.ArgumentNames.SequenceEqual(new[]{"resource_id"})&&read.RequiredArguments.SequenceEqual(new[]{"resource_id"}),"tool metadata lost account or schema boundary");
                Assert(inventory.Tools.Single(t=>t.ToolName=="write_data").ReadOnly==null&&!Json.Write(inventory).Contains(Secret),"unknown hint treated as read-only or schema defaults/auth leaked");
                var arguments=Json.Get(f.Transport.Last("mcpServerStatus/list"),"params");Assert(Json.Str(Json.Get(arguments,"threadId"))=="thread_fixture"&&Json.Str(Json.Get(arguments,"detail"))=="toolsAndAuthOnly"&&f.Transport.Count("turn/start")==0,"inventory executed a tool or used the wrong chat");
            }
        });
        check("native Cloud tool inventory refuses unconfirmed environment and malformed permission hints",()=>{
            using(var f=new Fixture()){
                f.Transport.Environment=null;Reject(()=>f.Backend.InventoryTools("thread_fixture",CancellationToken.None).GetAwaiter().GetResult());Assert(f.Transport.Count("mcpServerStatus/list")==0,"tools listed without a verified environment");
            }
            using(var f=new Fixture()){
                f.Transport.InventoryHint="true";Reject(()=>f.Backend.InventoryTools("thread_fixture",CancellationToken.None).GetAwaiter().GetResult());Assert(f.Transport.Count("turn/start")==0,"malformed hint caused tool execution");
            }
        });
        check("native Cloud tool inventory bounds repeated pagination cursors without executing tools",()=>{
            using(var f=new Fixture()){
                f.Transport.InventoryCycle=true;var error=Reject(()=>f.Backend.InventoryTools("thread_fixture",CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Code=="invalid-response"&&f.Transport.Count("mcpServerStatus/list")==2&&f.Transport.Count("turn/start")==0,"cyclic inventory pagination looped or executed work");
            }
        });
        check("native Cloud unsupported tool inventory stays an explicit RPC error rather than an empty success",()=>{
            using(var f=new Fixture()){
                f.Transport.ErrorMethod="mcpServerStatus/list";f.Transport.ErrorCode=-32601;
                var error=Reject(()=>f.Backend.InventoryTools("thread_fixture",CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Code=="rpc--32601"&&f.Transport.Count("mcpServerStatus/list")==1&&f.Transport.Count("turn/start")==0&&!error.ToString().Contains(Secret),"unsupported inventory was hidden or fell back to execution");
            }
        });
    }
}
