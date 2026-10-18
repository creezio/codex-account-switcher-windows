using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class WebhookWorkerSmoke
{
    private static int Main(string[] args){try{Run(Path.GetFullPath(args[0]),Path.GetFullPath(args[1])).GetAwaiter().GetResult();return 0;}catch(Exception error){Console.WriteLine("FAIL "+Program.SafeError(error));return 1;}}
    private static async Task Run(string root,string executable)
    {
        Directory.CreateDirectory(root);var store=new RelayStore(Path.Combine(root,"relay"));
        if(Webhooks.Jobs(store).Length!=0)throw new InvalidOperationException("La recette du service exige une file vide.");
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        CloudflareTunnel.SaveConfig(store,new CloudflareTunnelConfig{Enabled=false,Mode="quick"});Webhooks.SaveConfig(store,new WebhookConfig{Enabled=true,Port=port});
        var start=new ProcessStartInfo(executable,"--webhook-worker "+RelayWorker.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(executable)};
        using(var child=Process.Start(start))try{
            bool ready=false;DateTime deadline=DateTime.UtcNow.AddSeconds(20);
            using(var client=new HttpClient(new HttpClientHandler{UseProxy=false})){client.Timeout=TimeSpan.FromSeconds(2);
                while(DateTime.UtcNow<deadline&&!child.HasExited){try{ready=await client.GetStringAsync("http://127.0.0.1:"+port+"/health")=="{\"status\":\"ok\"}";}catch(HttpRequestException){}if(ready)break;await Task.Delay(250);}
            }
            if(!ready)throw new InvalidOperationException("Le vrai moteur webhook n'a pas ouvert son port local.");
            var state=WebhookWorker.Status(store);if(state.Pid!=child.Id||!state.Listening||!DesktopRuntime.SameProcess(state.Pid,state.Started))throw new InvalidOperationException("L'identite du moteur webhook n'est pas confirmee.");
            child.Refresh();long working=child.WorkingSet64;
            Webhooks.SaveConfig(store,new WebhookConfig{Enabled=false,Port=port});
            if(!child.WaitForExit(15000))throw new InvalidOperationException("Le service de recette ne s'est pas arrete cooperativement.");
            var stopped=WebhookWorker.Status(store);if(stopped.Pid!=0||stopped.Listening||child.ExitCode!=0)throw new InvalidOperationException("L'arret du moteur n'est pas confirme.");
            File.WriteAllText(Path.Combine(root,"worker-acceptance.json"),Json.Write(new{completedUtc=DateTime.UtcNow.ToString("o"),nativeWorker=true,health=true,identityPinned=true,cooperativeExit=true,exitCode=child.ExitCode,workingSetBytes=working,realCodexPrompts=0}));
            Console.WriteLine("PASS production webhook worker opens loopback, pins identity and exits cooperatively; RAM "+Math.Round(working/1048576.0,1)+" MiB; Codex prompts 0");
        }finally{Webhooks.SaveConfig(store,new WebhookConfig{Enabled=false,Port=port});}
    }
}
