using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class ConversationInstancePin
    {
        public string Id {get;set;} public string Account {get;set;} public string Home {get;set;}
        public long GrantRevision {get;set;}
    }
    internal sealed class ConversationTransferOperation
    {
        public string Id {get;set;} public string Principal {get;set;} public string Fingerprint {get;set;}
        public TransferRequest Request {get;set;} public ConversationInstancePin Source {get;set;} public ConversationInstancePin Destination {get;set;}
        public string State {get;set;} public string Error {get;set;} public string UpdatedUtc {get;set;}
        public int OwnerPid {get;set;} public long OwnerStarted {get;set;}
    }
    internal sealed class ConversationTransferDependencies
    {
        internal Func<string,string,ConversationInstancePin> Resolve=null;
        internal Action<string,string> AuthorizeAdmin=null;
        internal Action<RelaySession> AuthorizeSession=null;
        internal Func<string,string,string,CancellationToken,Task<CopyThreadPage>> List=null;
        internal ITransferBackend Backend=null;
        internal Action<Func<Task>> Schedule=null;
        internal Func<string,Stream> OpenRecord=null;
    }
    // No prompts, delegation, window launch or automatic retry. The existing
    // Transfers journal owns copy verification and archive reconciliation.
    internal sealed class ConversationTransfers
    {
        private readonly RelayStore store;
        private readonly ConversationTransferDependencies dependencies;
        internal ConversationTransfers(RelayStore data,ConversationTransferDependencies adapters=null){store=data;dependencies=adapters??new ConversationTransferDependencies();}
        private static string Record(string id){RelayStore.MessageId(id);return "conversation-transfer-op-"+id+".dpapi";}
        private ConversationTransferOperation Operation(string id,bool required)
        {return RelayStore.ReadFile<ConversationTransferOperation>(Path.Combine(store.Root,Record(id)),true,required,dependencies.OpenRecord);}
        private static bool Same(ConversationInstancePin a,ConversationInstancePin b)
        {return a!=null&&b!=null&&a.Id==b.Id&&a.Account==b.Account&&RelayStore.SamePath(a.Home,b.Home)&&a.GrantRevision==b.GrantRevision;}
        private ConversationInstancePin Resolve(string route,string instance)
        {
            if(dependencies.Resolve!=null)return dependencies.Resolve(route,instance);
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var owner=NamedInstances.Resolve(accounts,instance);NamedInstances.VerifyAccount(accounts,owner);
            var pin=new ConversationInstancePin{Id=owner.Id,Account=owner.AccountKey,Home=accounts.Instances.Home(owner)};
            if(route!=null){
                var portal=Portal.Config(store,route);var grant=portal.Instances.SingleOrDefault(g=>g.InstanceId==owner.Id);
                if(!portal.Enabled||portal.RootRouteId!=route||!portal.IncludeExisting||grant==null||grant.AccountKey!=pin.Account||!RelayStore.SamePath(grant.Home,pin.Home))throw new WebhookException(403,"Cette instance ou ses discussions existantes ne sont pas accessibles dans ce portail.");
                pin.GrantRevision=grant.AuthorizationRevision>0?grant.AuthorizationRevision:portal.Revision;
            }
            return pin;
        }
        private string Principal(string route,string actor,RelaySession session)
        {
            if(session!=null){
                if(dependencies.AuthorizeSession!=null)dependencies.AuthorizeSession(session);else RelaySessions.Resolve(store,session.Token,session.Home);
                return "chat:"+ToolTunnel.Hash(new{session.Home,session.Account,session.Thread});
            }
            if(dependencies.AuthorizeAdmin!=null)dependencies.AuthorizeAdmin(route,actor);else PortalAdministration.RequireActor(store,route,actor);
            return "admin:"+ToolTunnel.Hash(new{route,actor});
        }
        private Action Verify(string route,string actor,RelaySession session,ConversationTransferOperation operation)
        {
            return ()=>{
                if(Principal(route,actor,session)!=operation.Principal||!Same(operation.Source,Resolve(route,operation.Source.Id))||!Same(operation.Destination,Resolve(route,operation.Destination.Id)))throw new WebhookException(409,"Le compte ou l'autorisation de cette opération a changé. Aucun transfert poursuivi.");
            };
        }
        internal async Task<object> List(string route,string actor,RelaySession session,string instance,string query,string cursor,CancellationToken token)
        {
            Principal(route,actor,session);var pin=Resolve(route,instance);
            if(String.IsNullOrEmpty(cursor))cursor=null;
            if(String.IsNullOrWhiteSpace(query))query=null;
            if((query??"").Length>200||(cursor??"").Length>4096)throw new WebhookException(400,"Recherche ou curseur trop long.");
            var page=dependencies.List!=null?await dependencies.List(pin.Id,query,cursor,token).ConfigureAwait(false):await new ThreadCopies(store).List(pin.Id,query,cursor,token).ConfigureAwait(false);
            Principal(route,actor,session);if(!Same(pin,Resolve(route,pin.Id)))throw new WebhookException(409,"Le compte ou l'autorisation a changé pendant la lecture.");
            return new{instanceId=pin.Id,conversations=page.Threads.Select(t=>new{id=t.Id,title=t.Title,folder=t.Folder,status=t.Status}).ToArray(),cursor=String.IsNullOrEmpty(page.Cursor)?null:page.Cursor};
        }
        internal object Start(string route,string actor,RelaySession session,string key,string kind,string source,string destination,string thread,string title,bool explicitRequest)
        {
            string principal=Principal(route,actor,session);
            if(!explicitRequest)throw new WebhookException(403,"La copie ou le transfert doit être demandé par l'utilisateur.");
            if(String.IsNullOrWhiteSpace(key)||key.Length>128||key.Any(c=>c<33||c>126))throw new WebhookException(400,"Une clé d'idempotence stable est obligatoire.");
            Guid parsed;if((kind!="copy_thread"&&kind!="transfer_thread")||!Guid.TryParse(thread,out parsed)||String.IsNullOrWhiteSpace(title)||title.Length>160||title.Any(Char.IsControl))throw new WebhookException(400,"Action, discussion ou titre invalide.");
            var from=Resolve(route,source);var to=Resolve(route,destination);
            if(from.Id==to.Id||RelayStore.SamePath(from.Home,to.Home))throw new WebhookException(400,"Choisissez deux profils distincts.");
            string id=ToolTunnel.Hash(new{principal,key}).Substring(0,32);
            var request=new TransferRequest{Id=id,Kind=kind,Source=from.Id,Destination=to.Id,Item=thread,Title=title};
            string fingerprint=ToolTunnel.Hash(request);ConversationTransferOperation operation;
            // A retry is a read of the admitted operation, even while its worker
            // or another administrative mutation owns update admission.
            operation=Operation(id,false);
            if(operation.Id!=null){if(operation.Principal!=principal||operation.Fingerprint!=fingerprint||!Same(operation.Source,from)||!Same(operation.Destination,to))throw new WebhookException(409,"Cette clé correspond à une autre sélection ou à un autre compte.");return Read(route,actor,session,id);}
            using(ApplicationUpdates.AdmitPortalMutation(store))using(store.Lease("conversation-transfer-admit-"+id)){
                operation=Operation(id,false);
                if(operation.Id!=null){if(operation.Principal!=principal||operation.Fingerprint!=fingerprint||!Same(operation.Source,from)||!Same(operation.Destination,to))throw new WebhookException(409,"Cette clé correspond à une autre sélection ou à un autre compte.");return Read(route,actor,session,id);}
                using(var process=System.Diagnostics.Process.GetCurrentProcess())operation=new ConversationTransferOperation{Id=id,Principal=principal,Fingerprint=fingerprint,Request=request,Source=from,Destination=to,State="queued",OwnerPid=process.Id,OwnerStarted=process.StartTime.ToUniversalTime().Ticks};
                Save(operation);
            }
            Schedule(route,actor,session,operation);return Read(route,actor,session,id);
        }
        private void Save(ConversationTransferOperation operation){operation.UpdatedUtc=DateTime.UtcNow.ToString("o");store.WriteRecord(Record(operation.Id),operation);}
        internal object Read(string route,string actor,RelaySession session,string id)
        {
            var operation=Load(route,actor,session,id);var transfer=RelayStore.ReadFile<TransferRecord>(Path.Combine(store.Root,"transfer-"+id+".dpapi"),true,false);
            string state=operation.State;if((state=="queued"||state=="running")&&!DesktopRuntime.SameProcess(operation.OwnerPid,operation.OwnerStarted))state="interrupted";
            return new{id=operation.Id,state=state,kind=operation.Request.Kind,sourceInstanceId=operation.Source.Id,destinationInstanceId=operation.Destination.Id,sourceThreadId=operation.Request.Item,title=operation.Request.Title,updatedUtc=operation.UpdatedUtc,error=operation.Error,progress=transfer.Message,targetThreadId=transfer.Items==null?null:transfer.Items.Select(i=>i.Target).FirstOrDefault(),sourceArchived=transfer.Items!=null&&transfer.Items.Length==1&&transfer.Items[0].Archived,canResume=state=="blocked"||state=="interrupted",requiresIdleSource=true};
        }
        private ConversationTransferOperation Load(string route,string actor,RelaySession session,string id)
        {
            string principal=Principal(route,actor,session);ConversationTransferOperation operation;
            try{operation=Operation(id,true);}catch(FileNotFoundException){throw new WebhookException(404,"Opération introuvable.");}catch(DirectoryNotFoundException){throw new WebhookException(404,"Opération introuvable.");}
            if(operation.Id!=id||operation.Principal!=principal)throw new WebhookException(404,"Opération introuvable pour cette conversation ou cet administrateur.");
            Verify(route,actor,session,operation)();return operation;
        }
        internal object Resume(string route,string actor,RelaySession session,string id)
        {
            var operation=Load(route,actor,session,id);
            if(operation.State=="completed")return Read(route,actor,session,id);
            Schedule(route,actor,session,operation);return Read(route,actor,session,id);
        }
        private void Schedule(string route,string actor,RelaySession session,ConversationTransferOperation operation)
        {
            Func<Task> execute=async ()=>{
                // Serialize across MCP processes and the HTTP broker, without
                // making status reads wait for the native copy/converter.
                IDisposable lease;try{lease=store.Lease("conversation-transfer-run-"+operation.Id);}catch(IOException){return;}
                using(lease){
                    operation=Operation(operation.Id,true);if(operation.State=="completed")return;
                    try{
                        using(var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5))){
                            var verify=Verify(route,actor,session,operation);
                            using(ApplicationUpdates.AdmitPortalMutation(store)){
                                verify();
                                using(var process=System.Diagnostics.Process.GetCurrentProcess()){operation.OwnerPid=process.Id;operation.OwnerStarted=process.StartTime.ToUniversalTime().Ticks;}
                                operation.State="running";operation.Error=null;Save(operation);
                            }
                            // The durable, live-PID activity record now prevents an
                            // update; release admission so status and unrelated
                            // administration are available during a long import.
                            var transfers=new Transfers(store,new GuardedBackend(dependencies.Backend??new TransferBackend(store),verify));
                            await transfers.Prepare(operation.Request,timeout.Token).ConfigureAwait(false);
                            var result=await transfers.Run(operation.Id,timeout.Token).ConfigureAwait(false);verify();
                            operation.State=result.State=="completed"?"completed":"blocked";Save(operation);
                        }
                    }catch(Exception error){operation.State="blocked";operation.Error=Program.SafeError(error);Save(operation);}
                }
            };
            if(dependencies.Schedule!=null)dependencies.Schedule(execute);else Task.Run(execute);
        }
        private sealed class GuardedBackend:ITransferBackend
        {
            private readonly ITransferBackend backend;private readonly Action verify;
            internal GuardedBackend(ITransferBackend value,Action guard){backend=value;verify=guard;}
            public string Identity(string instance){verify();return backend.Identity(instance);}
            public Task<TransferProject[]> Projects(string instance,CancellationToken token){throw new InvalidOperationException("Seules les conversations individuelles sont prises en charge.");}
            public async Task<string> Fingerprint(string instance,string thread,bool idle,CancellationToken token){verify();var result=await backend.Fingerprint(instance,thread,idle,token).ConfigureAwait(false);verify();return result;}
            public async Task<ThreadCopyRecord> Copy(TransferRecord record,TransferItem item,CancellationToken token){verify();var result=await backend.Copy(record,item,token).ConfigureAwait(false);verify();return result;}
            public Task<string> Project(TransferRecord record,CancellationToken token){throw new InvalidOperationException("Aucun projet à créer.");}
            public Task Assign(TransferRecord record,TransferItem item,CancellationToken token){throw new InvalidOperationException("Aucun projet à modifier.");}
            public async Task Archive(string instance,string thread,CancellationToken token){verify();await backend.Archive(instance,thread,token).ConfigureAwait(false);verify();}
            public async Task<bool> IsArchived(string instance,string thread,CancellationToken token){verify();var result=await backend.IsArchived(instance,thread,token).ConfigureAwait(false);verify();return result;}
            public Task Open(TransferRecord record,TransferItem item,CancellationToken token){throw new InvalidOperationException("Aucune fenêtre n'est ouverte par cette opération.");}
        }
    }
}
