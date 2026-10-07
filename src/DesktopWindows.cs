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

        internal static DesktopWindow[] Snapshot()
        {
            var windows=new List<DesktopWindow>();
            foreach(var process in Process.GetProcesses())using(process){
                try {
                    if(!new[]{"ChatGPT","Codex"}.Contains(process.ProcessName,StringComparer.OrdinalIgnoreCase))continue;
                    var handle=process.MainWindowHandle;if(handle==IntPtr.Zero)continue;
                    // CLI processes and unrelated ChatGPT installations are not desktop instances.
                    if(process.MainModule.FileName.IndexOf("\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)<0)continue;
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
            try{var window=Find(root,new DesktopInstance{Id="local"});return new InstanceState{Running=window!=null,WindowReady=window!=null,DesktopPid=window==null?0:window.Pid,DesktopStartTicks=window==null?0:window.Started,Phase=window==null?"stopped":"running"};}
            catch(InvalidOperationException e){return new InstanceState{Running=true,Phase="unknown",Message=e.Message};}
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
                }else if(!accounts.Instances.Runtime.Probe(instance).Running)await accounts.Instances.Start(instance,token);
                var deadline=DateTime.UtcNow.AddSeconds(20);
                while(window==null&&DateTime.UtcNow<deadline){await Task.Delay(250,token);window=Find(accounts.Vault.Root,instance);}
            }
            return Focus(window)?"Fenêtre affichée : "+instance.Name:"Windows a signalé « "+instance.Name+" » dans la barre des tâches. Cliquez sur son icône pour la mettre au premier plan.";
        }
    }
}
