using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class CloudJobSpec
    {
        public string Id {get;set;}
        public string Instance {get;set;}
        public string ExpectedAccountKey {get;set;}
        public string ConfigurationId {get;set;}
        public string Title {get;set;}
        public string Prompt {get;set;}
        public string Feature {get;set;}
        public string Lot {get;set;}
        public string Specification {get;set;}
        public string Repository {get;set;}
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string PermissionProfile {get;set;}
        public bool ExplicitDelegation {get;set;}
        public bool ReturnToSource {get;set;}
    }
    internal sealed class CloudJobFollowUpSpec
    {
        public string Id {get;set;}
        public string ParentId {get;set;}
        public string Title {get;set;}
        public string Prompt {get;set;}
        public bool ExplicitDelegation {get;set;}
    }
    internal sealed class CloudJob
    {
        public int SchemaVersion {get;set;}
        public string Id {get;set;}
        public string Kind {get;set;}
        public string ParentId {get;set;}
        public string RootJobId {get;set;}
        public string ParentTurnId {get;set;}
        public int? FollowUpNumber {get;set;}
        public string InstanceId {get;set;}
        public string InstanceName {get;set;}
        public string AccountKey {get;set;}
        public string ConfigurationId {get;set;}
        public string ConfigurationVersion {get;set;}
        public string ConfigurationReadyVersion {get;set;}
        public string Title {get;set;}
        public string Prompt {get;set;}
        public string Branch {get;set;}
        public string AccountName {get;set;}
        public string Feature {get;set;}
        public string Lot {get;set;}
        public string Specification {get;set;}
        public string Repository {get;set;}
        public string[] RepositoryIds {get;set;}
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string PermissionProfile {get;set;}
        public CloudTaskExecutionObservation ObservedExecution {get;set;}
        public int? ExecutionVersion {get;set;}
        public string ExecutionState {get;set;}
        public string ExecutionObservedUtc {get;set;}
        public string ExecutionSetupState {get;set;}
        public string ExecutionSetupThreadId {get;set;}
        public string ExecutionSetupStartedUtc {get;set;}
        public string ExecutionSetupFinishedUtc {get;set;}
        public string Fingerprint {get;set;}
        public string State {get;set;}
        public string ThreadId {get;set;}
        public string TurnId {get;set;}
        public string Result {get;set;}
        public string Error {get;set;}
        public string SourceChannel {get;set;}
        public string SourceThread {get;set;}
        public string SourceHome {get;set;}
        public string SourceAccount {get;set;}
        public bool ReturnToSource {get;set;}
        public string ReturnState {get;set;}
        public string SourceReturnTurn {get;set;}
        public string SourceReturnState {get;set;}
        public string CreatedUtc {get;set;}
        public string UpdatedUtc {get;set;}
    }
    // Kept apart from CloudJob so a still-running older worker cannot discard
    // organization fields it does not understand when persisting task progress.
    internal sealed class CloudMissionRegistration
    {
        public string Id {get;set;}
        public string Kind {get;set;}
        public string ParentId {get;set;}
        public string RootJobId {get;set;}
        public string ParentTurnId {get;set;}
        public string FollowUpThreadId {get;set;}
        public int? FollowUpNumber {get;set;}
        public string Title {get;set;}
        public string InstanceId {get;set;}
        public string InstanceName {get;set;}
        public string AccountKey {get;set;}
        public string ConfigurationId {get;set;}
        public string ConfigurationVersion {get;set;}
        public string ConfigurationReadyVersion {get;set;}
        public string SourceChannel {get;set;}
        public string SourceThread {get;set;}
        public string SourceAccount {get;set;}
        public string SourceHome {get;set;}
        public string CreatedUtc {get;set;}
        public string Fingerprint {get;set;}
        public string Branch {get;set;}
        public string AccountName {get;set;}
        public string Feature {get;set;}
        public string Lot {get;set;}
        public string Specification {get;set;}
        public string Repository {get;set;}
        public string[] RepositoryIds {get;set;}
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string PermissionProfile {get;set;}
        public int? ExecutionVersion {get;set;}
    }
    // Separate from the legacy worker record: no prompt, token, native output,
    // or credentials. Absence of this receipt is not proof of non-delivery.
    internal sealed class CloudJobDispatchReceipt
    {
        public int Version {get;set;}
        public string JobId {get;set;}
        public string Binding {get;set;}
        public string PromptSha256 {get;set;}
        public string ClientUserMessageId {get;set;}
        public string AttemptedUtc {get;set;}
        public string TransportProof {get;set;}
        public string State {get;set;}
        public string TurnId {get;set;}
        public string Evidence {get;set;}
        public bool HistoryComplete {get;set;}
        public string LastCheckedUtc {get;set;}
        public string ErrorCode {get;set;}
        public int Checks {get;set;}
        public int ExplicitResumes {get;set;}
    }
    internal sealed class CloudJobs
    {
        private readonly RelayStore store;
        private readonly Func<DesktopInstance,ICloudTasksConnection> connection;
        private readonly Func<DesktopInstance,string,CancellationToken,Task<CloudConfiguration>> configuration;
        private readonly CloudJobMonitoring monitoring;
        internal CloudJobs(RelayStore store,Func<DesktopInstance,ICloudTasksConnection> connection=null,Func<DesktopInstance,string,CancellationToken,Task<CloudConfiguration>> configuration=null):this(store,connection,configuration,null){}
        internal CloudJobs(RelayStore store,Func<DesktopInstance,ICloudTasksConnection> connection,Func<DesktopInstance,string,CancellationToken,Task<CloudConfiguration>> configuration,Func<DateTime> utcNow)
        {
            this.store=store;
            this.monitoring=new CloudJobMonitoring(store,utcNow);
            this.connection=connection??(instance=>CloudInstanceConnection.OpenTasks(new AccountService(Path.GetDirectoryName(store.Root)),instance));
            this.configuration=configuration??ReadConfiguration;
        }
        private async Task<CloudConfiguration> ReadConfiguration(DesktopInstance instance,string id,CancellationToken token)
        {using(var client=CloudInstanceConnection.Open(new AccountService(Path.GetDirectoryName(store.Root)),instance))return await client.Get(id,token);}
        private static string Record(string id){RelayStore.MessageId(id);return "cloud-job-"+id+".dpapi";}
        private static string FollowUpRecord(string id){RelayStore.MessageId(id);return "cloud-follow-up-"+id+".dpapi";}
        private static string DispatchRecord(string id){RelayStore.MessageId(id);return "cloud-dispatch-"+id+".dpapi";}
        private static string DispatchBinding(CloudJob job)
        {var text=new StringBuilder();foreach(string field in new[]{job.Id,job.Fingerprint,job.InstanceId,job.AccountKey,job.ConfigurationId,job.ConfigurationVersion,job.ConfigurationReadyVersion,job.ThreadId,job.SourceChannel,job.SourceThread,job.SourceAccount,(job.SourceHome??"").TrimEnd('\\','/').ToUpperInvariant()}){string value=field??"";text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);}return CloudTasksBackend.PromptSha256(text.ToString());}
        private CloudJobDispatchReceipt ReadDispatch(CloudJob job)
        {
            if(!File.Exists(Path.Combine(store.Root,DispatchRecord(job.Id))))return null;
            var value=store.ReadRecord<CloudJobDispatchReceipt>(DispatchRecord(job.Id));
            if(value.Version!=1||value.JobId!=job.Id||value.Binding!=DispatchBinding(job)||value.ClientUserMessageId!=job.Id||!Regex.IsMatch(value.PromptSha256??"","^[0-9a-f]{64}$"))throw new CloudBackendException("reconciliation-binding","La preuve d'envoi ne correspond plus à cette mission. Aucun rapprochement ni nouvel envoi autorisé.");
            return value;
        }
        private void SaveDispatch(CloudJob job,CloudJobDispatchReceipt value){store.WriteRecord(DispatchRecord(job.Id),value);}
        internal CloudJobDispatchReceipt Reconciliation(string id){return ReadDispatch(Get(id));}
        private bool Exists(string id){return File.Exists(Path.Combine(store.Root,Record(id)))||File.Exists(Path.Combine(store.Root,FollowUpRecord(id)));}
        private static string RegistrationRecord(string id){RelayStore.MessageId(id);return "cloud-mission-"+id+".dpapi";}
        private CloudJob Hydrate(CloudJob job)
        {
            if(job.Kind=="follow-up"&&job.SchemaVersion!=3||job.SchemaVersion==3&&job.Kind!="follow-up")throw new InvalidOperationException("Le format de ce suivi Cloud est invalide.");
            if(File.Exists(Path.Combine(store.Root,RegistrationRecord(job.Id)))){
                var registered=store.ReadRecord<CloudMissionRegistration>(RegistrationRecord(job.Id));
                if(job.SchemaVersion==3&&(registered.Kind!="follow-up"||job.Kind!=registered.Kind||job.ParentId!=registered.ParentId||job.RootJobId!=registered.RootJobId||job.ParentTurnId!=registered.ParentTurnId||job.ThreadId!=registered.FollowUpThreadId||job.FollowUpNumber!=registered.FollowUpNumber))throw new InvalidOperationException("La filiation du suivi Cloud ne correspond plus au registre.");
                if(registered.Id!=job.Id||registered.Fingerprint!=job.Fingerprint||registered.Branch!=job.Branch||registered.InstanceId!=job.InstanceId||registered.AccountKey!=job.AccountKey||registered.ConfigurationId!=job.ConfigurationId||registered.ConfigurationVersion!=job.ConfigurationVersion||registered.ConfigurationReadyVersion!=job.ConfigurationReadyVersion||registered.SourceChannel!=job.SourceChannel||registered.SourceThread!=job.SourceThread||registered.SourceAccount!=job.SourceAccount||(registered.SourceHome!=job.SourceHome&&(String.IsNullOrEmpty(registered.SourceHome)||String.IsNullOrEmpty(job.SourceHome)||!RelayStore.SamePath(registered.SourceHome,job.SourceHome))))throw new InvalidOperationException("Le registre et la mission Cloud ne correspondent plus.");
                job.AccountName=registered.AccountName;job.Feature=registered.Feature;job.Lot=registered.Lot;job.Specification=registered.Specification;job.Repository=registered.Repository;job.RepositoryIds=registered.RepositoryIds;
                if((!String.IsNullOrEmpty(job.Model)&&job.Model!=registered.Model)||(!String.IsNullOrEmpty(job.ReasoningEffort)&&job.ReasoningEffort!=registered.ReasoningEffort)||(!String.IsNullOrEmpty(job.PermissionProfile)&&job.PermissionProfile!=registered.PermissionProfile))throw new InvalidOperationException("Les réglages d'exécution ne correspondent plus au registre Cloud.");
                job.Model=registered.Model;job.ReasoningEffort=registered.ReasoningEffort;job.PermissionProfile=registered.PermissionProfile;
                if(job.ExecutionVersion.HasValue&&job.ExecutionVersion!=registered.ExecutionVersion)throw new InvalidOperationException("La version d'exécution ne correspond plus au registre Cloud.");job.ExecutionVersion=registered.ExecutionVersion;
            }else if(job.SchemaVersion==3)throw new InvalidOperationException("Le registre durable de ce suivi Cloud est absent.");
            return job;
        }
        private void Register(CloudJob job)
        {
            if(String.IsNullOrEmpty(job.AccountName)&&job.Kind!="follow-up")return;
            if(File.Exists(Path.Combine(store.Root,RegistrationRecord(job.Id)))){Hydrate(job);return;}
            store.WriteRecord(RegistrationRecord(job.Id),new CloudMissionRegistration{Id=job.Id,Kind=job.Kind,ParentId=job.ParentId,RootJobId=job.RootJobId,ParentTurnId=job.ParentTurnId,FollowUpThreadId=job.Kind=="follow-up"?job.ThreadId:null,FollowUpNumber=job.FollowUpNumber,Title=job.Title,InstanceId=job.InstanceId,InstanceName=job.InstanceName,AccountKey=job.AccountKey,ConfigurationId=job.ConfigurationId,ConfigurationVersion=job.ConfigurationVersion,ConfigurationReadyVersion=job.ConfigurationReadyVersion,SourceChannel=job.SourceChannel,SourceThread=job.SourceThread,SourceAccount=job.SourceAccount,SourceHome=job.SourceHome,CreatedUtc=job.CreatedUtc,Fingerprint=job.Fingerprint,Branch=job.Branch,AccountName=job.AccountName,Feature=job.Feature,Lot=job.Lot,Specification=job.Specification,Repository=job.Repository,RepositoryIds=job.RepositoryIds,Model=job.Model,ReasoningEffort=job.ReasoningEffort,PermissionProfile=job.PermissionProfile,ExecutionVersion=job.ExecutionVersion});
        }
        private CloudJob[] Reservations()
        {
            return Directory.GetFiles(store.Root,"cloud-mission-*.dpapi",SearchOption.TopDirectoryOnly).Where(path=>Regex.IsMatch(Path.GetFileName(path),@"^cloud-mission-[0-9a-f]{32}\.dpapi$")).Select(path=>{
                SafeFiles.RejectLinks(path);var r=store.ReadRecord<CloudMissionRegistration>(Path.GetFileName(path));RegistrationRecord(r.Id);
                return new CloudJob{SchemaVersion=r.Kind=="follow-up"?3:String.IsNullOrEmpty(r.Model)&&String.IsNullOrEmpty(r.ReasoningEffort)&&String.IsNullOrEmpty(r.PermissionProfile)?1:2,Id=r.Id,Kind=r.Kind,ParentId=r.ParentId,RootJobId=r.RootJobId,ParentTurnId=r.ParentTurnId,ThreadId=r.FollowUpThreadId,FollowUpNumber=r.FollowUpNumber,Title=r.Title,InstanceId=r.InstanceId,InstanceName=r.InstanceName,AccountKey=r.AccountKey,ConfigurationId=r.ConfigurationId,ConfigurationVersion=r.ConfigurationVersion,ConfigurationReadyVersion=r.ConfigurationReadyVersion,SourceChannel=r.SourceChannel,SourceThread=r.SourceThread,SourceAccount=r.SourceAccount,SourceHome=r.SourceHome,CreatedUtc=r.CreatedUtc,Fingerprint=r.Fingerprint,Branch=r.Branch,AccountName=r.AccountName,Feature=r.Feature,Lot=r.Lot,Specification=r.Specification,Repository=r.Repository,RepositoryIds=r.RepositoryIds,Model=r.Model,ReasoningEffort=r.ReasoningEffort,PermissionProfile=r.PermissionProfile,ExecutionVersion=r.ExecutionVersion,State="reserved",ReturnState="none"};
            }).ToArray();
        }
        internal CloudJob Get(string id)
        {
            bool mission=File.Exists(Path.Combine(store.Root,Record(id))),followUp=File.Exists(Path.Combine(store.Root,FollowUpRecord(id)));
            if(mission&&followUp)throw new InvalidOperationException("Deux enregistrements Cloud utilisent le même identifiant. Aucun envoi autorisé.");
            var value=store.ReadRecord<CloudJob>(followUp?FollowUpRecord(id):Record(id));
            if(value.Id!=id||!new[]{1,2,3}.Contains(value.SchemaVersion)||followUp&&(value.SchemaVersion!=3||value.Kind!="follow-up"))throw new InvalidOperationException("Mission Cloud introuvable.");return Hydrate(value);
        }
        internal CloudJob[] List()
        {
            return Directory.GetFiles(store.Root,"cloud-job-*.dpapi",SearchOption.TopDirectoryOnly).Concat(Directory.GetFiles(store.Root,"cloud-follow-up-*.dpapi",SearchOption.TopDirectoryOnly).Where(path=>Regex.IsMatch(Path.GetFileName(path),@"^cloud-follow-up-[0-9a-f]{32}\.dpapi$"))).Select(path=>{
                SafeFiles.RejectLinks(path);return store.ReadRecord<CloudJob>(Path.GetFileName(path));
            }).Where(j=>new[]{1,2,3}.Contains(j.SchemaVersion)&&!String.IsNullOrEmpty(j.Id)).Select(Hydrate).OrderByDescending(j=>j.CreatedUtc).ToArray();
        }
        private void Save(CloudJob job){job.UpdatedUtc=DateTime.UtcNow.ToString("o");store.WriteRecord(job.Kind=="follow-up"?FollowUpRecord(job.Id):Record(job.Id),job);monitoring.Observe(job);}
        internal CloudJobMonitorSnapshot Monitor(CloudJob job){return monitoring.Read(job);}
        internal int MaximumParallel {get{return CloudCapacity.Read(store).MaxParallel;}}
        internal bool AdmissionReady(CloudJob job,CloudJob[] snapshot){return CloudJobWorker.CanStart(store,job,snapshot);}
        internal Dictionary<string,int> AdmissionPriorities()
        {var priorities=new Dictionary<string,int>(StringComparer.Ordinal);foreach(var mission in new CloudMissionQueue(store).List().Missions){priorities[mission.Id]=mission.Priority;if(!String.IsNullOrEmpty(mission.ObservedJobId))priorities[mission.ObservedJobId]=mission.Priority;}return priorities;}
        internal bool ScheduledDue(CloudJob job)
        {return (CloudJobWorker.RuntimePending(job)&&monitoring.CheckDue(job))||monitoring.NotificationDue(job);}
        internal bool NeedsSupervision(CloudJob job)
        {return CloudJobWorker.RuntimePending(job)||monitoring.PendingNotification(job);}
        internal static object Summary(CloudJob job,bool result,CloudJobMonitorSnapshot monitoring=null)
        {
            string text=job.Result??"";
            var pulls=Regex.Matches(text,@"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/[1-9][0-9]*\b").Cast<Match>().Select(m=>m.Value).Distinct().ToArray();
            var commits=text.Split('\n').Where(line=>!Regex.IsMatch(line,@"secret|token|password|api[\s_-]?key|master[\s_-]?key|vapid|broker",RegexOptions.IgnoreCase)).SelectMany(line=>Regex.Matches(line,"\\b(?:commit(?:\\s+sha)?|sha(?:\\s+commit)?)\\b\\s*[:=]?\\s*[`'\"]?([0-9a-f]{40})\\b",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>m.Groups[1].Value.ToLowerInvariant())).Distinct().ToArray();
            return new{job.Id,job.SchemaVersion,Kind=job.Kind??"mission",job.ParentId,job.RootJobId,job.ParentTurnId,job.FollowUpNumber,job.Title,job.InstanceId,job.InstanceName,job.AccountName,job.SourceChannel,job.SourceThread,job.ConfigurationId,job.ConfigurationVersion,job.ConfigurationReadyVersion,job.Repository,job.RepositoryIds,job.Feature,job.Lot,job.Specification,job.Branch,job.Model,job.ReasoningEffort,job.PermissionProfile,job.ObservedExecution,job.ExecutionVersion,ExecutionState=job.ExecutionState??(Execution(job).Required?"unverified":"inherited"),job.ExecutionObservedUtc,job.ExecutionSetupState,job.ExecutionSetupThreadId,job.ExecutionSetupStartedUtc,job.ExecutionSetupFinishedUtc,job.State,job.ThreadId,job.TurnId,job.ReturnState,job.SourceReturnState,ReportedPullRequests=pulls,ReportedCommitShas=commits,EvidenceState="reported-unverified",Monitoring=monitoring,job.Error,Result=result?job.Result:null,job.CreatedUtc,job.UpdatedUtc};
        }
        internal CloudJob[] Registry(RelaySession source,string repository)
        {
            if(source!=null)RequireSource(source);
            var jobs=List();return jobs.Concat(Reservations().Where(r=>!jobs.Any(j=>j.Id==r.Id))).Where(j=>(source==null||SameSource(j,source))&&(String.IsNullOrWhiteSpace(repository)||String.Equals(j.Repository,repository,StringComparison.OrdinalIgnoreCase))).OrderBy(j=>j.CreatedUtc).ToArray();
        }
        private static bool SameSource(CloudJob job,RelaySession source)
        {return job.SourceChannel==(source==null?null:source.Channel)&&job.SourceThread==(source==null?null:source.Thread)&&job.SourceAccount==(source==null?null:source.Account)&&(source==null?String.IsNullOrEmpty(job.SourceHome):!String.IsNullOrEmpty(job.SourceHome)&&RelayStore.SamePath(job.SourceHome,source.Home));}
        internal static string BranchSlug(string value,int limit)
        {
            string normalized=(value??"").Normalize(NormalizationForm.FormD);var chars=new StringBuilder();
            foreach(char c in normalized){if(CharUnicodeInfo.GetUnicodeCategory(c)==UnicodeCategory.NonSpacingMark)continue;if(c>='A'&&c<='Z')chars.Append(Char.ToLowerInvariant(c));else if(c>='a'&&c<='z'||c>='0'&&c<='9')chars.Append(c);else chars.Append('-');}
            string slug=Regex.Replace(chars.ToString(),"-+","-").Trim('-');if(slug.Length>limit)slug=slug.Substring(0,limit).TrimEnd('-');return slug;
        }
        private static string CanonicalLot(string lot)
        {
            if(String.IsNullOrWhiteSpace(lot))return null;
            var match=Regex.Match(lot.Trim(),@"^(?:lot-)?([0-9]{1,6}(?:[-.][0-9]{1,6}){0,3})$",RegexOptions.IgnoreCase);
            if(!match.Success)throw new InvalidOperationException("Indiquez un numéro de lot ou sous-lot, par exemple lot-003 ou lot-10-02.");
            string value=match.Groups[1].Value.Replace('.','-');return "lot-"+(value.IndexOf('-')<0?Int32.Parse(value,CultureInfo.InvariantCulture).ToString("D3",CultureInfo.InvariantCulture):value);
        }
        private static string LotKey(string lot)
        {return String.Join("-",CanonicalLot(lot).Substring(4).Split('-').Select(part=>Int32.Parse(part,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));}
        private static int PrimaryLot(string lot)
        {return Int32.Parse(CanonicalLot(lot).Substring(4).Split('-')[0],CultureInfo.InvariantCulture);}
        private static bool SameRepository(CloudJob job,string[] ids,string configurationId)
        {return ids.Length>0?(job.RepositoryIds??new string[0]).Intersect(ids,StringComparer.Ordinal).Any():job.ConfigurationId==configurationId;}
        private static string RequestFingerprint(CloudJobSpec spec,string instanceId,CloudTaskExecutionOptions execution)
        {
            // The Framework worker and .NET desktop escape JSON differently.
            // Hash unambiguous length-prefixed UTF-8 fields instead of JSON.
            var input=new StringBuilder();foreach(string field in new[]{spec.Title,spec.Prompt,instanceId,spec.ConfigurationId,spec.ReturnToSource?"true":"false",spec.Feature,spec.Lot,spec.Specification,spec.Repository}){string value=field??"";input.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);}
            if(execution.Required)foreach(string field in new[]{execution.Model,execution.ReasoningEffort,execution.PermissionProfile}){string value=field??"";input.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);}
            using(var sha=SHA256.Create())return (execution.Required?"v3:":"v2:")+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input.ToString()))).Replace("-","");
        }
        private static CloudTaskExecutionOptions Execution(CloudJob job)
        {return CloudTaskExecutionOptions.Validate(new CloudTaskExecutionOptions{Model=job.Model,ReasoningEffort=job.ReasoningEffort,PermissionProfile=job.PermissionProfile});}
        internal CloudJob Authorize(RelaySession session,string id)
        {
            var job=Get(id);if(session==null||job.SourceChannel!=session.Channel||job.SourceThread!=session.Thread||job.SourceAccount!=session.Account||!RelayStore.SamePath(job.SourceHome,session.Home))throw new InvalidOperationException("Cette mission Cloud appartient à un autre chat.");return job;
        }
        internal async Task<CloudJob> Submit(CloudJobSpec spec,RelaySession source,CancellationToken token)
        {
            if(spec==null||!spec.ExplicitDelegation)throw new InvalidOperationException("Une mission Cloud exige une délégation explicite de l'utilisateur.");
            spec=Json.Read<CloudJobSpec>(Json.Write(spec));
            if(source!=null){source=Json.Read<RelaySession>(Json.Write(source));RequireSource(source);}
            Record(spec.Id);
            if(String.IsNullOrWhiteSpace(spec.Title)||spec.Title.Length>200||String.IsNullOrWhiteSpace(spec.Prompt)||spec.Prompt.Length>24000||String.IsNullOrWhiteSpace(spec.ConfigurationId))throw new InvalidOperationException("Indiquez un titre, une mission et un environnement Cloud.");
            if((spec.Feature??"").Length>160||(spec.Specification??"").Length>1000||(spec.Repository??"").Length>200||((spec.Specification??"").Any(Char.IsControl)))throw new InvalidOperationException("La référence de fonctionnalité ou de spécification est invalide.");
            if(!String.IsNullOrEmpty(spec.Repository)&&!Regex.IsMatch(spec.Repository,@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))throw new InvalidOperationException("Le dépôt doit être indiqué sous la forme propriétaire/nom.");
            string requestedLot=CanonicalLot(spec.Lot);
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,spec.Instance);
            if(!String.IsNullOrEmpty(spec.ExpectedAccountKey)&&spec.ExpectedAccountKey!=instance.AccountKey)throw new CloudBackendException("target-binding","Le compte sélectionné a changé depuis la préparation de cette demande. Rechargez les instances.");
            if(source!=null)RequirePolicy(source,instance,accounts);
            using(store.Lease("cloud-admit-"+spec.Id)){
                var prior=Exists(spec.Id)?Get(spec.Id):null;
                var reserved=prior==null&&File.Exists(Path.Combine(store.Root,RegistrationRecord(spec.Id)))?Reservations().Single(r=>r.Id==spec.Id):null;
                var execution=CloudMissionPreferences.Resolve(store,spec,prior!=null?Execution(prior):reserved!=null?Execution(reserved):null);
                string fingerprint=RequestFingerprint(spec,instance.Id,execution);
                string legacyFingerprint=!execution.Required&&new[]{spec.Feature,spec.Lot,spec.Specification,spec.Repository}.All(String.IsNullOrWhiteSpace)?RelayPolicies.Fingerprint(new RelayJobSpec{Title=spec.Title,Prompt=spec.Prompt,To=instance.Id,Resource=spec.ConfigurationId,ExplicitDelegation=true,ReturnToSource=spec.ReturnToSource}):null;
                if(prior!=null){
                    if((prior.Fingerprint!=fingerprint&&(legacyFingerprint==null||prior.Fingerprint!=legacyFingerprint))||prior.AccountKey!=instance.AccountKey||prior.SourceChannel!=(source==null?null:source.Channel)||prior.SourceThread!=(source==null?null:source.Thread)||prior.SourceAccount!=(source==null?null:source.Account)||(source!=null&&!RelayStore.SamePath(prior.SourceHome,source.Home)))throw new InvalidOperationException("Cet identifiant Cloud appartient à une autre demande.");
                    Register(prior);return prior;
                }
                if(spec.ReturnToSource&&source==null)throw new InvalidOperationException("Le retour automatique exige un chat source connecté.");
                if(execution.Required){CloudJobWorker.RequireExecutionWorker(store);RequireReturnReceiver(source,spec.ReturnToSource);}
                var config=await configuration(instance,spec.ConfigurationId,token);
                if(config==null||config.Id!=spec.ConfigurationId||config.Status!="ready"||String.IsNullOrEmpty(config.VersionId)||!config.VersionRevision.HasValue||config.VersionRevision.Value<=1||String.IsNullOrEmpty(config.LatestReadyVersionId)||config.Draft!=null)throw new InvalidOperationException("L'environnement doit être publié, prêt et sans brouillon avant une mission.");
                if(source!=null)RequireSource(source);
                var profile=accounts.Data.Profiles.FirstOrDefault(p=>p.Key==instance.AccountKey);
                string account=profile==null?instance.Name:profile.Email;int at=(account??"").IndexOf('@');if(at>0)account=account.Substring(0,at);
                string accountSlug=BranchSlug(account,40),feature=BranchSlug(String.IsNullOrWhiteSpace(spec.Feature)?spec.Title:spec.Feature,72);
                if(accountSlug.Length==0||feature.Length==0)throw new InvalidOperationException("Indiquez un nom de compte et de fonctionnalité lisibles pour la branche.");
                string[] repositories=(config.Repositories??new List<CloudRepository>()).Select(r=>r.RepositoryId).Where(r=>!String.IsNullOrWhiteSpace(r)).Distinct().OrderBy(r=>r,StringComparer.Ordinal).ToArray();
                using(store.Lease("cloud-registry-v1")){
                    var registered=Reservations();var reservation=registered.SingleOrDefault(j=>j.Id==spec.Id);
                    if(reservation!=null){
                        if(reservation.Fingerprint!=fingerprint||reservation.InstanceId!=instance.Id||reservation.AccountKey!=instance.AccountKey||!SameSource(reservation,source))throw new InvalidOperationException("Cette réservation Cloud appartient à une autre demande.");
                        if(reservation.ConfigurationId!=config.Id||reservation.ConfigurationVersion!=config.VersionId||reservation.ConfigurationReadyVersion!=config.LatestReadyVersionId)throw new CloudBackendException("configuration-changed","L'environnement de cette réservation a changé. Aucun envoi n'est autorisé.");
                    }
                    var jobs=List();var existing=jobs.Concat(registered.Where(r=>!jobs.Any(j=>j.Id==r.Id))).Where(j=>j.Id!=spec.Id).ToArray();string lotName=reservation==null?requestedLot:reservation.Lot;
                    if(lotName==null){int lot=existing.Where(j=>SameRepository(j,repositories,config.Id)&&!String.IsNullOrEmpty(j.Lot)).Select(j=>PrimaryLot(j.Lot)).DefaultIfEmpty(0).Max()+1;if(lot>999999)throw new InvalidOperationException("Le registre a atteint le nombre maximal de lots.");lotName="lot-"+lot.ToString("D3",CultureInfo.InvariantCulture);}
                    string branch=reservation==null?"codex/"+accountSlug+"/"+feature+"/"+lotName:reservation.Branch;
                    if(existing.Any(j=>SameRepository(j,repositories,config.Id)&&((!String.IsNullOrEmpty(j.Lot)&&LotKey(j.Lot)==LotKey(lotName))||j.Branch==branch)))throw new InvalidOperationException("Ce lot ou cette branche est déjà attribué dans ce dépôt. Consultez le registre avant de déléguer.");
                    var job=new CloudJob{SchemaVersion=execution.Required?2:1,Id=spec.Id,InstanceId=instance.Id,InstanceName=instance.Name,AccountKey=instance.AccountKey,ConfigurationId=config.Id,ConfigurationVersion=config.VersionId,ConfigurationReadyVersion=config.LatestReadyVersionId,Title=spec.Title.Trim(),Prompt=spec.Prompt,AccountName=accountSlug,Feature=feature,Lot=lotName,Specification=spec.Specification,Repository=spec.Repository,RepositoryIds=repositories,Model=execution.Model,ReasoningEffort=execution.ReasoningEffort,PermissionProfile=execution.PermissionProfile,ExecutionVersion=reservation==null?(int?)CloudJobWorker.ExecutionVersion:reservation.ExecutionVersion,ExecutionState=execution.Required?"unverified":"inherited",Branch=branch,Fingerprint=fingerprint,State="queued",ReturnToSource=spec.ReturnToSource,ReturnState=spec.ReturnToSource?"waiting":"none",SourceChannel=source==null?null:source.Channel,SourceThread=source==null?null:source.Thread,SourceHome=source==null?null:source.Home,SourceAccount=source==null?null:source.Account,CreatedUtc=DateTime.UtcNow.ToString("o")};
                    if(reservation!=null){job.CreatedUtc=reservation.CreatedUtc;job.AccountName=reservation.AccountName;job.Feature=reservation.Feature;job.Specification=reservation.Specification;job.Repository=reservation.Repository;job.RepositoryIds=reservation.RepositoryIds;}
                    // Reserve before the queued job becomes visible to old workers.
                    // A failure after this write leaves an inspectable reservation,
                    // reused only by this exact request and its pinned identities.
                    if(execution.Required){CloudJobWorker.RequireExecutionWorker(store);RequireReturnReceiver(source,spec.ReturnToSource);}Register(job);Save(job);return job;
                }
            }
        }
        private static bool SameTarget(CloudJob left,CloudJob right)
        {return left.InstanceId==right.InstanceId&&left.AccountKey==right.AccountKey&&left.ThreadId==right.ThreadId&&left.ConfigurationId==right.ConfigurationId&&left.ConfigurationVersion==right.ConfigurationVersion&&left.ConfigurationReadyVersion==right.ConfigurationReadyVersion&&left.Branch==right.Branch&&left.Repository==right.Repository&&(left.RepositoryIds??new string[0]).SequenceEqual(right.RepositoryIds??new string[0])&&left.SourceChannel==right.SourceChannel&&left.SourceThread==right.SourceThread&&left.SourceAccount==right.SourceAccount&&!String.IsNullOrEmpty(left.SourceHome)&&!String.IsNullOrEmpty(right.SourceHome)&&RelayStore.SamePath(left.SourceHome,right.SourceHome);}
        private CloudJob FollowUpParent(CloudJob job)
        {
            if(job.Kind!="follow-up"||String.IsNullOrEmpty(job.ParentId)||String.IsNullOrEmpty(job.ParentTurnId)||String.IsNullOrEmpty(job.ThreadId)||!job.FollowUpNumber.HasValue||job.FollowUpNumber.Value<1)throw new CloudBackendException("follow-up-binding","Le suivi Cloud n’a pas de filiation vérifiable.");
            var parent=Get(job.ParentId);
            if(!SameTarget(parent,job)||parent.TurnId!=job.ParentTurnId||!new[]{"completed","failed"}.Contains(parent.State)||job.RootJobId!=(parent.RootJobId??parent.Id)||job.FollowUpNumber.Value!=(parent.FollowUpNumber??0)+1)throw new CloudBackendException("follow-up-binding","Le compte, le chat, la branche, le tour ou l’environnement du parent a changé. Aucun nouveau prompt envoyé.");
            return parent;
        }
        private static string FollowUpLock(CloudJob parent)
        {return "cloud-follow-up-"+RemotePeers.Hash(parent.AccountKey+"|"+parent.ThreadId);}
        private void VerifyLineage(CloudJob parent,string proposedId)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal){proposedId};var chain=new List<CloudJob>();var current=parent;
            for(int depth=0;depth<128;depth++){
                if(!seen.Add(current.Id))throw new CloudBackendException("follow-up-cycle","Cette filiation Cloud contient une boucle.");
                chain.Add(current);
                if(current.Kind!="follow-up"){
                    if(chain.Any(item=>item.Kind=="follow-up"&&item.RootJobId!=current.Id))throw new CloudBackendException("follow-up-binding","La racine du suivi Cloud ne correspond plus au registre.");
                    return;
                }
                current=FollowUpParent(current);
            }
            throw new CloudBackendException("follow-up-depth","La filiation Cloud dépasse la borne de vérification. Aucun nouveau prompt envoyé.");
        }
        private static string FollowUpFingerprint(CloudJobFollowUpSpec request,CloudJob parent,CloudTaskExecutionOptions execution)
        {
            var text=new StringBuilder();foreach(string field in new[]{request.ParentId,request.Title,request.Prompt,parent.Fingerprint,parent.InstanceId,parent.AccountKey,parent.ConfigurationId,parent.ConfigurationVersion,parent.ConfigurationReadyVersion,parent.ThreadId,parent.TurnId,parent.Branch,parent.Repository,String.Join("|",parent.RepositoryIds??new string[0]),execution.Model,execution.ReasoningEffort,execution.PermissionProfile}){string value=field??"";text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);}
            using(var sha=SHA256.Create())return "follow-up-v1:"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-","");
        }
        private void RequireFollowUpReceiver(RelaySession source)
        {
            RequireReturnReceiver(source,true);
            if(AgentProviders.Kind(store.Channel(source.Channel))!="codex-headless")return;
            var state=store.ReadRecord<RelayWorkerState>("worker.dpapi");
            if(!state.CloudJobSchemaVersion.HasValue||state.CloudJobSchemaVersion.Value<CloudJobWorker.SchemaVersion)throw new CloudBackendException("cloud-return-upgrade","Le moteur source sans fenêtre doit être mis à jour avant de recevoir les corrections de cette mission.");
        }
        internal Task<CloudJob> FollowUp(CloudJobFollowUpSpec request,RelaySession source,CancellationToken token)
        {
            if(request==null||!request.ExplicitDelegation)throw new InvalidOperationException("Un suivi Cloud exige une délégation ou une boucle de correction explicitement autorisée.");
            request=Json.Read<CloudJobFollowUpSpec>(Json.Write(request));Record(request.Id);Record(request.ParentId);token.ThrowIfCancellationRequested();
            if(source==null)throw new InvalidOperationException("Un suivi Cloud exige le chat orchestrateur qui a délégué la mission.");source=Json.Read<RelaySession>(Json.Write(source));RequireSource(source);
            if(request.Id==request.ParentId||String.IsNullOrWhiteSpace(request.Title)||request.Title.Length>200||String.IsNullOrWhiteSpace(request.Prompt)||request.Prompt.Length>24000)throw new InvalidOperationException("Indiquez un nouvel identifiant, un titre et les corrections attendues.");request.Title=request.Title.Trim();
            var parent=Authorize(source,request.ParentId);VerifyLineage(parent,request.Id);
            using(store.Lease("cloud-admit-"+request.Id))using(store.Lease(FollowUpLock(parent))){
                var prior=Exists(request.Id)?Get(request.Id):null;
                var reserved=prior==null&&File.Exists(Path.Combine(store.Root,RegistrationRecord(request.Id)))?Reservations().Single(item=>item.Id==request.Id):null;
                var frozen=prior??reserved;var inherited=Execution(parent);var execution=frozen!=null?Execution(frozen):inherited.Required?inherited:CloudMissionPreferences.Read(store);
                string fingerprint=FollowUpFingerprint(request,parent,execution);
                if(frozen!=null&&(frozen.Kind!="follow-up"||frozen.ParentId!=parent.Id||frozen.Fingerprint!=fingerprint||!SameSource(frozen,source)||!SameTarget(frozen,parent)||frozen.ParentTurnId!=parent.TurnId))throw new InvalidOperationException("Cet identifiant appartient à un autre suivi Cloud.");
                if(prior!=null)return Task.FromResult(prior);
                if(!new[]{"completed","failed"}.Contains(parent.State)||String.IsNullOrEmpty(parent.ThreadId)||String.IsNullOrEmpty(parent.TurnId))throw new CloudBackendException("follow-up-parent-active","Seule une mission terminée avec un tour Cloud identifié peut recevoir une correction. Le parent actif, annulé ou incertain reste intact.");
                var instance=Pinned(parent);VerifyOrigin(parent,instance);CloudJobWorker.RequireFollowUpWorker(store);CloudJobWorker.RequireExecutionWorker(store);RequireFollowUpReceiver(source);
                var registrations=Reservations();var jobs=List();var others=jobs.Concat(registrations.Where(item=>!jobs.Any(job=>job.Id==item.Id))).Where(item=>item.Id!=request.Id&&item.AccountKey==parent.AccountKey&&item.ThreadId==parent.ThreadId).ToArray();
                if(others.Any(item=>!new[]{"completed","failed","cancelled"}.Contains(item.State)))throw new CloudBackendException("follow-up-conflict","Une autre demande est déjà réservée dans ce chat Cloud. Terminez ou examinez cette demande avant une nouvelle correction.");
                var child=new CloudJob{SchemaVersion=3,Kind="follow-up",Id=request.Id,ParentId=parent.Id,RootJobId=parent.RootJobId??parent.Id,ParentTurnId=parent.TurnId,FollowUpNumber=(parent.FollowUpNumber??0)+1,ThreadId=parent.ThreadId,Title=request.Title,Prompt=request.Prompt,InstanceId=parent.InstanceId,InstanceName=parent.InstanceName,AccountKey=parent.AccountKey,ConfigurationId=parent.ConfigurationId,ConfigurationVersion=parent.ConfigurationVersion,ConfigurationReadyVersion=parent.ConfigurationReadyVersion,Branch=parent.Branch,AccountName=parent.AccountName,Feature=parent.Feature,Lot=parent.Lot,Specification=parent.Specification,Repository=parent.Repository,RepositoryIds=parent.RepositoryIds,Model=execution.Model,ReasoningEffort=execution.ReasoningEffort,PermissionProfile=execution.PermissionProfile,ExecutionVersion=CloudJobWorker.ExecutionVersion,ExecutionState="unverified",Fingerprint=fingerprint,SourceChannel=source.Channel,SourceThread=source.Thread,SourceHome=source.Home,SourceAccount=source.Account,ReturnToSource=true,ReturnState="waiting",State="queued",CreatedUtc=reserved==null?DateTime.UtcNow.ToString("o"):reserved.CreatedUtc};
                Register(child);Save(child);return Task.FromResult(child);
            }
        }
        private void RequireSource(RelaySession source)
        {
            if(String.IsNullOrEmpty(source.Thread))throw new InvalidOperationException("Le chat source est absent.");
            var channel=store.Channel(source.Channel);
            if(!channel.Enabled||channel.AccountKey!=source.Account||!RelayStore.SamePath(channel.Home,source.Home))throw new CloudBackendException("source-binding","Le compte ou le profil du chat source a changé.");
            try{if(AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(source.Home,"auth.json"))).Key!=source.Account)throw new InvalidOperationException();}
            catch{throw new CloudBackendException("source-binding","Le compte connecté dans le chat source n'est plus celui qui a autorisé la mission.");}
        }
        private void RequireReturnReceiver(RelaySession source,bool returns)
        {
            if(!returns||source==null||AgentProviders.Kind(store.Channel(source.Channel))!="codex-headless")return;
            var receiver=store.ReadRecord<RelayWorkerState>("worker.dpapi");
            if(!DesktopRuntime.SameProcess(receiver.Pid,receiver.Started)||!receiver.ExecutionVersion.HasValue||receiver.ExecutionVersion.Value<CloudJobWorker.ExecutionVersion)throw new CloudBackendException("cloud-return-upgrade","Le moteur de l'agent source sans fenêtre ne sait pas encore recevoir ces missions Cloud. Mettez à jour son suivi ou choisissez un résultat consultable sans retour automatique. Aucun départ n'a été autorisé.");
        }
        private void RequirePolicy(RelaySession source,DesktopInstance instance,AccountService accounts)
        {
            var channel=store.Channel(source.Channel);var policy=RelayPolicies.Load(store);
            if(policy.Projects.Any(p=>RelayStore.SamePath(p.Workspace,channel.Workspace))||store.Channels().Any(c=>RelayStore.SamePath(c.Home,accounts.Instances.Home(instance))&&RelayStore.SamePath(c.Workspace,channel.Workspace)&&policy.Agents.Any(a=>a.Channel==c.Id)))
                throw new CloudBackendException("cloud-policy","Ce dossier ou ce destinataire possède des règles avancées. La délégation Cloud n'a pas encore de correspondance explicite avec ces règles ; aucun départ n'est autorisé par ce raccourci.");
        }
        private void VerifyOrigin(CloudJob job,DesktopInstance instance)
        {
            if(String.IsNullOrEmpty(job.SourceChannel))return;
            var source=new RelaySession{Channel=job.SourceChannel,Thread=job.SourceThread,Home=job.SourceHome,Account=job.SourceAccount};RequireSource(source);
            RequirePolicy(source,instance,new AccountService(Path.GetDirectoryName(store.Root)));
        }
        private async Task VerifyConfiguration(CloudJob job,DesktopInstance instance,CancellationToken token)
        {
            var fresh=await configuration(instance,job.ConfigurationId,token);
            if(fresh==null||fresh.Id!=job.ConfigurationId||fresh.VersionId!=job.ConfigurationVersion||fresh.LatestReadyVersionId!=job.ConfigurationReadyVersion||fresh.Draft!=null||fresh.Status!="ready")throw new CloudBackendException("configuration-changed","L'environnement a changé depuis la préparation. Créez une nouvelle mission après vérification.");
            if(job.Kind=="follow-up"&&!(fresh.Repositories??new List<CloudRepository>()).Select(repo=>repo.RepositoryId).Where(id=>!String.IsNullOrWhiteSpace(id)).Distinct().OrderBy(id=>id,StringComparer.Ordinal).SequenceEqual(job.RepositoryIds??new string[0]))throw new CloudBackendException("configuration-changed","Les dépôts de l’environnement du parent ont changé. Aucun suivi envoyé.");
        }
        private bool ObserveExecution(CloudJob job,CloudTaskExecutionObservation observed,CloudTaskExecutionOptions requested)
        {
            job.ObservedExecution=observed;job.ExecutionObservedUtc=DateTime.UtcNow.ToString("o");
            bool mismatch=observed!=null&&((requested.Model!=null&&observed.Model!=null&&requested.Model!=observed.Model)||(requested.ReasoningEffort!=null&&observed.ReasoningEffort!=null&&requested.ReasoningEffort!=observed.ReasoningEffort)||(requested.PermissionProfile!=null&&observed.PermissionProfile!=null&&requested.PermissionProfile!=observed.PermissionProfile));
            job.ExecutionState=observed!=null&&observed.Matches(requested)?"verified":mismatch?"mismatch":"unknown";Save(job);
            return job.ExecutionState=="verified";
        }
        private async Task VerifyExecution(CloudJob job,ICloudTasksExecutionConnection client,CloudTaskExecutionOptions requested,CancellationToken token)
        {
            if(!ObserveExecution(job,await client.ReadExecution(job.ThreadId,token),requested))throw new CloudBackendException("execution-unverified","Le Cloud n'a pas confirmé le modèle, le raisonnement ou les permissions demandés. Aucun nouveau prompt métier envoyé. Consultez les réglages demandés et observés dans le registre.");
        }
        private async Task<bool> PrepareExecution(CloudJob job,DesktopInstance instance,ICloudTasksExecutionConnection client,CloudTaskExecutionOptions requested,CloudTaskThread thread,CancellationToken token)
        {
            if(!String.IsNullOrEmpty(job.ExecutionSetupThreadId)&&job.ExecutionSetupThreadId!=job.ThreadId)throw new CloudBackendException("execution-setup-binding","Le chat ne correspond plus au journal de préparation. Aucun réglage ni prompt envoyé.");
            // A restart after applying/uncertain is reconciled only from a fresh
            // native read. Never replay a permissions mutation after ambiguity.
            if(ObserveExecution(job,await client.ReadExecution(job.ThreadId,token),requested)){
                job.ExecutionSetupState=String.IsNullOrEmpty(job.ExecutionSetupState)?"not-needed":job.ExecutionSetupState=="not-needed"?"not-needed":"verified";job.ExecutionSetupThreadId=job.ThreadId;job.ExecutionSetupFinishedUtc=DateTime.UtcNow.ToString("o");Save(job);return true;
            }
            if(!String.IsNullOrEmpty(job.ExecutionSetupState))throw new CloudBackendException("execution-setup-unconfirmed","La préparation des réglages a déjà été tentée et le Cloud ne les confirme pas. Aucune nouvelle application automatique ; aucun prompt métier envoyé.");
            if(job.State!="prepared"&&job.State!="attention"||!String.IsNullOrEmpty(job.TurnId)||thread==null||thread.Id!=job.ThreadId||thread.AccountKey!=job.AccountKey||thread.VerifiedEnvironmentConfigId!=job.ConfigurationId||thread.Status!="idle"||thread.LatestTurn!=null||thread.AwaitingApproval||thread.AwaitingInput)throw new CloudBackendException("execution-setup-unsafe","Ces réglages ne peuvent être appliqués qu'au nouveau chat vierge réservé à cette mission, sans travail ni intervention en attente. Aucun réglage ni prompt envoyé.");
            VerifyOrigin(job,instance);await VerifyConfiguration(job,instance,token);VerifyOrigin(job,instance);
            if(RelayDispatch.Read(store).Mode!="running")return false;
            job.ExecutionSetupState="applying";job.ExecutionSetupThreadId=job.ThreadId;job.ExecutionSetupStartedUtc=DateTime.UtcNow.ToString("o");Save(job);
            try{await client.ConfigureExecution(job.ThreadId,requested,token);job.ExecutionSetupState="applied";Save(job);}
            catch{job.ExecutionSetupState="uncertain";Save(job);throw;}
            // The configure reply is not proof of persisted settings: read again
            // without overrides before allowing the first business prompt.
            if(!ObserveExecution(job,await client.ReadExecution(job.ThreadId,token),requested)){
                job.ExecutionSetupState="unconfirmed";Save(job);throw new CloudBackendException("execution-setup-unconfirmed","Le Cloud n'a pas confirmé les réglages après leur application unique. Aucun nouvel essai automatique ni prompt métier envoyé.");
            }
            job.ExecutionSetupState="verified";job.ExecutionSetupFinishedUtc=DateTime.UtcNow.ToString("o");Save(job);return true;
        }
        private DesktopInstance Pinned(CloudJob job)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,job.InstanceId);
            if(instance.AccountKey!=job.AccountKey)throw new InvalidOperationException("Le compte permanent de la mission Cloud a changé.");return instance;
        }
        private static string DispatchPrompt(CloudJob job)
        {
            string organization=String.IsNullOrEmpty(job.Lot)?"":"\nRegistre : "+job.AccountName+" / "+job.Feature+" / "+job.Lot+". Dépôt déclaré : "+(job.Repository??"non précisé")+". Référence du cahier des charges : "+(job.Specification??"à rapprocher par l'orchestrateur")+".";
            if(job.Kind=="follow-up")return "[CREEZIO_CLOUD_FOLLOW_UP:"+job.Id+"]\nCorrection explicitement déléguée par le même orchestrateur. Suite "+job.FollowUpNumber+" de la mission "+job.ParentId+" ; tour précédent "+job.ParentTurnId+". Ce chat, ce compte et son environnement restent ceux du développeur initial.\nTitre : "+job.Title+organization+"\n"+job.Prompt+"\n\nConserve exclusivement la branche "+job.Branch+" et mets à jour sa PR existante. Ne crée ni nouveau chat, ni nouvelle branche, ni nouvelle PR. Ne modifie ni ne fusionne main. Si la PR ou les droits manquent, signale ce blocage. Donne les tests, le SHA, la branche et l’URL de PR mise à jour. Le Switcher suit ce nouveau tour et rapporte sa réponse finale ; aucun appel au compte orchestrateur ni boucle d’attente nécessaire. N’inclus aucun secret.";
            return "[CREEZIO_CLOUD_REQUEST:"+job.Id+"]\nMission explicitement déléguée via Account Switcher. Applique les permissions de ce compte.\nTitre : "+job.Title+organization+"\n"+job.Prompt+"\n\nLe Switcher suit ce tour et transmettra son résultat au chat source si le retour a été demandé. Termine normalement avec une réponse finale et tes preuves ; aucun outil de notification, appel au compte source ou boucle d'attente n'est nécessaire.\n\nPour tout changement de code, utilise exclusivement la branche "+job.Branch+". Ne modifie ni ne fusionne main. Si un accès GitHub manque, signale le blocage. Retourne les tests, le SHA du commit, la branche et l'URL de PR si créée. N'inclus aucun secret. Une réussite déclarée sera vérifiée par l'orchestrateur.";
        }
        internal static string AdvanceDiagnostic(string phase,Exception error,CancellationToken token)
        {
            var phases=new[]{"preflight","repository-binding","connect","configuration","thread-admission","thread-start","read-thread","execution-settings","turn-admission","turn-start","read-thread-after-send","execution-verification","read-turn","result-check"};
            if(!phases.Contains(phase))phase="unknown";string type=error==null?"unknown":error.GetType().Name;if(!Regex.IsMatch(type,@"^[A-Za-z][A-Za-z0-9_]{0,79}$"))type="unknown";
            var parts=new List<string>{"phase="+phase,"type="+type};var backend=error as CloudBackendException;
            if(backend!=null){parts.Add("code="+(Regex.IsMatch(backend.Code??"",@"^[a-z][a-z0-9-]{0,79}$")?backend.Code:"redacted"));if(backend.HttpStatus.HasValue&&backend.HttpStatus.Value>=100&&backend.HttpStatus.Value<=599)parts.Add("http="+backend.HttpStatus.Value.ToString(CultureInfo.InvariantCulture));}
            // Cancellation may be the worker deadline or a caller cancellation.
            // Do not assert which one, nor copy exception/inner/payload text.
            if(error is OperationCanceledException){parts.Add("reason=timeout-or-cancelled");parts.Add("tokenCancelled="+(token.IsCancellationRequested?"true":"false"));}
            return "["+String.Join(";",parts)+"]";
        }
        internal async Task<CloudJob> Advance(string id,CancellationToken token,bool scheduled=false)
        {
            using(store.Lease("cloud-run-"+id)){
                var job=Get(id);
                if(job.ReturnState=="sending"){job.ReturnState="uncertain";Save(job);monitoring.Notification(job,"uncertain","Remise au chat source interrompue. Aucun renvoi automatique.");}
                if(new[]{"completed","failed","uncertain","cancelled"}.Contains(job.State))return job;
                if(job.State=="creating"||job.State=="sending"){
                    job.State="uncertain";job.Error="Envoi interrompu : résultat à rapprocher, aucun renvoi automatique.";Save(job);return job;
                }
                if(job.State=="attention"&&String.IsNullOrEmpty(job.ThreadId))return job;
                if(scheduled&&!monitoring.CheckDue(job))return job;
                using(var followUpLease=job.Kind=="follow-up"?store.Lease(FollowUpLock(job)):null){
                string phase="preflight";
                try{
                    monitoring.BeginCheck(job);
                    if(job.State=="queued"&&!job.ExecutionVersion.HasValue&&!Execution(job).Required&&CloudMissionPreferences.Read(store).Required)throw new CloudBackendException("execution-missing","Cette ancienne demande ne transmet pas les réglages Cloud exigés par vos préférences. Utilisez la nouvelle commande de délégation ou rechargez l'intégration du Switcher. Aucun chat Cloud ni prompt métier créé.");
                    var instance=Pinned(job);
                    if(String.IsNullOrEmpty(job.TurnId)){phase="repository-binding";await CloudQueueRepositoryBinding.Require(store,job,token);}
                    using(var client=connection(instance)){
                        var requested=Execution(job);var execution=client as ICloudTasksExecutionConnection;
                        if(job.Kind=="follow-up"){
                            CloudJobWorker.RequireFollowUpWorker(store);RequireFollowUpReceiver(new RelaySession{Channel=job.SourceChannel,Thread=job.SourceThread,Home=job.SourceHome,Account=job.SourceAccount});VerifyLineage(FollowUpParent(job),job.Id);
                            if(execution==null)throw new CloudBackendException("execution-unsupported","Ce moteur ne sait pas vérifier les réglages du chat existant. Aucun suivi envoyé.");
                        }
                        if(requested.Required){CloudJobWorker.RequireExecutionWorker(store);RequireReturnReceiver(String.IsNullOrEmpty(job.SourceChannel)?null:new RelaySession{Channel=job.SourceChannel},job.ReturnToSource);if(execution==null)throw new CloudBackendException("execution-unsupported","Ce moteur Cloud ne sait pas appliquer et vérifier les réglages d'exécution de cette mission. Mettez à jour le Switcher avant de la reprendre.");}
                        phase="connect";await client.Connect(token);
                        if(job.State=="queued"&&job.Kind=="follow-up"){job.State="prepared";Save(job);}
                        if(job.State=="queued"){
                            if(RelayDispatch.Read(store).Mode!="running")return job;
                            phase="configuration";VerifyOrigin(job,instance);await VerifyConfiguration(job,instance,token);
                            phase="thread-admission";
                            FileStream capacity;
                            try{capacity=store.Lease("cloud-capacity-v1");}
                            catch(IOException e){if((e.HResult&0xffff)==32||(e.HResult&0xffff)==33)return job;throw;}
                            using(capacity){
                                if(!CloudJobWorker.CanStart(store,job,List()))return job;
                                if(RelayDispatch.Read(store).Mode!="running")return job;
                                VerifyOrigin(job,instance);
                                job.State="creating";Save(job);
                            }
                            phase="thread-start";var created=requested.Required?await execution.Start(job.ConfigurationId,null,requested,token):await client.Start(job.ConfigurationId,null,token);
                            if(created==null||String.IsNullOrEmpty(created.Id)||created.AccountKey!=job.AccountKey)throw new InvalidOperationException("La création du chat Cloud n'a pas confirmé son compte.");
                            job.ThreadId=created.Id;job.State="prepared";Save(job);
                        }
                        phase="read-thread";var thread=await client.Read(job.ThreadId,token);
                        if(thread==null||thread.Id!=job.ThreadId||thread.AccountKey!=job.AccountKey||thread.VerifiedEnvironmentConfigId!=job.ConfigurationId)throw new InvalidOperationException("Le service n'a pas confirmé le compte et l'environnement de cette conversation. Aucun prompt envoyé.");
                        if(String.IsNullOrEmpty(job.TurnId))monitoring.CheckFinished(job,null);
                        if(job.State=="prepared"||(job.State=="attention"&&String.IsNullOrEmpty(job.TurnId))){
                            if(RelayDispatch.Read(store).Mode!="running")return job;
                            if(thread.AwaitingApproval||thread.AwaitingInput||thread.Status!="idle"||(thread.LatestTurn!=null&&new[]{"inProgress","running"}.Contains(thread.LatestTurn.Status))){job.State=job.Kind=="follow-up"?"attention":"prepared";job.Error="La conversation Cloud travaille ou attend une intervention native. Aucun prompt envoyé.";Save(job);return job;}
                            phase="configuration";VerifyOrigin(job,instance);await VerifyConfiguration(job,instance,token);VerifyOrigin(job,instance);
                            phase="execution-settings";
                            if(job.Kind=="follow-up"){
                                if(thread.LatestTurn==null||thread.LatestTurn.Id!=job.ParentTurnId||!new[]{"completed","failed","interrupted"}.Contains(thread.LatestTurn.Status))throw new CloudBackendException("follow-up-turn-changed","Le chat a reçu un autre tour depuis la mission parent. Utilisez le dernier suivi identifié ; aucun prompt ajouté à un travail différent.");
                                await VerifyExecution(job,execution,requested,token);
                                thread=await client.Read(job.ThreadId,token);
                                if(thread==null||thread.Id!=job.ThreadId||thread.AccountKey!=job.AccountKey||thread.VerifiedEnvironmentConfigId!=job.ConfigurationId||thread.Status!="idle"||thread.AwaitingApproval||thread.AwaitingInput||thread.LatestTurn==null||thread.LatestTurn.Id!=job.ParentTurnId||!new[]{"completed","failed","interrupted"}.Contains(thread.LatestTurn.Status))throw new CloudBackendException("follow-up-native-changed","Le chat parent a changé pendant la vérification. Aucun suivi ni changement de permissions envoyé.");
                            }else if(requested.Required&&!await PrepareExecution(job,instance,execution,requested,thread,token))return job;
                            VerifyOrigin(job,instance);
                            if(RelayDispatch.Read(store).Mode!="running")return job;
                            string prompt=DispatchPrompt(job);
                            phase="turn-admission";prompt=new CloudMissionQueue(store).ScopePrompt(job,prompt,List());
                            FileStream turnCapacity;try{turnCapacity=store.Lease("cloud-capacity-v1");}catch(IOException error){if((error.HResult&0xffff)==32||(error.HResult&0xffff)==33)return job;throw;}
                            using(turnCapacity){if(!CloudJobWorker.CanStart(store,job,List()))return job;VerifyOrigin(job,instance);if(RelayDispatch.Read(store).Mode!="running")return job;
                                var previous=ReadDispatch(job);SaveDispatch(job,new CloudJobDispatchReceipt{Version=1,JobId=job.Id,Binding=DispatchBinding(job),PromptSha256=CloudTasksBackend.PromptSha256(prompt),ClientUserMessageId=job.Id,AttemptedUtc=DateTime.UtcNow.ToString("o"),TransportProof="unknown",State="pending",Checks=previous==null?0:previous.Checks,ExplicitResumes=previous==null?0:previous.ExplicitResumes});job.State="sending";Save(job);}
                            phase="turn-start";var turn=requested.Required?await execution.StartTurn(job.ThreadId,prompt,job.Id,requested,token):await client.StartTurn(job.ThreadId,prompt,job.Id,token);
                            if(turn==null||String.IsNullOrEmpty(turn.Id))throw new InvalidOperationException("Le service n'a pas confirmé le tour Cloud.");
                            if(job.Kind=="follow-up"&&List().Any(previous=>previous.Id!=job.Id&&previous.AccountKey==job.AccountKey&&previous.ThreadId==job.ThreadId&&previous.TurnId==turn.Id))throw new CloudBackendUncertainException("le service a renvoyé un ancien tour au lieu du nouveau suivi");
                            job.TurnId=turn.Id;job.State="running";Save(job);
                            var acknowledged=ReadDispatch(job);if(acknowledged!=null){acknowledged.TransportProof="acknowledged";acknowledged.State="matched";acknowledged.TurnId=turn.Id;acknowledged.Evidence="native-turn-start-response";SaveDispatch(job,acknowledged);}
                            phase="read-thread-after-send";thread=await client.Read(job.ThreadId,token);
                            if(thread==null||thread.Id!=job.ThreadId||thread.AccountKey!=job.AccountKey||thread.VerifiedEnvironmentConfigId!=job.ConfigurationId)throw new InvalidOperationException("L'identité de la conversation Cloud a changé après l'envoi.");
                            if(requested.Required){phase="execution-verification";await VerifyExecution(job,execution,requested,token);}
                        }
                        phase="result-check";
                        if(!String.IsNullOrEmpty(job.TurnId)&&requested.Required&&job.ExecutionState!="verified"){
                            job.State="attention";Save(job);phase="execution-verification";await VerifyExecution(job,execution,requested,token);phase="result-check";
                        }
                        var latest=thread.LatestTurn;
                        if(!String.IsNullOrEmpty(job.TurnId)&&(latest==null||latest.Id!=job.TurnId||!latest.TextComplete&&new[]{"completed","failed","interrupted"}.Contains(latest.Status))){
                            var turns=client as ICloudTaskTurnConnection;
                            if(turns!=null){phase="read-turn";latest=await turns.ReadTurn(job.ThreadId,job.TurnId,token);}
                            if(latest==null||latest.Id!=job.TurnId){job.State="attention";job.Error="Le tour exact de cette mission n’a pas été retrouvé. Un autre message existe dans le chat ; son résultat ne sera pas utilisé.";Save(job);return job;}
                        }
                        if((thread.AwaitingApproval||thread.AwaitingInput)&&(latest==null||!new[]{"completed","failed","interrupted"}.Contains(latest.Status))){job.State="attention";job.Error=thread.AwaitingApproval?"Approbation native attendue dans Codex Cloud. Aucune approbation automatique.":"Réponse utilisateur attendue dans Codex Cloud.";Save(job);return job;}
                        if(latest!=null&&latest.Id==job.TurnId){
                            if(latest.Status=="completed"){
                                if(!latest.TextComplete||String.IsNullOrWhiteSpace(latest.Text)){job.State="attention";job.Error="Le tour Cloud est terminé, mais sa réponse finale complète n'est pas disponible. Aucun résultat remis.";}
                                else {job.State="completed";job.Result=latest.Text;job.Error=null;job.ReturnState=job.ReturnToSource?"pending":"none";}
                            }
                            else if(latest.Status=="failed"||latest.Status=="interrupted"){job.State="failed";job.Result=latest.Text;job.Error="Exécution Cloud : "+latest.Status;job.ReturnState=job.ReturnToSource?"pending":"none";}
                            else {job.State="running";job.Error=null;}
                            Save(job);
                        }
                        monitoring.CheckFinished(job,null);
                    }
                }catch(Exception e){
                    bool dispatched=job.State=="creating"||job.State=="sending";
                    string diagnostic=AdvanceDiagnostic(phase,e,token);var backend=e as CloudBackendException;
                    if(dispatched){if(phase=="turn-start"&&e is CloudTaskNotDispatchedException){var receipt=ReadDispatch(job);if(receipt!=null){receipt.TransportProof="preflight-not-sent";receipt.State="not-sent-confirmed";SaveDispatch(job,receipt);}}job.State="uncertain";job.Error="Résultat d'envoi Cloud incertain. Aucun renvoi automatique. "+diagnostic;}
                    else if(backend!=null&&backend.Code=="queue-repository-mismatch"&&job.State=="queued"&&String.IsNullOrEmpty(job.ThreadId)&&String.IsNullOrEmpty(job.TurnId)){job.State="failed";job.Error=backend.Message+" "+diagnostic;job.ReturnState=job.ReturnToSource?"pending":"none";}
                    else {job.Error=(backend!=null?backend.Message:"La mission Cloud n'a pas pu être vérifiée. Vérifiez le compte, l'environnement et la connexion dans Codex.")+" "+diagnostic;if(job.State=="queued"||job.State=="prepared")job.State="attention";}
                    Save(job);
                    monitoring.CheckFinished(job,"La vérification Cloud a échoué. "+diagnostic);
                    if(e is OperationCanceledException)throw;
                }
                return job;
                }
            }
        }
        internal async Task<CloudJob> Reconcile(string id,RelaySession source,CancellationToken token,bool trustedOperator=false,bool resumeProvenNotSent=false)
        {
            if(source==null&&!trustedOperator)throw new InvalidOperationException("Le rapprochement exige le chat source exact ou une action opérateur explicite.");
            using(store.Lease("cloud-run-"+id)){
                var job=source==null?Get(id):Authorize(source,id);if(source!=null)RequireSource(source);
                var instance=Pinned(job);VerifyOrigin(job,instance);
                var receipt=ReadDispatch(job);
                if(job.State!="uncertain"){
                    if(receipt!=null&&receipt.State=="matched"&&receipt.TurnId==job.TurnId)return job;
                    throw new InvalidOperationException("Seul un envoi Cloud incertain peut être rapproché ici.");
                }
                if(receipt==null){
                    string prompt=new CloudMissionQueue(store).ScopePrompt(job,DispatchPrompt(job),List());
                    receipt=new CloudJobDispatchReceipt{Version=1,JobId=job.Id,Binding=DispatchBinding(job),PromptSha256=CloudTasksBackend.PromptSha256(prompt),ClientUserMessageId=job.Id,AttemptedUtc=job.CreatedUtc,TransportProof="legacy-unknown",State="pending"};
                }
                receipt.Checks++;receipt.LastCheckedUtc=DateTime.UtcNow.ToString("o");receipt.ErrorCode=null;SaveDispatch(job,receipt);monitoring.BeginCheck(job);
                if(String.IsNullOrEmpty(job.ThreadId)){receipt.State="no-confirmed-thread";SaveDispatch(job,receipt);return job;}
                try{
                    using(var client=connection(instance)){
                        var reconcile=client as ICloudTaskReconciliationConnection;
                        if(reconcile==null){receipt.State="unsupported";SaveDispatch(job,receipt);return job;}
                        await client.Connect(token);DateTime attempted;
                        if(!DateTime.TryParse(receipt.AttemptedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out attempted))throw new CloudBackendException("reconciliation-binding","L'horodatage de la preuve d'envoi est invalide.");
                        var found=await reconcile.FindSubmittedTurn(job.ThreadId,job.ConfigurationId,job.Id,receipt.PromptSha256,attempted,token);
                        if(found==null||found.AccountKey!=job.AccountKey||found.ThreadId!=job.ThreadId||found.ConfigurationId!=job.ConfigurationId)throw new CloudBackendException("reconciliation-binding","La lecture native ne confirme pas ce compte, ce chat et son environnement.");
                        VerifyOrigin(job,instance);receipt.HistoryComplete=found.HistoryComplete;
                        if(found.Turn!=null){
                            if(!found.HistoryComplete||String.IsNullOrEmpty(found.Turn.Id)||!new[]{"client-id-and-prompt-sha256","exact-prompt-sha256"}.Contains(found.Evidence)||(!String.IsNullOrEmpty(job.TurnId)&&job.TurnId!=found.Turn.Id)||List().Any(other=>other.Id!=job.Id&&other.AccountKey==job.AccountKey&&other.ThreadId==job.ThreadId&&other.TurnId==found.Turn.Id))throw new CloudBackendException("reconciliation-conflict","Le tour rapproché ne correspond pas à une preuve unique de cette mission.");
                            var turns=client as ICloudTaskTurnConnection;if(turns==null)throw new CloudBackendException("reconciliation-unsupported","Ce moteur ne sait pas relire le tour exact sans en créer un autre.");
                            var turn=await turns.ReadTurn(job.ThreadId,found.Turn.Id,token);if(turn==null||turn.Id!=found.Turn.Id)throw new CloudBackendException("reconciliation-unconfirmed","Le tour rapproché n'a pas été confirmé par la relecture native.");
                            VerifyOrigin(job,instance);
                            job.TurnId=turn.Id;job.State="running";job.Error=null;Save(job);
                            receipt.State="matched";receipt.TurnId=turn.Id;receipt.Evidence=found.Evidence;receipt.TransportProof="native-user-message";SaveDispatch(job,receipt);
                            var execution=Execution(job);if(execution.Required){var configured=client as ICloudTasksExecutionConnection;if(configured==null)throw new CloudBackendException("execution-unverified","La mission a été retrouvée mais ses réglages doivent être vérifiés.");await VerifyExecution(job,configured,execution,token);}
                            if(turn.Status=="completed"){
                                if(turn.TextComplete&&!String.IsNullOrWhiteSpace(turn.Text)){job.State="completed";job.Result=turn.Text;job.ReturnState=job.ReturnToSource?"pending":"none";}
                                else{job.State="attention";job.Error="Le tour exact a été retrouvé, mais sa réponse finale complète reste indisponible. Aucun nouvel envoi autorisé.";}
                            }else if(turn.Status=="failed"||turn.Status=="interrupted"){job.State="failed";job.Result=turn.Text;job.Error="Exécution Cloud : "+turn.Status;job.ReturnState=job.ReturnToSource?"pending":"none";}
                            else if(!new[]{"inProgress","running"}.Contains(turn.Status)){job.State="attention";job.Error="Le tour exact a été retrouvé ; son état natif nécessite une vérification. Aucun nouvel envoi autorisé.";}
                            Save(job);monitoring.CheckFinished(job,null);return job;
                        }
                        receipt.State=found.HistoryComplete?"not-observed":"history-incomplete";
                        if(resumeProvenNotSent){
                            if(receipt.TransportProof!="preflight-not-sent"||!found.HistoryComplete||!String.IsNullOrEmpty(job.TurnId))throw new CloudBackendException("reconciliation-no-resend-proof","L'absence de tour ne prouve pas l'absence d'envoi. Aucune reprise autorisée.");
                            var thread=await client.Read(job.ThreadId,token);
                            if(thread==null||thread.Id!=job.ThreadId||thread.AccountKey!=job.AccountKey||thread.VerifiedEnvironmentConfigId!=job.ConfigurationId||thread.Status!="idle"||thread.AwaitingApproval||thread.AwaitingInput||(job.Kind=="follow-up"?(thread.LatestTurn==null||thread.LatestTurn.Id!=job.ParentTurnId||!new[]{"completed","failed","interrupted"}.Contains(thread.LatestTurn.Status)):thread.LatestTurn!=null))throw new CloudBackendException("reconciliation-native-changed","Le chat a changé ou attend une intervention. Aucun nouvel envoi autorisé.");
                            await VerifyConfiguration(job,instance,token);VerifyOrigin(job,instance);
                            receipt.State="explicitly-resumed";receipt.ExplicitResumes++;SaveDispatch(job,receipt);job.State="prepared";job.Error=null;Save(job);monitoring.CheckFinished(job,null);
                        }else SaveDispatch(job,receipt);
                        return job;
                    }
                }catch(Exception error){
                    var backend=error as CloudBackendException;receipt.State="read-failed";receipt.ErrorCode=backend!=null&&Regex.IsMatch(backend.Code??"","^[a-z][a-z0-9-]{0,79}$")?backend.Code:error is OperationCanceledException?"cancelled":"unavailable";SaveDispatch(job,receipt);
                    if(!String.IsNullOrEmpty(job.TurnId)&&job.State=="running"){job.State="attention";job.Error="Le tour a été retrouvé mais sa vérification reste incomplète. Aucun nouvel envoi autorisé.";Save(job);}
                    monitoring.CheckFinished(job,"Le rapprochement en lecture seule n'a pas abouti ("+receipt.ErrorCode+"). Aucun renvoi automatique.");if(error is OperationCanceledException)throw;return job;
                }
            }
        }
        private static bool HasNotificationMarker(object snapshot,string marker,out string turnId)
        {
            turnId=null;if(String.IsNullOrWhiteSpace(marker))return false;
            foreach(var turn in RelayEngine.Rows(Json.Get(snapshot,"turns")))foreach(var item in RelayEngine.Rows(Json.Get(turn,"items"))){
                if(Json.Str(Json.Get(item,"type"))=="userMessage"){
                    string text=String.Join("",RelayEngine.Rows(Json.Get(item,"content")).Where(part=>Json.Str(Json.Get(part,"type"))=="text"||Json.Str(Json.Get(part,"type"))=="input_text").Select(part=>Json.Str(Json.Get(part,"text"))));
                    if(text==marker||text.StartsWith(marker+"\n",StringComparison.Ordinal)||text.StartsWith(marker+"\r\n",StringComparison.Ordinal)){turnId=Json.Str(Json.Get(turn,"id"));return true;}
                }else if(Json.Str(Json.Get(item,"type"))=="functionCallOutput"&&Json.Str(Json.Get(item,"namespace"))=="codex_app"&&Json.Str(Json.Get(item,"name"))=="send_message_to_thread"){
                    // This is the existing public Codex-app envelope, not an
                    // arbitrary tool/assistant quotation of our marker.
                    string output=Json.Str(Json.Get(Json.Get(item,"output"),"text"));int start=output.IndexOf("<input>",StringComparison.Ordinal);
                    if(start>=0&&(output.Substring(start+7).StartsWith(marker+"\n",StringComparison.Ordinal)||output.Substring(start+7).StartsWith(marker+"\r\n",StringComparison.Ordinal))){turnId=Json.Str(Json.Get(turn,"id"));return true;}
                }
            }
            return false;
        }
        internal async Task Deliver(string id,IRelayTransport transport,CancellationToken token,bool scheduled=false)
        {
            using(store.Lease("cloud-run-"+id)){
                var job=Get(id);
                if(!job.ReturnToSource)return;
                if(job.ReturnState=="sending"){job.ReturnState="uncertain";Save(job);monitoring.Notification(job,"uncertain","Remise au chat source interrompue. Aucun renvoi automatique.");}
                if(monitoring.NotificationState(job)=="sending")monitoring.Notification(job,"uncertain","Remise au chat source interrompue. Aucun renvoi automatique.");
                if(!monitoring.BeginNotification(job,scheduled))return;
                bool result=monitoring.Kind(job)=="result",sending=false;
                try{
                    // Even an offline attempt advances scheduling fairness. A
                    // business result and its Error remain intact on I/O errors.
                    Save(job);
                    var source=store.Channel(job.SourceChannel);
                    if(source.AccountKey!=job.SourceAccount||!RelayStore.SamePath(source.Home,job.SourceHome))throw new CloudBackendException("source-binding","Le compte source du retour Cloud a changé.");
                    RequireSource(new RelaySession{Channel=job.SourceChannel,Thread=job.SourceThread,Home=job.SourceHome,Account=job.SourceAccount});
                    transport.VerifySend(source,job.SourceThread);
                    bool uncertain=monitoring.NotificationState(job)=="uncertain";
                    var snapshot=await transport.Call(source,"read_thread",new{threadId=job.SourceThread,turnLimit=uncertain?100:1,includeOutputs=uncertain,maxOutputCharsPerItem=20000},token);
                    try{DirectMessages.RequireThread(snapshot,job.SourceThread);}catch{throw new CloudBackendException("source-binding","Le chat source n’a pas été reconnu. Aucun envoi autorisé.");}
                    if(uncertain){
                        string returnedTurn;
                        if(HasNotificationMarker(snapshot,monitoring.Marker(job),out returnedTurn)){
                            if(result){job.ReturnState="delivered";if(AgentProviders.Kind(source)=="codex-headless"&&!String.IsNullOrEmpty(returnedTurn)){job.SourceReturnTurn=returnedTurn;job.SourceReturnState="running";}Save(job);}
                            monitoring.Notification(job,"delivered");
                        }
                        return; // Absence from a partial history never authorizes replay.
                    }
                    string status=Json.Str(Json.Get(Json.Get(Json.Get(snapshot,"thread"),"status"),"type"));
                    bool desktopQueued=AgentProviders.Kind(source)=="codex";
                    if(RelayEngine.AttentionFlag(snapshot)!=null||(!desktopQueued&&(status!="idle"||DirectMessages.Busy(snapshot)))||(desktopQueued&&!new[]{"idle","active","running","inProgress"}.Contains(status))){
                        monitoring.Notification(job,"source-busy","Le chat source attend une intervention ou ne peut pas recevoir de message en file.");return;
                    }
                    RequireSource(new RelaySession{Channel=job.SourceChannel,Thread=job.SourceThread,Home=job.SourceHome,Account=job.SourceAccount});transport.VerifySend(store.Channel(job.SourceChannel),job.SourceThread);
                    if(result){job.ReturnState="sending";Save(job);}monitoring.Notification(job,"sending");sending=true;
                    string body=result?(String.IsNullOrWhiteSpace(job.Result)?job.Error??"Résultat absent.":job.Result):job.Error??"Une intervention est nécessaire dans Codex Cloud.";
                    if(body.Length>16000)body=body.Substring(0,16000)+"\n[Suite via get_cloud_job]";
                    string prompt=monitoring.Marker(job)+"\n"+(result?"Résultat de ":"Intervention nécessaire pour ")+job.InstanceName+" · "+job.Title+" · état "+job.State+"\nBranche : "+job.Branch+"\nLot : "+(job.Lot??"non précisé")+" · dépôt : "+(job.Repository??"non précisé")+"\nMission : "+job.Id+" · chat Cloud : "+(job.ThreadId??"pas encore créé")+"\nCe message est transmis par le suivi automatique du Switcher. Il ne constitue pas une autorisation de fusionner ni d’approuver une demande native. Vérifie les preuves avant intégration.\n\n"+body;
                    var delivered=await transport.Call(source,"send_message_to_thread",new{threadId=job.SourceThread,prompt=prompt},token);
                    if(delivered==null)throw new InvalidOperationException("Le service n’a pas confirmé la remise de la notification.");
                    string deliveredThread=Json.Str(Json.Get(Json.Read<object>(Json.Write(delivered)),"threadId"));
                    if(!String.IsNullOrEmpty(deliveredThread)&&deliveredThread!=job.SourceThread)throw new InvalidOperationException("Le service a confirmé un autre chat pour cette notification.");
                    if(result&&AgentProviders.Kind(source)=="codex-headless"){
                        job.SourceReturnTurn=Json.Str(Json.Get(Json.Read<object>(Json.Write(delivered)),"turnId"));
                        if(String.IsNullOrEmpty(job.SourceReturnTurn))throw new InvalidOperationException("Le traitement local du retour n'a pas confirmé son tour. Aucun renvoi automatique.");
                        job.SourceReturnState="running";
                    }
                    if(result){job.ReturnState="delivered";Save(job);}monitoring.Notification(job,"delivered");
                }catch(Exception error){
                    if(sending){if(result){job.ReturnState="uncertain";Save(job);}monitoring.Notification(job,"uncertain","Remise au chat source incertaine. Aucun renvoi automatique.");}
                    else {
                        bool binding=error is CloudBackendException&&((CloudBackendException)error).Code=="source-binding";
                        bool permissions=error is RelayPermissionException;
                        monitoring.Notification(job,binding?"attention":permissions?"permissions":"source-offline",binding?"Le compte source ne correspond plus à celui de la mission. Aucun envoi autorisé.":permissions?"Le chat orchestrateur doit confirmer les permissions requises. Aucun changement automatique de permissions ; nouvelle vérification dans 60 secondes.":"Le chat orchestrateur est indisponible. La notification est conservée et sera réessayée.");
                    }
                    if(error is OperationCanceledException)throw;
                    if(!scheduled)throw;
                }
            }
        }
        internal bool HeadlessSource(CloudJob job)
        {return job.ReturnToSource&&store.Channels().Any(channel=>channel.Id==job.SourceChannel&&AgentProviders.Kind(channel)=="codex-headless");}
        internal CloudJob[] LocalReturns()
        {return List().Where(job=>HeadlessSource(job)&&(monitoring.PendingNotification(job)||job.SourceReturnState=="running")).OrderBy(job=>job.UpdatedUtc).ToArray();}
        internal async Task PumpLocalReturns(IRelayTransport transport,CancellationToken token,bool scheduled=true)
        {
            // The relay worker owns local inference engines. A Cloud worker only
            // persists their results; it must never launch a competing engine.
            foreach(var job in LocalReturns().Where(job=>!scheduled||(job.SourceReturnState=="running"?monitoring.SourceCheckDue(job):monitoring.NotificationDue(job))).Take(4)){
                try{
                    if(job.SourceReturnState!="running")await Deliver(job.Id,transport,token,scheduled).ConfigureAwait(false);
                    else using(store.Lease("cloud-run-"+job.Id)){
                        var current=Get(job.Id);monitoring.BeginSourceCheck(current);RequireSource(new RelaySession{Channel=current.SourceChannel,Thread=current.SourceThread,Home=current.SourceHome,Account=current.SourceAccount});
                        var snapshot=await transport.Call(store.Channel(current.SourceChannel),"read_thread",new{threadId=current.SourceThread},token).ConfigureAwait(false);
                        DirectMessages.RequireThread(snapshot,current.SourceThread);
                        var turn=RelayEngine.Rows(Json.Get(snapshot,"turns")).SingleOrDefault(value=>Json.Str(Json.Get(value,"id"))==current.SourceReturnTurn);
                        string status=Json.Str(Json.Get(turn,"status"));
                        if(new[]{"completed","failed","interrupted"}.Contains(status)){current.SourceReturnState=status;Save(current);}
                        monitoring.SourceCheckError(current,status=="failed"||status=="interrupted"?"Le traitement local de la notification s’est terminé avec l’état "+status+". Le résultat Cloud est conservé.":null);
                    }
                }catch(OperationCanceledException){throw;}
                catch(Exception error){var io=error as IOException;if(io!=null&&((io.HResult&0xffff)==32||(io.HResult&0xffff)==33))continue;using(store.Lease("cloud-run-"+job.Id)){var current=Get(job.Id);bool binding=error is CloudBackendException&&((CloudBackendException)error).Code=="source-binding";if(binding&&current.SourceReturnState=="running"){current.SourceReturnState="attention";Save(current);}monitoring.SourceCheckError(current,binding?"Le compte source a changé. Aucune reprise automatique.":"La connexion à l’orchestrateur est indisponible ; la vérification sera réessayée.");}}
            }
        }
        internal CloudJob CancelQueued(string id)
        {
            using(store.Lease("cloud-run-"+id)){var job=Get(id);if(job.State!="queued"&&!(job.Kind=="follow-up"&&job.State=="attention"&&String.IsNullOrEmpty(job.TurnId))&&!(job.State=="attention"&&String.IsNullOrEmpty(job.ThreadId)&&String.IsNullOrEmpty(job.TurnId)&&String.IsNullOrEmpty(job.ExecutionSetupThreadId)&&String.IsNullOrEmpty(job.ExecutionSetupState)))throw new InvalidOperationException("Seule une mission non envoyée peut être annulée ici. Arrêtez une exécution active dans Codex.");job.State="cancelled";job.ReturnState="none";Save(job);return job;}
        }
    }
    // Reconnecting a desktop source only reads its existing native endpoint.
    // Keep that maintenance off the mission loop and bound its lifetime, so a
    // stale/offline source cannot delay a Cloud completion or another account.
    internal sealed class CloudSourceReconnectPump : IDisposable
    {
        private readonly Func<CancellationToken,Task> refresh;
        private readonly Func<DateTime> utcNow;
        private readonly TimeSpan timeout;
        private Task pending=Task.FromResult(0);
        private CancellationTokenSource cancellation;
        private DateTime next=DateTime.MinValue;
        private bool disposed;
        private volatile string error;
        internal CloudSourceReconnectPump(Func<CancellationToken,Task> refresh,Func<DateTime> utcNow=null,TimeSpan? timeout=null)
        {if(refresh==null)throw new ArgumentNullException("refresh");this.refresh=refresh;this.utcNow=utcNow??(()=>DateTime.UtcNow);this.timeout=timeout??TimeSpan.FromSeconds(10);if(this.timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException("timeout");}
        internal Task Pending {get{return pending;}}
        internal string Error {get{return error;}}
        internal void Tick()
        {
            DateTime now=utcNow().ToUniversalTime();
            if(disposed||!pending.IsCompleted||now<next)return;
            next=now.AddSeconds(60);var deadline=new CancellationTokenSource(timeout);cancellation=deadline;
            pending=Task.Run(async()=>{
                try{await refresh(deadline.Token).ConfigureAwait(false);error=null;}
                catch(OperationCanceledException){error="La reconnexion du chat source n’a pas abouti dans le délai prévu. Les missions et leurs résultats restent conservés.";}
                catch(Exception){error="La reconnexion du chat source a échoué. Un nouvel essai sera fait sans interrompre les missions.";}
                finally{deadline.Dispose();}
            });
        }
        public void Dispose()
        {disposed=true;var active=cancellation;if(active!=null)try{active.Cancel();}catch(ObjectDisposedException){}}
    }
    internal static class CloudJobWorker
    {
        internal const int ExecutionVersion=1;
        internal const int SchemaVersion=3;
        internal static void RequireFollowUpWorker(RelayStore store)
        {
            var active=store.ReadRecord<RelayWorkerState>("cloud-worker-v1.dpapi");
            if(DesktopRuntime.SameProcess(active.Pid,active.Started)&&(!active.CloudJobSchemaVersion.HasValue||active.CloudJobSchemaVersion.Value<SchemaVersion))throw new CloudBackendException("cloud-worker-upgrade","Le suivi Cloud actif ne sait pas encore reprendre une mission dans son chat existant. Mettez à jour le Switcher avant de demander une correction ; aucun nouveau chat ni prompt créé.");
        }
        internal static void RequireExecutionWorker(RelayStore store)
        {
            var active=store.ReadRecord<RelayWorkerState>("cloud-worker-v1.dpapi");
            if(DesktopRuntime.SameProcess(active.Pid,active.Started)&&(!active.ExecutionVersion.HasValue||active.ExecutionVersion.Value<ExecutionVersion))throw new CloudBackendException("cloud-worker-upgrade","Le moteur Cloud actif est une ancienne version qui ignorerait ces réglages. Mettez à jour uniquement le suivi Cloud du Switcher avant de déléguer ; aucune instance Codex n'a besoin d'être fermée.");
        }
        internal static CloudJob[] SelectPending(IEnumerable<CloudJob> jobs,IEnumerable<CloudJob> capacitySnapshot=null,int maximum=CloudCapacity.DefaultMaximum,Func<CloudJob,bool> admissionReady=null,Func<CloudJob,int> admissionPriority=null)
        {
            var snapshot=jobs.ToArray();var pending=snapshot.Where(j=>RuntimePending(j)||CloudJobMonitoring.PotentialNotification(j)).ToArray();
            var capacity=(capacitySnapshot??snapshot).ToArray();
            var active=new Queue<CloudJob>(pending.Where(j=>RuntimePending(j)&&!CloudCapacity.NeedsAdmission(j)).OrderBy(Stamp,StringComparer.Ordinal).ThenBy(j=>j.Id,StringComparer.Ordinal));
            // Keep blocked requests visible for liveness, but do not let an old
            // backlog on one occupied account hide an independent runnable one.
            var queued=new Queue<CloudJob>(pending.Where(j=>RuntimePending(j)&&CloudCapacity.NeedsAdmission(j)).OrderBy(j=>(admissionReady==null?CloudCapacity.CanStart(j,capacity,maximum):admissionReady(j))?0:1).ThenByDescending(j=>admissionPriority==null?0:admissionPriority(j)).ThenBy(Stamp,StringComparer.Ordinal).ThenBy(j=>j.Id,StringComparer.Ordinal));
            // One return per source chat per batch. Rank sources by their last
            // attempt, not by their oldest remaining result: a backlog at one
            // busy source must not starve another source's single result.
            var returns=new Queue<CloudJob>(snapshot.Where(j=>!RuntimePending(j)&&(CloudJobMonitoring.PotentialNotification(j)||new[]{"delivered","attention"}.Contains(j.ReturnState)))
                .GroupBy(j=>new{j.SourceThread,j.SourceAccount,Home=(j.SourceHome??"").TrimEnd('\\','/').ToUpperInvariant()})
                .Select(group=>new{LastAttempt=group.Select(Stamp).OrderByDescending(value=>value,StringComparer.Ordinal).First(),Job=group.Where(CloudJobMonitoring.PotentialNotification).OrderBy(Stamp,StringComparer.Ordinal).ThenBy(j=>j.Id,StringComparer.Ordinal).FirstOrDefault()}).Where(group=>group.Job!=null)
                .OrderBy(group=>group.LastAttempt,StringComparer.Ordinal).ThenBy(group=>group.Job.Id,StringComparer.Ordinal).Select(group=>group.Job));
            var selected=new List<CloudJob>();Action<Queue<CloudJob>,int> take=(queue,count)=>{while(count-->0&&queue.Count>0&&selected.Count<4)selected.Add(queue.Dequeue());};
            take(active,2);take(queued,1);take(returns,1);
            while(selected.Count<4&&(active.Count>0||queued.Count>0||returns.Count>0)){take(active,1);take(queued,1);take(returns,1);}
            return selected.ToArray();
        }
        internal static bool RuntimePending(CloudJob job)
        {return !new[]{"completed","failed","uncertain","cancelled"}.Contains(job.State)&&!(job.State=="attention"&&String.IsNullOrEmpty(job.ThreadId));}
        private static string Stamp(CloudJob job){return job.UpdatedUtc??job.CreatedUtc??"";}
        internal static CloudJob[] SelectRemotePending(IEnumerable<CloudJob> jobs,Func<CloudJob,bool> headlessSource)
        {return SelectPending(jobs.Where(job=>!headlessSource(job)||RuntimePending(job)));}
        internal static CloudJob[] SelectScheduledPending(CloudJobs jobs)
        {var all=jobs.List();var priorities=jobs.AdmissionPriorities();return SelectPending(all.Where(jobs.ScheduledDue).Where(job=>!jobs.HeadlessSource(job)||RuntimePending(job)),all,jobs.MaximumParallel,job=>jobs.AdmissionReady(job,all),job=>{int priority;return priorities.TryGetValue(job.Id,out priority)?priority:0;});}
        internal static bool HasRemotePending(CloudJobs jobs){return jobs.List().Any(job=>jobs.NeedsSupervision(job)&&(!jobs.HeadlessSource(job)||RuntimePending(job)));}
        internal static bool CanStart(CloudJob current,IEnumerable<CloudJob> jobs)
        {return CloudCapacity.CanStart(current,jobs,CloudCapacity.DefaultMaximum);}
        internal static bool CanStart(RelayStore store,CloudJob current,IEnumerable<CloudJob> jobs)
        {var snapshot=jobs.ToArray();return CloudCapacity.CanStart(current,snapshot,CloudCapacity.Read(store).MaxParallel)&&new CloudMissionQueue(store).AllowsJobStart(current,snapshot);}
        internal static TimeSpan CheckBudget(CloudJob job)
        {
            if(job==null)throw new ArgumentNullException("job");
            return TimeSpan.FromSeconds(String.IsNullOrEmpty(job.TurnId)&&new[]{"queued","prepared","attention"}.Contains(job.State)?120:45);
        }
        internal static void Ensure(RelayStore store)
        {
            EnsureCore(()=>HeadlessSourceBroker.IsOwnedChild(store),()=>{var active=store.ReadRecord<RelayWorkerState>("cloud-worker-v1.dpapi");return DesktopRuntime.SameProcess(active.Pid,active.Started);},()=>Start(store));
        }
        internal static void EnsureCore(Func<bool> ownedNativeChild,Func<bool> running,Action spawn)
        {
            // A model's MCP process belongs to the inference server's kill-on-close
            // job. The durable queue is already saved; its owner starts the worker.
            if(ownedNativeChild()||running())return;
            spawn();
        }
        private static void Start(RelayStore store)
        {
            string executable=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CodexAccountSwitcher.exe");
            if(!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CodexAccountSwitcher.dll"))||!File.Exists(executable))throw new InvalidOperationException("Le suivi Cloud requiert le Switcher avec son runtime .NET 10. Utilisez la livraison complète ; le moteur Framework seul n'est pas un hôte Cloud.");
            // A detached worker must not inherit its caller's stdio handles: an MCP
            // client otherwise waits for EOF until the entire Cloud job has ended.
            var start=new ProcessStartInfo(executable,"--cloud-worker "+RelayWorker.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");using(var process=Process.Start(start)){process.StandardInput.Close();}
        }
        internal static int Run(string root)
        {
            var store=new RelayStore(root);FileStream lease;try{lease=store.Lease("cloud-worker-v1");}catch(IOException){return 0;}
            using(lease)using(var self=Process.GetCurrentProcess())using(var reconnect=new CloudSourceReconnectPump(token=>RelayReconnect.Refresh(store,token))){
                var heartbeat=new RelayWorkerState{Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Version=RelayWorker.Version,ExecutionVersion=ExecutionVersion,CloudJobSchemaVersion=SchemaVersion,CloudQueueAdmissionVersion=CloudQueueWorker.AdmissionVersion};
                try{
                    var jobs=new CloudJobs(store);int empty=0;string reconnectDiagnostic=null;
                    while(empty<3){
                        reconnect.Tick();
                        string reconnectError=reconnect.Error;
                        if(reconnectError!=reconnectDiagnostic){if(reconnectError!=null||heartbeat.Error==reconnectDiagnostic)heartbeat.Error=reconnectError;reconnectDiagnostic=reconnectError;}
                        heartbeat.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("cloud-worker-v1.dpapi",heartbeat);
                        // A completed result for a local source is consumed by
                        // RelayWorker, which may already own that profile's engine.
                        // Do not retain this separate Cloud host just to wait for it.
                        if(jobs.LocalReturns().Any())RelayWorker.Ensure(store);
                        // Keep the lightweight host alive between 60-second
                        // deadlines; absence of a due check is not an empty queue.
                        var active=SelectScheduledPending(jobs);
                        empty=HasRemotePending(jobs)?0:empty+1;
                        foreach(var job in active){
                            try{var updated=jobs.Get(job.Id);using(var deadline=new CancellationTokenSource(CheckBudget(updated))){
                                if(!CanStart(store,updated,jobs.List()))continue;
                                awaitableAdvance(store,jobs,updated,deadline.Token);
                            }}catch(Exception e){heartbeat.Error=Program.SafeError(e);}
                        }
                        Thread.Sleep(5000);
                    }
                }finally{heartbeat.Pid=0;heartbeat.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("cloud-worker-v1.dpapi",heartbeat);}
            }
            return 0;
        }
        private static void awaitableAdvance(RelayStore store,CloudJobs jobs,CloudJob job,CancellationToken token)
        {
            var current=jobs.Advance(job.Id,token,true).GetAwaiter().GetResult();
            if(jobs.HeadlessSource(current)){
                if(CloudJobMonitoring.PotentialNotification(current))RelayWorker.Ensure(store);
            }else jobs.Deliver(job.Id,AgentProviders.Create(store),token,true).GetAwaiter().GetResult();
        }
    }
}
