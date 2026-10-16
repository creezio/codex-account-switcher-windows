using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher.Desktop
{
    // Explicit acceptance entry point; never run on startup or against the primary instance.
    internal static class InstanceLifecycleValidation
    {
        internal static int Run(string id,string report,bool openOnly=false)
        {
            var lines=new List<string>();
            try{Validate(id,lines,openOnly).GetAwaiter().GetResult();File.WriteAllLines(report,lines);return 0;}
            catch(Exception e){lines.Add("FAIL "+e.GetType().Name+": "+Program.SafeError(e));File.WriteAllLines(report,lines);return 1;}
        }
        private static async Task Validate(string id,List<string> lines,bool openOnly)
        {
            if(!InstanceRules.ValidId(id))throw new InvalidOperationException("La recette nécessite une instance gérée explicite.");
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher");
            var accounts=new AccountService(root);var instance=accounts.Data.Instances.Single(i=>i.Id==id);
            var others=accounts.Data.Instances.Where(i=>i.Id!=id).Select(i=>accounts.Instances.Runtime.Probe(i)).Where(s=>s.Running&&s.DesktopPid>0).ToArray();
            string account=accounts.Instances.ActiveKey(instance);
            Action protect=()=>{if(others.Any(s=>!DesktopRuntime.SameProcess(s.DesktopPid,s.DesktopStartTicks)))throw new InvalidOperationException("Une autre instance a changé pendant la recette.");if(accounts.Instances.ActiveKey(instance)!=account)throw new InvalidOperationException("Le compte de la cible a changé.");};
            using(var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4))){
                var token=timeout.Token;var initial=accounts.Instances.Runtime.Probe(instance);
                lines.Add("Initial: "+initial.Phase+" pid="+initial.DesktopPid);
                await DesktopWindows.OpenOrShow(accounts,id,token);
                var opened=accounts.Instances.Runtime.Probe(instance);
                if(!opened.WindowReady)throw new InvalidOperationException("La fenêtre n’a pas été réouverte.");
                if(initial.Running&&initial.DesktopPid!=opened.DesktopPid)throw new InvalidOperationException("La réactivation a remplacé le processus.");
                protect();lines.Add("PASS window reopened without replacing the live desktop; pid="+opened.DesktopPid);
                if(openOnly)return;
                // Wait for the native second-instance callback to finish showing its newly created window.
                await Task.Delay(3000,token);
                using(var process=Process.GetProcessById(opened.DesktopPid))if(!process.CloseMainWindow())throw new InvalidOperationException("La fenêtre n’est pas encore disponible pour la fermeture de recette.");
                var deadline=DateTime.UtcNow.AddSeconds(10);
                while(DesktopWindows.Find(root,instance)!=null&&DateTime.UtcNow<deadline)await Task.Delay(200,token);
                var hidden=accounts.Instances.Runtime.Probe(instance);
                if(!hidden.Running||hidden.WindowReady||hidden.Phase!="background")throw new InvalidOperationException("La fermeture manuelle n’est pas reconnue comme arrière-plan.");
                lines.Add("PASS manual window close detected as background");
                await DesktopWindows.OpenOrShow(accounts,id,token);protect();
                var reopened=accounts.Instances.Runtime.Probe(instance);
                if(!reopened.WindowReady||reopened.DesktopPid!=opened.DesktopPid)throw new InvalidOperationException("La deuxième réactivation n’a pas conservé le processus.");
                lines.Add("PASS repeated window reopen retains the same process");
                await DesktopWindows.CloseOrRestart(accounts,id,true,reopened,token);protect();
                var restarted=accounts.Instances.Runtime.Probe(instance);
                if(!restarted.WindowReady||restarted.LaunchId==reopened.LaunchId)throw new InvalidOperationException("Le redémarrage n’a pas produit un nouveau lancement prêt.");
                lines.Add("PASS targeted restart waited for stop then opened a new launch; pid="+restarted.DesktopPid);
                await DesktopWindows.CloseOrRestart(accounts,id,false,restarted,token);protect();
                if(accounts.Instances.Runtime.Probe(instance).Running)throw new InvalidOperationException("L’instance n’est pas fermée.");
                lines.Add("PASS targeted close reaches stopped state");
                await DesktopWindows.OpenOrShow(accounts,id,token);protect();
                if(!accounts.Instances.Runtime.Probe(instance).WindowReady)throw new InvalidOperationException("L’instance finale n’est pas ouverte.");
                lines.Add("PASS closed instance reopened; original account and other desktops preserved");
            }
        }
    }
}
