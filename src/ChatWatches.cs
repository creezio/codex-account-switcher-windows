using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class ChatWatchEvent
    {
        public string At {get;set;}
        public string State {get;set;}
        public string Detail {get;set;}
    }
    internal sealed class ChatWatchRecord
    {
        public string Id {get;set;}
        public long Revision {get;set;}
        public string Name {get;set;}
        public string InstanceId {get;set;}
        public string AccountKey {get;set;}
        public string Home {get;set;}
        public string ThreadId {get;set;}
        public string Workspace {get;set;}
        public string CompletionFile {get;set;}
        public string CompletionProperty {get;set;}
        public string CompletionExpectedValue {get;set;}
        public int? NoProgressMinutes {get;set;}
        public bool Enabled {get;set;}
        public string CreatedUtc {get;set;}
        public string LastCheckedUtc {get;set;}
        public string LastProgressUtc {get;set;}
        public string ProgressKey {get;set;}
        public string State {get;set;}
        public string Detail {get;set;}
        public string LastTurnId {get;set;}
        public string EpisodeId {get;set;}
        public string AlertUtc {get;set;}
        public string NotifiedUtc {get;set;}
        public string AcknowledgedUtc {get;set;}
        public int ConsecutiveErrors {get;set;}
        public List<ChatWatchEvent> Events {get;set;}
    }
    internal sealed class ChatWatchObservation
    {
        public string State {get;set;}
        public string Detail {get;set;}
        public string TurnId {get;set;}
        public string ProgressKey {get;set;}
    }
    // Poll native chat metadata without inference. A finished turn is never
    // treated as a finished project unless the configured criterion confirms it.
    internal sealed class ChatWatches
    {
        internal const int IntervalSeconds=60;
        internal const int MaximumEvents=100;
        private readonly RelayStore store;
        private readonly Func<ChatWatchRecord,CancellationToken,Task<object>> reader;
        private readonly Func<DateTime> utcNow;
        private readonly Action<ChatWatchRecord> validateBinding;
        internal ChatWatches(RelayStore store,Func<ChatWatchRecord,CancellationToken,Task<object>> reader=null,Func<DateTime> utcNow=null,Action<ChatWatchRecord> validateBinding=null)
        {this.store=store;this.reader=reader??ReadNative;this.utcNow=utcNow??(()=>DateTime.UtcNow);this.validateBinding=validateBinding??ValidateBinding;}
        private DateTime Now {get{return utcNow().ToUniversalTime();}}
        private static string Record(string id){RelayStore.MessageId(id);return "chat-watch-"+id+".dpapi";}
        private static ChatWatchRecord Copy(ChatWatchRecord value){return Json.Read<ChatWatchRecord>(Json.Write(value));}
        private static string Hash(string text)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text??""))).Replace("-","").ToLowerInvariant();}
        private static bool SameTarget(ChatWatchRecord a,ChatWatchRecord b)
        {return a.InstanceId==b.InstanceId&&a.AccountKey==b.AccountKey&&a.ThreadId==b.ThreadId&&RelayStore.SamePath(a.Home,b.Home)&&RelayStore.SamePath(a.Workspace,b.Workspace);}
        private void ValidateBinding(ChatWatchRecord value)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));
            var instance=accounts.Data.Instances.SingleOrDefault(i=>!i.Archived&&i.Id==value.InstanceId);
            if(instance==null||instance.AccountKey!=value.AccountKey)throw new InvalidOperationException("Le compte permanent de la surveillance a changé.");
            NamedInstances.VerifyAccount(accounts,instance);string home=accounts.Instances.Home(instance);
            if(!String.IsNullOrEmpty(value.Home)&&!RelayStore.SamePath(home,value.Home))throw new InvalidOperationException("Le profil de la surveillance a changé.");
            value.Home=home;
        }
        private static void ValidateCore(ChatWatchRecord value,bool requireHome)
        {
            if(value==null)throw new ArgumentNullException("value");RelayStore.MessageId(value.Id);if(value.InstanceId!="local")RelayStore.MessageId(value.InstanceId);
            if(String.IsNullOrWhiteSpace(value.Name)||value.Name.Length>160||value.Name.Any(Char.IsControl))throw new InvalidOperationException("Donnez un nom de surveillance entre 1 et 160 caractères.");
            if(String.IsNullOrWhiteSpace(value.AccountKey)||value.AccountKey.Length>256||value.AccountKey.Any(Char.IsControl))throw new InvalidOperationException("Compte de surveillance invalide.");
            Guid thread;if(!Guid.TryParse(value.ThreadId,out thread))throw new InvalidOperationException("Identifiant de chat Codex invalide.");
            value.Workspace=RelayStore.WorkspacePath(value.Workspace);
            if(value.NoProgressMinutes.HasValue&&value.NoProgressMinutes.Value!=0&&(value.NoProgressMinutes.Value<5||value.NoProgressMinutes.Value>240))throw new InvalidOperationException("L’alerte sans progrès visible doit attendre entre 5 et 240 minutes, ou 0 pour la désactiver.");
            if(requireHome){
                if(String.IsNullOrWhiteSpace(value.Home)||!Path.IsPathRooted(value.Home)||value.Home.StartsWith("\\\\",StringComparison.Ordinal))throw new InvalidOperationException("Profil Codex local invalide.");
                value.Home=Path.GetFullPath(value.Home);SafeFiles.RejectLinks(value.Home);
            }
            if(!String.IsNullOrWhiteSpace(value.CompletionFile)){
                if(!Path.IsPathRooted(value.CompletionFile)||value.CompletionFile.StartsWith("\\\\",StringComparison.Ordinal))throw new InvalidOperationException("Le critère de fin doit être un fichier JSON local absolu du projet.");
                value.CompletionFile=Path.GetFullPath(value.CompletionFile);
                string prefix=value.Workspace.TrimEnd('\\','/')+Path.DirectorySeparatorChar;
                if(!value.CompletionFile.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetExtension(value.CompletionFile),".json",StringComparison.OrdinalIgnoreCase)||value.CompletionFile.Substring(2).Contains(":"))throw new InvalidOperationException("Le critère de fin doit rester dans le dossier du projet.");
                SafeFiles.RejectLinks(value.CompletionFile);
                value.CompletionProperty=String.IsNullOrWhiteSpace(value.CompletionProperty)?"completion.status":value.CompletionProperty.Trim();
                if(!Regex.IsMatch(value.CompletionProperty,@"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){0,15}\z"))throw new InvalidOperationException("Propriété du critère de fin invalide.");
                value.CompletionExpectedValue=String.IsNullOrWhiteSpace(value.CompletionExpectedValue)?"complete":value.CompletionExpectedValue.Trim();
                if(value.CompletionExpectedValue.Length>100||value.CompletionExpectedValue.Any(Char.IsControl))throw new InvalidOperationException("Valeur du critère de fin invalide.");
            }else{value.CompletionFile=null;value.CompletionProperty=null;value.CompletionExpectedValue=null;}
        }
        internal ChatWatchRecord[] List()
        {
            return Directory.EnumerateFiles(store.Root,"chat-watch-*.dpapi",SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Where(name=>Regex.IsMatch(name,@"\Achat-watch-[a-f0-9]{32}\.dpapi\z"))
                .Select(name=>Load(Path.GetFileNameWithoutExtension(name).Substring(11))).OrderByDescending(w=>w.Enabled).ThenBy(w=>w.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private ChatWatchRecord Load(string id)
        {
            var value=store.ReadRecord<ChatWatchRecord>(Record(id));
            if(value.Id!=id||value.Revision<1)throw new InvalidOperationException("Surveillance introuvable ou illisible.");
            value.Events=value.Events??new List<ChatWatchEvent>();return value;
        }
        internal ChatWatchRecord Get(string id){return Load(id);}
        internal bool HasEnabled(){return List().Any(value=>value.Enabled);}
        private void Event(ChatWatchRecord value,string state,string detail)
        {
            value.Events=value.Events??new List<ChatWatchEvent>();value.Events.Add(new ChatWatchEvent{At=Now.ToString("o"),State=state,Detail=detail});
            if(value.Events.Count>MaximumEvents)value.Events.RemoveRange(0,value.Events.Count-MaximumEvents);
        }
        private void Write(ChatWatchRecord value){value.Revision++;store.WriteRecord(Record(value.Id),value);}
        internal ChatWatchRecord Save(ChatWatchRecord input)
        {
            var value=Copy(input);value.Id=String.IsNullOrEmpty(value.Id)?Guid.NewGuid().ToString("N"):value.Id;
            ValidateCore(value,false);validateBinding(value);ValidateCore(value,true);
            using(store.Lease("chat-watch-"+value.Id)){
                if(File.Exists(Path.Combine(store.Root,Record(value.Id)))){
                    var current=Load(value.Id);
                    if(!SameTarget(current,value))throw new InvalidOperationException("Cette surveillance appartient déjà à un autre chat ou compte. Créez une nouvelle surveillance.");
                    if(value.Revision!=current.Revision)throw new InvalidOperationException("La surveillance a changé. Actualisez avant d’enregistrer.");
                    current.Name=value.Name;current.CompletionFile=value.CompletionFile;current.CompletionProperty=value.CompletionProperty;current.CompletionExpectedValue=value.CompletionExpectedValue;current.NoProgressMinutes=value.NoProgressMinutes;
                    if(current.Enabled!=value.Enabled)Enable(current,value.Enabled);
                    Event(current,"configured","Configuration de la surveillance enregistrée.");Write(current);return current;
                }
                value.Revision=0;value.CreatedUtc=Now.ToString("o");value.LastCheckedUtc=null;value.LastProgressUtc=null;value.ProgressKey=null;value.LastTurnId=null;
                value.State=value.Enabled?"waiting":"paused";value.Detail=value.Enabled?"Première vérification native en attente.":"Surveillance désactivée.";
                value.EpisodeId=Guid.NewGuid().ToString("N");value.AlertUtc=null;value.NotifiedUtc=null;value.AcknowledgedUtc=null;value.ConsecutiveErrors=0;value.Events=new List<ChatWatchEvent>();
                Event(value,value.State,value.Detail);Write(value);return value;
            }
        }
        private ChatWatchRecord Edit(string id,Action<ChatWatchRecord> change)
        {using(store.Lease("chat-watch-"+id)){var value=Load(id);change(value);Write(value);return value;}}
        private void ClearAlert(ChatWatchRecord value)
        {value.EpisodeId=Guid.NewGuid().ToString("N");value.AlertUtc=null;value.NotifiedUtc=null;value.AcknowledgedUtc=null;}
        private void Enable(ChatWatchRecord value,bool enabled)
        {
            value.Enabled=enabled;value.State=enabled?"waiting":"paused";value.Detail=enabled?"Vérification native en attente.":"Surveillance désactivée par l’utilisateur.";
            value.LastCheckedUtc=null;value.ConsecutiveErrors=0;ClearAlert(value);Event(value,value.State,value.Detail);
        }
        internal ChatWatchRecord SetEnabled(string id,bool enabled)
        {
            if(enabled){var existing=Get(id);validateBinding(existing);ValidateCore(existing,true);}
            return Edit(id,value=>{if(value.Enabled!=enabled)Enable(value,enabled);});
        }
        internal ChatWatchRecord Acknowledge(string id)
        {return Edit(id,value=>{if(!String.IsNullOrEmpty(value.AlertUtc)&&String.IsNullOrEmpty(value.AcknowledgedUtc)){value.AcknowledgedUtc=Now.ToString("o");Event(value,"acknowledged","Alerte prise en compte par l’utilisateur.");}});}
        internal ChatWatchRecord MarkNotified(string id,string episodeId)
        {return Edit(id,value=>{if(value.EpisodeId==episodeId&&!String.IsNullOrEmpty(value.AlertUtc)&&String.IsNullOrEmpty(value.NotifiedUtc)){value.NotifiedUtc=Now.ToString("o");Event(value,"notified","Notification Windows demandée par le Switcher.");}});}
        internal ChatWatchRecord ConfirmComplete(string id)
        {return Edit(id,value=>{value.Enabled=false;value.State="completed";value.Detail="Fin de mission confirmée explicitement par l’utilisateur.";ClearAlert(value);Event(value,"completed",value.Detail);});}
        private async Task<object> ReadNative(ChatWatchRecord watch,CancellationToken token)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));
            var instance=accounts.Data.Instances.SingleOrDefault(i=>!i.Archived&&i.Id==watch.InstanceId);
            if(instance==null||instance.AccountKey!=watch.AccountKey||!RelayStore.SamePath(accounts.Instances.Home(instance),watch.Home))throw new InvalidOperationException("La surveillance ne correspond plus au compte configuré.");
            // allowHeadless=false: observing a desktop must never start another
            // engine, desktop, chat or model just to discover its status.
            var channel=await NamedInstances.Connect(store,accounts,instance,watch.Workspace,token,watch.ThreadId,false).ConfigureAwait(false);
            if(channel.AccountKey!=watch.AccountKey||!RelayStore.SamePath(channel.Home,watch.Home))throw new InvalidOperationException("Le compte de la connexion a changé.");
            return await new DesktopRelayTransport().Call(channel,"read_thread",new{threadId=watch.ThreadId,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=4000},token).ConfigureAwait(false);
        }
        internal static ChatWatchObservation Classify(object snapshot,ChatWatchRecord watch,bool completionConfirmed)
        {
            DirectMessages.RequireThread(snapshot,watch.ThreadId);
            var thread=Json.Get(snapshot,"thread");var status=Json.Get(thread,"status");string kind=Json.Str(Json.Get(status,"type"));
            if(String.IsNullOrEmpty(kind))kind=Json.Str(status);
            if(!new[]{"active","running","inProgress","idle","completed","stopped","archived","failed","error","systemError"}.Contains(kind))throw new InvalidOperationException("L’état actif du chat n’est pas observable.");
            var turns=RelayEngine.Rows(Json.Get(snapshot,"turns")).ToArray();
            object turn=turns.FirstOrDefault();if(Json.Str(Json.Get(Json.Get(snapshot,"page"),"order"))=="oldest_first")turn=turns.LastOrDefault();
            string turnId=Json.Str(Json.Get(turn,"id")),turnState=Json.Str(Json.Get(turn,"status"));
            var result=new ChatWatchObservation{TurnId=String.IsNullOrEmpty(turnId)?null:turnId,ProgressKey=Hash(Json.Write(new{updated=Json.Get(thread,"updatedAt"),threadState=kind,flags=Json.Get(status,"activeFlags"),turn=turnId,state=turnState,items=Json.Get(turn,"items")}))};
            string attention=RelayEngine.AttentionFlag(snapshot);
            if(attention!=null){result.State=attention=="approval"?"waiting-approval":"waiting-input";result.Detail=attention=="approval"?"Le chat attend une approbation native dans Codex. Le travail ne peut pas continuer sans intervention.":"Le chat attend une réponse utilisateur dans Codex.";}
            else if(new[]{"active","running","inProgress"}.Contains(kind)||new[]{"running","inProgress"}.Contains(turnState)){result.State="running";result.Detail="Le chat travaille. Vérification native en lecture seule, sans consommation de modèle.";}
            else if(new[]{"failed","error","systemError"}.Contains(kind)||new[]{"failed","error","interrupted","cancelled"}.Contains(turnState)||Json.Get(turn,"error")!=null){result.State="failed";result.Detail="Le dernier tour est en échec ou a été interrompu ; la fin de mission n’est pas confirmée.";}
            else if(completionConfirmed){result.State="completed";result.Detail="Le chat est arrêté et le critère de fin de mission configuré est confirmé.";}
            else{
                result.State="stopped";result.Detail="Le chat s’est arrêté sans achèvement confirmé de la mission. Une réponse finale ou une publication partielle ne vaut pas fin de projet.";
                string final=RelayEngine.FinalText(turn);
                if(final.IndexOf("CREEZIO_OUTCOME",StringComparison.Ordinal)>=0&&Regex.IsMatch(final,@"[""']?status[""']?\s*:\s*[""']blocked[""']",RegexOptions.IgnoreCase))result.Detail="L’agent a signalé un blocage puis s’est arrêté. La mission reste inachevée ; ouvrez le chat pour lire la cause.";
            }
            return result;
        }
        private static bool CompletionConfirmed(ChatWatchRecord watch)
        {
            if(String.IsNullOrEmpty(watch.CompletionFile))return false;
            SafeFiles.RejectLinks(watch.CompletionFile);if(!File.Exists(watch.CompletionFile))return false;
            using(var stream=new FileStream(watch.CompletionFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                if(stream.Length>1048576)throw new InvalidOperationException("Le critère de fin est trop volumineux.");
                using(var text=new StreamReader(stream,Encoding.UTF8,true)){
                    object value=Json.Read<object>(text.ReadToEnd());foreach(string key in watch.CompletionProperty.Split('.'))value=Json.Get(value,key);
                    string actual=Json.Str(value);return String.Equals(actual,watch.CompletionExpectedValue,StringComparison.OrdinalIgnoreCase)||(watch.CompletionExpectedValue=="complete"&&String.Equals(actual,"completed",StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        private bool Due(ChatWatchRecord value)
        {DateTime checkedAt;return !DateTime.TryParse(value.LastCheckedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out checkedAt)||Now-checkedAt.ToUniversalTime()>=TimeSpan.FromSeconds(IntervalSeconds);}
        internal async Task CheckDue(CancellationToken token)
        {
            foreach(var value in List().Where(w=>w.Enabled&&Due(w))){token.ThrowIfCancellationRequested();await Check(value.Id,token).ConfigureAwait(false);}
        }
        internal async Task<ChatWatchRecord> Check(string id,CancellationToken token)
        {
            ChatWatchRecord before;using(store.Lease("chat-watch-"+id))before=Load(id);
            if(!before.Enabled)return before;token.ThrowIfCancellationRequested();ChatWatchObservation observation=null;bool failed=false;
            // Snapshot and network reads never hold a store lease. A concurrent
            // pause, acknowledgement or edit invalidates this revision below.
            try{
                ValidateCore(before,true);validateBinding(before);
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var read=Task.Run(()=>reader(Copy(before),deadline.Token),deadline.Token);
                    var completed=await Task.WhenAny(read,Task.Delay(TimeSpan.FromSeconds(15),token)).ConfigureAwait(false);
                    if(completed!=read){deadline.Cancel();ObserveFailure(read);throw new TimeoutException();}
                    object snapshot=await read.ConfigureAwait(false);deadline.Token.ThrowIfCancellationRequested();
                    validateBinding(before);ValidateCore(before,true);observation=Classify(snapshot,before,false);
                    if(observation.State=="stopped"&&!String.IsNullOrEmpty(before.CompletionFile)){
                        try{if(CompletionConfirmed(before))observation=Classify(snapshot,before,true);}
                        catch(Exception){observation.Detail="Le chat est arrêté ; le critère de fin est illisible ou indisponible. La fin de mission ne peut pas être confirmée.";}
                    }
                }
            }catch(OperationCanceledException){token.ThrowIfCancellationRequested();failed=true;}
            catch(Exception){failed=true;}
            token.ThrowIfCancellationRequested();
            using(store.Lease("chat-watch-"+id)){
                var current=Load(id);if(!current.Enabled||current.Revision!=before.Revision||!SameTarget(current,before))return current;
                string previous=current.State,oldTurn=current.LastTurnId;current.LastCheckedUtc=Now.ToString("o");
                if(failed){
                    if(previous!="unavailable")ClearAlert(current);
                    current.ConsecutiveErrors++;current.State="unavailable";
                    current.Detail=current.ConsecutiveErrors<2?"Première lecture native impossible. Le travail n’est plus vérifiable ; nouvel essai dans 60 secondes.":"La surveillance ne peut plus vérifier le chat après deux lectures natives. Vérifiez que la bonne instance est ouverte et toujours connectée à son compte.";
                    if(current.ConsecutiveErrors==1||previous!="unavailable")Event(current,current.State,current.Detail);
                    if(current.ConsecutiveErrors>=2&&String.IsNullOrEmpty(current.AlertUtc)){ClearAlert(current);current.AlertUtc=Now.ToString("o");Event(current,"alert",current.Detail);}
                }else{
                    current.ConsecutiveErrors=0;current.State=observation.State;current.Detail=observation.Detail;current.LastTurnId=observation.TurnId;
                    if(current.ProgressKey!=observation.ProgressKey){current.ProgressKey=observation.ProgressKey;current.LastProgressUtc=Now.ToString("o");}
                    else if(current.State=="running"&&previous!="running"&&previous!="stalled")current.LastProgressUtc=Now.ToString("o");
                    int quietMinutes=current.NoProgressMinutes??15;DateTime progress;
                    if(current.State=="running"&&quietMinutes>0&&DateTime.TryParse(current.LastProgressUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out progress)&&Now-progress.ToUniversalTime()>=TimeSpan.FromMinutes(quietMinutes)){
                        current.State="stalled";current.Detail="Aucun nouveau progrès visible depuis "+quietMinutes.ToString(CultureInfo.InvariantCulture)+" minutes. Un long test peut expliquer ce délai : ouvrez le chat pour vérifier son activité.";
                    }
                    if(previous!=current.State||oldTurn!=current.LastTurnId)Event(current,current.State,current.Detail);
                    if(current.State=="running"){
                        if(previous!="running"||oldTurn!=current.LastTurnId||!String.IsNullOrEmpty(current.AlertUtc))ClearAlert(current);
                    }else if(previous!=current.State||oldTurn!=current.LastTurnId||String.IsNullOrEmpty(current.AlertUtc)){
                        ClearAlert(current);current.AlertUtc=Now.ToString("o");Event(current,"alert",current.Detail);
                    }
                    if(current.State=="completed")current.Enabled=false;
                }
                Write(current);return current;
            }
        }
        private static void ObserveFailure(Task read)
        {read.ContinueWith(task=>{var ignored=task.Exception;},TaskContinuationOptions.OnlyOnFaulted);}
    }
}
