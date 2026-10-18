using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class WebhookChatTests
{
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static void Reject(int status,Action action){try{action();}catch(WebhookException ex){Assert(ex.StatusCode==status,"Unexpected status "+ex.StatusCode);return;}throw new Exception("Expected HTTP rejection "+status);}
    private sealed class Fixture
    {
        internal readonly RelayStore Store;internal WebhookRoute Route;internal string Key;internal WebhookJob Root;internal string Rollout;
        internal Fixture(string root,string name,bool ready=true)
        {
            string folder=Path.Combine(root,"webhook-chat-tests",name),home=Path.Combine(folder,"home"),workspace=Path.Combine(folder,"workspace");Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);Store=new RelayStore(Path.Combine(folder,"relay"));
            Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true,Port=47831});Route=Webhooks.SaveRoute(Store,new WebhookRoute{Name="Chat fixture",Slug="chat-fixture",AccountKey="fictional-account",InstanceId=new string('b',32),Home=home,Workspace=workspace,Enabled=true});Key=Webhooks.RotateKey(Store,Route.Id);Route=Webhooks.Route(Store,Route.Id);
            Root=Webhooks.Submit(Store,Route.Slug,Key,"root","{\"prompt\":\"Fictional first question\",\"title\":\"Fictional chat\"}");
            if(ready){
                Root.ThreadId="01900000-0000-7000-8000-"+Guid.NewGuid().ToString("N").Substring(0,12);Root.State="completed";Root.RelayJobId=Root.Id;Webhooks.SaveJob(Store,Root);
                Store.Save(new RelayMessage{Id=Root.Id,WebhookJobId=Root.Id,OperatorOrigin=true,NewConversation=true,TargetThreadId=Root.ThreadId,TargetAccountKey=Route.AccountKey,TargetHome=Route.Home,TargetWorkspace=Route.Workspace,State="completed",CreatedUtc=Root.CreatedUtc});
                long ms=Convert.ToInt64(Root.ThreadId.Replace("-","").Substring(0,12),16);var day=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ms);string directory=Path.Combine(home,"sessions",day.ToString("yyyy",CultureInfo.InvariantCulture),day.ToString("MM",CultureInfo.InvariantCulture),day.ToString("dd",CultureInfo.InvariantCulture));Directory.CreateDirectory(directory);Rollout=Path.Combine(directory,"rollout-fixture-"+Root.ThreadId+".jsonl");
                Append(new{type="session_meta",payload=new{id=Root.ThreadId}});Append(new{type="event_msg",payload=new{type="task_complete",turn_id="fixture-turn"}});
            }
        }
        internal void Append(object row){File.AppendAllText(Rollout,Json.Write(row)+"\n",new UTF8Encoding(false));}
        internal object Snapshot(){return Json.Read<object>(Json.Write(WebhookChats.Snapshot(Store,Route.Slug,Key,Root.Id)));}
        internal WebhookJob Send(string key="followup",string text="Next fictional question"){return WebhookChats.Send(Store,Route.Slug,Key,Root.Id,key,Json.Write(new{prompt=text}));}
        internal string View(){return Json.Write(Snapshot());}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("webhook chat renders native public plans and text agent items without private reasoning",()=>{
            var f=new Fixture(root,"public-native-plan");
            f.Append(new{type="response_item",payload=new{type="plan",id="public-plan",text="Plan public fictif : verifier le brief."}});
            f.Append(new{type="response_item",payload=new{type="agentMessage",id="native-text",text="Resultat public du composeur.",phase="final_answer"}});
            f.Append(new{type="response_item",payload=new{type="agentMessage",id="private-text",text="PRIVATE_REASONING_MUST_STAY_HIDDEN",phase="analysis"}});
            string view=f.View();Assert(view.Contains("Plan public fictif")&&view.Contains("Resultat public du composeur.")&&!view.Contains("PRIVATE_REASONING_MUST_STAY_HIDDEN"),"Native Plan results lost or private reasoning exposed");
        });
        check("webhook chat lists only owned newly-created conversations",()=>{
            var f=new Fixture(root,"owned-list");var existing=Json.Read<WebhookRoute>(Json.Write(f.Route));existing.ThreadId=Guid.NewGuid().ToString("D");f.Route=Webhooks.SaveRoute(f.Store,existing);Webhooks.Submit(f.Store,f.Route.Slug,f.Key,"existing-chat","{\"prompt\":\"Fiction\"}");
            var list=Json.Read<object>(Json.Write(WebhookChats.List(f.Store,f.Route.Slug,f.Key)));Assert(RelayEngine.Rows(Json.Get(list,"chats")).Count()==1,"Targeted legacy chat was exposed");
        });
        check("webhook chat root scope rejects arbitrary ids and followup ids",()=>{
            var f=new Fixture(root,"root-scope");Reject(404,()=>WebhookChats.Snapshot(f.Store,f.Route.Slug,f.Key,Guid.NewGuid().ToString("N")));var sent=f.Send();Reject(404,()=>WebhookChats.Snapshot(f.Store,f.Route.Slug,f.Key,sent.Id));
        });
        check("webhook chat route cannot inspect another route root",()=>{
            var f=new Fixture(root,"route-scope");var other=Json.Read<WebhookRoute>(Json.Write(f.Route));other.Id=null;other.Revision=0;other.Slug="other-chat";other=Webhooks.SaveRoute(f.Store,other);string key=Webhooks.RotateKey(f.Store,other.Id);Reject(404,()=>WebhookChats.Snapshot(f.Store,other.Slug,key,f.Root.Id));
        });
        check("webhook chat account or home rebinding revokes transcript access",()=>{
            var f=new Fixture(root,"binding-scope");f.Route.AccountKey="other-account";f.Route=Webhooks.SaveRoute(f.Store,f.Route);Reject(404,()=>f.Snapshot());Reject(404,()=>f.Send());
        });
        check("webhook chat requires the matching native creation receipt",()=>{
            var f=new Fixture(root,"native-receipt");var message=f.Store.Message(f.Root.Id);message.TargetThreadId=Guid.NewGuid().ToString("D");f.Store.Save(message);Reject(409,()=>f.Snapshot());Reject(409,()=>f.Send());
        });
        check("webhook chat queued prompt is explicit and has no invented final",()=>{
            var f=new Fixture(root,"not-created",false);var snapshot=f.Snapshot();var messages=RelayEngine.Rows(Json.Get(snapshot,"messages")).ToArray();Assert(messages.Length==1&&Json.Str(Json.Get(messages[0],"phase"))=="queued"&&Json.Str(Json.Get(messages[0],"role"))=="user","Queued request was presented as delivered");Reject(409,()=>f.Send());
        });
        check("webhook chat followup pins root thread and scopes idempotency",()=>{
            var f=new Fixture(root,"followup-pin");var sent=f.Send();Assert(sent.ConversationId==f.Root.Id&&sent.RouteSnapshot.ThreadId==f.Root.ThreadId&&sent.RouteSnapshot.AccountKey==f.Route.AccountKey,"Followup lost fixed native destination");Assert(f.Send().Id==sent.Id,"Followup retry duplicated admission");Reject(409,()=>f.Send("followup","Changed text"));
        });
        check("webhook chat concurrent followups never interleave two turns",()=>{
            var f=new Fixture(root,"concurrent");int accepted=0,rejected=0;Parallel.For(0,6,i=>{try{f.Send("request-"+i);System.Threading.Interlocked.Increment(ref accepted);}catch(WebhookException e){if(e.StatusCode!=409)throw;System.Threading.Interlocked.Increment(ref rejected);}});Assert(accepted==1&&rejected==5&&Webhooks.Jobs(f.Store).Length==2,"Concurrent followups bypassed turn serialization");
        });
        check("webhook chat rejects local routing overrides in followup payload",()=>{
            var f=new Fixture(root,"no-overrides");foreach(string field in new[]{"threadId","accountKey","home","permissionProfile","model"})Reject(400,()=>WebhookChats.Send(f.Store,f.Route.Slug,f.Key,f.Root.Id,"override-"+field,Json.Write(new Dictionary<string,string>{{"prompt","Fiction"},{field,"attacker"}})));Assert(Webhooks.Jobs(f.Store).Length==1,"Override queued native work");
        });
        check("webhook chat native active turn blocks admission even with stale receipt",()=>{
            var f=new Fixture(root,"native-busy");f.Append(new{type="event_msg",payload=new{type="task_started",turn_id="active-turn"}});Reject(409,()=>f.Send());Assert(Json.Str(Json.Get(f.Snapshot(),"state"))=="running","Native activity was hidden by completed receipt");f.Append(new{type="event_msg",payload=new{type="task_complete",turn_id="active-turn"}});Assert(f.Send().State=="queued","Completed native turn remained locked");
        });
        check("webhook chat unknown native activity never means idle",()=>{
            var f=new Fixture(root,"unknown-activity");File.WriteAllText(f.Rollout,Json.Write(new{type="session_meta",payload=new{id=f.Root.ThreadId}})+"\n",new UTF8Encoding(false));Reject(409,()=>f.Send());Assert(Json.Str(Json.Get(f.Snapshot(),"state"))=="checking","Unknown native state was presented as idle");
        });
        check("webhook chat bounded prompt preview reports clipping explicitly",()=>{
            var f=new Fixture(root,"prompt-preview",false);f.Root=Webhooks.Submit(f.Store,f.Route.Slug,f.Key,"long-root",Json.Write(new{prompt=new string('a',9000)}));var snapshot=f.Snapshot();Assert(Object.Equals(Json.Get(snapshot,"truncated"),true)&&Json.Str(Json.Get(RelayEngine.Rows(Json.Get(snapshot,"messages")).Single(),"text")).Length<=4001,"Long prompt was silently truncated");
        });
        check("webhook chat does not parse or display incomplete JSON lines",()=>{
            var f=new Fixture(root,"partial-line");string line=Json.Write(new{type="response_item",payload=new{type="message",role="assistant",content=new[]{new{type="output_text",text="Only after newline"}},phase="final_answer"}});File.AppendAllText(f.Rollout,line.Substring(0,line.Length/2),new UTF8Encoding(false));Assert(!f.View().Contains("Only after newline"),"Partial record became phantom answer");Reject(409,()=>f.Send());File.AppendAllText(f.Rollout,line.Substring(line.Length/2)+"\n",new UTF8Encoding(false));Assert(f.View().Contains("Only after newline"),"Completed append was lost");
        });
        check("webhook chat ignores malformed and oversized lines then recovers",()=>{
            var f=new Fixture(root,"malformed-lines");File.AppendAllText(f.Rollout,"{broken}\n"+new string('x',140000)+"\n",new UTF8Encoding(false));f.Append(new{type="event_msg",payload=new{type="agent_message",message="Recovered visible message",phase="final_answer"}});string view=f.View();Assert(view.Contains("Recovered visible message")&&!view.Contains(new string('x',50)),"Bad record broke bounded parser");
        });
        check("webhook chat excludes hidden analysis and encrypted reasoning",()=>{
            var f=new Fixture(root,"reasoning-safety");f.Append(new{type="response_item",payload=new{type="reasoning",encrypted_content="ENCRYPTED_PRIVATE",raw_content="PRIVATE_RAW",summary=new[]{new{type="summary_text",text="Published brief summary"}}}});f.Append(new{type="response_item",payload=new{type="message",role="assistant",channel="analysis",content=new[]{new{type="output_text",text="HIDDEN_ANALYSIS"}}}});f.Append(new{type="event_msg",payload=new{type="agent_reasoning",text="HIDDEN_EVENT"}});string view=f.View();Assert(view.Contains("Published brief summary")&&!view.Contains("ENCRYPTED_PRIVATE")&&!view.Contains("PRIVATE_RAW")&&!view.Contains("HIDDEN_ANALYSIS")&&!view.Contains("HIDDEN_EVENT"),"Private reasoning leaked");
        });
        check("webhook chat redacts credential patterns in messages and tools",()=>{
            var f=new Fixture(root,"redaction");string secret="whk_"+new string('A',43),api="sk-proj-"+new string('z',30);f.Append(new{type="event_msg",payload=new{type="agent_message",message="Bearer "+secret+" api_key="+api,phase="final_answer"}});f.Append(new{type="response_item",payload=new{type="function_call_output",call_id="call-one",output="{\"password\":\"fiction-secret\",\"api_key\":\""+api+"\"}"}});string view=f.View();Assert(!view.Contains(secret)&&!view.Contains(api)&&!view.Contains("fiction-secret"),"Transcript exposed credentials");
        });
        check("webhook chat tool events pair names and bounded output",()=>{
            var f=new Fixture(root,"tools");f.Append(new{type="response_item",payload=new{type="function_call",call_id="call-one",name="read_file",arguments="DO_NOT_EXPORT_ARGUMENTS"}});f.Append(new{type="response_item",payload=new{type="function_call_output",call_id="call-one",output="Fictional output"}});var activity=RelayEngine.Rows(Json.Get(f.Snapshot(),"activities")).Single();Assert(Json.Str(Json.Get(activity,"name"))=="read_file"&&Json.Str(Json.Get(activity,"status"))=="completed"&&Json.Str(Json.Get(activity,"text"))=="Fictional output"&&!f.View().Contains("DO_NOT_EXPORT_ARGUMENTS"),"Tool lifecycle or argument privacy failed");
        });
        check("webhook chat native tool text blocks preserve output and mask secrets",()=>{
            var f=new Fixture(root,"tool-text-blocks");string secret="whk_"+new string('B',43);f.Append(new{type="response_item",payload=new{type="custom_tool_call",call_id="blocks-one",name="clock"}});f.Append(new{type="response_item",payload=new{type="custom_tool_call_output",call_id="blocks-one",output=new[]{new{type="input_text",text="Script completed successfully"},new{type="input_text",text="{\"current_time\":\"2026-10-17T15:00:00Z\",\"authorization\":\"Bearer "+secret+"\"}"}}}});string view=f.View();Assert(view.Contains("Script completed successfully")&&view.Contains("current_time")&&view.Contains("2026-10-17T15:00:00Z")&&!view.Contains(secret),"Native text-block output was lost or exposed a credential");
        });
        check("webhook chat visible mirrors are deduplicated but repeated turns preserved",()=>{
            var f=new Fixture(root,"dedup");for(int i=0;i<2;i++){f.Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text="Same question"}}}});f.Append(new{type="event_msg",payload=new{type="user_message",message="Same question"}});f.Append(new{type="event_msg",payload=new{type="agent_message",message="Answer "+i,phase="final_answer"}});}var messages=RelayEngine.Rows(Json.Get(f.Snapshot(),"messages")).ToArray();Assert(messages.Count(m=>Json.Str(Json.Get(m,"text"))=="Same question")==2&&messages.Count(m=>Json.Str(Json.Get(m,"text")).StartsWith("Answer ",StringComparison.Ordinal))==2,"Mirrors duplicated or repeated user turn vanished");
        });
        check("webhook chat omits runtime policies and preflight chatter",()=>{
            var f=new Fixture(root,"clean-transcript");f.Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text="# AGENTS.md instructions for fixture PRIVATE_POLICY"}}}});f.Append(new{type="event_msg",payload=new{type="agent_message",message="Chat préparé.",phase="final_answer"}});string view=f.View();Assert(view.Contains("Fictional first question")&&!view.Contains("PRIVATE_POLICY")&&!view.Contains("Chat préparé."),"Technical preflight polluted conversation");
        });
        check("webhook chat supports current native capitalized message content",()=>{
            var f=new Fixture(root,"native-case");f.Append(new{type="event_msg",payload=new{type="item_completed",item=new{type="AgentMessage",id="native-message",content=new[]{new{type="Text",text="Native visible answer"}},phase="final_answer"}}});Assert(f.View().Contains("Native visible answer"),"Current native item shape was ignored");
        });
        check("webhook chat mismatched session metadata is never displayed",()=>{
            var f=new Fixture(root,"wrong-meta");File.WriteAllText(f.Rollout,Json.Write(new{type="session_meta",payload=new{id=Guid.NewGuid().ToString("D")}})+"\n",new UTF8Encoding(false));f.Append(new{type="event_msg",payload=new{type="agent_message",message="OTHER_THREAD_SECRET"}});Assert(!f.View().Contains("OTHER_THREAD_SECRET"),"Wrong native session was exposed");Reject(409,()=>f.Send());
        });
        check("webhook chat stream remains bounded and cursor changes on append",()=>{
            var f=new Fixture(root,"bounds");string first=Json.Str(Json.Get(f.Snapshot(),"cursor"));for(int i=0;i<160;i++)f.Append(new{type="event_msg",payload=new{type="agent_message",message="Visible "+i+new string('a',2000),phase="final_answer"}});var snapshot=f.Snapshot();string json=Json.Write(snapshot);Assert(Encoding.UTF8.GetByteCount(json)<850000&&RelayEngine.Rows(Json.Get(snapshot,"messages")).Count()<=120&&Json.Str(Json.Get(snapshot,"cursor"))!=first,"Transcript exceeded stream limit or ignored append");
        });
        check("webhook chat revocation immediately removes list and snapshot access",()=>{
            var f=new Fixture(root,"revoke");f.Snapshot();string old=f.Key;f.Key=Webhooks.RotateKey(f.Store,f.Route.Id);Reject(401,()=>WebhookChats.Snapshot(f.Store,f.Route.Slug,old,f.Root.Id));f.Route=Webhooks.Route(f.Store,f.Route.Id);f.Route.Enabled=false;Webhooks.SaveRoute(f.Store,f.Route);Reject(503,()=>f.Snapshot());
        });
    }
}
