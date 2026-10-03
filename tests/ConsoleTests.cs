using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class ConsoleTests
{
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void Throws(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected rejection");}
    internal sealed class Fake : IRelayTransport
    {
        public int Creates,Sends;public bool Occupied,WrongThread,Offline,UnknownSend;
        public string Prompt="",LastThread="existing";
        private static object Obj(object o){return Json.Read<object>(Json.Write(o));}
        public void Verify(RelayChannel c){if(Offline)throw new InvalidOperationException("offline");}
        public void VerifySend(RelayChannel c,string t){Verify(c);}
        public Task<object> Call(RelayChannel c,string tool,object args,CancellationToken token)
        {
            Verify(c);var a=Obj(args);
            if(tool=="list_threads")return Task.FromResult(Obj(new{threads=new[]{new{threadId="existing",title="Existing chat",source="codex"}},pinnedThreads=new object[0]}));
            if(tool=="create_thread"){Creates++;Prompt=Json.Str(Json.Get(a,"prompt"));LastThread="created";return Task.FromResult(Obj(new{threadId=LastThread,turnId="turn"}));}
            if(tool=="send_message_to_thread"){Sends++;Prompt=Json.Str(Json.Get(a,"prompt"));LastThread=Json.Str(Json.Get(a,"threadId"));if(UnknownSend)throw new IOException("unknown");return Task.FromResult(Obj(new{turnId="turn"}));}
            if(tool=="read_thread")return Task.FromResult(Obj(new{thread=new{id=WrongThread?"other":Json.Str(Json.Get(a,"threadId")),status=new{type=Occupied?"active":"idle"}},turns=new[]{new{id="turn",status=Occupied?"inProgress":"completed",items=new object[]{new{type="userMessage",content=new[]{new{type="text",text=Prompt}}},new{type="agentMessage",phase="final",text="OK\nCREEZIO_OUTCOME {\"status\":\"succeeded\"}"}}}},page=new{hasMore=false}}));
            throw new InvalidOperationException(tool);
        }
    }
    public static void RunAll(Action<string,Action> check,string root)
    {
        int n=0;
        Func<RelayStore> fixture=()=>{string p=Path.Combine(root,"console-"+(++n));Directory.CreateDirectory(p);var s=new RelayStore(Path.Combine(p,"relay"));s.Register(new RelayChannel{Id="only",Name="Only",Home=p,Workspace=p,AccountKey="one",Enabled=true,AnchorThreadId="anchor"});return s;};
        Func<RelayJobSpec> spec=()=>new RelayJobSpec{Id=Guid.NewGuid().ToString("N"),To="only",Title="Question",Prompt="Reply OK",Access="read"};
        check("operator prompt works with one channel and no source chat",()=>{var s=fixture();var f=new Fake();var m=new DirectMessages(s,f).Submit(spec(),"existing",CancellationToken.None).Result;new RelayEngine(s,f).Process(m.Id,CancellationToken.None).Wait();m=s.Message(m.Id);Assert(m.State=="completed"&&m.OperatorOrigin&&m.SourceThreadId==null&&!m.ReturnToSource&&f.Sends==1&&f.Creates==0,"not a direct completion");});
        check("operator new chat passes permission preflight before work",()=>{var s=fixture();var f=new Fake();var m=new DirectMessages(s,f).Submit(spec(),null,CancellationToken.None).Result;var e=new RelayEngine(s,f,(h,t)=>"read-only / on-request");e.Process(m.Id,CancellationToken.None).Wait();Assert(f.Creates==1&&f.Sends==0&&s.Message(m.Id).State=="queued","work sent before preflight");e.Process(m.Id,CancellationToken.None).Wait();Assert(s.Message(m.Id).State=="completed"&&f.Sends==1,"work not completed");});
        check("operator waits for native chat activity without interrupting",()=>{var s=fixture();var f=new Fake{Occupied=true};var m=new DirectMessages(s,f).Submit(spec(),"existing",CancellationToken.None).Result;var e=new RelayEngine(s,f);e.Process(m.Id,CancellationToken.None).Wait();Assert(f.Sends==0&&s.Message(m.Id).BlockReason=="conversation-busy","interrupted native turn");f.Occupied=false;e.Process(m.Id,CancellationToken.None).Wait();Assert(f.Sends==1&&s.Message(m.Id).State=="completed","did not resume");});
        check("operator cannot target a chat not recognized by instance",()=>{var s=fixture();var f=new Fake{WrongThread=true};Throws(()=>new DirectMessages(s,f).Submit(spec(),"existing",CancellationToken.None).GetAwaiter().GetResult());Assert(s.Count()==0,"persisted invalid target");});
        check("operator retry is idempotent offline and target is pinned",()=>{var s=fixture();var f=new Fake();var q=spec();var d=new DirectMessages(s,f);var first=d.Submit(q,"existing",CancellationToken.None).Result;f.Offline=true;Assert(d.Submit(q,"existing",CancellationToken.None).Result.Id==first.Id&&s.Count()==1,"duplicate submission");Throws(()=>d.Submit(q,"other",CancellationToken.None).GetAwaiter().GetResult());});
        check("operator uncertain delivery reconciles instead of resending",()=>{var s=fixture();var f=new Fake{UnknownSend=true};var m=new DirectMessages(s,f).Submit(spec(),"existing",CancellationToken.None).Result;var e=new RelayEngine(s,f);e.Process(m.Id,CancellationToken.None).Wait();Assert(s.Message(m.Id).State=="uncertain","lost uncertainty");e.Process(m.Id,CancellationToken.None).Wait();Assert(f.Sends==1&&s.Message(m.Id).State=="waiting","replayed uncertain message");});
        check("operator honours project target restrictions",()=>{var s=fixture();var p=new RelayPolicy();p.Projects.Add(new RelayProject{Id="restricted",Workspace=s.Channel("only").Workspace,TargetChannels="different"});RelayPolicies.Save(s,p);var q=spec();q.Project="restricted";Throws(()=>new DirectMessages(s,new Fake()).Submit(q,null,CancellationToken.None).GetAwaiter().GetResult());});
        check("agent self-delegation remains refused",()=>{var s=fixture();Throws(()=>s.Enqueue("only","anchor","only","title","prompt",null,false,null));});
        check("existing conversation never mistakes an unrelated single turn for reply",()=>{var s=fixture();var m=s.Enqueue("only",null,"only","title","prompt",null,false,null,null,x=>{x.SchemaVersion=3;x.OriginVerified=true;x.NewConversation=false;x.TargetThreadId="existing";x.RequestedThreadId="existing";x.State="waiting";},true);var f=new Fake();new RelayEngine(s,f).Process(m.Id,CancellationToken.None).Wait();Assert(s.Message(m.Id).State=="waiting","unrelated turn was accepted");});
    }
}
