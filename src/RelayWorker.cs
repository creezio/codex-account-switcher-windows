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
        public string Updated {get;set;}
        public string Error {get;set;}
        public bool Paused {get;set;}
    }
    internal static class RelayWorker
    {
        internal const string Version="0.12.4-beta.1";
        public static RelayWorkerState Status(RelayStore store){var state=store.ReadRecord<RelayWorkerState>("worker.dpapi");state.Paused=Paused(store);return state;}
        public static bool Running(RelayStore store){var s=Status(store);return DesktopRuntime.SameProcess(s.Pid,s.Started);}
        public static bool Paused(RelayStore store){return store.ReadRecord<RelayWorkerState>("worker-stop.dpapi").Paused;}
        public static void Resume(RelayStore store){store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Paused=false});Ensure(store);}
        public static void Ensure(RelayStore store)
        {
            if(Paused(store))return;
            if(Running(store)){if(Status(store).Version!=Version)throw new InvalidOperationException("Un autre moteur de relais est actif. Arrêtez-le puis redémarrez le moteur depuis cette version du switcher.");return;}
            string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");
            if(!File.Exists(exe))throw new InvalidOperationException("CreezioRelay.exe doit accompagner le switcher.");
            var start=new ProcessStartInfo(exe,"--worker "+Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory};
            // The worker has no calling chat: only persisted, verified origins are used.
            start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");
            using(var p=Process.Start(start)){}
        }
        internal static string Quote(string value){return "\""+value.TrimEnd('\\').Replace("\"","\\\"")+"\"";}
        public static void Stop(RelayStore store){store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Updated=DateTime.UtcNow.ToString("o"),Paused=true});}
        public static int Run(string root)
        {
            var store=new RelayStore(root);if(Paused(store))return 0;FileStream lease;try{lease=store.Lease("worker");}catch(IOException){return 0;}
            using(lease)using(var quit=new CancellationTokenSource())using(var process=Process.GetCurrentProcess()){
                DateTime start=DateTime.UtcNow;var state=new RelayWorkerState{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Version=Version};
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
                    await new Assistance(store).Pump(token);await engine.Pump(token);state.Error=null;
                }catch(OperationCanceledException){throw;}catch(Exception e){state.Error=Program.SafeError(e);}
                try{
                bool active=store.ActiveMessages().Any(m=>m.SchemaVersion>=3&&((m.State=="queued"&&RelayDispatch.AllowsStart(store,m))||m.State=="children"||m.State=="waiting"||m.State=="sending"||m.ReturnState=="sending"||RelayEngine.PendingReturn(m)||((m.State=="uncertain"||m.ReturnState=="uncertain")&&m.ReconcileAttempts<3)));
                active=active||new Assistance(store).List().Any(t=>t.State=="answered"||t.State=="delivering");
                if(active)idle=DateTime.UtcNow;
                if(!active&&RelayDispatch.Read(store).Mode=="drain"){RelayDispatch.Set(store,"paused");Stop(store);return;}
                if(!active&&!RelayPolicies.Load(store).KeepWorkerRunning&&DateTime.UtcNow-idle>TimeSpan.FromSeconds(12))return;
                }catch(IOException e){idle=DateTime.UtcNow;state.Error=Program.SafeError(e);}
                await Task.Delay(1500,token);
            }
            }finally{maintenanceToken.Cancel();try{maintenance.GetAwaiter().GetResult();}catch(OperationCanceledException){}try{engine.Drain().GetAwaiter().GetResult();}catch(OperationCanceledException){}}
        }
    }
    internal static class RelayReconnect
    {
        public static async Task Refresh(RelayStore store,CancellationToken token)
        {
            foreach(var channel in store.Channels().Where(c=>c.Enabled&&AgentProviders.Codex(c)&&!DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks))){
                try{
                    if(!InstanceRules.ValidId(channel.InstanceId))continue;
                    string root=Path.GetDirectoryName(store.Root),home=InstancePaths.Home(root,channel.InstanceId);
                    if(!RelayStore.SamePath(home,channel.Home))continue;
                    var statePath=Path.Combine(InstancePaths.Folder(root,channel.InstanceId),"state.json");
                    if(!File.Exists(statePath))continue;
                    var state=Json.Read<InstanceState>(SafeFiles.ReadText(statePath));
                    if(!DesktopRuntime.SameProcess(state.DesktopPid,state.DesktopStartTicks)||String.IsNullOrEmpty(state.AppToolsPipe))continue;
                    if(AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json"))).Key!=channel.AccountKey)continue;
                    using(var client=new AppToolsClient(state.AppToolsPipe,state.DesktopPid,state.DesktopStartTicks)){
                        var snapshot=await client.Call(channel.AnchorThreadId,"read_thread",new {threadId=channel.AnchorThreadId,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token);
                        if(Json.Str(Json.Get(Json.Get(snapshot,"thread"),"id"))!=channel.AnchorThreadId)continue;
                        channel.PipePath=state.AppToolsPipe;channel.ServerPid=state.DesktopPid;channel.ServerStartTicks=state.DesktopStartTicks;channel.ConnectedUtc=DateTime.UtcNow.ToString("o");channel.Diagnostic="Reconnexion vérifiée";store.Register(channel);
                    }
                }catch(OperationCanceledException){throw;}catch(Exception e){channel.Diagnostic=Program.SafeError(e);store.Register(channel);}
            }
        }
    }
}
