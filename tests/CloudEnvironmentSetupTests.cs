using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudEnvironmentSetupTests
{
    private const string Config="configuration_fixture~asenvcfg_setup";
    private const string Secret="PRIVATE_SETUP_SCRIPT_OR_AGENT_RESPONSE_NOT_FOR_JOURNAL";
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected setup refusal");}
    private static string Encode(object value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static AuthIdentity Identity(string account="account-a")
    {return new AuthIdentity{Key=account+":user-a",AccountId=account,AccessToken="e30."+Encode(new Dictionary<string,object>{{"exp",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600},{"https://api.openai.com/auth",new{chatgpt_account_id=account,chatgpt_user_id="user-a"}}})+".fixture"};}
    private sealed class Handler:HttpMessageHandler
    {
        internal Func<HttpRequestMessage,Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){return Reply(request);}
    }
    private sealed class Tasks:ICloudOnboardingConnection
    {
        internal readonly Fixture Owner;
        internal string Account="account-a:user-a",Environment=Config,Turn="setup-turn",Status="inProgress",CapturedPrompt,ClientId;
        internal bool Approval,Input,ThrowStart,ThrowTurn;
        internal int Starts,Turns,Reads;
        internal Tasks(Fixture owner){Owner=owner;}
        private CloudTaskThread Thread()
        {return new CloudTaskThread{Id="setup-thread",AccountKey=Account,VerifiedEnvironmentConfigId=Environment,Status=Status=="completed"?"idle":"active",AwaitingApproval=Approval,AwaitingInput=Input,LatestTurn=new CloudTaskTurn{Id=Turn,Status=Status,Text=Secret,TextComplete=true}};}
        public Task<CloudTaskThread> StartOnboarding(string configId,CancellationToken token)
        {Owner.AssertSending("creating-thread");Starts++;if(ThrowStart)throw new IOException(Secret);return Task.FromResult(Thread());}
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId,string prompt,string clientUserMessageId,IDictionary<string,string> names,CancellationToken token)
        {Owner.AssertSending("starting-setup");Turns++;CapturedPrompt=prompt;ClientId=clientUserMessageId;Assert(names.Count==1&&names["github/123"]=="fixture/lab","repository map changed");if(ThrowTurn)throw new IOException(Secret);return Task.FromResult(new CloudTaskTurn{Id="setup-turn",Status="inProgress"});}
        public Task<CloudTaskThread> Read(string threadId,CancellationToken token){Reads++;Assert(threadId=="setup-thread","wrong setup chat read");return Task.FromResult(Thread());}
        public void Dispose(){}
    }
    private sealed class Fixture:IDisposable
    {
        internal readonly string Root,Id=Guid.NewGuid().ToString("N");
        internal readonly CloudBackend Backend;
        private readonly HttpClient http;
        internal readonly Tasks Tasks;
        internal AuthIdentity Current=Identity();
        internal readonly IDictionary<string,string> Names=new Dictionary<string,string>{{"github/123","fixture/lab"}};
        internal readonly List<string> Requests=new List<string>();
        internal bool Draft,Published,ExistingThread,ChangedVersion,ChangedRepository,FinalNotReady,PublishBeforeComplete;
        internal string OperationState="RUNNING",TimeoutStage;
        internal int Bootstraps,Begins,Completes;
        internal Fixture(string root,int number)
        {Root=Path.Combine(root,"cloud-setup-"+number);Directory.CreateDirectory(Root);Tasks=new Tasks(this);var handler=new Handler{Reply=Reply};http=new HttpClient(handler);Backend=new CloudBackend(http,()=>Current,CloudBackend.Capture(Current));}
        internal string FilePath {get{return Path.Combine(Root,"cloud-setups-v1",Id+".json");}}
        internal object Record(){return Json.Read<object>(File.ReadAllText(FilePath));}
        internal void AssertSending(string stage){var record=Record();Assert(Json.Str(Json.Get(record,"State"))=="sending"&&Json.Str(Json.Get(record,"Stage"))==stage,"setup write lacks durable stage "+stage);}
        private object Configuration()
        {
            var repositories=new[]{new{repository_id=ChangedRepository?"github/other":"github/123",@ref="main",depth=1}};
            var config=new Dictionary<string,object>{{"id",Config},{"name","Fixture private setup"},{"is_owner",true},{"share_settings","private"},{"status",Published&&FinalNotReady?"starting":"ready"},{"version_id",Published?"published-version":ChangedVersion?"other-version":"initial-version"},{"version_revision",Published?2:1},{"latest_ready_version_id",Published&&!FinalNotReady?"published-version":null},{"repositories",repositories},{"network_policy",new{type="disabled"}},{"environment_id","environment_fixture"},{"thread_id",ExistingThread?"unrelated-existing-thread":Tasks.Starts>0?"setup-thread":null},{"install_script",Secret}};
            if(Draft&&!Published)config["draft"]=new{id="initial-draft",base_version_id="initial-version",revision=1,repositories=repositories,network_policy=new{type="disabled"},install_script=Secret};
            return config;
        }
        private Task<HttpResponseMessage> Reply(HttpRequestMessage request)
        {
            string path=request.RequestUri.AbsolutePath,method=request.Method.Method;Requests.Add(method+" "+path);object response;
            if(method!="GET"){
                string stage=Json.Str(Json.Get(Record(),"Stage"));AssertSending(stage);if(TimeoutStage==stage)throw new TaskCanceledException(Secret);
            }
            if(method=="GET"&&path=="/v1/environment-configs/"+Config)response=Configuration();
            else if(method=="PATCH"&&path.EndsWith("/draft",StringComparison.Ordinal)){Bootstraps++;Draft=true;response=Configuration();}
            else if(method=="POST"&&path.EndsWith("/draft/approve/begin",StringComparison.Ordinal)){Begins++;response=Operation();}
            else if(method=="GET"&&path=="/v1/environment-operations/setup-operation"){if(PublishBeforeComplete&&OperationState=="SUCCEEDED")Published=true;response=Operation();}
            else if(method=="POST"&&path.EndsWith("/draft/approve/complete",StringComparison.Ordinal)){Completes++;Published=true;response=Configuration();}
            else throw new InvalidOperationException("Unexpected setup request "+method+" "+path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Json.Write(response),Encoding.UTF8,"application/json")});
        }
        private object Operation(){return new{id="setup-operation",kind="APPROVE_ENVIRONMENT_CONFIG_DRAFT",environment_id="environment_fixture",state=OperationState,failure=OperationState=="FAILED"?new{code="BOOT_FAILED",message=Secret}:null};}
        internal CloudEnvironmentSetupResult Start(IDictionary<string,string> names=null){return CloudEnvironmentSetup.Start(Root,"instance-a","account-a:user-a",Backend,Tasks,Config,names??Names,Id,CancellationToken.None).GetAwaiter().GetResult();}
        internal CloudEnvironmentSetupResult Follow(string account="account-a:user-a"){return CloudEnvironmentSetup.Follow(Root,"instance-a",account,Backend,Tasks,Id,CancellationToken.None).GetAwaiter().GetResult();}
        internal CloudEnvironmentSetupResult Publish(){return CloudEnvironmentSetup.Publish(Root,"instance-a","account-a:user-a",Backend,Tasks,Id,CancellationToken.None).GetAwaiter().GetResult();}
        public void Dispose(){Backend.Dispose();http.Dispose();}
    }
    public static void RunAll(Action<string,Action> check,string root)
    {
        int number=0;
        check("cloud setup starts one native preparation with a fixed bounded prompt and stable message ID",()=>{
            using(var f=new Fixture(root,++number)){
                var result=f.Start();Assert(result.State=="running"&&f.Tasks.Starts==1&&f.Tasks.Turns==1&&f.Tasks.ClientId==f.Id,"setup not correlated once");
                Assert(f.Tasks.CapturedPrompt.Contains("Do not edit repository files")&&f.Tasks.CapturedPrompt.Contains("publish")&&f.Begins==0,"setup prompt omitted separate publication boundary");
                Assert(f.Start().State=="running"&&f.Tasks.Starts==1&&f.Tasks.Turns==1,"setup retry created a second thread or turn");
            }
        });
        check("cloud setup refuses already published, prepared or edited environments before creating a thread",()=>{
            foreach(var state in new[]{"published","thread","draft"})using(var f=new Fixture(root,++number)){
                f.Published=state=="published";f.ExistingThread=state=="thread";f.Draft=state=="draft";Reject(()=>f.Start());Assert(f.Tasks.Starts==0&&f.Tasks.Turns==0,"existing environment reused for initial setup");
            }
        });
        check("cloud setup verifies exact repository map and rejects a changed idempotent request",()=>{
            using(var f=new Fixture(root,++number)){
                Reject(()=>f.Start(new Dictionary<string,string>{{"github/other","fixture/lab"}}));Assert(f.Tasks.Starts==0,"repository mismatch started setup");
            }
            using(var f=new Fixture(root,++number)){
                f.Start();Reject(()=>f.Start(new Dictionary<string,string>{{"github/123","fixture/other"}}));Assert(f.Tasks.Starts==1&&f.Tasks.Turns==1,"changed request reused operation id");
            }
        });
        check("cloud setup never sends preparation to a different account or unconfirmed environment",()=>{
            foreach(var reason in new[]{"account","missing-account","environment"})using(var f=new Fixture(root,++number)){
                if(reason=="account")f.Tasks.Account="other-account";if(reason=="missing-account")f.Tasks.Account=null;if(reason=="environment")f.Tasks.Environment="other-config";
                Assert(Reject(()=>f.Start()) is CloudBackendUncertainException&&f.Tasks.Turns==0,"setup prompt sent without account and environment proof");
            }
        });
        check("cloud setup follows attention without granting native approvals or publishing automatically",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Approval=true;Assert(f.Follow().State=="awaiting-approval","native approval hidden");
                f.Tasks.Approval=false;f.Tasks.Input=true;Assert(f.Follow().State=="awaiting-input","native user input hidden");
                f.Tasks.Input=false;f.Tasks.Status="completed";Assert(f.Follow().State=="ready-to-publish"&&f.Bootstraps==0&&f.Begins==0&&f.Tasks.Turns==1,"preparation silently published or resent");
            }
        });
        check("cloud setup rejects an unrelated or failed preparation turn before publication",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Tasks.Turn="other-turn";Assert(f.Follow().State=="uncertain","unrelated completion accepted");Reject(()=>f.Publish());Assert(f.Begins==0,"unrelated turn published environment");
            }
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="failed";Assert(f.Follow().State=="failed","failed setup declared ready");Reject(()=>f.Publish());Assert(f.Begins==0,"failed setup published environment");
            }
        });
        check("cloud setup publication bootstraps its initial draft and follows the verified new version",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";Assert(f.Publish().State=="publishing"&&f.Bootstraps==1&&f.Begins==1&&f.Completes==0,"initial draft or approval did not start exactly once");
                f.OperationState="SUCCEEDED";Assert(f.Follow().State=="completed"&&f.Completes==1,"publication not verified");
                f.Follow();Assert(f.Bootstraps==1&&f.Begins==1&&f.Completes==1&&f.Tasks.Starts==1&&f.Tasks.Turns==1,"completed setup replayed a mutation");
            }
        });
        check("cloud setup refuses a version or repository changed after preparation",()=>{
            foreach(var change in new[]{"version","repository"})using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.ChangedVersion=change=="version";f.ChangedRepository=change=="repository";
                Reject(()=>f.Publish());Assert(f.Bootstraps==0&&f.Begins==0,"changed initial configuration overwritten");
            }
        });
        check("cloud setup never replays uncertain thread creation or preparation submission",()=>{
            foreach(var step in new[]{"thread","turn"})using(var f=new Fixture(root,++number)){
                f.Tasks.ThrowStart=step=="thread";f.Tasks.ThrowTurn=step=="turn";Reject(()=>f.Start());int starts=f.Tasks.Starts,turns=f.Tasks.Turns;
                Assert(f.Follow().State=="uncertain"&&f.Start().State=="uncertain"&&f.Tasks.Starts==starts&&f.Tasks.Turns==turns,"uncertain setup resent");
            }
        });
        check("cloud setup keeps script bodies, prompts and agent responses out of its journal",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Publish();string json=File.ReadAllText(f.FilePath);
                Assert(!json.Contains(Secret)&&!json.Contains("install_script")&&!json.Contains("InstallScript")&&!json.Contains("Prepare this private Cloud environment"),"private setup content persisted");
            }
        });
        check("cloud setup rejects a different caller account before any follow-up",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();int reads=f.Tasks.Reads,requests=f.Requests.Count;Reject(()=>f.Follow("other-account"));Assert(f.Tasks.Reads==reads&&f.Requests.Count==requests,"wrong-account follow contacted backend");
            }
        });
        check("cloud setup resumes verification read-only and does not claim an unready environment completed",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Publish();f.OperationState="SUCCEEDED";f.FinalNotReady=true;Assert(f.Follow().State=="verifying"&&f.Completes==1,"unready publication declared complete");
                int requests=f.Requests.Count;f.FinalNotReady=false;Assert(f.Follow().State=="completed"&&f.Completes==1&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET ",StringComparison.Ordinal)),"verification resent completion");
            }
        });
        check("cloud setup retains publication failures and uncertain writes without resubmission",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Publish();f.OperationState="FAILED";Assert(f.Follow().State=="failed"&&f.Completes==0,"failed approval completed");
            }
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.TimeoutStage="bootstrapping-draft";Assert(Reject(()=>f.Publish()) is CloudBackendUncertainException,"bootstrap timeout not uncertain");
                int requests=f.Requests.Count;Assert(f.Follow().State=="uncertain"&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET ",StringComparison.Ordinal))&&f.Begins==0,"uncertain bootstrap replayed");
            }
        });
        check("cloud setup acknowledges an initial version committed before approval completion",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Publish();f.PublishBeforeComplete=true;f.OperationState="SUCCEEDED";
                var result=f.Follow();Assert(result.State=="completed"&&result.Configuration.VersionId=="published-version"&&result.Configuration.Draft==null&&f.Bootstraps==1&&f.Begins==1&&f.Completes==1,"already committed initial version rejected");
                f.Follow();Assert(f.Completes==1&&f.Tasks.Starts==1&&f.Tasks.Turns==1,"initial publication or setup replayed");
            }
        });
        check("cloud setup recovers the precise historical approval conflict without beginning again",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Publish();var record=Json.Obj(f.Record());record["State"]="failed";record["Stage"]="approval-pending";record["FailureCode"]="conflict";record["HttpStatus"]=409;File.WriteAllText(f.FilePath,Json.Write(record));
                f.PublishBeforeComplete=true;f.OperationState="SUCCEEDED";var result=f.Follow();
                Assert(result.State=="completed"&&result.FailureCode==null&&f.Bootstraps==1&&f.Begins==1&&f.Completes==1&&f.Tasks.Turns==1,"historical setup conflict caused a new setup or approval");
            }
        });
        check("cloud setup does not revive a different failed preparation or publication stage",()=>{
            using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Publish();var record=Json.Obj(f.Record());record["State"]="failed";record["Stage"]="bootstrapping-draft";record["FailureCode"]="conflict";File.WriteAllText(f.FilePath,Json.Write(record));
                f.PublishBeforeComplete=true;f.OperationState="SUCCEEDED";int requests=f.Requests.Count;var result=f.Follow();
                Assert(result.State=="failed"&&f.Completes==0&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET /v1/environment-configs/",StringComparison.Ordinal)),"unrelated failed setup resumed publication");
            }
        });
        check("cloud setup reconciles an exact legacy repository receipt without accepting a changed scope",()=>{
            foreach(bool changed in new[]{false,true})using(var f=new Fixture(root,++number)){
                f.Start();f.Tasks.Status="completed";f.Publish();var record=Json.Obj(f.Record());record["State"]="failed";record["Stage"]="approval-pending";record["FailureCode"]="conflict";
                var repositories=new List<CloudRepository>{new CloudRepository{RepositoryId="github/123",Ref="main",Depth=1}};
                using(var sha=SHA256.Create())record["RepositoriesFingerprint"]=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(repositories)))).Replace("-","").ToLowerInvariant();
                File.WriteAllText(f.FilePath,Json.Write(record));f.PublishBeforeComplete=true;f.OperationState="SUCCEEDED";f.ChangedRepository=changed;
                if(changed){Reject(()=>f.Follow());Assert(f.Completes==0,"legacy hash accepted a different repository scope");}
                else Assert(f.Follow().State=="completed"&&f.Completes==1&&f.Bootstraps==1&&f.Begins==1,"exact legacy receipt was not reconciled safely");
            }
        });
    }
}
