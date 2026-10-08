using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class DesktopWindow
    {
        internal int Pid;
        internal long Started;
        internal IntPtr Handle;
    }
    internal static class DesktopWindows
    {
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        [StructLayout(LayoutKind.Sequential)] private struct FlashInfo {public uint Size;public IntPtr Window;public uint Flags,Count,Timeout;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ProcessEntry {public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
        [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private static Dictionary<int,int> Parents()
        {
            var result=new Dictionary<int,int>();var handle=CreateToolhelp32Snapshot(2,0);
            if(handle==new IntPtr(-1))throw new InvalidOperationException("Impossible de vérifier les processus Codex.");
            try{var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf(typeof(ProcessEntry))};if(Process32First(handle,ref entry))do{result[(int)entry.Pid]=(int)entry.Parent;}while(Process32Next(handle,ref entry));return result;}finally{CloseHandle(handle);}
        }

        internal static DesktopWindow[] Snapshot(bool includeBackground=false)
        {
            var windows=new List<DesktopWindow>();var parents=includeBackground?Parents():null;
            foreach(var process in Process.GetProcesses())using(process){
                try {
                    if(!new[]{"ChatGPT","Codex"}.Contains(process.ProcessName,StringComparer.OrdinalIgnoreCase))continue;
                    var handle=process.MainWindowHandle;if(handle==IntPtr.Zero&&!includeBackground)continue;
                    // CLI processes and unrelated ChatGPT installations are not desktop instances.
                    string path=process.MainModule.FileName;
                    if(path.IndexOf("\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)<0||!String.Equals(Path.GetFileName(Path.GetDirectoryName(path)),"app",StringComparison.OrdinalIgnoreCase))continue;
                    if(includeBackground&&parents.ContainsKey(process.Id)){
                        try{using(var parent=Process.GetProcessById(parents[process.Id]))if(String.Equals(parent.MainModule.FileName,path,StringComparison.OrdinalIgnoreCase))continue;}catch(ArgumentException){}
                    }
                    windows.Add(new DesktopWindow{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Handle=handle});
                }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            }
            return windows.ToArray();
        }
        internal static DesktopWindow SelectLocal(IEnumerable<DesktopWindow> windows,IEnumerable<InstanceState> managed)
        {
            var owned=managed.ToArray();
            if(owned.Any(s=>s.Running&&(s.DesktopPid<=0||s.DesktopStartTicks<=0)))throw new InvalidOperationException("Une instance démarre ou son identité est indisponible. Patientez puis réessayez.");
            var remaining=windows.Where(w=>!owned.Any(s=>s.DesktopPid==w.Pid&&s.DesktopStartTicks==w.Started)).ToArray();
            if(remaining.Length>1)throw new InvalidOperationException("Plusieurs fenêtres Codex non gérées sont ouvertes. Sélectionnez la session principale depuis la barre des tâches.");
            return remaining.SingleOrDefault();
        }
        internal static DesktopWindow Find(string root,DesktopInstance instance)
        {
            var windows=Snapshot();var runtime=new DesktopRuntime(root);
            if(!instance.IsLocal){var state=runtime.Probe(instance);return windows.SingleOrDefault(w=>w.Pid==state.DesktopPid&&w.Started==state.DesktopStartTicks);}
            var instances=new Vault(root).Load().Instances??new List<DesktopInstance>();
            return SelectLocal(windows,instances.Where(i=>!i.IsLocal).Select(runtime.Probe));
        }
        internal static InstanceState LocalState(string root)
        {
            try{var runtime=new DesktopRuntime(root);var managed=new Vault(root).Load().Instances.Where(i=>!i.IsLocal).Select(runtime.Probe);var process=SelectLocal(Snapshot(true),managed);bool window=process!=null&&process.Handle!=IntPtr.Zero;return new InstanceState{Running=process!=null,WindowReady=window,DesktopPid=process==null?0:process.Pid,DesktopStartTicks=process==null?0:process.Started,Phase=process==null?"stopped":window?"running":"background"};}
            catch(InvalidOperationException e){return new InstanceState{Running=true,Phase="unknown",Message=e.Message};}
        }
        internal static void RequireSameLaunch(InstanceState expected,InstanceState current)
        {
            if(expected==null||current.Phase=="unknown"||expected.DesktopPid!=current.DesktopPid||expected.DesktopStartTicks!=current.DesktopStartTicks||expected.LaunchId!=current.LaunchId)
                throw new InvalidOperationException("L’instance a changé depuis la demande. Vérifiez son état avant de réessayer.");
        }
        internal static async Task StopLocal(string root,CancellationToken token)
        {
            var state=LocalState(root);if(!state.Running)return;
            if(state.Phase=="unknown"||state.DesktopPid<=0)throw new InvalidOperationException("La session principale ne peut pas être identifiée avec certitude. Fermez-la depuis Codex.");
            using(var process=Process.GetProcessById(state.DesktopPid)){
                if(process.StartTime.ToUniversalTime().Ticks!=state.DesktopStartTicks)throw new InvalidOperationException("Codex a redémarré. Actualisez puis réessayez.");
                process.CloseMainWindow();
                var deadline=DateTime.UtcNow.AddSeconds(3);
                while(!process.HasExited&&DateTime.UtcNow<deadline)await Task.Delay(100,token);
                if(!process.HasExited){RequireSameLaunch(state,LocalState(root));token.ThrowIfCancellationRequested();process.Kill();}
                deadline=DateTime.UtcNow.AddSeconds(10);
                while(!process.HasExited&&DateTime.UtcNow<deadline)await Task.Delay(100,token);
                if(!process.HasExited)throw new InvalidOperationException("La fermeture n’est pas terminée. Aucun nouveau lancement n’a été effectué.");
            }
            // Never kill a process tree: another managed instance or the Switcher may be descendants.
        }
        internal static async Task<string> CloseOrRestart(AccountService accounts,string id,bool restart,InstanceState expected,CancellationToken token)
        {
            var instance=accounts.Data.Instances.Single(i=>i.Id==id);
            var runtime=accounts.Instances.Runtime;
            RequireSameLaunch(expected,runtime.Probe(instance));
            await runtime.Stop(instance,token);
            if(runtime.Probe(instance).Running)throw new InvalidOperationException("La fermeture n’est pas terminée. Le redémarrage n’a pas été lancé.");
            token.ThrowIfCancellationRequested();
            if(restart)return await OpenOrShow(accounts,id,token);
            return "Instance fermée : "+instance.Name+". Son compte et ses conversations sont conservés.";
        }
        internal static bool Focus(DesktopWindow window)
        {
            if(window==null)throw new InvalidOperationException("La fenêtre Codex n'est pas encore disponible. Patientez puis réessayez.");
            uint pid;GetWindowThreadProcessId(window.Handle,out pid);
            if(pid!=window.Pid||!DesktopRuntime.SameProcess(window.Pid,window.Started))throw new InvalidOperationException("Codex a redémarré. Actualisez puis réessayez.");
            ShowWindowAsync(window.Handle,IsIconic(window.Handle)?9:5);
            if(SetForegroundWindow(window.Handle))return true;
            var flash=new FlashInfo{Size=(uint)Marshal.SizeOf(typeof(FlashInfo)),Window=window.Handle,Flags=3,Count=3};FlashWindowEx(ref flash);return false;
        }
        internal static async Task<string> OpenOrShow(AccountService accounts,string instanceId,CancellationToken token)
        {
            var instance=accounts.Data.Instances.Single(i=>i.Id==instanceId);
            if(instance.Archived)throw new InvalidOperationException("Restaurez cette instance avant de l'ouvrir.");
            var window=Find(accounts.Vault.Root,instance);
            if(window==null){
                if(instance.IsLocal){
                    // Standard Windows activation retains the usual profile, without injecting auth or CODEX_HOME.
                    var package=await DesktopRuntime.FindPackage(token);
                    using(Process.Start(new ProcessStartInfo("explorer.exe","shell:AppsFolder\\"+package.Family+"!"+package.AppId){UseShellExecute=true})){}
                }else {
                    var state=accounts.Instances.Runtime.Probe(instance);
                    if(state.Phase=="unknown"||state.Phase=="stopping")throw new InvalidOperationException(state.Message??"Patientez jusqu’à la fin de l’opération en cours.");
                    if(!state.Running)await accounts.Instances.Start(instance,token);
                    else if(state.Phase!="starting")await new DesktopRuntime(accounts.Vault.Root).Reopen(instance,token);
                }
                var deadline=DateTime.UtcNow.AddSeconds(20);
                while(window==null&&DateTime.UtcNow<deadline){await Task.Delay(250,token);window=Find(accounts.Vault.Root,instance);}
            }
            return Focus(window)?"Fenêtre affichée : "+instance.Name:"Windows a signalé « "+instance.Name+" » dans la barre des tâches. Cliquez sur son icône pour la mettre au premier plan.";
        }
    }
}
