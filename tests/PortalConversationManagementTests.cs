using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

// All accounts, native replies and queue admissions are fictional. No real
// scheduler process, desktop pipe, app-server, registry entry or turn is used.
internal static class PortalConversationManagementTests
{
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static object Shape(object value){return Json.Read<object>(Json.Write(value));}
    private static object Field(object value,string key){return Json.Get(Shape(value),key);}
    private static string Text(object value,string key){return Json.Str(Field(value,key));}
    private static void Reject(Action action,int status=0){try{action();}catch(WebhookException e){if(status==0||status==e.StatusCode)return;throw;}catch(InvalidOperationException){if(status==0)return;throw;}catch(IOException){if(status==0)return;throw;}throw new Exception("Expected scoped management rejection");}
    private sealed class Rpc:IThreadProfileRpc
    {
        internal readonly Fixture Owner;internal readonly List<string> Calls=new List<string>();internal object LastSet;
        internal bool WrongThread,WrongWorkspace,WrongGoal,FailAfterSet;internal int Sets;
        internal Rpc(Fixture owner){Owner=owner;}
        public Task<object> Call(string method,object arguments,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add(method);
            Assert(new[]{"thread/read","thread/goal/get","thread/goal/set","thread/goal/clear"}.Contains(method),"Goal management tried resume, turn, prompt or an invented RPC");
            Assert(Text(arguments,"threadId")==Owner.Target.ThreadId,"Native request escaped selected thread");
            if(method=="thread/read")return Task.FromResult(Shape(new{thread=new{id=WrongThread?Guid.NewGuid().ToString("D"):Owner.Target.ThreadId,cwd=WrongWorkspace?Path.GetDirectoryName(Owner.Target.Workspace):Owner.Target.Workspace,status=new{type="idle"}}}));
            if(method=="thread/goal/get")return Task.FromResult(Shape(new{goal=WrongGoal?new{threadId=Guid.NewGuid().ToString("D"),objective="wrong",status="paused",tokenBudget=(long?)null,tokensUsed=0,timeUsedSeconds=0,createdAt=1,updatedAt=1}:(object)Owner.NativeGoal}));
            Assert(Text(arguments,"origin")=="user","Native mutation omitted explicit human origin");
            Sets++;LastSet=Shape(arguments);
            if(method=="thread/goal/clear"){Owner.NativeGoal=null;return Task.FromResult(Shape(new{}));}
            var values=Json.Obj(Shape(arguments));var next=Owner.NativeGoal==null?new Dictionary<string,object>{{"threadId",Owner.Target.ThreadId},{"objective",""},{"status","active"},{"tokenBudget",null},{"tokensUsed",0L},{"timeUsedSeconds",0L},{"createdAt",1L},{"updatedAt",1L}}:Json.Obj(Shape(Owner.NativeGoal));
            if(values.ContainsKey("objective")&&Json.Str(values["objective"])!=Json.Str(Json.Get(next,"objective"))){next["tokensUsed"]=0L;next["timeUsedSeconds"]=0L;next["objective"]=values["objective"];}
            if(values.ContainsKey("status"))next["status"]=values["status"];if(values.ContainsKey("tokenBudget"))next["tokenBudget"]=values["tokenBudget"];next["updatedAt"]=Convert.ToInt64(next["updatedAt"],CultureInfo.InvariantCulture)+1;Owner.NativeGoal=Shape(next);
            if(FailAfterSet){FailAfterSet=false;throw new IOException("Fictional response lost after native commit");}
            return Task.FromResult(Shape(new{goal=Owner.NativeGoal}));
        }
        public void Dispose(){}
    }
    private sealed class Fixture
    {
        internal readonly RelayStore Store;internal readonly PortalConversationTarget Target;internal readonly PortalConversationManagement Service;internal readonly Rpc Native;
        internal DateTime Clock=new DateTime(2026,10,18,8,0,0,DateTimeKind.Utc);internal object NativeGoal;internal bool Revoked,Changed,FailAfterAdmission,Local;
        internal readonly List<string> Admissions=new List<string>();internal int Authorizations;internal int Connects;
        internal Fixture(string sandbox,bool local=false)
        {
            Local=local;string folder=Path.Combine(sandbox,"cm-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(folder);Store=new RelayStore(Path.Combine(folder,"relay"));string home=Path.Combine(folder,"home"),workspace=Path.Combine(folder,"workspace"),instance=Path.Combine(folder,"instance");foreach(string path in new[]{home,workspace,instance})Directory.CreateDirectory(path);
            Target=new PortalConversationTarget{Local=local,RouteId=local?null:Guid.NewGuid().ToString("N"),Actor=local?null:new string('a',64),InstanceId=Guid.NewGuid().ToString("N"),ChatId="01990000-0000-7000-8000-"+Guid.NewGuid().ToString("N").Substring(0,12),AccountKey="fictional-account",Home=home,Workspace=workspace,InstanceFolder=instance,Access=local?"local":"headless",PermissionProfile="inherit",GrantRevision=local?0:2,RouteRevision=local?0:3,Executable=Path.Combine(folder,"never-started.exe")};Target.ThreadId=Target.ChatId;
            if(!local)Store.WriteRecord("webhook-routes-v1.dpapi",new List<WebhookRoute>{new WebhookRoute{Id=Target.RouteId,Revision=Target.RouteRevision,Enabled=true,Workspace=workspace,Home=home,AccountKey=Target.AccountKey,InstanceId=Target.InstanceId}});
            NativeGoal=Shape(new{threadId=Target.ThreadId,objective="Fictional durable objective",status="paused",tokenBudget=(long?)50000,tokensUsed=17L,timeUsedSeconds=9L,createdAt=1L,updatedAt=2L});Native=new Rpc(this);
            Service=new PortalConversationManagement(Store,(route,actor)=>{Authorizations++;if(Revoked)throw new WebhookException(403,"Fictional authorization revoked");},new PortalConversationManagementDependencies{
                Resolve=(route,actor,id,chat)=>{Assert(id==Target.InstanceId&&chat==Target.ChatId,"Remote selection was promoted");if(!local)Assert(route==Target.RouteId&&actor==Target.Actor,"Administration scope changed");var copy=Json.Read<PortalConversationTarget>(Json.Write(Target));copy.GoalUsesDesktop=Target.GoalUsesDesktop;return copy;},
                Verify=t=>{if(Revoked||Changed||t.GrantRevision!=Target.GrantRevision||t.RouteRevision!=Target.RouteRevision||t.PolicyRevision!=Target.PolicyRevision||t.SiteOrigin!=Target.SiteOrigin)throw new WebhookException(403,"Fictional pin revoked");Assert(t.Local==Local&&t.InstanceId==Target.InstanceId&&t.ThreadId==Target.ThreadId&&t.AccountKey==Target.AccountKey&&RelayStore.SamePath(t.Home,Target.Home)&&RelayStore.SamePath(t.Workspace,Target.Workspace),"Stored target pins changed");},
                GoalConnect=t=>{Connects++;return Native;},UtcNow=()=>Clock,
                Enqueue=(target,job,token)=>{
                    token.ThrowIfCancellationRequested();Admissions.Add(job.Id);
                    if(!local){string file="webhook-job-"+job.Id+".dpapi";if(!File.Exists(Path.Combine(Store.Root,file)))Store.WriteRecord(file,job);}
                    if(FailAfterAdmission){FailAfterAdmission=false;throw new IOException("Fictional admission response lost");}
                    return Task.FromResult(job);
                }
            });
        }
        internal object Read(){return Local?Service.ReadLocal(Target.InstanceId,Target.ChatId):Service.Read(Target.RouteId,Target.Actor,Target.InstanceId,Target.ChatId);}
        internal object Goal(object input,string key="goal-key"){return Local?Service.GoalLocal(Target.InstanceId,Target.ChatId,key,Shape(input)):Service.Goal(Target.RouteId,Target.Actor,Target.InstanceId,Target.ChatId,key,Shape(input));}
        internal object Save(object input,string key="schedule-key"){return Local?Service.SaveScheduleLocal(Target.InstanceId,Target.ChatId,key,Shape(input)):Service.SaveSchedule(Target.RouteId,Target.Actor,Target.InstanceId,Target.ChatId,key,Shape(input));}
        internal object Delete(string id,long revision,string key="delete-key"){return Local?Service.DeleteScheduleLocal(Target.InstanceId,Target.ChatId,id,key,Shape(new{revision=revision})):Service.DeleteSchedule(Target.RouteId,Target.Actor,Target.InstanceId,Target.ChatId,id,key,Shape(new{revision=revision}));}
        internal string Version(){return Text(Field(Read(),"goal"),"version");}
        internal object Schedule(string status="PAUSED",int interval=5){return new{name="Fictional scheduled task",prompt="Fictional instruction, never sent to any account",status=status,frequency="interval",intervalMinutes=interval,timeZoneId="UTC"};}
        internal PortalConversationSchedule Only(){string path=Directory.GetFiles(Store.Root,"conversation-schedule-*.dpapi").Single();return Store.ReadRecord<PortalConversationSchedule>(Path.GetFileName(path));}
        internal void Pump(){Service.PumpDue(CancellationToken.None).GetAwaiter().GetResult();}
        internal void NoGoal(){Target.GoalIssue="Fictional desktop already owns its native engine";}
    }
    private static DesktopGoalProxyState BridgePin(Fixture f)
    {return new DesktopGoalProxyState{SchemaVersion=1,InstanceId=f.Target.InstanceId,LaunchId=Guid.NewGuid().ToString("N"),AccountKey=f.Target.AccountKey,Home=f.Target.Home,PipeName="creezio-desktop-goal-"+Guid.NewGuid().ToString("N"),ProxyPid=71,ProxyStartedTicks=100,NativePid=72,NativeStartedTicks=200,HostPid=73,HostStartedTicks=300,DesktopPid=74,DesktopStartedTicks=400,Running=true,Ready=true};}
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("management reads real native goal methods without turn or resume",()=>{
            var f=new Fixture(sandbox);var view=f.Read();Assert(Text(Field(view,"goal"),"objective")=="Fictional durable objective"&&Text(Field(view,"goal"),"version").Length==64,"Native goal was replaced with local invented state");Assert(f.Native.Calls.SequenceEqual(new[]{"thread/read","thread/goal/get"})&&f.Admissions.Count==0,"Read started work");
        });
        check("management status patch preserves objective usage and sends user origin",()=>{
            var f=new Fixture(sandbox);string version=f.Version();var body=new{action="set",expectedVersion=version,status="blocked"};var result=f.Goal(body);
            Assert(Text(Field(result,"goal"),"status")=="blocked"&&Convert.ToInt64(Field(Field(result,"goal"),"tokensUsed"))==17,"Status update reset native usage");Assert(Json.Get(f.Native.LastSet,"objective")==null&&Text(f.Native.LastSet,"origin")=="user","Patch resent or invented objective");int calls=f.Native.Calls.Count;f.Goal(body);Assert(f.Native.Sets==1&&f.Native.Calls.Count==calls,"Idempotent retry repeated native mutation");
        });
        check("management new objective and explicit unlimited budget use native set",()=>{
            var f=new Fixture(sandbox);string version=f.Version();f.Goal(new{action="set",expectedVersion=version,objective="Replacement fictional outcome",status="paused",tokenBudget=(long?)null});Assert(Text(f.NativeGoal,"objective")=="Replacement fictional outcome"&&Field(f.NativeGoal,"tokenBudget")==null&&Convert.ToInt64(Field(f.NativeGoal,"tokensUsed"))==0,"Replacement/native budget behavior not preserved");
        });
        check("management native create and clear remain thread scoped",()=>{
            var f=new Fixture(sandbox);f.NativeGoal=null;var created=f.Goal(new{action="set",expectedVersion="",objective="New fictional outcome",status="paused"},"create");string version=Text(Field(created,"goal"),"version");var cleared=f.Goal(new{action="clear",expectedVersion=version},"clear");Assert(Field(cleared,"goal")==null&&f.NativeGoal==null&&f.Native.Sets==2,"Native goal lifecycle was not applied");
        });
        check("management stale goal version cannot overwrite native state",()=>{
            var f=new Fixture(sandbox);Reject(()=>f.Goal(new{action="set",expectedVersion=new string('b',64),status="active"}),409);Assert(f.Native.Sets==0,"Stale goal mutated native state");
        });
        check("management lost native goal response never replays mutation",()=>{
            var f=new Fixture(sandbox);string version=f.Version();f.Native.FailAfterSet=true;var body=new{action="set",expectedVersion=version,status="blocked"};Reject(()=>f.Goal(body));int calls=f.Native.Calls.Count;Reject(()=>f.Goal(body),409);Assert(f.Native.Sets==1&&f.Native.Calls.Count==calls,"Uncertain goal mutation replayed");Assert(Text(Field(f.Read(),"goal"),"status")=="blocked","Read-only reconciliation lost committed native goal");
        });
        check("management active desktop reports native goal limitation honestly",()=>{
            var f=new Fixture(sandbox);f.NoGoal();var view=f.Read();Assert(!Object.Equals(Field(Field(view,"capabilities"),"canWriteGoal"),true)&&Object.Equals(Field(Field(view,"capabilities"),"canManageSchedules"),true)&&f.Connects==0,"Unavailable goal opened a second engine or disabled real scheduler");Reject(()=>f.Goal(new{action="set",expectedVersion="",objective="Not executed"}),409);Assert(f.Connects==0,"Blocked desktop started native RPC");
        });
        check("management live owner bypasses stopped profile leases and keeps transport ephemeral",()=>{
            var f=new Fixture(sandbox);f.Target.GoalUsesDesktop=true;
            Assert(!Json.Write(f.Target).Contains("GoalUsesDesktop"),"Transient desktop mode entered durable schedule ownership");
            using(var entered=new ManualResetEventSlim(false))using(var release=new ManualResetEventSlim(false)){
                var held=Task.Run(()=>{using(new InstanceLease(f.Target.InstanceFolder))using(HeadlessProcessWire.AcquireProfileLease(f.Target.Home,CancellationToken.None)){entered.Set();release.Wait();}});
                Assert(entered.Wait(TimeSpan.FromSeconds(20)),"Fictional profile lease did not start");
                try{string version=f.Version();var view=f.Goal(new{action="set",expectedVersion=version,status="paused"});Assert(Text(Field(view,"goal"),"status")=="paused"&&f.Native.Sets==1,"Live native owner was not used under the desktop's leases");}
                finally{release.Set();held.GetAwaiter().GetResult();}
            }
            Assert(f.Admissions.Count==0&&f.Target.Access=="headless"&&f.Native.Calls.All(method=>new[]{"thread/read","thread/goal/get","thread/goal/set"}.Contains(method)),"Live headless-grant goal admitted work or used a turn");
        });
        check("management live goal loss remains uncertain without reconnect or replay",()=>{
            var f=new Fixture(sandbox);f.Target.GoalUsesDesktop=true;string version=f.Version();f.Native.FailAfterSet=true;var input=new{action="set",expectedVersion=version,status="paused"};
            Reject(()=>f.Goal(input));int calls=f.Native.Calls.Count,connects=f.Connects;Reject(()=>f.Goal(input),409);
            Assert(f.Native.Sets==1&&f.Native.Calls.Count==calls&&f.Connects==connects,"Live uncertain mutation reconnected or resent");
            Assert(Text(Field(f.Read(),"goal"),"status")=="paused","Read-only reconciliation missed native commit");
        });
        check("desktop goal bridge binds envelope and native CAS without changing native parameters",()=>{
            var f=new Fixture(sandbox);var pin=BridgePin(f);var packets=new List<object>();int checks=0;
            using(var bridge=new DesktopGoalBridge(pin,f.Target.ThreadId,()=>checks++,async(request,token)=>{
                packets.Add(Shape(request));var reply=await f.Native.Call(Text(request,"method"),Field(request,"parameters"),token);return Shape(new{ok=true,result=reply});
            })){
                bridge.Call("thread/read",new{threadId=f.Target.ThreadId,includeTurns=false},CancellationToken.None).GetAwaiter().GetResult();
                var goal=bridge.Call("thread/goal/get",new{threadId=f.Target.ThreadId},CancellationToken.None).GetAwaiter().GetResult();string version=PortalConversationManagement.NativeGoalVersion(goal,f.Target.ThreadId);
                bridge.Call("thread/goal/set",new{threadId=f.Target.ThreadId,origin="user",status="paused"},CancellationToken.None).GetAwaiter().GetResult();
                Assert(Text(packets.Last(),"expectedVersion")==version&&Text(packets.Last(),"accountKey")==pin.AccountKey&&Text(packets.Last(),"launchId")==pin.LaunchId&&Text(packets.Last(),"home")==pin.Home,"Bridge lost owner or CAS pins");
                Assert(Field(f.Native.LastSet,"expectedVersion")==null&&Text(f.Native.LastSet,"origin")=="user"&&checks==6,"Bridge invented native parameters or skipped owner revalidation");
            }
            Assert(PortalConversationManagement.NativeGoalVersion(Shape(new{goal=(object)null}),f.Target.ThreadId)=="","Empty goal does not match create CAS");
        });
        check("desktop goal bridge refuses turns foreign thread full history and mutation without read",()=>{
            var f=new Fixture(sandbox);int sent=0;
            using(var bridge=new DesktopGoalBridge(BridgePin(f),f.Target.ThreadId,()=>{},(request,token)=>{sent++;return Task.FromResult(Shape(new{ok=true,result=new{}}));})){
                Reject(()=>bridge.Call("turn/start",new{threadId=f.Target.ThreadId},CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>bridge.Call("thread/read",new{threadId=f.Target.ThreadId,includeTurns=true},CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>bridge.Call("thread/goal/get",new{threadId=Guid.NewGuid().ToString("D")},CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>bridge.Call("thread/goal/set",new{threadId=f.Target.ThreadId,origin="user",status="paused"},CancellationToken.None).GetAwaiter().GetResult());
                Assert(sent==0,"Rejected bridge operation reached a native owner");
            }
        });
        check("desktop goal bridge refuses reused process launch account home and not ready ownership",()=>{
            var f=new Fixture(sandbox);var pin=BridgePin(f);
            Action<DesktopGoalProxyState>[] changes={p=>p.ProxyStartedTicks++,p=>p.NativeStartedTicks++,p=>p.DesktopStartedTicks++,p=>p.HostStartedTicks++,p=>p.LaunchId=Guid.NewGuid().ToString("N"),p=>p.AccountKey="foreign",p=>p.Home=Path.GetDirectoryName(p.Home),p=>p.PipeName="creezio-desktop-goal-"+Guid.NewGuid().ToString("N"),p=>p.Ready=false,p=>p.Running=false};
            foreach(var change in changes){var copy=Json.Read<DesktopGoalProxyState>(Json.Write(pin));change(copy);Assert(!DesktopGoalBridge.SameOwner(pin,copy),"Changed native owner remained valid");}
            Assert(DesktopGoalBridge.SameOwner(pin,Json.Read<DesktopGoalProxyState>(Json.Write(pin))),"Identical native owner rejected");
        });
        check("desktop goal bridge owner change after get cannot reach set",()=>{
            var f=new Fixture(sandbox);var pin=BridgePin(f);var current=Json.Read<DesktopGoalProxyState>(Json.Write(pin));int sent=0;
            using(var bridge=new DesktopGoalBridge(pin,f.Target.ThreadId,()=>{if(!DesktopGoalBridge.SameOwner(pin,current))throw new InvalidOperationException("Fictional owner changed");},(request,token)=>{sent++;return Task.FromResult(Shape(new{ok=true,result=new{goal=f.NativeGoal}}));})){
                bridge.Call("thread/goal/get",new{threadId=f.Target.ThreadId},CancellationToken.None).GetAwaiter().GetResult();current.DesktopStartedTicks++;
                Reject(()=>bridge.Call("thread/goal/set",new{threadId=f.Target.ThreadId,origin="user",status="paused"},CancellationToken.None).GetAwaiter().GetResult());Assert(sent==1,"Owner drift reached mutation");
            }
        });
        check("desktop goal bridge proxy conflicts surface as stale version without native retry",()=>{
            var f=new Fixture(sandbox);int sent=0;
            using(var bridge=new DesktopGoalBridge(BridgePin(f),f.Target.ThreadId,()=>{},(request,token)=>{sent++;return Task.FromResult(Text(request,"method")=="thread/goal/get"?Shape(new{ok=true,result=new{goal=f.NativeGoal}}):Shape(new{ok=false,error="conflict"}));})){
                bridge.Call("thread/goal/get",new{threadId=f.Target.ThreadId},CancellationToken.None).GetAwaiter().GetResult();Reject(()=>bridge.Call("thread/goal/set",new{threadId=f.Target.ThreadId,origin="user",status="paused"},CancellationToken.None).GetAwaiter().GetResult(),409);Assert(sent==2&&f.Native.Sets==0,"Conflict retried or reached native mutation");
            }
        });
        check("desktop goal bridge cancelled requests never reach the owner",()=>{
            var f=new Fixture(sandbox);int sent=0;
            using(var stop=new CancellationTokenSource())using(var bridge=new DesktopGoalBridge(BridgePin(f),f.Target.ThreadId,()=>{},(request,token)=>{sent++;return Task.FromResult(Shape(new{ok=true,result=new{}}));})){
                stop.Cancel();try{bridge.Call("thread/goal/get",new{threadId=f.Target.ThreadId},stop.Token).GetAwaiter().GetResult();throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}Assert(sent==0,"Cancelled bridge request was sent");
            }
        });
        check("management rejects wrong native thread workspace and goal ownership",()=>{
            foreach(string mismatch in new[]{"thread","workspace","goal"}){var f=new Fixture(sandbox);f.Native.WrongThread=mismatch=="thread";f.Native.WrongWorkspace=mismatch=="workspace";f.Native.WrongGoal=mismatch=="goal";var view=f.Read();Assert(!Object.Equals(Field(Field(view,"capabilities"),"canWriteGoal"),true)&&f.Native.Sets==0,"Wrong native owner was accepted");}
        });
        check("management rejects goal input escalation and invalid budgets before native call",()=>{
            foreach(object request in new object[]{new{action="set",expectedVersion="",status="usageLimited"},new{action="set",expectedVersion="",tokenBudget=0},new{action="set",expectedVersion="",tokenBudget=1.5},new{action="set",expectedVersion="",tokenBudget=1000000001L},new{action="set",expectedVersion="",objective="ok",home="C:\\foreign"},new{action="clear",expectedVersion="",status="active"}}){var f=new Fixture(sandbox);Reject(()=>f.Goal(request),400);Assert(f.Connects==0,"Invalid input reached native engine");}
        });
        check("management revoked actor or changed pins blocks reads and mutations",()=>{
            var f=new Fixture(sandbox);f.Revoked=true;Reject(()=>f.Read(),403);Assert(f.Connects==0,"Revoked actor reached native engine");var changed=new Fixture(sandbox);changed.Changed=true;Reject(()=>changed.Goal(new{action="set",expectedVersion="",objective="not sent"}),403);Assert(changed.Native.Sets==0,"Changed account reached goal mutation");
        });
        check("management schedule CRUD is paused durable revisioned and idempotent",()=>{
            var f=new Fixture(sandbox);f.NoGoal();var body=f.Schedule();f.Save(body);var first=f.Only();f.Save(body);Assert(f.Only().Id==first.Id&&f.Only().Revision==1&&!f.Service.HasEnabled()&&f.Admissions.Count==0,"Paused create duplicated or started a job");
            var edit=new{id=first.Id,revision=first.Revision,name="Edited task",prompt="Edited fictional instruction",status="PAUSED",frequency="daily",localTime="12:30",timeZoneId="UTC"};f.Save(edit,"edit");Assert(f.Only().Revision==2&&f.Only().Name=="Edited task","Schedule edit failed");Reject(()=>f.Save(edit,"stale"),409);f.Delete(first.Id,2);Assert(f.Only().Status=="DELETED"&&!RelayEngine.Rows(Field(f.Read(),"schedules")).Any(),"Delete removed audit or kept visible task");
        });
        check("management schedule validation rejects invalid time days timezone and unknown fields",()=>{
            foreach(object request in new object[]{new{name="x",prompt="x",status="ACTIVE",frequency="interval",intervalMinutes=1,timeZoneId="UTC"},new{name="x",prompt="x",status="ACTIVE",frequency="daily",localTime="24:00",timeZoneId="UTC"},new{name="x",prompt="x",status="PAUSED",frequency="weekly",localTime="12:00",daysOfWeek=new[]{"MO","MO"},timeZoneId="UTC"},new{name="x",prompt="x",status="PAUSED",frequency="interval",intervalMinutes=5,timeZoneId="not/a/timezone"},new{name="x",prompt="x",status="PAUSED",frequency="interval",intervalMinutes=5,timeZoneId="UTC",instanceId="foreign"}}){var f=new Fixture(sandbox);f.NoGoal();Reject(()=>f.Save(request),400);Assert(Directory.GetFiles(f.Store.Root,"conversation-schedule-*.dpapi").Length==0&&f.Admissions.Count==0,"Invalid schedule persisted or executed");}
        });
        check("management interval occurrence coalesces missed ticks without duplicate admission",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule("ACTIVE"));DateTime due=DateTime.Parse(f.Only().NextRunUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);f.Pump();Assert(f.Admissions.Count==0,"Schedule ran early");f.Clock=due.AddHours(2);f.Pump();string id=f.Only().LastJobId;Assert(f.Admissions.Count==1&&id!=null&&DateTime.Parse(f.Only().NextRunUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)>f.Clock,"Missed ticks were replayed");f.Pump();Assert(f.Admissions.Count==1,"Same occurrence repeated");
        });
        check("management lost admission acknowledgement retries only same durable job id",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule("ACTIVE"));f.Clock=f.Clock.AddMinutes(6);f.FailAfterAdmission=true;f.Pump();var pending=f.Only().PendingJob;Assert(pending!=null&&f.Only().State=="waiting","Prepared occurrence was lost after ambiguous storage acknowledgement");f.Pump();Assert(f.Admissions.Count==2&&f.Admissions.Distinct().Count()==1&&f.Only().LastJobId==pending.Id&&f.Only().PendingJob==null,"Crash recovery created a second job identity");
        });
        check("management pausing ambiguous occurrence retains admitted job ownership",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule("ACTIVE"));f.Clock=f.Clock.AddMinutes(6);f.FailAfterAdmission=true;f.Pump();var row=f.Only();string id=row.PendingJob.Id;
            f.Save(new{id=row.Id,revision=row.Revision,name=row.Name,prompt=row.Prompt,status="PAUSED",frequency=row.Frequency,intervalMinutes=row.IntervalMinutes,timeZoneId=row.TimeZoneId},"pause");Assert(f.Only().Status=="PAUSED"&&f.Only().LastJobId==id,"Pause lost already admitted occurrence");f.Clock=f.Clock.AddDays(1);f.Pump();Assert(f.Admissions.Count==1,"Paused schedule replayed prepared occurrence");
        });
        check("management revoked schedule pauses before queue or native access",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule("ACTIVE"));f.Clock=f.Clock.AddMinutes(6);f.Revoked=true;f.Pump();Assert(f.Admissions.Count==0&&f.Only().Status=="PAUSED"&&f.Only().State=="blocked","Revoked actor still admitted occurrence");
        });
        check("management uncertain prior turn blocks all later occurrences",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule("ACTIVE"));f.Clock=f.Clock.AddMinutes(6);f.Pump();var row=f.Only();var job=f.Store.ReadRecord<WebhookJob>("webhook-job-"+row.LastJobId+".dpapi");job.State="uncertain";f.Store.WriteRecord("webhook-job-"+job.Id+".dpapi",job);f.Clock=f.Clock.AddDays(1);f.Pump();Assert(f.Only().Status=="PAUSED"&&f.Only().State=="attention"&&f.Admissions.Count==1,"Uncertain turn silently retried on a later tick");
        });
        check("management daily and weekly schedules preserve wall clock and skip passed dates",()=>{
            var f=new Fixture(sandbox);var daily=new PortalConversationSchedule{Frequency="daily",LocalTime="08:00",TimeZoneId="UTC",DaysOfWeek=new string[0]};Assert(f.Service.Next(daily,f.Clock)==f.Clock.AddDays(1),"Daily schedule repeated same local date");var weekly=new PortalConversationSchedule{Frequency="weekly",LocalTime="09:15",TimeZoneId="UTC",DaysOfWeek=new[]{"MO"}};Assert(f.Service.Next(weekly,f.Clock)==new DateTime(2026,10,19,9,15,0,DateTimeKind.Utc),"Weekly weekday or local hour changed");
        });
        check("management local wrappers need no portal identity and keep remote scope separate",()=>{
            var f=new Fixture(sandbox,true);f.NoGoal();f.Save(f.Schedule());Assert(f.Authorizations==0&&f.Only().Target.Local&&f.Only().Target.RouteId==null&&f.Only().Target.Actor==null,"Desktop fabricated external admin or portal grant");Assert(Text(Field(f.Read(),"capabilities"),"scheduleEngine")=="switcher"&&f.Authorizations==0,"Local read used remote administration");Reject(()=>f.Service.SaveSchedule(Guid.NewGuid().ToString("N"),new string('a',64),f.Target.InstanceId,f.Target.ChatId,"forged",Shape(f.Schedule())));Assert(Directory.GetFiles(f.Store.Root,"conversation-schedule-*.dpapi").Length==1,"Remote API promoted a local target");
        });
        check("management update freeze prevents schedule admission without losing occurrence",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule("ACTIVE"));f.Clock=f.Clock.AddMinutes(6);f.Store.WriteRecord("application-update-transaction-v1.dpapi",new ApplicationUpdateTransaction{Frozen=true});Reject(()=>f.Pump(),503);Assert(f.Admissions.Count==0&&f.Only().LastJobId==null,"Frozen update still dispatched scheduled work");
        });
        check("management worker records are never counted as schedule records",()=>{
            var f=new Fixture(sandbox);f.Store.WriteRecord(PortalScheduleWorker.StateRecord,new RelayWorkerState{Pid=0,Version="0.29.0-beta.1"});f.Store.WriteRecord(PortalScheduleWorker.LaunchRecord,new RelayWorkerState{Pid=0,Version="0.29.0-beta.1"});f.Store.WriteRecord(PortalScheduleWorker.StopRecord,new PortalScheduleStop());Assert(!f.Service.HasEnabled(),"Worker bookkeeping was deserialized as a schedule");f.NoGoal();Assert(!RelayEngine.Rows(Field(f.Read(),"schedules")).Any(),"Worker bookkeeping leaked into management catalog");
        });
        check("management scheduler migration pins exact owner and spawns only one waiter",()=>{
            var f=new Fixture(sandbox);var old=new RelayWorkerState{Pid=81,Started=1234,Version="0.29.0-beta.1"};f.Store.WriteRecord(PortalScheduleWorker.StateRecord,old);int spawned=0;
            Action spawn=()=>{spawned++;f.Store.WriteRecord(PortalScheduleWorker.LaunchRecord,new RelayWorkerState{Pid=82,Started=5678,Version="0.29.0-beta.2"});};
            Func<RelayWorkerState,bool> alive=s=>s.Pid==81||s.Pid==82;
            PortalScheduleWorker.EnsureCore(f.Store,"0.29.0-beta.2",()=>true,alive,spawn);var request=f.Store.ReadRecord<PortalScheduleStop>(PortalScheduleWorker.StopRecord);
            Assert(spawned==1&&PortalScheduleWorker.ShouldRetire(old,request),"Migration omitted exact owner retirement or replacement waiter");
            Assert(!PortalScheduleWorker.ShouldRetire(new RelayWorkerState{Pid=81,Started=9999,Version=old.Version},request),"Reused PID inherited stop authority");
            PortalScheduleWorker.EnsureCore(f.Store,"0.29.0-beta.2",()=>true,alive,spawn);Assert(spawned==1,"Migration started duplicate replacement workers");
        });
        check("management older scheduler callers cannot stop or resurrect over newer version",()=>{
            var f=new Fixture(sandbox);f.Store.WriteRecord(PortalScheduleWorker.StateRecord,new RelayWorkerState{Pid=90,Started=345,Version="0.30.0-beta.1"});int spawned=0;
            PortalScheduleWorker.EnsureCore(f.Store,"0.29.0-beta.1",()=>true,s=>s.Pid==90,()=>spawned++);
            Assert(spawned==0&&f.Store.ReadRecord<PortalScheduleStop>(PortalScheduleWorker.StopRecord).Pid==0,"Old caller tried to downgrade live scheduler");
            PortalScheduleWorker.EnsureCore(f.Store,"0.29.0-beta.1",()=>true,s=>false,()=>spawned++);Assert(spawned==0,"Old host resurrected an older scheduler after newer worker exited");
            Assert(!PortalScheduleWorker.ShouldRetire(new RelayWorkerState{Pid=90,Started=345,Version="0.30.0-beta.1"},new PortalScheduleStop{Pid=90,Started=345,Version="0.30.0-beta.1",RequestedVersion="0.29.0-beta.1"}),"Downward stop request was accepted");
        });
        check("management authorized regrant can remove old schedule without automatic rebind",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule());var previous=f.Only();f.Target.GrantRevision++;f.Target.RouteRevision++;f.Delete(previous.Id,previous.Revision,"delete-after-regrant");Assert(f.Only().Status=="DELETED"&&f.Only().Target.GrantRevision==previous.Target.GrantRevision,"Explicit delete required stale authority or silently rebound scheduler");
        });
        check("management mutation receipts do not duplicate schedule prompt catalogs",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(new{name="Receipt footprint",prompt=new string('x',20000),status="PAUSED",frequency="interval",intervalMinutes=5,timeZoneId="UTC"});string file=Directory.GetFiles(f.Store.Root,"conversation-mutation-*.dpapi").Single();var receipt=f.Store.ReadRecord<PortalConversationMutation>(Path.GetFileName(file));Assert(Json.Write(receipt.Result).Length<1000&&!Json.Write(receipt.Result).Contains(new string('x',100)),"Every mutation receipt copied the full prompt catalog");
        });
        check("management local scheduler waits for an admitted parent's children",()=>{
            var f=new Fixture(sandbox,true);f.NoGoal();f.Save(f.Schedule("ACTIVE"));f.Clock=f.Clock.AddMinutes(6);f.Pump();string id=f.Only().LastJobId;
            f.Store.Save(new RelayMessage{Id=id,SchemaVersion=4,OperatorOrigin=true,RequestedThreadId=f.Target.ThreadId,TargetThreadId=f.Target.ThreadId,TargetAccountKey=f.Target.AccountKey,TargetHome=f.Target.Home,TargetWorkspace=f.Target.Workspace,Workspace=f.Target.Workspace,State="children",CreatedUtc=f.Clock.ToString("o")});
            f.Clock=f.Clock.AddMinutes(6);f.Pump();Assert(f.Admissions.Count==1&&f.Only().LastJobId==id&&f.Only().PendingJob==null,"Parent waiting for children overlapped another scheduled turn");
        });
        check("management numeric schedule id cannot silently create a new schedule",()=>{
            var f=new Fixture(sandbox);f.NoGoal();Reject(()=>f.Save(new{id=123,name="invalid id",prompt="not run",status="PAUSED",frequency="interval",intervalMinutes=5,timeZoneId="UTC"}),400);Assert(Directory.GetFiles(f.Store.Root,"conversation-schedule-*.dpapi").Length==0,"Numeric edit id silently became a create");
        });
        check("management policy rotation cannot silently reauthorize prior schedules or goal receipts",()=>{
            var f=new Fixture(sandbox);f.NoGoal();f.Save(f.Schedule("ACTIVE"));f.Target.PolicyRevision++;f.Target.SiteOrigin="https://rotated.example.invalid";f.Clock=f.Clock.AddMinutes(6);f.Pump();Assert(f.Admissions.Count==0&&f.Only().Status=="PAUSED"&&f.Only().State=="blocked","Fresh actor cache silently rebound an old schedule to rotated policy");
            var g=new Fixture(sandbox);string version=g.Version();var request=new{action="set",expectedVersion=version,status="paused"};g.Goal(request);g.Target.PolicyRevision++;Reject(()=>g.Goal(request),409);Assert(g.Native.Sets==1,"Old native mutation receipt gained the new policy's authority");
        });
    }
}
