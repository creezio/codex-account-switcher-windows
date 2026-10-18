using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class WebhookWorker
    {
        internal static WebhookWorkerState Status(RelayStore store)
        {
            var state=store.ReadRecord<WebhookWorkerState>("webhook-worker-v1.dpapi");
            if(DesktopRuntime.SameProcess(state.Pid,state.Started))return state;
            var launch=store.ReadRecord<WebhookWorkerState>("webhook-launch-v1.dpapi");
            return DesktopRuntime.SameProcess(launch.Pid,launch.Started)?launch:state;
        }
        internal static void Ensure(RelayStore store)
        {
            using(store.Lease("webhook-worker-control")){
                if(!Webhooks.Config(store).Enabled)return;var state=Status(store);
                if(DesktopRuntime.SameProcess(state.Pid,state.Started))return;
                string executable=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");
                if(!File.Exists(executable))throw new InvalidOperationException("CreezioRelay.exe doit accompagner le Switcher pour recevoir les webhooks.");
                var start=new ProcessStartInfo(executable,"--webhook-worker "+RelayWorker.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");
                using(var process=Process.Start(start)){process.StandardInput.Close();store.WriteRecord("webhook-launch-v1.dpapi",new WebhookWorkerState{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Updated=DateTime.UtcNow.ToString("o"),Port=Webhooks.Config(store).Port});}
            }
        }
        internal static void Recover(RelayStore store)
        {
            // Never guess whether an interrupted native send reached Codex.
            // Durable queued and monitoring records are safe to reconcile;
            // sending is quarantined until a human or native receipt resolves it.
            foreach(var value in Webhooks.Jobs(store).Where(j=>j.State=="sending"))using(var lease=Webhooks.TryJobLease(store,value.Id)){
                if(lease==null)continue;var job=Webhooks.Job(store,value.Id);if(job.State!="sending")continue;
                job.State="uncertain";job.Error="L'envoi a ete interrompu. Verification necessaire avant toute nouvelle execution.";Webhooks.SaveJob(store,job);
            }
        }
        internal static async Task ProcessOne(RelayStore store,string id,Func<RelayStore,WebhookJob,CancellationToken,Task> process,CancellationToken token)
        {
            using(var lease=Webhooks.TryJobLease(store,id)){
                if(lease==null)return;var job=Webhooks.Job(store,id);
                if(!CanProcess(store,job)||!Webhooks.Config(store).Enabled)return;
                try{await process(store,job,token).ConfigureAwait(false);}
                catch(OperationCanceledException){throw;}
                catch(Exception){
                    // Reload the durable journal: the dispatcher may have saved
                    // a successful enqueue before its monitoring read failed.
                    job=Webhooks.Job(store,id);
                    if(job.State=="completed"||job.State=="failed"||job.State=="blocked"||job.State=="cancelled"||job.State=="uncertain")return;
                    job.State=String.IsNullOrEmpty(job.RelayJobId)?"uncertain":"monitoring";
                    job.Error="Le suivi de cette mission a rencontre une erreur. Aucun envoi ne sera repete sans reconciliation.";Webhooks.SaveJob(store,job);
                }
            }
        }
        private static bool CanProcess(RelayStore store,WebhookJob job)
        {return new[]{"queued","waiting","monitoring","attention"}.Contains(job.State)||(job.State=="uncertain"&&(!String.IsNullOrEmpty(job.RelayJobId)||store.Exists(job.Id)));}
        internal static async Task ProcessBounded(RelayStore store,string id,Func<RelayStore,WebhookJob,CancellationToken,Task> process,int timeoutMilliseconds=20000)
        {
            using(var timeout=new CancellationTokenSource()){
                timeout.CancelAfter(timeoutMilliseconds);
                try{await ProcessOne(store,id,process,timeout.Token).ConfigureAwait(false);}
                catch(OperationCanceledException){
                    if(!timeout.IsCancellationRequested)throw;
                    using(var lease=Webhooks.TryJobLease(store,id)){
                        if(lease==null)return;var job=Webhooks.Job(store,id);
                        if(new[]{"completed","failed","blocked","cancelled","uncertain"}.Contains(job.State))return;
                        if(store.Exists(id)){job.RelayJobId=id;job.State="monitoring";job.Error="La preparation a expire ; le recu local existant reste suivi, sans nouvel envoi.";}
                        else{job.State="waiting";job.Error="L'instance n'a pas repondu dans le delai de preparation. La demande reste en file.";}
                        Webhooks.SaveJob(store,job);
                    }
                }
            }
        }
        internal static int Run(string root)
        {
            var store=new RelayStore(root);FileStream ownership;try{ownership=store.Lease("webhook-worker-v1");}catch(IOException){return 0;}
            using(ownership)using(var self=Process.GetCurrentProcess()){
                var state=new WebhookWorkerState{Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Port=Webhooks.Config(store).Port};var gate=new object();bool closing=false;
                Action heartbeat=()=>{lock(gate){if(closing)return;state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("webhook-worker-v1.dpapi",state);}};
                try{
                    var config=Webhooks.Config(store);if(!config.Enabled)return 0;
                    using(var server=new WebhookHttp(store,config.Port)){
                        try{server.Start();lock(gate)state.Listening=true;}catch(System.Net.Sockets.SocketException){lock(gate)state.Error="Le port local des webhooks est indisponible. Choisissez un autre port dans le Switcher.";heartbeat();return 1;}
                        Recover(store);heartbeat();DateTime nextTunnel=DateTime.MinValue,nextAttachmentCleanup=DateTime.MinValue,nextAdministration=DateTime.MinValue;
                        using(var timer=new Timer(o=>{try{heartbeat();}catch(Exception){lock(gate)state.Error="Le signal de presence du moteur webhook ne peut plus etre enregistre.";}},null,2000,2000)){
                            while(true){
                                var latest=Webhooks.Config(store);if(!latest.Enabled||latest.Port!=config.Port)break;
                                if(DateTime.UtcNow>=nextAdministration){try{PortalAdministrationHttp.Tick(store);}catch(Exception){/* Administration failures are scoped to their own receipts. */}nextAdministration=DateTime.UtcNow.AddSeconds(2);}
                                if(DateTime.UtcNow>=nextAttachmentCleanup){
                                    // Only the dedicated upload store is eligible: sent files and
                                    // conversation history never enter this expiry path.
                                    try{var cleanup=PortalAttachments.Cleanup(store);store.WriteRecord("portal-attachment-cleanup-status.dpapi",new{at=DateTime.UtcNow.ToString("o"),state="completed",result=cleanup});}
                                    catch(IOException){AttachmentCleanupError(store,"storage-io");}catch(InvalidOperationException){AttachmentCleanupError(store,"verification-failed");}
                                    nextAttachmentCleanup=DateTime.UtcNow.AddMinutes(5);
                                }
                                if(DateTime.UtcNow>=nextTunnel){try{CloudflareTunnel.Ensure(store,config.Port);}catch(Exception){lock(gate)state.Error="Le tunnel Cloudflare n'a pas pu etre verifie. Consultez son etat dans le Switcher.";}nextTunnel=DateTime.UtcNow.AddSeconds(15);}
                                var due=Webhooks.Jobs(store).Where(j=>CanProcess(store,j)).OrderBy(j=>j.UpdatedUtc,StringComparer.Ordinal).ThenBy(j=>j.CreatedUtc,StringComparer.Ordinal).Take(8).ToArray();
                                for(int offset=0;offset<due.Length;offset+=4){
                                    if(!Webhooks.Config(store).Enabled)break;
                                    Task.WhenAll(due.Skip(offset).Take(4).Select(job=>ProcessBounded(store,job.Id,WebhookDispatch.Process))).GetAwaiter().GetResult();
                                }
                                Thread.Sleep(1000);
                            }
                        }
                    }
                }catch(Exception){lock(gate)state.Error="Le moteur des webhooks s'est arrete. Consultez sa configuration et le journal des missions.";return 1;}
                finally{
                    PortalAdministrationHttp.Stop(store);
                    try{CloudflareTunnel.StopOwned(store);}catch(Exception){}
                    lock(gate){closing=true;state.Listening=false;state.Pid=0;state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("webhook-worker-v1.dpapi",state);}
                }
            }
            return 0;
        }
        private static void AttachmentCleanupError(RelayStore store,string code)
        {
            try{store.WriteRecord("portal-attachment-cleanup-status.dpapi",new{at=DateTime.UtcNow.ToString("o"),state="failed",errorCode=code});}catch(IOException){}catch(InvalidOperationException){}
        }
    }
}
