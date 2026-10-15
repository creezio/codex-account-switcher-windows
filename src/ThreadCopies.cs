using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class CopyThreadChoice
    {
        public string Id {get;set;} public string Title {get;set;} public string Folder {get;set;}
        public string Path {get;set;} public string Mode {get;set;} public string Status {get;set;}
    }
    public sealed class CopyThreadPage
    { public CopyThreadChoice[] Threads {get;set;} public string Cursor {get;set;} }
    public sealed class ThreadCopyRecord
    {
        public string Id {get;set;} public string Source {get;set;} public string Destination {get;set;}
        public string SourceAccount {get;set;} public string DestinationAccount {get;set;}
        public string SourceThread {get;set;} public string TargetThread {get;set;} public string Title {get;set;}
        public string Folder {get;set;} public string State {get;set;} public string Message {get;set;}
        public string Created {get;set;} public string Updated {get;set;} public string Digest {get;set;}
        public int HistoryItems {get;set;} public string SourceMode {get;set;}
        public string ModelDigest {get;set;} public int ModelItems {get;set;} public string[] VisibleMessages {get;set;}
        public string PermissionProfile {get;set;} public object ApprovalPolicy {get;set;} public string ApprovalsReviewer {get;set;}
        public string RequestedFolder {get;set;} public string SourceFolder {get;set;}
        public bool NativeResumeNotice {get;set;}
        public bool LosslessPngEncoding {get;set;}
        public int DuplicateToolAnnotations {get;set;} public int EmptyContextDefaults {get;set;}
    }
    internal sealed class RolloutSnapshot
    {
        internal string Digest,SourceMode,ModelDigest;internal int Items,ModelItems,DuplicateToolAnnotations,EmptyContextDefaults;internal string[] VisibleMessages;internal bool NativeResumeNotice,LosslessPngEncoding;
        internal const string NativeAbortText="<turn_aborted>\nThe previous turn was interrupted on purpose. Any running unified exec processes may still be running in the background. If any tools/commands were aborted, they may have partially executed.\n</turn_aborted>";
        private static bool IsNativeAbortNotice(object payload)
        {
            var obj=Json.Obj(payload);var content=RelayEngine.Rows(Json.Get(payload,"content")).ToArray();
            return Json.Str(Json.Get(payload,"type"))=="message"&&Json.Str(Json.Get(payload,"role"))=="developer"&&obj.Keys.All(k=>new[]{"type","role","id","content","internal_chat_message_metadata_passthrough"}.Contains(k))&&content.Length==1&&Json.Obj(content[0]).Keys.All(k=>k=="type"||k=="text")&&Json.Str(Json.Get(content[0],"type"))=="input_text"&&Json.Str(Json.Get(content[0],"text"))==NativeAbortText&&Canonical(Json.Get(payload,"internal_chat_message_metadata_passthrough"))=="{\"content_item_kinds\":[\"generic.turn_aborted\"]}";
        }
        internal static string TextDigest(string role,string text){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(role+"\n"+text))).Replace("-","");}
        internal const long MaxJournalBytes=512L*1024*1024;
        internal const int MaxItemCharacters=32*1024*1024;
        internal const int NativeLineBytes=16*1024*1024;
        internal static string WriteRollout(object value)
        {
#if NETCOREAPP
            return System.Text.Json.JsonSerializer.Serialize(value,Desktop.JsonCompatibility.Options);
#else
            return new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=MaxItemCharacters}.Serialize(value);
#endif
        }
        internal static string DisplayLine(object value)
        {string line=WriteRollout(value);if(Encoding.UTF8.GetByteCount(line)+2>=NativeLineBytes)throw new InvalidOperationException("Ce message d’affichage dépasse la limite native ; aucun import incomplet n’est créé.");return line;}
        internal static object ReadRollout(string line)
        {
            // Image response items can exceed the small limit used for settings
            // and relay requests. Keep this exception confined to rollout imports.
#if NETCOREAPP
            return System.Text.Json.JsonSerializer.Deserialize<object>(line,Desktop.JsonCompatibility.Options);
#else
            return new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=MaxItemCharacters}.Deserialize<object>(line);
