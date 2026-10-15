using System;
using System.Collections.Generic;
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
    private sealed class Fake:ICloudTasksConnection
    {
        internal string Account,Config=ConfigId,ThreadId="fixture-cloud-chat",TurnId="fixture-cloud-turn",LatestId;
        internal string Prompt,ClientMessageId,Final="Mission verified",ReadStatus;
        internal bool Complete,TextComplete=true,Busy,Approval,Input,ThrowStart,ThrowTurn,ThrowRead;
        internal int Connects,Starts,Reads,Turns;
        internal Action ReadHook;
        public Task Connect(CancellationToken token){Connects++;return Task.FromResult(0);}
        public Task<CloudTaskPage> List(string cursor,int limit,CancellationToken token){return Task.FromResult(new CloudTaskPage{Data=new List<CloudTaskThread>()});}
        public Task<CloudTaskThread> Start(string configId,string cwd,CancellationToken token){Starts++;if(ThrowStart)throw new IOException(Secret);return Task.FromResult(Thread());}
        public Task<CloudTaskThread> StartOnboarding(string configId,CancellationToken token){throw new InvalidOperationException("Cloud jobs must never start environment onboarding");}
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId,string prompt,CancellationToken token){throw new InvalidOperationException("Cloud jobs must never send environment onboarding prompts");}
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId,string prompt,string clientUserMessageId,CancellationToken token){throw new InvalidOperationException("Cloud jobs must never send environment onboarding prompts");}
        private CloudTaskThread Thread()
        {
            return new CloudTaskThread{Id=ThreadId,AccountKey=Account,VerifiedEnvironmentConfigId=Config,Status=ReadStatus??(Busy||Turns>0&&!Complete?"active":"idle"),AwaitingApproval=Approval,AwaitingInput=Input,LatestTurn=Turns==0?null:new CloudTaskTurn{Id=LatestId??TurnId,Status=Complete?"completed":"inProgress",Text=Complete?Final:"intermediate commentary",TextComplete=TextComplete}};
        }
        public Task<CloudTaskThread> Read(string threadId,CancellationToken token){Reads++;Assert(!String.IsNullOrEmpty(threadId),"attempted to read a null cloud chat");if(ReadHook!=null)ReadHook();if(ThrowRead)throw new InvalidOperationException(Secret);return Task.FromResult(Thread());}
        public Task<CloudTaskTurn> StartTurn(string threadId,string prompt,CancellationToken token){return StartTurn(threadId,prompt,null,token);}
        public Task<CloudTaskTurn> StartTurn(string threadId,string prompt,string clientUserMessageId,CancellationToken token){Turns++;Prompt=prompt;ClientMessageId=clientUserMessageId;if(ThrowTurn)throw new IOException(Secret);return Task.FromResult(new CloudTaskTurn{Id=TurnId,Status="inProgress"});}
        public void Dispose(){}
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
        internal Fixture(string root,int number,string auth)
        {
            Accounts=new AccountService(Path.Combine(root,"cloud-jobs-"+number));var profile=Accounts.Import(auth,"Fixture");Instance=Accounts.Instances.Create("Cloud fixture");Accounts.Instances.BindAccount(Instance,profile);
            string sourceHome=Path.Combine(Accounts.Vault.Root,"source-home");Directory.CreateDirectory(sourceHome);File.WriteAllText(Path.Combine(sourceHome,"auth.json"),auth);string sourceAccount=AuthIdentity.Parse(auth).Key;
            Store=new RelayStore(Path.Combine(Accounts.Vault.Root,"relay"));Store.Register(new RelayChannel{Id="source",Name="Source",AccountKey=sourceAccount,Home=sourceHome,Workspace=Accounts.Vault.Root,Enabled=true});
            Source=new RelaySession{Channel="source",Thread="source-chat",Account=sourceAccount,Home=sourceHome};Client.Account=Instance.AccountKey;
            Configuration=new CloudConfiguration{Id=ConfigId,VersionId="published-version",LatestReadyVersionId="published-version",VersionRevision=2,Status="ready"};
            Jobs=new CloudJobs(Store,i=>Client,(i,id,t)=>{ConfigurationReads++;return Task.FromResult(Configuration);});
        }
        internal CloudJobSpec Spec(bool returns=true){return new CloudJobSpec{Id=Guid.NewGuid().ToString("N"),Instance=Instance.Id,ConfigurationId=ConfigId,Title="Fixture mission",Prompt=Secret,ExplicitDelegation=true,ReturnToSource=returns};}
        internal CloudJob Submit(CloudJobSpec spec=null){return Jobs.Submit(spec??Spec(),Source,CancellationToken.None).GetAwaiter().GetResult();}
        internal CloudJob Advance(string id){return Jobs.Advance(id,CancellationToken.None).GetAwaiter().GetResult();}
        internal void Save(CloudJob job){Store.WriteRecord("cloud-job-"+job.Id+".dpapi",job);}
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
    public static void RunAll(Action<string,Action> check,string root,string auth)
    {
        int number=0;
        check("cloud jobs require explicit delegation and a published ready configuration",()=>{
            var f=new Fixture(root,++number,auth);var spec=f.Spec();spec.ExplicitDelegation=false;Reject(()=>f.Submit(spec));
            f.Configuration.VersionRevision=1;Reject(()=>f.Submit());f.Configuration.VersionRevision=2;f.Configuration.Draft=new CloudConfigurationDraft();Reject(()=>f.Submit());
            f.Configuration.Draft=null;f.Configuration.Id="other-configuration";Reject(()=>f.Submit());
            Assert(f.Client.Starts==0&&f.Client.Turns==0&&f.Jobs.List().Length==0,"invalid job was admitted or dispatched");
        });
        check("cloud jobs encrypt prompts and expose metadata without prompt bodies",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();string bytes=Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,"cloud-job-"+job.Id+".dpapi")));
            Assert(!bytes.Contains(Secret)&&f.Jobs.Get(job.Id).Prompt==Secret,"cloud prompt not encrypted");
            Assert(!Json.Write(CloudJobs.Summary(job,false)).Contains(Secret)&&job.Branch=="codex/switcher/"+job.Id,"metadata leaks prompt or branch not isolated");
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
        check("cloud jobs wait for the exact idle source chat and never repeat uncertain returns",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();f.Advance(job.Id);f.Client.Complete=true;job=f.Advance(job.Id);var transport=new ConsoleTests.Fake{Occupied=true};
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(transport.Sends==0&&f.Jobs.Get(job.Id).ReturnState=="pending","busy source interrupted");
            transport.Occupied=false;transport.WrongThread=true;Reject(()=>f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult());Assert(transport.Sends==0,"unverified source received result");
            transport.WrongThread=false;transport.UnknownSend=true;Reject(()=>f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult());f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();
            Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="uncertain","uncertain return replayed");
        });
        check("cloud results for headless sources wait in the owner's durable local queue",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Fixture result";job.ReturnState="pending";f.Save(job);
            Assert(f.Jobs.LocalReturns().Length==0,"desktop return consumed by local engine pump");
            var channel=f.Store.Channel(f.Source.Channel);channel.Provider="codex-headless";f.Store.Register(channel);
            var transport=new SourceStateTransport{Flag="waitingOnApproval"};
            f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();
            Assert(f.Jobs.LocalReturns().Single().Id==job.Id&&transport.Sends==0,"queue lost its result or interrupted busy source");
            transport.Flag=null;f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();
            transport.TurnStatus="inProgress";f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();
            Assert(f.Jobs.LocalReturns().Length==1&&f.Jobs.Get(job.Id).SourceReturnState=="running"&&transport.Sends==1,"local result processing lost its durable keepalive or was resent");
            transport.TurnStatus="completed";f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();
            Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="delivered"&&f.Jobs.LocalReturns().Length==0,"local return was duplicated or left pending");
        });
        check("headless cloud return queue preserves uncertainty and rechecks source credentials",()=>{
            foreach(bool lost in new[]{false,true}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Fixture result";job.ReturnState=lost?"sending":"pending";f.Save(job);
                var channel=f.Store.Channel(f.Source.Channel);channel.Provider="codex-headless";f.Store.Register(channel);
                var transport=new SourceStateTransport();
                if(!lost){File.WriteAllText(Path.Combine(f.Source.Home,"auth.json"),"{}");f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Jobs.Get(job.Id).ReturnState=="attention","invalid source blocked the whole return queue");}
                else{f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();f.Jobs.PumpLocalReturns(transport,CancellationToken.None).GetAwaiter().GetResult();}
                Assert(transport.Sends==0&&(!lost||f.Jobs.Get(job.Id).ReturnState=="uncertain"),"unverified or uncertain return replayed");
            }
        });
        check("cloud jobs mark interrupted result delivery uncertain without sending",()=>{
            var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Verified";job.ReturnState="sending";f.Save(job);var transport=new ConsoleTests.Fake();
            f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Jobs.Get(job.Id).ReturnState=="uncertain"&&transport.Sends==0,"crashed source return replayed");
        });
        check("cloud jobs never return into an idle source with native attention or an active turn",()=>{
            foreach(var state in new[]{"waitingOnApproval","waitingOnUserInput","inProgress","running"}){
                var f=new Fixture(root,++number,auth);var job=f.Submit();job.State="completed";job.Result="Verified";job.ReturnState="pending";f.Save(job);
                var transport=new SourceStateTransport();if(state.StartsWith("waiting"))transport.Flag=state;else transport.TurnStatus=state;
                f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(transport.Sends==0&&f.Jobs.Get(job.Id).ReturnState=="pending","idle status hid native source activity");
                transport.Flag=null;transport.TurnStatus="completed";f.Jobs.Deliver(job.Id,transport,CancellationToken.None).GetAwaiter().GetResult();Assert(transport.Sends==1&&f.Jobs.Get(job.Id).ReturnState=="delivered","verified idle source did not receive its result once");
            }
        });
        check("cloud worker polls an older running task before newer queued jobs and ignores pre-chat blocks",()=>{
            var jobs=Enumerable.Range(0,4).Select(i=>new CloudJob{Id="queued-"+i,State="queued",AccountKey="same",UpdatedUtc="2026-10-15T12:00:0"+i+"Z"}).ToList();
            jobs.Add(new CloudJob{Id="running",State="running",AccountKey="same",ThreadId="native-chat",UpdatedUtc="2026-10-15T11:00:00Z"});
            jobs.Insert(0,new CloudJob{Id="blocked-before-chat",State="attention",UpdatedUtc="2026-10-15T10:00:00Z"});
            var batch=CloudJobWorker.SelectPending(jobs);
            Assert(batch.Length==4&&batch[0].Id=="running"&&batch.All(j=>j.Id!="blocked-before-chat"),"queued jobs starved the active task or inert pre-chat block occupied polling slots");
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
    }
}
