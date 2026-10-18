using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class WebhookDispatchTests
{
    private static void Assert(bool condition,string text){if(!condition)throw new Exception(text);}
    private sealed class Adapter:IRelayTransport
    {
        internal readonly GeneralRelayTests.Adapter Inner=new GeneralRelayTests.Adapter();
        internal object Create,Send;
        public void Verify(RelayChannel c){Inner.Verify(c);}
        public void VerifySend(RelayChannel c,string t){Inner.VerifySend(c,t);}
        public Task<object> Call(RelayChannel c,string tool,object args,CancellationToken token){if(tool=="create_thread")Create=Json.Read<object>(Json.Write(args));if(tool=="send_message_to_thread")Send=Json.Read<object>(Json.Write(args));return Inner.Call(c,tool,args,token);}
    }
    private sealed class Fixture
    {
        internal readonly GeneralRelayTests.Fixture Core;
        internal readonly Adapter Native=new Adapter();
        internal WebhookRoute Route;
        internal readonly string Key;
        internal int Connections;
        internal bool Offline,WrongAccount;
        internal RelayStore Store {get{return Core.Store;}}
        internal Fixture(string root)
        {
            Core=new GeneralRelayTests.Fixture(root);Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true});
            Route=Webhooks.SaveRoute(Store,new WebhookRoute{Slug="external-app",Name="External application",InstanceId=Guid.NewGuid().ToString("N"),AccountKey="target",Home=Core.Folder,Workspace=Core.Folder,Enabled=true,Access="desktop",PermissionProfile="full-access",Model="gpt-6.1-sol",ReasoningEffort="ultra",PromptPrefix="Only inspect this project."});
            Key=Webhooks.RotateKey(Store,Route.Id);Route=Webhooks.Route(Store,Route.Id);
        }
        internal WebhookJob Accept(string id){return Webhooks.Submit(Store,Route.Slug,Key,id,"{\"title\":\"Inspect prepared files\",\"prompt\":\"Review the prepared change.\",\"context\":\"Issue 12\"}");}
        internal WebhookJob Process(WebhookJob job)
        {
            WebhookDispatch.Process(Store,job,CancellationToken.None,(route,ct)=>{
                Connections++;if(Offline)throw new InvalidOperationException("offline");var channel=Store.Channel("target");if(WrongAccount)channel.AccountKey="another-account";return Task.FromResult(channel);
            },route=>{},ct=>Task.FromResult(0),Native).GetAwaiter().GetResult();return Webhooks.Job(Store,job.Id);
        }
        internal RelayMessage Run(WebhookJob job,string permissions="full-access")
        {
            var engine=new RelayEngine(Store,Native,(h,t)=>permissions);
            for(int n=0;n<3;n++){engine.Process(job.Id,CancellationToken.None).GetAwaiter().GetResult();var m=Store.Message(job.Id);if(m.State!="queued"||m.DispatchPhase!="work"||m.Error!=null)return m;}
            return Store.Message(job.Id);
        }
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("webhook headless mode never falls through to an open desktop after admission",()=>{
            var headless=new WebhookRoute{Access="headless"};WebhookDispatch.RequireExecutionMode(headless,new InstanceState{Phase="stopped",Running=false});
            foreach(var state in new[]{(InstanceState)null,new InstanceState{Phase="running",Running=true},new InstanceState{Phase="background",Running=true},new InstanceState{Phase="unknown"},new InstanceState{Phase="starting"},new InstanceState{Phase="stopping"}}){
                bool rejected=false;try{WebhookDispatch.RequireExecutionMode(headless,state);}catch(InvalidOperationException){rejected=true;}Assert(rejected,"A GUI or unknown owner widened the headless route");
            }
            WebhookDispatch.RequireExecutionMode(new WebhookRoute{Access="desktop"},new InstanceState{Phase="running",Running=true});
        });
        check("webhook admission reuses durable relay and does not invoke model",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("one"));Assert(j.RelayJobId==j.Id&&f.Store.Messages().Count==1&&f.Native.Inner.Created==0,"admission performed native work");var m=f.Store.Message(j.Id);Assert(m.OperatorOrigin&&m.WebhookJobId==j.Id&&m.Prompt.Contains("Only inspect")&&m.Prompt.Contains("Issue 12")&&RelayEngine.Envelope(m,f.Store.Channel("target")).Contains("route webhook"),"origin or context lost");});
        check("webhook duplicate admission creates exactly one native job",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("stable"));f.Process(f.Accept("stable"));Assert(f.Store.Messages().Count==1&&f.Connections==1,"duplicate relay admission");f.Run(j);f.Process(j);Assert(f.Native.Inner.Created==1&&f.Native.Inner.WorkSent==1,"native work duplicated");});
        check("webhook crash after relay admission reconciles the same receipt",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("crash"));j.RelayJobId=null;j.State="queued";Webhooks.SaveJob(f.Store,j);f.Offline=true;j=f.Process(j);Assert(j.RelayJobId==j.Id&&f.Connections==1&&f.Store.Messages().Count==1,"crash replayed admission");});
        check("webhook offline instance waits then resumes once",()=>{var f=new Fixture(root);f.Offline=true;var j=f.Process(f.Accept("offline"));Assert(j.State=="waiting"&&f.Store.Messages().Count==0,"offline request lost");f.Offline=false;j=f.Process(j);Assert(j.RelayJobId==j.Id&&f.Store.Messages().Count==1,"offline recovery failed");});
        check("webhook route change before admission cannot redirect a job",()=>{var f=new Fixture(root);var j=f.Accept("route-change");f.Route.Workspace=System.IO.Path.GetDirectoryName(f.Core.Folder);Webhooks.SaveRoute(f.Store,f.Route);j=f.Process(j);Assert(j.State=="blocked"&&f.Connections==0&&f.Store.Messages().Count==0,"frozen route redirected");});
        check("webhook wrong connected account blocks before enqueue",()=>{var f=new Fixture(root);f.WrongAccount=true;var j=f.Process(f.Accept("wrong-account"));Assert(j.State=="blocked"&&f.Store.Messages().Count==0,"wrong account admitted");});
        check("webhook revocation after admission blocks the actual native send",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("revoke"));f.Route.Enabled=false;Webhooks.SaveRoute(f.Store,f.Route);var m=f.Run(j);Assert(m.State=="cancelled"&&m.BlockReason=="webhook-route"&&f.Native.Inner.Created==0,"revoked route sent");});
        check("webhook global pause preserves queued native job for resume",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("pause"));Webhooks.SaveConfig(f.Store,new WebhookConfig{Enabled=false});var m=f.Run(j);Assert(m.State=="queued"&&m.BlockReason=="paused"&&f.Native.Inner.Created==0,"paused webhook started");Webhooks.SaveConfig(f.Store,new WebhookConfig{Enabled=true});m=f.Run(j);Assert(m.State=="completed"&&f.Native.Inner.Created==1,"paused webhook did not resume");});
        check("webhook native execution receives exact configured model and effort",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("model"));f.Run(j);Assert(Json.Str(Json.Get(f.Native.Create,"model"))=="gpt-6.1-sol"&&Json.Str(Json.Get(f.Native.Create,"thinking"))=="ultra"&&Json.Str(Json.Get(f.Native.Send,"model"))=="gpt-6.1-sol"&&Json.Str(Json.Get(f.Native.Send,"thinking"))=="ultra","model or effort silently ignored");});
        check("webhook cannot silently elevate a newly prepared native chat",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("permissions"));var m=f.Run(j,"workspace-write");Assert(m.DispatchPhase=="preflight"&&m.BlockReason=="permissions"&&f.Native.Inner.WorkSent==0,"work sent before native permissions confirmed");});
        check("webhook completion exposes final result without tool outputs",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("result"));f.Run(j);j=f.Process(j);Assert(j.State=="completed"&&j.Result.Contains("Checked.")&&j.ThreadId=="target-chat","final result not tracked");});
        check("webhook uncertain native result is never resubmitted",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("uncertain"));var m=f.Store.Message(j.Id);m.State="uncertain";m.Error="Native acknowledgement missing";f.Store.Save(m);j=f.Process(j);Assert(j.State=="uncertain"&&f.Connections==1&&f.Native.Inner.Created==0,"uncertain send repeated");});
        check("webhook missing durable relay receipt remains uncertain",()=>{var f=new Fixture(root);var j=f.Accept("missing");j.RelayJobId=j.Id;j.State="monitoring";j=f.Process(j);Assert(j.State=="uncertain"&&f.Connections==0,"lost receipt caused resend");});
        check("webhook monitoring follows native approval and subsequent recovery",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("approval"));var m=f.Store.Message(j.Id);m.State="waiting";m.BlockReason="approval";m.Error="Approval required";f.Store.Save(m);j=f.Process(j);Assert(j.State=="attention","native approval hidden");m.State="completed";m.BlockReason=null;m.Result="Done";f.Store.Save(m);j=f.Process(j);Assert(j.State=="completed"&&f.Connections==1,"approval recovery lost");});
        check("webhook explicit new-chat route does not reuse agent history",()=>{var f=new Fixture(root);f.Core.Policy.Agents.Single(a=>a.Channel=="target").ReuseConversation=true;f.Core.Save();var old=f.Process(f.Accept("old"));f.Run(old);var next=f.Process(f.Accept("new"));f.Run(next);Assert(f.Native.Inner.Created==2,"new conversation route silently reused history");});
        check("webhook route revoked during connect is blocked before local admission",()=>{var f=new Fixture(root);var j=f.Accept("during-connect");WebhookDispatch.Process(f.Store,j,CancellationToken.None,(route,ct)=>{f.Route.Enabled=false;Webhooks.SaveRoute(f.Store,f.Route);return Task.FromResult(f.Store.Channel("target"));},r=>{},ct=>Task.FromResult(0),f.Native).GetAwaiter().GetResult();Assert(Webhooks.Job(f.Store,j.Id).State=="blocked"&&f.Store.Messages().Count==0,"read-only preflight failure became uncertain or was admitted");});
        check("webhook uncertain receipt follows native reconciliation without a new job",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("reconciled"));var m=f.Store.Message(j.Id);m.State="uncertain";f.Store.Save(m);j=f.Process(j);m.State="completed";m.Result="Reconciled final";f.Store.Save(m);j=f.Process(j);Assert(j.State=="completed"&&j.Result=="Reconciled final"&&f.Connections==1,"native reconciliation lost or replayed");});
        check("webhook classified native journal does not remain in progress forever",()=>{var f=new Fixture(root);var j=f.Process(f.Accept("closed"));var m=f.Store.Message(j.Id);m.State="closed";f.Store.Save(m);j=f.Process(j);Assert(j.State=="cancelled"&&f.Connections==1,"classified receipt remained monitoring");});
    }
}
