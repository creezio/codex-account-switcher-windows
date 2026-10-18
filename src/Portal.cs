using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Creezio.Switcher
{
    public sealed class PortalInstanceGrant
    {
        public long AuthorizationRevision {get;set;}
        public string InstanceId {get;set;}
        public string AccountKey {get;set;}
        public string Home {get;set;}
        public string Workspace {get;set;}
        public string Access {get;set;}
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string PermissionProfile {get;set;}
    }
    public sealed class PortalConfig
    {
        public string RootRouteId {get;set;}
        public long Revision {get;set;}
        public bool Enabled {get;set;}
        public bool IncludeExisting {get;set;}
        public List<PortalInstanceGrant> Instances {get;set;}
        public PortalConfig(){Instances=new List<PortalInstanceGrant>();}
    }
    internal sealed class PortalRegistration
    {
        public string RootRouteId {get;set;}
        public string InstanceId {get;set;}
        public string AccountKey {get;set;}
        public string Home {get;set;}
        public string Workspace {get;set;}
        public string ThreadId {get;set;}
        public string Title {get;set;}
    }
    internal interface IPortalNative
    {
        object Describe(PortalInstanceGrant grant);
        void Verify(PortalInstanceGrant grant);
        object List(PortalInstanceGrant grant);
        object Read(PortalInstanceGrant grant,string thread);
    }
    // The bearer authenticates the private site. It does not itself grant access
    // to every local profile: a separate, locally saved allowlist pins each one.
    internal static class Portal
    {
        private static T Copy<T>(T value){return Json.Read<T>(Json.Write(value));}
        // Older relay executables only understand root-route IDs. A derived ID
        // makes their old validation fail closed before they can send this job.
        internal static string FrozenRouteId(string root,string instance)
        {return Webhooks.Hash(root+"\nportal-route:"+instance).Substring(0,32);}
        private static string ConfigFile(string route){RelayStore.MessageId(route);return "portal-"+route+"-v1.dpapi";}
        internal static PortalConfig Config(RelayStore store,string route){return store.ReadRecord<PortalConfig>(ConfigFile(route));}
        internal static PortalConfig SaveConfig(RelayStore store,PortalConfig input)
        {
            if(input==null)throw new ArgumentNullException("input");var value=Copy(input);Webhooks.Route(store,value.RootRouteId);
            if(value.Instances==null||value.Instances.Count>50||value.Instances.Any(i=>i==null)||value.Instances.Select(i=>i.InstanceId).Distinct(StringComparer.Ordinal).Count()!=value.Instances.Count)throw new InvalidOperationException("Choisissez au plus 50 instances distinctes pour ce portail.");
            foreach(var grant in value.Instances){
                if(grant.InstanceId!="local")RelayStore.MessageId(grant.InstanceId);
                if(String.IsNullOrWhiteSpace(grant.AccountKey)||grant.AccountKey.Length>256||grant.AccountKey.Any(Char.IsControl))throw new InvalidOperationException("Compte permanent du portail invalide.");
                grant.Home=RelayStore.WorkspacePath(grant.Home);grant.Workspace=RelayStore.WorkspacePath(grant.Workspace);
                grant.Access=String.IsNullOrEmpty(grant.Access)?"desktop":grant.Access;if(grant.Access!="desktop"&&grant.Access!="headless")throw new InvalidOperationException("Mode de portail invalide.");
                grant.PermissionProfile=String.IsNullOrEmpty(grant.PermissionProfile)?"inherit":grant.PermissionProfile;if(grant.PermissionProfile!="inherit"&&grant.PermissionProfile!="full-access")throw new InvalidOperationException("Profil de portail invalide.");
                if(!String.IsNullOrEmpty(grant.Model)&&!Regex.IsMatch(grant.Model,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,119}\z"))throw new InvalidOperationException("Modele invalide.");
                if(!String.IsNullOrEmpty(grant.ReasoningEffort)&&!new[]{"none","minimal","low","medium","high","xhigh","max","ultra"}.Contains(grant.ReasoningEffort))throw new InvalidOperationException("Niveau de raisonnement invalide.");
            }
            using(store.Lease("portal-config-"+value.RootRouteId)){
                var old=Config(store,value.RootRouteId);if(old.Revision!=value.Revision)throw new InvalidOperationException("La configuration du portail a change. Actualisez avant d'enregistrer.");
                if(old.Revision<0||old.Revision==Int64.MaxValue)throw new InvalidOperationException("La revision des acces du portail est invalide.");
                long next=old.Revision+1;bool globalChanged=old.Enabled!=value.Enabled||old.IncludeExisting!=value.IncludeExisting;
                foreach(var grant in value.Instances){
                    var previous=(old.Instances??new List<PortalInstanceGrant>()).SingleOrDefault(i=>i.InstanceId==grant.InstanceId);
                    // The global revision still provides CAS and a monotone epoch
                    // after revoke/regrant. Unrelated grants keep their epoch.
                    grant.AuthorizationRevision=previous!=null&&!globalChanged&&SameGrant(previous,grant)?GrantRevision(old,previous):next;
                }
                value.Revision=next;store.WriteRecord(ConfigFile(value.RootRouteId),value);return value;
            }
        }
        private static bool SameGrant(PortalInstanceGrant first,PortalInstanceGrant second)
        {return first.InstanceId==second.InstanceId&&first.AccountKey==second.AccountKey&&Same(first.Home,second.Home)&&Same(first.Workspace,second.Workspace)&&(first.Access??"desktop")==second.Access&&(first.Model??"")==(second.Model??"")&&(first.ReasoningEffort??"")==(second.ReasoningEffort??"")&&(String.IsNullOrEmpty(first.PermissionProfile)?"inherit":first.PermissionProfile)==second.PermissionProfile;}
        private static long GrantRevision(PortalConfig config,PortalInstanceGrant grant)
        {
            if(grant.AuthorizationRevision<0||grant.AuthorizationRevision>config.Revision)throw new InvalidOperationException("La revision des acces de cette instance est invalide.");
            // Legacy jobs were stamped with the then-global revision. Preserve
            // that exact value when an unchanged grant is migrated.
            return grant.AuthorizationRevision>0?grant.AuthorizationRevision:config.Revision;
        }
        private static PortalConfig RequireConfig(RelayStore store,WebhookRoute root)
        {var config=Config(store,root.Id);if(!config.Enabled||config.RootRouteId!=root.Id)throw new WebhookException(403,"Ce portail n'est pas autorise a parcourir les instances.");return config;}
        private static PortalInstanceGrant Grant(PortalConfig config,string id)
        {var grant=config.Instances.SingleOrDefault(i=>i.InstanceId==id);if(grant==null)throw new WebhookException(404,"Instance non partagee avec ce portail.");return grant;}
        internal static PortalInstanceGrant RequireGrant(RelayStore store,string slug,string bearer,string instanceId)
        {return RequireGrant(store,slug,bearer,instanceId,new PortalNative(store));}
        internal static PortalInstanceGrant ResolveGrant(RelayStore store,string slug,string bearer,string instanceId)
        {return RequireGrant(store,slug,bearer,instanceId);}
        internal static PortalInstanceGrant RequireGrant(RelayStore store,string slug,string bearer,string instanceId,IPortalNative native)
        {var route=Webhooks.Authorize(store,slug,bearer);var grant=Grant(RequireConfig(store,route),instanceId);native.Verify(grant);return Copy(grant);}
        internal static object ListInstances(RelayStore store,string slug,string bearer)
        {return ListInstances(store,slug,bearer,new PortalNative(store));}
        internal static object ListInstances(RelayStore store,string slug,string bearer,IPortalNative native)
        {
            var route=Webhooks.Authorize(store,slug,bearer);var config=RequireConfig(store,route);var rows=new List<object>();
            foreach(var grant in config.Instances){try{native.Verify(grant);rows.Add(native.Describe(grant));}catch(InvalidOperationException e){rows.Add(new{id=grant.InstanceId,name="Instance a verifier",account="",online=false,desktopOpen=false,executionMode="unavailable",issue=WebhookChats.Safe(e.Message,500)});}}
            return new{instances=rows.ToArray(),includeExisting=config.IncludeExisting};
        }
        internal static object ListChats(RelayStore store,string slug,string bearer,string instanceId,string cursor=null)
        {return ListChats(store,slug,bearer,instanceId,cursor,new PortalNative(store));}
        internal static object ListChats(RelayStore store,string slug,string bearer,string instanceId,string cursor,IPortalNative native)
        {
            if(!String.IsNullOrEmpty(cursor))throw new WebhookException(400,"Ce connecteur natif expose uniquement les discussions recentes ; il ne fournit pas de curseur de pagination.");
            var route=Webhooks.Authorize(store,slug,bearer);var config=RequireConfig(store,route);var grant=Grant(config,instanceId);native.Verify(grant);
            var rows=new List<object>();var known=new HashSet<string>(StringComparer.OrdinalIgnoreCase);bool truncated=false;
            if(config.IncludeExisting){
                var list=native.List(grant);var source=RelayEngine.Rows(Json.Get(list,"pinnedThreads")).Concat(RelayEngine.Rows(Json.Get(list,"threads"))).Take(200).ToArray();
                truncated=RelayEngine.Rows(Json.Get(list,"threads")).Count()>=PortalNative.ThreadLimit;
                foreach(var row in source){string sourceName=Json.Str(Json.Get(row,"source"));if(sourceName.Length>0&&sourceName!="codex")continue;string id=Json.Str(Json.Get(row,"threadId"));if(id.Length==0)id=Json.Str(Json.Get(row,"id"));if(!NativeId(id)||!known.Add(id))continue;
                    rows.Add(new{id=id,threadId=id,title=WebhookChats.Safe(Json.Str(Json.Get(row,"title")),120),state=Json.Str(Json.Get(row,"status")),updatedUtc=Json.Str(Json.Get(row,"updatedAt")),existing=true});if(rows.Count>=100){truncated=true;break;}
                }
                native.Verify(grant);
            }
            foreach(var metadata in Webhooks.Jobs(store).Where(j=>j.RouteId==route.Id&&String.IsNullOrEmpty(j.ConversationId)).Take(200)){
                var job=Webhooks.Job(store,metadata.Id);if(job.PortalInstanceId!=instanceId||!String.IsNullOrEmpty(job.PortalChatId)||!Binding(job.RouteSnapshot,grant))continue;
                if(!String.IsNullOrEmpty(job.ThreadId)&&known.Contains(job.ThreadId))continue;rows.Add(new{id=job.Id,threadId=job.ThreadId,title=WebhookChats.Safe(job.Title,120),state=job.State,updatedUtc=job.UpdatedUtc,existing=false});if(rows.Count>=100){truncated=true;break;}
            }
            return new{instanceId=instanceId,chats=rows.Take(100).ToArray(),nextCursor=(string)null,truncated=truncated};
        }
        private static bool NativeId(string value)
        {return Regex.IsMatch(value??"",@"\A[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-7[a-fA-F0-9]{3}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\z");}
        private static string RegistrationFile(string route,string instance,string thread)
        {return "portal-chat-"+Webhooks.Hash(route+"\n"+instance+"\n"+thread).Substring(0,40)+".dpapi";}
        private static bool Same(string first,string second)
        {return !String.IsNullOrEmpty(first)&&!String.IsNullOrEmpty(second)&&RelayStore.SamePath(first,second);}
        private static bool Binding(WebhookRoute route,PortalInstanceGrant grant)
        {return route!=null&&route.InstanceId==grant.InstanceId&&route.AccountKey==grant.AccountKey&&Same(route.Home,grant.Home);}
        private static PortalRegistration RegisterExisting(RelayStore store,WebhookRoute root,PortalInstanceGrant grant,string thread,IPortalNative native,bool requireIdle)
        {
            if(!NativeId(thread))throw new WebhookException(404,"Discussion introuvable.");var nativeRead=native.Read(grant,thread);DirectMessages.RequireThread(nativeRead,thread);native.Verify(grant);
            if(requireIdle&&DirectMessages.Busy(nativeRead))throw new WebhookException(409,"Cette discussion attend encore Codex ou une intervention.");
            string workspace=WebhookChats.NativeWorkspace(grant.Home,thread);var row=Json.Get(nativeRead,"thread");string title=Json.Str(Json.Get(row,"title"));if(title.Length==0)title=Json.Str(Json.Get(row,"name"));
            var registration=new PortalRegistration{RootRouteId=root.Id,InstanceId=grant.InstanceId,AccountKey=grant.AccountKey,Home=grant.Home,Workspace=workspace,ThreadId=thread,Title=WebhookChats.Safe(title,120)};
            store.WriteRecord(RegistrationFile(root.Id,grant.InstanceId,thread),registration);return registration;
        }
        private static PortalRegistration Existing(RelayStore store,WebhookRoute root,PortalConfig config,PortalInstanceGrant grant,string thread,IPortalNative native,bool refresh)
        {
            if(!config.IncludeExisting||!NativeId(thread))throw new WebhookException(404,"Discussion introuvable.");string file=RegistrationFile(root.Id,grant.InstanceId,thread);PortalRegistration value=store.ReadRecord<PortalRegistration>(file);
            if(refresh||value.ThreadId==null)value=RegisterExisting(store,root,grant,thread,native,refresh);
            if(value.RootRouteId!=root.Id||value.InstanceId!=grant.InstanceId||value.AccountKey!=grant.AccountKey||value.ThreadId!=thread||!Same(value.Home,grant.Home)||!Same(value.Workspace,WebhookChats.NativeWorkspace(grant.Home,thread)))throw new WebhookException(409,"L'identite ou le dossier de cette discussion a change. Actualisez la liste.");return value;
        }
        private static WebhookJob RootJob(RelayStore store,WebhookRoute root,PortalInstanceGrant grant,string id)
        {
            if(!Regex.IsMatch(id??"",@"\A[a-f0-9]{32}\z"))throw new WebhookException(404,"Discussion introuvable.");var job=Webhooks.Job(store,id);
            if(job.RouteId!=root.Id||job.PortalInstanceId!=grant.InstanceId||job.PortalRevision<=0||!String.IsNullOrEmpty(job.PortalChatId)||!String.IsNullOrEmpty(job.ConversationId)||job.RouteSnapshot==null||!String.IsNullOrEmpty(job.RouteSnapshot.ThreadId)||!Binding(job.RouteSnapshot,grant))throw new WebhookException(404,"Discussion introuvable.");
            if(!String.IsNullOrEmpty(job.ThreadId)){
                if(!NativeId(job.ThreadId)||!store.Exists(job.Id))throw new WebhookException(409,"Le recu natif de creation est encore attendu.");var message=store.Message(job.Id);
                if(!message.OperatorOrigin||!message.NewConversation||message.WebhookJobId!=job.Id||message.TargetThreadId!=job.ThreadId||message.TargetAccountKey!=grant.AccountKey||!Same(message.TargetHome,grant.Home)||!Same(message.TargetWorkspace??message.Workspace,job.RouteSnapshot.Workspace))throw new WebhookException(409,"Le recu natif de cette discussion ne correspond pas au portail.");
            }
            return job;
        }
        internal static object Snapshot(RelayStore store,string slug,string bearer,string instanceId,string chatId)
        {return Snapshot(store,slug,bearer,instanceId,chatId,new PortalNative(store));}
        internal static object Snapshot(RelayStore store,string slug,string bearer,string instanceId,string chatId,IPortalNative native)
        {
            var root=Webhooks.Authorize(store,slug,bearer);var config=RequireConfig(store,root);var grant=Grant(config,instanceId);native.Verify(grant);string thread,title;WebhookJob created=null;
            if(NativeId(chatId)){var registration=Existing(store,root,config,grant,chatId,native,false);thread=registration.ThreadId;title=registration.Title;}
            else{created=RootJob(store,root,grant,chatId);thread=created.ThreadId;title=created.Title;}
            var view=Json.Obj(Json.Read<object>(Json.Write(String.IsNullOrEmpty(thread)?new{id=chatId,threadId=(string)null,title=WebhookChats.Safe(title,120),state="queued",permission="unknown",messages=new object[0],activities=new object[0],jobs=new object[0],cursor="",truncated=false,catchingUp=true}:WebhookChats.NativeSnapshot(grant.Home,thread,chatId,title))));
            var metadataRows=Webhooks.Jobs(store).Where(j=>j.RouteId==root.Id).ToArray();var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase){chatId};
            if(!String.IsNullOrEmpty(thread)){
                aliases.Add(thread);
                // A new chat initially has only its durable root-job handle.
                // Once Codex returns its GUID, both URLs refer to the same
                // transcript, including followups not dispatched yet.
                foreach(var row in metadataRows.Where(j=>j.ThreadId==thread&&String.IsNullOrEmpty(j.ConversationId)))aliases.Add(row.Id);
            }
            var jobs=metadataRows.Where(j=>aliases.Contains(j.Id)||j.ConversationId!=null&&aliases.Contains(j.ConversationId)||thread!=null&&j.ThreadId==thread).Select(j=>Webhooks.Job(store,j.Id)).Where(j=>j.PortalInstanceId==instanceId&&Binding(j.RouteSnapshot,grant)).OrderBy(j=>j.CreatedUtc,StringComparer.Ordinal).ToArray();var visible=RelayEngine.Rows(Json.Get(view,"messages")).ToList();
            bool clipped=Object.Equals(Json.Get(view,"truncated"),true);
            foreach(var metadata in jobs.Reverse().Take(50).Reverse()){var job=Webhooks.Job(store,metadata.Id);if(job.PortalInstanceId!=instanceId||!Binding(job.RouteSnapshot,grant))continue;if((job.Prompt??"").Length>4000)clipped=true;visible.Add(Json.Read<object>(Json.Write(new{id="job-"+job.Id,role="user",text=WebhookChats.Safe(job.Prompt,4000),createdUtc=job.CreatedUtc,options=Webhooks.PublicOptions(store,job,grant),phase=new[]{"queued","waiting"}.Contains(job.State)?"queued":job.State=="uncertain"?"uncertain":"submitted"})));}
            visible=visible.OrderBy(m=>Json.Str(Json.Get(m,"createdUtc")),StringComparer.Ordinal).ToList();int characters=visible.Sum(m=>Json.Str(Json.Get(m,"text")).Length);while(visible.Count>120||characters>60000&&visible.Count>1){characters-=Json.Str(Json.Get(visible[0],"text")).Length;visible.RemoveAt(0);clipped=true;}
            var activities=RelayEngine.Rows(Json.Get(view,"activities")).ToList();characters=activities.Sum(m=>Json.Str(Json.Get(m,"text")).Length);while(characters>40000&&activities.Count>1){characters-=Json.Str(Json.Get(activities[0],"text")).Length;activities.RemoveAt(0);clipped=true;}
            view["messages"]=visible.ToArray();view["activities"]=activities.ToArray();view["truncated"]=clipped;view["jobs"]=jobs.Reverse().Take(50).Reverse().Select(j=>new{id=j.Id,state=j.State,error=WebhookChats.Safe(j.Error,500),createdUtc=j.CreatedUtc,updatedUtc=j.UpdatedUtc}).ToArray();
            var latest=jobs.LastOrDefault();if(latest!=null){if(Webhooks.IsPending(latest)||Json.Str(Json.Get(view,"state"))=="idle")view["state"]=latest.State;view["error"]=WebhookChats.Safe(latest.Error,1000);view["updatedUtc"]=latest.UpdatedUtc;view["cursor"]=Webhooks.Hash(Json.Str(Json.Get(view,"cursor"))+"\n"+latest.UpdatedUtc+"\n"+latest.State).Substring(0,24);}
            view["instanceId"]=instanceId;native.Verify(grant);return view;
        }
        internal static WebhookJob Create(RelayStore store,string slug,string bearer,string instanceId,string idempotency,string json)
        {return Admit(store,slug,bearer,instanceId,null,idempotency,json,new PortalNative(store));}
        internal static WebhookJob Send(RelayStore store,string slug,string bearer,string instanceId,string chatId,string idempotency,string json)
        {return Admit(store,slug,bearer,instanceId,chatId,idempotency,json,new PortalNative(store));}
        internal static WebhookJob Admit(RelayStore store,string slug,string bearer,string instanceId,string chatId,string idempotency,string json,IPortalNative native)
        {
            if(String.IsNullOrEmpty(idempotency)||idempotency.Length>128||idempotency.Any(c=>c<33||c>126))throw new WebhookException(400,"Idempotency-Key est obligatoire (1 a 128 caracteres ASCII visibles).");var payload=Webhooks.ParseBody(json);var options=PortalMessageOptions.FromPayload(payload);
            using(Webhooks.Admission(store)){
                var root=Webhooks.Authorize(store,slug,bearer);var config=RequireConfig(store,root);var grant=Grant(config,instanceId);native.Verify(grant);
                string id=Webhooks.Hash(root.Id+"\nportal:"+instanceId+"\nchat:"+(chatId??"")+"\n"+idempotency).Substring(0,32),fingerprint=Webhooks.PayloadFingerprint(payload);
                if(File.Exists(Path.Combine(store.Root,"webhook-job-"+id+".dpapi"))){var prior=Webhooks.Job(store,id);if(prior.Fingerprint!=fingerprint||prior.PortalInstanceId!=instanceId||prior.PortalChatId!=chatId)throw new WebhookException(409,"Cette cle d'idempotence designe deja un autre message.");return prior;}
                string thread=null,workspace=grant.Workspace;
                if(chatId!=null){
                    if(NativeId(chatId)){var registration=Existing(store,root,config,grant,chatId,native,true);thread=registration.ThreadId;workspace=registration.Workspace;}
                    else{var created=RootJob(store,root,grant,chatId);thread=created.ThreadId;if(String.IsNullOrEmpty(thread))throw new WebhookException(409,"La discussion est encore en creation.");workspace=created.RouteSnapshot.Workspace;var state=native.Read(grant,thread);DirectMessages.RequireThread(state,thread);if(DirectMessages.Busy(state))throw new WebhookException(409,"Cette discussion attend encore Codex ou une intervention.");}
                    WebhookChats.RequireNativeReady(grant.Home,thread);
                }
                var jobs=Webhooks.Jobs(store);if(chatId!=null&&jobs.Where(Webhooks.IsPending).Any(j=>j.ConversationId==chatId||j.Id==chatId||j.ThreadId==thread||Webhooks.Job(store,j.Id).RouteSnapshot!=null&&Webhooks.Job(store,j.Id).RouteSnapshot.ThreadId==thread))throw new WebhookException(409,"Une demande est deja en attente dans cette discussion. Aucun message entrelace.");
                if(jobs.Count(Webhooks.IsPending)>=Webhooks.MaximumPending||jobs.Count(j=>j.RouteId==root.Id&&Webhooks.IsPending(j))>=Webhooks.MaximumPendingPerRoute)throw new WebhookException(429,"La file de missions est pleine.");Webhooks.Limit(store,root,true);
                if((root.PromptPrefix??"").Length+payload["prompt"].Length+payload["context"].Length+1024>24000)throw new WebhookException(413,"Le message et ses instructions depassent 24000 caracteres.");
                var frozen=Copy(root);frozen.Id=Webhooks.ExecutionRouteId(root.Id,grant.InstanceId,options);frozen.SecretHash=null;frozen.InstanceId=grant.InstanceId;frozen.AccountKey=grant.AccountKey;frozen.Home=grant.Home;frozen.Workspace=workspace;frozen.ThreadId=thread;frozen.Access=grant.Access;frozen.Model=grant.Model;frozen.ReasoningEffort=grant.ReasoningEffort;frozen.PermissionProfile=grant.PermissionProfile;
                if(options!=null){PortalAttachments.Require(store,root.Id,grant,options.Attachments);PortalAttachments.Claim(store,root.Id,grant,options.Attachments);}
                var job=new WebhookJob{Id=id,RouteId=root.Id,PortalRevision=GrantRevision(config,grant),PortalInstanceId=grant.InstanceId,PortalChatId=chatId,ConversationId=chatId,ExternalId=idempotency,Title=payload["title"],Prompt=payload["prompt"],Context=payload["context"],Options=options,Fingerprint=fingerprint,State="queued",CreatedUtc=DateTime.UtcNow.ToString("o"),RouteSnapshot=frozen};ValidateJob(store,job);native.Verify(grant);Webhooks.SaveJob(store,job);return job;
            }
        }
        internal static void ValidateJob(RelayStore store,WebhookJob job)
        {
            var root=Webhooks.Route(store,job.RouteId);var config=RequireConfig(store,root);var grant=Grant(config,job.PortalInstanceId);var frozen=job.RouteSnapshot;
            if(!Webhooks.Config(store).Enabled||!root.Enabled||frozen==null||frozen.Id!=Webhooks.ExecutionRouteId(root.Id,grant.InstanceId,job.Options)||frozen.Revision!=root.Revision||GrantRevision(config,grant)!=job.PortalRevision||!Binding(frozen,grant)||frozen.Access!=grant.Access||frozen.Model!=grant.Model||frozen.ReasoningEffort!=grant.ReasoningEffort||frozen.PermissionProfile!=grant.PermissionProfile)throw new InvalidOperationException("L'acces du portail a cette instance a change. Aucun nouvel envoi effectue.");
            if(job.PortalChatId==null){if(!String.IsNullOrEmpty(frozen.ThreadId)||!Same(frozen.Workspace,grant.Workspace))throw new InvalidOperationException("Mandat de nouveau chat invalide.");return;}
            if(job.ConversationId!=job.PortalChatId)throw new InvalidOperationException("Identite de discussion invalide.");
            if(NativeId(job.PortalChatId)){
                if(!config.IncludeExisting)throw new InvalidOperationException("L'acces aux discussions existantes a ete retire.");var registration=store.ReadRecord<PortalRegistration>(RegistrationFile(root.Id,grant.InstanceId,job.PortalChatId));
                if(registration.ThreadId!=frozen.ThreadId||registration.RootRouteId!=root.Id||registration.InstanceId!=grant.InstanceId||registration.AccountKey!=grant.AccountKey||!Same(registration.Home,grant.Home)||!Same(registration.Workspace,frozen.Workspace)||!Same(WebhookChats.NativeWorkspace(grant.Home,frozen.ThreadId),frozen.Workspace))throw new InvalidOperationException("Le recu de discussion existante est invalide.");
            }else{var created=RootJob(store,root,grant,job.PortalChatId);if(created.ThreadId!=frozen.ThreadId||!Same(created.RouteSnapshot.Workspace,frozen.Workspace))throw new InvalidOperationException("Le chat cible ne correspond pas au chat cree.");}
        }
        internal static void ValidateJobRead(RelayStore store,WebhookJob job)
        {ValidateJobRead(store,job,new PortalNative(store));}
        internal static void ValidateJobRead(RelayStore store,WebhookJob job,IPortalNative native)
        {
            var root=Webhooks.Route(store,job.RouteId);var config=RequireConfig(store,root);var grant=Grant(config,job.PortalInstanceId);
            if(!Binding(job.RouteSnapshot,grant)||NativeId(job.PortalChatId)&&!config.IncludeExisting)throw new WebhookException(404,"Cette mission ne fait plus partie des acces du portail.");native.Verify(grant);
        }
    }
}
