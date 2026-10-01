using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        var primary=Process.GetProcessesByName("ChatGPT").Where(p=>p.MainWindowHandle!=IntPtr.Zero).Select(p=>new {p.Id,Ticks=p.StartTime.ToUniversalTime().Ticks}).ToArray();
        var instance=service.Data.Instances.FirstOrDefault(i=>!i.IsLocal && i.Name=="Validation multi-instance") ?? service.Instances.Create("Validation multi-instance");
        Profile account=null;
        if(authPath!=null) {account=service.Import(SafeFiles.ReadText(authPath),"Compte de validation");if(service.Instances.ActiveKey(instance)!=account.Key)service.Instances.Configure(instance,account);else{service.Instances.SyncFreshAuth(account);service.Save();}}
        try {
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90))) {
                await service.Instances.Start(instance,deadline.Token);
                var state=service.Instances.Runtime.Probe(instance);
                if(!state.Running || !state.WindowReady)throw new InvalidOperationException("Second desktop did not reach a ready window.");
                Console.WriteLine("PASS isolated desktop started and rendered; PID="+state.DesktopPid);
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
            Console.WriteLine("PASS original desktop processes, auth and config unchanged");
        }
    }
}
