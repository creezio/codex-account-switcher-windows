using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class RelayWorkerState
    {
        public int Pid {get;set;}
        public long Started {get;set;}
        public string Version {get;set;}
        public int? ExecutionVersion {get;set;}
        public int? CloudJobSchemaVersion {get;set;}
        public int? CloudQueueAdmissionVersion {get;set;}
        public string Updated {get;set;}
        public string Error {get;set;}
        public bool Paused {get;set;}
    }
    internal static class RelayWorker
    {
        // Portal missions require the per-instance grant to be revalidated
        // immediately before sending into a native conversation.
        internal const string Version="0.29.0-beta.7";
        public static RelayWorkerState Status(RelayStore store){var state=store.ReadRecord<RelayWorkerState>("worker.dpapi");state.Paused=Paused(store);return state;}
        public static bool Running(RelayStore store){var s=Status(store);return DesktopRuntime.SameProcess(s.Pid,s.Started);}
        public static bool Paused(RelayStore store){return store.ReadRecord<RelayWorkerState>("worker-stop.dpapi").Paused;}
        public static void Resume(RelayStore store){using(Control(store))store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Paused=false});Ensure(store);}
        public static void Ensure(RelayStore store)
        {
            EnsureCore(store,Version,state=>DesktopRuntime.SameProcess(state.Pid,state.Started),()=>Start(store),Thread.Sleep,5000);
        }
        private static FileStream Control(RelayStore store)
        {
            try{return store.Lease("worker-update");}
            catch(IOException error){if(!Sharing(error))throw;throw new InvalidOperationException("Une mise à jour du moteur de relais est en cours. Réessayez dans quelques secondes ; aucune nouvelle mission n'a été envoyée.");}
        }
        private static bool Sharing(IOException error){return (error.HResult&0xffff)==32||(error.HResult&0xffff)==33;}
        internal static void EnsureCore(RelayStore store,string desiredVersion,Func<RelayWorkerState,bool> running,Action spawn,Action<int> wait,int timeoutMilliseconds)
        {
            if(timeoutMilliseconds<0||timeoutMilliseconds>5000)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            using(Control(store)){
                bool signalled=false;RelayWorkerState retiring=null;var deadline=Stopwatch.StartNew();
                while(true){
                    // A user pause is independent from dispatch pause/drain. An
                    // upgrade changes neither policy nor any mission journal.
                    if(Paused(store))return;
                    var current=Status(store);
                    // The legacy worker writes Pid=0 just before leaving Run.
                    // Keep the original PID/start pinned until that process exits.
                    if(retiring!=null&&running(retiring))current=retiring;
                    if(running(current)){
                        if(current.Version==desiredVersion)return;
                        if(!signalled){
                            // 0.17.2 already observes Updated regardless of Paused.
                            // It will finish cancellation/uncertainty bookkeeping
                            // and release its own lease; never kill its process.
                            store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Updated=DateTime.UtcNow.ToString("o"),Paused=false});retiring=current;signalled=true;
                        }
                    }else{
                        FileStream available=null;
                        try{available=store.Lease("worker");}catch(IOException error){if(!Sharing(error))throw;}
                        if(available!=null){
                            // Release before spawning: the child owns this lease
                            // for its lifetime and must acquire it itself.
                            available.Dispose();
                            if(Paused(store))return;
                            spawn();return;
                        }
                    }
                    int remaining=timeoutMilliseconds-(int)deadline.ElapsedMilliseconds;
                    if(remaining<=0)throw new InvalidOperationException("La mise à jour du moteur attend sa fermeture coopérative. Les missions sont conservées ; réessayez dans quelques secondes. Aucune instance Codex n'a été fermée et aucun travail n'a été renvoyé.");
                    wait(Math.Min(50,remaining));
                }
            }
        }
        private static void Start(RelayStore store)
        {
            string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");
            if(!File.Exists(exe))throw new InvalidOperationException("CreezioRelay.exe doit accompagner le switcher.");
            var start=new ProcessStartInfo(exe,"--worker "+Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory};
            // The worker has no calling chat: only persisted, verified origins are used.
            start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");
            using(var p=Process.Start(start)){}
        }
        internal static string Quote(string value){return "\""+value.TrimEnd('\\').Replace("\"","\\\"")+"\"";}
        public static void Stop(RelayStore store){using(Control(store))store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Updated=DateTime.UtcNow.ToString("o"),Paused=true});}
        public static int Run(string root)
        {
            var store=new RelayStore(root);if(Paused(store))return 0;FileStream lease;try{lease=store.Lease("worker");}catch(IOException){return 0;}
            using(lease)using(var quit=new CancellationTokenSource())using(var process=Process.GetCurrentProcess()){
                DateTime start=DateTime.UtcNow;var state=new RelayWorkerState{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Version=Version,ExecutionVersion=CloudJobWorker.ExecutionVersion,CloudJobSchemaVersion=CloudJobWorker.SchemaVersion};
                Action heartbeat=delegate{try{state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("worker.dpapi",state);DateTime stop;if(DateTime.TryParse(store.ReadRecord<RelayWorkerState>("worker-stop.dpapi").Updated,out stop)&&stop.ToUniversalTime()>start)quit.Cancel();}catch(IOException){}};
                heartbeat();
                using(var timer=new System.Threading.Timer(o=>heartbeat(),null,2000,2000)){
                    try{Loop(store,state,quit.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){}catch(Exception e){state.Error=Program.SafeError(e);}
                }
                state.Pid=0;state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("worker.dpapi",state);return 0;
            }
        }
        private static async Task Loop(RelayStore store,RelayWorkerState state,CancellationToken token)
        {
            var engine=new RelayEngine(store);DateTime idle=DateTime.UtcNow,lastConnect=DateTime.MinValue;
            Task maintenance=Task.FromResult(0);
            using(var maintenanceToken=CancellationTokenSource.CreateLinkedTokenSource(token))try{
            while(!token.IsCancellationRequested){
                try{
                    if(maintenance.IsCompleted&&DateTime.UtcNow-lastConnect>TimeSpan.FromSeconds(15)){
                        if(maintenance.IsFaulted)state.Error=Program.SafeError(maintenance.Exception.GetBaseException());
                        maintenance=Task.Run(async()=>{await RelayReconnect.Refresh(store,maintenanceToken.Token);await RelayQuota.RefreshWaiting(store,maintenanceToken.Token);},maintenanceToken.Token);lastConnect=DateTime.UtcNow;
                    }
                    await new Assistance(store).Pump(token);await engine.Pump(token);
                    var cloudJobs=new CloudJobs(store);
                    if(CloudJobWorker.HasRemotePending(cloudJobs))CloudJobWorker.Ensure(store);
                    await cloudJobs.PumpLocalReturns(AgentProviders.Create(store),token);
                    await HeadlessRelayTransport.PumpOwned(store,token);state.Error=null;
                }catch(OperationCanceledException){throw;}catch(Exception e){state.Error=Program.SafeError(e);}
                try{
                bool active=store.ActiveMessages().Any(m=>m.SchemaVersion>=3&&((m.State=="queued"&&RelayDispatch.AllowsStart(store,m))||m.State=="children"||m.State=="waiting"||m.State=="sending"||m.ReturnState=="sending"||RelayEngine.PendingReturn(m)||((m.State=="uncertain"||m.ReturnState=="uncertain")&&m.ReconcileAttempts<3)));
                var cloudBacklog=new CloudJobs(store);
                active=active||new Assistance(store).List().Any(t=>t.State=="answered"||t.State=="delivering")||cloudBacklog.LocalReturns().Any()||CloudJobWorker.HasRemotePending(cloudBacklog)||HeadlessRelayTransport.HasOwnedWork(store);
                if(active)idle=DateTime.UtcNow;
                if(!active&&RelayDispatch.Read(store).Mode=="drain"){RelayDispatch.Set(store,"paused");Stop(store);return;}
                if(!active&&!RelayPolicies.Load(store).KeepWorkerRunning&&DateTime.UtcNow-idle>TimeSpan.FromSeconds(12))return;
                }catch(IOException e){idle=DateTime.UtcNow;state.Error=Program.SafeError(e);}
                await Task.Delay(1500,token);
            }
            }finally{maintenanceToken.Cancel();try{maintenance.GetAwaiter().GetResult();}catch(OperationCanceledException){}try{engine.Drain().GetAwaiter().GetResult();}catch(OperationCanceledException){}finally{HeadlessRelayTransport.Shutdown(store);}}
        }
    }
    internal static class RelayReconnect
    {
        internal sealed class Endpoint
        {
            internal string PipePath,ThreadId;
            internal int Pid;
            internal long Started;
        }
        public static async Task Refresh(RelayStore store,CancellationToken token)
        {
            await RefreshCore(store,()=>new AccountService(Path.GetDirectoryName(store.Root)),Discover,DesktopRuntime.SameProcess,token).ConfigureAwait(false);
        }
        private static DesktopInstance RequireInstance(AccountService accounts,RelayChannel channel)
        {
            var matches=accounts.Data.Instances.Where(i=>!i.Archived&&(String.IsNullOrEmpty(channel.InstanceId)||i.Id==channel.InstanceId)&&i.AccountKey==channel.AccountKey&&RelayStore.SamePath(accounts.Instances.Home(i),channel.Home)).ToArray();
            if(matches.Length!=1||String.IsNullOrEmpty(channel.AnchorThreadId))throw new InvalidOperationException("L’instance, le compte ou le chat d’ancrage ne correspond plus au canal enregistré.");
            NamedInstances.VerifyAccount(accounts,matches[0]);return matches[0];
        }
        private static async Task<Endpoint> Discover(RelayStore store,AccountService accounts,DesktopInstance instance,RelayChannel channel,CancellationToken token)
        {
            string pipe;using(var client=NamedInstances.OpenDesktop(store,accounts,instance,token,out pipe)){
                var snapshot=await client.Call(channel.AnchorThreadId,"read_thread",new{threadId=channel.AnchorThreadId,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token).ConfigureAwait(false);
                DirectMessages.RequireThread(snapshot,channel.AnchorThreadId);
                return new Endpoint{PipePath=pipe,Pid=client.ServerPid,Started=client.ServerStartTicks,ThreadId=channel.AnchorThreadId};
            }
        }
        internal static async Task RefreshCore(RelayStore store,Func<AccountService> accounts,Func<RelayStore,AccountService,DesktopInstance,RelayChannel,CancellationToken,Task<Endpoint>> discover,Func<int,long,bool> alive,CancellationToken token)
        {
            foreach(var channel in store.Channels().Where(c=>c.Enabled&&AgentProviders.Kind(c)=="codex")){
                token.ThrowIfCancellationRequested();
                try{
                    if(alive(channel.ServerPid,channel.ServerStartTicks))continue;
                    var before=accounts();var instance=RequireInstance(before,channel);
                    var endpoint=await discover(store,before,instance,channel,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();var after=accounts();var fresh=RequireInstance(after,channel);
                    var state=after.Instances.Runtime.Probe(fresh);
                    if(fresh.Id!=instance.Id||endpoint==null||String.IsNullOrEmpty(endpoint.PipePath)||endpoint.ThreadId!=channel.AnchorThreadId||!state.Running||state.Phase=="unknown"||state.DesktopPid!=endpoint.Pid||state.DesktopStartTicks!=endpoint.Started||!alive(endpoint.Pid,endpoint.Started))throw new InvalidOperationException("La preuve de reconnexion Codex n’est plus valide.");
                    store.RefreshDesktopEndpoint(channel,endpoint.PipePath,endpoint.Pid,endpoint.Started,"Reconnexion vérifiée");
                }catch(OperationCanceledException){throw;}
                catch(Exception){store.RefreshDesktopEndpoint(channel,null,0,0,"Reconnexion non confirmée. Vérifiez que l’instance et son compte d’origine sont toujours ouverts. Aucun autre moteur n’a été lancé.");}
            }
        }
    }
}
