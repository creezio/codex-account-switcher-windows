using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class RelayTests
{
    private static int sequence;
    private static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    private static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected rejection");}
    private static object Obj(object value){return Json.Read<object>(Json.Write(value));}
    private sealed class Fake : IRelayTransport
    {
        public int Creates,Returns,Followups;
        public bool Offline,ThrowCreate,ThrowReturn,Done=true,MissingFinal,NoUserItem,MalformedCreate,FailTurn,Delegated,PermissionBlocked,Truncated;
        public string Marker,Final="Version 2 publiée — même Site",Phase="final_answer";
        public void Verify(RelayChannel channel){if(Offline)throw new InvalidOperationException("Offline");}
        public void VerifySend(RelayChannel channel,string threadId){Verify(channel);if(PermissionBlocked)throw new InvalidOperationException("Permissions mismatch");}
        public Task<object> Call(RelayChannel channel,string tool,object args,CancellationToken token)
        {
            if(tool=="create_thread"){Creates++;if(ThrowCreate)throw new IOException("Reply lost after creation");Marker=Json.Str(Json.Get(Obj(args),"prompt")).Split('\n')[0];return Task.FromResult(Obj(MalformedCreate?(object)new {clientThreadId="pending"}:new {threadId="target-chat"}));}
            if(tool=="send_message_to_thread"){
                if(channel.Id=="source"){Returns++;if(ThrowReturn)throw new IOException("Reply acknowledgement lost");}
                else {Followups++;Marker=Json.Str(Json.Get(Obj(args),"prompt")).Split('\n')[0];}
                return Task.FromResult(Obj(Delegated?(object)new {threadId="target-chat"}:new {ok=true,turnId="target-turn"}));
            }
            if(tool=="read_thread") {
                if(channel.Id=="source")return Task.FromResult(Obj(new {thread=new {id="source-chat"},turns=new object[0]}));
                object user=new {type="userMessage",content=new[]{new {type="text",text=Marker??""}}};
                if(Delegated)user=new {type="functionCallOutput",name="send_message_to_thread",@namespace="codex_app",output=new {text="<codex_delegation>\n<input>"+Marker+"\nReady</input>\n</codex_delegation>"}};
                object answer=new {type="agentMessage",phase=MissingFinal?"commentary":Phase,text=Final,truncated=Truncated};
                return Task.FromResult(Obj(new {thread=new {id="target-chat"},page=new {hasMore=false},turns=new[]{new {id="target-turn",status=FailTurn?"failed":Done?"completed":"inProgress",items=NoUserItem?new[]{answer}:new[]{user,answer}}}}));
            }
            throw new Exception("Unexpected call "+tool);
        }
    }
    private sealed class Fixture
    {
        public RelayStore Store;public Fake Fake=new Fake();public RelayEngine Engine;
        public Fixture(string root){string folder=Path.Combine(root,"relay-"+(++sequence));Directory.CreateDirectory(folder);Store=new RelayStore(Path.Combine(folder,"store"));foreach(string id in new[]{"source","target"})Store.Register(new RelayChannel {Id=id,Name=id,AccountKey=id+"-account",Home=folder,Workspace=folder,Email=id+"@example.com",Enabled=true,AnchorThreadId=id+"-chat"});Engine=new RelayEngine(Store,Fake);}
        public RelayMessage New(bool back=false,string previous=null,string id=null){return Store.Enqueue("source","source-chat","target","Publish","Ready files","commit-123",back,previous,id);}
        public RelayMessage Process(RelayMessage m){Engine.Process(m.Id,CancellationToken.None).GetAwaiter().GetResult();return Store.Message(m.Id);}
    }
    public static void RunAll(Action<string,Action> check,string root)
    {
        check("relay encrypts prompts and survives restart",()=>{var f=new Fixture(root);var m=f.New();Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,"messages",m.Id+".dpapi"))).Contains("Ready files"),"cleartext message");Assert(new RelayStore(f.Store.Root).Message(m.Id).Prompt=="Ready files","lost persisted message");});
        check("relay idempotency rejects changed payload",()=>{var f=new Fixture(root);var m=f.New();Assert(f.New(false,null,m.Id).Id==m.Id&&f.Store.Messages().Count==1,"duplicate");Throws(()=>f.Store.Enqueue("source","source-chat","target","Changed","Ready files","commit-123",false,null,m.Id));});
        check("relay rejects traversal before opening lock files",()=>{var f=new Fixture(root);Throws(()=>f.Store.Cancel("../../outside"));Throws(()=>f.Store.Lease("../outside"));Throws(()=>f.Store.Channel("../outside"));});
        check("relay pins account and home on reconnect",()=>{var f=new Fixture(root);var c=f.Store.Channel("target");c.AccountKey="other";Throws(()=>f.Store.Register(c));c=f.Store.Channel("target");c.Home=Path.GetDirectoryName(c.Home);Throws(()=>f.Store.Register(c));});
        check("relay rejects different project roots",()=>{var f=new Fixture(root);var c=f.Store.Channel("target");c.Workspace=Path.GetDirectoryName(c.Workspace);f.Store.Register(c);Throws(()=>f.New());});
        check("relay disabled destination blocks new requests",()=>{var f=new Fixture(root);f.Store.SetEnabled("target",false);Throws(()=>f.New());});
        check("relay offline preflight leaves request safely queued",()=>{var f=new Fixture(root);var m=f.New();f.Fake.Offline=true;m=f.Process(m);Assert(m.State=="queued"&&f.Fake.Creates==0,"sent offline");f.Fake.Offline=false;m=f.Process(m);Assert(m.State=="completed"&&f.Fake.Creates==1,"did not resume");});
        check("relay workspace change after enqueue blocks delivery",()=>{var f=new Fixture(root);var m=f.New();var c=f.Store.Channel("target");c.Workspace=Path.GetDirectoryName(c.Workspace);f.Store.Register(c);m=f.Process(m);Assert(m.State=="queued"&&f.Fake.Creates==0,"sent into changed workspace");});
        check("relay complete roundtrip preserves final response",()=>{var f=new Fixture(root);var m=f.Process(f.New(true));Assert(m.State=="completed"&&m.ReturnState=="delivered"&&m.Result==f.Fake.Final&&f.Fake.Returns==1,"roundtrip failed");f.Process(m);Assert(f.Fake.Returns==1&&f.Fake.Creates==1,"duplicated roundtrip");});
        check("relay accepts desktop final_answer and app-server final",()=>{var f=new Fixture(root);f.Fake.Phase="final";Assert(f.Process(f.New()).State=="completed","final phase ignored");});
        check("relay binds initial turn when desktop omits user item",()=>{var f=new Fixture(root);f.Fake.NoUserItem=true;var m=f.Process(f.New());Assert(m.State=="completed"&&m.TargetTurnId=="target-turn","initial turn not bound");});
        check("relay never returns intermediate commentary",()=>{var f=new Fixture(root);f.Fake.Done=false;var m=f.Process(f.New(true));Assert(m.State=="waiting"&&f.Fake.Returns==0&&m.Result==null,"premature return");f.Fake.Done=true;m=f.Process(m);Assert(m.ReturnState=="delivered","completion not delivered");});
        check("relay failed destination is not called success",()=>{var f=new Fixture(root);f.Fake.FailTurn=true;var m=f.Process(f.New(true));Assert(m.State=="failed"&&f.Fake.Returns==0,"failure returned as success");});
        check("relay missing final requires explicit reread",()=>{var f=new Fixture(root);f.Fake.MissingFinal=true;var m=f.Process(f.New());Assert(m.State=="attention","no attention");f.Fake.MissingFinal=false;f.Store.Recheck(m.Id);m=f.Process(m);Assert(m.State=="completed"&&f.Fake.Creates==1,"reread resent task");});
        check("relay ambiguous create never retries",()=>{var f=new Fixture(root);f.Fake.ThrowCreate=true;var m=f.Process(f.New());Assert(m.State=="uncertain","not uncertain");f.Process(m);Assert(f.Fake.Creates==1,"duplicate publication risk");});
        check("relay interrupted persisted sending is not replayed",()=>{var f=new Fixture(root);var m=f.New();m.State="sending";f.Store.Save(m);m=f.Process(m);Assert(m.State=="uncertain"&&f.Fake.Creates==0,"replayed after restart");});
        check("relay ambiguous response delivery preserves result without retry",()=>{var f=new Fixture(root);f.Fake.ThrowReturn=true;var m=f.Process(f.New(true));Assert(m.State=="completed"&&m.ReturnState=="uncertain"&&m.Result==f.Fake.Final,"lost response");f.Process(m);Assert(f.Fake.Returns==1,"duplicate return");});
        check("relay pending creation is not treated as real thread id",()=>{var f=new Fixture(root);f.Fake.MalformedCreate=true;var m=f.Process(f.New());Assert(m.State=="uncertain"&&m.TargetThreadId==null,"invented thread id");});
        check("relay serializes requests to one destination",()=>{var f=new Fixture(root);f.Fake.Done=false;var first=f.Process(f.New());var second=f.Process(f.New());Assert(first.State=="waiting"&&second.State=="queued"&&f.Fake.Creates==1,"concurrent destination tasks");});
        check("relay store lease prevents two workers dispatching same request",()=>{var f=new Fixture(root);var m=f.New();using(f.Store.Lease("message-"+m.Id)){m=f.Process(m);Assert(m.State=="queued"&&f.Fake.Creates==0,"lease ignored");}});
        check("relay cancellation cannot stop an already sent task",()=>{var f=new Fixture(root);var m=f.New();f.Store.Cancel(m.Id);Assert(f.Process(m).State=="cancelled"&&f.Fake.Creates==0,"cancel sent");m=f.Process(f.New());Throws(()=>f.Store.Cancel(m.Id));});
        check("relay continuation reuses destination conversation",()=>{var f=new Fixture(root);var first=f.Process(f.New());var next=f.Process(f.New(false,first.Id));Assert(next.State=="completed"&&next.TargetThreadId==first.TargetThreadId&&f.Fake.Creates==1&&f.Fake.Followups==1,"continuation created duplicate thread");});
        check("relay paused or unfinished task cannot be continued as completed",()=>{var f=new Fixture(root);f.Fake.Done=false;var m=f.Process(f.New());Throws(()=>f.New(false,m.Id));});
        check("relay continuation cannot reuse a conversation from another project",()=>{var f=new Fixture(root);var m=f.Process(f.New());foreach(string id in new[]{"source","target"}){var c=f.Store.Channel(id);c.Workspace=Path.GetDirectoryName(c.Workspace);f.Store.Register(c);}Throws(()=>f.New(false,m.Id));});
        check("relay reviewed uncertain task releases channel without resending",()=>{var f=new Fixture(root);f.Fake.ThrowCreate=true;var m=f.Process(f.New());f.Store.CloseReviewed(m.Id);f.Fake.ThrowCreate=false;Assert(f.Process(f.New()).State=="completed"&&f.Fake.Creates==2,"reviewed task did not release channel");});
        check("relay matches desktop delegated followup without returned turn id",()=>{var f=new Fixture(root);f.Fake.Delegated=true;var first=f.Process(f.New());var next=f.Process(f.New(false,first.Id));Assert(next.State=="completed"&&f.Fake.Followups==1,"desktop followup lost");});
        check("relay rejects marker appearing in arbitrary tool output",()=>{var turn=Obj(new {items=new[]{new {type="functionCallOutput",name="exec_command",@namespace="shell",output=new {text="<input>[CREEZIO_REQUEST:x]\n"}}}});Assert(!RelayEngine.HasMarker(turn,"[CREEZIO_REQUEST:x]"),"untrusted output matched");});
        check("relay permission mismatch stays queued before dispatch",()=>{var f=new Fixture(root);f.Fake.PermissionBlocked=true;var m=f.Process(f.New());Assert(m.State=="queued"&&f.Fake.Creates==0&&m.Error!=null,"sent without permissions");});
        check("relay truncated final requires inspection and is never returned",()=>{var f=new Fixture(root);f.Fake.Truncated=true;var m=f.Process(f.New(true));Assert(m.State=="attention"&&f.Fake.Returns==0,"truncated response returned");});
        check("relay full access requires both sandbox and approvals",()=>{Assert(RelayPermissions.Describe(Obj(new {sandbox_policy=new {type="danger-full-access"},approval_policy="never"}))=="full-access","full access missed");Assert(RelayPermissions.Describe(Obj(new {sandbox_policy=new {type="read-only"},approval_policy="never"}))!="full-access","readonly promoted");Assert(RelayPermissions.Describe(Obj(new {sandbox_policy=new {type="danger-full-access"},approval_policy="on-request"}))!="full-access","approvals overlooked");});
        check("relay unknown permissions never become full access",()=>{Assert(RelayPermissions.Read(root,"../invalid")=="unknown","invalid thread read");Throws(()=>RelayPermissions.Require(new RelayChannel{Id="test",Home=root,RequireFullAccess=true},"missing"));});
        check("relay reads latest effective permissions in long history",()=>{string id="01a0fe5f-604e-72f0-9578-0dbd87248f0a";var day=new DateTime(1970,1,1).AddMilliseconds(Convert.ToInt64(id.Replace("-","").Substring(0,12),16));string home=Path.Combine(root,"permission-test"),dir=Path.Combine(home,"sessions",day.ToString("yyyy"),day.ToString("MM"),day.ToString("dd"));Directory.CreateDirectory(dir);string path=Path.Combine(dir,"rollout-"+id+".jsonl");string line=Json.Write(new {type="turn_context",payload=new {sandbox_policy=new {type="danger-full-access"},approval_policy="never"}});File.WriteAllText(path,line+"\n"+String.Join("\n",Enumerable.Repeat(new string('x',1000),2300)));Assert(RelayPermissions.Read(home,id)=="full-access","lost older context in long turn");File.AppendAllText(path,"\n"+Json.Write(new {type="turn_context",payload=new {sandbox_policy=new {type="read-only"},approval_policy="on-request"}})+"\n");Assert(RelayPermissions.Read(home,id)!="full-access","stale full access reused");});
    }
}
