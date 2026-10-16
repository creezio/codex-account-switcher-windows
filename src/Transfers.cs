using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class TransferProject
    {public string Id {get;set;} public string Name {get;set;} public string[] Roots {get;set;} public CopyThreadChoice[] Threads {get;set;} public string InventoryWarning {get;set;}}
    public sealed class TransferRequest
    {
        public string Id {get;set;} public string Kind {get;set;} public string Source {get;set;} public string Destination {get;set;}
        public string Item {get;set;} public string Title {get;set;} public string Folder {get;set;}
    }
    public sealed class TransferItem
    {
        public string Source {get;set;} public string Title {get;set;} public string Digest {get;set;}
        public string CopyId {get;set;} public string Target {get;set;} public string TargetDigest {get;set;}
        public bool Assigned {get;set;} public bool Archived {get;set;}
    }
    public sealed class TransferRecord
    {
        public TransferRequest Request {get;set;} public string SourceAccount {get;set;} public string DestinationAccount {get;set;}
        public string State {get;set;} public string Message {get;set;} public string Created {get;set;}
        public string[] Roots {get;set;} public string Project {get;set;} public TransferItem[] Items {get;set;}
        public ProjectFileManifest Files {get;set;}
    }
    internal interface ITransferBackend
    {
        string Identity(string instance);
        Task<TransferProject[]> Projects(string instance,CancellationToken token);
        Task<string> Fingerprint(string instance,string thread,bool requireIdle,CancellationToken token);
        Task<ThreadCopyRecord> Copy(TransferRecord record,TransferItem item,CancellationToken token);
        Task<string> Project(TransferRecord record,CancellationToken token);
        Task Assign(TransferRecord record,TransferItem item,CancellationToken token);
        Task Archive(string instance,string thread,CancellationToken token);
        Task<bool> IsArchived(string instance,string thread,CancellationToken token);
        Task Open(TransferRecord record,TransferItem item,CancellationToken token);
    }
    internal sealed class Transfers
    {
        private readonly RelayStore store;private readonly ITransferBackend backend;
        internal Transfers(RelayStore store,ITransferBackend backend=null){this.store=store;this.backend=backend??new TransferBackend(store);}
        internal static bool ProjectKind(string kind){return new[]{"link_project","copy_project","transfer_project"}.Contains(kind);}
        internal static bool Archives(string kind){return kind=="transfer_thread"||kind=="transfer_project";}
        internal Task<TransferProject[]> Projects(string instance,CancellationToken token){return backend.Projects(instance,token);}
        internal TransferRecord[] Recent(string instance){return Directory.GetFiles(store.Root,"transfer-*.dpapi").Select(p=>store.ReadRecord<TransferRecord>(Path.GetFileName(p))).Where(r=>r.Request!=null&&r.Request.Source==instance).OrderByDescending(r=>r.Created).ToArray();}
        private void Save(TransferRecord r){store.WriteRecord("transfer-"+r.Request.Id+".dpapi",r);}
        private void Identity(TransferRecord r){if(backend.Identity(r.Request.Source)!=r.SourceAccount||backend.Identity(r.Request.Destination)!=r.DestinationAccount)throw new InvalidOperationException("Un compte associé a changé. L’opération reste suspendue.");}
        internal async Task<TransferRecord> Prepare(TransferRequest request,CancellationToken token)
        {
            RelayStore.MessageId(request.Id);if(request.Source==request.Destination)throw new InvalidOperationException("Choisissez deux instances différentes.");
            if(!ProjectKind(request.Kind)&&request.Kind!="transfer_thread")throw new InvalidOperationException("Action de transfert inconnue.");
            if(String.IsNullOrWhiteSpace(request.Title)||request.Title.Length>160)throw new InvalidOperationException("Saisissez un nom de 1 à 160 caractères.");
            using(store.Lease("transfer-"+request.Id)){
                var old=store.ReadRecord<TransferRecord>("transfer-"+request.Id+".dpapi");
                if(old.Request!=null){if(Json.Write(old.Request)!=Json.Write(request))throw new InvalidOperationException("Cet identifiant appartient à une autre opération.");Identity(old);return old;}
                var r=new TransferRecord{Request=Json.Read<TransferRequest>(Json.Write(request)),SourceAccount=backend.Identity(request.Source),DestinationAccount=backend.Identity(request.Destination),Created=DateTime.UtcNow.ToString("o"),State="ready"};
                CopyThreadChoice[] selected;
                if(ProjectKind(request.Kind)){
                    var p=(await backend.Projects(request.Source,token).ConfigureAwait(false)).SingleOrDefault(x=>x.Id==request.Item);
                    if(p==null)throw new InvalidOperationException("Projet local introuvable dans l’instance source.");
                    if(request.Kind!="link_project"&&!String.IsNullOrEmpty(p.InventoryWarning))throw new InvalidOperationException(p.InventoryWarning);
                    r.Roots=p.Roots;selected=request.Kind=="link_project"?new CopyThreadChoice[0]:p.Threads;
                    if(!String.IsNullOrWhiteSpace(request.Folder)){
                        if(request.Kind=="link_project"||p.Roots.Length!=1)throw new InvalidOperationException("La copie indépendante exige un projet à un seul dossier.");
                        r.Files=ProjectFiles.Plan(p.Roots[0],request.Folder,token);r.Roots=new[]{r.Files.Destination};
                    }
                }else {if(!String.IsNullOrEmpty(request.Folder))throw new InvalidOperationException("Le transfert d’un chat conserve son dossier.");selected=new[]{new CopyThreadChoice{Id=request.Item,Title=request.Title}};r.Roots=new string[0];}
                r.Items=selected.Select(t=>new TransferItem{Source=t.Id,Title=t.Title,CopyId=Guid.NewGuid().ToString("N")}).ToArray();
                foreach(var item in r.Items)item.Digest=await backend.Fingerprint(request.Source,item.Source,true,token).ConfigureAwait(false);
                r.Message=r.Items.Length+" conversation(s) active(s) sélectionnée(s). "+(r.Files==null?"Aucun fichier dupliqué.":r.Files.Files.Length+" fichier(s), "+r.Files.Bytes.ToString("N0")+" octets à copier.");Save(r);return r;
            }
        }
        internal async Task<TransferRecord> Run(string id,CancellationToken token,IProgress<string> progress=null)
        {
            RelayStore.MessageId(id);using(store.Lease("transfer-"+id)){
                var r=store.ReadRecord<TransferRecord>("transfer-"+id+".dpapi");if(r.Request==null)throw new InvalidOperationException("Préparez l’opération avant de la lancer.");Identity(r);if(r.State=="completed")return r;
                try{
                    if(r.Files!=null&&!r.Files.Completed){Report(r,"files","Copie et vérification des fichiers…",progress);await Task.Run(()=>ProjectFiles.Copy(r.Files,()=>Save(r),token),token).ConfigureAwait(false);}
                    if(ProjectKind(r.Request.Kind)){
                        Report(r,"project","Vérification du projet dans Codex…",progress);r.Project=await backend.Project(r,token).ConfigureAwait(false);
                        if(r.Project==null){Report(r,"awaiting_codex","Projet enregistré. Codex doit actualiser sa liste. Cliquez sur Actualiser Codex et reprendre : seule l’instance destinataire sera fermée puis rouverte après vérification de ses chats. Les sources sont conservées.",progress);return r;}Save(r);
                    }
                    foreach(var item in r.Items){
                        token.ThrowIfCancellationRequested();Identity(r);
                        if(item.Target==null){
                            if(await backend.Fingerprint(r.Request.Source,item.Source,true,token).ConfigureAwait(false)!=item.Digest)throw new InvalidOperationException("Une conversation source a changé depuis la préparation. Créez une nouvelle opération pour inclure ses nouveaux messages.");
                            Report(r,"copying","Copie et vérification "+(Array.IndexOf(r.Items,item)+1)+" / "+r.Items.Length+" · "+item.Title,progress);
                            var copy=await backend.Copy(r,item,token).ConfigureAwait(false);if(copy.State!="completed")throw new InvalidOperationException(copy.Message??"La copie reste à vérifier.");
                            item.Target=copy.TargetThread;item.TargetDigest=await backend.Fingerprint(r.Request.Destination,item.Target,false,token).ConfigureAwait(false);Save(r);
                        }
                        if(ProjectKind(r.Request.Kind)&&!item.Assigned){await backend.Assign(r,item,token).ConfigureAwait(false);item.Assigned=true;Save(r);}
                    }
                    if(Archives(r.Request.Kind)){
                        // Reconcile a lost archive response before comparing an archived journal.
                        foreach(var item in r.Items.Where(x=>!x.Archived))if(await backend.IsArchived(r.Request.Source,item.Source,token).ConfigureAwait(false)){item.Archived=true;Save(r);}
                        // No source is archived until the entire batch passes these checks.
                        foreach(var item in r.Items.Where(x=>!x.Archived))await CheckPair(r,item,token).ConfigureAwait(false);
                        foreach(var item in r.Items.Where(x=>!x.Archived)){
                            Identity(r);await CheckPair(r,item,token).ConfigureAwait(false);Report(r,"archiving","Archivage de la source · "+item.Title,progress);
                            await backend.Archive(r.Request.Source,item.Source,token).ConfigureAwait(false);
                            if(!await backend.IsArchived(r.Request.Source,item.Source,token).ConfigureAwait(false))throw new InvalidOperationException("Archivage non confirmé. Reprenez la vérification ; les copies ne seront pas recréées.");item.Archived=true;Save(r);
                        }
                    }
                    Report(r,"completed",Archives(r.Request.Kind)?"Transfert vérifié. Conversations originales archivées ; fichiers et projet source conservés.":r.Request.Kind=="link_project"?"Projet accessible dans Codex, avec les mêmes fichiers et sans historique importé.":"Projet et conversations copiés, vérifiés et associés dans Codex. Sources conservées.",progress);return r;
                }catch(Exception e){r.Message="Opération suspendue : "+Program.SafeError(e)+" Les étapes confirmées sont conservées ; Reprendre ne recrée pas les copies.";Save(r);throw;}
            }
        }
        private async Task CheckPair(TransferRecord r,TransferItem item,CancellationToken token)
        {
            if(await backend.Fingerprint(r.Request.Source,item.Source,true,token).ConfigureAwait(false)!=item.Digest)throw new InvalidOperationException("La source a changé ; son archivage est bloqué.");
            if(await backend.Fingerprint(r.Request.Destination,item.Target,true,token).ConfigureAwait(false)!=item.TargetDigest)throw new InvalidOperationException("La destination a changé ; l’archivage de la source est bloqué.");
            if(ProjectKind(r.Request.Kind))await backend.Assign(r,item,token).ConfigureAwait(false);
        }
        private void Report(TransferRecord r,string state,string message,IProgress<string> progress){r.State=state;r.Message=message;Save(r);if(progress!=null)progress.Report(message);}
        internal Task Open(TransferRecord r,TransferItem item,CancellationToken token){Identity(r);return backend.Open(r,item,token);}
    }
}
