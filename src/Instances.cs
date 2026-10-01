using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class InstanceRules
    {
        public static bool ValidId(string id) { Guid value; return id != null && id.Length == 32 && Guid.TryParseExact(id,"N",out value); }
        public static void Normalize(VaultData data)
        {
            if(data.Instances == null) data.Instances = new List<DesktopInstance>();
            if(data.Instances.Any(i=>i==null || (!i.IsLocal && !ValidId(i.Id))) || data.Instances.GroupBy(i=>i.Id).Any(g=>g.Count()>1))
                throw new InvalidDataException("Instances invalides dans le coffre.");
            if(!data.Instances.Any(i=>i.IsLocal)) data.Instances.Insert(0,new DesktopInstance {Id="local",Name="Session habituelle"});
            foreach(var instance in data.Instances) {
                if(String.IsNullOrWhiteSpace(instance.Name)) instance.Name = instance.IsLocal ? "Session habituelle" : "Instance Codex";
                if(instance.IsLocal) instance.Archived = false;
            }
            foreach(var profile in data.Profiles) {
                if(data.Version == 1) profile.AllInstances = true;
                if(profile.InstanceIds == null) profile.InstanceIds = new List<string>();
                profile.InstanceIds = profile.InstanceIds.Distinct().Where(id=>data.Instances.Any(i=>i.Id==id)).ToList();
                if(String.IsNullOrWhiteSpace(profile.Label)) profile.Label = String.IsNullOrWhiteSpace(profile.Email)?"Compte ChatGPT":profile.Email;
            }
            data.Version = 2;
        }
        public static IEnumerable<Profile> Available(VaultData data, DesktopInstance instance)
        { return data.Profiles.Where(p=>p.Allows(instance.Id)); }
        public static Profile Best(VaultData data, DesktopInstance instance)
        { return Available(data,instance).Where(p=>p.Score.HasValue && p.Score>0).OrderByDescending(p=>p.Score).FirstOrDefault(); }
        public static void RequireAvailable(Profile profile, DesktopInstance instance)
        {
            if(instance.Archived) throw new InvalidOperationException("Restaurez d'abord cette instance archivée.");
            if(!profile.Allows(instance.Id)) throw new InvalidOperationException("Ce compte n'est pas associé à cette instance. Modifiez ses associations dans Comptes.");
        }
    }

    public sealed class InstanceState
    {
        public string LaunchId { get; set; }
        public int HostPid { get; set; }
        public long HostStartTicks { get; set; }
        public int DesktopPid { get; set; }
        public long DesktopStartTicks { get; set; }
        public string Phase { get; set; }
        public string Message { get; set; }
        public bool WindowReady { get; set; }
        public bool NetworkWarning { get; set; }
        public bool Running { get; set; }
    }
    public sealed class DesktopPackage
    {
        public string Family { get; set; }
        public string AppId { get; set; }
        public string Executable { get; set; }
    }
    public sealed class InstanceLaunch
    {
        public string Root { get; set; }
        public string InstanceId { get; set; }
        public string LaunchId { get; set; }
        public string DesktopExecutable { get; set; }
        public string Name { get; set; }
    }
    internal static class InstancePaths
    {
        public static string Folder(string root,string id)
        {
            if(!InstanceRules.ValidId(id)) throw new InvalidOperationException("Identifiant d'instance non valide.");
            string path=Path.Combine(Path.GetFullPath(root),"instances",id);
            SafeFiles.RejectLinks(path); return path;
        }
        public static string Home(string root,string id) { return Path.Combine(Folder(root,id),"codex-home"); }
        public static string UserData(string root,string id) { return Path.Combine(Folder(root,id),"electron-profile"); }
        public static string LockName(string directory)
        {
            using(var sha=SHA256.Create()) return "Local\\Creezio.CodexInstance."+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant()))).Replace("-","");
        }
    }
    internal sealed class InstanceLease : IDisposable
    {
        private readonly Mutex mutex;
        public InstanceLease(string directory)
        {
            mutex=new Mutex(false,InstancePaths.LockName(directory));
            bool acquired;
            try { acquired=mutex.WaitOne(0); } catch(AbandonedMutexException) { acquired=true; }
            if(!acquired) {mutex.Dispose();throw new InvalidOperationException("Cette instance est ouverte ou en cours de lancement. Fermez uniquement cette instance avant de changer son compte.");}
        }
        public void Dispose() {mutex.ReleaseMutex();mutex.Dispose();}
        public static bool Busy(string directory)
        {
            try {using(var lease=new InstanceLease(directory)) return false;} catch(InvalidOperationException) {return true;}
        }
    }
    internal interface IInstanceRuntime
    {
        InstanceState Probe(DesktopInstance instance);
        Task Start(DesktopInstance instance,CancellationToken token);
        Task Stop(DesktopInstance instance,CancellationToken token);
    }

    internal sealed class DesktopRuntime : IInstanceRuntime
    {
        private readonly string root;
        public DesktopRuntime(string vaultRoot) {root=Path.GetFullPath(vaultRoot);}
        internal static string PowerShell { get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");} }
        internal static string QuotePS(string value) {return "'"+value.Replace("'","''")+"'";}
        internal static string EncodePS(string value) {return Convert.ToBase64String(Encoding.Unicode.GetBytes(value));}
        private static async Task<string> RunPowerShell(string command,CancellationToken token)
        {
            var start=new ProcessStartInfo(PowerShell,"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "+EncodePS(command)) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            using(var process=Process.Start(start)) {
                var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
                var deadline=DateTime.UtcNow.AddSeconds(25);
                while(!process.HasExited) {
                    if(token.IsCancellationRequested || DateTime.UtcNow>deadline) {try{process.Kill();}catch{} token.ThrowIfCancellationRequested();throw new InvalidOperationException("Le lanceur Windows n'a pas répondu. Réessayez.");}
                    await Task.Delay(100,token);
                }
                string result=await output; await error;
                if(process.ExitCode!=0) throw new InvalidOperationException("Windows n'a pas pu lancer le paquet Codex. Vérifiez que Codex est installé pour cet utilisateur depuis le Microsoft Store.");
                return result.Trim();
            }
        }
        internal static async Task<DesktopPackage> FindPackage(CancellationToken token)
        {
            string command="$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; "+
                "$p=Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1; if(-not $p){throw 'missing'}; "+
                "$m=Get-AppxPackageManifest -Package $p.PackageFullName; $a=$m.Package.Applications.Application | Where-Object {$_.Executable -match '(?i)(ChatGPT|Codex)\\.exe$'} | Select-Object -First 1; if(-not $a){throw 'missing'}; "+
                "@{Family=$p.PackageFamilyName;AppId=$a.Id;Executable=(Join-Path $p.InstallLocation $a.Executable)} | ConvertTo-Json -Compress";
            var package=Json.Read<DesktopPackage>(await RunPowerShell(command,token));
            if(package==null || String.IsNullOrWhiteSpace(package.Family) || String.IsNullOrWhiteSpace(package.AppId) || !File.Exists(package.Executable))
                throw new InvalidOperationException("L'installation de bureau Codex n'est pas reconnue.");
            return package;
        }
        internal static bool SameProcess(int id,long ticks)
        {
            if(id<=0 || ticks<=0) return false;
            try {using(var p=Process.GetProcessById(id)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks==ticks;} catch(ArgumentException){return false;}
        }
        public InstanceState Probe(DesktopInstance instance)
        {
            if(instance.IsLocal) return new InstanceState {Running=CodexEnvironment.ClientsRunning(),Phase="external",Message="Session habituelle · ouverture et fermeture dans Codex"};
            string folder=InstancePaths.Folder(root,instance.Id), path=Path.Combine(folder,"state.json");
            try {
                var state=File.Exists(path)?Json.Read<InstanceState>(SafeFiles.ReadText(path)):new InstanceState {Phase="stopped"};
                bool held=InstanceLease.Busy(folder);
                state.Running=held || SameProcess(state.DesktopPid,state.DesktopStartTicks);
                if(!state.Running && state.Phase!="error") state.Phase="stopped";
                if(held && state.Phase=="stopped") state.Phase="starting";
                return state;
            } catch {return new InstanceState {Running=true,Phase="unknown",Message="État indéterminé : aucune modification de cette instance n'est autorisée."};}
        }
        public async Task Start(DesktopInstance instance,CancellationToken token)
        {
            if(instance.IsLocal) throw new InvalidOperationException("Ouvrez votre session habituelle avec son raccourci Codex.");
            if(instance.Archived) throw new InvalidOperationException("Restaurez l'instance avant de l'ouvrir.");
            if(Probe(instance).Running) throw new InvalidOperationException("Cette instance est déjà ouverte.");
            var package=await FindPackage(token);
            string folder=InstancePaths.Folder(root,instance.Id);
            var request=new InstanceLaunch {Root=root,InstanceId=instance.Id,LaunchId=Guid.NewGuid().ToString("N"),DesktopExecutable=package.Executable,Name=instance.Name};
            using(var lease=new InstanceLease(folder)) {
                SafeFiles.AtomicWrite(Path.Combine(folder,"launch.json"),Encoding.UTF8.GetBytes(Json.Write(request)));
            }
            string host=System.Reflection.Assembly.GetExecutingAssembly().Location;
            string hostArgs="--instance-host \""+Path.Combine(folder,"launch.json")+"\"";
            string command="$ErrorActionPreference='Stop'; Invoke-CommandInDesktopPackage -PackageFamilyName "+QuotePS(package.Family)+" -AppId "+QuotePS(package.AppId)+" -Command "+QuotePS(host)+" -Args "+QuotePS(hostArgs)+" -PreventBreakaway";
            try {
                await RunPowerShell(command,token);
                var deadline=DateTime.UtcNow.AddSeconds(45);
                while(DateTime.UtcNow<deadline) {
                    token.ThrowIfCancellationRequested();
                    var state=Probe(instance);
                    if(state.LaunchId==request.LaunchId) {
                        if(state.Phase=="error" || (!state.Running && state.Phase=="stopped")) throw new InvalidOperationException(state.Message ?? "L'instance s'est arrêtée pendant le lancement.");
                        if(state.WindowReady && state.Running) return;
                    }
                    await Task.Delay(250,token);
                }
                throw new InvalidOperationException("Codex démarre encore. Son état reste visible dans Instances ; aucune autre instance n'a été fermée.");
            } catch(OperationCanceledException) {
                SafeFiles.AtomicWrite(Path.Combine(folder,"stop.json"),Encoding.UTF8.GetBytes(Json.Write(new {LaunchId=request.LaunchId})));
                throw;
            }
        }
        public async Task Stop(DesktopInstance instance,CancellationToken token)
        {
            if(instance.IsLocal) throw new InvalidOperationException("La session habituelle ne peut pas être fermée depuis le switcher.");
            var state=Probe(instance);
            if(!state.Running) return;
            if(String.IsNullOrEmpty(state.LaunchId) || !SameProcess(state.HostPid,state.HostStartTicks))
                throw new InvalidOperationException("Cette fenêtre n'est pas contrôlée par le switcher. Fermez-la directement dans Codex.");
            SafeFiles.AtomicWrite(Path.Combine(InstancePaths.Folder(root,instance.Id),"stop.json"),Encoding.UTF8.GetBytes(Json.Write(new {LaunchId=state.LaunchId})));
            var deadline=DateTime.UtcNow.AddSeconds(20);
            while(DateTime.UtcNow<deadline && Probe(instance).Running) await Task.Delay(250,token);
            if(Probe(instance).Running) throw new InvalidOperationException("La fermeture n'est pas encore terminée. Le compte de l'instance reste inchangé.");
        }
    }
}
