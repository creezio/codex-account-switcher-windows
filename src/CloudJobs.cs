using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        public bool ExplicitDelegation {get;set;}
        public bool ReturnToSource {get;set;}
    }
    internal sealed class CloudJob
    {
        public int SchemaVersion {get;set;}
        public string Id {get;set;}
        public string InstanceId {get;set;}
        public string InstanceName {get;set;}
        public string AccountKey {get;set;}
        public string ConfigurationId {get;set;}
        public string ConfigurationVersion {get;set;}
        public string ConfigurationReadyVersion {get;set;}
        public string Title {get;set;}
        public string Prompt {get;set;}
        public string Branch {get;set;}
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
    internal sealed class CloudJobs
    {
        private readonly RelayStore store;
        private readonly Func<DesktopInstance,ICloudTasksConnection> connection;
        private readonly Func<DesktopInstance,string,CancellationToken,Task<CloudConfiguration>> configuration;
        internal CloudJobs(RelayStore store,Func<DesktopInstance,ICloudTasksConnection> connection=null,Func<DesktopInstance,string,CancellationToken,Task<CloudConfiguration>> configuration=null)
        {
            this.store=store;
            this.connection=connection??(instance=>CloudInstanceConnection.OpenTasks(new AccountService(Path.GetDirectoryName(store.Root)),instance));
            this.configuration=configuration??ReadConfiguration;
        }
        private async Task<CloudConfiguration> ReadConfiguration(DesktopInstance instance,string id,CancellationToken token)
        {using(var client=CloudInstanceConnection.Open(new AccountService(Path.GetDirectoryName(store.Root)),instance))return await client.Get(id,token);}
        private static string Record(string id){RelayStore.MessageId(id);return "cloud-job-"+id+".dpapi";}
        internal CloudJob Get(string id){var value=store.ReadRecord<CloudJob>(Record(id));if(value.Id!=id||value.SchemaVersion!=1)throw new InvalidOperationException("Mission Cloud introuvable.");return value;}
        internal CloudJob[] List()
        {
            return Directory.GetFiles(store.Root,"cloud-job-*.dpapi",SearchOption.TopDirectoryOnly).Select(path=>{
                SafeFiles.RejectLinks(path);return store.ReadRecord<CloudJob>(Path.GetFileName(path));
            }).Where(j=>j.SchemaVersion==1&&!String.IsNullOrEmpty(j.Id)).OrderByDescending(j=>j.CreatedUtc).ToArray();
        }
        private void Save(CloudJob job){job.UpdatedUtc=DateTime.UtcNow.ToString("o");store.WriteRecord(Record(job.Id),job);}
        internal static object Summary(CloudJob job,bool result)
        {return new{job.Id,job.Title,job.InstanceId,job.InstanceName,job.ConfigurationId,job.ConfigurationVersion,job.Branch,job.State,job.ThreadId,job.TurnId,job.ReturnState,job.SourceReturnState,job.Error,Result=result?job.Result:null,job.CreatedUtc,job.UpdatedUtc};}
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
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,spec.Instance);
            if(!String.IsNullOrEmpty(spec.ExpectedAccountKey)&&spec.ExpectedAccountKey!=instance.AccountKey)throw new CloudBackendException("target-binding","Le compte sélectionné a changé depuis la préparation de cette demande. Rechargez les instances.");
            if(source!=null)RequirePolicy(source,instance,accounts);
            string fingerprint=RelayPolicies.Fingerprint(new RelayJobSpec{Title=spec.Title,Prompt=spec.Prompt,To=instance.Id,Resource=spec.ConfigurationId,ExplicitDelegation=true,ReturnToSource=spec.ReturnToSource});
            using(store.Lease("cloud-admit-"+spec.Id)){
                if(File.Exists(Path.Combine(store.Root,Record(spec.Id)))){
                    var prior=Get(spec.Id);
                    if(prior.Fingerprint!=fingerprint||prior.AccountKey!=instance.AccountKey||prior.SourceChannel!=(source==null?null:source.Channel)||prior.SourceThread!=(source==null?null:source.Thread)||prior.SourceAccount!=(source==null?null:source.Account)||(source!=null&&!RelayStore.SamePath(prior.SourceHome,source.Home)))throw new InvalidOperationException("Cet identifiant Cloud appartient à une autre demande.");
                    return prior;
                }
                if(spec.ReturnToSource&&source==null)throw new InvalidOperationException("Le retour automatique exige un chat source connecté.");
                var config=await configuration(instance,spec.ConfigurationId,token);
                if(config==null||config.Id!=spec.ConfigurationId||config.Status!="ready"||String.IsNullOrEmpty(config.VersionId)||!config.VersionRevision.HasValue||config.VersionRevision.Value<=1||String.IsNullOrEmpty(config.LatestReadyVersionId)||config.Draft!=null)throw new InvalidOperationException("L'environnement doit être publié, prêt et sans brouillon avant une mission.");
                if(source!=null)RequireSource(source);
                var job=new CloudJob{SchemaVersion=1,Id=spec.Id,InstanceId=instance.Id,InstanceName=instance.Name,AccountKey=instance.AccountKey,ConfigurationId=config.Id,ConfigurationVersion=config.VersionId,ConfigurationReadyVersion=config.LatestReadyVersionId,Title=spec.Title.Trim(),Prompt=spec.Prompt,Branch="codex/switcher/"+spec.Id,Fingerprint=fingerprint,State="queued",ReturnToSource=spec.ReturnToSource,ReturnState=spec.ReturnToSource?"waiting":"none",SourceChannel=source==null?null:source.Channel,SourceThread=source==null?null:source.Thread,SourceHome=source==null?null:source.Home,SourceAccount=source==null?null:source.Account,CreatedUtc=DateTime.UtcNow.ToString("o")};
                Save(job);return job;
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
        }
        private DesktopInstance Pinned(CloudJob job)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,job.InstanceId);
            if(instance.AccountKey!=job.AccountKey)throw new InvalidOperationException("Le compte permanent de la mission Cloud a changé.");return instance;
        }
        internal async Task<CloudJob> Advance(string id,CancellationToken token)
        {
            using(store.Lease("cloud-run-"+id)){
                var job=Get(id);
                if(job.ReturnState=="sending"){job.ReturnState="uncertain";job.Error="Remise au chat source interrompue. Aucun renvoi automatique.";Save(job);}
                if(new[]{"completed","failed","uncertain","cancelled"}.Contains(job.State))return job;
                if(job.State=="creating"||job.State=="sending"){
                    job.State="uncertain";job.Error="Envoi interrompu : résultat à rapprocher, aucun renvoi automatique.";Save(job);return job;
                }
                if(job.State=="attention"&&String.IsNullOrEmpty(job.ThreadId))return job;
                try{
                    var instance=Pinned(job);
                    using(var client=connection(instance)){
                        await client.Connect(token);
                        if(job.State=="queued"){
                            if(RelayDispatch.Read(store).Mode!="running")return job;
                            VerifyOrigin(job,instance);await VerifyConfiguration(job,instance,token);
                            FileStream capacity;
                            try{capacity=store.Lease("cloud-capacity-v1");}
                            catch(IOException e){if((e.HResult&0xffff)==32||(e.HResult&0xffff)==33)return job;throw;}
                            using(capacity){
                                if(!CloudJobWorker.CanStart(job,List()))return job;
                                if(RelayDispatch.Read(store).Mode!="running")return job;
                                VerifyOrigin(job,instance);
                                job.State="creating";Save(job);
                            }
                            var created=await client.Start(job.ConfigurationId,null,token);
                            if(created==null||String.IsNullOrEmpty(created.Id)||created.AccountKey!=job.AccountKey)throw new InvalidOperationException("La création du chat Cloud n'a pas confirmé son compte.");
                            job.ThreadId=created.Id;job.State="prepared";Save(job);
                        }
                        var thread=await client.Read(job.ThreadId,token);
                        if(thread==null||thread.Id!=job.ThreadId||thread.AccountKey!=job.AccountKey||thread.VerifiedEnvironmentConfigId!=job.ConfigurationId)throw new InvalidOperationException("Le service n'a pas confirmé le compte et l'environnement de cette conversation. Aucun prompt envoyé.");
                        if(job.State=="prepared"||(job.State=="attention"&&String.IsNullOrEmpty(job.TurnId))){
                            if(RelayDispatch.Read(store).Mode!="running")return job;
                            if(thread.AwaitingApproval||thread.AwaitingInput||thread.Status!="idle"||(thread.LatestTurn!=null&&new[]{"inProgress","running"}.Contains(thread.LatestTurn.Status))){job.State="prepared";job.Error="La conversation Cloud travaille ou attend une intervention native. Aucun prompt envoyé.";Save(job);return job;}
                            VerifyOrigin(job,instance);await VerifyConfiguration(job,instance,token);VerifyOrigin(job,instance);
                            if(RelayDispatch.Read(store).Mode!="running")return job;
                            string prompt="[CREEZIO_CLOUD_REQUEST:"+job.Id+"]\nMission explicitement déléguée via Account Switcher. Applique les permissions de ce compte.\nTitre : "+job.Title+"\n"+job.Prompt+"\n\nPour tout changement de code, utilise exclusivement la branche "+job.Branch+". Ne modifie ni ne fusionne main. Si un accès GitHub manque, signale le blocage. Retourne les tests, le SHA du commit, la branche et l'URL de PR si créée. N'inclus aucun secret. Une réussite déclarée sera vérifiée par l'orchestrateur.";
                            job.State="sending";Save(job);
                            var turn=await client.StartTurn(job.ThreadId,prompt,job.Id,token);
                            if(turn==null||String.IsNullOrEmpty(turn.Id))throw new InvalidOperationException("Le service n'a pas confirmé le tour Cloud.");
                            job.TurnId=turn.Id;job.State="running";Save(job);
                            thread=await client.Read(job.ThreadId,token);
                            if(thread==null||thread.Id!=job.ThreadId||thread.AccountKey!=job.AccountKey||thread.VerifiedEnvironmentConfigId!=job.ConfigurationId)throw new InvalidOperationException("L'identité de la conversation Cloud a changé après l'envoi.");
                        }
                        if(thread.AwaitingApproval||thread.AwaitingInput){job.State="attention";job.Error=thread.AwaitingApproval?"Approbation native attendue dans Codex Cloud. Aucune approbation automatique.":"Réponse utilisateur attendue dans Codex Cloud.";Save(job);return job;}
                        var latest=thread.LatestTurn;
                        if(latest!=null&&latest.Id==job.TurnId){
                            if(latest.Status=="completed"){
                                if(!latest.TextComplete||String.IsNullOrWhiteSpace(latest.Text)){job.State="attention";job.Error="Le tour Cloud est terminé, mais sa réponse finale complète n'est pas disponible. Aucun résultat remis.";}
                                else {job.State="completed";job.Result=latest.Text;job.Error=null;job.ReturnState=job.ReturnToSource?"pending":"none";}
                            }
                            else if(latest.Status=="failed"||latest.Status=="interrupted"){job.State="failed";job.Result=latest.Text;job.Error="Exécution Cloud : "+latest.Status;job.ReturnState=job.ReturnToSource?"pending":"none";}
                            else {job.State="running";job.Error=null;}
                            Save(job);
                        }
                    }
                }catch(Exception e){
                    bool dispatched=job.State=="creating"||job.State=="sending";
                    if(dispatched){job.State="uncertain";job.Error="Résultat d'envoi Cloud incertain. Aucun renvoi automatique.";}
                    else {job.Error=e is CloudBackendException?e.Message:"La mission Cloud n'a pas pu être vérifiée. Vérifiez le compte, l'environnement et la connexion dans Codex.";if(job.State=="queued"||job.State=="prepared")job.State="attention";}
                    Save(job);if(e is OperationCanceledException)throw;
                }
                return job;
            }
        }
        internal async Task Deliver(string id,IRelayTransport transport,CancellationToken token)
        {
            using(store.Lease("cloud-run-"+id)){
                var job=Get(id);
                if(job.ReturnState=="sending"){job.ReturnState="uncertain";job.Error="Remise au chat source interrompue. Aucun renvoi automatique.";Save(job);return;}
                if(!job.ReturnToSource||job.ReturnState!="pending")return;
                var source=store.Channel(job.SourceChannel);
                if(source.AccountKey!=job.SourceAccount||!RelayStore.SamePath(source.Home,job.SourceHome))throw new InvalidOperationException("Le compte source du retour Cloud a changé.");
                transport.VerifySend(source,job.SourceThread);
                var snapshot=await transport.Call(source,"read_thread",new{threadId=job.SourceThread,turnLimit=1,includeOutputs=false},token);
                DirectMessages.RequireThread(snapshot,job.SourceThread);
                string status=Json.Str(Json.Get(Json.Get(Json.Get(snapshot,"thread"),"status"),"type"));
                if(status!="idle"||DirectMessages.Busy(snapshot))return;
                RequireSource(new RelaySession{Channel=job.SourceChannel,Thread=job.SourceThread,Home=job.SourceHome,Account=job.SourceAccount});transport.VerifySend(store.Channel(job.SourceChannel),job.SourceThread);
                job.ReturnState="sending";Save(job);
                try{
                    string body=String.IsNullOrWhiteSpace(job.Result)?job.Error??"Résultat absent.":job.Result;if(body.Length>16000)body=body.Substring(0,16000)+"\n[Suite via get_cloud_job]";
                    var delivered=await transport.Call(source,"send_message_to_thread",new{threadId=job.SourceThread,prompt="[CREEZIO_CLOUD_RESULT:"+job.Id+"]\nRésultat de "+job.InstanceName+" · état "+job.State+" · branche "+job.Branch+"\nCe résultat ne constitue pas une autorisation de fusionner. Vérifie les preuves avant intégration.\n\n"+body},token);
                    if(AgentProviders.Kind(source)=="codex-headless"){
                        job.SourceReturnTurn=Json.Str(Json.Get(Json.Read<object>(Json.Write(delivered)),"turnId"));
                        if(String.IsNullOrEmpty(job.SourceReturnTurn))throw new InvalidOperationException("Le traitement local du retour n'a pas confirmé son tour. Aucun renvoi automatique.");
                        job.SourceReturnState="running";
                    }
                    job.ReturnState="delivered";if(job.State=="completed")job.Error=null;Save(job);
                }catch{job.ReturnState="uncertain";job.Error="Remise au chat source incertaine. Aucun renvoi automatique.";Save(job);throw;}
            }
        }
        internal bool HeadlessSource(CloudJob job)
        {return job.ReturnToSource&&store.Channels().Any(channel=>channel.Id==job.SourceChannel&&AgentProviders.Kind(channel)=="codex-headless");}
        internal CloudJob[] LocalReturns()
        {return List().Where(job=>HeadlessSource(job)&&(job.ReturnState=="pending"||job.ReturnState=="sending"||job.SourceReturnState=="running")).OrderBy(job=>job.UpdatedUtc).ToArray();}
        internal async Task PumpLocalReturns(IRelayTransport transport,CancellationToken token)
        {
            // The relay worker owns local inference engines. A Cloud worker only
            // persists their results; it must never launch a competing engine.
            foreach(var job in LocalReturns().Take(4)){
                try{
                    if(job.SourceReturnState!="running")await Deliver(job.Id,transport,token).ConfigureAwait(false);
                    else using(store.Lease("cloud-run-"+job.Id)){
                        var current=Get(job.Id);RequireSource(new RelaySession{Channel=current.SourceChannel,Thread=current.SourceThread,Home=current.SourceHome,Account=current.SourceAccount});
                        var snapshot=await transport.Call(store.Channel(current.SourceChannel),"read_thread",new{threadId=current.SourceThread},token).ConfigureAwait(false);
                        DirectMessages.RequireThread(snapshot,current.SourceThread);
                        var turn=RelayEngine.Rows(Json.Get(snapshot,"turns")).SingleOrDefault(value=>Json.Str(Json.Get(value,"id"))==current.SourceReturnTurn);
                        string status=Json.Str(Json.Get(turn,"status"));
                        if(new[]{"completed","failed","interrupted"}.Contains(status)){current.SourceReturnState=status;if(status!="completed")current.Error="Le traitement local du résultat Cloud s’est terminé avec l’état "+status+". Le résultat Cloud reste disponible.";Save(current);}
                    }
                }catch(OperationCanceledException){throw;}
                catch(Exception error){var io=error as IOException;if(io!=null&&((io.HResult&0xffff)==32||(io.HResult&0xffff)==33))continue;using(store.Lease("cloud-run-"+job.Id)){var current=Get(job.Id);if(current.SourceReturnState=="running")current.SourceReturnState="attention";else if(current.ReturnState=="pending")current.ReturnState="attention";current.Error="Retour local à vérifier : "+Program.SafeError(error);Save(current);}}
            }
        }
        internal CloudJob CancelQueued(string id)
        {
            using(store.Lease("cloud-run-"+id)){var job=Get(id);if(job.State!="queued")throw new InvalidOperationException("Seule une mission en attente peut être annulée ici. Arrêtez une exécution active dans Codex.");job.State="cancelled";job.ReturnState="none";Save(job);return job;}
        }
    }
    internal static class CloudJobWorker
    {
        internal static CloudJob[] SelectPending(IEnumerable<CloudJob> jobs)
        {
            return jobs.Where(j=>(!new[]{"completed","failed","uncertain","cancelled"}.Contains(j.State)&&!(j.State=="attention"&&String.IsNullOrEmpty(j.ThreadId)))||j.ReturnState=="pending"||j.ReturnState=="sending")
                .OrderBy(j=>j.State=="queued"?1:0).ThenBy(j=>j.UpdatedUtc).Take(4).ToArray();
        }
        internal static CloudJob[] SelectRemotePending(IEnumerable<CloudJob> jobs,Func<CloudJob,bool> headlessSource)
        {return SelectPending(jobs.Where(job=>!headlessSource(job)||!new[]{"completed","failed","cancelled"}.Contains(job.State)));}
        internal static bool HasRemotePending(CloudJobs jobs){return SelectRemotePending(jobs.List(),jobs.HeadlessSource).Length>0;}
        internal static bool CanStart(CloudJob current,IEnumerable<CloudJob> jobs)
        {
            if(current.State!="queued")return true;
            var inFlight=jobs.Where(j=>j.Id!=current.Id&&(new[]{"creating","prepared","sending","running"}.Contains(j.State)||((j.State=="attention"||j.State=="uncertain")&&!String.IsNullOrEmpty(j.ThreadId)))).ToArray();
            return inFlight.Length<2&&!inFlight.Any(j=>j.AccountKey==current.AccountKey);
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
            using(lease)using(var self=Process.GetCurrentProcess()){
                var heartbeat=new RelayWorkerState{Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Version=RelayWorker.Version};
                try{
                    var jobs=new CloudJobs(store);int empty=0;
                    while(empty<3){
                        heartbeat.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("cloud-worker-v1.dpapi",heartbeat);
                        // A completed result for a local source is consumed by
                        // RelayWorker, which may already own that profile's engine.
                        // Do not retain this separate Cloud host just to wait for it.
                        if(jobs.LocalReturns().Any())RelayWorker.Ensure(store);
                        var active=SelectRemotePending(jobs.List(),jobs.HeadlessSource);
                        empty=active.Length==0?empty+1:0;
                        foreach(var job in active){
                            try{using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45))){
                                var updated=jobs.Get(job.Id);
                                if(!CanStart(updated,jobs.List()))continue;
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
            var current=jobs.Advance(job.Id,token).GetAwaiter().GetResult();
            if(jobs.HeadlessSource(current)){
                if(current.ReturnState=="pending"||current.ReturnState=="sending")RelayWorker.Ensure(store);
            }else jobs.Deliver(job.Id,AgentProviders.Create(store),token).GetAwaiter().GetResult();
        }
    }
}
