using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class HeadlessApproval
    {
        public string Id {get;set;} public string Method {get;set;} public string ThreadId {get;set;} public string TurnId {get;set;}public string ItemId {get;set;}
        public string AccountKey {get;set;} public string InstanceId {get;set;} public string ChannelId {get;set;} public string Home {get;set;} public string RuntimeId {get;set;}
        public string Summary {get;set;} public string BaseSummary {get;set;}public bool DetailsVerified {get;set;}public object Questions {get;set;} public object Permissions {get;set;}
        public string State {get;set;} public string Decision {get;set;} public Dictionary<string,string[]> Answers {get;set;}
        public string CreatedUtc {get;set;} public string ExpiresUtc {get;set;}
    }
    // Human decisions cross the UI/worker boundary through private DPAPI records.
    internal sealed class HeadlessApprovals
    {
        private readonly RelayStore store; private readonly TimeSpan timeout;
        internal HeadlessApprovals(RelayStore store,TimeSpan? timeout=null){this.store=store;this.timeout=timeout??TimeSpan.FromMinutes(5);if(this.timeout<=TimeSpan.Zero||this.timeout>TimeSpan.FromMinutes(5))throw new ArgumentException("Délai d'intervention invalide.");}
        private static readonly string[] Methods={"item/commandExecution/requestApproval","item/fileChange/requestApproval","item/tool/requestUserInput","item/permissions/requestApproval"};
        internal static string SafeSummary(string text)
        {
            text=Regex.Replace(text??"",@"(?i)(?:bearer\s+)[a-z0-9._~+/=-]+","Bearer [masqué]");
            text=Regex.Replace(text,@"(?i)((?:access[_-]?token|refresh[_-]?token|token|api[_-]?key|authorization|password|secret|authBearerToken)\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s,;}]+)","$1[masqué]");
            text=Regex.Replace(text,@"(?i)((?:--?|/)(?:access[_-]?token|refresh[_-]?token|token|api[_-]?key|authorization|password|secret|authBearerToken)\s+)(?:""[^""]*""|'[^']*'|[^\s,;}]+)","$1[masqué]");
            text=Regex.Replace(text,@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b","[jeton masqué]");
            text=Regex.Replace(text,@"\b(?:sk-|gh[pousr]_|github_pat_)[A-Za-z0-9_-]{12,}\b","[jeton masqué]");
            return text.Length>6000?text.Substring(0,6000)+"…":text;
        }
        private string FileName(string id){RelayStore.MessageId(id);return "headless-approval-"+id+".dpapi";}
        private bool Expired(HeadlessApproval request){DateTime due;return !DateTime.TryParse(request.ExpiresUtc,out due)||due.ToUniversalTime()<=DateTime.UtcNow;}
        private void Verify(HeadlessApproval request)
        {
            var channel=store.Channel(request.ChannelId);HeadlessRelayTransport.Validate(store,channel);
            if(channel.AccountKey!=request.AccountKey||channel.InstanceId!=request.InstanceId||!RelayStore.SamePath(channel.Home,request.Home))throw new InvalidOperationException("La destination de cette intervention a changé.");
            if(!Live(request))throw new InvalidOperationException("Le moteur qui attendait cette réponse s'est arrêté. Aucune autorisation transmise.");
        }
        private bool Live(HeadlessApproval request)
        {if(String.IsNullOrEmpty(request.RuntimeId))return true;var runtime=store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(request.AccountKey));return runtime.Running&&runtime.RuntimeId==request.RuntimeId&&DesktopRuntime.SameProcess(runtime.WorkerPid,runtime.WorkerStarted)&&(runtime.ProcessId<=0||DesktopRuntime.SameProcess(runtime.ProcessId,runtime.ProcessStartedTicks));}
        internal HeadlessApproval[] Pending()
        {
            var result=new List<HeadlessApproval>();
            foreach(string path in Directory.GetFiles(store.Root,"headless-approval-*.dpapi")){
                var item=store.ReadRecord<HeadlessApproval>(Path.GetFileName(path));
                if(item.State!="pending")continue;
                if(Expired(item)||!Live(item)){using(store.Lease("headless-approval-"+item.Id)){item=store.ReadRecord<HeadlessApproval>(FileName(item.Id));if(item.State=="pending"&&(Expired(item)||!Live(item))){item.State=Expired(item)?"expired":"cancelled";item.Decision="decline";item.Answers=null;store.WriteRecord(FileName(item.Id),item);}}continue;}
                result.Add(item);
            }
            return result.OrderBy(item=>item.CreatedUtc,StringComparer.Ordinal).ToArray();
        }
        internal void CancelOwned(string account,string home)
        {
            foreach(string path in Directory.GetFiles(store.Root,"headless-approval-*.dpapi")){
                var request=store.ReadRecord<HeadlessApproval>(Path.GetFileName(path));if(request.AccountKey!=account||!RelayStore.SamePath(request.Home??"",home)||!new[]{"pending","answered"}.Contains(request.State))continue;
                using(store.Lease("headless-approval-"+request.Id)){request=store.ReadRecord<HeadlessApproval>(FileName(request.Id));if(new[]{"pending","answered"}.Contains(request.State)){request.State="cancelled";request.Decision="decline";request.Answers=null;store.WriteRecord(FileName(request.Id),request);}}
            }
        }
        internal void Respond(string id,string decision,IDictionary<string,string[]> answers=null)
        {
            if(!new[]{"accept","decline","cancel"}.Contains(decision))throw new InvalidOperationException("Décision invalide.");
            using(store.Lease("headless-approval-"+id)){
                var request=store.ReadRecord<HeadlessApproval>(FileName(id));
                if(request.Id!=id||request.State!="pending"||Expired(request))throw new InvalidOperationException("Cette intervention a expiré ou a déjà reçu une réponse.");
                Verify(request);
                if(decision=="accept"&&request.Method=="item/fileChange/requestApproval"&&!request.DetailsVerified)throw new InvalidOperationException("Les fichiers et changements natifs ne sont pas encore connus. Attendez leur chargement ou refusez cette demande ; aucune autorisation accordée.");
                if(request.Method=="item/tool/requestUserInput"&&decision=="accept"){
                    var questions=RelayEngine.Rows(request.Questions).ToArray();
                    if(answers==null||questions.Length==0||questions.Any(q=>!answers.ContainsKey(Json.Str(Json.Get(q,"id"))))||answers.Count!=questions.Length)throw new InvalidOperationException("Répondez à toutes les questions affichées.");
                    if(answers.Any(pair=>pair.Value==null||pair.Value.Length==0||pair.Value.Length>12||pair.Value.Any(value=>String.IsNullOrWhiteSpace(value)||value.Length>8000)))throw new InvalidOperationException("Réponse utilisateur invalide.");
                    request.Answers=answers.ToDictionary(pair=>pair.Key,pair=>(string[])pair.Value.Clone(),StringComparer.Ordinal);
                }else if(answers!=null&&answers.Count>0)throw new InvalidOperationException("Cette demande n'attend pas de réponse textuelle.");
                request.Decision=decision;request.State="answered";store.WriteRecord(FileName(id),request);
            }
        }
        internal static object Response(HeadlessApproval request)
        {
            bool accepted=request.Decision=="accept";
            object response;
            if(request.Method=="item/permissions/requestApproval")response=new{permissions=accepted?(request.Permissions??new object()):new object(),scope="turn"};
            else if(request.Method=="item/tool/requestUserInput")response=new{answers=accepted&&request.Answers!=null?request.Answers.ToDictionary(pair=>pair.Key,pair=>(object)new{answers=pair.Value}):new Dictionary<string,object>()};
            else response=new{decision=accepted?"accept":request.Decision=="cancel"?"cancel":"decline"};
            return Json.Read<object>(Json.Write(response));
        }
        private static object Questions(object native)
        {
            var questions=RelayEngine.Rows(native).ToArray();if(questions.Length==0||questions.Length>24)throw new InvalidOperationException("Nombre de questions invalide.");
            var ids=new HashSet<string>(StringComparer.Ordinal);var result=new List<object>();
            foreach(var question in questions){string id=Json.Str(Json.Get(question,"id"));if(String.IsNullOrWhiteSpace(id)||id.Length>256||!ids.Add(id)||Object.Equals(Json.Get(question,"isSecret"),true))throw new InvalidOperationException("Question invalide ou secrète. Utilisez le parcours natif de connexion pour les secrets.");
                var options=RelayEngine.Rows(Json.Get(question,"options")).ToArray();if(options.Length>32)throw new InvalidOperationException("Trop de choix dans cette question.");
                result.Add(new{id=id,header=SafeSummary(Json.Str(Json.Get(question,"header"))),question=SafeSummary(Json.Str(Json.Get(question,"question"))),isOther=Json.Get(question,"isOther"),isSecret=false,options=options.Select(option=>new{label=SafeSummary(Json.Str(Json.Get(option,"label"))),description=SafeSummary(Json.Str(Json.Get(option,"description")))}).ToArray()});
            }return result.ToArray();
        }
        internal static string ChangeKey(string thread,string turn,string item){return Json.Write(new{thread=thread,turn=turn,item=item});}
        internal static string FileChangeSummary(object item)
        {
            if(Json.Str(Json.Get(item,"type"))!="fileChange"||String.IsNullOrEmpty(Json.Str(Json.Get(item,"id"))))return null;var changes=RelayEngine.Rows(Json.Get(item,"changes")).Take(21).ToArray();if(changes.Length==0||changes.Length>20||changes.Any(change=>String.IsNullOrEmpty(Json.Str(Json.Get(change,"path")))||String.IsNullOrEmpty(Json.Str(Json.Get(change,"diff")))))return null;
            var text=new StringBuilder(6000);foreach(var change in changes)foreach(string part in new[]{Json.Str(Json.Get(change,"path"))+" · "+Json.Write(Json.Get(change,"kind"))+"\n",Json.Str(Json.Get(change,"diff")),"\n\n"}){int room=5900-text.Length;if(part.Length>room){text.Append(part,0,Math.Max(0,room));text.Append("\n[Extrait tronqué]");return SafeSummary(text.ToString());}text.Append(part);}return SafeSummary(text.ToString());
        }
        internal void ObserveFileChange(string thread,string turn,object item)
        {
            string summary=FileChangeSummary(item),id=Json.Str(Json.Get(item,"id"));if(summary==null)return;
            foreach(var pending in Pending().Where(value=>value.Method=="item/fileChange/requestApproval"&&value.ThreadId==thread&&value.TurnId==turn&&value.ItemId==id&&!value.DetailsVerified))try{
                using(store.Lease("headless-approval-"+pending.Id)){var current=store.ReadRecord<HeadlessApproval>(FileName(pending.Id));if(current.State!="pending"||Expired(current)||current.ThreadId!=thread||current.TurnId!=turn||current.ItemId!=id)continue;Verify(current);current.DetailsVerified=true;current.Summary=SafeSummary("Modifications proposées (extrait natif) :\n"+summary+"\nMotif :\n"+current.BaseSummary);store.WriteRecord(FileName(current.Id),current);}
            }catch(IOException){}
        }
        internal async Task<object> Wait(RelayChannel channel,string method,object parameters,CancellationToken token,string runtimeId=null,string nativeChangeSummary=null)
        {
            if(!Methods.Contains(method))throw new InvalidOperationException("Cette demande native n'est pas prise en charge. Aucune autorisation accordée.");
            if(method=="item/fileChange/requestApproval"&&!String.IsNullOrEmpty(Json.Str(Json.Get(parameters,"grantRoot"))))throw new InvalidOperationException("Cette demande propose une autorisation de dossier pour la session entière, non prise en charge par ce moteur. Aucune autorisation accordée.");
            HeadlessRelayTransport.Validate(store,channel);
            string thread=Json.Str(Json.Get(parameters,"threadId"));var receipt=HeadlessRelayTransport.RequireOwned(store,channel,thread);string turn=Json.Str(Json.Get(parameters,"turnId"));if(!String.IsNullOrEmpty(receipt.TurnId)&&!String.IsNullOrEmpty(turn)&&receipt.TurnId!=turn)throw new InvalidOperationException("Cette intervention ne concerne pas le tour de la mission.");
            var request=new HeadlessApproval{Id=Guid.NewGuid().ToString("N"),Method=method,ThreadId=thread,TurnId=Json.Str(Json.Get(parameters,"turnId")),ItemId=Json.Str(Json.Get(parameters,"itemId")),AccountKey=channel.AccountKey,InstanceId=channel.InstanceId,ChannelId=channel.Id,Home=channel.Home,RuntimeId=runtimeId,State="pending",CreatedUtc=DateTime.UtcNow.ToString("o"),ExpiresUtc=DateTime.UtcNow.Add(timeout).ToString("o")};
            Verify(request);
            request.Questions=method=="item/tool/requestUserInput"?Questions(Json.Get(parameters,"questions")):null;
            request.Permissions=method=="item/permissions/requestApproval"?Json.Get(parameters,"permissions"):null;
            request.Summary=SafeSummary(Json.Str(Json.Get(parameters,"command"))+"\n"+Json.Str(Json.Get(parameters,"reason"))+"\n"+Json.Str(Json.Get(parameters,"cwd"))+(request.Permissions==null?"":"\n"+Json.Write(request.Permissions)));
            request.BaseSummary=request.Summary;request.DetailsVerified=method!="item/fileChange/requestApproval"||!String.IsNullOrWhiteSpace(nativeChangeSummary);if(method=="item/fileChange/requestApproval"&&request.DetailsVerified)request.Summary=SafeSummary("Modifications proposées (extrait natif) :\n"+nativeChangeSummary+"\nMotif :\n"+request.BaseSummary);
            store.WriteRecord(FileName(request.Id),request);
            try{
                while(true){
                    token.ThrowIfCancellationRequested();request=store.ReadRecord<HeadlessApproval>(FileName(request.Id));Verify(request);
                    if(request.State=="answered"){var response=Response(request);request.State="consumed";request.Answers=null;store.WriteRecord(FileName(request.Id),request);return response;}
                    if(request.State!="pending"||Expired(request)){request.State="expired";request.Decision="decline";store.WriteRecord(FileName(request.Id),request);return Response(request);}
                    await Task.Delay(200,token).ConfigureAwait(false);
                }
            }catch{
                using(store.Lease("headless-approval-"+request.Id)){request=store.ReadRecord<HeadlessApproval>(FileName(request.Id));if(request.State=="pending"||request.State=="answered"){request.State="cancelled";request.Decision="decline";request.Answers=null;store.WriteRecord(FileName(request.Id),request);}}
                throw;
            }
        }
    }
}
