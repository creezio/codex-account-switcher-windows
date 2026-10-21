using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class ConversationTransferTests
{
    private static object Obj(object value){return Json.Read<object>(Json.Write(value));}
    private static string Text(object value,string field){return Json.Str(Json.Get(Obj(value),field));}
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Reject(int code,Action action){try{action();}catch(WebhookException error){Assert(error.StatusCode==code,"Unexpected status "+error.StatusCode);return;}throw new Exception("Expected rejection "+code);}
    private sealed class Backend:ITransferBackend
    {
        internal int Copies,Archives;internal bool Busy,LostCopy,LostArchive,Archived;
        internal Action AfterCopy=null;
        internal TaskCompletionSource<bool> Pause=null;
        private readonly Dictionary<string,ThreadCopyRecord> copies=new Dictionary<string,ThreadCopyRecord>();
        public string Identity(string instance){return instance+"-account";}
        public Task<TransferProject[]> Projects(string instance,CancellationToken token){throw new Exception("No project inventory expected");}
        public async Task<string> Fingerprint(string instance,string thread,bool idle,CancellationToken token){if(Busy&&idle)throw new InvalidOperationException("La discussion est occupée.");if(Pause!=null)await Pause.Task.ConfigureAwait(false);return "digest-"+thread;}
        public Task<ThreadCopyRecord> Copy(TransferRecord record,TransferItem item,CancellationToken token){
            ThreadCopyRecord result;if(!copies.TryGetValue(item.CopyId,out result)){Copies++;result=new ThreadCopyRecord{Id=item.CopyId,State="completed",TargetThread=Guid.NewGuid().ToString()};copies[item.CopyId]=result;}
            if(AfterCopy!=null)AfterCopy();if(LostCopy){LostCopy=false;throw new IOException("Réponse de copie perdue.");}return Task.FromResult(result);
        }
        public Task<string> Project(TransferRecord record,CancellationToken token){throw new Exception("No project creation expected");}
        public Task Assign(TransferRecord record,TransferItem item,CancellationToken token){throw new Exception("No project assignment expected");}
        public Task Archive(string instance,string thread,CancellationToken token){Archives++;Archived=true;if(LostArchive){LostArchive=false;throw new IOException("Réponse d'archivage perdue.");}return Task.CompletedTask;}
        public Task<bool> IsArchived(string instance,string thread,CancellationToken token){return Task.FromResult(Archived);}
        public Task Open(TransferRecord record,TransferItem item,CancellationToken token){throw new Exception("No window opening expected");}
    }
    private sealed class Fixture
    {
        internal readonly RelayStore Store;internal readonly Backend Native=new Backend();internal readonly Queue<Func<Task>> Tasks=new Queue<Func<Task>>();
        internal readonly RelaySession Session;internal readonly ConversationTransfers Service;
        internal readonly string Source=new string('a',32),Destination=new string('b',32),Thread=Guid.NewGuid().ToString(),Key=Guid.NewGuid().ToString("N");
        internal bool Revoked,Reassigned;internal string LastCursor,LastQuery;
        internal int MissingReads;
        internal Fixture(string root,RelayStore store=null,bool asynchronous=false){
            Store=store??new RelayStore(Path.Combine(root,"conversation-transfer-"+Guid.NewGuid().ToString("N")));
            Session=new RelaySession{Home=Path.Combine(Store.Root,"caller"),Account="caller-account",Thread=Guid.NewGuid().ToString(),Token="fixture-session"};
            Service=new ConversationTransfers(Store,new ConversationTransferDependencies{
                Resolve=(route,id)=>{if(id!=Source&&id!=Destination)throw new WebhookException(404,"Unknown instance");return new ConversationInstancePin{Id=id,Account=id+"-account"+(Reassigned?"-changed":""),Home=Path.Combine(Store.Root,id),GrantRevision=1};},
                AuthorizeSession=session=>{if(Revoked)throw new WebhookException(403,"Revoked");},
                List=(id,query,cursor,token)=>{LastQuery=query;LastCursor=cursor;return Task.FromResult(new CopyThreadPage{Threads=new[]{new CopyThreadChoice{Id=Thread,Title="Fixture",Path="PRIVATE-ROLLOUT-PATH",Folder=Store.Root}},Cursor="page-two"});},
                Backend=Native,Schedule=asynchronous?null:(Action<Func<Task>>)(work=>Tasks.Enqueue(work)),
                OpenRecord=path=>{if(MissingReads>0){MissingReads--;throw new FileNotFoundException("Simulated atomic replacement gap");}return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);}});
        }
        internal object Start(string kind="copy_thread"){return Service.Start(null,null,Session,Key,kind,Source,Destination,Thread,"Copie de recette",true);}
        internal void Drain(){while(Tasks.Count>0)Tasks.Dequeue()().GetAwaiter().GetResult();}
        internal object Read(string id){return Service.Read(null,null,Session,id);}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("conversation plugin copy preserves original, verifies native journal and returns target",()=>{var f=new Fixture(root);string id=Text(f.Start(),"id");Assert(Text(f.Read(id),"state")=="queued"&&f.Native.Copies==0,"Submission mutated history");Assert(ApplicationUpdates.BusyReason(f.Store)!=null,"Queued operation missing from updater activity");f.Drain();var result=f.Read(id);Assert(Text(result,"state")=="completed"&&Text(result,"targetThreadId").Length>0&&f.Native.Copies==1&&f.Native.Archives==0&&!Object.Equals(Json.Get(Obj(result),"sourceArchived"),true),"Copy contract broken");Assert(ApplicationUpdates.BusyReason(f.Store)==null,"Completed operation blocks updates");});
        check("conversation transfer archives only verified destination and repeats no writes",()=>{var f=new Fixture(root);string id=Text(f.Start("transfer_thread"),"id");f.Drain();var r=f.Read(id);Assert(Text(r,"state")=="completed"&&Object.Equals(Json.Get(Obj(r),"sourceArchived"),true)&&f.Native.Archives==1,"Archive not confirmed");f.Start("transfer_thread");f.Service.Resume(null,null,f.Session,id);f.Drain();Assert(f.Native.Copies==1&&f.Native.Archives==1,"Completed operation replayed");});
        check("conversation busy source is blocked without copy and explicit resume reuses selection",()=>{var f=new Fixture(root);f.Native.Busy=true;string id=Text(f.Start(),"id");f.Drain();Assert(Text(f.Read(id),"state")=="blocked"&&f.Native.Copies==0,"Busy source copied");f.Start();Assert(f.Tasks.Count==0,"Retry automatically resumed blocker");f.Native.Busy=false;f.Service.Resume(null,null,f.Session,id);f.Drain();Assert(Text(f.Read(id),"state")=="completed"&&f.Native.Copies==1,"Resume failed");});
        check("conversation lost copy response resumes same native copy id without duplicates",()=>{var f=new Fixture(root);f.Native.LostCopy=true;string id=Text(f.Start("transfer_thread"),"id");f.Drain();Assert(Text(f.Read(id),"state")=="blocked"&&f.Native.Archives==0,"Uncertain copy archived");f.Service.Resume(null,null,f.Session,id);f.Drain();Assert(Text(f.Read(id),"state")=="completed"&&f.Native.Copies==1&&f.Native.Archives==1,"Copy duplicated after lost response");});
        check("conversation lost archive response reconciles original without rearchiving",()=>{var f=new Fixture(root);f.Native.LostArchive=true;string id=Text(f.Start("transfer_thread"),"id");f.Drain();Assert(Text(f.Read(id),"state")=="blocked","Lost archive reported success");f.Service.Resume(null,null,f.Session,id);f.Drain();Assert(Text(f.Read(id),"state")=="completed"&&f.Native.Copies==1&&f.Native.Archives==1,"Archive replayed");});
        check("conversation selection and copy versus transfer are immutable for one request key",()=>{var f=new Fixture(root);f.Start();Reject(409,()=>f.Start("transfer_thread"));Reject(409,()=>f.Service.Start(null,null,f.Session,f.Key,"copy_thread",f.Source,f.Destination,f.Thread,"Different title",true));Assert(f.Tasks.Count==1,"Conflicting operation scheduled");});
        check("conversation receipts are private to exact calling chat and transport principal",()=>{var f=new Fixture(root);string id=Text(f.Start(),"id");var other=new RelaySession{Home=f.Session.Home,Account=f.Session.Account,Thread=Guid.NewGuid().ToString()};Reject(404,()=>f.Service.Read(null,null,other,id));Reject(404,()=>f.Service.Resume(null,null,other,id));});
        check("conversation rejects missing mandate, invalid thread, same profile and malformed key",()=>{var f=new Fixture(root);Reject(403,()=>f.Service.Start(null,null,f.Session,f.Key,"copy_thread",f.Source,f.Destination,f.Thread,"Title",false));Reject(400,()=>f.Service.Start(null,null,f.Session,f.Key,"copy_thread",f.Source,f.Destination,"invalid","Title",true));Reject(400,()=>f.Service.Start(null,null,f.Session,f.Key,"copy_thread",f.Source,f.Source,f.Thread,"Title",true));Reject(400,()=>f.Service.Start(null,null,f.Session,"bad\nkey","copy_thread",f.Source,f.Destination,f.Thread,"Title",true));Assert(f.Tasks.Count==0,"Invalid request admitted");});
        check("conversation account reassignment blocks work and receipt disclosure",()=>{var f=new Fixture(root);string id=Text(f.Start(),"id");f.Reassigned=true;f.Drain();Assert(f.Native.Copies==0,"Reassigned account used");Reject(409,()=>f.Read(id));Reject(409,()=>f.Service.Resume(null,null,f.Session,id));});
        check("conversation revoked caller after copy cannot archive source",()=>{var f=new Fixture(root);f.Native.AfterCopy=()=>f.Revoked=true;string id=Text(f.Start("transfer_thread"),"id");f.Drain();Assert(f.Native.Copies==1&&f.Native.Archives==0,"Revocation ignored before archiving");Reject(403,()=>f.Read(id));f.Revoked=false;f.Native.AfterCopy=null;f.Service.Resume(null,null,f.Session,id);f.Drain();Assert(Text(f.Read(id),"state")=="completed"&&f.Native.Copies==1,"Reconciliation duplicated copy");});
        check("conversation metadata listing preserves cursor/search without private rollout paths",()=>{var f=new Fixture(root);var r=f.Service.List(null,null,f.Session,f.Source,"Fixture","next",CancellationToken.None).GetAwaiter().GetResult();Assert(f.LastCursor=="next"&&f.LastQuery=="Fixture"&&Text(r,"cursor")=="page-two"&&!Json.Write(r).Contains("PRIVATE-ROLLOUT-PATH")&&f.Native.Copies==0,"Metadata listing leaked or mutated");});
        check("conversation process loss reports interrupted and never silently restarts",()=>{var f=new Fixture(root);string id=Text(f.Start(),"id");var op=f.Store.ReadRecord<ConversationTransferOperation>("conversation-transfer-op-"+id+".dpapi");op.OwnerPid=0;op.OwnerStarted=0;f.Store.WriteRecord("conversation-transfer-op-"+id+".dpapi",op);f.Tasks.Clear();Assert(Text(f.Read(id),"state")=="interrupted","Dead worker reported running");f.Start();Assert(f.Tasks.Count==0,"Lost process auto resumed");f.Service.Resume(null,null,f.Session,id);f.Drain();Assert(Text(f.Read(id),"state")=="completed","Explicit recovery failed");});
        check("conversation frozen update blocks admission without starting native work",()=>{var f=new Fixture(root);f.Store.WriteRecord(ApplicationUpdates.TransactionName,new ApplicationUpdateTransaction{Frozen=true});Reject(503,()=>f.Start());Assert(f.Tasks.Count==0&&f.Native.Copies==0,"Frozen update admitted operation");});
        check("conversation status stabilizes missing record during atomic replacement without false 404",()=>{var f=new Fixture(root);string id=Text(f.Start(),"id");f.MissingReads=2;Assert(Text(f.Read(id),"state")=="queued"&&f.MissingReads==0&&f.Tasks.Count==1,"Atomic replacement became a missing or new operation");});
        check("conversation real background task returns durable receipt and concurrent retry creates one copy",()=>{
            var f=new Fixture(root,null,true);f.Native.Pause=new TaskCompletionSource<bool>();string id=null;
            try{id=Text(f.Start(),"id");Assert(Text(f.Read(id),"state")!="completed"&&f.Native.Copies==0,"Submission waited for native copy");Assert(Text(f.Start(),"id")==id,"Concurrent retry changed operation");f.Service.Resume(null,null,f.Session,id);}
            finally{f.Native.Pause.TrySetResult(true);}
            Assert(SpinWait.SpinUntil(()=>Text(f.Read(id),"state")=="completed",TimeSpan.FromSeconds(20))&&f.Native.Copies==1&&f.Native.Archives==0,"Background processing failed or duplicated copy");
        });
        check("conversation MCP exposes all five tools and executes bound copy with status",()=>{
            var f=new Fixture(root);var server=new RelayMcp(f.Store,f.Session.Home,f.Service,key=>{if(key!=f.Session.Token)throw new InvalidOperationException("Unbound");return f.Session;});
            Func<string,object,object> call=(name,args)=>server.Handle(Obj(new{jsonrpc="2.0",id=3,method="tools/call",@params=new{name,arguments=args}}),CancellationToken.None).GetAwaiter().GetResult();
            server.Handle(Obj(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2025-11-25"}}),CancellationToken.None).GetAwaiter().GetResult();
            var names=RelayMcp.Tools().Select(t=>Text(t,"name")).ToArray();foreach(string name in new[]{"list_instance_conversations","copy_conversation","transfer_conversation","get_conversation_transfer","resume_conversation_transfer"})Assert(names.Contains(name),"Missing tool "+name);
            var result=call("copy_conversation",new{session=f.Session.Token,id=f.Key,source=f.Source,destination=f.Destination,threadId=f.Thread,title="Fixture",explicitRequest=true});var envelope=Json.Get(Obj(result),"result");Assert(!Object.Equals(Json.Get(envelope,"isError"),true),"MCP submission failed");var content=RelayEngine.Rows(Json.Get(envelope,"content")).First();string id=Text(Json.Read<object>(Json.Str(Json.Get(content,"text"))),"id");f.Drain();
            var status=call("get_conversation_transfer",new{session=f.Session.Token,id});Assert(Json.Write(status).Contains("completed")&&f.Native.Copies==1&&f.Native.Archives==0,"MCP result mismatch");
            var moved=call("transfer_conversation",new{session=f.Session.Token,id=Guid.NewGuid().ToString("N"),source=f.Source,destination=f.Destination,threadId=f.Thread,title="Transfer fixture",explicitRequest=true});Assert(!Object.Equals(Json.Get(Json.Get(Obj(moved),"result"),"isError"),true),"MCP transfer rejected");f.Drain();Assert(f.Native.Copies==2&&f.Native.Archives==1,"MCP transfer did not use archiving engine");
            var denied=call("copy_conversation",new{session="invented"});Assert(Object.Equals(Json.Get(Json.Get(Obj(denied),"result"),"isError"),true),"Unbound MCP call admitted");
        });
        check("conversation signed HTTP webhook copy, status and resume use same native journal",()=>{
            var admin=new PortalAdministrationTests.Fixture(root);var f=new Fixture(root,admin.Store);string collection="conversation-transfers";
            var body=new{kind="transfer_thread",source=f.Source,destination=f.Destination,threadId=f.Thread,title="HTTP fixture",explicitRequest=true};
            var submit=admin.Request(collection,"POST",Json.Write(body));var response=PortalAdministrationHttp.Handle(f.Store,submit,null,null,f.Service);Assert(response.Status==202,"Signed transfer rejected: "+response.Status+" "+response.Body);string id=Text(Json.Read<object>(response.Body),"id");f.Drain();
            response=PortalAdministrationHttp.Handle(f.Store,admin.Request(collection+"/"+id,"GET",""),null,null,f.Service);Assert(response.Status==200&&Text(Json.Read<object>(response.Body),"state")=="completed"&&f.Native.Archives==1,"Signed receipt missing");
            response=PortalAdministrationHttp.Handle(f.Store,admin.Request(collection+"/"+id+"/resume","POST","{}"),null,null,f.Service);Assert(response.Status==202&&f.Native.Copies==1,"Resume duplicated history");
        });
        check("conversation HTTP rejects ordinary bearer, tampered body and duplicate fields",()=>{
            var admin=new PortalAdministrationTests.Fixture(root);var f=new Fixture(root,admin.Store);var request=admin.Request("conversation-transfers","POST","{}");request.Headers["Authorization"]="Bearer ordinary-chat-token";Assert(PortalAdministrationHttp.Handle(f.Store,request,null,null,f.Service).Status==403,"Bearer promoted to admin");
            request=admin.Request("conversation-transfers","POST","{}");request.Body="{\"kind\":\"copy_thread\"}";Assert(PortalAdministrationHttp.Handle(f.Store,request,null,null,f.Service).Status==403,"Tampered body accepted");
            request=admin.Request("conversation-transfers","POST","{\"kind\":\"copy_thread\",\"kind\":\"transfer_thread\"}");Assert(PortalAdministrationHttp.Handle(f.Store,request,null,null,f.Service).Status==400&&f.Tasks.Count==0,"Duplicate action accepted");
            foreach(string suffix in new[]{"conversation-transfers","conversation-transfers/list","conversation-transfers/"+new string('c',32),"conversation-transfers/"+new string('c',32)+"/resume"})Assert(PortalAdministration.Path("/v1/admin/"+admin.Route.Id+"/"+suffix).Groups[1].Value==admin.Route.Id,"Route pin lost");
        });
        check("conversation signed administrator revocation blocks queued native mutation",()=>{
            var admin=new PortalAdministrationTests.Fixture(root);var f=new Fixture(root,admin.Store);
            var request=admin.Request("conversation-transfers","POST",Json.Write(new{kind="transfer_thread",source=f.Source,destination=f.Destination,threadId=f.Thread,title="Revocation fixture",explicitRequest=true}));
            var response=PortalAdministrationHttp.Handle(f.Store,request,null,null,f.Service);Assert(response.Status==202,"Admission rejected");string id=Text(Json.Read<object>(response.Body),"id");
            admin.Policy.Enabled=false;admin.Policy=PortalAdministration.Save(f.Store,admin.Policy);f.Drain();Assert(f.Native.Copies==0&&f.Native.Archives==0,"Revoked administrator still copied");
            response=PortalAdministrationHttp.Handle(f.Store,admin.Request("conversation-transfers/"+id,"GET",""),null,null,f.Service);Assert(response.Status==403,"Revoked admin read receipt");
        });
    }
}
