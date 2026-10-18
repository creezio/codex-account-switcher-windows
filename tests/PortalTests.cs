using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class PortalTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(int code,Action action){try{action();}catch(WebhookException e){Assert(e.StatusCode==code,"Expected "+code+", got "+e.StatusCode);return;}throw new Exception("Missing rejection "+code);}
    private static void Invalid(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected invalid operation");}
    private sealed class Native : IPortalNative,IPortalNativeAdmission
    {
        internal int Reads,Lists,Verifications;internal bool WrongAccount,Busy,WrongThread,SendUnavailable;
        internal string Thread,Title="Existing fictional conversation";
        public void Verify(PortalInstanceGrant grant){Verifications++;if(WrongAccount)throw new InvalidOperationException("Account changed");}
        public void VerifySend(PortalInstanceGrant grant){if(SendUnavailable)throw new WebhookException(409,"Headless sends unavailable while desktop is open");}
        public object Describe(PortalInstanceGrant grant){return new{id=grant.InstanceId,name="Fixture instance",account="fixture@example.test",online=true,desktopOpen=true,executionMode="desktop",issue=(string)null};}
        public object List(PortalInstanceGrant grant){Lists++;return Json.Read<object>(Json.Write(new{pinnedThreads=new object[0],threads=new[]{new{threadId=Thread,title=Title,source="codex",status="idle"}}}));}
        public object Read(PortalInstanceGrant grant,string thread){Reads++;return Json.Read<object>(Json.Write(new{thread=new{id=WrongThread?Guid.NewGuid().ToString("D"):thread,title=Title,status=new{type=Busy?"active":"idle"}},turns=new object[0]}));}
    }
    private sealed class Fixture
    {
        internal RelayStore Store;internal WebhookRoute Route;internal PortalConfig Config;internal PortalInstanceGrant Grant;internal string Key,Thread,Rollout;internal Native Native;
        internal Fixture(string root,string name)
        {
            string folder=Path.Combine(root,"portal-tests",name+"-"+Guid.NewGuid().ToString("N").Substring(0,8)),home=Path.Combine(folder,"home"),workspace=Path.Combine(folder,"workspace"),routeHome=Path.Combine(folder,"root-home");Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);Directory.CreateDirectory(routeHome);Store=new RelayStore(Path.Combine(folder,"relay"));
            Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true});Route=Webhooks.SaveRoute(Store,new WebhookRoute{Name="Private portal fixture",Slug="private-portal",InstanceId=new string('a',32),AccountKey="original-root-account",Home=routeHome,Workspace=workspace,Enabled=true});Key=Webhooks.RotateKey(Store,Route.Id);Route=Webhooks.Route(Store,Route.Id);
            Grant=new PortalInstanceGrant{InstanceId=new string('b',32),AccountKey="selected-fixture-account",Home=home,Workspace=workspace,Access="desktop",PermissionProfile="inherit",Model="gpt-6.1-sol",ReasoningEffort="ultra"};Config=Portal.SaveConfig(Store,new PortalConfig{RootRouteId=Route.Id,Enabled=true,IncludeExisting=true,Instances=new List<PortalInstanceGrant>{Grant}});
            Thread="01900000-0000-7000-8000-"+Guid.NewGuid().ToString("N").Substring(0,12);Native=new Native{Thread=Thread};
            long ms=Convert.ToInt64(Thread.Replace("-","").Substring(0,12),16);var day=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ms);string session=Path.Combine(home,"sessions",day.ToString("yyyy",CultureInfo.InvariantCulture),day.ToString("MM",CultureInfo.InvariantCulture),day.ToString("dd",CultureInfo.InvariantCulture));Directory.CreateDirectory(session);Rollout=Path.Combine(session,"rollout-fixture-"+Thread+".jsonl");
            Append(new{type="session_meta",payload=new{id=Thread,cwd=workspace}});Append(new{type="event_msg",payload=new{type="task_complete",turn_id="fictional-turn"}});
        }
        internal void Append(object row){File.AppendAllText(Rollout,Json.Write(row)+"\n",new UTF8Encoding(false));}
        internal WebhookJob New(string key="new",string prompt="Fictional prompt"){return Portal.Admit(Store,Route.Slug,Key,Grant.InstanceId,null,key,Json.Write(new{prompt=prompt}),Native);}
        internal WebhookJob Send(string key="message",string prompt="Follow up"){return Portal.Admit(Store,Route.Slug,Key,Grant.InstanceId,Thread,key,Json.Write(new{prompt=prompt}),Native);}
        internal object View(string id=null){return Json.Read<object>(Json.Write(Portal.Snapshot(Store,Route.Slug,Key,Grant.InstanceId,id??Thread,Native)));}
        internal WebhookJob Created()
        {var job=New();job.ThreadId=Thread;job.State="completed";Webhooks.SaveJob(Store,job);Store.Save(new RelayMessage{Id=job.Id,WebhookJobId=job.Id,OperatorOrigin=true,NewConversation=true,TargetThreadId=Thread,TargetAccountKey=Grant.AccountKey,TargetHome=Grant.Home,TargetWorkspace=Grant.Workspace,State="completed"});return job;}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("portal metadata reads do not admit a new message when execution mode is unavailable",()=>{
            var f=new Fixture(root,"metadata-not-send");f.Config.Instances[0].Access="headless";f.Config=Portal.SaveConfig(f.Store,f.Config);f.Grant=f.Config.Instances[0];f.Native.SendUnavailable=true;
            Portal.ListChats(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,null,f.Native);f.View();
            Assert(f.Native.Lists>0&&f.Native.Reads>0,"Read-only metadata fixture did not remain available");
            Reject(409,()=>f.New());Reject(409,()=>f.Send());
            Assert(Webhooks.Jobs(f.Store).Length==0&&f.Store.Messages().Count==0,"Metadata availability widened admission or started native work");
            f.Native.SendUnavailable=false;var accepted=f.New("stable");f.Native.SendUnavailable=true;
            Assert(f.New("stable").Id==accepted.Id&&Webhooks.Jobs(f.Store).Length==1,"Reading an existing idempotent receipt admitted a new job");
        });
        check("portal composer options and uploads stay immutable across admission and retries",()=>{
            var f=new Fixture(root,"composer-admission");
            var uploaded=PortalAttachments.Upload(f.Store,f.Route,f.Grant,"file",Json.Write(new{name="brief.txt",mimeType="text/plain",contentBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes("Fictional attachment only"))}));
            string attachment=Json.Str(Json.Get(Json.Get(Json.Read<object>(Json.Write(uploaded)),"attachment"),"id"));
            string body=Json.Write(new{prompt="Read the fictional attachment",options=new{model="gpt-6.1-sol",reasoningEffort="ultra",attachments=new[]{attachment}}});
            var job=Portal.Admit(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,null,"compose",body,f.Native);
            var retry=Portal.Admit(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,null,"compose",body,f.Native);
            Assert(job.Id==retry.Id&&retry.Options.Attachments.SequenceEqual(new[]{attachment}),"Retry lost the uploaded file or created another job");
            Assert(job.RouteSnapshot.Id==Webhooks.ExecutionRouteId(f.Route.Id,f.Grant.InstanceId,job.Options)&&job.RouteSnapshot.Id!=Portal.FrozenRouteId(f.Route.Id,f.Grant.InstanceId),"Old engines can silently ignore rich options");
            Reject(409,()=>Portal.Admit(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,null,"compose",body.Replace("gpt-6.1-sol","gpt-6-astra"),f.Native));
            var view=f.View(job.Id);string publicView=Json.Write(view);Assert(publicView.Contains("brief.txt")&&!publicView.Contains("blob.bin")&&!publicView.Contains(f.Store.Root.Replace("\\","\\\\")),"Snapshot omitted file metadata or leaked storage path");
            job.Options.Model="gpt-6-astra";Invalid(()=>Webhooks.SaveJob(f.Store,job));
            Assert(Webhooks.Job(f.Store,job.Id).Options.Model=="gpt-6.1-sol","Immutable options were overwritten");
        });
        check("portal explicit allowlist excludes future and unrelated profiles",()=>{
            var f=new Fixture(root,"allowlist");var view=Json.Read<object>(Json.Write(Portal.ListInstances(f.Store,f.Route.Slug,f.Key,f.Native)));Assert(RelayEngine.Rows(Json.Get(view,"instances")).Count()==1,"Allowlist expanded");Reject(404,()=>Portal.RequireGrant(f.Store,f.Route.Slug,f.Key,new string('c',32),f.Native));Assert(f.Native.Lists==0&&f.Native.Reads==0,"Cheap list started native work");
        });
        check("portal missing configuration never expands root route credentials",()=>{
            var f=new Fixture(root,"disabled");f.Config.Enabled=false;Portal.SaveConfig(f.Store,f.Config);Reject(403,()=>f.New());Assert(Webhooks.Jobs(f.Store).Length==0,"Disabled portal admitted work");
        });
        check("portal bad secret and disabled root reject all entry points",()=>{
            var f=new Fixture(root,"auth");Reject(401,()=>Portal.ListInstances(f.Store,f.Route.Slug,"bad",f.Native));f.Route.Enabled=false;Webhooks.SaveRoute(f.Store,f.Route);Reject(503,()=>f.View());
        });
        check("portal instance reassociation fails before native listing or transcript",()=>{
            var f=new Fixture(root,"stale-account");f.Native.WrongAccount=true;Invalid(()=>f.New());Invalid(()=>f.View());Assert(f.Native.Reads==0&&Webhooks.Jobs(f.Store).Length==0,"Stale account reached data or queue");
        });
        check("portal native list bounded projection never imports chats or changes native state",()=>{
            var f=new Fixture(root,"list-read-only");string before=File.ReadAllText(f.Rollout);var list=Json.Read<object>(Json.Write(Portal.ListChats(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,null,f.Native)));var chat=RelayEngine.Rows(Json.Get(list,"chats")).Single();Assert(Json.Str(Json.Get(chat,"id"))==f.Thread&&f.Native.Lists==1&&f.Native.Reads==0&&Webhooks.Jobs(f.Store).Length==0&&File.ReadAllText(f.Rollout)==before,"List mutated chat or copied history");Reject(400,()=>Portal.ListChats(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,"fake-cursor",f.Native));
        });
        check("portal new chat durable handle pins selected account not root account",()=>{
            var f=new Fixture(root,"new-pin");var job=f.New();Assert(job.RouteSnapshot.AccountKey==f.Grant.AccountKey&&job.RouteSnapshot.AccountKey!=f.Route.AccountKey&&job.RouteSnapshot.InstanceId==f.Grant.InstanceId&&job.PortalInstanceId==f.Grant.InstanceId&&job.RouteSnapshot.ThreadId==null&&job.ConversationId==null,"Selected instance overwritten by root route");var view=f.View(job.Id);Assert(Json.Str(Json.Get(view,"id"))==job.Id&&Json.Str(Json.Get(view,"state"))=="queued"&&Json.Write(view).Contains("Fictional prompt"),"Durable handle unavailable before native ID");
        });
        check("portal existing chat first read verifies native identity and own session metadata",()=>{
            var f=new Fixture(root,"existing-read");f.Append(new{type="event_msg",payload=new{type="agent_message",message="Visible native message",phase="final_answer"}});string before=File.ReadAllText(f.Rollout);var view=f.View();Assert(Json.Str(Json.Get(view,"id"))==f.Thread&&Json.Write(view).Contains("Visible native message")&&f.Native.Reads==1&&File.ReadAllText(f.Rollout)==before&&Webhooks.Jobs(f.Store).Length==0,"Read was not verified/read-only");f.View();Assert(f.Native.Reads==1,"Live follow-up reopened native reader each snapshot");
        });
        check("portal wrong native receipt or foreign session metadata is never exposed",()=>{
            var f=new Fixture(root,"wrong-receipt");f.Native.WrongThread=true;Invalid(()=>f.View());var g=new Fixture(root,"wrong-meta");File.WriteAllText(g.Rollout,Json.Write(new{type="session_meta",payload=new{id=Guid.NewGuid().ToString("D"),cwd=g.Grant.Workspace}})+"\n");Reject(409,()=>g.View());
        });
        check("portal existing history cannot be read when existing access disabled",()=>{
            var f=new Fixture(root,"no-existing");f.Config.IncludeExisting=false;Portal.SaveConfig(f.Store,f.Config);Reject(404,()=>f.View());Reject(404,()=>f.Send());Assert(f.Native.Reads==0,"Disabled existing feature reached native history");Assert(f.New().State=="queued","New chats unexpectedly disabled");
        });
        check("portal new and existing idempotence scopes include instance and chat",()=>{
            var f=new Fixture(root,"idempotence");var first=f.New("same");var again=f.New("same");Assert(first.Id==again.Id,"New retry duplicated");var existing=f.Send("same");Assert(existing.Id!=first.Id&&f.Send("same").Id==existing.Id,"Chat idempotence collided");Reject(409,()=>f.Send("same","Changed"));Assert(Webhooks.Jobs(f.Store).Length==2,"Idempotency conflict queued work");
        });
        check("portal concurrent sends admit one turn and preserve native idle history",()=>{
            var f=new Fixture(root,"concurrency");int accepted=0,blocked=0;Parallel.For(0,5,i=>{try{f.Send("send-"+i);Interlocked.Increment(ref accepted);}catch(WebhookException e){if(e.StatusCode!=409)throw;Interlocked.Increment(ref blocked);}});Assert(accepted==1&&blocked==4&&Webhooks.Jobs(f.Store).Length==1,"Concurrent turn interleaving admitted");
        });
        check("portal native active or approval state blocks before job creation",()=>{
            var f=new Fixture(root,"native-busy");f.Native.Busy=true;Reject(409,()=>f.Send());Assert(Webhooks.Jobs(f.Store).Length==0,"Busy native chat admitted a turn");var g=new Fixture(root,"rollout-busy");g.Append(new{type="event_msg",payload=new{type="task_started",turn_id="active"}});Reject(409,()=>g.Send());
        });
        check("portal existing messages target original GUID and overlay queued prompt",()=>{
            var f=new Fixture(root,"existing-send");var job=f.Send();Assert(job.RouteSnapshot.ThreadId==f.Thread&&job.PortalChatId==f.Thread&&job.ConversationId==f.Thread&&job.RouteSnapshot.Workspace==f.Grant.Workspace,"Existing thread was copied or rebound");var view=f.View();Assert(Json.Write(view).Contains("job-"+job.Id)&&Json.Write(view).Contains("Follow up")&&Json.Str(Json.Get(view,"state"))=="queued","Accepted user message missing from live transcript");
        });
        check("portal allowlist revision revokes queued native dispatch",()=>{
            var f=new Fixture(root,"revoke-job");var job=f.Send();Portal.ValidateJob(f.Store,job);f.Config.Instances.Clear();Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));Assert(f.Store.ActiveMessages().Count==0,"Validation dispatched work");
        });
        check("portal unrelated grant additions and removals preserve accepted messages",()=>{
            var f=new Fixture(root,"scoped-grant");var job=f.New();long epoch=job.PortalRevision;var stale=Json.Read<PortalConfig>(Json.Write(f.Config));
            var other=Json.Read<PortalInstanceGrant>(Json.Write(f.Config.Instances.Single()));other.InstanceId=new string('c',32);other.AccountKey="other-account";f.Config.Instances.Add(other);f.Config=Portal.SaveConfig(f.Store,f.Config);
            Assert(f.Config.Revision>epoch&&f.Config.Instances[0].AuthorizationRevision==epoch,"Unrelated addition rotated selected grant");Portal.ValidateJob(f.Store,job);
            var next=f.New("after-add");Assert(next.PortalRevision==epoch,"New admission stamped global revision instead of selected grant");
            f.Config.Instances.RemoveAll(grant=>grant.InstanceId==other.InstanceId);f.Config=Portal.SaveConfig(f.Store,f.Config);Portal.ValidateJob(f.Store,job);Portal.ValidateJob(f.Store,next);
            Invalid(()=>Portal.SaveConfig(f.Store,stale));Assert(f.Config.Instances.Single().AuthorizationRevision==epoch,"Unrelated removal rotated selected grant");
        });
        check("portal own grant changes and reversions invalidate previously accepted messages",()=>{
            foreach(string field in new[]{"permission","workspace","home","account","access","model","reasoning"}){
                var f=new Fixture(root,"own-"+field);var job=f.New();var original=Json.Read<PortalInstanceGrant>(Json.Write(f.Config.Instances.Single()));var changed=f.Config.Instances.Single();
                if(field=="permission")changed.PermissionProfile="full-access";
                if(field=="workspace"){changed.Workspace=Path.Combine(Path.GetDirectoryName(changed.Workspace),"other-workspace");Directory.CreateDirectory(changed.Workspace);}
                if(field=="home"){changed.Home=Path.Combine(Path.GetDirectoryName(changed.Home),"other-home");Directory.CreateDirectory(changed.Home);}
                if(field=="account")changed.AccountKey="changed-account";
                if(field=="access")changed.Access="headless";
                if(field=="model")changed.Model="gpt-6-astra";
                if(field=="reasoning")changed.ReasoningEffort="high";
                f.Config=Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));
                f.Config.Instances[0]=original;f.Config=Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));
                Assert(f.Config.Instances[0].AuthorizationRevision>job.PortalRevision,"Restoring "+field+" revived revoked work");
            }
        });
        check("portal revoke and regrant cannot restore an old authorization epoch",()=>{
            var f=new Fixture(root,"regrant");var job=f.New();var original=Json.Read<PortalInstanceGrant>(Json.Write(f.Config.Instances.Single()));f.Config.Instances.Clear();f.Config=Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));
            original.AuthorizationRevision=job.PortalRevision;f.Config.Instances.Add(original);f.Config=Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));Assert(f.Config.Instances.Single().AuthorizationRevision>job.PortalRevision,"Client supplied epoch restored revoked work");
        });
        check("portal global feature changes revoke existing grant epochs even after restoration",()=>{
            foreach(string field in new[]{"enabled","existing"}){
                var f=new Fixture(root,"global-"+field);var job=f.New();if(field=="enabled")f.Config.Enabled=false;else f.Config.IncludeExisting=false;
                f.Config=Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));if(field=="enabled")f.Config.Enabled=true;else f.Config.IncludeExisting=true;
                f.Config=Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));Assert(f.Config.Instances[0].AuthorizationRevision>job.PortalRevision,"Global restoration revived old work");
            }
        });
        check("portal legacy grants migrate their exact global epoch without trusting caller revisions",()=>{
            var f=new Fixture(root,"legacy-grant");f.Config.Revision=7;f.Config.Instances.Single().AuthorizationRevision=0;f.Store.WriteRecord("portal-"+f.Route.Id+"-v1.dpapi",f.Config);
            var job=f.New();Assert(job.PortalRevision==7,"Legacy admission no longer uses its known global epoch");Portal.ValidateJob(f.Store,job);
            var other=Json.Read<PortalInstanceGrant>(Json.Write(f.Config.Instances.Single()));other.InstanceId=new string('c',32);other.AuthorizationRevision=1;f.Config.Instances[0].AuthorizationRevision=999;f.Config.Instances.Add(other);f.Config=Portal.SaveConfig(f.Store,f.Config);
            Assert(f.Config.Instances[0].AuthorizationRevision==7&&f.Config.Instances[1].AuthorizationRevision==8,"Migration trusted caller revisions or rotated unchanged grant");Portal.ValidateJob(f.Store,job);
            f.Config.Instances[0].AuthorizationRevision=Int64.MaxValue;f.Config=Portal.SaveConfig(f.Store,f.Config);Portal.ValidateJob(f.Store,job);Assert(f.Config.Instances[0].AuthorizationRevision==7,"No-op client revision changed server epoch");
        });
        check("portal dispatch guard preserves selected grant and cancels after root key rotation",()=>{
            var f=new Fixture(root,"dispatch-guard");var job=f.New();var message=new RelayMessage{Id=job.Id,OperatorOrigin=true,WebhookJobId=job.Id,TargetAccountKey=f.Grant.AccountKey,TargetHome=f.Grant.Home,TargetWorkspace=f.Grant.Workspace,RequestedModel=f.Grant.Model,RequestedReasoningEffort=f.Grant.ReasoningEffort,RequestedPermission=f.Grant.PermissionProfile};WebhookDispatch.ValidateBeforeDispatch(f.Store,message);Webhooks.RotateKey(f.Store,f.Route.Id);Invalid(()=>WebhookDispatch.ValidateBeforeDispatch(f.Store,message));Assert(message.State=="cancelled","Revoked root did not cancel native send");
        });
        check("portal frozen route fails closed in old relay executables",()=>{
            var f=new Fixture(root,"legacy-engine");var job=f.New();Assert(job.RouteId==f.Route.Id&&job.RouteSnapshot.Id!=f.Route.Id&&job.RouteSnapshot.Id==Portal.FrozenRouteId(f.Route.Id,f.Grant.InstanceId),"Legacy root identity would bypass new portal guards");Portal.ValidateJob(f.Store,job);
        });
        check("portal immutable accepted identity prevents retargeting after admission",()=>{
            var f=new Fixture(root,"immutable");var job=f.New();job.PortalInstanceId=new string('c',32);Invalid(()=>Webhooks.SaveJob(f.Store,job));Assert(Webhooks.Job(f.Store,job.Id).PortalInstanceId==f.Grant.InstanceId,"Accepted target changed");
        });
        check("portal legacy transcript and job APIs cannot bypass revoked instance grants",()=>{
            var f=new Fixture(root,"legacy-revocation");var job=f.New();Reject(404,()=>WebhookChats.Snapshot(f.Store,f.Route.Slug,f.Key,job.Id));Portal.ValidateJobRead(f.Store,job,f.Native);f.Config.Instances.Clear();Portal.SaveConfig(f.Store,f.Config);Reject(404,()=>Portal.ValidateJobRead(f.Store,job,f.Native));
        });
        check("portal rejects browser routing overrides and arbitrary paths",()=>{
            var f=new Fixture(root,"no-overrides");foreach(string field in new[]{"home","workspace","threadId","instanceId","permissionProfile","model"})Reject(400,()=>Portal.Admit(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,null,"override-"+field,Json.Write(new Dictionary<string,string>{{"prompt","Fiction"},{field,"attack"}}),f.Native));Reject(404,()=>f.View("../../auth.json"));Assert(Webhooks.Jobs(f.Store).Length==0,"Routing override admitted");
        });
        check("portal public transcript omits hidden chain and includes bounded tool summary",()=>{
            var f=new Fixture(root,"public-only");f.Append(new{type="response_item",payload=new{type="message",role="assistant",channel="analysis",content=new[]{new{type="output_text",text="PRIVATE_CHAIN"}}}});f.Append(new{type="response_item",payload=new{type="reasoning",raw_content="PRIVATE_RAW",encrypted_content="PRIVATE_ENCRYPTED",summary=new[]{new{type="summary_text",text="Public brief summary"}}}});f.Append(new{type="response_item",payload=new{type="function_call",name="test_tool",call_id="call1",arguments="SECRET_ARGS"}});string view=Json.Write(f.View());Assert(view.Contains("Public brief summary")&&view.Contains("test_tool")&&!view.Contains("PRIVATE_CHAIN")&&!view.Contains("PRIVATE_RAW")&&!view.Contains("PRIVATE_ENCRYPTED")&&!view.Contains("SECRET_ARGS"),"Private reasoning or arguments leaked");
        });
        check("portal already registered transcript never crosses a replaced profile grant",()=>{
            var f=new Fixture(root,"registration-pin");f.View();f.Config.Instances[0].AccountKey="different-account";Portal.SaveConfig(f.Store,f.Config);Reject(409,()=>f.View());
        });
        check("portal new chat followup requires actual native creation receipt",()=>{
            var f=new Fixture(root,"creation-receipt");var job=f.New();job.ThreadId=f.Thread;job.State="completed";Webhooks.SaveJob(f.Store,job);Reject(409,()=>f.View(job.Id));f.Store.Save(new RelayMessage{Id=job.Id,WebhookJobId=job.Id,OperatorOrigin=true,NewConversation=true,TargetThreadId=f.Thread,TargetAccountKey=f.Grant.AccountKey,TargetHome=f.Grant.Home,TargetWorkspace=f.Grant.Workspace,State="completed"});var next=Portal.Admit(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,job.Id,"followup","{\"prompt\":\"Second turn\"}",f.Native);Assert(next.RouteSnapshot.ThreadId==f.Thread&&next.ConversationId==job.Id,"New chat did not retain its real native thread");
        });
        check("portal native GUID transcript includes root and queued durable-handle followup prompts",()=>{
            var f=new Fixture(root,"native-guid-alias");var created=f.Created();var next=Portal.Admit(f.Store,f.Route.Slug,f.Key,f.Grant.InstanceId,created.Id,"durable-followup","{\"prompt\":\"Queued durable followup\"}",f.Native);Assert(next.ThreadId==null,"Fixture should exercise pre-dispatch metadata");var view=f.View();string json=Json.Write(view);Assert(Json.Str(Json.Get(view,"id"))==f.Thread&&json.Contains("job-"+created.Id)&&json.Contains("Fictional prompt")&&json.Contains("job-"+next.Id)&&json.Contains("Queued durable followup")&&Json.Str(Json.Get(view,"state"))=="queued","Native GUID lost durable root or queued user text");
        });
        check("portal durable transcript includes queued followup submitted using native GUID",()=>{
            var f=new Fixture(root,"durable-guid-alias");var created=f.Created();var next=f.Send("guid-followup","Queued native GUID followup");Assert(next.ThreadId==null,"Fixture should exercise pre-dispatch metadata");var view=f.View(created.Id);string json=Json.Write(view);Assert(Json.Str(Json.Get(view,"id"))==created.Id&&json.Contains("job-"+created.Id)&&json.Contains("job-"+next.Id)&&json.Contains("Queued native GUID followup")&&Json.Str(Json.Get(view,"state"))=="queued","Durable handle lost native GUID followup");
        });
        check("portal alias reconciliation never exposes another instance with the same GUID",()=>{
            var f=new Fixture(root,"alias-account-isolation");var created=f.Created();string otherHome=Path.Combine(Path.GetDirectoryName(f.Grant.Home),"other-home");Directory.CreateDirectory(otherHome);var other=Json.Read<PortalInstanceGrant>(Json.Write(f.Grant));other.InstanceId=new string('c',32);other.AccountKey="another-permanent-account";other.Home=otherHome;f.Config.Instances.Add(other);f.Config=Portal.SaveConfig(f.Store,f.Config);
            string otherRollout=f.Rollout.Replace(f.Grant.Home,otherHome);Directory.CreateDirectory(Path.GetDirectoryName(otherRollout));File.WriteAllText(otherRollout,Json.Write(new{type="session_meta",payload=new{id=f.Thread,cwd=other.Workspace}})+"\n"+Json.Write(new{type="event_msg",payload=new{type="task_complete",turn_id="other-fictional"}})+"\n",new UTF8Encoding(false));
            var hidden=Portal.Admit(f.Store,f.Route.Slug,f.Key,other.InstanceId,null,"other-root","{\"prompt\":\"OTHER_ACCOUNT_ROOT_SECRET\"}",f.Native);hidden.ThreadId=f.Thread;hidden.State="completed";Webhooks.SaveJob(f.Store,hidden);var hiddenFollowup=Portal.Admit(f.Store,f.Route.Slug,f.Key,other.InstanceId,f.Thread,"other-guid-followup","{\"prompt\":\"OTHER_ACCOUNT_FOLLOWUP_SECRET\"}",f.Native);
            foreach(string handle in new[]{created.Id,f.Thread}){var view=f.View(handle);string json=Json.Write(view);Assert(json.Contains("job-"+created.Id)&&!json.Contains("OTHER_ACCOUNT_ROOT_SECRET")&&!json.Contains("OTHER_ACCOUNT_FOLLOWUP_SECRET")&&!json.Contains(hidden.Id)&&!json.Contains(hiddenFollowup.Id)&&Json.Str(Json.Get(view,"state"))=="completed","Alias crossed account/profile grant");}
        });
    }
}
