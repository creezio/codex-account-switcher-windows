using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Creezio.Switcher
{
    internal sealed class PortalScheduleStop
    {
        public int Pid {get;set;} public long Started {get;set;}
        public string Version {get;set;} public string RequestedVersion {get;set;}
    }
    // Separate from the read-only ChatWatchWorker so existing supervision
    // processes need not be retired or gain permission to dispatch a turn.
    internal static class PortalScheduleWorker
    {
        internal const string Version=RelayWorker.Version;
        internal const string StateRecord="conversation-schedule-worker-v1.dpapi";
        internal const string LaunchRecord="conversation-schedule-launch-v1.dpapi",StopRecord="conversation-schedule-stop-v1.dpapi";
        internal static bool Newer(string desired,string current)
        {if(String.IsNullOrEmpty(desired)||String.IsNullOrEmpty(current))return false;try{return SwitcherUpdatePackages.CompareVersions(desired,current)>0;}catch(InvalidOperationException){return false;}}
        internal static bool ShouldRetire(RelayWorkerState worker,PortalScheduleStop request)
        {return worker!=null&&request!=null&&worker.Pid>0&&worker.Pid==request.Pid&&worker.Started==request.Started&&worker.Version==request.Version&&Newer(request.RequestedVersion,worker.Version);}
        internal static bool Running(RelayStore store)
        {
            var current=store.ReadRecord<RelayWorkerState>(StateRecord);
            if(DesktopRuntime.SameProcess(current.Pid,current.Started))return true;
            var launch=store.ReadRecord<RelayWorkerState>(LaunchRecord);
            return DesktopRuntime.SameProcess(launch.Pid,launch.Started);
        }
        internal static void Ensure(RelayStore store)
        {
            using(store.Lease("conversation-schedule-control-v1"))
            EnsureCore(store,Version,()=>new PortalConversationManagement(store).HasEnabled(),value=>DesktopRuntime.SameProcess(value.Pid,value.Started),()=>Start(store));
        }
        internal static void EnsureCore(RelayStore store,string desired,Func<bool> hasEnabled,Func<RelayWorkerState,bool> alive,Action spawn)
        {
            SwitcherUpdatePackages.RequireVersion(desired);if(!hasEnabled())return;
            var current=store.ReadRecord<RelayWorkerState>(StateRecord);var launch=store.ReadRecord<RelayWorkerState>(LaunchRecord);var request=store.ReadRecord<PortalScheduleStop>(StopRecord);
            // Old webhook hosts may keep ticking after an application update.
            // Their Ensure must never retire or respawn a newer scheduler.
            if(Newer(current.Version,desired)||Newer(launch.Version,desired)||Newer(request.RequestedVersion,desired))return;
            var running=alive(current)?current:alive(launch)?launch:null;
            if(running!=null){
                if(!Newer(desired,running.Version))return;
                store.WriteRecord(StopRecord,new PortalScheduleStop{Pid=running.Pid,Started=running.Started,Version=running.Version,RequestedVersion=desired});
                if(alive(launch)&&!Newer(desired,launch.Version))return;
                // The new child waits for the old child's lifetime lease. The
                // old child finishes its current admission, then exits itself.
                spawn();return;
            }
            spawn();
        }
        private static void Start(RelayStore store)
        {
            string executable=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");
            if(!File.Exists(executable))throw new InvalidOperationException("Le moteur de planification Switcher doit accompagner cette version.");
            var start=new ProcessStartInfo(executable,"--conversation-schedule-worker "+RelayWorker.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");
            using(var child=Process.Start(start)){
                child.StandardInput.Close();
                store.WriteRecord(LaunchRecord,new RelayWorkerState{Pid=child.Id,Started=child.StartTime.ToUniversalTime().Ticks,Version=Version,Updated=DateTime.UtcNow.ToString("o")});
            }
        }
        internal static int Run(string root)
        {
            var store=new RelayStore(root);
            using(var self=Process.GetCurrentProcess()){
                var state=new RelayWorkerState{Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Version=Version};
                FileStream lease=null;
                while(lease==null){
                    var request=store.ReadRecord<PortalScheduleStop>(StopRecord);var current=store.ReadRecord<RelayWorkerState>(StateRecord);
                    if(Newer(request.RequestedVersion,Version)||Newer(current.Version,Version)||DesktopRuntime.SameProcess(current.Pid,current.Started)&&!Newer(Version,current.Version))return 0;
                    try{lease=store.Lease("conversation-schedule-worker-v1");}catch(IOException){Thread.Sleep(100);}
                }
                using(lease){
                var manager=new PortalConversationManagement(store);
                try{
                    Func<bool> keepRunning=()=>!ShouldRetire(state,store.ReadRecord<PortalScheduleStop>(StopRecord));
                    while(keepRunning()&&manager.HasEnabled()){
                        state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord(StateRecord,state);
                        try{manager.PumpDue(CancellationToken.None,keepRunning).GetAwaiter().GetResult();state.Error=null;}
                        catch(Exception){state.Error="Une occurrence attend la verification de ses autorisations ou de son admission dans le Switcher.";}
                        state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord(StateRecord,state);Thread.Sleep(5000);
                    }
                }finally{state.Pid=0;state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord(StateRecord,state);}
                }
            }
            return 0;
        }
    }
}
