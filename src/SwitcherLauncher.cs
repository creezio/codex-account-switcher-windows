using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Creezio.Switcher
{
    // The stable executable never lives inside a version directory. GUI and
    // runtime payloads are immutable and selected by one atomic pointer.
    internal static class SwitcherLauncher
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool background=args.Contains("--startup");
            try{
                var store=SwitcherInstallation.Store();
                if(args.Length==2&&args[0]=="--install-from"){
                    string source=Path.GetFullPath(args[1]);
                    var existing=SwitcherInstallation.Read(SwitcherInstallation.Root,false);
                    if(existing!=null&&existing.Version!=RelayWorker.Version)throw new InvalidOperationException("Une autre version est installée. Utilisez la mise à jour depuis Switcher.");
                    var adopted=SwitcherInstallation.Adopt(SwitcherInstallation.Root,source,RelayWorker.Version);
                    if(SwitcherInstallation.Read(SwitcherInstallation.Root,false).Version!=adopted.Version)throw new InvalidOperationException("Une installation plus ancienne existe déjà. Utilisez la mise à jour depuis Switcher.");
                    SwitcherInstallation.CreateShortcut();ApplicationUpdates.SavePreferences(store,true,true);return 0;
                }
                SafeFiles.PrivateDirectory(SwitcherInstallation.Root);
                string leasePath=Path.Combine(SwitcherInstallation.Root,"application-update.lock");SafeFiles.RejectLinks(leasePath);
                if(args.Length==2&&args[0]=="--apply"){
                    using(var lease=new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))return Apply(store,args[1]);
                }
                if(args.Length!=0&&!(args.Length==1&&args[0]=="--startup"))throw new InvalidOperationException("Option de lancement inconnue.");
                // An ordinary click during a transaction waits for its outcome;
                // it must not accidentally activate the previous GUI meanwhile.
                FileStream available=null;
                for(int attempt=0;available==null&&attempt<450;attempt++){
                    try{available=new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                    catch(IOException e){if((e.HResult&65535)!=32&&(e.HResult&65535)!=33)throw;Thread.Sleep(200);}
                }
                if(available==null)throw new IOException("La mise à jour de Switcher est encore en cours.");
                using(available){Recover(store);using(var child=Launch(SwitcherInstallation.Read(SwitcherInstallation.Root,true),background,null)){};}
                return 0;
            }catch(Exception error){
                try{SafeFiles.AtomicWrite(Path.Combine(SwitcherInstallation.Root,"last-launch-error.txt"),System.Text.Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o")+"\r\n"+error.GetType().Name+" · 0x"+error.HResult.ToString("X8")+"\r\nSwitcher n'a pas pu être lancé. L'installation et les comptes ont été conservés.\r\n"));}catch(IOException){}catch(UnauthorizedAccessException){}
                if(!background)System.Windows.Forms.MessageBox.Show("Switcher n'a pas pu démarrer. L'installation et les comptes sont conservés. Consultez le diagnostic dans le dossier de l'application.","Switcher",System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Error);
                return 1;
            }
        }
        internal static Process Launch(SwitcherInstallationPointer pointer,bool hidden,string transaction)
        {
            if(pointer==null)throw new IOException("Switcher n'est pas encore installé.");
            string arguments=transaction==null?(hidden?"--startup":""):"--update-pending "+transaction+(hidden?" --startup":"");
            var start=new ProcessStartInfo(Path.Combine(pointer.Directory,"CodexAccountSwitcher.exe"),arguments){UseShellExecute=false,WorkingDirectory=pointer.Directory,CreateNoWindow=true,WindowStyle=hidden?ProcessWindowStyle.Hidden:ProcessWindowStyle.Normal};
            start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");
            return Process.Start(start);
        }
        private static bool Alive(int pid,long started){return DesktopRuntime.SameProcess(pid,started);}
        internal static void RestoreAdmissions(RelayStore store,ApplicationUpdateTransaction transaction)
        {
            if(!transaction.Frozen)return;
            using(store.Lease("dispatch")){
                var current=RelayDispatch.Read(store);
                if(current.Mode=="paused"&&current.Accepted.Count==0)store.WriteRecord("dispatch-state.dpapi",transaction.Dispatch);
            }
            var config=Webhooks.Config(store);if(transaction.BrokerEnabled&&!config.Enabled){config.Enabled=true;Webhooks.SaveConfig(store,config);}
            transaction.Frozen=false;store.WriteRecord(ApplicationUpdates.TransactionName,transaction);
        }
        private static FileStream Freeze(RelayStore store,ApplicationUpdateTransaction transaction)
        {
            FileStream relayControl=store.Lease("worker-update");
            try{
            WebhookWorkerState webhook;
            using(store.Lease("application-update-admission")){
            string initialBusy=ApplicationUpdates.BusyReason(store);if(initialBusy!=null)throw new InvalidOperationException(initialBusy);
            transaction.Dispatch=RelayDispatch.Read(store);transaction.BrokerEnabled=Webhooks.Config(store).Enabled;
            // Journal intent before changing admission. Recovery is possible even
            // if Windows stops between the two independently atomic records.
            transaction.Frozen=true;store.WriteRecord(ApplicationUpdates.TransactionName,transaction);
            RelayDispatch.Set(store,"paused");
            webhook=WebhookWorker.Status(store);
            if(transaction.BrokerEnabled){var config=Webhooks.Config(store);config.Enabled=false;Webhooks.SaveConfig(store,config);}
            }
            for(int attempt=0;attempt<150&&Alive(webhook.Pid,webhook.Started);attempt++)Thread.Sleep(100);
            if(Alive(webhook.Pid,webhook.Started))throw new IOException("Le service Switcher Live attend de terminer son opération.");
            string busy=ApplicationUpdates.BusyReason(store);if(busy!=null)throw new InvalidOperationException(busy);
            // Retire the idle old relay while admissions are still paused. Once
            // dispatch resumes only the candidate may create the new worker.
                var relay=RelayWorker.Status(store);
                if(Alive(relay.Pid,relay.Started)){
                    store.WriteRecord("worker-stop.dpapi",new RelayWorkerState{Updated=DateTime.UtcNow.ToString("o"),Paused=relay.Paused});
                    for(int attempt=0;attempt<100&&Alive(relay.Pid,relay.Started);attempt++)Thread.Sleep(100);
                    if(Alive(relay.Pid,relay.Started))throw new IOException("Le relais attend sa fermeture coopérative.");
                }
            return relayControl;
            }catch{relayControl.Dispose();throw;}
        }
        internal static int Apply(RelayStore store,string id)
        {
            var transaction=store.ReadRecord<ApplicationUpdateTransaction>(ApplicationUpdates.TransactionName);DateTime expiry;
            if(transaction.Id!=id||transaction.Phase!="prepared"||!DateTime.TryParse(transaction.ExpiresUtc,out expiry)||DateTime.UtcNow>expiry.ToUniversalTime())throw new InvalidOperationException("Demande de mise à jour expirée.");
            Process candidate=null;bool committed=false;SwitcherInstallationPointer current=null;FileStream relayControl=null;
            try{
                current=SwitcherInstallation.Read(SwitcherInstallation.Root,true);
                if(current==null||transaction.Previous==null||transaction.Next==null||Json.Write(current)!=Json.Write(transaction.Previous))throw new InvalidOperationException("L'installation a changé. Vérifiez à nouveau la mise à jour.");
                if(SwitcherUpdatePackages.CompareVersions(transaction.Next.Version,current.Version)<=0)throw new InvalidOperationException("Une mise à jour doit être plus récente que la version installée.");
                if(!RelayStore.SamePath(transaction.Next.Directory,SwitcherInstallation.VersionDirectory(SwitcherInstallation.Root,transaction.Next.Version))||SwitcherUpdatePackages.ValidateDirectory(transaction.Next.Directory,transaction.Next.Version)!=transaction.Next.ManifestSha256)throw new InvalidOperationException("Paquet de mise à jour invalide.");
                if(Alive(transaction.OwnerPid,transaction.OwnerStarted)){
                    using(var owner=Process.GetProcessById(transaction.OwnerPid))if(!RelayStore.SamePath(owner.MainModule.FileName,transaction.OwnerPath)||!String.Equals(Path.GetFileName(owner.MainModule.FileName),"CodexAccountSwitcher.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Le processus à redémarrer ne correspond plus à Switcher.");
                }
                for(int attempt=0;attempt<150&&Alive(transaction.OwnerPid,transaction.OwnerStarted);attempt++)Thread.Sleep(200);
                if(Alive(transaction.OwnerPid,transaction.OwnerStarted))throw new IOException("Switcher attend la fermeture de sa fenêtre. Vos modifications sont conservées.");
                using(var launcher=Process.GetCurrentProcess()){transaction.LauncherPid=launcher.Id;transaction.LauncherStarted=launcher.StartTime.ToUniversalTime().Ticks;}
                relayControl=Freeze(store,transaction);
                transaction.Phase="starting";store.WriteRecord(ApplicationUpdates.TransactionName,transaction);
                candidate=Launch(transaction.Next,transaction.Hidden,id);transaction.CandidatePid=candidate.Id;transaction.CandidateStarted=candidate.StartTime.ToUniversalTime().Ticks;store.WriteRecord(ApplicationUpdates.TransactionName,transaction);
                bool ready=false;
                for(int attempt=0;attempt<300&&!candidate.HasExited;attempt++){
                    var receipt=store.ReadRecord<ApplicationUpdateReady>(ApplicationUpdates.ReadyName);
                    if(receipt.Id==id&&receipt.Pid==transaction.CandidatePid&&receipt.Started==transaction.CandidateStarted&&receipt.Version==transaction.Next.Version){ready=true;break;}
                    Thread.Sleep(200);
                }
                if(!ready)throw new IOException("La nouvelle interface n'a pas confirmé son démarrage.");
                // Until this write the candidate is restricted to the WPF shell
                // and vault reads. Integration engine pointers remain untouched.
                transaction.Next.Transaction=id;SwitcherInstallation.Write(SwitcherInstallation.Root,transaction.Next);committed=true;
                transaction.Phase="committed";store.WriteRecord(ApplicationUpdates.TransactionName,transaction);
                relayControl.Dispose();relayControl=null;
                RestoreAdmissions(store,transaction);
                var record=store.ReadRecord<ApplicationUpdateRecord>(ApplicationUpdates.RecordName);record.Release=null;record.Staged=null;record.Phase="current";record.Message="Switcher a été mis à jour vers "+transaction.Next.Version+".";store.WriteRecord(ApplicationUpdates.RecordName,record);
                return 0;
            }catch(Exception){
                if(relayControl!=null){relayControl.Dispose();relayControl=null;}
                // A committed candidate may already publish a newer MCP engine.
                // Never pretend that reverting only the GUI also reverts that.
                if(!committed){
                    if(candidate!=null&&!candidate.HasExited&&Alive(transaction.CandidatePid,transaction.CandidateStarted)){
                        if(RelayStore.SamePath(candidate.MainModule.FileName,Path.Combine(transaction.Next.Directory,"CodexAccountSwitcher.exe"))){candidate.Kill();candidate.WaitForExit(5000);}
                    }
                    transaction.Phase="failed";transaction.Error="La nouvelle interface n'a pas démarré. La version précédente a été conservée.";store.WriteRecord(ApplicationUpdates.TransactionName,transaction);
                    RestoreAdmissions(store,transaction);
                    if(current!=null&&!Alive(transaction.OwnerPid,transaction.OwnerStarted)){using(var previous=Launch(current,transaction.Hidden,null)){};}
                    var record=store.ReadRecord<ApplicationUpdateRecord>(ApplicationUpdates.RecordName);record.Phase="error";record.Message=transaction.Error;store.WriteRecord(ApplicationUpdates.RecordName,record);
                }
                else {RestoreAdmissions(store,transaction);}
                return 1;
            }finally{if(candidate!=null)candidate.Dispose();}
        }
        internal static void Recover(RelayStore store)
        {
            var transaction=store.ReadRecord<ApplicationUpdateTransaction>(ApplicationUpdates.TransactionName);
            if(transaction.Id==null||(!transaction.Frozen&&transaction.Phase!="starting"))return;
            var pointer=SwitcherInstallation.Read(SwitcherInstallation.Root,true);
            bool committed=pointer!=null&&pointer.Transaction==transaction.Id;
            if(!committed&&Alive(transaction.CandidatePid,transaction.CandidateStarted))throw new IOException("Le démarrage de la nouvelle version est encore en attente.");
            RestoreAdmissions(store,transaction);transaction.Phase=committed?"committed":"failed";store.WriteRecord(ApplicationUpdates.TransactionName,transaction);
        }
    }
}
