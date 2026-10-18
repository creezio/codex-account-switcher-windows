using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Creezio.Switcher
{
    // The Switcher owns this lightweight process. No scheduler, model turn or
    // Codex desktop is started by supervision, including after a GUI restart.
    internal static class ChatWatchWorker
    {
        internal const string Version="1";
        internal static RelayWorkerState Status(RelayStore store)
        {
            var worker=store.ReadRecord<RelayWorkerState>("chat-watch-worker-v1.dpapi");
            if(DesktopRuntime.SameProcess(worker.Pid,worker.Started))return worker;
            var launching=store.ReadRecord<RelayWorkerState>("chat-watch-launch-v1.dpapi");
            return DesktopRuntime.SameProcess(launching.Pid,launching.Started)?launching:worker;
        }
        internal static bool Running(RelayStore store){var value=Status(store);return DesktopRuntime.SameProcess(value.Pid,value.Started);}
        internal static void EnsureCore(Func<bool> hasEnabled,Func<bool> running,Action spawn)
        {if(hasEnabled()&&!running())spawn();}
        internal static void Ensure(RelayStore store)
        {
            using(store.Lease("chat-watch-control-v1"))EnsureCore(()=>new ChatWatches(store).HasEnabled(),()=>Running(store),()=>Start(store));
        }
        private static void Start(RelayStore store)
        {
            string executable=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");
            if(!File.Exists(executable))throw new InvalidOperationException("CreezioRelay.exe doit accompagner le Switcher pour surveiller les chats.");
            var start=new ProcessStartInfo(executable,"--chat-watch-worker "+RelayWorker.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");
            using(var process=Process.Start(start)){
                process.StandardInput.Close();
                // Only the child writes its heartbeat file. Launch bookkeeping
                // cannot race its first heartbeat or overwrite a diagnostic.
                store.WriteRecord("chat-watch-launch-v1.dpapi",new RelayWorkerState{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Version=Version,Updated=DateTime.UtcNow.ToString("o")});
            }
        }
        internal static int Run(string root)
        {
            var store=new RelayStore(root);FileStream lease;try{lease=store.Lease("chat-watch-worker-v1");}catch(IOException){return 0;}
            using(lease)using(var self=Process.GetCurrentProcess()){
                var state=new RelayWorkerState{Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Version=Version};var gate=new object();bool closing=false;
                Action heartbeat=()=>{lock(gate){if(closing)return;state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("chat-watch-worker-v1.dpapi",state);}};
                try{
                    var watches=new ChatWatches(store);heartbeat();
                    using(var timer=new System.Threading.Timer(o=>{try{heartbeat();}catch(Exception){lock(gate)state.Error="Le signal de présence du moteur de surveillance ne peut plus être enregistré.";}},null,2000,2000)){
                        while(watches.HasEnabled()){
                            try{watches.CheckDue(CancellationToken.None).GetAwaiter().GetResult();lock(gate)state.Error=null;}
                            catch(Exception){lock(gate)state.Error="La vérification native des surveillances n’a pas pu se terminer. Consultez les alertes du Switcher.";}
                            Thread.Sleep(5000);
                        }
                    }
                }finally{lock(gate){closing=true;state.Pid=0;state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("chat-watch-worker-v1.dpapi",state);}}
            }
            return 0;
        }
    }
}
