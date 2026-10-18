using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class CloudCommands
    {
        internal static async Task<CloudConfiguration> Create(AccountService accounts,DesktopInstance instance,CloudConfigurationCreate request,CancellationToken token)
        {return await Create(accounts,instance,request,Guid.NewGuid().ToString("N"),token);}
        internal static async Task<CloudConfiguration> Create(AccountService accounts,DesktopInstance instance,CloudConfigurationCreate request,string operationId,CancellationToken token)
        {
            using(var client=CloudInstanceConnection.Open(accounts,instance))
                return await CloudMutationJournal.Run(accounts.Vault.Root,operationId,"create",instance.Id,instance.AccountKey,request,()=>client.Create(request,token),id=>client.Get(id,token));
        }
        internal static async Task<CloudConfiguration> Rename(AccountService accounts,DesktopInstance instance,string id,string name,CancellationToken token)
        {return await Rename(accounts,instance,id,name,Guid.NewGuid().ToString("N"),token);}
        internal static async Task<CloudConfiguration> Rename(AccountService accounts,DesktopInstance instance,string id,string name,string operationId,CancellationToken token)
        {
            using(var client=CloudInstanceConnection.Open(accounts,instance))
                return await CloudMutationJournal.Run(accounts.Vault.Root,operationId,"rename",instance.Id,instance.AccountKey,new{id=id,name=name},()=>client.Rename(id,name,token),configurationId=>client.Get(configurationId,token));
        }
        private static string Required(object input,string key)
        {string value=Json.Str(Json.Get(input,key));if(String.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Champ Cloud requis : "+key);return value;}
        private static void Known(object input,params string[] names)
        {
            if(input==null)throw new InvalidOperationException("Objet de configuration Cloud requis.");
            var fields=Json.Obj(input);if(fields==null)throw new InvalidOperationException("Objet de configuration Cloud invalide.");
            foreach(string field in fields.Keys)if(!names.Contains(field))throw new InvalidOperationException("Champ de file Cloud inconnu : "+field);
        }
        private static bool Flag(object input,string key,bool fallback=false)
        {object value=Json.Get(input,key);if(value==null)return fallback;if(!(value is bool))throw new InvalidOperationException("Booléen Cloud invalide : "+key);return (bool)value;}
        private static int Integer(object input,string key,int fallback,int minimum,int maximum)
        {object value=Json.Get(input,key);if(value==null)return fallback;double? number=Json.Number(value);if(!number.HasValue||number.Value%1!=0||number.Value<minimum||number.Value>maximum)throw new InvalidOperationException("Entier Cloud invalide : "+key);return (int)number.Value;}
        private static string ObservedId(object input,string key)
        {if(!Json.Obj(input).ContainsKey(key))throw new InvalidOperationException("Identifiant observé requis, éventuellement null : "+key);object value=Json.Get(input,key);if(value==null)return null;if(!(value is string)||String.IsNullOrWhiteSpace((string)value))throw new InvalidOperationException("Identifiant observé invalide : "+key);return (string)value;}
        private static List<object> Array(object input,string key,bool required=false)
        {
            object value=Json.Get(input,key);if(value==null){if(required)throw new InvalidOperationException("Liste Cloud requise : "+key);return new List<object>();}
            if(!(value is System.Collections.IEnumerable)||value is string||value is System.Collections.IDictionary)throw new InvalidOperationException("Liste Cloud invalide : "+key);
            return RelayEngine.Rows(value).ToList();
        }
        private static List<string> Strings(object input,string key,bool required=false)
        {var values=Array(input,key,required).Select(value=>{if(!(value is string)||String.IsNullOrWhiteSpace((string)value))throw new InvalidOperationException("Valeur de liste Cloud invalide : "+key);return (string)value;}).ToList();if(required&&values.Count==0)throw new InvalidOperationException("Liste Cloud vide : "+key);return values;}
        internal static CloudQueueTeam QueueTeam(object value)
        {
            Known(value,"id","name","repository","mandate","explicitDelegation","targets","maximumParallel","enabled","idleAfterMinutes","idleAlertsEnabled");
            if(!Flag(value,"explicitDelegation"))throw new InvalidOperationException("La configuration d’équipe exige un mandat utilisateur explicite pour ce projet.");
            return new CloudQueueTeam{Id=Required(value,"id"),Name=Required(value,"name"),Repository=Required(value,"repository"),Mandate=Required(value,"mandate"),ExplicitDelegation=true,
                Targets=Array(value,"targets",true).Select(target=>{Known(target,"instanceId","expectedAccountKey","configurationId");return new CloudQueueTarget{InstanceId=Required(target,"instanceId"),ExpectedAccountKey=Required(target,"expectedAccountKey"),ConfigurationId=Required(target,"configurationId")};}).ToList(),
                MaximumParallel=Integer(value,"maximumParallel",2,1,8),Enabled=Flag(value,"enabled",true),IdleAfterMinutes=Integer(value,"idleAfterMinutes",5,1,1440),IdleAlertsEnabled=Flag(value,"idleAlertsEnabled",true)};
        }
        internal static CloudPreparedMission QueueMission(object value)
        {
            Known(value,"id","teamId","title","prompt","repository","feature","lot","specification","model","reasoningEffort","permissionProfile","explicitDelegation","priority","reservedFiles","dependencies","eligibleInstanceIds");
            if(!Flag(value,"explicitDelegation"))throw new InvalidOperationException("La mission préparée exige une délégation utilisateur explicite dans le mandat du projet.");
            return new CloudPreparedMission{Id=Required(value,"id"),TeamId=Required(value,"teamId"),Title=Required(value,"title"),Prompt=Required(value,"prompt"),Repository=Required(value,"repository"),Feature=Required(value,"feature"),Lot=Required(value,"lot"),Specification=Required(value,"specification"),
                Model=Json.Str(Json.Get(value,"model")),ReasoningEffort=Json.Str(Json.Get(value,"reasoningEffort")),PermissionProfile=Json.Str(Json.Get(value,"permissionProfile")),ExplicitDelegation=true,Priority=Integer(value,"priority",0,-1000,1000),ReservedFiles=Strings(value,"reservedFiles",true),EligibleInstanceIds=Strings(value,"eligibleInstanceIds",true),
                Dependencies=Array(value,"dependencies").Select(dependency=>{Known(dependency,"missionId","mode");string mode=Json.Str(Json.Get(dependency,"mode"));if(String.IsNullOrEmpty(mode))mode="accepted";if(mode!="accepted"&&mode!="completed")throw new InvalidOperationException("Une dépendance Cloud exige accepted ou completed.");return new CloudQueueDependency{MissionId=Required(dependency,"missionId"),Mode=mode};}).ToList()};
        }
        internal static object QueueSnapshot(CloudQueueSnapshot snapshot)
        {
            return new{dispatchMode=snapshot.DispatchMode,teams=snapshot.Teams.Select(team=>new{id=team.Id,name=team.Name,repository=team.Repository,maximumParallel=team.MaximumParallel,enabled=team.Enabled,paused=team.Paused,idleAfterMinutes=team.IdleAfterMinutes,idleAlertsEnabled=team.IdleAlertsEnabled,targets=team.Targets.Select(target=>new{instanceId=target.InstanceId,accountKey=target.ExpectedAccountKey,configurationId=target.ConfigurationId}).ToArray()}).ToArray(),
                missions=snapshot.Missions.Select(mission=>new{id=mission.Id,teamId=mission.TeamId,title=mission.Title,repository=mission.Repository,feature=mission.Feature,lot=mission.Lot,specification=mission.Specification,state=mission.State,priority=mission.Priority,model=mission.Model,reasoningEffort=mission.ReasoningEffort,permissionProfile=mission.PermissionProfile,reservedFiles=mission.ReservedFiles,dependencies=mission.Dependencies,eligibleInstanceIds=mission.EligibleInstanceIds,target=mission.Target,jobId=mission.ObservedJobId,threadId=mission.ObservedThreadId,turnId=mission.ObservedTurnId,errorCode=mission.ErrorCode,errorHttpStatus=mission.ErrorHttpStatus,blockReason=mission.BlockReason,nextAttemptUtc=mission.NextAttemptUtc,acceptedUtc=mission.AcceptedUtc,rejectedUtc=mission.RejectedUtc}).ToArray(),alerts=snapshot.Alerts};
        }
        internal static object ExecuteQueue(RelayStore relay,object input,RelaySession source)
        {
            if(source==null)throw new InvalidOperationException("La file Cloud exige une session du chat orchestrateur réel.");
            Known(input,"operation","session","team","mission","teamId","id","jobId","threadId","turnId","action","episodeId","startWorker");
            bool startWorker=Flag(input,"startWorker",true);var queue=new CloudMissionQueue(relay);string operation=Required(input,"operation");bool changed=true;
            if(operation=="cloud-team-configure")queue.ConfigureTeam(QueueTeam(Json.Get(input,"team")),source);
            else if(operation=="cloud-queue-prepare")queue.Prepare(QueueMission(Json.Get(input,"mission")),source);
            else if(operation=="cloud-queue-list")changed=false;
            else if(operation=="cloud-queue-review")queue.Accept(Required(input,"id"),Required(input,"jobId"),Required(input,"threadId"),Required(input,"turnId"),source);
            else if(operation=="cloud-queue-control"){
                string action=Required(input,"action"),teamId=Json.Str(Json.Get(input,"teamId")),id=Json.Str(Json.Get(input,"id"));
                if(!String.IsNullOrEmpty(teamId)&&!String.IsNullOrEmpty(id))throw new InvalidOperationException("Choisissez une équipe ou une mission, pas les deux.");
                if(action=="ack-idle"){if(!String.IsNullOrEmpty(id))throw new InvalidOperationException("L’alerte d’inactivité appartient à une équipe.");queue.AcknowledgeIdle(Required(input,"teamId"),Required(input,"episodeId"),source);}
                else if(action=="cancel"){if(!String.IsNullOrEmpty(teamId))throw new InvalidOperationException("Annulez une mission préparée précise.");queue.Cancel(Required(input,"id"),source);}
                else if(action=="reject"){if(!String.IsNullOrEmpty(teamId))throw new InvalidOperationException("La revue de l’échec porte sur une mission précise.");queue.Reject(Required(input,"id"),Required(input,"jobId"),ObservedId(input,"threadId"),ObservedId(input,"turnId"),source);}
                else if(action=="pause"||action=="resume"){if(!String.IsNullOrEmpty(teamId))queue.SetTeamPaused(teamId,action=="pause",source);else queue.SetMissionPaused(Required(input,"id"),action=="pause",source);}
                else throw new InvalidOperationException("Action de file Cloud inconnue.");
            }else throw new InvalidOperationException("Commande de file Cloud inconnue.");
            var snapshot=QueueSnapshot(queue.List(source));if(changed&&startWorker)CloudQueueWorker.Ensure(relay);return snapshot;
        }
        internal static async Task<object> Execute(RelayStore relay,object input,CancellationToken token)
        {
            var accounts=new AccountService(Path.GetDirectoryName(relay.Root));
            string operation=Required(input,"operation");
            if(operation=="cloud-queue-adopt"){
                Known(input,"operation","id","teamId","reservedFiles","priority","session","explicitOperatorAuthorization");
                string sessionToken=Json.Str(Json.Get(input,"session"));var source=String.IsNullOrEmpty(sessionToken)?null:RelaySessions.Resolve(relay,sessionToken,CodexEnvironment.DefaultHome());
                bool trusted=Flag(input,"explicitOperatorAuthorization");if(source==null&&!trusted)throw new InvalidOperationException("L'adoption exige le chat source exact ou une action opérateur explicite.");
                var queue=new CloudMissionQueue(relay);int? priority=Json.Get(input,"priority")==null?(int?)null:Integer(input,"priority",0,-1000,1000);
                var adopted=queue.AdoptExisting(Required(input,"id"),Required(input,"teamId"),Strings(input,"reservedFiles",true),source,trusted,priority);
                CloudQueueWorker.Ensure(relay);return new{missionId=adopted.Id,snapshot=QueueSnapshot(queue.List(source))};
            }
            if(operation=="cloud-team-configure"||operation.StartsWith("cloud-queue-",StringComparison.Ordinal))return ExecuteQueue(relay,input,RelaySessions.Resolve(relay,Required(input,"session"),CodexEnvironment.DefaultHome()));
            var jobs=new CloudJobs(relay);
            var monitoring=new CloudJobMonitoring(relay);
            if(operation=="cloud-jobs")return jobs.List().Select(job=>CloudJobs.Summary(job,false,monitoring.Read(job))).ToArray();
            if(operation=="cloud-job-registry"){
                string sessionToken=Json.Str(Json.Get(input,"session"));var source=String.IsNullOrEmpty(sessionToken)?null:RelaySessions.Resolve(relay,sessionToken,CodexEnvironment.DefaultHome());
                return jobs.Registry(source,Json.Str(Json.Get(input,"repository"))).Select(job=>CloudJobs.Summary(job,false,monitoring.Read(job))).ToArray();
            }
            if(operation=="cloud-job-reconcile"){
                Known(input,"operation","id","session","explicitOperatorAuthorization","resumeProvenNotSent");
                string sessionToken=Json.Str(Json.Get(input,"session"));var source=String.IsNullOrEmpty(sessionToken)?null:RelaySessions.Resolve(relay,sessionToken,CodexEnvironment.DefaultHome());
                var job=await jobs.Reconcile(Required(input,"id"),source,token,Flag(input,"explicitOperatorAuthorization"),Flag(input,"resumeProvenNotSent"));
                return new{job=CloudJobs.Summary(job,false,monitoring.Read(job)),reconciliation=jobs.Reconciliation(job.Id)};
            }
            if(operation=="cloud-job-read"||operation=="cloud-job-poll"||operation=="cloud-job-cancel"){
                var job=operation=="cloud-job-poll"?await jobs.Advance(Required(input,"id"),token):operation=="cloud-job-cancel"?jobs.CancelQueued(Required(input,"id")):jobs.Get(Required(input,"id"));
                return CloudJobs.Summary(job,operation!="cloud-job-cancel",monitoring.Read(job));
            }
            if(operation=="cloud-job-submit"){
                string sessionToken=Json.Str(Json.Get(input,"session"));var source=String.IsNullOrEmpty(sessionToken)?null:RelaySessions.Resolve(relay,sessionToken,CodexEnvironment.DefaultHome());
                var admitted=await jobs.Submit(Json.Read<CloudJobSpec>(Json.Write(Json.Get(input,"job"))),source,token);
                if(!Object.Equals(Json.Get(input,"startWorker"),false))CloudJobWorker.Ensure(relay);
                return CloudJobs.Summary(admitted,true,monitoring.Read(admitted));
            }
            if(operation=="cloud-job-follow-up"){
                var source=RelaySessions.Resolve(relay,Required(input,"session"),CodexEnvironment.DefaultHome());
                var admitted=await jobs.FollowUp(Json.Read<CloudJobFollowUpSpec>(Json.Write(Json.Get(input,"followUp"))),source,token);
                if(!Object.Equals(Json.Get(input,"startWorker"),false))CloudJobWorker.Ensure(relay);
                return CloudJobs.Summary(admitted,false,monitoring.Read(admitted));
            }
            var instance=NamedInstances.Resolve(accounts,Required(input,"instance"));
            if(operation=="cloud-github-repositories"||operation=="cloud-github-repository"||operation=="cloud-github-status"){
                using(var github=CloudInstanceConnection.OpenGitHub(accounts,instance)){
                    if(operation=="cloud-github-status")return await github.ListInstallations(token);
                    if(operation=="cloud-github-repository")return await github.GetRepository(Required(input,"repositoryId"),token);
                    return await github.ListRepositories((int)(Json.Number(Json.Get(input,"page"))??1),token);
                }
            }
            if(operation=="cloud-setup-start")return await CloudEnvironmentSetup.Start(accounts,instance,Required(input,"configurationId"),Json.Read<System.Collections.Generic.Dictionary<string,string>>(Json.Write(Json.Get(input,"repositoryNames"))),Required(input,"id"),token);
            if(operation=="cloud-setup-follow")return await CloudEnvironmentSetup.Follow(accounts,instance,Required(input,"id"),token);
            if(operation=="cloud-setup-publish")return await CloudEnvironmentSetup.Publish(accounts,instance,Required(input,"id"),token);
            if(operation=="cloud-task-list"||operation=="cloud-task-read"||operation=="cloud-task-tools"){
                using(var client=CloudInstanceConnection.OpenTasks(accounts,instance)){
                    if(operation=="cloud-task-tools")return await ((ICloudTasksInventoryConnection)client).InventoryTools(Required(input,"threadId"),token);
                    if(operation=="cloud-task-read")return await client.Read(Required(input,"threadId"),token);
                    string cursor=Json.Str(Json.Get(input,"cursor"));return await client.List(String.IsNullOrEmpty(cursor)?null:cursor,10,token);
                }
            }
            if(operation=="cloud-update")return await CloudEnvironmentUpdate.Update(accounts,instance,Required(input,"configurationId"),Json.Read<CloudDraftPatch>(Json.Write(Json.Get(input,"patch"))),Required(input,"id"),token);
            if(operation=="cloud-follow")return await CloudEnvironmentUpdate.Follow(accounts,instance,Required(input,"id"),token);
            if(operation=="cloud-create")return await Create(accounts,instance,Json.Read<CloudConfigurationCreate>(Json.Write(Json.Get(input,"configuration"))),Required(input,"id"),token);
            if(operation=="cloud-rename")return await Rename(accounts,instance,Required(input,"configurationId"),Required(input,"name"),Required(input,"id"),token);
            using(var client=CloudInstanceConnection.Open(accounts,instance)){
                if(operation=="cloud-list"){string cursor=Json.Str(Json.Get(input,"cursor"));return await client.List("user",String.IsNullOrEmpty(cursor)?null:cursor,token);}
                if(operation=="cloud-read")return await client.Get(Required(input,"configurationId"),token);
            }
            throw new InvalidOperationException("Commande Cloud inconnue.");
        }
    }
}
