using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class ApplicationUpdateStatus
    {
        public string InstalledVersion {get;set;} public string AvailableVersion {get;set;}
        public string Phase {get;set;} public string Message {get;set;} public string BusyReason {get;set;}
        public string StableLauncher {get;set;}
        public bool StableInstalled {get;set;} public bool AutoUpdate {get;set;} public bool StartWithWindows {get;set;}
        public bool CanDownload {get;set;} public bool CanApply {get;set;}
    }
    public sealed class ApplicationUpdatePreferences
    {public bool Automatic {get;set;} public ApplicationUpdatePreferences(){Automatic=true;}}
    internal sealed class ApplicationUpdateRecord
    {
        public SwitcherUpdateRelease Release {get;set;} public string Staged {get;set;}
        public string Phase {get;set;} public string Message {get;set;} public string CheckedUtc {get;set;}
        public int OperationPid {get;set;}public long OperationStarted {get;set;}
    }
    public sealed class ApplicationUpdateTransaction
    {
        public string Id {get;set;} public string ExpiresUtc {get;set;} public string Phase {get;set;}
        public SwitcherInstallationPointer Previous {get;set;} public SwitcherInstallationPointer Next {get;set;}
        public int OwnerPid {get;set;} public long OwnerStarted {get;set;} public string OwnerPath {get;set;}
        public int CandidatePid {get;set;} public long CandidateStarted {get;set;} public bool Hidden {get;set;}
        public int LauncherPid {get;set;}public long LauncherStarted {get;set;}
        public DispatchState Dispatch {get;set;} public bool BrokerEnabled {get;set;} public bool Frozen {get;set;}
        public string Error {get;set;}
    }
    public sealed class ApplicationUpdateReady
    {public string Id {get;set;}public int Pid {get;set;}public long Started {get;set;}public string Version {get;set;}}
    internal static class ApplicationUpdates
    {
        internal const string RecordName="application-update-state-v1.dpapi",PreferencesName="application-update-preferences-v1.dpapi",TransactionName="application-update-transaction-v1.dpapi",ReadyName="application-update-ready-v1.dpapi";
        private static readonly SemaphoreSlim operation=new SemaphoreSlim(1,1);
        internal static ApplicationUpdateStatus Read(RelayStore store)
        {return Read(store,()=>SwitcherInstallation.Read(SwitcherInstallation.Root,false),SwitcherInstallation.StartsWithWindows);}
        internal static ApplicationUpdateStatus Read(RelayStore store,Func<SwitcherInstallationPointer> pointerRead,Func<bool> startupRead)
        {
            var record=store.ReadRecord<ApplicationUpdateRecord>(RecordName);var prefs=store.ReadRecord<ApplicationUpdatePreferences>(PreferencesName);
            if(record.Phase=="downloading"&&!DesktopRuntime.SameProcess(record.OperationPid,record.OperationStarted)){record.Phase="error";record.Message="Le téléchargement a été interrompu. Vous pouvez le reprendre.";}
            var pointer=pointerRead();string busy=BusyReason(store);
            bool staged=!String.IsNullOrEmpty(record.Staged)&&Directory.Exists(record.Staged)&&record.Release!=null;
            return new ApplicationUpdateStatus{InstalledVersion=RelayWorker.Version,AvailableVersion=record.Release==null?null:record.Release.Version,
                Phase=record.Phase??"idle",Message=record.Message??"Les mises à jour sont vérifiées automatiquement.",BusyReason=busy,
                StableInstalled=pointer!=null&&File.Exists(SwitcherInstallation.Launcher),StableLauncher=SwitcherInstallation.Launcher,
                AutoUpdate=prefs.Automatic,StartWithWindows=startupRead(),
                CanDownload=record.Release!=null&&!staged,CanApply=staged&&busy==null&&pointer!=null};
        }
        internal static string BusyReason(RelayStore store)
        {
            try{
                if(store.ActiveMessages().Any(m=>RelayRouter.Executing(m)||m.State=="queued"||RelayEngine.PendingReturn(m)))return "Une mission de relais travaille ou attend son départ.";
                if(Webhooks.Jobs(store).Any(Webhooks.IsPending))return "Une demande Switcher Live est en cours.";
                foreach(string file in Directory.GetFiles(store.Root,"headless-runtime-*.dpapi")){
                    var runtime=store.ReadRecord<HeadlessRuntimeState>(Path.GetFileName(file));
                    if(runtime.Running&&DesktopRuntime.SameProcess(runtime.WorkerPid,runtime.WorkerStarted))return "Un compte travaille en arrière-plan.";
                }
                foreach(string file in Directory.GetFiles(store.Root,"portal-enrollment-*.dpapi")){
                    if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file),"^portal-enrollment-[a-f0-9]{24}-[a-f0-9]{32}\\.dpapi$"))continue;
                    var enrollment=store.ReadRecord<PortalEnrollmentRecord>(Path.GetFileName(file));
                    if(!new[]{"ready","cancelled","expired","interrupted","failed"}.Contains(enrollment.State)&&DesktopRuntime.SameProcess(enrollment.OwnerPid,enrollment.OwnerStarted))return "Une connexion de compte est en cours.";
                }
                foreach(string file in Directory.GetFiles(store.Root,"portal-plugin-install-*.dpapi")){
                    var plugin=store.ReadRecord<PortalPluginOperation>(Path.GetFileName(file));
                    if(plugin.State=="installing"&&DesktopRuntime.SameProcess(plugin.OwnerPid,plugin.OwnerStarted))return "Un plugin est en cours d'installation.";
                }
                foreach(string file in Directory.GetFiles(store.Root,"conversation-transfer-op-*.dpapi")){
                    var operation=store.ReadRecord<ConversationTransferOperation>(Path.GetFileName(file));
                    if((operation.State=="queued"||operation.State=="running")&&DesktopRuntime.SameProcess(operation.OwnerPid,operation.OwnerStarted))return "Une copie ou un transfert de conversation est en cours.";
                }
                return null;
            }catch(Exception e){if(e is IOException||e is UnauthorizedAccessException||e is System.ComponentModel.Win32Exception||e is InvalidOperationException||e is System.Security.Cryptography.CryptographicException)return "L'activité doit être vérifiée avant de redémarrer Switcher.";throw;}
        }
        internal static IDisposable AdmitPortalMutation(RelayStore store)
        {
            var lease=store.Lease("application-update-admission");
            try{
                if(store.ReadRecord<ApplicationUpdateTransaction>(TransactionName).Frozen)throw new WebhookException(503,"Switcher installe une mise à jour. Réessayez dans quelques instants.");
                return lease;
            }catch{lease.Dispose();throw;}
        }
        internal static void SavePreferences(RelayStore store,bool automatic,bool startWithWindows)
        {SavePreferences(store,automatic,startWithWindows,SwitcherInstallation.SetStartup);}
        internal static void SavePreferences(RelayStore store,bool automatic,bool startWithWindows,Action<bool> startupWrite)
        {
            using(store.Lease("application-update-preferences")){
                startupWrite(startWithWindows);
                store.WriteRecord(PreferencesName,new ApplicationUpdatePreferences{Automatic=automatic});
            }
        }
        internal static async Task<ApplicationUpdateStatus> Check(RelayStore store,CancellationToken token)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            try{
                var release=await SwitcherUpdatePackages.Find(RelayWorker.Version,token).ConfigureAwait(false);
                var record=store.ReadRecord<ApplicationUpdateRecord>(RecordName);
                if(release==null||record.Release==null||record.Release.Version!=release.Version||record.Release.ArchiveSha256!=release.ArchiveSha256||record.Release.ArchiveBytes!=release.ArchiveBytes||record.Release.ArchiveUrl!=release.ArchiveUrl)record.Staged=null;
                record.Release=release;record.CheckedUtc=DateTime.UtcNow.ToString("o");record.Phase=release==null?"current":record.Staged==null?"available":"ready";
                record.Message=release==null?"Aucune version officielle plus récente. Votre version est conservée.":record.Staged==null?"Une mise à jour est disponible.":"La mise à jour est prête à être installée.";
                store.WriteRecord(RecordName,record);return Read(store);
            }catch(Exception e){if(e is OperationCanceledException)throw;Failure(store,"La vérification a échoué. Réessayez plus tard.");throw;}
            finally{operation.Release();}
        }
        internal static async Task<ApplicationUpdateStatus> Download(RelayStore store,CancellationToken token)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            try{
                var record=store.ReadRecord<ApplicationUpdateRecord>(RecordName);if(record.Release==null)throw new InvalidOperationException("Vérifiez d'abord les mises à jour.");
                record.Phase="downloading";record.Message="Téléchargement et vérification de la mise à jour…";
                using(var self=Process.GetCurrentProcess()){record.OperationPid=self.Id;record.OperationStarted=self.StartTime.ToUniversalTime().Ticks;}
                store.WriteRecord(RecordName,record);
                var pointer=SwitcherInstallation.Read(SwitcherInstallation.Root,false);
                record.Staged=await SwitcherUpdatePackages.Stage(record.Release,SwitcherInstallation.Root,pointer==null?AppDomain.CurrentDomain.BaseDirectory:pointer.Directory,token).ConfigureAwait(false);
                record.Phase="ready";record.Message="La mise à jour est prête. Elle sera installée quand Switcher sera disponible.";store.WriteRecord(RecordName,record);return Read(store);
            }catch(Exception){Failure(store,"Le téléchargement n'a pas abouti. La version actuelle reste disponible.");throw;}
            finally{operation.Release();}
        }
        private static void Failure(RelayStore store,string message)
        {var record=store.ReadRecord<ApplicationUpdateRecord>(RecordName);record.Phase="error";record.Message=message;store.WriteRecord(RecordName,record);}
        internal static Task<ApplicationUpdateStatus> InstallCurrent(RelayStore store,CancellationToken token)
        {
            return Task.Run(()=>{
                token.ThrowIfCancellationRequested();using(store.Lease("application-update-install")){
                    var current=SwitcherInstallation.Read(SwitcherInstallation.Root,false);
                    if(current!=null&&current.Version!=RelayWorker.Version)throw new InvalidOperationException("L'entrée permanente utilise déjà une autre version. Ouvrez Switcher depuis son raccourci et utilisez sa mise à jour intégrée.");
                    SwitcherInstallation.Adopt(SwitcherInstallation.Root,AppDomain.CurrentDomain.BaseDirectory,RelayWorker.Version);
                    SwitcherInstallation.CreateShortcut();return Read(store);
                }
            },token);
        }
        internal static Task<bool> Apply(RelayStore store,CancellationToken token){return Apply(store,token,false);}
        internal static async Task<bool> Apply(RelayStore store,CancellationToken token,bool hidden)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            try{
                using(store.Lease("application-update-install")){
                    string busy=BusyReason(store);if(busy!=null)throw new InvalidOperationException(busy);
                    var record=store.ReadRecord<ApplicationUpdateRecord>(RecordName);if(record.Release==null||String.IsNullOrEmpty(record.Staged))return false;
                    var previous=SwitcherInstallation.Read(SwitcherInstallation.Root,true);if(previous==null)throw new InvalidOperationException("Installez d'abord l'entrée stable de Switcher.");
                    if(SwitcherUpdatePackages.CompareVersions(record.Release.Version,previous.Version)<=0||SwitcherUpdatePackages.CompareVersions(record.Release.Version,RelayWorker.Version)<=0)throw new InvalidOperationException("Cette mise à jour n'est pas plus récente que la version installée.");
                    // Stage's idempotent path also checks the archive-bound proof.
                    // A replaced GitHub asset under the same tag cannot reuse an
                    // older staged directory merely because its version matches.
                    string verifiedStage=await SwitcherUpdatePackages.Stage(record.Release,SwitcherInstallation.Root,previous.Directory,token).ConfigureAwait(false);
                    if(!RelayStore.SamePath(verifiedStage,record.Staged))throw new InvalidOperationException("La préparation ne correspond plus à la livraison sélectionnée.");
                    if(!RelayStore.SamePath(record.Staged,SwitcherInstallation.VersionDirectory(SwitcherInstallation.Root,record.Release.Version)))throw new InvalidOperationException("Destination de mise à jour invalide.");
                    var next=new SwitcherInstallationPointer{SchemaVersion=1,Directory=record.Staged,Version=record.Release.Version,ManifestSha256=SwitcherUpdatePackages.ValidateDirectory(record.Staged,record.Release.Version)};
                    ApplicationUpdateTransaction transaction;
                    using(var self=Process.GetCurrentProcess())transaction=new ApplicationUpdateTransaction{Id=Guid.NewGuid().ToString("N"),ExpiresUtc=DateTime.UtcNow.AddMinutes(3).ToString("o"),Phase="prepared",Previous=previous,Next=next,OwnerPid=self.Id,OwnerStarted=self.StartTime.ToUniversalTime().Ticks,OwnerPath=self.MainModule.FileName,Hidden=hidden};
                    store.WriteRecord(TransactionName,transaction);
                    using(var child=Process.Start(new ProcessStartInfo(SwitcherInstallation.Launcher,"--apply "+transaction.Id){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=SwitcherInstallation.Root})){}
                    return true;
                }
            }finally{operation.Release();}
        }
        internal static async Task Automatic(RelayStore store,CancellationToken token)
        {
            if(!store.ReadRecord<ApplicationUpdatePreferences>(PreferencesName).Automatic)return;
            var record=store.ReadRecord<ApplicationUpdateRecord>(RecordName);DateTime checkedAt;
            if(!DateTime.TryParse(record.CheckedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out checkedAt)||DateTime.UtcNow-checkedAt.ToUniversalTime()>TimeSpan.FromHours(6))await Check(store,token).ConfigureAwait(false);
            var status=Read(store);if(status.AutoUpdate&&status.CanDownload)await Download(store,token).ConfigureAwait(false);
        }
        // Called by the candidate WPF shell before Initialize or any integration
        // maintenance. No process pointer is published until this acknowledgement.
        internal static async Task<bool> ReadyToCommit(RelayStore store,string id,CancellationToken token)
        {
            var transaction=store.ReadRecord<ApplicationUpdateTransaction>(TransactionName);
            DateTime expires;
            if(transaction.Id!=id||transaction.Phase!="starting"||transaction.Next==null||transaction.Next.Version!=RelayWorker.Version||!RelayStore.SamePath(transaction.Next.Directory,AppDomain.CurrentDomain.BaseDirectory)||!DateTime.TryParse(transaction.ExpiresUtc,out expires)||DateTime.UtcNow>expires.ToUniversalTime())return false;
            using(var self=Process.GetCurrentProcess())store.WriteRecord(ReadyName,new ApplicationUpdateReady{Id=id,Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Version=RelayWorker.Version});
            for(int attempt=0;attempt<300;attempt++){
                token.ThrowIfCancellationRequested();var pointer=SwitcherInstallation.Read(SwitcherInstallation.Root,false);
                if(pointer!=null&&pointer.Transaction==id&&pointer.Version==RelayWorker.Version){
                    // The launcher may have stopped immediately after the atomic
                    // commit. The acknowledged candidate can finish restoration.
                    var committed=store.ReadRecord<ApplicationUpdateTransaction>(TransactionName);
                    if(committed.Id==id&&committed.Frozen){
                        if(DesktopRuntime.SameProcess(committed.LauncherPid,committed.LauncherStarted)){await Task.Delay(200,token).ConfigureAwait(false);continue;}
                        SwitcherLauncher.RestoreAdmissions(store,committed);
                    }
                    return true;
                }
                var latest=store.ReadRecord<ApplicationUpdateTransaction>(TransactionName);if(latest.Id!=id||latest.Phase=="failed")return false;
                await Task.Delay(200,token).ConfigureAwait(false);
            }
            return false;
        }
    }
}
