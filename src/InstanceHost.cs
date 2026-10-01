using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Creezio.Switcher
{
    // A persistent host owns exactly one desktop and its children. It does not own the switcher.
    internal static class InstanceHost
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window,string text);
        private sealed class Health
        {
            public volatile bool Mounted, BootstrapFailed, NetworkWarning;
            public void Observe(object sender,DataReceivedEventArgs line)
            {
                string text=line.Data;if(text==null) return;
                if(text.Contains("app routes mounted")) Mounted=true;
                if(text.Contains("Desktop bootstrap failed")) BootstrapFailed=true;
                if(text.Contains("Desktop network policy does not allow")) NetworkWarning=true;
                // Raw Codex output is discarded; it may contain private account details.
            }
        }
        internal static ProcessStartInfo BuildStart(InstanceLaunch request)
        {
            string home=InstancePaths.Home(request.Root,request.InstanceId), ui=InstancePaths.UserData(request.Root,request.InstanceId);
            var start=new ProcessStartInfo(request.DesktopExecutable,"--user-data-dir=\""+ui+"\"") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=InstancePaths.Folder(request.Root,request.InstanceId)
            };
            foreach(string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray()) {
                if(new[]{"CODEX_","OPENAI_","CHATGPT_","ELECTRON_","OAI_"}.Any(prefix=>key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) || key.Equals("NODE_OPTIONS",StringComparison.OrdinalIgnoreCase))
                    start.EnvironmentVariables.Remove(key);
            }
            start.EnvironmentVariables["CODEX_HOME"]=home;
            start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;
            start.EnvironmentVariables["CODEX_ELECTRON_USER_DATA_PATH"]=ui;
            return start;
        }
        public static int Run(string requestPath)
        {
            InstanceState state=null;string folder=null;Process child=null;ProcessJob job=null;InstanceLease lease=null;
            try {
                var request=Json.Read<InstanceLaunch>(SafeFiles.ReadText(requestPath));
                if(request==null || !InstanceRules.ValidId(request.InstanceId) || !InstanceRules.ValidId(request.LaunchId)) return 2;
                folder=InstancePaths.Folder(request.Root,request.InstanceId);
                if(!String.Equals(Path.GetFullPath(requestPath),Path.Combine(folder,"launch.json"),StringComparison.OrdinalIgnoreCase)) return 2;
                if(!File.Exists(request.DesktopExecutable) || !new[]{"ChatGPT.exe","Codex.exe"}.Contains(Path.GetFileName(request.DesktopExecutable),StringComparer.OrdinalIgnoreCase)) return 2;
                // Directory ACLs are prepared by the unpackaged switcher, never modified in MSIX context.
                SafeFiles.RejectLinks(InstancePaths.Home(request.Root,request.InstanceId));
                SafeFiles.RejectLinks(InstancePaths.UserData(request.Root,request.InstanceId));
                lease=new InstanceLease(folder);
                using(var self=Process.GetCurrentProcess()) state=new InstanceState {LaunchId=request.LaunchId,HostPid=self.Id,HostStartTicks=self.StartTime.ToUniversalTime().Ticks,Phase="starting",Running=true};
                Save(folder,state);
                var health=new Health();
                child=new Process {StartInfo=BuildStart(request)};
                child.OutputDataReceived+=health.Observe;child.ErrorDataReceived+=health.Observe;
                child.Start();job=new ProcessJob(child);
                state.DesktopPid=child.Id;state.DesktopStartTicks=child.StartTime.ToUniversalTime().Ticks;
                child.BeginOutputReadLine();child.BeginErrorReadLine();
                bool closeRequested=false;DateTime closeDeadline=DateTime.MaxValue;
                string stop=Path.Combine(folder,"stop.json");
                while(!child.HasExited) {
                    child.Refresh();
                    if(health.BootstrapFailed) {state.Phase="error";state.Message="Codex n'a pas pu démarrer avec ce profil. Vérifiez sa version et réessayez.";break;}
                    if(!closeRequested && File.Exists(stop)) {
                        InstanceState command=null;
                        try {command=Json.Read<InstanceState>(SafeFiles.ReadText(stop));} catch { }
                        if(command!=null && command.LaunchId==request.LaunchId) {
                            closeRequested=true;closeDeadline=DateTime.UtcNow.AddSeconds(8);state.Phase="stopping";
                            child.CloseMainWindow();
                        }
                    }
                    if(closeRequested && DateTime.UtcNow>=closeDeadline) break;
                    state.WindowReady=health.Mounted && child.MainWindowHandle!=IntPtr.Zero;
                    state.NetworkWarning=health.NetworkWarning;
                    if(!closeRequested) state.Phase=state.WindowReady?"running":"starting";
                    if(child.MainWindowHandle!=IntPtr.Zero && !String.IsNullOrWhiteSpace(request.Name)) {
                        string suffix=" · "+request.Name;
                        if(!child.MainWindowTitle.EndsWith(suffix,StringComparison.Ordinal)) SetWindowText(child.MainWindowHandle,child.MainWindowTitle+suffix);
                    }
                    try {Save(folder,state);} catch(IOException) { /* A transient status-file error must not interrupt work. */ }
                    Thread.Sleep(500);
                }
                if(state.Phase!="error") {state.Phase="stopped";state.Message=null;}
                return 0;
            } catch {
                if(state!=null) {state.Phase="error";state.Message="Le lancement de cette instance a échoué. Les autres instances sont restées ouvertes.";}
                return 1;
            } finally {
                // Only our owned child tree is closed, including on a helper crash. No process-name kill.
                if(job!=null) job.Dispose();
                else if(child!=null) {try {if(!child.HasExited) child.Kill();}catch{}}
                if(child!=null) {try{child.WaitForExit(5000);}catch{} child.Dispose();}
                if(state!=null && folder!=null) {state.Running=false;state.WindowReady=false;try{Save(folder,state);}catch{}}
                if(lease!=null) lease.Dispose();
            }
        }
        private static void Save(string folder,InstanceState state)
        {SafeFiles.AtomicWrite(Path.Combine(folder,"state.json"),Encoding.UTF8.GetBytes(Json.Write(state)));}
    }
}