#endif
        }
        internal static string EmbeddedUrl(object content)
        {string url=Json.Str(Json.Get(content,"url")),legacy=Json.Str(Json.Get(content,"image_url"));return url.Length==0?legacy:url;}
        internal static bool EmbeddedImage(object content)
        {
            if(Json.Str(Json.Get(content,"type"))!="image"||Json.Get(content,"detail")!=null)return false;
            string url=EmbeddedUrl(content),legacy=Json.Str(Json.Get(content,"image_url"));
            if(!Json.Obj(content).Keys.All(k=>new[]{"type","url","image_url","detail"}.Contains(k))||(legacy.Length>0&&legacy!=url))return false;
            return url.Length<=MaxItemCharacters&&new[]{"data:image/png;base64,","data:image/jpeg;base64,","data:image/webp;base64,","data:image/gif;base64,"}.Any(p=>url.StartsWith(p,StringComparison.Ordinal));
        }
        internal static string VisibleDigest(string role,string text,IEnumerable<string> images,IEnumerable<string> embedded=null)
        {
            var paths=(images??new string[0]).ToArray();var inline=(embedded??new string[0]).ToArray();if(paths.Length==0&&inline.Length==0)return TextDigest(role,text);
            var proof=new List<string>();foreach(string image in paths){
                string path=Path.GetFullPath(NativePath(image));SafeFiles.RejectLinks(path);
                if(!File.Exists(path))throw new InvalidOperationException("Une image locale jointe à ce thread est introuvable. Rétablissez le fichier avant de copier la conversation.");
                var info=new FileInfo(path);long length=info.Length;DateTime modified=info.LastWriteTimeUtc;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())proof.Add(path.ToUpperInvariant()+":"+BitConverter.ToString(sha.ComputeHash(stream)));
                info.Refresh();if(info.Length!=length||info.LastWriteTimeUtc!=modified)throw new InvalidOperationException("Une image jointe a changé pendant la préparation.");
            }
            string attachments=paths.Length==0?"":"\nlocal-images:"+Canonical(proof);
            if(inline.Length>0){var hashes=new List<string>();foreach(string url in inline){if(!EmbeddedImage(new Dictionary<string,object>{{"type","image"},{"url",url}}))throw new InvalidOperationException("Format d’image intégrée non pris en charge.");byte[] bytes;try{bytes=Convert.FromBase64String(url.Substring(url.IndexOf(',')+1));}catch(FormatException){throw new InvalidOperationException("Image intégrée invalide.");}if(bytes.Length==0)throw new InvalidOperationException("Image intégrée vide.");using(var sha=SHA256.Create())hashes.Add(url.Substring(0,url.IndexOf(',')+1)+BitConverter.ToString(sha.ComputeHash(bytes)));}attachments+="\nembedded-images:"+Canonical(hashes);}
            return TextDigest(role,text+attachments);
        }
        private static string Canonical(object value)
        {
            var obj=value as IDictionary<string,object>;
            // Native forks assign an id to older messages which did not have one.
            // Tool call ids and all actual content remain part of the comparison.
            if(obj!=null)return "{"+String.Join(",",obj.Where(p=>p.Value!=null&&!(p.Key=="id"&&Json.Str(Json.Get(obj,"type"))=="message")&&!EmptyAttachmentDefault(obj,p)).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>Quote(p.Key)+":"+Canonical(p.Value)))+"}";
            var items=value as IEnumerable;if(items!=null&&!(value is string))return "["+String.Join(",",items.Cast<object>().Select(Canonical))+"]";
            if(value is string)return Quote((string)value);if(value==null)return "null";if(value is bool)return (bool)value?"true":"false";
            return Convert.ToString(value,CultureInfo.InvariantCulture);
        }
        private static string Quote(string text)
        {var s=new StringBuilder("\"");foreach(char c in text)if(c<' '||c>'~'||c=='"'||c=='\\')s.Append("\\u").Append(((int)c).ToString("x4",CultureInfo.InvariantCulture));else s.Append(c);return s.Append('"').ToString();}
        private static bool EmptyAttachmentDefault(IDictionary<string,object> obj,KeyValuePair<string,object> p)
        {var list=p.Value as IEnumerable;return Json.Str(Json.Get(obj,"type"))=="user_message"&&new[]{"images","local_images","local_audio","text_elements"}.Contains(p.Key)&&list!=null&&!list.Cast<object>().Any();}
        internal static bool Under(string path,string root)
        {return Path.GetFullPath(NativePath(path)).StartsWith(Path.GetFullPath(NativePath(root)).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
        internal static string NativePath(string path)
        {
            if(String.IsNullOrEmpty(path))return path;
            if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))path=@"\\"+path.Substring(8);
            else if(path.StartsWith(@"\\?\",StringComparison.Ordinal)){path=path.Substring(4);if(path.Length<3||!Char.IsLetter(path[0])||path[1]!=':'||path[2]!='\\')throw new InvalidOperationException("Chemin Windows de conversation non pris en charge.");}
            if(path.StartsWith(@"\\.\",StringComparison.Ordinal)||path.IndexOf(':',Math.Min(path.Length,2))>=0)throw new InvalidOperationException("Chemin Windows de conversation non pris en charge.");
            return path;
        }
        internal static void RequireRollout(string home,string path)
        {
            path=NativePath(path);
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||!path.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)||!(Under(path,Path.Combine(home,"sessions"))||Under(path,Path.Combine(home,"archived_sessions"))))throw new InvalidOperationException("Ce thread n’a pas de journal local compatible dans cette instance.");
            SafeFiles.RejectLinks(path);
            if(!File.Exists(path))throw new InvalidOperationException("L’historique local n’est pas encore enregistré. Terminez un échange dans Codex puis réessayez.");
        }
        internal static bool HasMarker(string home,object thread,string marker)
        {
            string path=NativePath(Json.Str(Json.Get(thread,"path")));if(String.IsNullOrEmpty(path))return false;RequireRollout(home,path);
            using(var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete),Encoding.UTF8,true)){
                string line=reader.ReadLine();if(line==null||line.Length>4194304)return false;
                var meta=Json.Read<object>(line);var payload=Json.Get(meta,"payload");
                return Json.Str(Json.Get(meta,"type"))=="session_meta"&&Json.Str(Json.Get(payload,"id"))==Json.Str(Json.Get(thread,"id"))&&Json.Str(Json.Get(payload,"thread_source"))==marker;
            }
        }
        internal static RolloutSnapshot Scan(string path,string expectedId,string copy=null,CancellationToken token=default(CancellationToken),bool ignoreNativeResumeNotice=false)
        {
            if(ignoreNativeResumeNotice&&copy!=null)throw new InvalidOperationException("Native notice normalization is verification-only.");
            path=NativePath(path);
            var info=new FileInfo(path);if(info.Length>MaxJournalBytes)throw new InvalidOperationException("Ce journal dépasse la limite d’import de 512 Mio.");
            long length=info.Length;DateTime modified=info.LastWriteTimeUtc;var result=new RolloutSnapshot();var visible=new List<string>();
            var compatibility=copy==null?null:new LegacyHistoryCompatibility(path,token);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new StreamReader(stream,Encoding.UTF8,true))
            using(var writer=copy==null?null:new StreamWriter(new FileStream(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None),new UTF8Encoding(false)))
            using(var sha=SHA256.Create())using(var modelSha=SHA256.Create()) {
                string line;bool first=true;byte[] pendingNotice=null;bool noticeAborted=false,noticeSuffixValid=true;var pngEncoding=writer==null?null:new PngHistoryEncoding();
                Action<byte[],bool> append=(bytes,isModel)=>{sha.TransformBlock(bytes,0,bytes.Length,null,0);result.Items++;if(isModel){modelSha.TransformBlock(bytes,0,bytes.Length,null,0);result.ModelItems++;}};
                while((line=reader.ReadLine())!=null){
                    token.ThrowIfCancellationRequested();if(line.Length==0)continue;
                    if(line.Length>MaxItemCharacters)throw new InvalidOperationException("Un élément de cet historique dépasse la taille prise en charge (32 Mio).");
                    object entry;try{entry=ReadRollout(line);}catch{throw new InvalidOperationException("Le journal est incomplet ou son format n’est pas reconnu. Attendez la fin du travail puis réessayez.");}
                    string type=Json.Str(Json.Get(entry,"type"));var payload=Json.Get(entry,"payload");
                    if(pendingNotice!=null){string eventType=Json.Str(Json.Get(payload,"type"));if(type=="event_msg"&&eventType=="turn_aborted")noticeAborted=true;else if(type!="event_msg"||eventType!="thread_settings_applied")noticeSuffixValid=false;}
                    if(first){
                        first=false;if(type!="session_meta"||Json.Str(Json.Get(payload,"id"))!=expectedId)throw new InvalidOperationException("Le journal ne correspond pas à la conversation sélectionnée.");
                        result.SourceMode=Json.Str(Json.Get(payload,"history_mode"));if(result.SourceMode.Length==0)result.SourceMode="legacy";
                        if(result.SourceMode!="legacy"&&result.SourceMode!="paginated")throw new InvalidOperationException("Ce format d’historique n’est pas encore pris en charge.");
                        // Paginated metadata points at the source SQLite store. The complete
                        // JSONL export is replayed independently; no database or auth is copied.
                        if(writer!=null){Json.Obj(payload)["history_mode"]="legacy";writer.WriteLine(Json.Write(entry));}
                        continue;
                    }
                    if(type=="response_item"||type=="compacted"||(type=="event_msg"&&(Json.Str(Json.Get(payload,"type"))=="user_message"||Json.Str(Json.Get(payload,"type"))=="agent_message"))){
                        if(pendingNotice!=null){append(pendingNotice,true);pendingNotice=null;}
                        byte[] bytes=Encoding.UTF8.GetBytes(type+"\n"+Canonical(payload)+"\n");
                        if(ignoreNativeResumeNotice&&type=="response_item"&&IsNativeAbortNotice(payload)){pendingNotice=bytes;noticeAborted=false;noticeSuffixValid=true;}
                        else append(bytes,type!="event_msg");
                        if(type=="event_msg")visible.Add(VisibleDigest(Json.Str(Json.Get(payload,"type"))=="user_message"?"user":"assistant",Json.Str(Json.Get(payload,"message")),RelayEngine.Rows(Json.Get(payload,"local_images")).Select(Json.Str),RelayEngine.Rows(Json.Get(payload,"images")).Select(Json.Str)));
                        if(copy!=null)CheckImages(payload);
                    }
                    if(writer!=null){
                        compatibility.Apply(entry);if(compatibility.DuplicateAnnotations>0||compatibility.EmptyDefaults>0)line=WriteRollout(entry);
                        if(Encoding.UTF8.GetByteCount(line)+2>=NativeLineBytes){
                            pngEncoding.Optimize(entry);string optimized=WriteRollout(entry);
                            if(Encoding.UTF8.GetByteCount(optimized)+2>=NativeLineBytes)throw new InvalidOperationException("Codex ne peut importer cet élément de plus de 16 Mio sans perdre du contexte. L’original est conservé ; aucune copie n’a été créée.");
                            if(Canonical(entry)!=Canonical(ReadRollout(optimized)))throw new InvalidOperationException("Le contenu JSON a changé pendant la préparation de l’import.");
                            line=optimized;result.LosslessPngEncoding=pngEncoding.Changed;
                        }
                        writer.WriteLine(line);
                    }
                    // Paginated messages use item_completed instead of the old
                    // display events. Codex's legacy migration currently ignores
                    // UserMessage/AgentMessage item_completed records. Add their
                    // display-only equivalents; model response items stay intact.
                    if(writer!=null&&result.SourceMode=="paginated"&&type=="event_msg"&&Json.Str(Json.Get(payload,"type"))=="item_completed"){
                        var display=LegacyDisplay(Json.Get(payload,"item"));
                        if(display!=null&&writer!=null)writer.WriteLine(DisplayLine(new{timestamp=Json.Get(entry,"timestamp"),type="event_msg",payload=display}));
                    }
                }
                if(pendingNotice!=null){if(noticeAborted&&noticeSuffixValid)result.NativeResumeNotice=true;else append(pendingNotice,true);}
                if(first||result.Items==0)throw new InvalidOperationException("Cette conversation n’a pas d’historique de travail importable.");
                sha.TransformFinalBlock(new byte[0],0,0);result.Digest=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
                modelSha.TransformFinalBlock(new byte[0],0,0);result.ModelDigest=BitConverter.ToString(modelSha.Hash).Replace("-","").ToLowerInvariant();result.VisibleMessages=visible.ToArray();
            }
            if(compatibility!=null){result.DuplicateToolAnnotations=compatibility.DuplicateAnnotations;result.EmptyContextDefaults=compatibility.EmptyDefaults;}
            info.Refresh();if(info.Length!=length||info.LastWriteTimeUtc!=modified)throw new InvalidOperationException("La conversation a changé pendant la préparation. Attendez la fin du travail puis réessayez.");
            return result;
        }
        internal static object LegacyDisplay(object item)
        {
            string type=Json.Str(Json.Get(item,"type"));
            if(type=="AgentMessage"){
                var content=RelayEngine.Rows(Json.Get(item,"content")).ToArray();
                if(content.Any(c=>Json.Str(Json.Get(c,"type"))!="Text"))throw new InvalidOperationException("Ce message utilise un contenu dont l’affichage natif n’est pas encore pris en charge.");
                return new{type="agent_message",message=String.Join("\n",content.Select(c=>Json.Str(Json.Get(c,"text")))),phase=Json.Get(item,"phase")};
            }
            if(type=="UserMessage"){
                var content=RelayEngine.Rows(Json.Get(item,"content")).ToArray();
                // Never silently turn a multimodal message into text-only UI.
                if(content.Any(c=>!new[]{"text","localImage","local_image"}.Contains(Json.Str(Json.Get(c,"type")))&&!EmbeddedImage(c)))throw new InvalidOperationException("Cette conversation contient une pièce jointe dont l’affichage natif ne peut pas encore être copié fidèlement.");
                var paths=content.Where(c=>new[]{"localImage","local_image"}.Contains(Json.Str(Json.Get(c,"type")))).Select(c=>Json.Str(Json.Get(c,"path"))).ToArray();
                var embedded=content.Where(EmbeddedImage).Select(EmbeddedUrl).ToArray();
                VisibleDigest("user","",paths,embedded);
                return new{type="user_message",message=String.Join("\n",content.Where(c=>Json.Str(Json.Get(c,"type"))=="text").Select(c=>Json.Str(Json.Get(c,"text")))),images=embedded,local_images=paths,text_elements=new object[0]};
            }
            return null;
        }
        private static void CheckImages(object value)
        {
            var obj=value as IDictionary<string,object>;
            if(obj!=null){
                string type=Json.Str(Json.Get(obj,"type")),path=null;
                if(type=="local_image"||type=="localImage")path=Json.Str(Json.Get(obj,"path"));
                if(type=="input_image"){string url=Json.Str(Json.Get(obj,"image_url"));Uri uri;if(Uri.TryCreate(url,UriKind.Absolute,out uri)&&uri.IsFile)path=uri.LocalPath;else if(Path.IsPathRooted(url)&&!url.StartsWith("data:")&&!url.StartsWith("http"))path=url;}
                if(!String.IsNullOrEmpty(path)&&!File.Exists(path))throw new InvalidOperationException("Une image locale jointe à ce thread est introuvable. Rétablissez le fichier avant de copier la conversation.");
                foreach(var pair in obj)CheckImages(pair.Value);
            }else {var list=value as IEnumerable;if(list!=null&&!(value is string))foreach(object item in list)CheckImages(item);}
        }
    }
    internal interface IThreadCopies
    {
        Task<CopyThreadPage> List(string instance,string query,string cursor,CancellationToken token);
        ThreadCopyRecord[] Recent(string source);
        Task<ThreadCopyRecord> Copy(string id,string source,string destination,string thread,string title,CancellationToken token);
        Task Open(ThreadCopyRecord record,CancellationToken token);
    }
    internal sealed class ThreadCopies:IThreadCopies
    {
        private readonly RelayStore store;
        private readonly Func<string,IThreadProfileRpc> connect;
        private readonly Func<AccountService,DesktopInstance,string,string,CancellationToken,Task> idle;
        private readonly bool nativeDesktop;
        private readonly Func<string,string,string,CancellationToken,Task> migrate;
        internal ThreadCopies(RelayStore store,Func<string,IThreadProfileRpc> factory=null,Func<AccountService,DesktopInstance,string,string,CancellationToken,Task> idleCheck=null,Func<string,string,string,CancellationToken,Task> migration=null)
        {
            this.store=store;connect=factory??(home=>new ThreadProfileRpc(new AccountService(Path.GetDirectoryName(store.Root)).Settings.CodexExecutable,home));
            idle=idleCheck??CheckIdle;nativeDesktop=idleCheck==null;migrate=migration??ThreadCopyMigration.Run;
        }
        private AccountService Accounts(){return new AccountService(Path.GetDirectoryName(store.Root));}
        private static DesktopInstance Instance(AccountService a,string id){var i=a.Data.Instances.SingleOrDefault(x=>x.Id==id&&!x.Archived);if(i==null)throw new InvalidOperationException("Instance introuvable ou archivée.");NamedInstances.VerifyAccount(a,i);return i;}
        private async Task CheckIdle(AccountService a,DesktopInstance source,string folder,string thread,CancellationToken token)
        {
            var channel=await NamedInstances.Connect(store,a,source,folder,token,thread,false).ConfigureAwait(false);
            var live=await new DesktopRelayTransport().Call(channel,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token).ConfigureAwait(false);
            DirectMessages.RequireThread(live,thread);if(DirectMessages.Busy(live))throw new InvalidOperationException("Ce thread travaille encore ou attend une réponse. Terminez ou arrêtez son tour dans Codex avant de le copier.");
        }
        private static CopyThreadChoice Choice(object t)
        {return new CopyThreadChoice{Id=Json.Str(Json.Get(t,"id")),Title=Json.Str(Json.Get(t,"name")),Folder=RolloutSnapshot.NativePath(Json.Str(Json.Get(t,"cwd"))),Path=RolloutSnapshot.NativePath(Json.Str(Json.Get(t,"path"))),Mode=Json.Str(Json.Get(t,"historyMode")),Status=Json.Str(Json.Get(Json.Get(t,"status"),"type"))};}
        public async Task<CopyThreadPage> List(string instance,string query,string cursor,CancellationToken token)
        {
            var a=Accounts();var owner=Instance(a,instance);
            using(var rpc=connect(a.Instances.Home(owner))){
                var page=await rpc.Call("thread/list",new{limit=50,cursor=cursor,searchTerm=String.IsNullOrWhiteSpace(query)?null:query,sortKey="updated_at",useStateDbOnly=true},token).ConfigureAwait(false);
                var choices=RelayEngine.Rows(Json.Get(page,"data")).Select(Choice).ToArray();
                foreach(var c in choices)if(String.IsNullOrWhiteSpace(c.Title))c.Title="Conversation "+c.Id;
                return new CopyThreadPage{Threads=choices,Cursor=Json.Str(Json.Get(page,"nextCursor"))};
            }
        }
        public ThreadCopyRecord[] Recent(string source)
        {return Directory.GetFiles(store.Root,"thread-copy-*.dpapi").Select(p=>store.ReadRecord<ThreadCopyRecord>(Path.GetFileName(p))).Where(r=>r.Source==source).OrderByDescending(r=>r.Created).Take(12).ToArray();}
        private void Save(ThreadCopyRecord r){r.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("thread-copy-"+r.Id+".dpapi",r);}
        internal static async Task<object> DestinationDefaults(IThreadProfileRpc rpc,string folder,CancellationToken token)
        {
            // Ask Codex to resolve its own configured defaults. This ephemeral
            // session has no prompt, history or autonomous goal, and is discarded.
            var result=await rpc.Call("thread/start",new{cwd=folder,runtimeWorkspaceRoots=new[]{folder},ephemeral=true,historyMode="legacy"},token).ConfigureAwait(false);
            string ephemeral=Json.Str(Json.Get(Json.Get(result,"thread"),"id"));
            if(ephemeral.Length>0)await rpc.Call("thread/unsubscribe",new{threadId=ephemeral},token).ConfigureAwait(false);
            if(String.IsNullOrEmpty(Json.Str(Json.Get(Json.Get(result,"activePermissionProfile"),"id")))||Json.Get(result,"approvalPolicy")==null||String.IsNullOrEmpty(Json.Str(Json.Get(result,"approvalsReviewer"))))throw new InvalidOperationException("Cette version de Codex ne fournit pas les permissions de destination nécessaires à une copie sûre.");
            return result;
        }
        public Task<ThreadCopyRecord> Copy(string id,string source,string destination,string thread,string title,CancellationToken token)
        {return CopyTo(id,source,destination,thread,title,null,token);}
        internal async Task<ThreadCopyRecord> CopyTo(string id,string source,string destination,string thread,string title,string folder,CancellationToken token)
        {
            if(!String.IsNullOrEmpty(folder))folder=RelayStore.WorkspacePath(folder);else folder=null;
            RelayStore.MessageId(id);if(source==destination)throw new InvalidOperationException("Choisissez une autre instance pour la copie.");
            Guid parsed;if(!Guid.TryParse(thread,out parsed))throw new InvalidOperationException("Identifiant de conversation invalide.");
            if(String.IsNullOrWhiteSpace(title)||title.Length>160)throw new InvalidOperationException("Donnez à la copie un titre de 1 à 160 caractères.");
            using(store.Lease("thread-copy-"+id)){
                var a=Accounts();var from=Instance(a,source);var to=Instance(a,destination);
                var record=store.ReadRecord<ThreadCopyRecord>("thread-copy-"+id+".dpapi");
                if(record.Id!=null&&(record.Source!=source||record.Destination!=destination||record.SourceThread!=thread||record.Title!=title||record.RequestedFolder!=folder||record.SourceAccount!=from.AccountKey||record.DestinationAccount!=to.AccountKey))throw new InvalidOperationException("Cette copie est liée à un autre contenu ou compte. Recommencez depuis le formulaire.");
                string sourceHome=a.Instances.Home(from),targetHome=a.Instances.Home(to);
                if(RelayStore.SamePath(sourceHome,targetHome))throw new InvalidOperationException("Les deux instances utilisent le même profil Codex.");
                if(record.Id==null)record=new ThreadCopyRecord{Id=id,Source=source,Destination=destination,SourceThread=thread,SourceAccount=from.AccountKey,DestinationAccount=to.AccountKey,Title=title,RequestedFolder=folder,Created=DateTime.UtcNow.ToString("o"),State="preparing"};
                // A retry must never rename, clear a newly set goal or revalidate an
                // already delivered conversation that its owner may have continued.
                if(record.State=="completed")return record;
                using(var target=connect(targetHome)){
                    if(record.State=="verification_failed"){
                        var read=await target.Call("thread/read",new{threadId=record.TargetThread,includeTurns=false},token).ConfigureAwait(false);var choice=Choice(Json.Get(read,"thread"));RolloutSnapshot.RequireRollout(targetHome,choice.Path);
                        var content=RolloutSnapshot.Scan(choice.Path,record.TargetThread,null,token);
                        if(!MatchesNativeResumeNotice(record,choice.Path,content,token))return record;
                        if(nativeDesktop)await RequireNativeRecoveryIdle(a,to,record.TargetThread,token).ConfigureAwait(false);
                        else await idle(a,to,record.Folder,record.TargetThread,token).ConfigureAwait(false);
                        record.ModelDigest=content.ModelDigest;record.ModelItems=content.ModelItems;record.VisibleMessages=content.VisibleMessages;record.NativeResumeNotice=true;record.State="migrating";Save(record);
                        return await Finish(record,a,to,target,token).ConfigureAwait(false);
                    }
                    if(record.State=="copying"||record.State=="uncertain"){
                        string cursor=null;int pages=0;var found=new List<string>();
                        do{var page=await target.Call("thread/list",new{limit=100,cursor=cursor,modelProviders=new string[0],useStateDbOnly=true},token).ConfigureAwait(false);foreach(var t in RelayEngine.Rows(Json.Get(page,"data")))if(RolloutSnapshot.HasMarker(targetHome,t,"creezio-copy-"+id))found.Add(Json.Str(Json.Get(t,"id")));cursor=Json.Str(Json.Get(page,"nextCursor"));}while(cursor.Length>0&&++pages<20);
                        if(found.Distinct().Count()!=1){record.State="uncertain";record.Message="Résultat non confirmé. Aucune nouvelle copie ne sera lancée automatiquement. Vérifiez les conversations de la destination.";Save(record);return record;}
                        record.TargetThread=found.Distinct().Single();record.State="verifying";Save(record);
                    }
                    if(!String.IsNullOrEmpty(record.TargetThread))return await Finish(record,a,to,target,token).ConfigureAwait(false);
                    using(var origin=connect(sourceHome)){
                        var read=await origin.Call("thread/read",new{threadId=thread,includeTurns=false},token).ConfigureAwait(false);var choice=Choice(Json.Get(read,"thread"));
                        if(choice.Id!=thread)throw new InvalidOperationException("Conversation source introuvable.");
                        RolloutSnapshot.RequireRollout(sourceHome,choice.Path);record.SourceFolder=RelayStore.WorkspacePath(choice.Folder);record.Folder=folder??record.SourceFolder;
                        await idle(a,from,record.SourceFolder,thread,token).ConfigureAwait(false);
                        // A fresh destination does not need an unrelated starter chat.
                        if(nativeDesktop){var state=a.Instances.Runtime.Probe(to);if(!state.Running||state.Phase=="unknown"||!DesktopRuntime.SameProcess(state.DesktopPid,state.DesktopStartTicks))throw new InvalidOperationException("Ouvrez « "+to.Name+" » dans le Switcher avant de copier la conversation.");}
                        string scratch=Path.Combine(store.Root,"thread-copies");SafeFiles.PrivateDirectory(scratch);string snapshot=Path.Combine(scratch,id+".jsonl");
                        SafeFiles.RejectLinks(snapshot);if(File.Exists(snapshot))File.Delete(snapshot);
                        try{
                            var originalFile=new FileInfo(choice.Path);long originalLength=originalFile.Length;DateTime originalTime=originalFile.LastWriteTimeUtc;
                            var content=RolloutSnapshot.Scan(choice.Path,thread,snapshot,token);
                            if(content.SourceMode=="paginated")await CompleteSnapshotDisplay(origin,thread,snapshot,token).ConfigureAwait(false);
                            var prepared=RolloutSnapshot.Scan(snapshot,thread,null,token);record.Digest=prepared.Digest;record.HistoryItems=prepared.Items;record.SourceMode=content.SourceMode;record.LosslessPngEncoding=content.LosslessPngEncoding;record.DuplicateToolAnnotations=content.DuplicateToolAnnotations;record.EmptyContextDefaults=content.EmptyContextDefaults;
                            var defaults=await DestinationDefaults(target,record.Folder,token).ConfigureAwait(false);record.PermissionProfile=Json.Str(Json.Get(Json.Get(defaults,"activePermissionProfile"),"id"));record.ApprovalPolicy=Json.Get(defaults,"approvalPolicy");record.ApprovalsReviewer=Json.Str(Json.Get(defaults,"approvalsReviewer"));
                            await idle(a,from,record.SourceFolder,thread,token).ConfigureAwait(false);
                            var current=RolloutSnapshot.Scan(choice.Path,thread,null,token);originalFile.Refresh();if(current.Digest!=content.Digest||originalFile.Length!=originalLength||originalFile.LastWriteTimeUtc!=originalTime)throw new InvalidOperationException("L’historique a changé. Recommencez après la fin du travail.");
                            if(content.SourceMode=="paginated"){var visibleNow=await ReadVisible(origin,thread,token).ConfigureAwait(false);if(!prepared.VisibleMessages.SequenceEqual(visibleNow))throw new InvalidOperationException("Les messages visibles ont changé pendant la préparation. Recommencez après la fin du travail.");}
                            NamedInstances.VerifyAccount(a,from);NamedInstances.VerifyAccount(a,to);token.ThrowIfCancellationRequested();record.State="copying";Save(record);
                            var fork=await target.Call("thread/fork",new{threadId=thread,path=snapshot,cwd=record.Folder,runtimeWorkspaceRoots=new[]{record.Folder},deferGoalContinuation=true,threadSource="creezio-copy-"+id,permissions=record.PermissionProfile,approvalPolicy=record.ApprovalPolicy,approvalsReviewer=record.ApprovalsReviewer,excludeTurns=true},token).ConfigureAwait(false);
                            record.TargetThread=Json.Str(Json.Get(Json.Get(fork,"thread"),"id"));if(record.TargetThread==thread||!Guid.TryParse(record.TargetThread,out parsed))throw new InvalidOperationException("Codex n’a pas créé un identifiant de copie distinct.");
                            record.State="verifying";Save(record);
                            return await Finish(record,a,to,target,token).ConfigureAwait(false);
                        }catch(Exception){if(record.State=="copying"){record.State="uncertain";record.Message="La réponse de Codex n’a pas confirmé la copie. Utilisez Vérifier ; une nouvelle copie ne sera pas créée en double.";Save(record);}throw;}
                        finally{try{if(File.Exists(snapshot))File.Delete(snapshot);}catch(IOException){}catch(UnauthorizedAccessException){}}
                    }
                }
            }
        }
        private async Task<ThreadCopyRecord> Finish(ThreadCopyRecord r,AccountService a,DesktopInstance to,IThreadProfileRpc target,CancellationToken token)
        {
            NamedInstances.VerifyAccount(a,to);
            if(r.State!="migrating"){
            // No inherited autonomous goal is allowed to start during import.
            await target.Call("thread/goal/clear",new{threadId=r.TargetThread},token).ConfigureAwait(false);
            var resumed=await target.Call("thread/resume",new{threadId=r.TargetThread,permissions=r.PermissionProfile,approvalPolicy=r.ApprovalPolicy,approvalsReviewer=r.ApprovalsReviewer,deferGoalContinuation=true,excludeTurns=true},token).ConfigureAwait(false);
            if(Json.Str(Json.Get(Json.Get(resumed,"activePermissionProfile"),"id"))!=r.PermissionProfile||Json.Write(Json.Get(resumed,"approvalPolicy"))!=Json.Write(r.ApprovalPolicy)||Json.Str(Json.Get(resumed,"approvalsReviewer"))!=r.ApprovalsReviewer)throw new InvalidOperationException("Les permissions de la copie ne sont pas confirmées par Codex. La reprise automatique reste bloquée.");
            await target.Call("thread/name/set",new{threadId=r.TargetThread,name=r.Title},token).ConfigureAwait(false);
            var read=await target.Call("thread/read",new{threadId=r.TargetThread,includeTurns=false},token).ConfigureAwait(false);var copy=Choice(Json.Get(read,"thread"));
            if(copy.Id!=r.TargetThread)throw new InvalidOperationException("La copie n’est pas retrouvée dans l’instance destinataire.");
            RolloutSnapshot.RequireRollout(a.Instances.Home(to),copy.Path);var content=RolloutSnapshot.Scan(copy.Path,r.TargetThread,null,token);
            if(content.Digest!=r.Digest||content.Items!=r.HistoryItems){if(MatchesNativeResumeNotice(r,copy.Path,content,token))r.NativeResumeNotice=true;else{r.State="verification_failed";r.Message="Codex a créé une copie, mais son historique ne correspond pas intégralement à la source. Aucune reprise automatique ; vérifiez dans Codex.";Save(r);return r;}}
            r.ModelDigest=content.ModelDigest;r.ModelItems=content.ModelItems;r.VisibleMessages=content.VisibleMessages;
            r.State="migrating";r.Message="Historique copié ; préparation de son affichage dans Codex.";Save(r);
            await target.Call("thread/unsubscribe",new{threadId=r.TargetThread},token).ConfigureAwait(false);
            }
            target.Dispose();
            await migrate(a.Settings.CodexExecutable,a.Instances.Home(to),r.TargetThread,token).ConfigureAwait(false);
            // Validate the endpoint actually consumed by Desktop, not only the
            // legacy transcript endpoint which can succeed with a blank UI.
            using(var verify=connect(a.Instances.Home(to))){
                var read=await verify.Call("thread/read",new{threadId=r.TargetThread,includeTurns=false},token).ConfigureAwait(false);var copy=Choice(Json.Get(read,"thread"));
                RolloutSnapshot.RequireRollout(a.Instances.Home(to),copy.Path);var content=RolloutSnapshot.Scan(copy.Path,r.TargetThread,null,token);
                if(content.ModelDigest!=r.ModelDigest||content.ModelItems!=r.ModelItems)throw new InvalidOperationException("La conversion native a modifié le contexte de la copie. Aucune reprise automatique ; vérifiez dans Codex.");
                await VerifyVisible(verify,r.TargetThread,r.VisibleMessages,token).ConfigureAwait(false);
            }
            r.State="completed";r.Message="Historique copié et vérifié. L’original est conservé. Vous pouvez poursuivre dans le Codex destinataire.";Save(r);return r;
        }
        private static bool MatchesNativeResumeNotice(ThreadCopyRecord record,string path,RolloutSnapshot actual,CancellationToken token)
        {
            if(actual.Items!=record.HistoryItems+1)return false;
            // Codex adds this exact tagged notice when reopening an interrupted
            // turn. Accept only one final notice followed by its native abort
            // event; every original model item and visible message must match.
            var normalized=RolloutSnapshot.Scan(path,record.TargetThread,null,token,true);
            return normalized.NativeResumeNotice&&normalized.Digest==record.Digest&&normalized.Items==record.HistoryItems;
        }
        private async Task RequireNativeRecoveryIdle(AccountService accounts,DesktopInstance instance,string thread,CancellationToken token)
        {
            // Legacy copies have no thread/items/list endpoint until migrated.
            // Read their state from Desktop's sidebar instead of opening them.
            var state=accounts.Instances.Runtime.Probe(instance);
            if(!state.Running||!DesktopRuntime.SameProcess(state.DesktopPid,state.DesktopStartTicks))throw new InvalidOperationException("Ouvrez l’instance destinataire avant de vérifier la copie.");
            var channel=await NamedInstances.Connect(store,accounts,instance,Environment.CurrentDirectory,token,null,false).ConfigureAwait(false);
            var listed=await new DesktopRelayTransport().Call(channel,"list_threads",new{limit=50},token).ConfigureAwait(false);
            var row=RelayEngine.Rows(Json.Get(listed,"threads")).Concat(RelayEngine.Rows(Json.Get(listed,"pinnedThreads"))).SingleOrDefault(t=>Json.Str(Json.Get(t,"id"))==thread&&Json.Str(Json.Get(t,"kind"))=="codex"&&new[]{"","local"}.Contains(Json.Str(Json.Get(t,"hostId"))));
            if(row==null||!TransferBackend.IdleStatus(Json.Str(Json.Get(row,"status"))))throw new InvalidOperationException("La copie n’est pas confirmée au repos dans Codex. Terminez son travail avant de la vérifier.");
        }
        internal static async Task VerifyVisible(IThreadProfileRpc rpc,string thread,string[] expected,CancellationToken token)
        {
            var actual=await ReadVisible(rpc,thread,token).ConfigureAwait(false);
            if(!actual.SequenceEqual(expected??new string[0]))throw new InvalidOperationException("L’ordre ou le contenu des messages visibles diffère de la source. La copie reste à vérifier.");
        }
        internal static async Task<string[]> ReadVisible(IThreadProfileRpc rpc,string thread,CancellationToken token)
        {var rows=await ReadVisibleRows(rpc,thread,token).ConfigureAwait(false);if(rows.Any(r=>r.Unsupported))throw new InvalidOperationException("Une pièce jointe de cet historique ne peut pas encore être reproduite fidèlement dans Codex.");return rows.Select(r=>RolloutSnapshot.VisibleDigest(r.Role,r.Text,r.LocalImages,r.EmbeddedImages)).ToArray();}
        internal sealed class VisibleRow {internal string Role,Text,Turn;internal string[] LocalImages,EmbeddedImages;internal bool Unsupported;}
        private static async Task<VisibleRow[]> ReadVisibleRows(IThreadProfileRpc rpc,string thread,CancellationToken token)
        {
            var actual=new List<VisibleRow>();string cursor=null;int pages=0;
            do{
                var page=await rpc.Call("thread/items/list",new{threadId=thread,limit=10,cursor=cursor,sortDirection="asc"},token).ConfigureAwait(false);
                foreach(var row in RelayEngine.Rows(Json.Get(page,"data"))){var item=Json.Get(row,"item");string type=Json.Str(Json.Get(item,"type"));
                    if(type=="agentMessage")actual.Add(new VisibleRow{Role="assistant",Text=Json.Str(Json.Get(item,"text")),Turn=Json.Str(Json.Get(row,"turnId"))});
                    if(type=="userMessage"){var content=RelayEngine.Rows(Json.Get(item,"content")).ToArray();actual.Add(new VisibleRow{Role="user",Text=String.Join("\n",content.Where(c=>Json.Str(Json.Get(c,"type"))=="text").Select(c=>Json.Str(Json.Get(c,"text")))),LocalImages=content.Where(c=>new[]{"localImage","local_image"}.Contains(Json.Str(Json.Get(c,"type")))).Select(c=>Json.Str(Json.Get(c,"path"))).ToArray(),EmbeddedImages=content.Where(RolloutSnapshot.EmbeddedImage).Select(RolloutSnapshot.EmbeddedUrl).ToArray(),Turn=Json.Str(Json.Get(row,"turnId")),Unsupported=content.Any(c=>!new[]{"text","localImage","local_image"}.Contains(Json.Str(Json.Get(c,"type")))&&!RolloutSnapshot.EmbeddedImage(c))});}
                }
                cursor=Json.Str(Json.Get(page,"nextCursor"));if(++pages>20000)throw new InvalidOperationException("Historique trop long pour vérifier intégralement son affichage.");
            }while(cursor.Length>0);
            return actual.ToArray();
        }
        internal static async Task CompleteSnapshotDisplay(IThreadProfileRpc rpc,string thread,string snapshot,CancellationToken token)
        {
            var prepared=RolloutSnapshot.Scan(snapshot,thread,null,token);var rows=await ReadVisibleRows(rpc,thread,token).ConfigureAwait(false);var native=rows.Select(r=>RolloutSnapshot.VisibleDigest(r.Role,r.Text,r.LocalImages,r.EmbeddedImages)).ToArray();
            if(rows.Any(r=>r.Unsupported))throw new InvalidOperationException("Une pièce jointe de cet historique ne peut pas encore être reproduite fidèlement dans Codex.");
            if(prepared.VisibleMessages.SequenceEqual(native))return;
            if(prepared.VisibleMessages.Length>0){
                // Older forks can contain a partial legacy transcript alongside
                // the complete paginated transcript. Replace only display events
                // in this private snapshot; the native source UI is authoritative.
                string temp=snapshot+".display-tmp";SafeFiles.RejectLinks(temp);bool owned=false;
                try{using(var reader=new StreamReader(snapshot))using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){owned=true;using(var writer=new StreamWriter(output,new UTF8Encoding(false))){string line;while((line=reader.ReadLine())!=null){token.ThrowIfCancellationRequested();var entry=RolloutSnapshot.ReadRollout(line);string kind=Json.Str(Json.Get(Json.Get(entry,"payload"),"type"));if(Json.Str(Json.Get(entry,"type"))=="event_msg"&&(kind=="user_message"||kind=="agent_message"))continue;writer.WriteLine(line);}}}File.Replace(temp,snapshot,null);}
                finally{if(owned&&File.Exists(temp))File.Delete(temp);}
            }
            // Recent Desktop releases store display messages only in SQLite. Rebuild
            // them in the private snapshot, retaining their order and turn grouping.
            using(var writer=new StreamWriter(snapshot,true,new UTF8Encoding(false))){
                string turn=null;foreach(var row in rows){token.ThrowIfCancellationRequested();string next=String.IsNullOrEmpty(row.Turn)?"switcher-import":row.Turn;
                    if(next!=turn){if(turn!=null)writer.WriteLine(Json.Write(new{type="event_msg",timestamp=DateTime.UtcNow.ToString("o"),payload=new{type="task_complete",turn_id=turn}}));turn=next;writer.WriteLine(Json.Write(new{type="event_msg",timestamp=DateTime.UtcNow.ToString("o"),payload=new{type="task_started",turn_id=turn,model_context_window=258400}}));}
                    object display=row.Role=="user"?(object)new{type="user_message",message=row.Text,images=row.EmbeddedImages??new string[0],local_images=row.LocalImages??new string[0],text_elements=new object[0]}:new{type="agent_message",message=row.Text,phase="final_answer"};writer.WriteLine(RolloutSnapshot.DisplayLine(new{type="event_msg",timestamp=DateTime.UtcNow.ToString("o"),payload=display}));
                }
                if(turn!=null)writer.WriteLine(Json.Write(new{type="event_msg",timestamp=DateTime.UtcNow.ToString("o"),payload=new{type="task_complete",turn_id=turn}}));
            }
            if(RolloutSnapshot.Scan(snapshot,thread,null,token).ModelDigest!=prepared.ModelDigest)throw new InvalidOperationException("La reconstruction de l’affichage a changé le contexte modèle.");
        }
        public async Task Open(ThreadCopyRecord record,CancellationToken token)
        {
            if(String.IsNullOrEmpty(record.TargetThread))throw new InvalidOperationException("Aucune copie confirmée à ouvrir.");var a=Accounts();var owner=Instance(a,record.Destination);if(owner.AccountKey!=record.DestinationAccount)throw new InvalidOperationException("Le compte destinataire a changé.");
            var channel=await NamedInstances.Connect(store,a,owner,record.Folder,token,record.TargetThread,false).ConfigureAwait(false);await new DesktopRelayTransport().Call(channel,"navigate_to_codex_page",new{threadId=record.TargetThread},token).ConfigureAwait(false);
        }
    }
}
