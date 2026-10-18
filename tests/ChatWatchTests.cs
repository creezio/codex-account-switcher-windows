using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class ChatWatchTests
{
    private const string Account = "fixture-watch-account";
    private const string PrivateError = "PRIVATE_WATCH_ERROR_MUST_NOT_BECOME_A_NOTIFICATION";
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected watch rejection");}
    private sealed class Fixture
    {
        internal readonly RelayStore Store;
        internal readonly string Home,Workspace,ThreadId;
        internal DateTime Now=new DateTime(2026,10,17,12,0,0,DateTimeKind.Utc);
        internal ChatWatches Watches;
        internal ChatWatchRecord Record;
        internal object Snapshot;
        internal Exception ReadError;
        internal Action BeforeRead;
        internal int Reads,Validations;
        internal string ExpectedAccount=Account;
        internal Fixture(string root,string name)
        {
            string folder=Path.Combine(root,"chat-watches",name);
            Home=Path.Combine(folder,"codex-home");Workspace=Path.Combine(folder,"workspace");
            Directory.CreateDirectory(Home);Directory.CreateDirectory(Workspace);
            Store=new RelayStore(Path.Combine(folder,"relay"));ThreadId=Guid.NewGuid().ToString();
            Snapshot=Native("active","inProgress");Watches=Monitor();
            Record=Watches.Save(new ChatWatchRecord{Id=Guid.NewGuid().ToString("N"),Name="Fixture mission",InstanceId=new String('a',32),AccountKey=Account,ThreadId=ThreadId,Home=Home,Workspace=Workspace,Enabled=true});
        }
        internal ChatWatches Monitor()
        {
            return new ChatWatches(Store,(record,token)=>{
                token.ThrowIfCancellationRequested();Reads++;if(BeforeRead!=null)BeforeRead();
                if(ReadError!=null)throw ReadError;return Task.FromResult(Snapshot);
            },()=>Now,record=>{
                Validations++;
                if(record.AccountKey!=ExpectedAccount||!RelayStore.SamePath(record.Home,Home))throw new InvalidOperationException(PrivateError);
            });
        }
        internal object Native(string threadState,string turnState,string flag=null,string turnId="fixture-turn",string threadId=null)
        {
            return Json.Read<object>(Json.Write(new{schemaVersion=1,thread=new{id=threadId??ThreadId,title="Fixture mission",status=new{type=threadState,activeFlags=flag==null?new string[0]:new[]{flag}}},turns=new[]{new{id=turnId,status=turnState,items=new object[0]}}}));
        }
        internal ChatWatchRecord Tick(){return Watches.Check(Record.Id,CancellationToken.None).GetAwaiter().GetResult();}
        internal ChatWatchRecord Advance(int seconds=60){Now=Now.AddSeconds(seconds);return Tick();}
        internal string Completion(string status)
        {
            string path=Path.Combine(Workspace,"mission-state.json");
            File.WriteAllText(path,Json.Write(new{status="published_qualified_remaining_business_blocked",completion=new{entire18LotPlan=status}}),new UTF8Encoding(false));
            return path;
        }
    }
    internal static void RunAll(Action<string,Action> check,string root,string firstAuth,string secondAuth)
    {
        check("chat watch observes running chat without creating a turn",()=>{
            var f=new Fixture(root,"running");var state=f.Tick();
            Assert(state.State=="running"&&f.Reads==1,"active chat was not read once");
            Assert(String.IsNullOrEmpty(state.AlertUtc)&&!String.IsNullOrEmpty(state.LastCheckedUtc),"healthy work raised an alert or missed its timestamp");
            Assert(f.Store.Messages().Count==0,"passive watch enqueued a relay message");
        });
        check("chat watch surfaces native approval and user-input waits immediately",()=>{
            foreach(string flag in new[]{"waitingOnApproval","waitingOnUserInput"}){
                var f=new Fixture(root,flag);f.Snapshot=f.Native("active","inProgress",flag);var state=f.Tick();
                Assert(state.State==(flag=="waitingOnApproval"?"waiting-approval":"waiting-input"),"native wait flag was hidden by active turn");
                Assert(!String.IsNullOrEmpty(state.AlertUtc)&&!String.IsNullOrEmpty(state.EpisodeId),"waiting chat did not produce a durable alert episode");
            }
        });
        check("chat watch alerts on a stopped chat without assuming the mission finished",()=>{
            var f=new Fixture(root,"stopped");f.Snapshot=f.Native("idle","completed");var state=f.Tick();
            Assert(state.State=="stopped"&&!String.IsNullOrEmpty(state.AlertUtc),"ended turn falsely completed the mission");
        });
        check("chat watch reports a failed turn even when the thread reports idle",()=>{
            var f=new Fixture(root,"failed");f.Snapshot=f.Native("idle","failed");var state=f.Tick();
            Assert(state.State=="failed"&&!String.IsNullOrEmpty(state.AlertUtc),"failed native turn was classified as ordinary completion");
        });
        check("chat watch uses the latest native turn instead of an older failed turn",()=>{
            var f=new Fixture(root,"newest-turn");
            f.Snapshot=Json.Read<object>(Json.Write(new{thread=new{id=f.ThreadId,status=new{type="active",activeFlags=new string[0]}},turns=new[]{new{id="latest",status="inProgress"},new{id="old-failure",status="failed"}}}));
            Assert(f.Tick().State=="running","a previous failed turn blocked current execution");
        });
        check("chat watch never treats a partial publication as the complete project",()=>{
            var f=new Fixture(root,"partial-publication");var definition=f.Watches.Get(f.Record.Id);
            definition.CompletionFile=f.Completion("not_complete");definition.CompletionProperty="completion.entire18LotPlan";definition.CompletionExpectedValue="complete";f.Watches.Save(definition);
            f.Snapshot=f.Native("idle","completed");var partial=f.Tick();
            Assert(partial.State=="stopped"&&!String.IsNullOrEmpty(partial.AlertUtc),"published-but-incomplete state hid an unfinished project");
            f.Completion("complete");var complete=f.Advance();
            Assert(complete.State=="completed","the explicit project completion criterion was ignored");
        });
        check("chat watch requires the configured completion property rather than a final-text claim",()=>{
            var f=new Fixture(root,"final-claim");var definition=f.Watches.Get(f.Record.Id);
            definition.CompletionFile=f.Completion("not_complete");definition.CompletionProperty="completion.entire18LotPlan";definition.CompletionExpectedValue="complete";f.Watches.Save(definition);
            f.Snapshot=Json.Read<object>(Json.Write(new{thread=new{id=f.ThreadId,status=new{type="idle",activeFlags=new string[0]}},turns=new[]{new{id="claim",status="completed",items=new[]{new{type="agentMessage",phase="final_answer",text="Tout est fini et publié, 18 lots terminés."}}}}}));
            Assert(f.Tick().State=="stopped","unverified prose bypassed the configured completion criterion");
        });
        check("chat watch read failures replace stale running state and alert on the second check",()=>{
            var f=new Fixture(root,"read-failures");Assert(f.Tick().State=="running","fixture never ran");
            f.ReadError=new IOException(PrivateError);var first=f.Advance();
            Assert(first.State=="unavailable"&&first.ConsecutiveErrors==1&&String.IsNullOrEmpty(first.AlertUtc),"first read failure claimed healthy running or alerted without grace");
            var second=f.Advance();Assert(second.State=="unavailable"&&second.ConsecutiveErrors==2&&!String.IsNullOrEmpty(second.AlertUtc),"lost surveillance was hidden after two failed checks");
            Assert(!Json.Write(second).Contains(PrivateError),"raw private transport error leaked into the durable watch/notification");
        });
        check("chat watch rejects a snapshot from another thread",()=>{
            var f=new Fixture(root,"wrong-thread");f.Snapshot=f.Native("active","inProgress",null,"foreign-turn",Guid.NewGuid().ToString());
            Assert(f.Tick().State=="unavailable"&&f.Advance().ConsecutiveErrors==2,"a foreign thread was accepted as the watched chat");
        });
        check("chat watch deduplicates an alert episode across worker restarts",()=>{
            var f=new Fixture(root,"restart-dedup");f.Snapshot=f.Native("idle","completed");var original=f.Tick();
            f.Watches.MarkNotified(original.Id,original.EpisodeId);f.Watches=f.Monitor();var repeated=f.Advance();
            Assert(repeated.EpisodeId==original.EpisodeId&&repeated.AlertUtc==original.AlertUtc,"restart created a duplicate stop episode");
            Assert(!String.IsNullOrEmpty(repeated.NotifiedUtc),"restart lost durable notification acknowledgement");
        });
        check("chat watch recovery resets errors and allows an alert for a later stop",()=>{
            var f=new Fixture(root,"recovery");f.Snapshot=f.Native("idle","completed");var stopped=f.Tick();f.Watches.MarkNotified(stopped.Id,stopped.EpisodeId);
            f.Snapshot=f.Native("active","inProgress",null,"new-turn");var running=f.Advance();
            Assert(running.State=="running"&&running.ConsecutiveErrors==0,"recovered execution stayed blocked");
            f.Snapshot=f.Native("idle","completed",null,"new-turn");var again=f.Advance();
            Assert(again.State=="stopped"&&again.EpisodeId!=stopped.EpisodeId&&!String.IsNullOrEmpty(again.AlertUtc)&&String.IsNullOrEmpty(again.NotifiedUtc),"second stop was swallowed by the previous notification");
        });
        check("chat watch flags unchanged running activity after fifteen minutes without calling it stopped",()=>{
            var f=new Fixture(root,"stalled-threshold");f.Tick();var before=f.Advance(14*60);
            Assert(before.State=="running"&&String.IsNullOrEmpty(before.AlertUtc),"long-running work was flagged before its progress threshold");
            var stalled=f.Advance();Assert(stalled.State=="stalled"&&!String.IsNullOrEmpty(stalled.AlertUtc),"unchanged active work was never surfaced for attention");
            f.Watches.MarkNotified(stalled.Id,stalled.EpisodeId);var repeated=f.Advance();
            Assert(repeated.State=="stalled"&&repeated.EpisodeId==stalled.EpisodeId&&!String.IsNullOrEmpty(repeated.NotifiedUtc),"unchanged stalled activity created repeated notification episodes");
        });
        check("chat watch renewed native progress recovers an activity alert",()=>{
            var f=new Fixture(root,"stalled-recovery");f.Tick();var stalled=f.Advance(15*60);Assert(stalled.State=="stalled","fixture did not stall");
            f.Snapshot=f.Native("active","inProgress",null,"new-progress-turn");var progressed=f.Advance();
            Assert(progressed.State=="running"&&String.IsNullOrEmpty(progressed.AlertUtc)&&progressed.LastProgressUtc==f.Now.ToString("o"),"new native progress did not clear the activity alert");
        });
        check("chat watch activity threshold can be disabled for intentionally long operations",()=>{
            var f=new Fixture(root,"stalled-disabled");var definition=f.Watches.Get(f.Record.Id);definition.NoProgressMinutes=0;f.Watches.Save(definition);f.Tick();
            var later=f.Advance(240*60);Assert(later.State=="running"&&String.IsNullOrEmpty(later.AlertUtc),"disabled progress threshold still alerted on a long-running operation");
        });
        check("chat watch leaving a long approval wait starts a fresh activity baseline",()=>{
            var f=new Fixture(root,"stalled-after-approval");f.Tick();f.Snapshot=f.Native("active","inProgress","waitingOnApproval");f.Advance();f.Advance(20*60);
            f.Snapshot=f.Native("active","inProgress");var resumed=f.Advance();
            Assert(resumed.State=="running"&&String.IsNullOrEmpty(resumed.AlertUtc),"time awaiting approval was counted as stalled execution after approval");
        });
        check("chat watch restored connectivity starts a fresh activity baseline",()=>{
            var f=new Fixture(root,"stalled-after-unavailable");f.Tick();f.ReadError=new IOException("offline");f.Advance(20*60);f.Advance();
            f.ReadError=null;var resumed=f.Advance();
            Assert(resumed.State=="running"&&String.IsNullOrEmpty(resumed.AlertUtc),"unobserved offline time was counted as stalled execution after reconnect");
        });
        check("chat watch concurrent acknowledgement wins over an in-flight read",()=>{
            var f=new Fixture(root,"ack-race");f.Snapshot=f.Native("idle","completed");f.Tick();
            f.BeforeRead=()=>f.Watches.Acknowledge(f.Record.Id);var checkedState=f.Advance();var persisted=f.Monitor().Get(f.Record.Id);
            Assert(!String.IsNullOrEmpty(checkedState.AcknowledgedUtc)&&persisted.AcknowledgedUtc==checkedState.AcknowledgedUtc,"in-flight observation overwrote user acknowledgement");
        });
        check("chat watch concurrent disabling wins over an in-flight read",()=>{
            var f=new Fixture(root,"disable-race");f.BeforeRead=()=>f.Watches.SetEnabled(f.Record.Id,false);f.Tick();
            var persisted=f.Monitor().Get(f.Record.Id);Assert(!persisted.Enabled&&persisted.State=="paused","in-flight observation re-enabled a disabled watch");
            f.BeforeRead=null;int previous=f.Reads;f.Advance();Assert(f.Reads==previous,"disabled watch contacted Codex");
        });
        check("chat watch wrong account binding prevents every native read",()=>{
            var f=new Fixture(root,"wrong-account");f.ExpectedAccount="different-account";var first=f.Tick();f.Advance();
            Assert(f.Reads==0&&first.State=="unavailable","native read escaped the pinned account check");
        });
        check("chat watch production binding rejects an actual changed profile before the reader",()=>{
            string folder=Path.Combine(root,"chat-watches","real-profile-binding");var accounts=new AccountService(folder);
            var profile=accounts.Import(firstAuth,"Watch fixture");var instance=accounts.Instances.Create("Watch fixture");accounts.Instances.BindAccount(instance,profile);
            string home=accounts.Instances.Home(instance),workspace=Path.Combine(folder,"workspace");Directory.CreateDirectory(workspace);
            var store=new RelayStore(Path.Combine(accounts.Vault.Root,"relay"));int reads=0;
            var watches=new ChatWatches(store,(record,token)=>{reads++;return Task.FromResult<object>(new{});},()=>new DateTime(2026,10,17,12,0,0,DateTimeKind.Utc));
            var watch=watches.Save(new ChatWatchRecord{Id=Guid.NewGuid().ToString("N"),Name="Real binding fixture",InstanceId=instance.Id,AccountKey=profile.Key,ThreadId=Guid.NewGuid().ToString(),Home=home,Workspace=workspace,Enabled=true});
            File.WriteAllText(Path.Combine(home,"auth.json"),secondAuth);
            var changed=watches.Check(watch.Id,CancellationToken.None).GetAwaiter().GetResult();
            Assert(reads==0&&changed.State=="unavailable","a changed real auth.json reached the native reader");
        });
        check("chat watch a binding changed during read is not accepted",()=>{
            var f=new Fixture(root,"account-changed-during-read");f.BeforeRead=()=>f.ExpectedAccount="changed-account";
            Assert(f.Tick().State=="unavailable","read was persisted after its account binding changed");
        });
        check("chat watch disabled and confirmed-complete definitions persist without inference",()=>{
            var f=new Fixture(root,"complete-and-disable");var complete=f.Watches.ConfirmComplete(f.Record.Id);
            Assert(complete.State=="completed","explicit completion did not persist");
            f.Watches=f.Monitor();f.Watches.CheckDue(CancellationToken.None).GetAwaiter().GetResult();
            Assert(f.Reads==0&&f.Watches.Get(f.Record.Id).State=="completed","confirmed-complete watch kept contacting Codex");
        });
        check("chat watch due checks obey the sixty-second interval",()=>{
            var f=new Fixture(root,"cadence");f.Watches.CheckDue(CancellationToken.None).GetAwaiter().GetResult();Assert(f.Reads==1,"first due watch was not read");
            f.Now=f.Now.AddSeconds(59);f.Watches.CheckDue(CancellationToken.None).GetAwaiter().GetResult();Assert(f.Reads==1,"watch polled before sixty seconds");
            f.Now=f.Now.AddSeconds(1);f.Watches.CheckDue(CancellationToken.None).GetAwaiter().GetResult();Assert(f.Reads==2,"watch did not poll at sixty seconds");
        });
        check("chat watch malformed native state is unavailable rather than a fictional stop",()=>{
            var f=new Fixture(root,"malformed-snapshot");f.Snapshot=new{thread=new{id=f.ThreadId},turns=new object[0]};
            Assert(f.Tick().State=="unavailable","missing native status was invented as a normal stop");
        });
        check("chat watch unknown native state does not invent a stopped or complete project",()=>{
            var f=new Fixture(root,"unknown-snapshot");f.Snapshot=f.Native("new-unrecognized-native-state","completed");
            Assert(f.Tick().State=="unavailable","unrecognized native status was guessed as stopped/completed");
        });
        check("chat watch unloaded native thread cannot borrow a stale active turn",()=>{
            var f=new Fixture(root,"unloaded-snapshot");f.Snapshot=f.Native("notLoaded","inProgress");
            Assert(f.Tick().State=="unavailable","an unloaded native chat falsely appeared to still be running");
        });
        check("chat watch malformed completion evidence cannot hide running work or an incomplete stop",()=>{
            var f=new Fixture(root,"bad-completion-json");var definition=f.Watches.Get(f.Record.Id);
            definition.CompletionFile=f.Completion("not_complete");definition.CompletionProperty="completion.entire18LotPlan";definition.CompletionExpectedValue="complete";f.Watches.Save(definition);
            File.WriteAllText(definition.CompletionFile,"{invalid JSON");
            Assert(f.Tick().State=="running","bad completion evidence was confused with loss of the live chat");
            f.Snapshot=f.Native("idle","completed");var stopped=f.Advance();
            Assert(stopped.State=="stopped"&&!String.IsNullOrEmpty(stopped.AlertUtc),"unreadable completion evidence hid a premature stop");
        });
        check("chat watch immutable account home and thread cannot be silently repinned",()=>{
            var f=new Fixture(root,"immutable-binding");var changed=f.Watches.Get(f.Record.Id);changed.ThreadId=Guid.NewGuid().ToString();Reject(()=>f.Watches.Save(changed));
            changed=f.Watches.Get(f.Record.Id);changed.AccountKey="another-account";Reject(()=>f.Watches.Save(changed));
            Assert(f.Watches.Get(f.Record.Id).ThreadId==f.ThreadId&&f.Reads==0,"definition mutation changed the watched identity");
        });
        check("chat watch worker admission only starts its own monitor when needed",()=>{
            int spawns=0;
            ChatWatchWorker.EnsureCore(()=>false,()=>false,()=>spawns++);
            ChatWatchWorker.EnsureCore(()=>true,()=>true,()=>spawns++);
            Assert(spawns==0,"disabled or already-running monitor spawned a process");
            ChatWatchWorker.EnsureCore(()=>true,()=>false,()=>spawns++);
            Assert(spawns==1,"enabled missing monitor was not started exactly once");
        });
    }
}
