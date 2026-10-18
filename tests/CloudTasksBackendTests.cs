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
        internal bool Truncated,HasMore,Approval,Input,ServerApproval,InventoryCycle,NoTurns;
        internal string CurrentModel="gpt-6.1-sol",CatalogueModel="gpt-6.1-sol",ObservedModel="gpt-6.1-sol",ObservedEffort="ultra",ObservedSandbox="dangerFullAccess",ObservedPolicy="never",ObservedReviewer="user",ObservedProfile;
        internal string[] CatalogueEfforts=new[]{"low","medium","high","xhigh","max","ultra"};
        internal bool OmitExecutionSettings,ModelCatalogueCycle=false;
        internal object InventoryHint=true;
        internal int ErrorCode=500;
        internal Action<string> ReceiveHook;
        internal Func<string,object,object> CustomReply;
        internal int Aborts;
        public bool IsOpen {get;private set;}
        private string method;
        public Task Connect(string accessToken,string accountId,CancellationToken token){Token=accessToken;Account=accountId;if(ConnectError!=null)throw new ArgumentException(ConnectError);IsOpen=true;return Task.FromResult(0);}
        private object Thread()
        {
            return new{id="thread_fixture",name="Fixture",model=CurrentModel,status=new{type=Status,activeFlags=Approval?new[]{"waitingOnApproval"}:Input?new[]{"waitingOnUserInput"}:new string[0]},environments=Environment==null?new object[0]:new object[]{new{environmentId="environment_fixture",environmentConfigId=Environment}}};
        }
        private object Execution()
        {
            if(OmitExecutionSettings)return new{thread=Thread()};
            return new Dictionary<string,object>{{"thread",Thread()},{"model",ObservedModel},{"reasoningEffort",ObservedEffort},{"sandbox",ObservedSandbox==null?null:new{type=ObservedSandbox}},{"approvalPolicy",ObservedPolicy},{"approvalsReviewer",ObservedReviewer},{"activePermissionProfile",ObservedProfile==null?null:new{id=ObservedProfile}}};
        }
        private static object Field(object value,string key){if(value==null)return null;var property=value.GetType().GetProperty(key);return property==null?Json.Get(value,key):property.GetValue(value,null);}
        public Task Send(string message,CancellationToken token)
        {
            var value=Json.Read<object>(message);Messages.Add(value);method=Json.Str(Json.Get(value,"method"));object result;
            if(method=="initialized")return Task.FromResult(0);
            if(method=="thread/read"&&ServerApproval)replies.Enqueue(Json.Write(new{id="native-approval-request",method="item/commandExecution/requestApproval",@params=new{threadId="thread_fixture",command=Secret}}));
            if(method==ErrorMethod){replies.Enqueue(Json.Write(new{id=Json.Get(value,"id"),error=new{code=ErrorCode,message=Secret,data=new{token=Secret}}}));return Task.FromResult(0);}
            if(CustomReply!=null){var custom=CustomReply(method,Json.Get(value,"params"));if(custom!=null){if(method=="thread/items/list")foreach(var row in RelayEngine.Rows(Field(custom,"data"))){if(Json.Str(Field(Field(row,"item"),"output")).Length>=4194304)throw new CloudBackendException("response-too-large","Fixture bounded frame");}replies.Enqueue(Json.Write(new{id=Json.Get(value,"id"),result=custom}));return Task.FromResult(0);}}
            if(method=="initialize")result=new{userAgent="fixture-native-cloud"};
            else if(method=="thread/start"||method=="thread/read")result=new{thread=Thread()};
            else if(method=="thread/resume")result=Execution();
            else if(method=="model/list")result=new{data=new[]{new{id=CatalogueModel,model=CatalogueModel,isDefault=true,supportedReasoningEfforts=CatalogueEfforts.Select(e=>new{reasoningEffort=e}).ToArray()}},nextCursor=ModelCatalogueCycle?"same-model-cursor":null};
            else if(method=="thread/list")result=new{data=new[]{Thread()},nextCursor=(string)null};
            else if(method=="thread/turns/list")result=new{data=NoTurns?new object[0]:new object[]{new{id="turn_fixture",status=LatestStatus}},nextCursor=(string)null};
            else if(method=="thread/items/list")result=new{data=new object[]{new{turnId="turn_fixture",item=new{type="agentMessage",phase="final",text=Final,truncated=Truncated}},new{turnId="turn_fixture",item=new{type="agentMessage",phase="commentary",text="PRIVATE_INTERMEDIATE_TEXT"}},new{turnId="turn_fixture",item=new{type="functionCallOutput",output="PRIVATE_TOOL_OUTPUT"}}},nextCursor=HasMore?"more-items":null};
            else if(method=="mcpServerStatus/list")result=new{data=new[]{new{name=InventoryCycle?"fixture_server_"+Count("mcpServerStatus/list"):"fixture_server",tools=new Dictionary<string,object>{{"read_data",new{description="Read permitted data",annotations=new{readOnlyHint=InventoryHint},inputSchema=new{properties=new Dictionary<string,object>{{"resource_id",new{type="string",@default=Secret}}},required=new[]{"resource_id"}},_meta=new{access_token=Secret}}},{"write_data",new{description="Modify permitted data",inputSchema=new{properties=new{},required=new string[0]}}}}}},nextCursor=InventoryCycle?"cursor-repeated":null};
            else if(method=="turn/start")result=new{turn=new{id="started_turn_fixture",status="inProgress"}};
            else throw new InvalidOperationException("Unexpected native method "+method);
            replies.Enqueue(Json.Write(new{id=Json.Get(value,"id"),result=result}));return Task.FromResult(0);
        }
        public Task<string> Receive(CancellationToken token){if(ReceiveHook!=null)ReceiveHook(method);if(method==TimeoutMethod)throw new IOException(Secret);if(method==CancelMethod)throw new OperationCanceledException();string reply=replies.Dequeue();if(Encoding.UTF8.GetByteCount(reply)>4194304)throw new CloudBackendException("response-too-large","Fixture bounded frame");return Task.FromResult(reply);}
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
        check("native Cloud legacy creation and turns keep inherited execution settings unchanged",()=>{
            using(var f=new Fixture()){
                f.Backend.Start(Config,null,CancellationToken.None).GetAwaiter().GetResult();f.Turn();
                foreach(string method in new[]{"thread/start","turn/start"}){
                    var args=Json.Obj(Json.Get(f.Transport.Last(method),"params"));
                    Assert(!new[]{"model","config","effort","permissions","sandbox","sandboxPolicy","approvalPolicy","approvalsReviewer"}.Any(args.ContainsKey),"legacy call acquired implicit execution overrides");
                }
                Assert(f.Transport.Count("model/list")==0&&f.Transport.Count("thread/resume")==0,"legacy call added execution probes");
            }
        });
        check("native Cloud explicitly creates the requested model ultra reasoning and native full access",()=>{
            using(var f=new Fixture()){
                var options=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"};
                f.Backend.Start(Config,null,options,CancellationToken.None).GetAwaiter().GetResult();var args=Json.Obj(Json.Get(f.Transport.Last("thread/start"),"params"));
                Assert(Json.Str(Json.Get(args,"model"))==options.Model&&Json.Str(Json.Get(Json.Get(args,"config"),"model_reasoning_effort"))=="ultra","creation omitted exact model or reasoning");
                Assert(Json.Str(Json.Get(args,"sandbox"))=="danger-full-access"&&Json.Str(Json.Get(args,"approvalPolicy"))=="never"&&Json.Str(Json.Get(args,"approvalsReviewer"))=="user"&&!args.ContainsKey("permissions"),"full access did not use the verified native legacy builder");
                Assert(f.Transport.Count("model/list")==1&&f.Transport.Count("turn/start")==0,"creation skipped model capability validation or sent a prompt");
            }
        });
        check("native Cloud explicitly submits the same execution tuple on its first turn",()=>{
            using(var f=new Fixture()){
                var options=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"};
                f.Backend.StartTurn("thread_fixture","Allowed task","stable-execution-message",options,CancellationToken.None).GetAwaiter().GetResult();var args=Json.Obj(Json.Get(f.Transport.Last("turn/start"),"params"));
                Assert(Json.Str(Json.Get(args,"model"))==options.Model&&Json.Str(Json.Get(args,"effort"))=="ultra"&&Json.Str(Json.Get(Json.Get(args,"sandboxPolicy"),"type"))=="dangerFullAccess","turn omitted model reasoning or full-access policy");
                Assert(Json.Str(Json.Get(args,"approvalPolicy"))=="never"&&Json.Str(Json.Get(args,"approvalsReviewer"))=="user"&&Json.Str(Json.Get(args,"clientUserMessageId"))=="stable-execution-message"&&!args.ContainsKey("sandbox"),"turn mixed thread and turn permission contracts");
            }
        });
        check("native Cloud custom permissions remain the exact requested profile without widening",()=>{
            using(var f=new Fixture()){
                var options=new CloudTaskExecutionOptions{PermissionProfile="customer-approved"};f.Backend.Start(Config,null,options,CancellationToken.None).GetAwaiter().GetResult();
                f.Backend.StartTurn("thread_fixture","Allowed task","custom-profile",options,CancellationToken.None).GetAwaiter().GetResult();
                foreach(string method in new[]{"thread/start","turn/start"}){
                    var args=Json.Obj(Json.Get(f.Transport.Last(method),"params"));
                    Assert(Json.Str(Json.Get(args,"permissions"))=="customer-approved"&&!new[]{"sandbox","sandboxPolicy","approvalPolicy","approvalsReviewer"}.Any(args.ContainsKey),"custom profile silently expanded to full access");
                }
            }
        });
        check("native Cloud unavailable models and reasoning fail before creating or prompting a chat",()=>{
            foreach(string invalid in new[]{"model","effort","catalogue"})using(var f=new Fixture()){
                var options=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra"};
                if(invalid=="model")options.Model="gpt-unavailable";if(invalid=="effort")f.Transport.CatalogueEfforts=new[]{"low","high"};if(invalid=="catalogue"){f.Transport.ErrorMethod="model/list";f.Transport.ErrorCode=-32601;}
                Reject(()=>f.Backend.Start(Config,null,options,CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>f.Backend.StartTurn("thread_fixture","Allowed task","unsupported-execution",options,CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Transport.Count("thread/start")==0&&f.Transport.Count("turn/start")==0,"unavailable settings substituted another model or executed a prompt");
            }
        });
        check("native Cloud effort-only overrides use the actual current model rather than a catalogue fallback",()=>{
            using(var f=new Fixture()){
                f.Transport.CurrentModel="gpt-other-model";var options=new CloudTaskExecutionOptions{ReasoningEffort="ultra"};
                Reject(()=>f.Backend.StartTurn("thread_fixture","Allowed task","effort-only",options,CancellationToken.None).GetAwaiter().GetResult());
                f.Transport.CurrentModel=null;Reject(()=>f.Backend.StartTurn("thread_fixture","Allowed task","effort-unknown",options,CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Transport.Count("turn/start")==0,"effort used a different model or an unverified current model");
            }
        });
        check("native Cloud freezes execution options before awaiting model discovery",()=>{
            using(var f=new Fixture()){
                var options=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"};
                f.Transport.ReceiveHook=method=>{if(method=="model/list"){options.Model="gpt-other";options.ReasoningEffort="low";options.PermissionProfile=":read-only";}};
                f.Backend.Start(Config,null,options,CancellationToken.None).GetAwaiter().GetResult();var args=Json.Get(f.Transport.Last("thread/start"),"params");
                Assert(Json.Str(Json.Get(args,"model"))=="gpt-6.1-sol"&&Json.Str(Json.Get(Json.Get(args,"config"),"model_reasoning_effort"))=="ultra"&&Json.Str(Json.Get(args,"sandbox"))=="danger-full-access","awaiting discovery changed the frozen request");
            }
        });
        check("native Cloud reads persisted execution settings using a fresh resume with no requested overrides",()=>{
            using(var f=new Fixture()){
                var observation=f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();var args=Json.Obj(Json.Get(f.Transport.Last("thread/resume"),"params"));
                Assert(args.Count==3&&Json.Str(Json.Get(args,"threadId"))=="thread_fixture"&&Object.Equals(Json.Get(args,"excludeTurns"),true)&&Object.Equals(Json.Get(args,"deferGoalContinuation"),true),"execution readback replayed overrides or allowed automatic goal continuation");
                Assert(observation.Verified&&observation.Model=="gpt-6.1-sol"&&observation.ReasoningEffort=="ultra"&&observation.PermissionProfile==":danger-full-access"&&observation.ApprovalPolicy=="never"&&observation.ApprovalsReviewer=="user","native root settings were not parsed");
                f.Transport.ObservedEffort="low";var fresh=f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();
                Assert(fresh.ReasoningEffort=="low"&&f.Transport.Count("thread/resume")==2&&f.Transport.Count("turn/start")==0,"execution readback reused requested or cached settings or started work");
            }
        });
        check("native Cloud unknown or mismatched execution observations never match the requested tuple",()=>{
            var requested=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"};
            using(var f=new Fixture()){
                f.Transport.OmitExecutionSettings=true;var unknown=f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();
                Assert(!unknown.Verified&&unknown.Model==null&&unknown.ReasoningEffort==null&&unknown.PermissionProfile==null&&!unknown.Matches(requested),"unknown settings were filled from requested values");
                f.Transport.OmitExecutionSettings=false;f.Transport.ObservedModel="gpt-other-model";var mismatch=f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();
                Assert(mismatch.Model=="gpt-other-model"&&!mismatch.Matches(requested),"different native model was treated as requested");
                f.Transport.ObservedModel="gpt-6.1-sol";f.Transport.ObservedEffort=null;Assert(!f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult().Matches(requested),"unknown reasoning matched ultra");
            }
        });
        check("native Cloud only identifies legacy full access from the exact native permissions tuple",()=>{
            foreach(string missing in new[]{"sandbox","policy","reviewer"})using(var f=new Fixture()){
                if(missing=="sandbox")f.Transport.ObservedSandbox="workspaceWrite";if(missing=="policy")f.Transport.ObservedPolicy="on-request";if(missing=="reviewer")f.Transport.ObservedReviewer=null;
                Assert(f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult().PermissionProfile==null,"incomplete or restricted tuple was called full access");
            }
            foreach(string missing in new[]{"sandbox","policy","reviewer"})using(var f=new Fixture()){
                f.Transport.ObservedProfile=":danger-full-access";
                if(missing=="sandbox")f.Transport.ObservedSandbox="workspaceWrite";if(missing=="policy")f.Transport.ObservedPolicy=null;if(missing=="reviewer")f.Transport.ObservedReviewer=null;
                var observation=f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();
                Assert(observation.PermissionProfile==null&&!observation.Matches(new CloudTaskExecutionOptions{PermissionProfile=":danger-full-access"}),"full-access profile label concealed missing or contradictory native permissions");
            }
            using(var f=new Fixture()){
                f.Transport.ObservedProfile="customer-approved";f.Transport.ObservedSandbox=null;f.Transport.ObservedPolicy=null;f.Transport.ObservedReviewer=null;
                Assert(f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult().PermissionProfile=="customer-approved","explicit custom native profile was discarded or rewritten");
            }
        });
        check("native Cloud execution readback failures do not fall back to a cache or a settings mutation",()=>{
            using(var f=new Fixture()){
                f.Transport.ErrorMethod="thread/resume";f.Transport.ErrorCode=-32601;
                var error=Reject(()=>f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Code=="rpc--32601"&&f.Transport.Count("thread/resume")==1&&f.Transport.Count("turn/start")==0,"unsupported readback was hidden or replaced by work");
            }
        });
        check("native Cloud execution readback preserves the previously verified environment",()=>{
            using(var f=new Fixture()){
                f.Backend.Read("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();f.Transport.Environment="different_configuration";
                var error=Reject(()=>f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Code=="execution-environment-changed"&&f.Transport.Count("thread/resume")==1&&f.Transport.Count("turn/start")==0,"resume accepted settings from another environment");
                Reject(()=>f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult());
            }
        });
        check("native Cloud configures only a fresh empty chat before its first prompt and then reads without overrides",()=>{
            using(var f=new Fixture()){
                f.Transport.NoTurns=true;var options=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"};
                var observed=f.Backend.ConfigureExecution("thread_fixture",options,CancellationToken.None).GetAwaiter().GetResult();var args=Json.Obj(Json.Get(f.Transport.Last("thread/resume"),"params"));
                Assert(observed.Matches(options)&&Json.Str(Json.Get(args,"model"))=="gpt-6.1-sol"&&Json.Str(Json.Get(Json.Get(args,"config"),"model_reasoning_effort"))=="ultra","native initial configuration omitted model or effort");
                Assert(Json.Str(Json.Get(args,"sandbox"))=="danger-full-access"&&Json.Str(Json.Get(args,"approvalPolicy"))=="never"&&Json.Str(Json.Get(args,"approvalsReviewer"))=="user"&&Object.Equals(Json.Get(args,"deferGoalContinuation"),true)&&Object.Equals(Json.Get(args,"excludeTurns"),true),"initial resume did not use the proven permission builder or deferred goal continuation");
                Assert(f.Transport.Count("thread/read")==2&&f.Transport.Count("turn/start")==0&&f.Transport.Count("thread/start")==0,"initial configuration skipped its fresh guards or sent bootstrap work");
                f.Backend.ReadExecution("thread_fixture",CancellationToken.None).GetAwaiter().GetResult();Assert(Json.Obj(Json.Get(f.Transport.Last("thread/resume"),"params")).Count==3,"observation replayed requested overrides after configuration");
            }
        });
        check("native Cloud initial configuration refuses used busy unknown or intervention-bound chats",()=>{
            foreach(string reason in new[]{"history","busy","unknown","approval","input","environment"})using(var f=new Fixture()){
                f.Transport.NoTurns=reason!="history";if(reason=="busy")f.Transport.Status="active";if(reason=="unknown")f.Transport.Status="unknown";if(reason=="approval")f.Transport.Approval=true;if(reason=="input")f.Transport.Input=true;if(reason=="environment")f.Transport.Environment=null;
                Reject(()=>f.Backend.ConfigureExecution("thread_fixture",new CloudTaskExecutionOptions{PermissionProfile=":danger-full-access"},CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Transport.Count("thread/resume")==0&&f.Transport.Count("turn/start")==0,"used or unverified chat had its permissions changed");
            }
        });
        check("native Cloud initial configuration rechecks the untouched chat after model discovery",()=>{
            foreach(string changed in new[]{"activity","history","environment"})using(var f=new Fixture()){
                f.Transport.NoTurns=true;f.Transport.ReceiveHook=method=>{if(method=="model/list"){if(changed=="activity")f.Transport.Status="active";if(changed=="history")f.Transport.NoTurns=false;if(changed=="environment")f.Transport.Environment="different_configuration";}};
                Reject(()=>f.Backend.ConfigureExecution("thread_fixture",new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"},CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Transport.Count("thread/resume")==0&&f.Transport.Count("turn/start")==0,"concurrent activity or environment drift was ignored before changing settings");
            }
        });
        check("native Cloud initial configuration checks publication and refuses unsupported execution without fallback",()=>{
            foreach(string invalid in new[]{"draft","unpublished","model","effort"})using(var f=new Fixture()){
                f.Transport.NoTurns=true;var options=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"};
                if(invalid=="draft")f.Configuration.Draft=new CloudConfigurationDraft();if(invalid=="unpublished")f.Configuration.VersionRevision=1;if(invalid=="model")options.Model="gpt-unavailable";if(invalid=="effort")f.Transport.CatalogueEfforts=new[]{"low"};
                Reject(()=>f.Backend.ConfigureExecution("thread_fixture",options,CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Transport.Count("thread/resume")==0&&f.Transport.Count("turn/start")==0,"invalid configuration or unsupported settings reached mutation");
            }
        });
        check("native Cloud initial configuration returns actual unknown or different settings rather than requested values",()=>{
            using(var f=new Fixture()){
                f.Transport.NoTurns=true;f.Transport.ObservedPolicy="on-request";f.Transport.ObservedReviewer="auto_review";
                var options=new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"};
                var observed=f.Backend.ConfigureExecution("thread_fixture",options,CancellationToken.None).GetAwaiter().GetResult();
                Assert(!observed.Matches(options)&&observed.PermissionProfile==null&&observed.ApprovalPolicy=="on-request"&&observed.ApprovalsReviewer=="auto_review","requested full access was passed off as native observation");
                f.Transport.OmitExecutionSettings=true;observed=f.Backend.ConfigureExecution("thread_fixture",options,CancellationToken.None).GetAwaiter().GetResult();
                Assert(!observed.Verified&&!observed.Matches(options)&&observed.Model==null,"missing initial settings were filled from the request");
            }
        });
        check("native Cloud initial configuration never retries an uncertain native resume",()=>{
            using(var f=new Fixture()){
                f.Transport.NoTurns=true;f.Transport.TimeoutMethod="thread/resume";
                var error=Reject(()=>f.Backend.ConfigureExecution("thread_fixture",new CloudTaskExecutionOptions{PermissionProfile=":danger-full-access"},CancellationToken.None).GetAwaiter().GetResult());
                Assert(error is CloudBackendUncertainException&&f.Transport.Count("thread/resume")==1&&f.Transport.Count("turn/start")==0,"uncertain initial permissions were retried or bootstrapped by a prompt");
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
                f.Transport.HasMore=true;Reject(()=>f.Backend.Read("thread_fixture",CancellationToken.None).GetAwaiter().GetResult());
                f.Transport.HasMore=false;f.Transport.Truncated=true;Assert(!f.Backend.Read("thread_fixture",CancellationToken.None).GetAwaiter().GetResult().LatestTurn.TextComplete,"truncated final falsely complete");
            }
        });
        check("native Cloud reads the exact admitted turn after a newer follow-up with complete paginated final text",()=>{
            using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>{
                    string cursor=Json.Str(Json.Get(args,"cursor"));
                    if(method=="thread/turns/list")return cursor.Length==0?new{data=new[]{new{id="newer_turn",status="inProgress"}},nextCursor="older-turns"}:(object)new{data=new[]{new{id="admitted_turn",status="completed"}},nextCursor=(string)null};
                    if(method=="thread/items/list"){
                        Assert(Json.Str(Json.Get(args,"turnId"))=="admitted_turn","read another turn's items");
                        return cursor.Length==0?new{data=new[]{new{turnId="admitted_turn",item=new{id="final-later",type="agentMessage",phase="final",text="Second part",truncated=false}}},nextCursor="older-items"}:(object)new{data=new[]{new{turnId="admitted_turn",item=new{id="final-earlier",type="agentMessage",phase="final",text="First part Bearer SUPER_PRIVATE_TOKEN",truncated=false}}},nextCursor=(string)null};
                    }
                    return null;
                };
                var turn=((ICloudTaskTurnConnection)f.Backend).ReadTurn("thread_fixture","admitted_turn",CancellationToken.None).GetAwaiter().GetResult();
                Assert(turn.Id=="admitted_turn"&&turn.Status=="completed"&&turn.TextComplete&&turn.Text.StartsWith("First part")&&turn.Text.EndsWith("Second part")&&!turn.Text.Contains("SUPER_PRIVATE_TOKEN"),"target final text lost correlation, order, completeness or redaction");
                Assert(f.Transport.Count("thread/turns/list")==2&&f.Transport.Count("thread/items/list")==2&&f.Transport.Count("thread/read")==2&&f.Transport.Count("thread/resume")==0&&f.Transport.Count("turn/start")==0,"exact turn read mutated work or missed context recheck");
            }
        });
        check("native Cloud exact turn lookup returns absent rather than substituting a newer result",()=>{
            using(var f=new Fixture()){
                Assert(f.Backend.ReadTurn("thread_fixture","missing_turn",CancellationToken.None).GetAwaiter().GetResult()==null,"missing admitted turn borrowed the latest result");
                Assert(f.Transport.Count("thread/items/list")==0&&f.Transport.Count("thread/read")==2,"missing turn read unrelated items or skipped binding checks");
            }
        });
        check("native Cloud bounds exact turn history and refuses cyclic pagination",()=>{
            using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>method!="thread/turns/list"?null:(object)new{data=Enumerable.Range(0,25).Select(index=>new{id="history_"+f.Transport.Count("thread/turns/list")+"_"+index,status="completed"}).ToArray(),nextCursor="history_"+f.Transport.Count("thread/turns/list")};
                Assert(f.Backend.ReadTurn("thread_fixture","missing_turn",CancellationToken.None).GetAwaiter().GetResult()==null&&f.Transport.Count("thread/turns/list")==4&&f.Transport.Count("thread/items/list")==0,"exact turn history exceeded its 100-turn bound");
            }
            using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>method!="thread/turns/list"?null:(object)new{data=new[]{new{id="history_"+f.Transport.Count("thread/turns/list"),status="completed"}},nextCursor="repeated"};
                var error=Reject(()=>f.Backend.ReadTurn("thread_fixture","missing_turn",CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Code=="invalid-response"&&f.Transport.Count("thread/turns/list")==2,"cyclic exact turn pagination did not stop");
            }
        });
        check("native Cloud exact turn reads refuse cross-turn items and changed environment or account",()=>{
            using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>method!="thread/items/list"?null:(object)new{data=new[]{new{turnId="another_turn",item=new{type="agentMessage",phase="final",text="Wrong result"}}},nextCursor=(string)null};
                Reject(()=>f.Backend.ReadTurn("thread_fixture","turn_fixture",CancellationToken.None).GetAwaiter().GetResult());
            }
            foreach(string change in new[]{"environment","account"})using(var f=new Fixture()){
                f.Transport.ReceiveHook=method=>{if(method=="thread/items/list"){if(change=="environment")f.Transport.Environment="changed_environment";else f.Current=Identity("account-b");}};
                Reject(()=>f.Backend.ReadTurn("thread_fixture","turn_fixture",CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Transport.Count("turn/start")==0&&f.Transport.Count("thread/resume")==0,"identity drift caused a mutation");
            }
        });
        check("native Cloud exact turn final text remains incomplete on truncated or cyclic item pages",()=>{
            using(var f=new Fixture()){
                f.Transport.Truncated=true;Assert(!f.Backend.ReadTurn("thread_fixture","turn_fixture",CancellationToken.None).GetAwaiter().GetResult().TextComplete,"truncated exact turn text was complete");
            }
            using(var f=new Fixture()){
                f.Transport.HasMore=true;var error=Reject(()=>f.Backend.ReadTurn("thread_fixture","turn_fixture",CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;
                Assert(error!=null&&error.Code=="invalid-response"&&f.Transport.Count("thread/items/list")==2,"cyclic exact turn item pagination did not stop");
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
        check("native Cloud reconciliation identifies an older exact user message without returning its prompt",()=>{
            using(var f=new Fixture()){
                string prompt="[CREEZIO_CLOUD_REQUEST:fixture_message] "+Secret;
                f.Transport.CustomReply=(method,args)=>{
                    string cursor=Json.Str(Json.Get(args,"cursor"));
                    if(method=="thread/turns/list")return cursor.Length==0?new{data=new[]{new{id="newer",status="completed"}},nextCursor="older"}:(object)new{data=new[]{new{id="owned",status="completed"}},nextCursor=(string)null};
                    if(method=="thread/items/list"){string turn=Json.Str(Json.Get(args,"turnId"));return new{data=new[]{new{turnId=turn,item=new{id="user_"+turn,type="userMessage",clientId=turn=="owned"?"fixture_message":"other",content=new[]{new{type="text",text=turn=="owned"?prompt:"unrelated"}}}}},nextCursor=(string)null};}return null;
                };
                var result=f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(prompt),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.HistoryComplete&&result.Turn.Id=="owned"&&result.Evidence=="client-id-and-prompt-sha256"&&result.AccountKey==f.Current.Key&&!Json.Write(result).Contains(Secret),"exact user proof was missing or exposed input");
                Assert(f.Transport.Count("thread/read")==2&&f.Transport.Count("turn/start")==0&&f.Transport.Count("thread/resume")==0,"reconciliation mutated work or skipped account/context recheck");
            }
        });
        check("native Cloud reconciliation never matches agent quotations or a matching client id with changed input",()=>{
            foreach(bool agent in new[]{false,true})using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>method!="thread/items/list"?null:(object)new{data=new[]{new{turnId="turn_fixture",item=new{id="quote",type=agent?"agentMessage":"userMessage",clientId="fixture_message",content=new[]{new{type="text",text=agent?Secret:"wrong"}}}}},nextCursor=(string)null};
                if(agent){var result=f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(Secret),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult();Assert(result.HistoryComplete&&result.Turn==null,"agent quoted prompt was trusted");}
                else{var error=Reject(()=>f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(Secret),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;Assert(error!=null&&error.Code=="reconciliation-conflict"&&!error.ToString().Contains(Secret),"client id conflict was ignored or leaked");}
                Assert(f.Transport.Count("turn/start")==0,"conflicting evidence sent work");
            }
        });
        check("native Cloud reconciliation accepts exact hash without legacy client id and rejects duplicated turns",()=>{
            foreach(bool duplicate in new[]{false,true})using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>{
                    if(method=="thread/turns/list")return new{data=(duplicate?new[]{"owned","duplicate"}:new[]{"owned"}).Select(id=>new{id=id,status="completed"}).ToArray(),nextCursor=(string)null};
                    if(method=="thread/items/list"){string turn=Json.Str(Json.Get(args,"turnId"));return new{data=new[]{new{turnId=turn,item=new{id="user_"+turn,type="userMessage",clientId=(string)null,content=new[]{new{type="text",text=Secret}}}}},nextCursor=(string)null};}return null;
                };
                if(duplicate)Assert(Reject(()=>f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(Secret),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult()) is CloudBackendException,"duplicate prompt was silently attached");
                else Assert(f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(Secret),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult().Evidence=="exact-prompt-sha256","legacy exact input was not recognized");
            }
        });
        check("native Cloud reconciliation keeps bounded incomplete history inconclusive",()=>{
            using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>method=="thread/items/list"?(object)new{data=new object[0],nextCursor=(string)null}:method!="thread/turns/list"?null:(object)new{data=new[]{new{id="page_"+f.Transport.Count("thread/turns/list"),status="completed"}},nextCursor="next_"+f.Transport.Count("thread/turns/list")};
                var result=f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(Secret),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult();Assert(!result.HistoryComplete&&result.Turn==null&&f.Transport.Count("thread/turns/list")==4&&f.Transport.Count("turn/start")==0,"bounded absent history authorized reconciliation or replay");
            }
        });
        check("native Cloud reconciliation paginates user input and rejects changed context",()=>{
            foreach(bool change in new[]{false,true})using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>{
                    if(method!="thread/items/list")return null;bool first=String.IsNullOrEmpty(Json.Str(Json.Get(args,"cursor")));
                    return first?new{data=new object[0],nextCursor="user-page"}:(object)new{data=new object[]{new{turnId="turn_fixture",item=new{id="user",type="userMessage",clientId="fixture_message",content=new[]{new{type="text",text=Secret}}}}},nextCursor=(string)null};
                };
                if(change)f.Transport.ReceiveHook=method=>{if(method=="thread/items/list")f.Transport.Environment="changed";};
                if(change)Reject(()=>f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(Secret),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult());
                else Assert(f.Backend.FindSubmittedTurn("thread_fixture",Config,"fixture_message",CloudTasksBackend.PromptSha256(Secret),DateTime.UtcNow,CancellationToken.None).GetAwaiter().GetResult().Turn.Id=="turn_fixture","user input on second page was missed");
            }
        });
        check("native Cloud proves preflight non-dispatch but preserves uncertainty after the transport send boundary",()=>{
            using(var f=new Fixture()){
                f.Transport.Approval=true;Assert(Reject(()=>f.Turn()) is CloudTaskNotDispatchedException&&f.Transport.Count("turn/start")==0,"preflight failure lacked transport proof");
            }
            using(var f=new Fixture()){
                f.Transport.CancelMethod="turn/start";var error=Reject(()=>f.Turn());Assert(error is CloudBackendUncertainException&&!(error is CloudTaskNotDispatchedException)&&f.Transport.Count("turn/start")==1,"a possibly sent RPC was falsely certified unsent");
            }
        });
        check("native Cloud final text paging recovers oversized tool pages without widening the frame limit",()=>{
            using(var f=new Fixture()){
                f.Transport.CustomReply=(method,args)=>{
                    if(method!="thread/items/list")return null;int limit=(int)Json.Number(Json.Get(args,"limit")).Value;string cursor=Json.Str(Json.Get(args,"cursor"));
                    if(limit>1&&cursor.Length==0)return new{data=new[]{new{turnId="turn_fixture",item=new{id="tool",type="functionCallOutput",output=new string('X',4194304)}}},nextCursor="older"};
                    if(cursor.Length==0)return new{data=new object[]{new{turnId="turn_fixture",item=new{id="final_two",type="agentMessage",phase="final",text="second",truncated=false}}},nextCursor="older"};
                    return new{data=new object[]{new{turnId="turn_fixture",item=new{id="final_one",type="agentMessage",phase="final_answer",text="first",truncated=false}}},nextCursor=(string)null};
                };
                var turn=f.Backend.ReadTurn("thread_fixture","turn_fixture",CancellationToken.None).GetAwaiter().GetResult();Assert(turn.TextComplete&&turn.Text=="first\n\nsecond"&&f.Transport.Count("thread/items/list")==3,"oversized page lost final fragments or did not retry readonly cursor1");
                Assert(f.Transport.Count("turn/start")==0&&f.Transport.Count("thread/resume")==0,"page recovery mutated the task");
            }
        });
        check("native Cloud readonly transport errors preserve the failed method without raw payload",()=>{
            using(var f=new Fixture()){
                f.Transport.TimeoutMethod="thread/items/list";var error=Reject(()=>f.Backend.ReadTurn("thread_fixture","turn_fixture",CancellationToken.None).GetAwaiter().GetResult()) as CloudBackendException;Assert(error!=null&&error.Code=="unavailable"&&error.Message.Contains("méthode=thread/items/list")&&error.Message.Contains("IOException")&&!error.ToString().Contains(Secret),"failed native stage was hidden or exposed raw data");
            }
        });
    }
}
