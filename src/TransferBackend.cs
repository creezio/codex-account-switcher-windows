using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class TransferBackend:ITransferBackend
    {
        private readonly RelayStore store;
        internal TransferBackend(RelayStore store){this.store=store;}
        private AccountService Accounts(){return new AccountService(Path.GetDirectoryName(store.Root));}
        private static DesktopInstance Instance(AccountService a,string id){var i=a.Data.Instances.SingleOrDefault(x=>x.Id==id&&!x.Archived);if(i==null)throw new InvalidOperationException("Instance introuvable.");NamedInstances.VerifyAccount(a,i);return i;}
        public string Identity(string id){var a=Accounts();return Instance(a,id).AccountKey;}
        private async Task<object> Native(string instance,string method,object args,CancellationToken token,string thread=null)
        {var a=Accounts();var i=Instance(a,instance);var channel=await NamedInstances.Connect(store,a,i,Environment.CurrentDirectory,token,thread).ConfigureAwait(false);return await new DesktopRelayTransport().Call(channel,method,args,token).ConfigureAwait(false);}
        private static object State(string home)
        {string path=Path.Combine(home,".codex-global-state.json");SafeFiles.RejectLinks(path);if(!File.Exists(path))return new Dictionary<string,object>();using(var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)))return Json.Read<object>(reader.ReadToEnd());}
        private static bool Local(object project){return Json.Str(Json.Get(project,"projectKind"))=="local"&&new[]{"","local"}.Contains(Json.Str(Json.Get(project,"hostId")));}
        public async Task<TransferProject[]> Projects(string instance,CancellationToken token)
        {
            var native=await Native(instance,"list_projects",new{},token).ConfigureAwait(false);var a=Accounts();var owner=Instance(a,instance);string home=a.Instances.Home(owner);var state=State(home);
            var listed=await Native(instance,"list_threads",new{limit=50},token).ConfigureAwait(false);
            var visible=RelayEngine.Rows(Json.Get(listed,"threads")).Concat(RelayEngine.Rows(Json.Get(listed,"pinnedThreads"))).Where(t=>Json.Str(Json.Get(t,"kind"))=="codex"&&new[]{"","local"}.Contains(Json.Str(Json.Get(t,"hostId")))).GroupBy(t=>Json.Str(Json.Get(t,"id"))).ToDictionary(g=>g.Key,g=>Json.Str(Json.Get(g.First(),"projectId")));
            var result=new List<TransferProject>();var allThreads=new List<object>();var allProjects=new List<object>();
            using(var rpc=new ThreadProfileRpc(a.Settings.CodexExecutable,home)){
                string cursor=null;var seen=new HashSet<string>();
                do{var page=await rpc.Call("thread/list",new{limit=100,cursor=cursor,archived=false,useStateDbOnly=true,modelProviders=new string[0]},token).ConfigureAwait(false);allThreads.AddRange(RelayEngine.Rows(Json.Get(page,"data")));cursor=Json.Str(Json.Get(page,"nextCursor"));if(cursor.Length>0&&!seen.Add(cursor))throw new InvalidOperationException("Pagination des conversations incohérente.");}while(cursor.Length>0);
                cursor=null;seen.Clear();do{var page=await rpc.Call("project/list",new{limit=100,cursor=cursor},token).ConfigureAwait(false);allProjects.AddRange(RelayEngine.Rows(Json.Get(page,"data")));cursor=Json.Str(Json.Get(page,"nextCursor"));if(cursor.Length>0&&!seen.Add(cursor))throw new InvalidOperationException("Pagination des projets incohérente.");}while(cursor.Length>0);
            }
            foreach(var p in RelayEngine.Rows(Json.Get(native,"projects")).Where(Local)){
                string id=Json.Str(Json.Get(p,"projectId"));var legacy=Json.Get(Json.Get(state,"local-projects"),id);
                string backendId=Json.Str(Json.Get(Json.Get(Json.Get(state,"app-server-project-id-by-legacy-project-id-by-host"),"local:"+home),id));if(backendId.Length==0)backendId=id;
                var server=allProjects.SingleOrDefault(x=>Json.Str(Json.Get(x,"id"))==backendId);
                string[] roots=RelayEngine.Rows(Json.Get(legacy,"rootPaths")).Select(Json.Str).ToArray();if(roots.Length==0)roots=RelayEngine.Rows(Json.Get(server,"roots")).Select(x=>Json.Str(Json.Get(x,"path"))).ToArray();
                if(roots.Length==0)throw new InvalidOperationException("Les dossiers du projet « "+Json.Str(Json.Get(p,"label"))+" » ne peuvent pas être vérifiés.");
                foreach(string root in roots)RelayStore.WorkspacePath(root);
                var threads=allThreads.Where(t=>visible.ContainsKey(Json.Str(Json.Get(t,"id")))?visible[Json.Str(Json.Get(t,"id"))]==id:Membership(state,t,id,backendId)).Select(t=>new CopyThreadChoice{Id=Json.Str(Json.Get(t,"id")),Title=Json.Str(Json.Get(t,"name")),Folder=RolloutSnapshot.NativePath(Json.Str(Json.Get(t,"cwd")))}).GroupBy(t=>t.Id).Select(g=>g.First()).ToArray();
                foreach(var t in threads)if(String.IsNullOrWhiteSpace(t.Title))t.Title="Conversation "+t.Id;
                bool unknown=allThreads.Any(t=>!visible.ContainsKey(Json.Str(Json.Get(t,"id")))&&Json.Get(Json.Get(state,"thread-project-assignments"),Json.Str(Json.Get(t,"id")))==null&&String.IsNullOrEmpty(Json.Str(Json.Get(t,"projectId")))&&!RelayEngine.Rows(Json.Get(state,"projectless-thread-ids")).Any(x=>Json.Str(x)==Json.Str(Json.Get(t,"id"))));
                result.Add(new TransferProject{Id=id,Name=Json.Str(Json.Get(p,"label")),Roots=roots,Threads=threads,InventoryWarning=unknown?"Codex ne fournit pas l’appartenance de certains anciens chats de ce profil. Le transfert global est bloqué pour éviter un projet incomplet ; utilisez les actions conversation par conversation ou ouvrez les anciens chats dans Codex avant de réessayer.":null});
            }
            return result.ToArray();
        }
        internal static bool Membership(object state,object thread,string legacyId,string backendId)
        {
            string id=Json.Str(Json.Get(thread,"id"));var assignment=Json.Get(Json.Get(state,"thread-project-assignments"),id);
            // Explicit frontend membership wins over stale backend metadata.
            if(assignment!=null)return Json.Str(Json.Get(assignment,"projectKind"))=="local"&&Json.Str(Json.Get(assignment,"projectId"))==legacyId&&new[]{"","local"}.Contains(Json.Str(Json.Get(Json.Get(state,"thread-project-membership-host-ids"),id)));
            if(RelayEngine.Rows(Json.Get(state,"projectless-thread-ids")).Any(x=>Json.Str(x)==id))return false;
            return Json.Str(Json.Get(thread,"projectId"))==backendId;
        }
        public async Task<string> Fingerprint(string instance,string thread,bool requireIdle,CancellationToken token)
        {
            var a=Accounts();var owner=Instance(a,instance);string home=a.Instances.Home(owner);Guid guid;if(!Guid.TryParse(thread,out guid))throw new InvalidOperationException("Identifiant de conversation invalide.");
            if(requireIdle){var live=await Native(instance,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token,thread).ConfigureAwait(false);DirectMessages.RequireThread(live,thread);if(DirectMessages.Busy(live))throw new InvalidOperationException("La conversation travaille ou attend une réponse. Terminez son tour avant de transférer.");}
            using(var rpc=new ThreadProfileRpc(a.Settings.CodexExecutable,home)){
                var result=await rpc.Call("thread/read",new{threadId=thread,includeTurns=false},token).ConfigureAwait(false);var t=Json.Get(result,"thread");if(Json.Str(Json.Get(t,"id"))!=thread)throw new InvalidOperationException("Conversation non retrouvée.");
                string path=RolloutSnapshot.NativePath(Json.Str(Json.Get(t,"path")));RolloutSnapshot.RequireRollout(home,path);var snapshot=RolloutSnapshot.Scan(path,thread,null,token);
                // Desktop migrates display events from JSONL to SQLite when opened.
                // Compare model context plus the native visible transcript, not that
                // storage rewrite. Both are re-read before an original is archived.
                string[] visible=await ThreadCopies.ReadVisible(rpc,thread,token).ConfigureAwait(false);var after=RolloutSnapshot.Scan(path,thread,null,token);
                if(snapshot.ModelDigest!=after.ModelDigest||snapshot.ModelItems!=after.ModelItems)throw new InvalidOperationException("La conversation a changé pendant sa vérification.");
                return "v1:"+snapshot.ModelDigest+":"+snapshot.ModelItems+":"+RolloutSnapshot.TextDigest("visible",String.Join("\n",visible));
            }
        }
        public Task<ThreadCopyRecord> Copy(TransferRecord r,TransferItem item,CancellationToken token)
        {
            string folder=null;if(r.Files!=null){
                var a=Accounts();var home=a.Instances.Home(Instance(a,r.Request.Source));
                // The source cwd is checked during Prepare through Projects; map it inside the copied root.
                return CopyMapped(r,item,home,token);
            }
            return new ThreadCopies(store).CopyTo(item.CopyId,r.Request.Source,r.Request.Destination,item.Source,item.Title.Length>160?item.Title.Substring(0,160):item.Title,folder,token);
        }
        private async Task<ThreadCopyRecord> CopyMapped(TransferRecord r,TransferItem item,string home,CancellationToken token)
        {
            var a=Accounts();string folder;
            using(var rpc=new ThreadProfileRpc(a.Settings.CodexExecutable,home)){var data=await rpc.Call("thread/read",new{threadId=item.Source,includeTurns=false},token).ConfigureAwait(false);string cwd=RolloutSnapshot.NativePath(Json.Str(Json.Get(Json.Get(data,"thread"),"cwd")));folder=ProjectFiles.Map(r.Files,cwd);}
            return await new ThreadCopies(store).CopyTo(item.CopyId,r.Request.Source,r.Request.Destination,item.Source,item.Title.Length>160?item.Title.Substring(0,160):item.Title,folder,token).ConfigureAwait(false);
        }
        public async Task<string> Project(TransferRecord r,CancellationToken token)
        {
            var a=Accounts();var target=Instance(a,r.Request.Destination);string serverId;object project;
            using(var rpc=new ThreadProfileRpc(a.Settings.CodexExecutable,a.Instances.Home(target))){
                var created=await rpc.Call("project/import",new{idempotencyKey="account-switcher-"+r.Request.Id,name=r.Request.Title,roots=r.Roots.Select(p=>new{path=p}).ToArray(),metadata=new Dictionary<string,string>{{"account-switcher-operation",r.Request.Id}}},token).ConfigureAwait(false);serverId=Json.Str(Json.Get(Json.Get(created,"project"),"id"));
                project=Json.Get(created,"project");if(String.IsNullOrEmpty(serverId))throw new InvalidOperationException("Codex n’a pas confirmé le projet destinataire.");
            }
            var runtime=a.Instances.Runtime.Probe(target);
            if(!runtime.Running&&runtime.Phase=="stopped"){
                using(var lease=new InstanceLease(target.IsLocal?a.Instances.Home(target):InstancePaths.Folder(a.Vault.Root,target.Id)))OfflineProjects.Register(a.Instances.Home(target),project,()=>{var current=a.Instances.Runtime.Probe(target);return !current.Running&&current.Phase=="stopped";});
                return null;
            }
            var listed=await Native(r.Request.Destination,"list_projects",new{},token).ConfigureAwait(false);var state=State(a.Instances.Home(target));
            var mapping=Json.Get(Json.Get(state,"app-server-project-id-by-legacy-project-id-by-host"),"local:"+a.Instances.Home(target));
            var visible=RelayEngine.Rows(Json.Get(listed,"projects")).Where(Local).SingleOrDefault(p=>Json.Str(Json.Get(p,"projectId"))==serverId||Json.Str(Json.Get(mapping,Json.Str(Json.Get(p,"projectId"))))==serverId);
            return visible==null?null:Json.Str(Json.Get(visible,"projectId"));
        }
        public async Task Assign(TransferRecord r,TransferItem item,CancellationToken token)
        {
            // The native tool updates the sidebar's membership as well as the backend.
            if(!item.Assigned)await Native(r.Request.Destination,"navigate_to_codex_page",new{threadId=item.Target},token,item.Target).ConfigureAwait(false);
            for(int attempt=0;;attempt++){
                try{await Native(r.Request.Destination,"move_thread_to_project",new{threadId=item.Target,projectId=r.Project},token,item.Target).ConfigureAwait(false);break;}
                catch(InvalidOperationException e){if(attempt>=11||!e.Message.Contains("source project could not be safely determined"))throw;}
                await Task.Delay(500,token).ConfigureAwait(false);
            }
            // New Desktop versions need not persist legacy membership in the JSON
            // cache. Verify the actual sidebar, allowing its asynchronous refresh.
            for(int attempt=0;attempt<12;attempt++){
                var listed=await Native(r.Request.Destination,"list_threads",new{limit=50},token,item.Target).ConfigureAwait(false);
                if(RelayEngine.Rows(Json.Get(listed,"threads")).Concat(RelayEngine.Rows(Json.Get(listed,"pinnedThreads"))).Any(t=>Json.Str(Json.Get(t,"id"))==item.Target&&Json.Str(Json.Get(t,"projectId"))==r.Project))return;
                await Task.Delay(250,token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Codex n’a pas encore confirmé l’association au projet. Reprenez la vérification.");
        }
        public async Task Archive(string instance,string thread,CancellationToken token)
        {await Native(instance,"set_thread_archived",new{threadId=thread,archived=true},token,thread).ConfigureAwait(false);}
        public async Task<bool> IsArchived(string instance,string thread,CancellationToken token)
        {
            var a=Accounts();var owner=Instance(a,instance);using(var rpc=new ThreadProfileRpc(a.Settings.CodexExecutable,a.Instances.Home(owner))){
                string cursor=null;var seen=new HashSet<string>();do{var page=await rpc.Call("thread/list",new{archived=true,limit=100,cursor=cursor,useStateDbOnly=true,modelProviders=new string[0]},token).ConfigureAwait(false);if(RelayEngine.Rows(Json.Get(page,"data")).Any(t=>Json.Str(Json.Get(t,"id"))==thread))return true;cursor=Json.Str(Json.Get(page,"nextCursor"));if(cursor.Length>0&&!seen.Add(cursor))throw new InvalidOperationException("Pagination des archives incohérente.");}while(cursor.Length>0);return false;
            }
        }
        public async Task Open(TransferRecord r,TransferItem item,CancellationToken token)
        {await Native(r.Request.Destination,"navigate_to_codex_page",new{threadId=item.Target},token,item.Target).ConfigureAwait(false);}
        internal async Task RequireIdleInstance(string instance,CancellationToken token)
        {
            // Check every local chat, not only the 50 most recent sidebar entries.
            var a=Accounts();var owner=Instance(a,instance);var ids=new List<string>();using(var rpc=new ThreadProfileRpc(a.Settings.CodexExecutable,a.Instances.Home(owner))){string cursor=null;var seen=new HashSet<string>();do{var page=await rpc.Call("thread/list",new{limit=100,cursor=cursor,archived=false,useStateDbOnly=true,modelProviders=new string[0]},token).ConfigureAwait(false);ids.AddRange(RelayEngine.Rows(Json.Get(page,"data")).Select(t=>Json.Str(Json.Get(t,"id"))));cursor=Json.Str(Json.Get(page,"nextCursor"));if(cursor.Length>0&&!seen.Add(cursor))throw new InvalidOperationException("Liste de conversations non vérifiable.");}while(cursor.Length>0);}
            var legacy=new List<string>();
            foreach(string id in ids){try{var data=await Native(instance,"read_thread",new{threadId=id,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token,id).ConfigureAwait(false);DirectMessages.RequireThread(data,id);if(DirectMessages.Busy(data))throw new InvalidOperationException("Un chat de cette instance travaille ou attend une réponse. Terminez-le avant d’actualiser Codex.");}
                catch(InvalidOperationException e){if(!e.Message.Contains("thread/items/list is not supported yet"))throw;legacy.Add(id);}}
            var sidebar=await Native(instance,"list_threads",new{limit=50},token).ConfigureAwait(false);var rows=RelayEngine.Rows(Json.Get(sidebar,"threads")).Concat(RelayEngine.Rows(Json.Get(sidebar,"pinnedThreads"))).ToArray();
            if(legacy.Any(id=>!rows.Any(t=>Json.Str(Json.Get(t,"id"))==id&&IdleStatus(Json.Str(Json.Get(t,"status")))))||rows.Any(t=>!IdleStatus(Json.Str(Json.Get(t,"status")))))throw new InvalidOperationException("Au moins un chat n’est pas confirmé au repos. Fermez vous-même cette instance après avoir vérifié ses tâches.");
        }
        internal static bool IdleStatus(string status){return status=="idle"||status=="notLoaded";}
    }
}
