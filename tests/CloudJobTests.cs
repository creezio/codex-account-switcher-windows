using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudJobTests
{
    private const string ConfigId="fixture_environment~asenvcfg_test";
    private const string Secret="PRIVATE_CLOUD_JOB_PROMPT_MUST_STAY_ENCRYPTED";
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected cloud job rejection");}
    private sealed class Fake:ICloudTasksConnection,ICloudTasksExecutionConnection,ICloudTaskTurnConnection,ICloudTaskReconciliationConnection
    {
        internal string Account,Config=ConfigId,ThreadId="fixture-cloud-chat",TurnId="fixture-cloud-turn",LatestId;
        internal string Prompt,ClientMessageId,Final="Mission verified",ReadStatus;
        internal bool Complete,TextComplete=true,Busy,Approval,Input,ThrowStart,ThrowTurn,ThrowRead;
        internal int Connects,Starts,Reads,Turns;
        internal int TargetedReads;
        internal CloudTaskTurn TargetedTurn;
        internal int ConfiguredStarts,ConfiguredTurns,ExecutionReads;
        internal int ConfigureCalls;
        internal bool ThrowConfigure;
        internal CloudTaskExecutionOptions StartOptions,TurnOptions;
        internal CloudTaskExecutionOptions ConfigureOptions;
        internal CloudTaskExecutionObservation AfterConfigure;
        internal Action ConfigureHook;
        internal CloudTaskExecutionObservation Observation=new CloudTaskExecutionObservation{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access",ApprovalPolicy="never",ApprovalsReviewer="user",Verified=true};
        internal Action ReadHook;
        internal Action StartTurnHook;
        internal Action ExecutionReadHook;
        internal bool HistoryComplete=true,WrongFindBinding;
        internal int Finds;
        internal Action FindHook;
        public Task<CloudTaskReconciliation> FindSubmittedTurn(string threadId,string config,string clientId,string hash,DateTime attempted,CancellationToken token){Finds++;if(FindHook!=null)FindHook();bool matched=Turns>0&&Prompt!=null&&ClientMessageId==clientId&&CloudTasksBackend.PromptSha256(Prompt)==hash;return Task.FromResult(new CloudTaskReconciliation{AccountKey=WrongFindBinding?"other-account":Account,ThreadId=threadId,ConfigurationId=Config,HistoryComplete=HistoryComplete,Turn=matched&&HistoryComplete?new CloudTaskTurn{Id=TurnId,Status=Complete?"completed":"inProgress"}:null,Evidence=matched?"client-id-and-prompt-sha256":null});}
        public Task Connect(CancellationToken token){Connects++;return Task.FromResult(0);}
        public Task<CloudTaskPage> List(string cursor,int limit,CancellationToken token){return Task.FromResult(new CloudTaskPage{Data=new List<CloudTaskThread>()});}
        public Task<CloudTaskThread> Start(string configId,string cwd,CancellationToken token){Starts++;if(ThrowStart)throw new IOException(Secret);return Task.FromResult(Thread());}
        public Task<CloudTaskThread> Start(string configId,string cwd,CloudTaskExecutionOptions options,CancellationToken token){ConfiguredStarts++;StartOptions=CloudTaskExecutionOptions.Validate(options);return Start(configId,cwd,token);}
        public Task<CloudTaskTurn> StartTurn(string threadId,string prompt,string clientUserMessageId,CloudTaskExecutionOptions options,CancellationToken token){ConfiguredTurns++;TurnOptions=CloudTaskExecutionOptions.Validate(options);return StartTurn(threadId,prompt,clientUserMessageId,token);}
        public Task<CloudTaskExecutionObservation> ReadExecution(string threadId,CancellationToken token){ExecutionReads++;if(ExecutionReadHook!=null)ExecutionReadHook();return Task.FromResult(Observation);}
        public Task<CloudTaskExecutionObservation> ConfigureExecution(string threadId,CloudTaskExecutionOptions options,CancellationToken token){ConfigureCalls++;ConfigureOptions=CloudTaskExecutionOptions.Validate(options);if(Busy||Approval||Input||Turns>0)throw new InvalidOperationException("Not a fresh empty thread");if(ConfigureHook!=null)ConfigureHook();if(AfterConfigure!=null)Observation=AfterConfigure;if(ThrowConfigure)throw new IOException(Secret);return Task.FromResult(Observation);}
        public Task<CloudTaskThread> StartOnboarding(string configId,CancellationToken token){throw new InvalidOperationException("Cloud jobs must never start environment onboarding");}
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId,string prompt,CancellationToken token){throw new InvalidOperationException("Cloud jobs must never send environment onboarding prompts");}
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId,string prompt,string clientUserMessageId,CancellationToken token){throw new InvalidOperationException("Cloud jobs must never send environment onboarding prompts");}
        private CloudTaskThread Thread()
        {
            return new CloudTaskThread{Id=ThreadId,AccountKey=Account,VerifiedEnvironmentConfigId=Config,Status=ReadStatus??(Busy||Turns>0&&!Complete?"active":"idle"),AwaitingApproval=Approval,AwaitingInput=Input,LatestTurn=Turns==0?null:new CloudTaskTurn{Id=LatestId??TurnId,Status=Complete?"completed":"inProgress",Text=Complete?Final:"intermediate commentary",TextComplete=TextComplete}};
        }
        public Task<CloudTaskThread> Read(string threadId,CancellationToken token){Reads++;Assert(!String.IsNullOrEmpty(threadId),"attempted to read a null cloud chat");if(ReadHook!=null)ReadHook();if(ThrowRead)throw new InvalidOperationException(Secret);return Task.FromResult(Thread());}
        public Task<CloudTaskTurn> ReadTurn(string threadId,string turnId,CancellationToken token){TargetedReads++;Assert(threadId==ThreadId,"targeted read changed its chat");return Task.FromResult(TargetedTurn!=null&&TargetedTurn.Id==turnId?TargetedTurn:null);}
        public Task<CloudTaskTurn> StartTurn(string threadId,string prompt,CancellationToken token){return StartTurn(threadId,prompt,null,token);}
        public Task<CloudTaskTurn> StartTurn(string threadId,string prompt,string clientUserMessageId,CancellationToken token){if(StartTurnHook!=null)StartTurnHook();Turns++;Prompt=prompt;ClientMessageId=clientUserMessageId;if(ThrowTurn)throw new IOException(Secret);return Task.FromResult(new CloudTaskTurn{Id=TurnId,Status="inProgress"});}
        public void Dispose(){}
    }
    private sealed class LegacyConnection:ICloudTasksConnection
    {
        private readonly Fake inner;internal LegacyConnection(Fake inner){this.inner=inner;}
        public void Dispose(){}
        public Task Connect(CancellationToken token){return inner.Connect(token);}
        public Task<CloudTaskPage> List(string cursor,int limit,CancellationToken token){return inner.List(cursor,limit,token);}
        public Task<CloudTaskThread> Start(string configId,string cwd,CancellationToken token){return inner.Start(configId,cwd,token);}
        public Task<CloudTaskThread> StartOnboarding(string configId,CancellationToken token){return inner.StartOnboarding(configId,token);}
        public Task<CloudTaskThread> Read(string id,CancellationToken token){return inner.Read(id,token);}
        public Task<CloudTaskTurn> StartTurn(string id,string prompt,CancellationToken token){return inner.StartTurn(id,prompt,token);}
        public Task<CloudTaskTurn> StartTurn(string id,string prompt,string clientId,CancellationToken token){return inner.StartTurn(id,prompt,clientId,token);}
        public Task<CloudTaskTurn> StartOnboardingTurn(string id,string prompt,CancellationToken token){return inner.StartOnboardingTurn(id,prompt,token);}
        public Task<CloudTaskTurn> StartOnboardingTurn(string id,string prompt,string clientId,CancellationToken token){return inner.StartOnboardingTurn(id,prompt,clientId,token);}
    }
    private sealed class Fixture
    {
        internal readonly AccountService Accounts;
        internal readonly DesktopInstance Instance;
        internal readonly RelayStore Store;
        internal readonly RelaySession Source;
        internal readonly Fake Client=new Fake();
        internal readonly CloudJobs Jobs;
        internal CloudConfiguration Configuration;
        internal int ConfigurationReads;
        internal Fixture(string root,int number,string auth,Func<DateTime> utcNow=null)
        {
            Accounts=new AccountService(Path.Combine(root,"cloud-jobs-"+number));var profile=Accounts.Import(auth,"Fixture");Instance=Accounts.Instances.Create("Cloud fixture");Accounts.Instances.BindAccount(Instance,profile);
            string sourceHome=Path.Combine(Accounts.Vault.Root,"source-home");Directory.CreateDirectory(sourceHome);File.WriteAllText(Path.Combine(sourceHome,"auth.json"),auth);string sourceAccount=AuthIdentity.Parse(auth).Key;
            Store=new RelayStore(Path.Combine(Accounts.Vault.Root,"relay"));Store.Register(new RelayChannel{Id="source",Name="Source",AccountKey=sourceAccount,Home=sourceHome,Workspace=Accounts.Vault.Root,Enabled=true});
            Source=new RelaySession{Channel="source",Thread="source-chat",Account=sourceAccount,Home=sourceHome};Client.Account=Instance.AccountKey;
            Configuration=new CloudConfiguration{Id=ConfigId,VersionId="published-version",LatestReadyVersionId="published-version",VersionRevision=2,Status="ready"};
            Jobs=new CloudJobs(Store,i=>Client,(i,id,t)=>{ConfigurationReads++;return Task.FromResult(Configuration);},utcNow);
        }
        internal CloudJobSpec Spec(bool returns=true){return new CloudJobSpec{Id=Guid.NewGuid().ToString("N"),Instance=Instance.Id,ConfigurationId=ConfigId,Title="Fixture mission",Prompt=Secret,ExplicitDelegation=true,ReturnToSource=returns};}
        internal CloudJob Submit(CloudJobSpec spec=null){return Jobs.Submit(spec??Spec(),Source,CancellationToken.None).GetAwaiter().GetResult();}
        internal CloudJob Advance(string id){return Jobs.Advance(id,CancellationToken.None).GetAwaiter().GetResult();}
        internal void Save(CloudJob job){Store.WriteRecord((job.Kind=="follow-up"?"cloud-follow-up-":"cloud-job-")+job.Id+".dpapi",job);}
        internal CloudJob CompletedParent()
        {var spec=Spec();spec.Model="gpt-6.1-sol";spec.ReasoningEffort="ultra";spec.PermissionProfile=":danger-full-access";var parent=Submit(spec);Advance(parent.Id);Client.Complete=true;return Advance(parent.Id);}
        internal CloudJobFollowUpSpec FollowUpSpec(CloudJob parent)
        {return new CloudJobFollowUpSpec{Id=Guid.NewGuid().ToString("N"),ParentId=parent.Id,Title="Correction fixture",Prompt=Secret,ExplicitDelegation=true};}
        internal CloudJob FollowUp(CloudJobFollowUpSpec spec)
        {return Jobs.FollowUp(spec,Source,CancellationToken.None).GetAwaiter().GetResult();}
        internal void NewTurn(string id)
        {Client.StartTurnHook=()=>{Client.TurnId=id;Client.Complete=false;};}
    }
    private sealed class SourceStateTransport:IRelayTransport
    {
        internal string Flag,TurnStatus="completed";
        internal int Sends;
        public void Verify(RelayChannel channel){}
        public void VerifySend(RelayChannel channel,string thread){}
        public Task<object> Call(RelayChannel channel,string tool,object args,CancellationToken token)
        {
            if(tool=="read_thread")return Task.FromResult(Json.Read<object>(Json.Write(new{thread=new{id="source-chat",status=new{type="idle",activeFlags=Flag==null?new string[0]:new[]{Flag}}},turns=new[]{new{id=Sends>0?"source-return":"source-turn",status=TurnStatus}}})));
            Assert(tool=="send_message_to_thread","unexpected source operation");Sends++;return Task.FromResult<object>(new{turnId="source-return"});
        }
    }
    private sealed class SchedulerReturnTransport:IRelayTransport
    {
        internal readonly HashSet<string> Busy=new HashSet<string>();
        internal readonly Dictionary<string,int> Sends=new Dictionary<string,int>();
        public void Verify(RelayChannel channel){}
        public void VerifySend(RelayChannel channel,string thread){}
        public Task<object> Call(RelayChannel channel,string tool,object args,CancellationToken token)
        {
            string thread=Json.Str(Json.Get(Json.Read<object>(Json.Write(args)),"threadId"));
            if(tool=="read_thread")return Task.FromResult(Json.Read<object>(Json.Write(new{thread=new{id=thread,status=new{type=Busy.Contains(thread)?"active":"idle"}},turns=new[]{new{id="source-turn",status=Busy.Contains(thread)?"inProgress":"completed"}}})));
            Assert(tool=="send_message_to_thread","unexpected scheduler source operation");if(!Sends.ContainsKey(thread))Sends[thread]=0;Sends[thread]++;return Task.FromResult<object>(new{turnId="source-return"});
        }
    }
    private sealed class NotificationTransport:IRelayTransport
    {
        internal string Status="idle",Flag,Prompt="";
        internal string WrapperType,WrapperNamespace="codex_app",WrapperName="send_message_to_thread";
        internal bool QuotedWrapper;
        internal bool Offline,WrongThread,LostAcknowledgement,DropMessage,AgentOnlyMarker;
        internal bool RefusePermissions;
        internal int Sends,Reads;
        public void Verify(RelayChannel channel){if(Offline)throw new IOException(Secret);}
        public void VerifySend(RelayChannel channel,string thread){Verify(channel);if(RefusePermissions)throw new RelayPermissionException(Secret);}
        public Task<object> Call(RelayChannel channel,string tool,object args,CancellationToken token)
        {
            Verify(channel);var input=Json.Read<object>(Json.Write(args));string thread=Json.Str(Json.Get(input,"threadId"));
            if(tool=="read_thread"){
                Reads++;object[] items=DropMessage?new object[0]:AgentOnlyMarker?new object[]{new{type="agentMessage",phase="final",text=Prompt}}:new object[]{new{type="userMessage",content=new[]{new{type="text",text=Prompt}}}};
                if(WrapperType!=null)items=new object[]{new{type=WrapperType,@namespace=WrapperNamespace,name=WrapperName,output=new{text="<input>"+(QuotedWrapper?"quoted ":"")+Prompt+"</input>"}}};
                return Task.FromResult(Json.Read<object>(Json.Write(new{thread=new{id=WrongThread?"another-chat":thread,status=new{type=Status,activeFlags=Flag==null?new string[0]:new[]{Flag}}},turns=new[]{new{id="source-notification-turn",status=Status=="active"?"inProgress":"completed",items=items}},page=new{hasMore=true}})));
            }
            Assert(tool=="send_message_to_thread","unexpected notification operation");Sends++;Prompt=Json.Str(Json.Get(input,"prompt"));if(LostAcknowledgement)throw new IOException(Secret);return Task.FromResult<object>(new{threadId=thread,turnId="source-notification-turn"});
        }
    }
    public static void RunAll(Action<string,Action> check,string root,string auth)
    {
        // Reuse the same test root without erasing a previous run's fixtures.
        int number=(Directory.Exists(root)?Directory.GetDirectories(root,"cloud-jobs-*"):new string[0]).Select(path=>{int value;return Int32.TryParse(Path.GetFileName(path).Substring("cloud-jobs-".Length),out value)?value:0;}).DefaultIfEmpty(0).Max();
        check("cloud jobs require explicit delegation and a published ready configuration",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.ExplicitDelegation=false;Reject(()=>f.Submit(spec));
            f.Configuration.VersionRevision=1;Reject(()=>f.Submit());f.Configuration.VersionRevision=2;f.Configuration.Draft=new CloudConfigurationDraft();Reject(()=>f.Submit());
            f.Configuration.Draft=null;f.Configuration.Id="other-configuration";Reject(()=>f.Submit());
            Assert(f.Client.Starts==0&&f.Client.Turns==0&&f.Jobs.List().Length==0,"invalid job was admitted or dispatched");
        });
        check("cloud jobs encrypt prompts and expose metadata without prompt bodies",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();string bytes=Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,"cloud-job-"+job.Id+".dpapi")));
            Assert(!bytes.Contains(Secret)&&f.Jobs.Get(job.Id).Prompt==Secret,"cloud prompt not encrypted");
            Assert(!Json.Write(CloudJobs.Summary(job,false)).Contains(Secret)&&job.Branch=="codex/alice/fixture-mission/lot-001"&&!job.Branch.Contains(job.Id),"metadata leaks prompt or branch not readable");
        });
        check("cloud execution preserves native defaults when this user has no saved preference",()=>{
            var f=new Fixture(root,++number,auth);var defaults=CloudMissionPreferences.Read(f.Store);var job=f.Submit();f.Advance(job.Id);
            Assert(!defaults.Required&&job.Model==null&&job.ReasoningEffort==null&&job.PermissionProfile==null&&f.Client.ConfiguredStarts==0&&f.Client.ConfiguredTurns==0&&f.Client.ExecutionReads==0,"global defaults elevated an unconfigured user");
        });
        check("cloud execution freezes user preferences and verifies their native application before the prompt",()=>{
            var f=new Fixture(root,++number,auth);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"});var job=f.Submit();
            Assert(job.Model=="gpt-6.1-sol"&&job.ReasoningEffort=="ultra"&&job.PermissionProfile==":danger-full-access"&&job.Fingerprint.StartsWith("v3:"),"preferences not frozen in admission");job=f.Advance(job.Id);
            Assert(f.Client.ConfiguredStarts==1&&f.Client.ConfiguredTurns==1&&f.Client.ExecutionReads==2&&f.Client.ConfigureCalls==0&&job.ExecutionSetupState=="not-needed"&&f.Client.TurnOptions.ReasoningEffort=="ultra"&&job.ExecutionState=="verified"&&job.ObservedExecution.ApprovalPolicy=="never","requested settings not applied or native observation not retained");
        });
        check("cloud execution explicit options override this user's defaults",()=>{
            var f=new Fixture(root,++number,auth);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"});var spec=f.Spec();spec.Model="gpt-6-luna";spec.ReasoningEffort="high";spec.PermissionProfile="inherit";var job=f.Submit(spec);
            Assert(job.Model=="gpt-6-luna"&&job.ReasoningEffort=="high"&&job.PermissionProfile==null,"explicit execution settings did not override preferences");
        });
        check("cloud execution retries reuse the admitted snapshot after user defaults change",()=>{
            var f=new Fixture(root,++number,auth);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"});var spec=f.Spec();var job=f.Submit(spec);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6-luna",ReasoningEffort="low"});var retry=f.Submit(spec);
            Assert(retry.Id==job.Id&&retry.Model=="gpt-6.1-sol"&&retry.ReasoningEffort=="ultra"&&retry.PermissionProfile==":danger-full-access"&&f.Jobs.List().Length==1,"changed preferences silently changed an admitted task");
            spec.ReasoningEffort="low";Reject(()=>f.Submit(spec));
        });
        check("cloud execution orphan reservations also preserve preferences across a crash",()=>{
            var f=new Fixture(root,++number,auth);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra"});var spec=f.Spec();var job=f.Submit(spec);File.Delete(Path.Combine(f.Store.Root,"cloud-job-"+job.Id+".dpapi"));CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6-luna",ReasoningEffort="low"});var retry=f.Submit(spec);Assert(retry.Model=="gpt-6.1-sol"&&retry.ReasoningEffort=="ultra"&&retry.Branch==job.Branch,"orphan reservation reinterpreted new defaults");
        });
        check("cloud execution rejects unsafe model effort and profile identifiers before admission",()=>{
            foreach(string field in new[]{"model","effort","profile"}){var f=new Fixture(root,++number,auth);var spec=f.Spec();if(field=="model")spec.Model="model\nother";if(field=="effort")spec.ReasoningEffort="unbounded";if(field=="profile")spec.PermissionProfile="../../full";Reject(()=>f.Submit(spec));Assert(f.Jobs.List().Length==0&&f.Client.Starts==0,"invalid execution setting admitted");}
        });
        check("cloud execution blocks unknown or mismatched native settings without sending the business prompt",()=>{
            foreach(string kind in new[]{"unknown","model","effort","profile"}){var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";spec.ReasoningEffort="ultra";spec.PermissionProfile=":danger-full-access";var job=f.Submit(spec);if(kind=="unknown"){f.Client.Observation.Verified=false;f.Client.Observation.ReasoningEffort=null;}if(kind=="model")f.Client.Observation.Model="gpt-6-luna";if(kind=="effort")f.Client.Observation.ReasoningEffort="low";if(kind=="profile")f.Client.Observation.PermissionProfile=":workspace-write";job=f.Advance(job.Id);Assert(job.State=="attention"&&job.ExecutionState==(kind=="unknown"?"unknown":"mismatch")&&f.Client.Turns==0&&job.ObservedExecution!=null,"unconfirmed settings received a business prompt or were called verified");}
        });
        check("cloud execution journals one explicit setup before configuring its own virgin chat",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";spec.ReasoningEffort="ultra";spec.PermissionProfile=":danger-full-access";var job=f.Submit(spec);f.Client.Observation=new CloudTaskExecutionObservation{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":workspace-write",ApprovalPolicy="on-request",ApprovalsReviewer="auto_review",Verified=true};f.Client.AfterConfigure=new CloudTaskExecutionObservation{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access",ApprovalPolicy="never",ApprovalsReviewer="user",Verified=true};
            f.Client.ConfigureHook=()=>{var persisted=f.Jobs.Get(job.Id);Assert(persisted.ExecutionSetupState=="applying"&&persisted.ExecutionSetupThreadId==f.Client.ThreadId&&f.Client.Turns==0,"native configuration was not journaled before mutation");};
            job=f.Advance(job.Id);Assert(job.State=="running"&&job.ExecutionState=="verified"&&job.ExecutionSetupState=="verified"&&f.Client.ConfigureCalls==1&&f.Client.ExecutionReads==3&&f.Client.Turns==1&&f.Client.ConfigureOptions.PermissionProfile==spec.PermissionProfile,"first prompt did not wait for single setup and persisted readback");
            f.Advance(job.Id);Assert(f.Client.ConfigureCalls==1&&f.Client.Turns==1,"poll reapplied settings or resent mission");
        });
        check("cloud execution never repeats a setup whose response was lost",()=>{
            foreach(bool applied in new[]{false,true}){var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";spec.ReasoningEffort="ultra";spec.PermissionProfile=":danger-full-access";var job=f.Submit(spec);f.Client.Observation=new CloudTaskExecutionObservation{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":workspace-write",Verified=true};if(applied)f.Client.AfterConfigure=new CloudTaskExecutionObservation{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access",ApprovalPolicy="never",ApprovalsReviewer="user",Verified=true};f.Client.ThrowConfigure=true;
                job=f.Advance(job.Id);Assert(job.State=="attention"&&job.ExecutionSetupState=="uncertain"&&f.Client.ConfigureCalls==1&&f.Client.Turns==0,"uncertain setup dispatched work");f.Client.ThrowConfigure=false;job=f.Advance(job.Id);Assert(f.Client.ConfigureCalls==1&&f.Client.Turns==(applied?1:0)&&job.State==(applied?"running":"attention"),"uncertain setup was replayed or not reconciled from native read");
            }
        });
        check("cloud execution reconciles a durable applying journal only through native readback",()=>{
            foreach(bool matches in new[]{false,true}){var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";var job=f.Submit(spec);job.State="prepared";job.ThreadId=f.Client.ThreadId;job.ExecutionSetupThreadId=job.ThreadId;job.ExecutionSetupState="applying";f.Save(job);if(!matches)f.Client.Observation.Model="gpt-6-luna";job=f.Advance(job.Id);Assert(f.Client.ConfigureCalls==0&&f.Client.Starts==0&&f.Client.Turns==(matches?1:0)&&job.State==(matches?"running":"attention"),"restart repeated a configuration mutation");}
        });
        check("cloud execution refuses to configure a used chat or a pending native intervention",()=>{
            foreach(string kind in new[]{"history","approval","input","busy"}){var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";var job=f.Submit(spec);f.Client.Observation.Model="gpt-6-luna";if(kind=="history"){f.Client.Turns=1;f.Client.Complete=true;}if(kind=="approval")f.Client.Approval=true;if(kind=="input")f.Client.Input=true;if(kind=="busy")f.Client.Busy=true;int existingTurns=f.Client.Turns;job=f.Advance(job.Id);Assert(f.Client.ConfigureCalls==0&&f.Client.Turns==existingTurns&&job.State!="running","used or pending chat had its execution settings modified");}
        });
        check("cloud execution rejects setup journal thread substitutions before reading or configuring",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";var job=f.Submit(spec);job.State="prepared";job.ThreadId=f.Client.ThreadId;job.ExecutionSetupState="applying";job.ExecutionSetupThreadId="another-thread";f.Save(job);job=f.Advance(job.Id);Assert(job.State=="attention"&&f.Client.ExecutionReads==0&&f.Client.ConfigureCalls==0&&f.Client.Turns==0,"setup journal was reused for a different chat");
        });
        check("cloud execution refuses an adapter that cannot apply required settings",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";var job=f.Submit(spec);var legacy=new CloudJobs(f.Store,i=>new LegacyConnection(f.Client),(i,id,t)=>Task.FromResult(f.Configuration));var result=legacy.Advance(job.Id,CancellationToken.None).GetAwaiter().GetResult();Assert(result.State=="attention"&&f.Client.Starts==0&&f.Client.Connects==0,"unsupported adapter silently used its defaults");
        });
        check("cloud execution refuses admission while a live older worker would ignore the options",()=>{
            var f=new Fixture(root,++number,auth);using(var current=Process.GetCurrentProcess()){
                f.Store.WriteRecord("cloud-worker-v1.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,Version="old"});var spec=f.Spec();spec.Model="gpt-6.1-sol";Reject(()=>f.Submit(spec));Assert(f.Jobs.List().Length==0&&f.Client.Starts==0,"old worker could receive an unsupported execution request");
                f.Store.WriteRecord("cloud-worker-v1.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,Version="new",ExecutionVersion=CloudJobWorker.ExecutionVersion});Assert(f.Submit(spec).Model=="gpt-6.1-sol","capable live worker incorrectly blocked");
            }
        });
        check("cloud execution blocks legacy queued requests missing the user's required settings",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.ExecutionVersion=null;f.Save(job);var registration=f.Store.ReadRecord<CloudMissionRegistration>("cloud-mission-"+job.Id+".dpapi");registration.ExecutionVersion=null;f.Store.WriteRecord("cloud-mission-"+job.Id+".dpapi",registration);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"});job=f.Advance(job.Id);
            Assert(job.State=="attention"&&job.ThreadId==null&&f.Client.Starts==0&&f.Client.Connects==0&&job.Error.Contains("ancienne demande"),"loaded old MCP silently launched with different defaults");
        });
        check("cloud execution keeps legacy native defaults when no user preference requires otherwise",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.ExecutionVersion=null;f.Save(job);var registration=f.Store.ReadRecord<CloudMissionRegistration>("cloud-mission-"+job.Id+".dpapi");registration.ExecutionVersion=null;f.Store.WriteRecord("cloud-mission-"+job.Id+".dpapi",registration);job=f.Advance(job.Id);Assert(job.State=="running"&&f.Client.Starts==1&&f.Client.Turns==1,"unconfigured user lost legacy behavior");
        });
        check("cloud execution freezes an explicit inherited snapshot even if later user preferences change",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();var job=f.Submit(spec);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra"});Assert(f.Submit(spec).Model==null,"retry reinterpreted an inherited admitted snapshot");job=f.Advance(job.Id);Assert(job.State=="running"&&f.Client.ConfiguredStarts==0&&job.ExecutionVersion==CloudJobWorker.ExecutionVersion,"later preferences changed an admitted native-default job");
        });
        check("cloud execution schema two keeps required options invisible to legacy worker dispatch",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";var job=f.Submit(spec);
            var legacy=Directory.GetFiles(f.Store.Root,"cloud-job-*.dpapi").Select(path=>f.Store.ReadRecord<CloudJob>(Path.GetFileName(path))).Where(value=>value.SchemaVersion==1).ToArray();
            Assert(job.SchemaVersion==2&&legacy.Length==0&&f.Jobs.Get(job.Id).SchemaVersion==2&&f.Jobs.List().Single().Id==job.Id,"older worker could execute a task whose options it ignores");
            var normal=f.Submit();Assert(normal.SchemaVersion==1&&f.Jobs.List().Length==2,"ordinary version one compatibility lost");
        });
        check("cloud execution requires a capable receiver for automatic returns into a headless source",()=>{
            var f=new Fixture(root,++number,auth);var source=f.Store.Channel(f.Source.Channel);source.Provider="codex-headless";f.Store.Register(source);var spec=f.Spec();spec.Model="gpt-6.1-sol";Reject(()=>f.Submit(spec));Assert(f.Jobs.List().Length==0,"unsupported headless receiver was promised an automatic return");
            using(var current=Process.GetCurrentProcess()){
                f.Store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,Version="old"});Reject(()=>f.Submit(spec));
                f.Store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,ExecutionVersion=CloudJobWorker.ExecutionVersion});Assert(f.Submit(spec).SchemaVersion==2,"updated headless receiver refused");
            }
            var noReturn=f.Spec(false);noReturn.Model="gpt-6.1-sol";f.Store.WriteRecord("worker.dpapi",new RelayWorkerState());Assert(!f.Submit(noReturn).ReturnToSource,"explicit consultation-only outcome acquired an automatic return");
        });
        check("cloud mission branches identify the actual account feature and specification lot",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Feature="Oracle unités & factures";spec.Lot="10.02";spec.Specification="docs/orchestration/MATRICE-18-LOTS.md#lot-10";spec.Repository="creezio/projetqa-app";
            var job=f.Submit(spec);Assert(job.Branch=="codex/alice/oracle-unites-factures/lot-10-02"&&job.Lot=="lot-10-02"&&job.Specification==spec.Specification&&job.Repository==spec.Repository,"organization was not frozen in branch and registry");
            Assert(f.Submit(spec).Branch==job.Branch&&f.Jobs.List().Length==1,"same mandate allocated a second lot");
            f.Advance(job.Id);Assert(f.Client.Prompt.Contains(spec.Specification)&&f.Client.Prompt.Contains("lot-10-02"),"worker omitted specification context");
        });
        check("cloud mission lot reservations span environments sharing the same repository",()=>{
            var f=new Fixture(root,++number,auth);f.Configuration.Repositories=new List<CloudRepository>{new CloudRepository{RepositoryId="repo-one"}};var first=f.Submit();
            f.Configuration.Id="second-config";var spec=f.Spec();spec.ConfigurationId="second-config";var second=f.Submit(spec);Assert(first.Lot=="lot-001"&&second.Lot=="lot-002","same repository allocated duplicate lot through another environment");
            spec=f.Spec();spec.ConfigurationId="second-config";spec.Lot="1";Reject(()=>f.Submit(spec));Assert(f.Jobs.List().Length==2,"occupied lot was admitted");
        });
        check("cloud mission sublot aliases cannot reserve the same specification twice",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Lot="10-02";f.Submit(spec);
            spec=f.Spec();spec.Lot="LOT-010-002";Reject(()=>f.Submit(spec));Assert(f.Jobs.List().Length==1,"zero padded duplicate sublot accepted");
            spec=f.Spec();spec.Lot="10-03";Assert(f.Submit(spec).Lot=="lot-10-03","a different sublot was refused");
        });
        check("cloud mission invalid refs cannot create unsafe Git branches",()=>{
            foreach(var invalid in new[]{"../../main","10--02","-1","lot-/main","10-02.lock","1000000","10-2-3-4-5"}){var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Lot=invalid;Reject(()=>f.Submit(spec));Assert(f.Jobs.List().Length==0,"invalid lot admitted");}
            var bad=new Fixture(root,++number,auth);var request=bad.Spec();request.Repository="https://github.com/a/b";Reject(()=>bad.Submit(request));
        });
        check("cloud mission organization is part of the immutable idempotency contract",()=>{
            foreach(var field in new[]{"feature","lot","specification","repository"}){var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Feature="feature";spec.Lot="8-02";spec.Specification="SPEC.md#8";spec.Repository="owner/repository";var job=f.Submit(spec);if(field=="feature")spec.Feature="other";if(field=="lot")spec.Lot="8-03";if(field=="specification")spec.Specification="SPEC.md#9";if(field=="repository")spec.Repository="owner/other";Reject(()=>f.Submit(spec));Assert(f.Jobs.Get(job.Id).Lot=="lot-8-02","rejected resubmit changed reservation");}
        });
        check("cloud registry survives older workers discarding fields they do not understand",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Feature="Feature é";spec.Lot="10-02";spec.Specification="SPEC.md#10";var job=f.Submit(spec);var old=Json.Obj(Json.Read<object>(Json.Write(job)));
            foreach(string field in new[]{"AccountName","Feature","Lot","Specification","Repository","RepositoryIds"})old.Remove(field);old["State"]="running";f.Store.WriteRecord("cloud-job-"+job.Id+".dpapi",old);
            var read=f.Jobs.Get(job.Id);Assert(read.Lot==job.Lot&&read.Specification==spec.Specification&&read.Branch==job.Branch&&read.State=="running","older worker discarded durable organization");
            Assert(f.Jobs.List().Single().Feature==job.Feature,"list failed to hydrate registry");
        });
        check("cloud registry fails closed if a worker changes the registered branch",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.Branch="codex/changed/branch";f.Save(job);Reject(()=>f.Jobs.Get(job.Id));
        });
        check("cloud reservation survives a crash before the queued job is visible",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Lot="10-02";spec.Specification="SPEC.md#10";var job=f.Submit(spec);File.Delete(Path.Combine(f.Store.Root,"cloud-job-"+job.Id+".dpapi"));
            Assert(f.Jobs.List().Length==0&&f.Jobs.Registry(f.Source,null).Single().State=="reserved","orphan reservation was not inspectable");
            var duplicate=f.Spec();duplicate.Lot="010-002";Reject(()=>f.Submit(duplicate));Assert(f.Submit(spec).Branch==job.Branch&&f.Jobs.List().Length==1,"crash retry changed reservation or duplicate lot admitted");
        });
        check("cloud orphan reservation cannot be claimed by another source or changed configuration",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();var job=f.Submit(spec);File.Delete(Path.Combine(f.Store.Root,"cloud-job-"+job.Id+".dpapi"));
            var other=new RelaySession{Channel=f.Source.Channel,Thread="foreign-chat",Home=f.Source.Home,Account=f.Source.Account};Reject(()=>f.Jobs.Submit(spec,other,CancellationToken.None).GetAwaiter().GetResult());
            f.Configuration.VersionId="another-version";Reject(()=>f.Submit(spec));Assert(f.Jobs.List().Length==0,"foreign source or new version consumed existing reservation");
        });
        check("cloud registry lists only its source chat and optional repository",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Repository="owner/project";var job=f.Submit(spec);
            Assert(f.Jobs.Registry(f.Source,"owner/project").Single().Id==job.Id&&f.Jobs.Registry(f.Source,"owner/other").Length==0,"repository filter incorrect");
            var other=new RelaySession{Channel=f.Source.Channel,Thread="another-chat",Home=f.Source.Home,Account=f.Source.Account};Assert(f.Jobs.Registry(other,null).Length==0,"another source chat read this registry");
        });
        check("cloud registry reports PR and full commit hashes without claiming independent verification",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="PR https://github.com/owner/project/pull/42 commit 0123456789abcdef0123456789abcdef01234567";f.Save(job);var summary=Json.Obj(Json.Read<object>(Json.Write(CloudJobs.Summary(f.Jobs.Get(job.Id),false))));
            Assert(Json.Str(Json.Get(summary,"EvidenceState"))=="reported-unverified"&&RelayEngine.Rows(Json.Get(summary,"ReportedPullRequests")).Count()==1&&RelayEngine.Rows(Json.Get(summary,"ReportedCommitShas")).Count()==1&&Json.Get(summary,"Result")==null,"reported evidence lost, claimed verified, or prompt/result exposed");
        });
        check("cloud registry never promotes arbitrary hexadecimal credentials to commit metadata",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.Result="MEILI_MASTER_KEY=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nvalue bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\nsecret commit cccccccccccccccccccccccccccccccccccccccc\nCommit SHA: `dddddddddddddddddddddddddddddddddddddddd`";f.Save(job);
            string summary=Json.Write(CloudJobs.Summary(f.Jobs.Get(job.Id),false));Assert(!summary.Contains(new string('a',40))&&!summary.Contains(new string('b',40))&&!summary.Contains(new string('c',40))&&summary.Contains(new string('d',40)),"non-Git credential was exposed as a commit");
        });
        check("legacy cloud missions retain their original branch and can be retried",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();var job=f.Submit(spec);File.Delete(Path.Combine(f.Store.Root,"cloud-mission-"+job.Id+".dpapi"));job.AccountName=null;job.Feature=null;job.Lot=null;job.Branch="codex/switcher/"+job.Id;job.Fingerprint=RelayPolicies.Fingerprint(new RelayJobSpec{Title=spec.Title,Prompt=spec.Prompt,To=f.Instance.Id,Resource=spec.ConfigurationId,ExplicitDelegation=true,ReturnToSource=spec.ReturnToSource});f.Save(job);
            Assert(f.Submit(spec).Branch==job.Branch&&f.Jobs.List().Single().Lot==null,"legacy mission was silently renamed");
        });
        check("cloud jobs retry the same request without new admission and reject changed payload or source",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();var job=f.Submit(spec);int reads=f.ConfigurationReads;
            Assert(f.Submit(spec).Id==job.Id&&f.Jobs.List().Length==1&&f.ConfigurationReads==reads,"idempotent submit reread or duplicated job");
            spec.Prompt="changed";Reject(()=>f.Submit(spec));spec.Prompt=Secret;
            var changed=new RelaySession{Channel=f.Source.Channel,Thread="other-chat",Home=f.Source.Home,Account=f.Source.Account};Reject(()=>f.Jobs.Submit(spec,changed,CancellationToken.None).GetAwaiter().GetResult());
            changed.Thread=f.Source.Thread;changed.Home=Path.GetDirectoryName(f.Source.Home);Reject(()=>f.Jobs.Submit(spec,changed,CancellationToken.None).GetAwaiter().GetResult());
        });
        check("cloud jobs freeze the request before awaiting configuration discovery",()=>{
            var f=new Fixture(root,++number,auth);var ready=new TaskCompletionSource<CloudConfiguration>();var jobs=new CloudJobs(f.Store,i=>f.Client,(i,id,t)=>ready.Task);var spec=f.Spec();
            var pending=jobs.Submit(spec,f.Source,CancellationToken.None);spec.Prompt="new UI text";spec.Title="new UI title";ready.SetResult(f.Configuration);var job=pending.GetAwaiter().GetResult();
            Assert(job.Prompt==Secret&&job.Title=="Fixture mission","request changed while awaiting metadata");
        });
        check("cloud jobs only expose a source-bound result to the exact originating chat",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();Assert(f.Jobs.Authorize(f.Source,job.Id).Id==job.Id,"original source refused");
            Reject(()=>f.Jobs.Authorize(new RelaySession{Channel=f.Source.Channel,Thread="foreign",Account=f.Source.Account,Home=f.Source.Home},job.Id));
            Reject(()=>f.Jobs.Authorize(new RelaySession{Channel=f.Source.Channel,Thread=f.Source.Thread,Account=f.Source.Account,Home=Path.GetDirectoryName(f.Source.Home)},job.Id));
        });
        check("cloud jobs pin configuration identity and published version before creating a task",()=>{
            foreach(var change in new[]{"id","ready-version","version"}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();
                if(change=="id")f.Configuration.Id="other";else if(change=="ready-version")f.Configuration.LatestReadyVersionId="changed";else f.Configuration.VersionId="changed";
                var result=f.Advance(job.Id);Assert(result.State=="attention"&&f.Client.Starts==0&&f.Client.Turns==0,"changed configuration dispatched");
                f.Advance(job.Id);Assert(f.Client.Reads==0,"preparation failure attempted to read a missing chat");
            }
        });
        check("cloud jobs verify the returned account and environment before sending a prompt",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.Config="other-environment";f.Advance(job.Id);Assert(f.Client.Turns==0,"prompt sent to wrong environment");
            var other=new Fixture(root,++number,auth);job=other.Submit();other.Client.Account="wrong-account";var result=other.Advance(job.Id);Assert(result.State=="uncertain"&&other.Client.Turns==0,"wrong-account creation accepted");
        });
        check("cloud jobs use a stable client message ID and a dedicated branch in their mission",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job=f.Advance(job.Id);
            Assert(job.State=="running"&&f.Client.Starts==1&&f.Client.Turns==1&&f.Client.ClientMessageId==job.Id,"start or turn duplicated or not correlated");
            Assert(f.Client.Prompt.Contains(Secret)&&f.Client.Prompt.Contains(job.Branch)&&f.Client.Prompt.Contains("Ne modifie ni ne fusionne main"),"cloud delivery contract omitted");
            f.Advance(job.Id);Assert(f.Client.Starts==1&&f.Client.Turns==1,"running poll created another task or turn");
        });
        check("cloud jobs never send a prompt through native approval, busy or unknown chat state",()=>{
            foreach(var state in new[]{"approval","input","busy","unknown"}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.Approval=state=="approval";f.Client.Input=state=="input";f.Client.Busy=state=="busy";if(state=="unknown")f.Client.ReadStatus="unknown";
                Assert(f.Advance(job.Id).State=="prepared"&&f.Client.Turns==0,"native attention state received a prompt");
                f.Client.Approval=false;f.Client.Input=false;f.Client.Busy=false;f.Client.ReadStatus=null;f.Advance(job.Id);Assert(f.Client.Starts==1&&f.Client.Turns==1,"unblocked chat was recreated or never resumed");
            }
        });
        check("cloud jobs honor pause after creating a chat but before sending the mission",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.ReadHook=()=>RelayDispatch.Set(f.Store,"paused");
            Assert(f.Advance(job.Id).State=="prepared"&&f.Client.Turns==0,"pause after preparation ignored");
            f.Client.ReadHook=null;RelayDispatch.Set(f.Store,"running");f.Advance(job.Id);Assert(f.Client.Starts==1&&f.Client.Turns==1,"resume duplicated prepared task");
        });
        check("cloud jobs uncertain task or message sends never replay after restart",()=>{
            foreach(var step in new[]{"task","message"}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.ThrowStart=step=="task";f.Client.ThrowTurn=step=="message";
                Assert(f.Advance(job.Id).State=="uncertain","lost write outcome not marked uncertain");int starts=f.Client.Starts,turns=f.Client.Turns;
                var restarted=new CloudJobs(f.Store,i=>f.Client,(i,id,t)=>Task.FromResult(f.Configuration));restarted.Advance(job.Id,CancellationToken.None).GetAwaiter().GetResult();
                Assert(f.Client.Starts==starts&&f.Client.Turns==turns&&!Json.Write(CloudJobs.Summary(restarted.Get(job.Id),false)).Contains(Secret),"uncertain operation replayed or leaked exception text");
                string error=restarted.Get(job.Id).Error;Assert(error.Contains("phase="+(step=="task"?"thread-start":"turn-start"))&&error.Contains("type=IOException")&&!error.Contains("reason=timeout-or-cancelled"),"transport failure lost its safe phase or was falsely called a timeout");
            }
        });
        check("cloud jobs do not mistake unrelated or truncated completed turns for their result",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Advance(job.Id);f.Client.Complete=true;f.Client.LatestId="unrelated-turn";
            Assert(f.Advance(job.Id).State!="completed","unrelated final turn accepted");
            f.Client.LatestId=null;f.Client.TextComplete=false;var partial=f.Advance(job.Id);Assert(partial.State=="attention"&&partial.ReturnState!="pending"&&partial.Result==null,"partial answer delivered as completed");
            f.Client.TextComplete=true;f.Client.Final=" ";Assert(f.Advance(job.Id).State!="completed","empty final accepted");
            f.Client.Final="Verified result";var complete=f.Advance(job.Id);Assert(complete.State=="completed"&&complete.Result=="Verified result"&&complete.ReturnState=="pending","complete final never recovered");
        });
        check("cloud jobs safely recover a pre-send read failure without creating a second chat",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.ThrowRead=true;Assert(f.Advance(job.Id).State=="attention"&&f.Client.Starts==1&&f.Client.Turns==0,"read failure dispatched prompt");
            f.Client.ThrowRead=false;f.Advance(job.Id);Assert(f.Client.Starts==1&&f.Client.Turns==1,"recoverable read failure duplicated chat");
        });
        check("cloud jobs only cancel queued work and never claim a running task was stopped",()=>{
            var f=new Fixture(root,++number,auth);var queued=f.Submit();Assert(f.Jobs.CancelQueued(queued.Id).State=="cancelled"&&f.Client.Starts==0,"queued cancellation launched work");
            var running=f.Submit();f.Advance(running.Id);Reject(()=>f.Jobs.CancelQueued(running.Id));Assert(f.Jobs.Get(running.Id).State=="running","active task falsely cancelled");
        });
        check("cloud jobs target the exact source and reconcile a lost acknowledgement without replay",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Advance(job.Id);f.Client.Complete=true;job=f.Advance(job.Id);var transport=new ConsoleTests.Fake{Occupied=true};
            transport.WrongThread=true;Reject(()=>f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult());Assert(transport.Sends==0,"unverified source received result");
            transport.WrongThread=false;var monitor=f.Store.ReadRecord<CloudJobMonitorRecord>("cloud-monitor-"+job.Id+".dpapi");monitor.NotificationState="pending";f.Store.WriteRecord("cloud-monitor-"+job.Id+".dpapi",monitor);
            transport.UnknownSend=true;Reject(()=>f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult());f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();
            Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="delivered","uncertain return replayed or confirmed marker ignored");
        });
        check("cloud results for headless sources wait in the owner's durable local queue",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Fixture result";job.ReturnState="pending";f.Save(job);
            Assert(f.Jobs.LocalReturns().Length==0,"desktop return consumed by local engine pump");
            var channel=f.Store.Channel(f.Source.Channel);channel.Provider="codex-headless";f.Store.Register(channel);
            var transport=new SourceStateTransport{Flag="waitingOnApproval"};
            f.Jobs.PumpLocalReturns(transport,CancellationToken.None,false).GetAwaiter().GetResult();
            Assert(f.Jobs.LocalReturns().Single().Id==job.Id&&transport.Sends==0,"queue lost its result or interrupted busy source");
            transport.Flag=null;f.Jobs.PumpLocalReturns(transport,CancellationToken.None,false).GetAwaiter().GetResult();
            transport.TurnStatus="inProgress";f.Jobs.PumpLocalReturns(transport,CancellationToken.None,false).GetAwaiter().GetResult();
            Assert(f.Jobs.LocalReturns().Length==1&&f.Jobs.Get(job.Id).SourceReturnState=="running"&&transport.Sends==1,"local result processing lost its durable keepalive or was resent");
            transport.TurnStatus="completed";f.Jobs.PumpLocalReturns(transport,CancellationToken.None,false).GetAwaiter().GetResult();
            Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="delivered"&&f.Jobs.LocalReturns().Length==0,"local return was duplicated or left pending");
        });
        check("headless cloud return queue preserves uncertainty and rechecks source credentials",()=>{
            foreach(bool lost in new[]{false,true}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Fixture result";job.ReturnState=lost?"sending":"pending";f.Save(job);
                var channel=f.Store.Channel(f.Source.Channel);channel.Provider="codex-headless";f.Store.Register(channel);
                var transport=new SourceStateTransport();
                if(!lost){File.WriteAllText(Path.Combine(f.Source.Home,"auth.json"),"{}");f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Jobs.Get(job.Id).ReturnState=="pending"&&f.Jobs.Monitor(job).NotificationState=="attention","invalid source changed the business result or was retried");}
                else{f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();}
                Assert(transport.Sends==0&&(!lost||f.Jobs.Get(job.Id).ReturnState=="uncertain"),"unverified or uncertain return replayed");
            }
        });
        check("cloud jobs mark interrupted result delivery uncertain without sending",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Verified";job.ReturnState="sending";f.Save(job);var transport=new ConsoleTests.Fake();
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Jobs.Get(job.Id).ReturnState=="uncertain"&&transport.Sends==0,"crashed source return replayed");
        });
        check("cloud jobs never return into a source waiting for native approval or user input",()=>{
            foreach(var state in new[]{"waitingOnApproval","waitingOnUserInput"}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Verified";job.ReturnState="pending";f.Save(job);
                var transport=new SourceStateTransport();if(state.StartsWith("waiting"))transport.Flag=state;else transport.TurnStatus=state;
                f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(transport.Sends==0&&f.Jobs.Get(job.Id).ReturnState=="pending","idle status hid native source activity");
                transport.Flag=null;transport.TurnStatus="completed";f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="delivered","verified idle source did not receive its result once");
            }
        });
        check("cloud follow-up keeps the original developer chat branch and completed history",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();string original=Json.Write(f.Jobs.Get(parent.Id));var request=f.FollowUpSpec(parent);var child=f.FollowUp(request);
            Assert(child.SchemaVersion==3&&child.Kind=="follow-up"&&child.ParentId==parent.Id&&child.RootJobId==parent.Id&&child.ParentTurnId==parent.TurnId&&child.ThreadId==parent.ThreadId&&child.Branch==parent.Branch&&child.ReturnToSource,"follow-up replaced its developer or branch");f.NewTurn("new-correction-turn");child=f.Advance(child.Id);
            Assert(child.State=="running"&&child.TurnId=="new-correction-turn"&&f.Client.Starts==1&&f.Client.Turns==2&&f.Client.ConfigureCalls==0&&f.Client.ClientMessageId==request.Id&&child.ExecutionState=="verified","correction created a chat or was sent without observed settings");
            Assert(f.Client.Prompt.Contains(parent.Branch)&&f.Client.Prompt.Contains(parent.Id)&&f.Client.Prompt.Contains("nouvelle PR")&&Json.Write(f.Jobs.Get(parent.Id))==original,"original history changed or existing PR instruction absent");
            f.Client.Complete=true;f.Client.Final="Corrected delivery";child=f.Advance(child.Id);Assert(child.State=="completed"&&child.Result=="Corrected delivery"&&child.ReturnState=="pending"&&f.Jobs.Get(parent.Id).Result==parent.Result,"correction did not get its own result or replaced parent evidence");
        });
        check("cloud follow-up requires explicit authorization and the exact original source chat",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var request=f.FollowUpSpec(parent);request.ExplicitDelegation=false;Reject(()=>f.FollowUp(request));request.ExplicitDelegation=true;
            Reject(()=>f.Jobs.FollowUp(request,null,CancellationToken.None).GetAwaiter().GetResult());Reject(()=>f.Jobs.FollowUp(request,new RelaySession{Channel=f.Source.Channel,Thread="other-chat",Account=f.Source.Account,Home=f.Source.Home},CancellationToken.None).GetAwaiter().GetResult());
            Assert(f.Jobs.List().Length==1&&f.Client.Starts==1&&f.Client.Turns==1,"unauthorized correction became visible or started");
        });
        check("cloud follow-up refuses running cancelled and uncertain parents without replacing them",()=>{
            foreach(string state in new[]{"running","cancelled","uncertain"}){
                var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();parent.State=state;f.Save(parent);Reject(()=>f.FollowUp(f.FollowUpSpec(parent)));
                Assert(f.Jobs.List().Length==1&&f.Jobs.Get(parent.Id).State==state&&f.Client.Turns==1,"nonterminal parent was changed or received a correction");
            }
        });
        check("cloud follow-up idempotency freezes settings and rejects changed prompts or parent",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var request=f.FollowUpSpec(parent);var child=f.FollowUp(request);int connects=f.Client.Connects;f.Client.ThrowRead=true;CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6-luna",ReasoningEffort="low",PermissionProfile=":workspace-write"});
            Assert(f.FollowUp(request).Id==child.Id&&f.Jobs.List().Length==2&&f.Client.Connects==connects&&child.Model==parent.Model&&child.PermissionProfile==parent.PermissionProfile,"retry changed preferences or made a native call");request.Prompt="changed";Reject(()=>f.FollowUp(request));request.Prompt=Secret;request.ParentId=child.Id;Reject(()=>f.FollowUp(request));
        });
        check("cloud follow-up orphan reservations recover the same chat branch and idempotency key",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var request=f.FollowUpSpec(parent);var child=f.FollowUp(request);File.Delete(Path.Combine(f.Store.Root,"cloud-follow-up-"+child.Id+".dpapi"));
            var recovered=f.FollowUp(request);Assert(recovered.Kind=="follow-up"&&recovered.ThreadId==parent.ThreadId&&recovered.Branch==parent.Branch&&recovered.ParentTurnId==parent.TurnId&&f.Jobs.List().Length==2&&f.Client.Turns==1,"orphan recovery allocated a new branch or prompt");
        });
        check("cloud follow-up is invisible to old schema workers and demands a capable live worker",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();using(var current=Process.GetCurrentProcess()){
                f.Store.WriteRecord("cloud-worker-v1.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,ExecutionVersion=1});Reject(()=>f.FollowUp(f.FollowUpSpec(parent)));
                f.Store.WriteRecord("cloud-worker-v1.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,ExecutionVersion=1,CloudJobSchemaVersion=3});var child=f.FollowUp(f.FollowUpSpec(parent));
                var oldVisible=Directory.GetFiles(f.Store.Root,"cloud-job-*.dpapi").Select(path=>f.Store.ReadRecord<CloudJob>(Path.GetFileName(path))).Where(job=>new[]{1,2}.Contains(job.SchemaVersion)).ToArray();Assert(oldVisible.Length==1&&oldVisible[0].Id==parent.Id&&child.SchemaVersion==3&&File.Exists(Path.Combine(f.Store.Root,"cloud-follow-up-"+child.Id+".dpapi"))&&!File.Exists(Path.Combine(f.Store.Root,"cloud-job-"+child.Id+".dpapi"))&&f.Jobs.List().Length==2,"old worker could mistake a follow-up for a new-chat mission");
            }
        });
        check("cloud follow-up requires an upgraded headless source receiver before admission",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var source=f.Store.Channel(f.Source.Channel);source.Provider="codex-headless";f.Store.Register(source);using(var current=Process.GetCurrentProcess()){
                f.Store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,ExecutionVersion=1});Reject(()=>f.FollowUp(f.FollowUpSpec(parent)));
                f.Store.WriteRecord("worker.dpapi",new RelayWorkerState{Pid=current.Id,Started=current.StartTime.ToUniversalTime().Ticks,ExecutionVersion=1,CloudJobSchemaVersion=3});Assert(f.FollowUp(f.FollowUpSpec(parent)).SchemaVersion==3,"capable receiver refused");
            }
        });
        check("cloud follow-up never sends through a busy chat native approval or user input",()=>{
            foreach(string state in new[]{"busy","approval","input"}){
                var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));if(state=="busy")f.Client.Busy=true;if(state=="approval")f.Client.Approval=true;if(state=="input")f.Client.Input=true;child=f.Advance(child.Id);
                Assert(child.State=="attention"&&child.TurnId==null&&f.Client.Starts==1&&f.Client.Turns==1&&f.Client.ConfigureCalls==0,"native attention was bypassed or an existing chat reconfigured");
            }
        });
        check("cloud follow-up verifies account environment repository and version before its prompt",()=>{
            foreach(string drift in new[]{"account","environment","repository","version"}){
                var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));if(drift=="account")f.Client.Account="different-account";if(drift=="environment")f.Client.Config="different-environment";if(drift=="repository")f.Configuration.Repositories=new List<CloudRepository>{new CloudRepository{RepositoryId="other-repository"}};if(drift=="version")f.Configuration.VersionId="changed-version";child=f.Advance(child.Id);
                Assert(child.State=="attention"&&f.Client.Starts==1&&f.Client.Turns==1&&child.Error!=null,"identity or environment drift received a correction");
            }
        });
        check("cloud follow-up blocks unknown or mismatched settings without configuring a used chat",()=>{
            foreach(bool unknown in new[]{false,true}){
                var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));if(unknown)f.Client.Observation.Verified=false;else f.Client.Observation.PermissionProfile=":workspace-write";child=f.Advance(child.Id);
                Assert(child.State=="attention"&&f.Client.ConfigureCalls==0&&f.Client.Turns==1&&child.ExecutionState!=(unknown?"verified":"unknown"),"used chat was elevated or unknown settings accepted");
            }
        });
        check("cloud follow-up preserves inherited settings when a legacy parent had no explicit snapshot",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.Submit();f.Advance(parent.Id);f.Client.Complete=true;parent=f.Advance(parent.Id);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6.1-sol",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"});var child=f.FollowUp(f.FollowUpSpec(parent));f.NewTurn("new-inherited-turn");child=f.Advance(child.Id);
            Assert(child.State=="running"&&child.Model=="gpt-6.1-sol"&&child.ReasoningEffort=="ultra"&&child.PermissionProfile==":danger-full-access"&&f.Client.ConfigureCalls==0&&f.Client.ConfiguredTurns==1,"legacy fallback changed a used chat or omitted saved preferences");
        });
        check("cloud follow-up preserves a partial parent snapshot instead of adding newly changed defaults",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";var parent=f.Submit(spec);f.Advance(parent.Id);f.Client.Complete=true;parent=f.Advance(parent.Id);CloudMissionPreferences.Save(f.Store,new CloudTaskExecutionOptions{Model="gpt-6-luna",ReasoningEffort="ultra",PermissionProfile=":danger-full-access"});f.Client.Observation.PermissionProfile=":workspace-write";var child=f.FollowUp(f.FollowUpSpec(parent));f.NewTurn("partial-parent-correction");child=f.Advance(child.Id);
            Assert(child.State=="running"&&child.Model==parent.Model&&child.ReasoningEffort==null&&child.PermissionProfile==null&&f.Client.TurnOptions.PermissionProfile==null&&f.Client.ConfigureCalls==0,"partial parent snapshot picked up newly elevated defaults");
        });
        check("cloud follow-up never overtakes a newer unregistered native turn",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));f.Client.LatestId="unregistered-manual-turn";child=f.Advance(child.Id);
            Assert(child.State=="attention"&&f.Client.Turns==1&&f.Client.ConfigureCalls==0,"follow-up overtook later manual work");
        });
        check("cloud follow-up rechecks native attention after observing execution settings",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));f.Client.ExecutionReadHook=()=>f.Client.Approval=true;child=f.Advance(child.Id);
            Assert(child.State=="attention"&&f.Client.Turns==1&&f.Client.Approval&&f.Client.ConfigureCalls==0,"attention appearing during preflight was bypassed");
        });
        check("cloud follow-up unknown send outcomes remain durable and are never resent after restart",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var request=f.FollowUpSpec(parent);var child=f.FollowUp(request);f.NewTurn("uncertain-correction-turn");f.Client.ThrowTurn=true;child=f.Advance(child.Id);Assert(child.State=="uncertain"&&child.TurnId==null,"unknown correction send lost its uncertainty");
            var restarted=new CloudJobs(f.Store,i=>f.Client,(i,id,t)=>Task.FromResult(f.Configuration));restarted.Advance(child.Id,CancellationToken.None).GetAwaiter().GetResult();Assert(f.FollowUp(request).State=="uncertain"&&f.Client.Starts==1&&f.Client.Turns==2,"uncertain correction was duplicated");
        });
        check("cloud follow-up refuses an old turn returned as the new correction acknowledgement",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));child=f.Advance(child.Id);Assert(child.State=="uncertain"&&child.TurnId==null&&child.Result==null&&f.Client.Turns==2,"parent result was mistaken for new correction evidence");
        });
        check("cloud follow-up prevents sibling corrections and supports a new linked correction after completion",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));Reject(()=>f.FollowUp(f.FollowUpSpec(parent)));f.NewTurn("correction-one");child=f.Advance(child.Id);f.Client.Complete=true;child=f.Advance(child.Id);
            var next=f.FollowUp(f.FollowUpSpec(child));Assert(next.RootJobId==parent.Id&&next.ParentId==child.Id&&next.ParentTurnId==child.TurnId&&next.FollowUpNumber==2&&next.Branch==parent.Branch,"sequential review lost its lineage");f.NewTurn("correction-two");next=f.Advance(next.Id);Assert(next.State=="running"&&f.Client.Starts==1&&f.Client.Turns==3,"next correction created a chat or failed to start");
        });
        check("cloud follow-up records failed preflight and permits cancellation before any new prompt",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));f.Client.Approval=true;child=f.Advance(child.Id);Assert(child.State=="attention"&&f.Jobs.Get(child.Id).ParentId==parent.Id,"preflight failure disappeared");child=f.Jobs.CancelQueued(child.Id);
            Assert(child.State=="cancelled"&&f.Client.Turns==1&&f.Jobs.Get(parent.Id).State=="completed","pre-prompt cancellation interrupted or rewrote the developer");
        });
        check("cloud follow-up enforces current capacity immediately before its new turn",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));var blocker=f.Submit();blocker.State="running";blocker.AccountKey="another-account";blocker.ThreadId="other-chat";var registration=f.Store.ReadRecord<CloudMissionRegistration>("cloud-mission-"+blocker.Id+".dpapi");registration.AccountKey=blocker.AccountKey;f.Store.WriteRecord("cloud-mission-"+blocker.Id+".dpapi",registration);f.Save(blocker);CloudCapacity.Save(f.Store,new CloudCapacityPreferences{MaxParallel=1});f.NewTurn("capacity-correction");child=f.Advance(child.Id);
            Assert(child.State=="prepared"&&child.TurnId==null&&f.Client.Turns==1,"follow-up bypassed current admission capacity");blocker.State="completed";f.Save(blocker);child=f.Advance(child.Id);Assert(child.State=="running"&&f.Client.Turns==2,"released capacity did not allow exactly one correction");
        });
        check("cloud follow-up registry exposes lineage and MCP authorization without private prompt bodies",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var child=f.FollowUp(f.FollowUpSpec(parent));var publicSummary=Json.Read<object>(Json.Write(CloudJobs.Summary(child,false,f.Jobs.Monitor(child))));
            Assert(Json.Str(Json.Get(publicSummary,"Kind"))=="follow-up"&&Json.Str(Json.Get(publicSummary,"ParentId"))==parent.Id&&!Json.Write(publicSummary).Contains(Secret)&&f.Jobs.Registry(f.Source,null).Length==2,"registry hid lineage or leaked prompt");
            var tool=RelayMcp.Tools().Select(value=>Json.Read<object>(Json.Write(value))).Single(value=>Json.Str(Json.Get(value,"name"))=="follow_up_cloud_job");Assert(RelayEngine.Rows(Json.Get(Json.Get(tool,"inputSchema"),"required")).Select(Json.Str).Contains("explicitDelegation"),"MCP follow-up omitted authorization");
        });
        check("cloud follow-up rejects tampered links and direct cycles before any correction is dispatched",()=>{
            var f=new Fixture(root,++number,auth);var parent=f.CompletedParent();var request=f.FollowUpSpec(parent);request.Id=parent.Id;Reject(()=>f.FollowUp(request));request.Id=Guid.NewGuid().ToString("N");var child=f.FollowUp(request);child.ParentId=child.Id;f.Save(child);var registration=f.Store.ReadRecord<CloudMissionRegistration>("cloud-mission-"+child.Id+".dpapi");registration.ParentId=child.Id;f.Store.WriteRecord("cloud-mission-"+child.Id+".dpapi",registration);
            var newRequest=f.FollowUpSpec(child);Reject(()=>f.FollowUp(newRequest));child=f.Advance(child.Id);Assert(child.State=="attention"&&f.Client.Turns==1&&f.Client.Starts==1,"cyclic lineage reached the native chat");
        });
        check("cloud monitoring finds its completed turn after newer manual work without approving that work",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job=f.Advance(job.Id);f.Client.Complete=true;f.Client.LatestId="newer-manual-turn";f.Client.Approval=true;f.Client.TargetedTurn=new CloudTaskTurn{Id=job.TurnId,Status="completed",Text="Exact mission evidence",TextComplete=true};
            job=f.Advance(job.Id);Assert(job.State=="completed"&&job.Result=="Exact mission evidence"&&f.Client.TargetedReads==1&&f.Client.Approval&&f.Client.Turns==1,"newer native intervention hid or replaced the completed target turn");
        });
        check("cloud monitoring retrieves a paginated final answer even when its first snapshot was incomplete",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job=f.Advance(job.Id);f.Client.Complete=true;f.Client.TextComplete=false;f.Client.TargetedTurn=new CloudTaskTurn{Id=job.TurnId,Status="completed",Text="Complete paginated mission evidence",TextComplete=true};
            job=f.Advance(job.Id);Assert(job.State=="completed"&&job.Result==f.Client.TargetedTurn.Text&&f.Client.TargetedReads==1,"incomplete first page permanently hid the available final answer");
        });
        check("cloud monitoring reads a passive encrypted snapshot without polling or file writes",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();int files=Directory.GetFiles(f.Store.Root).Length;var monitor=new CloudJobMonitoring(f.Store);
            var first=monitor.Read(job);var second=monitor.Read(job);
            Assert(first.NotificationState=="waiting"&&first.Events.Length==second.Events.Length&&f.Client.Connects==0&&f.Client.Starts==0&&f.Client.Reads==0&&Directory.GetFiles(f.Store.Root).Length==files,"dashboard read performed I/O outside local storage");
            string record=Path.Combine(f.Store.Root,"cloud-monitor-"+job.Id+".dpapi");Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(record)).Contains(Secret)&&!Json.Write(first).Contains(Secret),"monitor journal disclosed its mission prompt");
        });
        check("cloud scheduled checks run at sixty seconds and preserve worker liveness between deadlines",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();
            job=f.Jobs.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();int reads=f.Client.Reads;Assert(job.State=="running","scheduled mission did not start");
            clock=clock.AddSeconds(59);f.Jobs.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();
            Assert(f.Client.Reads==reads&&CloudJobWorker.SelectScheduledPending(f.Jobs).Length==0&&CloudJobWorker.HasRemotePending(f.Jobs),"early network poll or sleeping worker retired");
            clock=clock.AddSeconds(1);Assert(CloudJobWorker.SelectScheduledPending(f.Jobs).Single().Id==job.Id,"due task disappeared");f.Jobs.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();
            var state=f.Jobs.Monitor(f.Jobs.Get(job.Id));Assert(f.Client.Reads==reads+1&&DateTime.Parse(state.LastCheckedUtc).ToUniversalTime()==clock&&DateTime.Parse(state.NextCheckUtc).ToUniversalTime()==clock.AddSeconds(60),"sixty-second schedule was not persisted");
        });
        check("cloud monitoring resumes persisted deadlines after a worker restart without a second prompt",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();f.Jobs.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();int reads=f.Client.Reads;
            var restarted=new CloudJobs(f.Store,i=>f.Client,(i,id,t)=>Task.FromResult(f.Configuration),()=>clock);clock=clock.AddSeconds(30);restarted.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();Assert(f.Client.Reads==reads,"restart forgot the saved deadline");
            clock=clock.AddSeconds(30);restarted.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();Assert(f.Client.Reads==reads+1&&f.Client.Starts==1&&f.Client.Turns==1,"restart duplicated a mission or did not resume polling");
        });
        check("cloud monitoring records connection failures separately and clears them on a verified read",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();f.Jobs.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();clock=clock.AddSeconds(60);f.Client.ThrowRead=true;
            job=f.Jobs.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();var failed=f.Jobs.Monitor(job);Assert(job.State=="running"&&failed.LastCheckError!=null&&!Json.Write(failed).Contains(Secret),"connection failure changed business state or leaked details");
            clock=clock.AddSeconds(60);f.Client.ThrowRead=false;job=f.Jobs.Advance(job.Id,CancellationToken.None,true).GetAwaiter().GetResult();var recovered=f.Jobs.Monitor(job);
            Assert(recovered.LastCheckError==null&&recovered.Events.Count(e=>e.Kind=="check-error")==1&&recovered.Events.Count(e=>e.Kind=="check-recovered")==1,"recovery was not journaled exactly once");
        });
        check("cloud completion is sent through the public desktop queue while the orchestrator is active",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);var transport=new NotificationTransport{Status="active"};
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();var current=f.Jobs.Get(job.Id);var monitor=f.Jobs.Monitor(current);
            Assert(transport.Sends==1&&current.ReturnState=="delivered"&&monitor.NotificationState=="delivered"&&monitor.NotificationSentUtc!=null&&current.SourceReturnState==null&&transport.Prompt.StartsWith("[CREEZIO_CLOUD_RESULT:"+job.Id+"]"),"active desktop source never received its queued result or delivery claimed source processing");
        });
        check("cloud notifications retry offline sources only after sixty seconds without changing completed results",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";job.Error="Business evidence retained";f.Save(job);var transport=new NotificationTransport{Offline=true};
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();var offline=f.Jobs.Monitor(f.Jobs.Get(job.Id));Assert(offline.NotificationState=="source-offline"&&offline.NotificationAttempts==1&&offline.LastNotificationError!=null,"offline source was not exposed");
            transport.Offline=false;clock=clock.AddSeconds(59);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==0,"offline result retried before its deadline");
            clock=clock.AddSeconds(1);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();var current=f.Jobs.Get(job.Id);Assert(transport.Sends==1&&current.State=="completed"&&current.Error==job.Error&&current.Result==job.Result&&f.Jobs.Monitor(current).NotificationAttempts==2,"offline result was lost or business evidence overwritten");
        });
        check("cloud notifications never queue through a native approval or user input request",()=>{
            foreach(string flag in new[]{"waitingOnApproval","waitingOnUserInput"}){
                DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);var transport=new NotificationTransport{Status="active",Flag=flag};
                f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==0&&f.Jobs.Monitor(job).NotificationState=="source-busy","native intervention received a queued message");
                transport.Flag=null;clock=clock.AddSeconds(60);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==1,"resolved native intervention kept its result blocked");
            }
        });
        check("cloud notification permissions are explicit and retried without changing them",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);var transport=new NotificationTransport{RefusePermissions=true};
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();var snapshot=f.Jobs.Monitor(job);Assert(snapshot.NotificationState=="permissions"&&snapshot.LastNotificationError!=null&&!Json.Write(snapshot).Contains(Secret)&&transport.Sends==0&&f.Jobs.Get(job.Id).ReturnState=="pending","permission refusal was hidden as offline or changed the task");
            transport.RefusePermissions=false;clock=clock.AddSeconds(59);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==0,"permissions were retried before the deadline");
            clock=clock.AddSeconds(1);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==1&&f.Jobs.Monitor(f.Jobs.Get(job.Id)).NotificationState=="delivered","confirmed permissions did not release the pending notification");
        });
        check("cloud notification retries share one source-chat slot per minute across aliases",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var first=f.Submit();var second=f.Submit();foreach(var job in new[]{first,second}){job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);}var transport=new NotificationTransport{Status="active"};
            f.Jobs.Deliver(first.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();f.Jobs.Deliver(second.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();
            Assert(transport.Sends==1&&f.Jobs.Monitor(second).NotificationNextAttemptUtc!=null&&f.Jobs.Monitor(second).NotificationAttempts==0,"source received a backlog burst");
            clock=clock.AddSeconds(60);f.Jobs.Deliver(second.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==2,"second result did not receive the next source slot");
        });
        check("cloud lost acknowledgements reconcile only the exact source user message and never resend",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);var transport=new NotificationTransport{Status="active",LostAcknowledgement=true};
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(f.Jobs.Get(job.Id).ReturnState=="uncertain"&&transport.Sends==1,"lost acknowledgement was called delivered");
            clock=clock.AddSeconds(60);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();
            Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="delivered"&&f.Jobs.Monitor(f.Jobs.Get(job.Id)).NotificationState=="delivered","known source marker was ignored or sent twice");
        });
        check("cloud uncertain notification cannot be confirmed by quoted agent text or incomplete absent history",()=>{
            foreach(bool agentOnly in new[]{false,true}){
                DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);var transport=new NotificationTransport{LostAcknowledgement=true,AgentOnlyMarker=agentOnly,DropMessage=!agentOnly};
                f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();clock=clock.AddSeconds(60);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();clock=clock.AddSeconds(60);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();
                Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="uncertain","untrusted or partial history authorized a resend or false confirmation");
            }
        });
        check("cloud notification reconciliation accepts only the known public app envelope",()=>{
            foreach(string kind in new[]{"native","namespace","name","snake-case","quoted"}){
                DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);var transport=new NotificationTransport{LostAcknowledgement=true,WrapperType=kind=="snake-case"?"function_call_output":"functionCallOutput",WrapperNamespace=kind=="namespace"?"untrusted_tool":"codex_app",WrapperName=kind=="name"?"read_file":"send_message_to_thread",QuotedWrapper=kind=="quoted"};
                f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();clock=clock.AddSeconds(60);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();
                Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState==(kind=="native"?"delivered":"uncertain"),"unrecognized or quoted envelope confirmed a message");
            }
        });
        check("cloud notification cannot change source identity or silently fall back to another chat",()=>{
            foreach(bool accountChanged in new[]{false,true}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Verified";f.Save(job);var transport=new NotificationTransport{WrongThread=!accountChanged};if(accountChanged)File.WriteAllText(Path.Combine(f.Source.Home,"auth.json"),"{}");
                f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==0&&f.Jobs.Monitor(job).NotificationState=="attention"&&f.Jobs.Get(job.Id).Result=="Verified","invalid identity received a result or deleted evidence");
            }
        });
        check("cloud interventions notify once and a later completion produces a separate result notification",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="attention";job.ThreadId=f.Client.ThreadId;job.TurnId=f.Client.TurnId;job.Error="Native approval required";f.Save(job);var transport=new NotificationTransport{Status="active"};
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==1&&transport.Prompt.StartsWith("[CREEZIO_CLOUD_ATTENTION:")&&f.Jobs.Monitor(job).NotificationKind=="intervention"&&f.Jobs.Get(job.Id).ReturnState=="waiting","intervention was not kept separate from a business result");
            clock=clock.AddSeconds(60);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==1,"same intervention was repeated");
            job.State="completed";job.Result="Verified";job.Error=null;job.ReturnState="pending";f.Save(job);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();Assert(transport.Sends==2&&f.Jobs.Monitor(job).NotificationKind=="result"&&f.Jobs.Get(job.Id).ReturnState=="delivered","terminal result was hidden behind the earlier intervention");
        });
        check("cloud resumed or cancelled requests cannot keep an obsolete intervention notification alive",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();var monitor=new CloudJobMonitoring(f.Store);job.State="attention";job.Error="Native input";monitor.Observe(job);Assert(monitor.PendingNotification(job),"fixture intervention missing");job.State="running";job.Error=null;monitor.Observe(job);Assert(!monitor.PendingNotification(job)&&monitor.Read(job).NotificationState=="waiting","resumed request kept its obsolete alert");
            job.State="attention";job.Error="Other input";monitor.Observe(job);job.State="cancelled";job.ReturnState="none";monitor.Observe(job);Assert(!monitor.PendingNotification(job)&&monitor.Read(job).NotificationState=="none","cancelled request kept its old alert");
        });
        check("cloud repeated backend request IDs do not repeat an intervention or hide its final result",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var job=f.Submit();job.State="attention";job.ThreadId=f.Client.ThreadId;job.TurnId=f.Client.TurnId;job.Error="Environment unavailable (HTTP 500) · requête req_first";f.Save(job);var transport=new NotificationTransport{Status="active"};
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();clock=clock.AddSeconds(60);job.Error="Environment unavailable (HTTP 500) · requête req_second";f.Save(job);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();
            Assert(transport.Sends==1&&f.Jobs.Get(job.Id).Error.EndsWith("req_second")&&f.Jobs.Monitor(job).NotificationState=="delivered","request-specific diagnostics duplicated the same alert or were erased");
            job.State="completed";job.ReturnState="pending";job.Result="Verified";job.Error=null;f.Save(job);f.Jobs.Deliver(job.Id,transport,CancellationToken.None,true).GetAwaiter().GetResult();
            Assert(transport.Sends==2&&f.Jobs.Get(job.Id).ReturnState=="delivered","deduplication hid a later terminal result");
        });
        check("cloud monitoring journals transitions only and bounds its history without storing result bodies",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();var monitor=new CloudJobMonitoring(f.Store);int count=monitor.Read(job).Events.Length;for(int i=0;i<20;i++){monitor.Observe(job);monitor.BeginCheck(job);monitor.CheckFinished(job,null);}Assert(monitor.Read(job).Events.Length==count,"ordinary unchanged polls grew the audit");
            job.Result=Secret;for(int i=0;i<130;i++){job.State=i%2==0?"running":"attention";monitor.Observe(job);}var snapshot=monitor.Read(job);Assert(snapshot.Events.Length==100&&!Json.Write(snapshot).Contains(Secret),"audit was unbounded or stored a result body");
        });
        check("cloud monitoring rejects substituted or corrupt journals without polling or notification",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();var other=f.Submit();File.Copy(Path.Combine(f.Store.Root,"cloud-monitor-"+job.Id+".dpapi"),Path.Combine(f.Store.Root,"cloud-monitor-"+other.Id+".dpapi"),true);
            var monitor=new CloudJobMonitoring(f.Store);Assert(monitor.Read(other).NotificationState=="attention"&&monitor.Read(other).LastCheckError!=null&&!monitor.CheckDue(other)&&!monitor.NotificationDue(other),"substituted audit authorized automatic work");
        });
        check("cloud due scheduler uses non-due active jobs when ranking capacity for queued accounts",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);var f=new Fixture(root,++number,auth,()=>clock);var active=f.Submit();active.State="running";active.ThreadId="active-native";f.Save(active);new CloudJobMonitoring(f.Store,()=>clock).BeginCheck(active);
            var pending=new List<CloudJob>();for(int i=0;i<5;i++){var job=f.Submit();job.UpdatedUtc="2026-10-15T01:00:0"+i+"Z";f.Save(job);pending.Add(job);}
            var independent=f.Submit();independent.AccountKey="independent-account";var registration=f.Store.ReadRecord<CloudMissionRegistration>("cloud-mission-"+independent.Id+".dpapi");registration.AccountKey=independent.AccountKey;f.Store.WriteRecord("cloud-mission-"+independent.Id+".dpapi",registration);f.Save(independent);
            // The account changes in this synthetic scheduling fixture, so no
            // network operation is attempted on the copied metadata.
            File.Delete(Path.Combine(f.Store.Root,"cloud-monitor-"+independent.Id+".dpapi"));var batch=CloudJobWorker.SelectScheduledPending(f.Jobs);
            Assert(batch.Any(job=>job.Id==independent.Id)&&batch.All(job=>job.Id!=active.Id),"non-due active account was excluded from capacity and hid independent queued work");
        });
        check("cloud worker polls an older running task before newer queued jobs and ignores pre-chat blocks",()=>{
            var jobs=Enumerable.Range(0,4).Select(i=>new CloudJob{Id="queued-"+i,State="queued",AccountKey="same",UpdatedUtc="2026-10-15T12:00:0"+i+"Z"}).ToList();
            jobs.Add(new CloudJob{Id="running",State="running",AccountKey="same",ThreadId="native-chat",UpdatedUtc="2026-10-15T11:00:00Z"});
            jobs.Insert(0,new CloudJob{Id="blocked-before-chat",State="attention",UpdatedUtc="2026-10-15T10:00:00Z"});
            var batch=CloudJobWorker.SelectPending(jobs);
            Assert(batch.Length==4&&batch[0].Id=="running"&&batch.All(j=>j.Id!="blocked-before-chat"),"queued jobs starved the active task or inert pre-chat block occupied polling slots");
        });
        check("cloud source reconnection is nonblocking and cannot overlap a slow native read",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);int calls=0;var entered=new ManualResetEventSlim();var release=new TaskCompletionSource<int>();
            using(var pump=new CloudSourceReconnectPump(token=>{Interlocked.Increment(ref calls);entered.Set();return release.Task;},()=>clock)){
                pump.Tick();Assert(entered.Wait(5000),"maintenance did not start");clock=clock.AddSeconds(120);pump.Tick();Assert(calls==1&&!pump.Pending.IsCompleted,"slow reconnection overlapped or blocked the mission thread");
                release.SetResult(0);pump.Pending.GetAwaiter().GetResult();pump.Tick();pump.Pending.GetAwaiter().GetResult();Assert(calls==2,"finished maintenance could not resume at the next due check");
            }
        });
        check("cloud source reconnection retries at sixty seconds and isolates maintenance errors",()=>{
            DateTime clock=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);int calls=0;
            using(var pump=new CloudSourceReconnectPump(token=>{if(Interlocked.Increment(ref calls)==1)throw new IOException(Secret);return Task.FromResult(0);},()=>clock)){
                pump.Tick();pump.Pending.GetAwaiter().GetResult();Assert(pump.Error!=null&&!pump.Error.Contains(Secret),"maintenance error escaped or exposed details");
                clock=clock.AddSeconds(59);pump.Tick();Assert(calls==1,"reconnection retried before sixty seconds");
                clock=clock.AddSeconds(1);pump.Tick();pump.Pending.GetAwaiter().GetResult();Assert(calls==2&&pump.Error==null,"verified maintenance did not clear its prior diagnostic");
            }
        });
        check("cloud source reconnection has a cancellation deadline and never retains a foreground wait",()=>{
            int cancelled=0;
            using(var pump=new CloudSourceReconnectPump(async token=>{try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Interlocked.Increment(ref cancelled);throw;}},null,TimeSpan.FromMilliseconds(20))){
                pump.Tick();Assert(pump.Pending.Wait(5000)&&cancelled==1&&pump.Error!=null,"bounded reconnection did not cancel its native read");
            }
        });
        check("cloud source reconnection disposal cancels only its own maintenance and never restarts it",()=>{
            var entered=new ManualResetEventSlim();int calls=0,cancelled=0;
            var pump=new CloudSourceReconnectPump(async token=>{Interlocked.Increment(ref calls);entered.Set();try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Interlocked.Increment(ref cancelled);throw;}});
            pump.Tick();Assert(entered.Wait(5000),"maintenance did not enter its native read");pump.Dispose();Assert(pump.Pending.Wait(5000)&&cancelled==1,"shutdown left maintenance pending");pump.Tick();Assert(calls==1,"disposed maintenance restarted");
        });
        check("cloud worker reserves queued progress even when four completed results await one busy source",()=>{
            var jobs=Enumerable.Range(0,4).Select(i=>new CloudJob{Id="return-"+i,State="completed",ReturnState="pending",SourceChannel="source",SourceThread="busy-chat",UpdatedUtc="2026-10-15T10:00:0"+i+"Z"}).ToList();
            jobs.Add(new CloudJob{Id="queued",State="queued",AccountKey="new-account",UpdatedUtc="2026-10-15T11:00:00Z"});var batch=CloudJobWorker.SelectPending(jobs);
            Assert(batch.Any(job=>job.Id=="queued")&&batch.Count(job=>job.ReturnState=="pending")==1&&batch.Length<=4,"pending results starved admission or retried one busy source repeatedly in the same batch");
        });
        check("cloud worker preserves running polls beside a queued backlog and enforces two account slots",()=>{
            var jobs=Enumerable.Range(0,4).Select(i=>new CloudJob{Id="queued-"+i,State="queued",AccountKey="queued-account-"+i,UpdatedUtc="2026-10-15T10:00:0"+i+"Z"}).ToList();
            var first=new CloudJob{Id="running-a",State="running",AccountKey="active-a",ThreadId="native-a",UpdatedUtc="2026-10-15T11:00:00Z"};var second=new CloudJob{Id="running-b",State="running",AccountKey="active-b",ThreadId="native-b",UpdatedUtc="2026-10-15T11:00:01Z"};jobs.Add(first);jobs.Add(second);
            var batch=CloudJobWorker.SelectPending(jobs);Assert(batch.Length==4&&batch[0].Id==first.Id&&batch[1].Id==second.Id&&batch.Any(job=>job.State=="queued"),"queued backlog starved active completion polls");
            var queued=jobs[0];Assert(!CloudJobWorker.CanStart(queued,jobs),"scheduler widened the two-job execution limit");first.State="completed";Assert(CloudJobWorker.CanStart(queued,jobs),"a completed poll did not release execution capacity");queued.AccountKey=second.AccountKey;Assert(!CloudJobWorker.CanStart(queued,jobs),"scheduler widened the per-account execution limit");
        });
        check("cloud scheduler does not hide an independent account behind an occupied account backlog",()=>{
            var active=new CloudJob{Id="running-a",State="running",AccountKey="account-a",ThreadId="native-a",UpdatedUtc="2026-10-15T08:00:00Z"};
            var a1=new CloudJob{Id="queued-a1",State="queued",AccountKey="account-a",UpdatedUtc="2026-10-15T09:00:00Z"};var a2=new CloudJob{Id="queued-a2",State="queued",AccountKey="account-a",UpdatedUtc="2026-10-15T09:00:01Z"};var b=new CloudJob{Id="queued-b",State="queued",AccountKey="account-b",UpdatedUtc="2026-10-15T10:00:00Z"};var result=new CloudJob{Id="return",State="completed",ReturnState="pending",SourceThread="busy-source",UpdatedUtc="2026-10-15T07:00:00Z"};var jobs=new[]{active,a1,a2,b,result};
            var batch=CloudJobWorker.SelectPending(jobs);Assert(batch.Length==4&&batch.Any(job=>job.Id==active.Id)&&batch.Any(job=>job.Id==result.Id)&&batch.Any(job=>job.Id==b.Id)&&Array.IndexOf(batch,b)<Array.IndexOf(batch,a1),"occupied account backlog starved the runnable second account");
            Assert(CloudJobWorker.CanStart(b,jobs)&&!CloudJobWorker.CanStart(a1,jobs),"fixture did not preserve per-account capacity");b.State="running";b.ThreadId="native-b";Assert(!CloudJobWorker.CanStart(a2,jobs),"second account admission widened global execution capacity");
        });
        check("cloud scheduler retains blocked queued requests so manual interventions cannot retire the worker",()=>{
            var uncertainA=new CloudJob{Id="uncertain-a",State="uncertain",AccountKey="account-a",ThreadId="native-a"};var uncertainB=new CloudJob{Id="uncertain-b",State="uncertain",AccountKey="account-b",ThreadId="native-b"};var queued=new CloudJob{Id="queued",State="queued",AccountKey="account-c"};var jobs=new[]{uncertainA,uncertainB,queued};
            Assert(!CloudJobWorker.CanStart(queued,jobs)&&CloudJobWorker.SelectPending(jobs).Single().Id==queued.Id&&CloudJobWorker.SelectRemotePending(jobs,job=>false).Single().Id==queued.Id,"all-blocked admission vanished from the worker liveness selection");
        });
        check("cloud scheduler preserves native polls behind blocked follow-up admissions",()=>{
            foreach(string state in new[]{"attention","prepared","creating"}){
                var blocked=Enumerable.Range(0,4).Select(i=>new CloudJob{Id="follow-up-"+i,Kind="follow-up",State=state,AccountKey="occupied-account",ThreadId="inherited-chat-"+i,UpdatedUtc="2026-10-15T08:00:0"+i+"Z"}).ToList();
                var running=new CloudJob{Id="running",State="running",AccountKey="occupied-account",ThreadId="active-chat",TurnId="active-turn",UpdatedUtc="2026-10-15T11:00:00Z"};blocked.Add(running);
                var independent=new CloudJob{Id="independent",State="queued",AccountKey="other-account",UpdatedUtc="2026-10-15T12:00:00Z"};blocked.Add(independent);
                var batch=CloudJobWorker.SelectPending(blocked,null,3);
                Assert(batch.Length==4&&batch[0].Id==running.Id&&batch.Any(job=>job.Id==independent.Id),"pre-prompt follow-up backlog starved native completion polls or independent admission");
                Assert(blocked.Take(4).All(job=>!CloudJobWorker.CanStart(job,blocked)),"follow-up fixture did not block on its occupied developer account");
            }
        });
        check("cloud return source rotation survives busy sources and successful backlogs",()=>{
            foreach(bool busy in new[]{false,true}){
                var f=new Fixture(root,++number,auth);var original=new List<CloudJob>();
                for(int index=0;index<4;index++){var job=f.Submit();job.State="completed";job.ReturnState="pending";job.Result="Fixture result";job.UpdatedUtc="2026-10-15T10:00:0"+index+"Z";f.Save(job);original.Add(job);}
                var idleSource=new RelaySession{Channel=f.Source.Channel,Thread="idle-source-chat",Home=f.Source.Home,Account=f.Source.Account};var other=f.Jobs.Submit(f.Spec(),idleSource,CancellationToken.None).GetAwaiter().GetResult();other.State="completed";other.ReturnState="pending";other.Result="Other fixture result";other.UpdatedUtc="2026-10-15T11:00:00Z";f.Save(other);
                var work=new[]{new CloudJob{Id="native-a",State="running",ThreadId="native-a",UpdatedUtc="2026-10-15T08:00:00Z"},new CloudJob{Id="native-b",State="running",ThreadId="native-b",UpdatedUtc="2026-10-15T08:00:01Z"},new CloudJob{Id="queued",State="queued",UpdatedUtc="2026-10-15T09:00:00Z"}};
                var transport=new SchedulerReturnTransport();if(busy)transport.Busy.Add(f.Source.Thread);
                var first=CloudJobWorker.SelectPending(f.Jobs.List().Concat(work)).Single(job=>job.ReturnState=="pending");Assert(first.SourceThread==f.Source.Thread,"fixture did not begin with oldest source");f.Jobs.Deliver(first.Id,transport,CancellationToken.None).GetAwaiter().GetResult();
                var after=f.Jobs.Get(first.Id);Assert(String.CompareOrdinal(after.UpdatedUtc,first.UpdatedUtc)>0&&after.ReturnState=="delivered"&&after.Result==first.Result,"source queue was not timestamped or changed its business result");
                var next=CloudJobWorker.SelectPending(f.Jobs.List().Concat(work)).Single(job=>job.ReturnState=="pending");Assert(next.Id==other.Id,"one source's remaining backlog starved another source after an attempt");f.Jobs.Deliver(next.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(transport.Sends.ContainsKey(idleSource.Thread)&&transport.Sends[idleSource.Thread]==1&&f.Jobs.Get(other.Id).ReturnState=="delivered","idle second source did not receive its result exactly once");
            }
        });
        check("cloud scheduler fills unused class slots without duplicating return sources",()=>{
            var queue=Enumerable.Range(0,7).Select(i=>new CloudJob{Id="queued-"+i,State="queued",UpdatedUtc="2026-10-15T10:00:0"+i+"Z"}).ToArray();Assert(CloudJobWorker.SelectPending(queue).Length==4,"unused running or return slots prevented queued scans");
            var returns=Enumerable.Range(0,7).Select(i=>new CloudJob{Id="return-"+i,State="completed",ReturnState="pending",SourceThread="source-"+i,UpdatedUtc="2026-10-15T10:00:0"+i+"Z"}).ToArray();Assert(CloudJobWorker.SelectPending(returns).Length==4,"unused runtime slots prevented independent returns");
            var aliases=new[]{new CloudJob{Id="alias-a",State="completed",ReturnState="pending",SourceChannel="channel-a",SourceThread="same-chat",SourceAccount="same-account",SourceHome=@"C:\fixture\home"},new CloudJob{Id="alias-b",State="completed",ReturnState="pending",SourceChannel="channel-b",SourceThread="same-chat",SourceAccount="same-account",SourceHome=@"c:\FIXTURE\home\"}};Assert(CloudJobWorker.SelectPending(aliases).Length==1,"channel or path aliases retried the same actual source chat twice");
        });
        check("cloud worker counts native attention against account and global concurrency limits",()=>{
            var queued=new CloudJob{Id="new",State="queued",AccountKey="account-a"};
            var attention=new CloudJob{Id="native-attention",State="attention",AccountKey="account-a",ThreadId="native-chat",TurnId="native-turn"};
            Assert(!CloudJobWorker.CanStart(queued,new[]{attention,queued}),"pending native intervention released its account slot");
            queued.AccountKey="account-b";Assert(CloudJobWorker.CanStart(queued,new[]{attention,queued}),"one active account blocked an independent second account");
            var running=new CloudJob{Id="running",State="running",AccountKey="account-c"};Assert(!CloudJobWorker.CanStart(queued,new[]{attention,running,queued}),"global two-job limit bypassed");
            attention.ThreadId=null;Assert(CloudJobWorker.CanStart(queued,new[]{attention,running,queued}),"blocked admission consumed execution capacity");
        });
        check("cloud worker reserves capacity for uncertain submissions with a known native chat",()=>{
            var queued=new CloudJob{Id="new",State="queued",AccountKey="account-a"};var uncertain=new CloudJob{Id="lost-reply",State="uncertain",AccountKey="account-a",ThreadId="native-chat"};
            Assert(!CloudJobWorker.CanStart(queued,new[]{queued,uncertain}),"uncertain task released its account while it could still be working");
            queued.AccountKey="account-b";Assert(CloudJobWorker.CanStart(queued,new[]{queued,uncertain}),"one uncertain task blocked an independent second account");
            var running=new CloudJob{Id="running",State="running",AccountKey="account-c"};Assert(!CloudJobWorker.CanStart(queued,new[]{queued,uncertain,running}),"uncertain known task released global execution capacity");
            uncertain.ThreadId=null;Assert(CloudJobWorker.CanStart(queued,new[]{queued,uncertain,running}),"uncreated uncertain chat invented an execution slot");
        });
        check("cloud jobs recheck revoked source bindings and actual source credentials before dispatch",()=>{
            foreach(bool changedAuth in new[]{false,true}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();
                if(changedAuth)File.WriteAllText(Path.Combine(f.Source.Home,"auth.json"),"{}");else f.Store.SetEnabled(f.Source.Channel,false);
                Assert(f.Advance(job.Id).State=="attention"&&f.Client.Starts==0&&f.Client.Turns==0,"revoked or changed source authorized cloud work");
            }
        });
        check("cloud jobs cannot bypass advanced local project rules through the cloud shortcut",()=>{
            var f=new Fixture(root,++number,auth);var accepted=f.Submit();var policy=new RelayPolicy();policy.Projects.Add(new RelayProject{Id="protected",Workspace=f.Accounts.Vault.Root});RelayPolicies.Save(f.Store,policy);
            Reject(()=>f.Submit());Assert(f.Advance(accepted.Id).State=="attention"&&f.Client.Starts==0&&f.Client.Turns==0,"advanced policy bypassed at admission or dispatch");
        });
        check("cloud jobs recheck published configuration after preparing a chat and before its first prompt",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.Busy=true;Assert(f.Advance(job.Id).State=="prepared"&&f.Client.Turns==0,"preparation should wait on busy native chat");
            f.Client.Busy=false;f.Configuration.LatestReadyVersionId="new-published-version";Assert(f.Advance(job.Id).State=="attention"&&f.Client.Starts==1&&f.Client.Turns==0,"prepared chat used a changed published environment");
        });
        check("cloud jobs enforce account capacity even when the caller advances jobs directly",()=>{
            var f=new Fixture(root,++number,auth);var first=f.Submit();f.Advance(first.Id);var second=f.Submit();
            Assert(f.Advance(second.Id).State=="queued"&&f.Client.Starts==1&&f.Client.Turns==1,"direct advance bypassed same-account concurrency");
        });
        check("cloud jobs keep capacity contention queued without creating a task",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();
            using(f.Store.Lease("cloud-capacity-v1"))Assert(f.Advance(job.Id).State=="queued"&&f.Client.Starts==0&&f.Client.Turns==0,"busy capacity lease blocked admission permanently or dispatched work");
            Assert(f.Advance(job.Id).State=="running"&&f.Client.Starts==1&&f.Client.Turns==1,"capacity release did not permit one dispatch");
        });
        check("cloud jobs never hold the capacity lease while remote configuration discovery waits",()=>{
            var f=new Fixture(root,++number,auth);var first=f.Submit();var second=f.Submit();int checks=0;var discovery=new TaskCompletionSource<CloudConfiguration>();
            var jobs=new CloudJobs(f.Store,i=>f.Client,(i,id,t)=>++checks==1?discovery.Task:Task.FromResult(f.Configuration));
            var waiting=jobs.Advance(first.Id,CancellationToken.None);
            try{
                using(f.Store.Lease("cloud-capacity-v1")){}
                Assert(jobs.Advance(second.Id,CancellationToken.None).GetAwaiter().GetResult().State=="running","independent metadata read held global dispatch lease");
            }finally{discovery.TrySetResult(f.Configuration);}
            Assert(waiting.GetAwaiter().GetResult().State=="queued"&&f.Client.Starts==1&&f.Client.Turns==1,"concurrent configuration completion bypassed account capacity");
        });
        check("cloud jobs recheck pause after awaiting the prepared environment and before its first prompt",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.Busy=true;Assert(f.Advance(job.Id).State=="prepared","fixture did not prepare its native chat");f.Client.Busy=false;
            var discovery=new TaskCompletionSource<CloudConfiguration>();var jobs=new CloudJobs(f.Store,i=>f.Client,(i,id,t)=>discovery.Task);var waiting=jobs.Advance(job.Id,CancellationToken.None);
            try{Assert(!waiting.IsCompleted&&f.Client.Turns==0,"fixture failed to wait for remote environment metadata");RelayDispatch.Set(f.Store,"paused");}finally{discovery.TrySetResult(f.Configuration);}
            Assert(waiting.GetAwaiter().GetResult().State=="prepared"&&f.Client.Starts==1&&f.Client.Turns==0,"pause during prepared metadata lookup was ignored");
            RelayDispatch.Set(f.Store,"running");Assert(jobs.Advance(job.Id,CancellationToken.None).GetAwaiter().GetResult().State=="running"&&f.Client.Starts==1&&f.Client.Turns==1,"resume recreated the chat or failed to send exactly once");
        });
        check("cloud worker gives first prompt preparation a separate bounded budget",()=>{
            foreach(string kind in new[]{"mission","follow-up"})foreach(string state in new[]{"queued","prepared","attention"})Assert(CloudJobWorker.CheckBudget(new CloudJob{Kind=kind,State=state,ThreadId=state=="queued"?null:"native-chat"}).TotalSeconds==120,"first prompt or correction inherited the short monitoring deadline");
            foreach(string state in new[]{"queued","prepared","attention","running"})Assert(CloudJobWorker.CheckBudget(new CloudJob{State=state,ThreadId="native-chat",TurnId="confirmed-turn"}).TotalSeconds==45,"confirmed turn retained an admission budget");
            foreach(string state in new[]{"creating","sending","uncertain","completed","failed","cancelled"})Assert(CloudJobWorker.CheckBudget(new CloudJob{State=state}).TotalSeconds==45,"non-admission state was allowed to start a long preparation");
        });
        check("cloud uncertain dispatch records safe backend status without forwarding exception text",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.StartTurnHook=()=>{throw new CloudBackendException("quota-limited",Secret,429,Secret);};job=f.Advance(job.Id);Assert(job.State=="uncertain"&&job.Error.Contains("phase=turn-start")&&job.Error.Contains("type=CloudBackendException")&&job.Error.Contains("code=quota-limited")&&job.Error.Contains("http=429")&&!job.Error.Contains(Secret),"uncertain HTTP diagnostic lost metadata or exposed payload");int starts=f.Client.Starts;f.Advance(job.Id);Assert(f.Client.Starts==starts,"backend uncertainty retransmitted its prompt");
        });
        check("cloud cancellation during first prompt stays uncertain and never retransmits",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";spec.ReasoningEffort="ultra";spec.PermissionProfile=":danger-full-access";var job=f.Submit(spec);int attempts=0;
            using(var deadline=new CancellationTokenSource()){
                f.Client.StartTurnHook=()=>{attempts++;deadline.Cancel();throw new OperationCanceledException(Secret,null,deadline.Token);};Reject(()=>f.Jobs.Advance(job.Id,deadline.Token).GetAwaiter().GetResult());job=f.Jobs.Get(job.Id);
                Assert(job.State=="uncertain"&&job.Error.Contains("phase=turn-start")&&job.Error.Contains("type=OperationCanceledException")&&job.Error.Contains("reason=timeout-or-cancelled")&&job.Error.Contains("tokenCancelled=true")&&!job.Error.Contains(Secret),"cancelled send was not durable uncertain with a redacted diagnostic");Assert(f.Client.ExecutionReads>0&&job.ObservedExecution!=null&&job.ObservedExecution.Verified,"deadline change bypassed required native execution verification");
            }
            f.Advance(job.Id);Assert(attempts==1&&f.Client.Starts==1,"cancelled uncertain prompt was automatically retransmitted");
        });
        check("cloud cancellation before dispatch remains attention with a safe preparation phase",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";spec.ReasoningEffort="ultra";spec.PermissionProfile=":danger-full-access";var job=f.Submit(spec);
            using(var deadline=new CancellationTokenSource()){
                f.Client.ExecutionReadHook=()=>{deadline.Cancel();throw new OperationCanceledException(Secret,null,deadline.Token);};Reject(()=>f.Jobs.Advance(job.Id,deadline.Token).GetAwaiter().GetResult());job=f.Jobs.Get(job.Id);Assert(job.State=="attention"&&job.TurnId==null&&f.Client.Turns==0&&job.Error.Contains("phase=execution-settings")&&job.Error.Contains("reason=timeout-or-cancelled")&&!job.Error.Contains(Secret),"preparation cancellation was falsely called a sent turn or leaked details");
            }
        });
        check("cloud dispatch diagnostics reject unsafe phase code and status metadata",()=>{
            string diagnostic=CloudJobs.AdvanceDiagnostic(Secret,new CloudBackendException(Secret,Secret,900,Secret),CancellationToken.None);Assert(diagnostic.Contains("phase=unknown")&&diagnostic.Contains("code=redacted")&&!diagnostic.Contains("http=")&&!diagnostic.Contains(Secret),"unsafe diagnostic metadata was copied");
            diagnostic=CloudJobs.AdvanceDiagnostic("turn-start",new OperationCanceledException(Secret),CancellationToken.None);Assert(diagnostic.Contains("reason=timeout-or-cancelled")&&diagnostic.Contains("tokenCancelled=false")&&!diagnostic.Contains(Secret),"independent operation cancellation was misclassified or exposed raw text");
        });
        check("cloud uncertain reconciliation attaches the exact completed turn without replaying its mandate",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();string fingerprint=job.Fingerprint,branch=job.Branch;f.Client.ThrowTurn=true;f.Advance(job.Id);f.Client.ThrowTurn=false;f.Client.TargetedTurn=new CloudTaskTurn{Id=f.Client.TurnId,Status="completed",Text="Exact result",TextComplete=true};
            job=f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None).GetAwaiter().GetResult();
            Assert(job.State=="completed"&&job.Result=="Exact result"&&job.TurnId==f.Client.TurnId&&job.Fingerprint==fingerprint&&job.Branch==branch&&f.Client.Starts==1&&f.Client.Turns==1,"reconciliation changed mandate, borrowed a result or resent work");
            var receipt=f.Jobs.Reconciliation(job.Id);Assert(receipt.State=="matched"&&receipt.Evidence=="client-id-and-prompt-sha256"&&receipt.Checks==1&&!Json.Write(receipt).Contains(Secret),"matching audit leaked input or was not durable");
            var restarted=new CloudJobs(f.Store,i=>f.Client,(i,id,t)=>Task.FromResult(f.Configuration));Assert(restarted.Reconcile(job.Id,f.Source,CancellationToken.None).GetAwaiter().GetResult().State=="completed"&&f.Client.Finds==1&&f.Client.Turns==1,"repeated operator reconciliation sent work or repeated a completed match");
        });
        check("cloud legacy empty native history is never proof that an uncertain prompt was not sent",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.StartTurnHook=()=>{throw new IOException(Secret);};f.Advance(job.Id);File.Delete(Path.Combine(f.Store.Root,"cloud-dispatch-"+job.Id+".dpapi"));
            job=f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None).GetAwaiter().GetResult();Assert(job.State=="uncertain"&&job.TurnId==null&&f.Jobs.Reconciliation(job.Id).State=="not-observed"&&f.Jobs.Reconciliation(job.Id).TransportProof=="legacy-unknown","empty legacy history was certified unsent");
            job=f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None,false,true).GetAwaiter().GetResult();f.Advance(job.Id);Assert(job.State=="uncertain"&&f.Client.Turns==0&&f.Jobs.Reconciliation(job.Id).ErrorCode=="reconciliation-no-resend-proof","explicit action bypassed missing transport proof");
        });
        check("cloud legacy uncertain sends can recover an exact reconstructed input but not a newer unrelated turn",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.ThrowTurn=true;f.Advance(job.Id);File.Delete(Path.Combine(f.Store.Root,"cloud-dispatch-"+job.Id+".dpapi"));f.Client.TargetedTurn=new CloudTaskTurn{Id=f.Client.TurnId,Status="completed",Text="Recovered exact legacy result",TextComplete=true};
            Assert(f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None).GetAwaiter().GetResult().State=="completed"&&f.Client.Turns==1,"legacy exact input could not be recovered without resend");
        });
        check("cloud reconciliation requires exact source and target bindings and preserves uncertainty on incomplete reads",()=>{
            foreach(string reason in new[]{"source","target","history","error"}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.ThrowTurn=true;f.Advance(job.Id);
                if(reason=="source"){Reject(()=>f.Jobs.Reconcile(job.Id,new RelaySession{Channel=f.Source.Channel,Thread="wrong-chat",Home=f.Source.Home,Account=f.Source.Account},CancellationToken.None).GetAwaiter().GetResult());Assert(f.Client.Finds==0,"foreign source could inspect evidence");continue;}
                if(reason=="target")f.Client.WrongFindBinding=true;if(reason=="history")f.Client.HistoryComplete=false;if(reason=="error")f.Client.FindHook=()=>{throw new IOException(Secret);};
                job=f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None).GetAwaiter().GetResult();Assert(job.State=="uncertain"&&job.TurnId==null&&f.Client.Turns==1&&!Json.Write(f.Jobs.Reconciliation(job.Id)).Contains(Secret),"unproven read attached a turn, resent or leaked exception");
            }
        });
        check("cloud reconciliation alone never resumes a proven unsent attempt without explicit operator action",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.StartTurnHook=()=>{throw new CloudTaskNotDispatchedException(new OperationCanceledException(Secret));};f.Advance(job.Id);
            job=f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None).GetAwaiter().GetResult();Assert(job.State=="uncertain"&&f.Jobs.Reconciliation(job.Id).TransportProof=="preflight-not-sent"&&f.Client.Turns==0,"read-only reconciliation resumed a mission");
            job=f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None,false,true).GetAwaiter().GetResult();Assert(job.State=="prepared"&&f.Jobs.Reconciliation(job.Id).ExplicitResumes==1&&f.Client.Turns==0,"explicit proven non-send did not preserve the same prepared job");
            f.Client.StartTurnHook=null;job=f.Advance(job.Id);Assert(job.State=="running"&&f.Client.Starts==1&&f.Client.Turns==1,"explicit retry recreated a chat or sent twice");
        });
        check("cloud explicit non-send recovery refuses changed environments busy chats and substituted receipts",()=>{
            foreach(string reason in new[]{"configuration","busy","receipt"}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.StartTurnHook=()=>{throw new CloudTaskNotDispatchedException(new IOException(Secret));};f.Advance(job.Id);
                if(reason=="configuration")f.Configuration.VersionId="new-version";if(reason=="busy")f.Client.Busy=true;
                if(reason=="receipt"){var value=f.Jobs.Reconciliation(job.Id);value.Binding="wrong-binding";f.Store.WriteRecord("cloud-dispatch-"+job.Id+".dpapi",value);Reject(()=>f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None,false,true).GetAwaiter().GetResult());}
                else Assert(f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None,false,true).GetAwaiter().GetResult().State=="uncertain","changed native preparation authorized retry");
                Assert(f.Client.Starts==1&&f.Client.Turns==0,"recovery created or dispatched work");
            }
        });
        check("cloud reconciled execution observations must be repaired before terminal delivery",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.Model="gpt-6.1-sol";spec.ReasoningEffort="ultra";spec.PermissionProfile=":danger-full-access";var job=f.Submit(spec);f.Client.ThrowTurn=true;f.Advance(job.Id);f.Client.ThrowTurn=false;f.Client.TargetedTurn=new CloudTaskTurn{Id=f.Client.TurnId,Status="completed",Text="Exact result",TextComplete=true};f.Client.Complete=true;f.Client.Observation.Model="different-model";
            job=f.Jobs.Reconcile(job.Id,f.Source,CancellationToken.None).GetAwaiter().GetResult();Assert(job.State=="attention"&&job.TurnId==f.Client.TurnId&&job.Result==null,"mismatched model completed reconciliation");job=f.Advance(job.Id);Assert(job.State=="attention"&&job.Result==null&&job.ReturnState!="pending","regular monitoring bypassed the reconciliation execution mismatch");f.Client.Observation.Model=spec.Model;Assert(f.Advance(job.Id).State=="completed"&&f.Client.Turns==1,"read-only settings repair did not finish the admitted turn");
        });
        check("cloud reconciliation operator capability cannot be inferred from a null source",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Client.ThrowTurn=true;f.Advance(job.Id);Reject(()=>f.Jobs.Reconcile(job.Id,null,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Client.Finds==0,"missing source silently acquired operator privilege");
        });
        check("cloud scheduled admission guard prevents four blocked legacy jobs from starving a ready adopted job",()=>{
            var backlog=Enumerable.Range(0,4).Select(index=>new CloudJob{Id="legacy_"+index,State="queued",AccountKey="idle_"+index,UpdatedUtc="2026-10-16T10:00:00Z"}).ToList();var adopted=new CloudJob{Id="adopted_ready",State="queued",AccountKey="ready-account",UpdatedUtc="2026-10-17T10:00:00Z"};backlog.Add(adopted);
            var active=new CloudJob{Id="active_poll",State="running",ThreadId="native",TurnId="turn",AccountKey="running-account"};backlog.Add(active);
            var batch=CloudJobWorker.SelectPending(backlog,backlog,3,job=>job.Id==adopted.Id,job=>job.Id==adopted.Id?1000:0);Assert(batch.Length==4&&batch.Contains(adopted)&&batch.Contains(active)&&Array.IndexOf(batch,adopted)<Array.FindIndex(batch,job=>job.Id.StartsWith("legacy_",StringComparison.Ordinal)),"legacy blocked claims starved admitted work or hid an active poll");
            var second=new CloudJob{Id="ready_low_priority",State="queued",AccountKey="another-free-account",UpdatedUtc="2026-10-15T10:00:00Z"};backlog.Add(second);batch=CloudJobWorker.SelectPending(backlog,backlog,3,job=>job.Id==adopted.Id||job.Id==second.Id,job=>job.Id==adopted.Id?1000:0);Assert(Array.IndexOf(batch,adopted)<Array.IndexOf(batch,second),"ready adopted priority was ignored");
        });
    }
}
