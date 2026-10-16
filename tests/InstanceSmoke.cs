using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class InstanceSmoke
{
    private static string Fingerprint(string path){if(!File.Exists(path))return "absent";using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));}
    public static int Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--instance-host")return InstanceHost.Run(args[1]);
        if(args.Length<1){Console.Error.WriteLine("A private test data directory is required.");return 2;}
        try{Run(args[0],args.Length>1?args[1]:null).GetAwaiter().GetResult();return 0;}
        catch(Exception e){Console.Error.WriteLine("FAIL "+Program.SafeError(e));return 1;}
    }
    private static async Task Run(string root,string authPath)
    {
        var service=new AccountService(Path.GetFullPath(root));service.Settings.AutoResetCredits=false;
        string auth=Path.Combine(CodexEnvironment.DefaultHome(),"auth.json"),config=Path.Combine(CodexEnvironment.DefaultHome(),"config.toml");
        string beforeAuth=Fingerprint(auth),beforeConfig=Fingerprint(config);
        var primary=Process.GetProcesses().Where(p=>p.ProcessName.Equals("ChatGPT",StringComparison.OrdinalIgnoreCase)||p.ProcessName.Equals("Codex",StringComparison.OrdinalIgnoreCase)||p.ProcessName.Equals("CodexAccountSwitcher",StringComparison.OrdinalIgnoreCase)).Select(p=>new {p.Id,Ticks=p.StartTime.ToUniversalTime().Ticks}).ToArray();
        var instance=service.Data.Instances.FirstOrDefault(i=>!i.IsLocal && i.Name=="Validation multi-instance") ?? service.Instances.Create("Validation multi-instance");
        if(service.Instances.Runtime.Probe(instance).Running)throw new InvalidOperationException("L'instance de recette est déjà active. Elle est conservée sans intervention.");
        Profile account=null;
        if(authPath!=null) {account=service.Import(SafeFiles.ReadText(authPath),"Compte de validation");if(service.Instances.ActiveKey(instance)!=account.Key)service.Instances.Configure(instance,account);else{service.Instances.SyncFreshAuth(account);service.Save();}}
        try {
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90))) {
                string folder=InstancePaths.Folder(root,instance.Id),requestPath=Path.Combine(folder,"launch.json");
                var package=await DesktopRuntime.FindPackage(deadline.Token);
                var badRequest=new InstanceLaunch{Root=root,InstanceId=instance.Id,LaunchId=Guid.NewGuid().ToString("N"),DesktopExecutable=Path.Combine(folder,"absent","ChatGPT.exe"),Name=instance.Name};
                SafeFiles.AtomicWrite(requestPath,Encoding.UTF8.GetBytes(Json.Write(badRequest)));
                await DesktopRuntime.ActivatePackage(package,DesktopRuntime.HostExecutable(),"--instance-host \""+requestPath+"\"",deadline.Token);
                var failedDeadline=DateTime.UtcNow.AddSeconds(15);InstanceStartupResult failure=null;
                while(DateTime.UtcNow<failedDeadline){failure=InstanceStartup.Read(folder,badRequest.LaunchId);if(failure!=null&&!InstanceLease.Busy(folder))break;await Task.Delay(100,deadline.Token);}
                if(failure==null||failure.ErrorCode!="0x80070002"||failure.Stage!="prepare-profile"||service.Instances.Runtime.Probe(instance).Caption!="Échec du lancement")throw new InvalidOperationException("Packaged host did not publish its actual startup error.");
                Console.WriteLine("PASS actual packaged host failure surfaced before the 45-second timeout: "+failure.ErrorType+" "+failure.ErrorCode);
                foreach(string name in new[]{"state.json","launch.json","stop.json","startup.json"})File.WriteAllText(Path.Combine(folder,name)+".switcher-tmp","fictional interrupted write");
                await service.Instances.Start(instance,deadline.Token);
                var state=service.Instances.Runtime.Probe(instance);
                if(!state.Running || !state.WindowReady)throw new InvalidOperationException("Second desktop did not reach a ready window.");
                Console.WriteLine("PASS isolated desktop started and rendered; PID="+state.DesktopPid);
                if(Directory.GetFiles(folder,"*.switcher-tmp").Any()||state.Phase=="error")throw new InvalidOperationException("Legacy orphan or prior failure remained after retry.");
                Console.WriteLine("PASS four legacy orphans recovered automatically and prior error ignored on retry");
                // A new manager has no in-memory handle: persistent discovery must still work.
                var reopened=new AccountService(service.Vault.Root);
                if(!reopened.Instances.Runtime.Probe(instance).Running)throw new InvalidOperationException("Instance was lost after reopening the manager.");
                Console.WriteLine("PASS running instance rediscovered by a fresh manager");
                if(account!=null) {
                    var usage=await service.FetchUsage(account.AuthJson,deadline.Token);
                    if(usage.Buckets.Count==0)throw new InvalidOperationException("Authenticated quota read failed.");
                    Console.WriteLine("PASS account authentication and live read-only quota request");
                }
                await Task.Delay(10000,deadline.Token);
                state=service.Instances.Runtime.Probe(instance);
                Console.WriteLine("Desktop network warning observed: "+state.NetworkWarning);
            }
        }
        finally {
            service.Instances.Runtime.Stop(instance,CancellationToken.None).GetAwaiter().GetResult();
            if(service.Instances.Runtime.Probe(instance).Running)throw new InvalidOperationException("Test desktop did not stop.");
            Console.WriteLine("PASS targeted desktop stop");
            if(Fingerprint(auth)!=beforeAuth || Fingerprint(config)!=beforeConfig || primary.Any(p=>!DesktopRuntime.SameProcess(p.Id,p.Ticks)))throw new InvalidOperationException("Original instance changed.");
            Console.WriteLine("PASS all original desktops, instance hosts, switcher, auth and config unchanged");
        }
    }
}
