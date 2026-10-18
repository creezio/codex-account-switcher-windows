using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // These pins are resolved locally. Remote input never supplies a home,
    // account, executable, permission profile, route revision or native owner.
    internal sealed class PortalConversationTarget
    {
        public bool Local {get;set;}
        public string RouteId {get;set;} public string Actor {get;set;} public string InstanceId {get;set;}
        public string ChatId {get;set;} public string ThreadId {get;set;} public string AccountKey {get;set;}
        public string Home {get;set;} public string Workspace {get;set;} public string Access {get;set;}
        public string Executable {get;set;} public string InstanceFolder {get;set;} public string GoalIssue {get;set;}
        public string Model {get;set;} public string ReasoningEffort {get;set;} public string PermissionProfile {get;set;}
        public long GrantRevision {get;set;} public long RouteRevision {get;set;}
        public long PolicyRevision {get;set;} public string SiteOrigin {get;set;}
    }
    internal sealed class PortalConversationSchedule
    {
        public int SchemaVersion {get;set;} public string Id {get;set;} public long Revision {get;set;}
        public PortalConversationTarget Target {get;set;} public string Name {get;set;} public string Prompt {get;set;}
        public string Status {get;set;} public string Frequency {get;set;} public int? IntervalMinutes {get;set;}
        public string LocalTime {get;set;} public string[] DaysOfWeek {get;set;} public string TimeZoneId {get;set;}
        public string NextRunUtc {get;set;} public string LastRunUtc {get;set;} public string LastJobId {get;set;}
        public string State {get;set;} public string Error {get;set;} public string LastMutationId {get;set;}
        public WebhookJob PendingJob {get;set;}
    }
    internal sealed class PortalConversationMutation
    {
        public string Id {get;set;} public string Fingerprint {get;set;} public string State {get;set;}
        public PortalConversationTarget Target {get;set;} public object Result {get;set;}
    }
    internal sealed class PortalConversationManagementDependencies
    {
        internal Func<string,string,string,string,PortalConversationTarget> Resolve=null;
        internal Action<PortalConversationTarget> Verify=null;
        internal Func<PortalConversationTarget,IThreadProfileRpc> GoalConnect=null;
        internal Func<PortalConversationTarget,WebhookJob,CancellationToken,Task<WebhookJob>> Enqueue=null;
        internal Func<DateTime> UtcNow=()=>DateTime.UtcNow;
        internal Func<string,TimeZoneInfo> TimeZone=id=>TimeZoneInfo.FindSystemTimeZoneById(id);
    }
    // Native goals remain native Codex state. Schedules are explicitly owned by
    // Switcher and admit ordinary durable webhook jobs; they never impersonate
    // the desktop's Automations registry or send a model request themselves.
    internal sealed class PortalConversationManagement
    {
        internal const long MaximumTokenBudget=1000000000;
        internal const int MaximumSchedules=2000,MaximumSchedulesPerChat=100;
        private readonly RelayStore store;
        private readonly string root;
        private readonly Action<string,string> require;
        private readonly PortalConversationManagementDependencies dependencies;
        private readonly bool local;
        private static readonly string[] Days={"MO","TU","WE","TH","FR","SA","SU"};
        internal PortalConversationManagement(RelayStore value,Action<string,string> authorize=null,PortalConversationManagementDependencies adapters=null)
            :this(value,authorize,adapters,false){}
        private PortalConversationManagement(RelayStore value,Action<string,string> authorize,PortalConversationManagementDependencies adapters,bool localAccess)
        {store=value;root=Path.GetDirectoryName(store.Root);require=authorize??((route,actor)=>PortalAdministration.RequireActor(store,route,actor));dependencies=adapters??new PortalConversationManagementDependencies();local=localAccess;}
        private PortalConversationManagement LocalService(){return new PortalConversationManagement(store,require,dependencies,true);}
        private DateTime Now {get{return dependencies.UtcNow().ToUniversalTime();}}
        private static T Copy<T>(T value){return Json.Read<T>(Json.Write(value));}
        private static WebhookException Bad(string message){return new WebhookException(400,message);}
        private static string Record(string id){RelayStore.MessageId(id);return "conversation-schedule-"+id+".dpapi";}
        private static string MutationRecord(string id){RelayStore.MessageId(id);return "conversation-mutation-"+id+".dpapi";}
        private static bool NativeId(string id){return Regex.IsMatch(id??"",@"\A[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-7[a-fA-F0-9]{3}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\z");}
        private static bool Same(string first,string second){return !String.IsNullOrEmpty(first)&&!String.IsNullOrEmpty(second)&&RelayStore.SamePath(first,second);}
        private static bool SameTarget(PortalConversationTarget a,PortalConversationTarget b)
        {return a!=null&&b!=null&&a.Local==b.Local&&a.RouteId==b.RouteId&&a.Actor==b.Actor&&a.InstanceId==b.InstanceId&&a.ChatId==b.ChatId&&a.ThreadId==b.ThreadId&&a.AccountKey==b.AccountKey&&Same(a.Home,b.Home)&&Same(a.Workspace,b.Workspace)&&a.GrantRevision==b.GrantRevision&&a.RouteRevision==b.RouteRevision&&a.PolicyRevision==b.PolicyRevision&&a.SiteOrigin==b.SiteOrigin&&a.Access==b.Access&&a.Model==b.Model&&a.ReasoningEffort==b.ReasoningEffort&&a.PermissionProfile==b.PermissionProfile;}
        private static bool SameScheduleOwner(PortalConversationTarget a,PortalConversationTarget b)
        {return a!=null&&b!=null&&a.Local==b.Local&&a.RouteId==b.RouteId&&a.InstanceId==b.InstanceId&&a.ChatId==b.ChatId&&a.ThreadId==b.ThreadId&&a.AccountKey==b.AccountKey&&Same(a.Home,b.Home)&&Same(a.Workspace,b.Workspace);}
        private PortalConversationTarget Resolve(string route,string actor,string instanceId,string chatId)
        {
            if(local)return ResolveLocal(instanceId,chatId);
            RelayStore.MessageId(route);if(instanceId!="local")RelayStore.MessageId(instanceId);
            if(!NativeId(chatId)&&!Regex.IsMatch(chatId??"",@"\A[a-f0-9]{32}\z"))throw Bad("Identifiant de discussion invalide.");
            require(route,actor);
            if(dependencies.Resolve!=null){var supplied=dependencies.Resolve(route,actor,instanceId,chatId);if(supplied==null||supplied.Local||supplied.RouteId!=route||supplied.Actor!=actor||supplied.InstanceId!=instanceId||supplied.ChatId!=chatId)throw new WebhookException(403,"Le transport ne correspond pas au scope d'administration.");return supplied;}
            var config=Portal.Config(store,route);var policy=PortalAdministration.Policy(store);var grant=config.Instances.SingleOrDefault(g=>g.InstanceId==instanceId);
            if(!config.Enabled||config.RootRouteId!=route||grant==null)throw new WebhookException(404,"Cette instance ne fait plus partie du portail.");
            var accounts=new AccountService(root);var instance=NamedInstances.Resolve(accounts,instanceId);NamedInstances.VerifyAccount(accounts,instance);
            if(instance.AccountKey!=grant.AccountKey||!Same(accounts.Instances.Home(instance),grant.Home))throw new WebhookException(409,"Le compte permanent de cette instance a change.");
            var selected=Webhooks.Route(store,route);if(!selected.Enabled)throw new WebhookException(403,"Ce portail est desactive.");
            string thread=chatId;
            if(NativeId(chatId)){if(!config.IncludeExisting)throw new WebhookException(404,"L'acces aux discussions existantes est desactive.");}
            else{
                var job=Webhooks.Job(store,chatId);var pin=job.RouteSnapshot;
                if(job.RouteId!=route||job.PortalInstanceId!=instanceId||job.PortalRevision<=0||!String.IsNullOrEmpty(job.PortalChatId)||!String.IsNullOrEmpty(job.ConversationId)||pin==null||pin.InstanceId!=instanceId||pin.AccountKey!=grant.AccountKey||!Same(pin.Home,grant.Home))throw new WebhookException(404,"Discussion introuvable dans cette instance.");
                thread=job.ThreadId;if(!NativeId(thread)||!store.Exists(job.Id))throw new WebhookException(409,"Le recu natif de creation est encore attendu.");
                var receipt=store.Message(job.Id);if(!receipt.OperatorOrigin||!receipt.NewConversation||receipt.WebhookJobId!=job.Id||receipt.TargetThreadId!=thread||receipt.TargetAccountKey!=grant.AccountKey||!Same(receipt.TargetHome,grant.Home))throw new WebhookException(409,"Le recu de cette discussion n'est pas confirme.");
            }
            string workspace=WebhookChats.NativeWorkspace(grant.Home,thread);
            if(grant.Access=="headless"){
                var channel=store.Channels().SingleOrDefault(c=>c.Enabled&&c.InstanceId==instanceId&&AgentProviders.Kind(c)=="codex-headless"&&c.AccountKey==grant.AccountKey&&Same(c.Home,grant.Home)&&Same(c.Workspace,workspace));
                if(channel==null)throw new WebhookException(409,"Cette discussion sans fenetre n'appartient pas a la connexion autorisee.");HeadlessRelayTransport.RequireOwned(store,channel,thread);
            }
            string issue=null;var state=accounts.Instances.Runtime.Probe(instance);
            if(instance.IsLocal)issue="La gestion native des objectifs de la session habituelle exige un canal proprietaire compatible.";
            else if(state==null||state.Running||state.Phase=="unknown"||state.Phase=="starting"||state.Phase=="stopping")issue="Fermez cette instance Codex avant de gerer son objectif natif ; aucun second moteur n'est demarre.";
            else if(String.IsNullOrWhiteSpace(accounts.Settings.CodexExecutable)||!File.Exists(accounts.Settings.CodexExecutable))issue="L'executable Codex natif n'est pas disponible.";
            var runtime=store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(grant.AccountKey));
            if(runtime.Running&&Same(runtime.Home,grant.Home)&&DesktopRuntime.SameProcess(runtime.WorkerPid,runtime.WorkerStarted))issue="Un moteur Codex utilise deja ce profil. Attendez son arret avant de gerer l'objectif.";
            return new PortalConversationTarget{RouteId=route,Actor=actor,InstanceId=instanceId,ChatId=chatId,ThreadId=thread,AccountKey=grant.AccountKey,Home=grant.Home,Workspace=workspace,Access=grant.Access,Executable=accounts.Settings.CodexExecutable,InstanceFolder=instance.IsLocal?null:InstancePaths.Folder(root,instanceId),GoalIssue=issue,GrantRevision=grant.AuthorizationRevision>0?grant.AuthorizationRevision:config.Revision,RouteRevision=selected.Revision,PolicyRevision=policy.Revision,SiteOrigin=policy.SiteOrigin,Model=grant.Model,ReasoningEffort=grant.ReasoningEffort,PermissionProfile=grant.PermissionProfile};
        }
        private PortalConversationTarget ResolveLocal(string instanceId,string chatId)
        {
            if(instanceId!="local")RelayStore.MessageId(instanceId);if(!NativeId(chatId))throw Bad("Choisissez une discussion Codex native existante.");
            if(dependencies.Resolve!=null){var supplied=dependencies.Resolve(null,null,instanceId,chatId);if(!supplied.Local)throw new InvalidOperationException("Le transport local de test ne porte pas son scope local.");return supplied;}
            var accounts=new AccountService(root);var instance=NamedInstances.Resolve(accounts,instanceId);NamedInstances.VerifyAccount(accounts,instance);
            string home=accounts.Instances.Home(instance),workspace=WebhookChats.NativeWorkspace(home,chatId);var state=accounts.Instances.Runtime.Probe(instance);
            string issue=instance.IsLocal?"Le canal proprietaire de la session habituelle ne permet pas encore de gerer les objectifs.":state==null||state.Running||state.Phase=="unknown"||state.Phase=="starting"||state.Phase=="stopping"?"Fermez cette instance avant de gerer son objectif natif ; aucun second moteur n'est demarre.":!File.Exists(accounts.Settings.CodexExecutable)?"L'executable natif Codex est indisponible.":null;
            var runtime=store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(instance.AccountKey));if(runtime.Running&&Same(runtime.Home,home)&&DesktopRuntime.SameProcess(runtime.WorkerPid,runtime.WorkerStarted))issue="Le moteur de cette instance est encore actif. Attendez son arret pour gerer l'objectif.";
            return new PortalConversationTarget{Local=true,InstanceId=instanceId,ChatId=chatId,ThreadId=chatId,AccountKey=instance.AccountKey,Home=home,Workspace=workspace,Access="local",Executable=accounts.Settings.CodexExecutable,InstanceFolder=instance.IsLocal?null:InstancePaths.Folder(root,instanceId),GoalIssue=issue,PermissionProfile="inherit"};
        }
        private void Verify(PortalConversationTarget target)
        {
            if(!target.Local)require(target.RouteId,target.Actor);
            if(dependencies.Verify!=null){dependencies.Verify(target);return;}
            // Probe while we hold InstanceLease can report starting; only the
            // stable identity/grant pins are compared during our own metadata RPC.
            var current=target.Local?ResolveLocal(target.InstanceId,target.ChatId):Resolve(target.RouteId,target.Actor,target.InstanceId,target.ChatId);
            if(!SameTarget(target,current))throw new WebhookException(409,"L'autorisation ou l'identite de cette discussion a change.");
        }
        private static Dictionary<string,object> Body(object value,string[] allowed)
        {
            var result=Json.Obj(value);if(result.Count==0||result.Keys.Any(k=>!allowed.Contains(k,StringComparer.Ordinal)))throw Bad("Champs de gestion de discussion invalides.");return result;
        }
        private static string Text(object value,string name,int maximum,bool multiline=false)
        {
            var text=value as string;if(String.IsNullOrWhiteSpace(text)||text.Length>maximum||text.Any(c=>Char.IsControl(c)&&(!multiline||(c!='\n'&&c!='\r'&&c!='\t'))))throw Bad(name+" est vide ou trop long.");return text;
        }
        private static long Number(object value,string name,long minimum,long maximum)
        {
            if(value==null||value is bool||value is string)throw Bad(name+" doit etre un entier.");decimal number;
            try{number=Convert.ToDecimal(value,CultureInfo.InvariantCulture);}catch(Exception e){if(!(e is FormatException)&&!(e is OverflowException)&&!(e is InvalidCastException))throw;throw Bad(name+" doit etre un entier.");}
            if(number!=Decimal.Truncate(number)||number<minimum||number>maximum)throw Bad(name+" est hors limites.");return (long)number;
        }
        private static DateTime Utc(string value)
        {DateTime result;if(!DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out result)||result.Kind!=DateTimeKind.Utc)throw new InvalidOperationException("Horodatage de planification invalide.");return result;}
        private static object GoalProjection(object reply,string thread)
        {
            var goal=Json.Get(reply,"goal");if(goal==null)return null;
            if(Json.Str(Json.Get(goal,"threadId"))!=thread)throw new InvalidOperationException("L'objectif natif ne correspond pas a la discussion.");
            string objective=Text(Json.Get(goal,"objective"),"L'objectif",4000,true),status=Json.Str(Json.Get(goal,"status"));
            if(!new[]{"active","paused","blocked","usageLimited","budgetLimited","complete"}.Contains(status))throw new InvalidOperationException("Etat d'objectif natif non reconnu.");
            long? budget=Json.Get(goal,"tokenBudget")==null?(long?)null:Number(Json.Get(goal,"tokenBudget"),"Budget natif",1,9007199254740991L);
            var data=new{objective=objective,status=status,tokenBudget=budget,tokensUsed=Number(Json.Get(goal,"tokensUsed"),"Utilisation native",0,9007199254740991L),timeUsedSeconds=Number(Json.Get(goal,"timeUsedSeconds"),"Duree native",0,9007199254740991L),createdAt=Number(Json.Get(goal,"createdAt"),"Date native",0,9007199254740991L),updatedAt=Number(Json.Get(goal,"updatedAt"),"Date native",0,9007199254740991L)};
            var result=Json.Obj(Json.Read<object>(Json.Write(data)));result["version"]=Webhooks.Hash(Json.Write(data));return result;
        }
        private object WithGoalRpc(PortalConversationTarget target,Func<IThreadProfileRpc,CancellationToken,object> action)
        {
            if(target.GoalIssue!=null)throw new WebhookException(409,target.GoalIssue);
            using(var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20)))using(new InstanceLease(target.InstanceFolder))
            using(HeadlessProcessWire.AcquireProfileLease(target.Home,stop.Token)){
                Verify(target);
                if(dependencies.Resolve==null){var current=target.Local?ResolveLocal(target.InstanceId,target.ChatId):Resolve(target.RouteId,target.Actor,target.InstanceId,target.ChatId);if(current.GoalIssue!=null||!Same(current.Executable,target.Executable))throw new WebhookException(409,current.GoalIssue??"L'executable natif de cette instance a change.");}
                using(var rpc=dependencies.GoalConnect==null?new ThreadProfileRpc(target.Executable,target.Home):dependencies.GoalConnect(target)){
                    var metadata=rpc.Call("thread/read",new{threadId=target.ThreadId,includeTurns=false},stop.Token).GetAwaiter().GetResult();
                    DirectMessages.RequireThread(metadata,target.ThreadId);
                    if(!Same(RolloutSnapshot.NativePath(Json.Str(Json.Get(Json.Get(metadata,"thread"),"cwd"))),target.Workspace))throw new WebhookException(409,"Le dossier natif de cette discussion a change.");
                    var result=action(rpc,stop.Token);Verify(target);return result;
                }
            }
        }
        private object ReadGoal(PortalConversationTarget target)
        {return WithGoalRpc(target,(rpc,token)=>GoalProjection(rpc.Call("thread/goal/get",new{threadId=target.ThreadId},token).GetAwaiter().GetResult(),target.ThreadId));}
        private PortalConversationSchedule[] All()
        {
            var paths=Directory.EnumerateFiles(store.Root,"conversation-schedule-*.dpapi",SearchOption.TopDirectoryOnly).Where(p=>Regex.IsMatch(Path.GetFileName(p),@"\Aconversation-schedule-[a-f0-9]{32}\.dpapi\z")).Take(MaximumSchedules+1).ToArray();
            if(paths.Length>MaximumSchedules)throw new InvalidOperationException("Le nombre de planifications depasse la limite locale.");
            return paths.Where(p=>Regex.IsMatch(Path.GetFileName(p),@"\Aconversation-schedule-[a-f0-9]{32}\.dpapi\z")).Select(p=>Load(Path.GetFileNameWithoutExtension(p).Substring(22))).ToArray();
        }
        private PortalConversationSchedule Load(string id)
        {var value=store.ReadRecord<PortalConversationSchedule>(Record(id));if(value.SchemaVersion!=1||value.Id!=id||value.Revision<1||value.Target==null)throw new WebhookException(404,"Planification introuvable.");return value;}
        private static object ScheduleProjection(PortalConversationSchedule row)
        {return new{id=row.Id,revision=row.Revision,name=row.Name,prompt=row.Prompt,status=row.Status,frequency=row.Frequency,intervalMinutes=row.IntervalMinutes,localTime=row.LocalTime,daysOfWeek=row.DaysOfWeek??new string[0],timeZoneId=row.TimeZoneId,nextRunUtc=row.NextRunUtc,lastRunUtc=row.LastRunUtc,lastJobId=row.LastJobId,state=row.State,error=row.Error};}
        private object Snapshot(PortalConversationTarget target,object knownGoal=null,bool supplied=false,string suppliedIssue=null)
        {
            object goal=knownGoal;string issue=suppliedIssue??target.GoalIssue;
            if(!supplied&&issue==null)try{goal=ReadGoal(target);}catch(InvalidOperationException){issue="L'objectif natif n'est pas disponible avec l'etat ou la version actuelle de Codex.";}catch(IOException){issue="Le canal natif de l'objectif est occupe ou indisponible.";}catch(OperationCanceledException){issue="La lecture native de l'objectif a depasse son delai.";}
            Verify(target);
            return new{instanceId=target.InstanceId,chatId=target.ChatId,threadId=target.ThreadId,capabilities=new{canReadGoal=issue==null,canWriteGoal=issue==null,canManageSchedules=true,scheduleEngine="switcher",timeZoneId=TimeZoneInfo.Local.Id,goalIssue=issue},goal=goal,schedules=All().Where(s=>s.Status!="DELETED"&&s.Target.Local==target.Local&&s.Target.RouteId==target.RouteId&&s.Target.InstanceId==target.InstanceId&&s.Target.ChatId==target.ChatId&&s.Target.AccountKey==target.AccountKey&&Same(s.Target.Home,target.Home)).Select(ScheduleProjection).ToArray()};
        }
        internal object Read(string route,string actor,string instance,string chat)
        {return Snapshot(Resolve(route,actor,instance,chat));}
        internal object ReadLocal(string instance,string chat){return LocalService().Read(null,null,instance,chat);}
        internal object GoalLocal(string instance,string chat,string key,object input){return LocalService().Goal(null,null,instance,chat,key,input);}
        internal object SaveScheduleLocal(string instance,string chat,string key,object input){return LocalService().SaveSchedule(null,null,instance,chat,key,input);}
        internal object DeleteScheduleLocal(string instance,string chat,string id,string key,object input){return LocalService().DeleteSchedule(null,null,instance,chat,id,key,input);}
        internal object ListChatsLocal(string instanceId)
        {
            if(instanceId!="local")RelayStore.MessageId(instanceId);var accounts=new AccountService(root);var instance=NamedInstances.Resolve(accounts,instanceId);NamedInstances.VerifyAccount(accounts,instance);
            string home=accounts.Instances.Home(instance),index=Path.Combine(home,"session_index.jsonl");var rows=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);bool truncated=false;
            SafeFiles.RejectLinks(home);SafeFiles.RejectLinks(index);
            if(File.Exists(index))using(var file=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                file.Position=Math.Max(0,file.Length-262144);truncated=file.Position>0;using(var reader=new StreamReader(file)){if(file.Position>0)reader.ReadLine();string line;int count=0;
                    while((line=reader.ReadLine())!=null){if(++count>2000){truncated=true;break;}if(line.Length>8192)continue;
                        try{var row=Json.Read<object>(line);string id=Json.Str(Json.Get(row,"id"));if(!NativeId(id))continue;
                            string name=Json.Str(Json.Get(row,"thread_name"));if(name.Length==0)name=Json.Str(Json.Get(row,"title"));rows[id]=new{id=id,title=WebhookChats.Safe(name.Length==0?id:name,120),updatedUtc=WebhookChats.Safe(Json.Str(Json.Get(row,"updated_at")),80)};
                        }catch(ArgumentException){}
                    }
                }
            }
            var verified=new List<object>();foreach(var row in rows.Values.Reverse().Take(101)){
                try{WebhookChats.NativeWorkspace(home,Json.Str(Json.Get(Json.Read<object>(Json.Write(row)),"id")));verified.Add(row);}catch(InvalidOperationException){}if(verified.Count==100){truncated=true;break;}
            }
            var current=new AccountService(root);var selected=NamedInstances.Resolve(current,instanceId);NamedInstances.VerifyAccount(current,selected);if(selected.AccountKey!=instance.AccountKey||!Same(current.Instances.Home(selected),home))throw new WebhookException(409,"Le compte de cette liste a change.");
            return new{chats=verified.ToArray(),truncated=truncated};
        }
        private string OperationId(PortalConversationTarget target,string key,string kind)
        {
            if(String.IsNullOrWhiteSpace(key)||key.Length>128||key.Any(c=>c<33||c>126))throw Bad("Idempotency-Key est obligatoire (1 a 128 caracteres ASCII visibles).");
            return Webhooks.Hash((target.Local?"local":"portal")+"\n"+target.RouteId+"\n"+target.Actor+"\n"+target.InstanceId+"\n"+target.ChatId+"\n"+kind+"\n"+key).Substring(0,32);
        }
        private object Mutate(PortalConversationTarget target,string key,string kind,object input,Func<string,object> change)
        {
            string id=OperationId(target,key,kind),fingerprint=Webhooks.Hash(Json.Write(input));
            using(store.Lease("conversation-mutation-"+id)){
                string path=Path.Combine(store.Root,MutationRecord(id));
                if(File.Exists(path)){
                    var previous=store.ReadRecord<PortalConversationMutation>(MutationRecord(id));
                    if(previous.Id!=id||previous.Fingerprint!=fingerprint||!SameTarget(previous.Target,target))throw new WebhookException(409,"Cette cle d'idempotence correspond a une autre demande.");
                    Verify(target);if(previous.State=="done"){string issue=Json.Str(Json.Get(previous.Result,"goalIssue"));return Snapshot(target,Json.Get(previous.Result,"goal"),true,issue.Length==0?null:issue);}
                    throw new WebhookException(409,"La reponse de cette modification n'est pas confirmee. Actualisez son etat avant une nouvelle action explicite ; aucun renvoi automatique.");
                }
                Verify(target);var operation=new PortalConversationMutation{Id=id,Fingerprint=fingerprint,Target=Copy(target),State="prepared"};store.WriteRecord(MutationRecord(id),operation);
                // Mark uncertain before crossing any native or persistent write
                // boundary. A lost response is never replayed by the service.
                operation.State="uncertain";store.WriteRecord(MutationRecord(id),operation);
                var result=change(id);Verify(target);operation.State="done";
                // Do not duplicate every schedule's prompt into every receipt.
                // Only the native outcome is retained; catalog rows are read
                // afresh without inference when an idempotent receipt is read.
                var view=Json.Read<object>(Json.Write(result));operation.Result=new Dictionary<string,object>{{"goal",Json.Get(view,"goal")},{"goalIssue",Json.Get(Json.Get(view,"capabilities"),"goalIssue")}};
                store.WriteRecord(MutationRecord(id),operation);return result;
            }
        }
        internal object Goal(string route,string actor,string instance,string chat,string key,object input)
        {
            var body=Body(input,new[]{"action","expectedVersion","objective","status","tokenBudget"});string action=Json.Str(Json.Get(body,"action"));
            if(action!="set"&&action!="clear")throw Bad("Action d'objectif invalide.");
            if(!body.ContainsKey("expectedVersion")||!(body["expectedVersion"] is string)||!Regex.IsMatch((string)body["expectedVersion"],@"\A(?:[a-f0-9]{64})?\z"))throw Bad("La version attendue de l'objectif est obligatoire.");
            var args=new Dictionary<string,object>{{"origin","user"}};
            if(action=="clear"&&body.Keys.Any(k=>k!="action"&&k!="expectedVersion"))throw Bad("Effacer l'objectif ne prend pas de nouveaux parametres.");
            if(body.ContainsKey("objective"))args["objective"]=Text(body["objective"],"L'objectif",4000,true);
            if(body.ContainsKey("status")){string state=Json.Str(body["status"]);if(!new[]{"active","paused","blocked","complete"}.Contains(state))throw Bad("Transition d'objectif reservee au systeme ou invalide.");args["status"]=state;}
            if(body.ContainsKey("tokenBudget"))args["tokenBudget"]=body["tokenBudget"]==null?null:(object)Number(body["tokenBudget"],"Budget",1,MaximumTokenBudget);
            if(action=="set"&&args.Count==1)throw Bad("Indiquez au moins une modification d'objectif.");
            var target=Resolve(route,actor,instance,chat);args["threadId"]=target.ThreadId;
            using(store.Lease("conversation-goal-"+Webhooks.Hash(target.Home+"\n"+target.ThreadId).Substring(0,32)))
            return Mutate(target,key,"goal",input,id=>WithGoalRpc(target,(rpc,token)=>{
                var current=GoalProjection(rpc.Call("thread/goal/get",new{threadId=target.ThreadId},token).GetAwaiter().GetResult(),target.ThreadId);
                if(Json.Str(Json.Get(current,"version"))!=(string)body["expectedVersion"])throw new WebhookException(409,"L'objectif a change. Actualisez avant de l'enregistrer.");
                Verify(target);object result=rpc.Call(action=="clear"?"thread/goal/clear":"thread/goal/set",args,token).GetAwaiter().GetResult();
                object goal=action=="clear"?null:GoalProjection(result,target.ThreadId);return Snapshot(target,goal,true);
            }));
        }
        private PortalConversationSchedule ParseSchedule(object input,PortalConversationTarget target,string id)
        {
            var body=Body(input,new[]{"id","revision","name","prompt","status","frequency","intervalMinutes","localTime","daysOfWeek","timeZoneId"});
            string status=Json.Str(Json.Get(body,"status")),frequency=Json.Str(Json.Get(body,"frequency"));
            if(status!="ACTIVE"&&status!="PAUSED")throw Bad("Choisissez explicitement ACTIVE ou PAUSED.");
            if(!new[]{"interval","daily","weekly"}.Contains(frequency))throw Bad("Frequence de planification invalide.");
            var result=new PortalConversationSchedule{SchemaVersion=1,Id=id,Target=Copy(target),Name=Text(Json.Get(body,"name"),"Le nom",120),Prompt=Text(Json.Get(body,"prompt"),"La consigne",20000,true),Status=status,Frequency=frequency,TimeZoneId=Text(Json.Get(body,"timeZoneId"),"Le fuseau",160),DaysOfWeek=new string[0]};
            try{dependencies.TimeZone(result.TimeZoneId);}catch(TimeZoneNotFoundException){throw Bad("Fuseau horaire inconnu sur ce PC.");}catch(InvalidTimeZoneException){throw Bad("Fuseau horaire invalide.");}
            if(frequency=="interval"){
                result.IntervalMinutes=(int)Number(Json.Get(body,"intervalMinutes"),"Intervalle",5,10080);
                if(Json.Get(body,"localTime")!=null||RelayEngine.Rows(Json.Get(body,"daysOfWeek")).Any())throw Bad("Un intervalle ne prend pas d'heure locale ni de jours.");
            }else{
                result.LocalTime=Json.Str(Json.Get(body,"localTime"));if(!Regex.IsMatch(result.LocalTime,@"\A(?:[01][0-9]|2[0-3]):[0-5][0-9]\z"))throw Bad("Heure locale attendue au format HH:mm.");
                if(Json.Get(body,"intervalMinutes")!=null)throw Bad("Une heure locale ne prend pas d'intervalle.");
                var days=RelayEngine.Rows(Json.Get(body,"daysOfWeek")).Select(Json.Str).ToArray();
                if(frequency=="daily"&&days.Length>0||frequency=="weekly"&&(days.Length==0||days.Length>7||days.Distinct().Count()!=days.Length||days.Any(d=>!Days.Contains(d))))throw Bad("Choisissez des jours distincts de MO a SU.");
                result.DaysOfWeek=Days.Where(days.Contains).ToArray();
            }
            return result;
        }
        internal DateTime Next(PortalConversationSchedule schedule,DateTime after)
        {
            after=after.ToUniversalTime();if(schedule.Frequency=="interval")return after.AddMinutes(schedule.IntervalMinutes.Value);
            var zone=dependencies.TimeZone(schedule.TimeZoneId);DateTime local=TimeZoneInfo.ConvertTimeFromUtc(after,zone).Date;TimeSpan time=TimeSpan.ParseExact(schedule.LocalTime,"hh\\:mm",CultureInfo.InvariantCulture);
            for(int n=0;n<370;n++){
                DateTime day=local.AddDays(n),candidate=DateTime.SpecifyKind(day+time,DateTimeKind.Unspecified);
                if(schedule.Frequency=="weekly"&&!schedule.DaysOfWeek.Contains(Days[((int)day.DayOfWeek+6)%7]))continue;
                if(zone.IsInvalidTime(candidate))continue;
                // One occurrence per local date, including a repeated DST hour.
                DateTime utc=zone.IsAmbiguousTime(candidate)?new DateTimeOffset(candidate,zone.GetAmbiguousTimeOffsets(candidate).Max()).UtcDateTime:TimeZoneInfo.ConvertTimeToUtc(candidate,zone);
                if(utc>after)return utc;
            }
            throw new InvalidOperationException("Aucune prochaine occurrence valide dans ce fuseau.");
        }
        internal object SaveSchedule(string route,string actor,string instance,string chat,string key,object input)
        {
            var inputFields=Json.Obj(input);if(inputFields.ContainsKey("id")&&inputFields["id"]!=null&&!(inputFields["id"] is string))throw Bad("L'identifiant de planification doit etre du texte.");
            var target=Resolve(route,actor,instance,chat);string supplied=Json.Str(Json.Get(input,"id"));
            if(supplied.Length>0)RelayStore.MessageId(supplied);
            string id=supplied.Length>0?supplied:OperationId(target,key,"schedule-create");var parsed=ParseSchedule(input,target,id);
            return Mutate(target,key,"schedule-save",input,operation=>{
                using(store.Lease("conversation-schedules-capacity"))using(store.Lease("conversation-schedule-"+id)){
                    bool exists=File.Exists(Path.Combine(store.Root,Record(id)));PortalConversationSchedule previous=null;
                    if(exists){previous=Load(id);if(!SameScheduleOwner(previous.Target,target)||previous.Status=="DELETED")throw new WebhookException(404,"Planification introuvable dans ce chat.");
                        if(supplied.Length==0||Number(Json.Get(input,"revision"),"Revision",1,Int64.MaxValue)!=previous.Revision)throw new WebhookException(409,"La planification a change. Actualisez avant d'enregistrer.");
                        if(previous.PendingJob!=null&&parsed.Status!="PAUSED"&&previous.Status!="PAUSED")throw new WebhookException(409,"Une occurrence est deja preparee. Mettez la planification en pause avant de la modifier.");
                    }else{
                        if(supplied.Length>0)throw new WebhookException(404,"Planification introuvable.");var all=All();
                        if(all.Length>=MaximumSchedules||all.Count(s=>s.Status!="DELETED"&&s.Target.Local==target.Local&&s.Target.RouteId==target.RouteId&&s.Target.InstanceId==instance&&s.Target.ChatId==chat)>=MaximumSchedulesPerChat)throw new WebhookException(429,"La limite de planifications est atteinte.");
                    }
                    Verify(target);parsed.Revision=previous==null?1:checked(previous.Revision+1);parsed.LastMutationId=operation;
                    parsed.LastRunUtc=previous==null?null:previous.LastRunUtc;parsed.LastJobId=previous==null?null:previous.LastJobId;
                    if(previous!=null&&previous.PendingJob!=null){
                        WebhookJob admitted=PreparedAdmission(previous.Target,previous.PendingJob);
                        if(admitted!=null){parsed.LastJobId=admitted.Id;parsed.LastRunUtc=previous.LastRunUtc??Now.ToString("o");}
                        else if(parsed.Status=="PAUSED")parsed.PendingJob=previous.PendingJob;
                        // Explicit resume after a pause may replace an occurrence
                        // proved never admitted. An existing receipt is retained.
                    }
                    bool sameClock=previous!=null&&previous.Status=="ACTIVE"&&parsed.Status=="ACTIVE"&&previous.Frequency==parsed.Frequency&&previous.IntervalMinutes==parsed.IntervalMinutes&&previous.LocalTime==parsed.LocalTime&&previous.TimeZoneId==parsed.TimeZoneId&&(previous.DaysOfWeek??new string[0]).SequenceEqual(parsed.DaysOfWeek);
                    parsed.NextRunUtc=parsed.Status=="ACTIVE"?(sameClock?previous.NextRunUtc:Next(parsed,Now).ToString("o")):null;parsed.State=parsed.Status=="ACTIVE"?"scheduled":"paused";
                    store.WriteRecord(Record(id),parsed);
                }
                return Snapshot(target);
            });
        }
        internal object DeleteSchedule(string route,string actor,string instance,string chat,string id,string key,object input)
        {
            RelayStore.MessageId(id);var body=Body(input,new[]{"revision"});long revision=Number(Json.Get(body,"revision"),"Revision",1,Int64.MaxValue);var target=Resolve(route,actor,instance,chat);
            return Mutate(target,key,"schedule-delete-"+id,input,operation=>{
                using(store.Lease("conversation-schedule-"+id)){
                    var current=Load(id);if(!SameScheduleOwner(current.Target,target))throw new WebhookException(404,"Planification introuvable dans ce chat.");
                    if(current.Revision!=revision)throw new WebhookException(409,"La planification a change. Actualisez avant de supprimer.");
                    Verify(target);current.Status="DELETED";current.State="deleted";current.NextRunUtc=null;current.LastMutationId=operation;current.Revision=checked(current.Revision+1);store.WriteRecord(Record(id),current);
                }
                return Snapshot(target);
            });
        }
        internal bool HasEnabled(){return All().Any(s=>s.Status=="ACTIVE");}
        private static bool LocalPending(string state)
        {return new[]{"queued","sending","waiting","running","children","attention","uncertain"}.Contains(state);}
        private WebhookJob Template(PortalConversationSchedule row)
        {
            var t=row.Target;var route=t.Local?new WebhookRoute():Copy(Webhooks.Route(store,t.RouteId));route.Id=t.Local?null:Webhooks.ExecutionRouteId(t.RouteId,t.InstanceId,null);route.SecretHash=null;route.InstanceId=t.InstanceId;route.AccountKey=t.AccountKey;route.Home=t.Home;route.Workspace=t.Workspace;route.ThreadId=t.ThreadId;route.Access=t.Access;route.Model=t.Model;route.ReasoningEffort=t.ReasoningEffort;route.PermissionProfile=t.PermissionProfile;
            string occurrence=Webhooks.Hash(row.Id+"\n"+row.Revision.ToString(CultureInfo.InvariantCulture)+"\n"+row.NextRunUtc).Substring(0,32);
            string title=row.Name.Length>120?row.Name.Substring(0,120):row.Name;
            return new WebhookJob{Id=occurrence,RouteId=t.RouteId,PortalInstanceId=t.InstanceId,PortalChatId=t.ChatId,ConversationId=t.ChatId,PortalRevision=t.GrantRevision,ExternalId="schedule-"+row.Id+"-"+occurrence,Title=title,Prompt=row.Prompt,Context="",Fingerprint=Webhooks.PayloadFingerprint(new Dictionary<string,string>{{"title",title},{"prompt",row.Prompt},{"context",""}}),State="queued",CreatedUtc=Now.ToString("o"),RouteSnapshot=route};
        }
        private async Task<WebhookJob> EnqueueLocal(PortalConversationTarget target,WebhookJob job,CancellationToken token)
        {
            Verify(target);
            if(store.Exists(job.Id)){
                var prior=store.Message(job.Id);if(!prior.OperatorOrigin||prior.RequestedThreadId!=target.ThreadId||prior.TargetAccountKey!=target.AccountKey||!Same(prior.TargetHome,target.Home)||!Same(prior.TargetWorkspace??prior.Workspace,target.Workspace)||prior.Title!=job.Title||prior.Prompt!=job.Prompt)throw new WebhookException(409,"Le recu local de cette occurrence ne correspond pas a sa preparation.");
                return new WebhookJob{Id=prior.Id,Fingerprint=job.Fingerprint,State=prior.State};
            }
            WebhookChats.RequireNativeReady(target.Home,target.ThreadId);
            if(store.Messages().Any(m=>m.TargetAccountKey==target.AccountKey&&Same(m.TargetHome,target.Home)&&m.TargetThreadId==target.ThreadId&&LocalPending(m.State)))throw new WebhookException(409,"Une demande locale attend deja dans cette discussion.");
            var accounts=new AccountService(root);var selected=NamedInstances.Resolve(accounts,target.InstanceId);
            var channel=await NamedInstances.Connect(store,accounts,selected,target.Workspace,token,target.ThreadId).ConfigureAwait(false);
            Verify(target);if(channel.AccountKey!=target.AccountKey||!Same(channel.Home,target.Home)||!Same(channel.Workspace,target.Workspace))throw new InvalidOperationException("Le destinataire local a change.");
            var request=new RelayJobSpec{Id=job.Id,To=channel.Id,Title=job.Title,Prompt=job.Prompt,Kind="general",Access="write",ReturnToSource=false,ExplicitDelegation=true};
            var message=await new DirectMessages(store).Submit(request,target.ThreadId,token).ConfigureAwait(false);
            Verify(target);RelayWorker.Ensure(store);return new WebhookJob{Id=message.Id,Fingerprint=job.Fingerprint,State=message.State};
        }
        private WebhookJob PreparedAdmission(PortalConversationTarget target,WebhookJob prepared)
        {
            if(target.Local){
                if(!store.Exists(prepared.Id))return null;var prior=store.Message(prepared.Id);
                if(!prior.OperatorOrigin||prior.RequestedThreadId!=target.ThreadId||prior.TargetAccountKey!=target.AccountKey||!Same(prior.TargetHome,target.Home)||!Same(prior.TargetWorkspace??prior.Workspace,target.Workspace)||prior.Title!=prepared.Title||prior.Prompt!=prepared.Prompt)throw new WebhookException(409,"Le recu de l'occurrence locale doit etre verifie.");
                return new WebhookJob{Id=prior.Id,Fingerprint=prepared.Fingerprint,State=prior.State};
            }
            if(!File.Exists(Path.Combine(store.Root,"webhook-job-"+prepared.Id+".dpapi")))return null;
            var row=Webhooks.Job(store,prepared.Id);if(row.Fingerprint!=prepared.Fingerprint||row.PortalInstanceId!=target.InstanceId||row.PortalChatId!=target.ChatId||row.RouteId!=target.RouteId||row.RouteSnapshot==null||row.RouteSnapshot.ThreadId!=target.ThreadId||row.RouteSnapshot.AccountKey!=target.AccountKey||!Same(row.RouteSnapshot.Home,target.Home)||!Same(row.RouteSnapshot.Workspace,target.Workspace))throw new WebhookException(409,"Le recu de cette occurrence doit etre verifie.");return row;
        }
        private Task<WebhookJob> Enqueue(PortalConversationTarget target,WebhookJob job,CancellationToken token)
        {
            if(target.Local)return EnqueueLocal(target,job,token);
            token.ThrowIfCancellationRequested();Verify(target);
            using(Webhooks.Admission(store)){
                Verify(target);
                if(File.Exists(Path.Combine(store.Root,"webhook-job-"+job.Id+".dpapi"))){var prior=Webhooks.Job(store,job.Id);if(prior.Fingerprint!=job.Fingerprint||prior.RouteId!=job.RouteId||prior.PortalInstanceId!=target.InstanceId||prior.PortalChatId!=target.ChatId||prior.RouteSnapshot==null||prior.RouteSnapshot.ThreadId!=target.ThreadId||prior.RouteSnapshot.AccountKey!=target.AccountKey||!Same(prior.RouteSnapshot.Home,target.Home)||!Same(prior.RouteSnapshot.Workspace,target.Workspace))throw new WebhookException(409,"Le recu de cette occurrence ne correspond pas a la demande preparee.");return Task.FromResult(prior);}
                WebhookChats.RequireNativeReady(target.Home,target.ThreadId);
                var jobs=Webhooks.Jobs(store);if(jobs.Where(Webhooks.IsPending).Any(j=>j.ThreadId==target.ThreadId||j.ConversationId==target.ChatId||Webhooks.Job(store,j.Id).RouteSnapshot!=null&&Webhooks.Job(store,j.Id).RouteSnapshot.ThreadId==target.ThreadId))throw new WebhookException(409,"Une demande attend deja dans cette discussion.");
                if(jobs.Count(Webhooks.IsPending)>=Webhooks.MaximumPending||jobs.Count(j=>j.RouteId==target.RouteId&&Webhooks.IsPending(j))>=Webhooks.MaximumPendingPerRoute)throw new WebhookException(429,"La file de missions est pleine.");
                if((job.RouteSnapshot.PromptPrefix??"").Length+job.Prompt.Length+1024>24000)throw new WebhookException(413,"La consigne et les instructions du portail sont trop longues.");
                if(NativeId(target.ChatId)){
                    string registration="portal-chat-"+Webhooks.Hash(target.RouteId+"\n"+target.InstanceId+"\n"+target.ThreadId).Substring(0,40)+".dpapi";
                    store.WriteRecord(registration,new PortalRegistration{RootRouteId=target.RouteId,InstanceId=target.InstanceId,AccountKey=target.AccountKey,Home=target.Home,Workspace=target.Workspace,ThreadId=target.ThreadId,Title=job.Title});
                }
                Portal.ValidateJob(store,job);Webhooks.Limit(store,Webhooks.Route(store,target.RouteId),true);Verify(target);token.ThrowIfCancellationRequested();Webhooks.SaveJob(store,job);WebhookWorker.Ensure(store);return Task.FromResult(job);
            }
        }
        // One prepared occurrence and one durable job ID survive a crash on
        // either side of admission. Missed intervals coalesce into one run.
        // Only storage admission is retried; an uncertain native turn is paused.
        internal async Task PumpDue(CancellationToken token,Func<bool> continueAdmission=null)
        {
            foreach(var candidate in All().Where(s=>s.Status=="ACTIVE").OrderBy(s=>s.NextRunUtc,StringComparer.Ordinal).Take(MaximumSchedules)){
                if(continueAdmission!=null&&!continueAdmission())return;
                token.ThrowIfCancellationRequested();if(candidate.PendingJob==null&&(candidate.NextRunUtc==null||Utc(candidate.NextRunUtc)>Now))continue;
                using(ApplicationUpdates.AdmitPortalMutation(store))using(store.Lease("conversation-schedule-"+candidate.Id)){
                    var current=Load(candidate.Id);if(current.Status!="ACTIVE")continue;
                    try{
                        try{Verify(current.Target);}catch(InvalidOperationException){current.Status="PAUSED";current.State="blocked";current.Error="Les autorisations de la planification ont change. Verifiez-les avant de la reprendre.";current.NextRunUtc=null;store.WriteRecord(Record(current.Id),current);continue;}
                        if(current.LastJobId!=null){string lastState=current.Target.Local?(store.Exists(current.LastJobId)?store.Message(current.LastJobId).State:"missing"):Webhooks.Job(store,current.LastJobId).State;if(lastState=="attention"||lastState=="uncertain"||lastState=="missing"){current.Status="PAUSED";current.State="attention";current.Error="La derniere occurrence attend une verification. Aucun nouvel envoi automatique.";current.NextRunUtc=null;store.WriteRecord(Record(current.Id),current);continue;}if(current.Target.Local?LocalPending(lastState):Webhooks.IsPending(new WebhookJob{State=lastState}))continue;}
                        if(current.PendingJob==null){if(current.NextRunUtc==null||Utc(current.NextRunUtc)>Now)continue;current.PendingJob=Template(current);current.State="prepared";store.WriteRecord(Record(current.Id),current);}
                        token.ThrowIfCancellationRequested();var result=await (dependencies.Enqueue==null?Enqueue(current.Target,current.PendingJob,token):dependencies.Enqueue(current.Target,Copy(current.PendingJob),token)).ConfigureAwait(false);
                        if(result==null||result.Id!=current.PendingJob.Id||result.Fingerprint!=current.PendingJob.Fingerprint)throw new InvalidOperationException("Recu d'occurrence invalide.");
                        current.LastJobId=result.Id;current.LastRunUtc=Now.ToString("o");current.PendingJob=null;current.NextRunUtc=Next(current,Now).ToString("o");current.State="scheduled";current.Error=null;store.WriteRecord(Record(current.Id),current);
                    }catch(OperationCanceledException){throw;}
                    catch(WebhookException e){if(e.StatusCode==409||e.StatusCode==429){current.State="waiting";current.Error="Occurrence en attente : discussion occupee ou admission indisponible.";}else{current.Status="PAUSED";current.State="blocked";current.Error="L'autorisation de cette planification doit etre verifiee. Aucun nouvel envoi.";current.NextRunUtc=null;}store.WriteRecord(Record(current.Id),current);}
                    catch(IOException){current.State="waiting";current.Error="La confirmation de l'occurrence est attendue ; son identifiant reste conserve.";store.WriteRecord(Record(current.Id),current);}
                    catch(InvalidOperationException){current.Status="PAUSED";current.State="blocked";current.Error="L'identite ou l'autorisation de cette planification a change. Aucun nouvel envoi.";current.NextRunUtc=null;store.WriteRecord(Record(current.Id),current);}
                }
            }
        }
    }
}
