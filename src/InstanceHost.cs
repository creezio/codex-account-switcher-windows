using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Creezio.Switcher
{
    // A persistent host owns exactly one desktop and its children. It does not own the switcher.
    internal static class InstanceHost
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window,string text);
        internal static string AppPipeFromLog(string text)
        {
            if(String.IsNullOrEmpty(text)||!text.Contains("dynamic_app_tools_listening"))return null;
            var name=Regex.Match(text,@"(?<![a-zA-Z0-9_/.-])codex-(?:browser-use|app-tools)-[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}(?![a-zA-Z0-9_\\/.-])");
            return name.Success?"\\\\.\\pipe\\"+name.Value:null;
        }
        private sealed class Health
        {
            public volatile bool Mounted, BootstrapFailed, NetworkWarning;
            public string AppToolsPipe;
            public void Observe(object sender,DataReceivedEventArgs line)
            {
                string text=line.Data;if(text==null) return;
                if(text.Contains("app routes mounted")) Mounted=true;
                if(text.Contains("Desktop bootstrap failed")) BootstrapFailed=true;
                if(text.Contains("Desktop network policy does not allow")) NetworkWarning=true;
                if(text.Contains("dynamic_app_tools_listening")) {
                    string pipe=AppPipeFromLog(text);
                    if(pipe!=null) AppToolsPipe=pipe;
                }
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
                if(new[]{"CODEX_","OPENAI_","CHATGPT_","ELECTRON_","OAI_","CREEZIO_DESKTOP_BRIDGE_"}.Any(prefix=>key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) || key.Equals("NODE_OPTIONS",StringComparison.OrdinalIgnoreCase))
                    start.EnvironmentVariables.Remove(key);
            }
            start.EnvironmentVariables["CODEX_HOME"]=home;
            start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;
            start.EnvironmentVariables["CODEX_ELECTRON_USER_DATA_PATH"]=ui;
            if(!String.IsNullOrEmpty(request.MetadataBridgeExecutable)||!String.IsNullOrEmpty(request.NativeCodexExecutable)){
                if(String.IsNullOrEmpty(request.AccountKey)||!InstanceRules.ValidId(request.LaunchId)||String.IsNullOrEmpty(request.MetadataBridgeExecutable)||String.IsNullOrEmpty(request.NativeCodexExecutable)||!Path.IsPathRooted(request.MetadataBridgeExecutable)||!Path.IsPathRooted(request.NativeCodexExecutable)||!String.Equals(Path.GetFileName(request.MetadataBridgeExecutable),"SwitcherCodexBridge.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("La passerelle de cette instance n'est pas liee a son lancement.");
                start.EnvironmentVariables["CODEX_CLI_PATH"]=request.MetadataBridgeExecutable;
                start.EnvironmentVariables["CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH"]=Path.Combine(InstancePaths.Folder(request.Root,request.InstanceId),"launch.json");
            }
            return start;
        }
        // A second Electron activation reopens the existing profile through its single-instance lock.
        // It does not replace auth, restart the live desktop, or acquire the host's lifetime lease.
        internal static void RequireActivation(InstanceLaunch request,InstanceState state,string launchId)
        {
            if(request==null||state==null||!InstanceRules.ValidId(launchId)||request.LaunchId!=launchId||state.LaunchId!=launchId||!state.Running||state.Phase=="stopping")
                throw new InvalidOperationException("Le lancement de l’instance a changé. Actualisez puis réessayez.");
            if(!DesktopRuntime.SameProcess(state.HostPid,state.HostStartTicks)||!DesktopRuntime.SameProcess(state.DesktopPid,state.DesktopStartTicks))
                throw new InvalidOperationException("Le processus de cette instance n’est plus disponible.");
        }
        internal static int Activate(string requestPath,string launchId)
        {
            try {
                var request=Json.Read<InstanceLaunch>(SafeFiles.ReadText(requestPath));
                string folder=InstancePaths.Folder(request.Root,request.InstanceId);
                if(!String.Equals(Path.GetFullPath(requestPath),Path.Combine(folder,"launch.json"),StringComparison.OrdinalIgnoreCase))return 2;
                var state=Json.Read<InstanceState>(SafeFiles.ReadText(Path.Combine(folder,"state.json")));
                RequireActivation(request,state,launchId);
                using(var running=Process.GetProcessById(state.DesktopPid)) {
                    // Use the live executable, even if the Store has since installed another version.
                    request.DesktopExecutable=running.MainModule.FileName;
                    if(!new[]{"ChatGPT.exe","Codex.exe"}.Contains(Path.GetFileName(request.DesktopExecutable),StringComparer.OrdinalIgnoreCase))return 2;
                }
                var start=BuildStart(request);
                start.RedirectStandardOutput=false;start.RedirectStandardError=false;
                start.StandardOutputEncoding=null;start.StandardErrorEncoding=null;
                using(var process=Process.Start(start)) {
                    if(process.WaitForExit(15000))return process.ExitCode;
                    // No automatic restart or termination: preserve any work already running.
                    return 1;
                }
            }catch{return 1;}
        }
        public static int Run(string requestPath)
        {
            InstanceState state=null;string folder=null;Process child=null;ProcessJob job=null;InstanceLease lease=null;
            InstanceLaunch request=null;string stage="read-request";
            try {
                request=Json.Read<InstanceLaunch>(SafeFiles.ReadText(requestPath));
                if(request==null || !InstanceRules.ValidId(request.InstanceId) || !InstanceRules.ValidId(request.LaunchId)) throw new InvalidDataException("Demande invalide.");
                folder=InstancePaths.Folder(request.Root,request.InstanceId);
                if(!String.Equals(Path.GetFullPath(requestPath),Path.Combine(folder,"launch.json"),StringComparison.OrdinalIgnoreCase)){folder=null;throw new InvalidDataException("Chemin invalide.");}
                stage="prepare-profile";
                if(!File.Exists(request.DesktopExecutable))throw new FileNotFoundException("Exécutable Codex absent.");
                if(!new[]{"ChatGPT.exe","Codex.exe"}.Contains(Path.GetFileName(request.DesktopExecutable),StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Exécutable non reconnu.");
                // Directory ACLs are prepared by the unpackaged switcher, never modified in MSIX context.
                SafeFiles.RejectLinks(InstancePaths.Home(request.Root,request.InstanceId));
                SafeFiles.RejectLinks(InstancePaths.UserData(request.Root,request.InstanceId));
                stage="acquire-instance";lease=new InstanceLease(folder);
                stage="recover-temporaries";AtomicFiles.RecoverLegacyInstance(folder);
                using(var self=Process.GetCurrentProcess()) state=new InstanceState {LaunchId=request.LaunchId,HostPid=self.Id,HostStartTicks=self.StartTime.ToUniversalTime().Ticks,Phase="starting",Running=true};
                stage="write-state";Save(folder,state);
                var health=new Health();
                child=new Process {StartInfo=BuildStart(request)};
                child.OutputDataReceived+=health.Observe;child.ErrorDataReceived+=health.Observe;
                stage="start-desktop";child.Start();job=new ProcessJob(child);
                state.DesktopPid=child.Id;state.DesktopStartTicks=child.StartTime.ToUniversalTime().Ticks;
                child.BeginOutputReadLine();child.BeginErrorReadLine();
                bool closeRequested=false,wasReady=false;DateTime closeDeadline=DateTime.MaxValue;
                string stop=Path.Combine(folder,"stop.json");
                stage="bootstrap";
                while(!child.HasExited) {
                    child.Refresh();
                    if(health.BootstrapFailed) {var failed=InstanceStartup.Bootstrap(request.LaunchId);state.Phase="error";state.Message=failed.Message;Report(folder,failed);break;}
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
                    wasReady|=state.WindowReady;
                    state.NetworkWarning=health.NetworkWarning;
                    state.AppToolsPipe=health.AppToolsPipe;
                    if(!closeRequested) state.Phase=state.WindowReady?"running":wasReady?"background":"starting";
                    if(child.MainWindowHandle!=IntPtr.Zero && !String.IsNullOrWhiteSpace(request.Name)) {
                        string suffix=" · "+request.Name;
                        if(!child.MainWindowTitle.EndsWith(suffix,StringComparison.Ordinal)) SetWindowText(child.MainWindowHandle,child.MainWindowTitle+suffix);
                    }
                    try {Save(folder,state);} catch(IOException) { /* A transient status-file error must not interrupt work. */ }
                    Thread.Sleep(500);
                }
                if(!wasReady&&!closeRequested&&state.Phase!="error") {var failed=InstanceStartup.DesktopExit(request.LaunchId,child.ExitCode);state.Phase="error";state.Message=failed.Message;Report(folder,failed);}
                if(state.Phase!="error") {state.Phase="stopped";state.Message=null;}
                return state.Phase=="error"?1:0;
            } catch(Exception error) {
                var failed=InstanceStartup.Failure(request==null?null:request.LaunchId,stage,error);
                if(state!=null) {state.Phase="error";state.Message=failed.Message;}
                Report(folder,failed);
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
        private static void Report(string folder,InstanceStartupResult error)
        {
            try{if(folder!=null)InstanceStartup.Save(folder,error);}catch{}
            // Diagnostic codes and trusted labels only, never raw Codex output, arguments or credentials.
            try{Console.Error.WriteLine(error.Message);}catch{}
        }
    }
}
