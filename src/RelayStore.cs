using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace Creezio.Switcher
{
    public sealed class RelayChannel
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Home {get;set;}
        public string AccountKey {get;set;}
        public string Email {get;set;}
        public string PipePath {get;set;}
        public int ServerPid {get;set;}
        public long ServerStartTicks {get;set;}
        public string AnchorThreadId {get;set;}
        public string Workspace {get;set;}
        public bool Enabled {get;set;}
        public string ConnectedUtc {get;set;}
        public bool RequireFullAccess {get;set;}
        public string InstanceId {get;set;}
        public string CodexProjectId {get;set;}
        public string Diagnostic {get;set;}
    }
    public sealed class RelayMessage
    {
        public string Id {get;set;}
        public string SourceChannelId {get;set;}
        public string TargetChannelId {get;set;}
        public string SourceAccountKey {get;set;}
        public string TargetAccountKey {get;set;}
        public string SourceThreadId {get;set;}
        public string TargetThreadId {get;set;}
        public string TargetTurnId {get;set;}
        public bool NewConversation {get;set;}
        public string ReplyTo {get;set;}
        public string Title {get;set;}
        public string Prompt {get;set;}
        public string Workspace {get;set;}
        public string Revision {get;set;}
        public bool ReturnToSource {get;set;}
        public string State {get;set;}
        public string ReturnState {get;set;}
        public string Result {get;set;}
        public string Error {get;set;}
        public string CreatedUtc {get;set;}
        public string UpdatedUtc {get;set;}
        public string SourceHome {get;set;}
        public string TargetHome {get;set;}
        public int SchemaVersion {get;set;}
        public RelayJobSpec Job {get;set;}
        public string SubmissionHash {get;set;}
        public string RootJobId {get;set;}
        public int Depth {get;set;}
        public bool OriginVerified {get;set;}
        public string RoutingReason {get;set;}
        public string Outcome {get;set;}
        public string BlockReason {get;set;}
        public string DeadlineUtc {get;set;}
        public string LastPollUtc {get;set;}
        public string TargetWorkspace {get;set;}
        public string ResultPath {get;set;}
        public bool CancellationRequested {get;set;}
        public List<RelayEvent> Events {get;set;}
        public int ReconcileAttempts {get;set;}
        public string ReconcileAfterUtc {get;set;}
        public string DispatchPhase {get;set;}
        public string ExpectedPermission {get;set;}
        public string ObservedPermission {get;set;}
        public string PolicyRevision {get;set;}
        public string ReturnMode {get;set;}
        public int ReturnDelaySeconds {get;set;}
        public string CompletedUtc {get;set;}
        public bool AwaitingChildren {get;set;}
        public string Continuation {get;set;}
        public string ReturnBatchId {get;set;}
    }
    public sealed class RelayEvent
    {
        public string At {get;set;}
        public string State {get;set;}
        public string Detail {get;set;}
    }
    public sealed class RelayResult
    {
        public string JobId {get;set;}
        public string ThreadId {get;set;}
        public string Status {get;set;}
        public string Text {get;set;}
        public Dictionary<string,string> Files {get;set;}
        public string ReportedUtc {get;set;}
    }
    internal sealed partial class RelayStore
    {
        private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Creezio.Relay.v1");
        public readonly string Root;
        public static string DefaultRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher","relay");}}
        public RelayStore(string root)
        {
            Root=Path.GetFullPath(root);SafeFiles.PrivateDirectory(Root);
            SafeFiles.PrivateDirectory(Path.Combine(Root,"messages"));
            // v0.4 scans only messages/. Keep new contracts out of reach of its legacy pump.
            SafeFiles.PrivateDirectory(Path.Combine(Root,"jobs"));
            SafeFiles.PrivateDirectory(Path.Combine(Root,"jobs-v3"));
            Directory.CreateDirectory(Path.Combine(Root,"catalog-v1"));SafeFiles.RejectLinks(Path.Combine(Root,"catalog-v1"));
        }
        public static void ChannelId(string id)
        {if(id==null || !Regex.IsMatch(id,"^[a-z0-9][a-z0-9-]{0,47}$"))throw new InvalidOperationException("Le nom du canal doit contenir 1 à 48 lettres minuscules, chiffres ou tirets.");}
        public static void MessageId(string id)
        {if(!InstanceRules.ValidId(id))throw new InvalidOperationException("Identifiant de demande invalide.");}
        public static string WorkspacePath(string path)
        {
            if(String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))throw new InvalidOperationException("Choisissez un dossier local absolu pour le projet.");
            path=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if(path.StartsWith("\\\\",StringComparison.Ordinal) || path.Length<=3 || !Directory.Exists(path))throw new InvalidOperationException("Le dossier de projet local est introuvable ou trop large.");
            SafeFiles.RejectLinks(path);return path;
        }
        internal static bool SamePath(string a,string b){return String.Equals(Path.GetFullPath(a).TrimEnd('\\','/'),Path.GetFullPath(b).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);}
        internal FileStream Lease(string name)
        {
            if(!Regex.IsMatch(name??"","^[a-zA-Z0-9-]{1,100}$"))throw new InvalidOperationException("Nom de verrou invalide.");
            string path=Path.Combine(Root,name+".lock");SafeFiles.RejectLinks(path);
            return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        }
        private T Read<T>(string path) where T:new()
        {
            if(!File.Exists(path))return new T();SafeFiles.RejectLinks(path);
            byte[] encrypted;
            FileStream opened=null;
            for(int attempt=0;opened==null;attempt++)try{opened=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);}catch(IOException e){int code=e.HResult&65535;if(attempt>=5||!new[]{2,32,33,1175}.Contains(code))throw;System.Threading.Thread.Sleep(20*(attempt+1));}
            using(var stream=opened)using(var output=new MemoryStream()){
                if(stream.Length>4194304)throw new IOException("Relay record too large.");stream.CopyTo(output);encrypted=output.ToArray();
            }
            byte[] clear=ProtectedData.Unprotect(encrypted,Entropy,DataProtectionScope.CurrentUser);
            try{return Json.Read<T>(Encoding.UTF8.GetString(clear));}finally{Array.Clear(clear,0,clear.Length);}
        }
        internal T ReadRecord<T>(string name) where T:new(){if(Path.GetFileName(name)!=name)throw new InvalidOperationException("Nom de fichier invalide.");return Read<T>(Path.Combine(Root,name));}
        internal void WriteRecord(string name,object data){if(Path.GetFileName(name)!=name)throw new InvalidOperationException("Nom de fichier invalide.");Write(Path.Combine(Root,name),data);}
        private void Write(string path,object data)
        {
            byte[] clear=Encoding.UTF8.GetBytes(Json.Write(data));
            try{
                byte[] encrypted=ProtectedData.Protect(clear,Entropy,DataProtectionScope.CurrentUser);
                for(int attempt=0;;attempt++)try{SafeFiles.AtomicWrite(path,encrypted);break;}
                catch(IOException e){int code=e.HResult&65535;if(attempt>=4||!new[]{32,33,1175}.Contains(code)||File.Exists(path+".switcher-tmp"))throw;System.Threading.Thread.Sleep(25*(1<<attempt));}
            }finally{Array.Clear(clear,0,clear.Length);}
        }
        public List<RelayChannel> Channels(){return Read<List<RelayChannel>>(Path.Combine(Root,"channels.dpapi"));}
        public RelayChannel Channel(string id)
        {ChannelId(id);var channel=Channels().SingleOrDefault(c=>c.Id==id);if(channel==null)throw new InvalidOperationException("Canal introuvable. Connectez-le depuis une conversation Codex.");return channel;}
        public void Register(RelayChannel channel)
        {
            ChannelId(channel.Id);channel.Workspace=WorkspacePath(channel.Workspace);
            using(Lease("registry")) {
                var list=Channels();var existing=list.SingleOrDefault(c=>c.Id==channel.Id);
                if(existing!=null && (existing.AccountKey!=channel.AccountKey || !SamePath(existing.Home,channel.Home)))
                    throw new InvalidOperationException("Ce canal est réservé à un autre compte ou profil. Utilisez un nouveau nom de canal.");
                list.RemoveAll(c=>c.Id==channel.Id);list.Add(channel);Write(Path.Combine(Root,"channels.dpapi"),list);
            }
        }
        public void SetEnabled(string id,bool enabled)
        {
            using(Lease("registry")) {var list=Channels();var c=list.Single(x=>x.Id==id);c.Enabled=enabled;Write(Path.Combine(Root,"channels.dpapi"),list);}
        }
        public RelayMessage Message(string id)
        {MessageId(id);string path=Path.Combine(Root,"jobs-v3",id+".dpapi");if(!File.Exists(path))path=Path.Combine(Root,"jobs",id+".dpapi");if(!File.Exists(path))path=Path.Combine(Root,"messages",id+".dpapi");if(!File.Exists(path))throw new InvalidOperationException("Demande introuvable.");return Read<RelayMessage>(path);}
        public List<RelayMessage> Messages()
        {
            return Query(null,0,Int32.MaxValue);
        }
        public void Report(RelayMessage message,RelaySession session,string status,string text,Dictionary<string,string> files)
        {
            if(message.TargetChannelId!=session.Channel||message.TargetThreadId!=session.Thread||message.State!="waiting"||message.DispatchPhase=="preflight")throw new InvalidOperationException("Seule la conversation destinataire active, après réception du travail, peut préparer son résultat.");
            if(!new[]{"succeeded","failed","blocked","cancelled"}.Contains(status)||String.IsNullOrWhiteSpace(text)||text.Length>250000)throw new InvalidOperationException("Résultat invalide ou supérieur à 250 000 caractères.");
            RelayPolicies.VerifyFiles(message.TargetWorkspace??message.Workspace,files);
            using(Lease("report-"+message.Id))WriteRecord("result-"+message.Id+".dpapi",new RelayResult{JobId=message.Id,ThreadId=session.Thread,Status=status,Text=text,Files=files,ReportedUtc=DateTime.UtcNow.ToString("o")});
        }
        public RelayResult Reported(RelayMessage message){var r=ReadRecord<RelayResult>("result-"+message.Id+".dpapi");return r.JobId==message.Id&&r.ThreadId==message.TargetThreadId?r:null;}
        public void Save(RelayMessage message)
        {
            MessageId(message.Id);message.UpdatedUtc=DateTime.UtcNow.ToString("o");
            if(message.Events==null)message.Events=new List<RelayEvent>();
            string state=message.State+"/"+message.ReturnState+"/"+message.Outcome;
            string detail=message.Error??message.RoutingReason;
            var last=message.Events.LastOrDefault();
            if(last==null||last.State!=state||last.Detail!=detail){message.Events.Add(new RelayEvent{At=message.UpdatedUtc,State=state,Detail=detail});if(message.Events.Count>200)message.Events.RemoveAt(0);}
            using(CatalogLease()){
                var catalog=EnsureCatalog();
                WriteRecord("catalog-dirty.dpapi",new CatalogHeader{Generation=Guid.NewGuid().ToString("N")});
                Write(Path.Combine(Root,message.SchemaVersion>=3?"jobs-v3":message.SchemaVersion>=2?"jobs":"messages",message.Id+".dpapi"),message);
                UpdateCatalog(catalog,message);
                File.Delete(Path.Combine(Root,"catalog-dirty.dpapi"));
            }
        }
        public bool Exists(string id){MessageId(id);return File.Exists(Path.Combine(Root,"messages",id+".dpapi"))||File.Exists(Path.Combine(Root,"jobs",id+".dpapi"))||File.Exists(Path.Combine(Root,"jobs-v3",id+".dpapi"));}
        public RelayMessage Enqueue(string sourceId,string sourceThread,string targetId,string title,string prompt,string revision,bool returnToSource,string replyTo,string requestedId=null,Action<RelayMessage> prepare=null)
        {
            replyTo=String.IsNullOrWhiteSpace(replyTo)?null:replyTo;
            if(String.IsNullOrWhiteSpace(prompt) || prompt.Length>24000)throw new InvalidOperationException("Le message doit contenir entre 1 et 24 000 caractères.");
            if(String.IsNullOrWhiteSpace(sourceThread))throw new InvalidOperationException("Conversation source manquante.");
            if(String.IsNullOrWhiteSpace(title) || title.Length>120)throw new InvalidOperationException("Choisissez un titre de 1 à 120 caractères.");
            if((revision??"").Length>160)throw new InvalidOperationException("Référence de version trop longue.");
            var source=Channel(sourceId);var target=Channel(targetId);
            if(!source.Enabled || !target.Enabled)throw new InvalidOperationException("Un des canaux est désactivé.");
            if(source.Id==target.Id)throw new InvalidOperationException("Choisissez un autre canal destinataire.");
            if(prepare==null&&!SamePath(source.Workspace,target.Workspace))throw new InvalidOperationException("Les canaux doivent désigner le même dossier de projet. Connectez un canal par projet.");
            var message=new RelayMessage {Id=requestedId??Guid.NewGuid().ToString("N"),SourceChannelId=source.Id,TargetChannelId=target.Id,SourceAccountKey=source.AccountKey,TargetAccountKey=target.AccountKey,SourceHome=source.Home,TargetHome=target.Home,SourceThreadId=sourceThread,Title=title,Prompt=prompt,Revision=revision??"",Workspace=source.Workspace,ReturnToSource=returnToSource,ReplyTo=replyTo,State="queued",ReturnState="none",CreatedUtc=DateTime.UtcNow.ToString("o")};
            message.NewConversation=String.IsNullOrWhiteSpace(replyTo);MessageId(message.Id);
            if(!String.IsNullOrWhiteSpace(replyTo)) {
                var previous=Message(replyTo);
                if(previous.SourceChannelId!=sourceId || previous.TargetChannelId!=targetId || previous.SourceAccountKey!=source.AccountKey || previous.TargetAccountKey!=target.AccountKey || !SamePath(previous.Workspace,message.Workspace) || String.IsNullOrEmpty(previous.TargetThreadId) || previous.State!="completed")throw new InvalidOperationException("La conversation précédente n'est pas terminée ou appartient à un autre canal ou dossier.");
                message.TargetThreadId=previous.TargetThreadId;
            }
            if(prepare!=null)prepare(message);
            if(!SamePath(source.Workspace,target.Workspace)&&(message.Job==null||!RelayRouter.WorkspaceAllowed(RelayPolicies.Load(this),source,target,message.Job)))throw new InvalidOperationException("Espace isolé non autorisé.");
            using(Lease("registry")) {
                if(Exists(message.Id)) {
                    var old=Message(message.Id);
                    if(old.SourceChannelId!=sourceId || old.SourceThreadId!=sourceThread || old.TargetChannelId!=targetId || old.Prompt!=prompt || old.Title!=title || old.Revision!=message.Revision || old.ReturnToSource!=returnToSource || old.ReplyTo!=replyTo)throw new InvalidOperationException("Cet identifiant existe avec un contenu différent.");
                    return old;
                }
                Save(message);
            }
            return message;
        }
        public void Cancel(string id)
        {
            MessageId(id);
            using(Lease("message-"+id)) {var m=Message(id);if(m.State!="queued")throw new InvalidOperationException("Une demande déjà envoyée ne peut pas être annulée depuis le relais.");m.State="cancelled";Save(m);}
        }
        public void RequestCancel(string id)
        {
            MessageId(id);using(Lease("message-"+id)){
                var m=Message(id);
                if(m.State=="queued"||m.State=="children"){m.Result=m.State=="children"?"Reprise du parent annulée. Les sous-tâches déjà envoyées restent indépendantes.":"Demande annulée avant exécution.";m.State="cancelled";m.Outcome="cancelled";m.CompletedUtc=DateTime.UtcNow.ToString("o");m.ReturnState=m.ReturnToSource?(m.ReturnMode=="manual"?"manual":"pending"):"none";}
                else if(m.State=="waiting"){m.CancellationRequested=true;m.Error="Interruption demandée. Cette interface Codex n'expose pas encore d'arrêt ciblé confirmé ; utilisez Arrêter dans la conversation destinataire.";}
                else throw new InvalidOperationException("Cette tâche ne peut pas être annulée dans son état actuel.");
                Save(m);
            }
        }
        public void CloseReviewed(string id)
        {
            MessageId(id);
            using(Lease("message-"+id)){var m=Message(id);if(!new[]{"uncertain","attention","failed"}.Contains(m.State))throw new InvalidOperationException("Seule une demande arrêtée peut être classée après vérification.");m.State="closed";m.Error="Classée par l'utilisateur après vérification dans Codex. Historique conservé.";Save(m);}
        }
        public void Recheck(string id)
        {
            MessageId(id);
            using(Lease("message-"+id)) {
                var m=Message(id);
                if(m.State=="uncertain"||m.ReturnState=="uncertain"){m.ReconcileAttempts=0;m.ReconcileAfterUtc=null;Save(m);return;}
                if(String.IsNullOrEmpty(m.TargetThreadId) || !new[]{"attention","failed","waiting"}.Contains(m.State))throw new InvalidOperationException("Aucune conversation connue à relire. Aucun nouvel envoi ne sera effectué.");
                m.State="waiting";m.Error=null;Save(m);
            }
        }
    }
}
