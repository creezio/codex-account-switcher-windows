using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class AccountUsagePolicy
    {
        public string Account {get;set;}
        public string Mode {get;set;}
        public double Threshold {get;set;}
        public string Window {get;set;}
        public bool Notifications {get;set;}
        public AccountUsagePolicy(){Mode="inherit";Threshold=1;Window="all";Notifications=true;}
    }
    public sealed class UsagePolicies
    {
        public bool Background {get;set;}
        public List<AccountUsagePolicy> Accounts {get;set;}
        public UsagePolicies(){Accounts=new List<AccountUsagePolicy>();}
    }
    public sealed class UsageHistory
    {
        public List<UsageEvent> Events {get;set;}
        public UsageHistory(){Events=new List<UsageEvent>();}
    }
    public sealed class UsageEvent
    {
        public string Time {get;set;}
        public string State {get;set;}
        public string Request {get;set;}
        public string Message {get;set;}
        public double? Remaining {get;set;}
    }
    internal static class UsageCoordinator
    {
        public static UsagePolicies Policies(RelayStore store){return store.ReadRecord<UsagePolicies>("usage-policy.dpapi");}
        public static void SavePolicies(RelayStore store,UsagePolicies value)
        {
            if(value.Accounts==null||value.Accounts.Any(p=>String.IsNullOrEmpty(p.Account)||!new[]{"inherit","auto","manual","off"}.Contains(p.Mode)||p.Threshold<0||p.Threshold>25||Double.IsNaN(p.Threshold)||!new[]{"all","session","weekly"}.Contains(p.Window))||value.Accounts.GroupBy(p=>p.Account).Any(g=>g.Count()>1))throw new InvalidOperationException("Politique de limites invalide. Seuil : 0 à 25 %.");
            using(store.Lease("usage-policy"))store.WriteRecord("usage-policy.dpapi",value);
        }
        internal static AccountUsagePolicy For(RelayStore store,string account){return Policies(store).Accounts.FirstOrDefault(p=>p.Account==account)??new AccountUsagePolicy{Account=account};}
        internal static bool Automatic(AccountUsagePolicy p,bool global){return p.Mode=="auto"||(p.Mode=="inherit"&&global);}
        public static bool Running(RelayStore store){var state=store.ReadRecord<RelayWorkerState>("usage-worker.dpapi");return DesktopRuntime.SameProcess(state.Pid,state.Started);}
        public static void Ensure(RelayStore store)
        {
            if(!Policies(store).Background||Running(store))return;
            string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");if(!File.Exists(exe))throw new InvalidOperationException("CreezioRelay.exe absent.");
            var start=new ProcessStartInfo(exe,"--usage-worker "+RelayWorker.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory};
            start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");using(var process=Process.Start(start)){}
        }
        public static void Merge(Profile destination,Profile source)
        {
            if(source==null||source.Key!=destination.Key||String.IsNullOrEmpty(source.QuotaTimeUtc))return;
            destination.Quotas=source.Quotas;destination.QuotaTimeUtc=source.QuotaTimeUtc;destination.Error=source.Error;destination.ResetAttempt=source.ResetAttempt;destination.ResetCredits=source.ResetCredits;destination.ResetMessage=source.ResetMessage;
        }
        public static void Cache(RelayStore store,Profile p)
        {
            var safe=Json.Read<Profile>(Json.Write(p));safe.AuthJson=null;store.WriteRecord(RelayQuota.Name(p.Key),safe);
            if(p.ResetAttempt==null)return;
            string file="reset-history-"+RelayReturns.Key(p.Key)+".dpapi";var history=store.ReadRecord<UsageHistory>(file);var last=history.Events.LastOrDefault();
            if(last==null||last.Request!=p.ResetAttempt.IdempotencyKey||last.State!=p.ResetAttempt.State){
                if(history.Events.Count>=1000){store.WriteRecord("reset-archive-"+RelayReturns.Key(p.Key)+"-"+Guid.NewGuid().ToString("N")+".dpapi",history);history=new UsageHistory();}
                history.Events.Add(new UsageEvent{Time=DateTime.UtcNow.ToString("o"),Request=p.ResetAttempt.IdempotencyKey,State=p.ResetAttempt.State,Message=p.ResetMessage,Remaining=p.Score});store.WriteRecord(file,history);
            }
        }
        internal static bool LegacyGuiRunning()
        {
            foreach(var p in Process.GetProcessesByName("CodexAccountSwitcher"))using(p){try{if(p.MainModule.FileVersionInfo.FileMajorPart==0&&p.MainModule.FileVersionInfo.FileMinorPart<6)return true;}catch{return true;}}
            return false;
        }
        public static async Task Refresh(RelayStore store,string account,bool allowReset,CancellationToken token,bool manual=false)
        {
            FileStream gate;try{gate=store.Lease("usage-"+RelayReturns.Key(account));}catch(IOException){return;}
            using(gate){
                var service=new AccountService(Path.GetDirectoryName(store.Root));var profile=service.Data.Profiles.FirstOrDefault(p=>p.Key==account);if(profile==null)return;
                var cached=store.ReadRecord<Profile>(RelayQuota.Name(account));Merge(profile,cached);var policy=For(store,account);profile.ResetThreshold=policy.Threshold;profile.ResetWindow=policy.Window;
                // Persist reset intent in the shared quota record; never rewrite a stale vault snapshot.
                await service.RefreshUsage(profile,()=>{
                    if(!allowReset||LegacyGuiRunning())return false;
                    var latest=new AccountService(service.Vault.Root);var present=latest.Data.Profiles.FirstOrDefault(p=>p.Key==account);var current=For(store,account);
                    if(present==null||current.Mode=="off")return false;
                    if(manual)return true;
                    return current.Threshold==policy.Threshold&&current.Window==policy.Window&&Automatic(current,latest.Settings.AutoResetCredits)&&latest.Instances.IsResetActive(present);
                },()=>Cache(store,profile),token,manual);
            }
        }
        public static int Run(string root)
        {
            var store=new RelayStore(root);FileStream lease;try{lease=store.Lease("usage-worker");}catch(IOException){return 0;}
            using(lease)using(var process=Process.GetCurrentProcess()){
                var state=new RelayWorkerState{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Version=RelayWorker.Version};
                var attempts=new Dictionary<string,DateTime>();
                try{
                    while(Policies(store).Background){
                        state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("usage-worker.dpapi",state);
                        try{
                            var service=new AccountService(Path.GetDirectoryName(root));var active=service.Instances.ActiveProfiles().Select(p=>p.Key).ToArray();
                            foreach(var p in service.Data.Profiles.Where(p=>active.Contains(p.Key)||service.Settings.AutoRefresh)){
                                if(!Policies(store).Background)break;
                                DateTime last;
                                int seconds=active.Contains(p.Key)?60:300;
                                // A read-only relay refresh must not postpone the independent reset check.
                                if(attempts.TryGetValue(p.Key,out last)&&DateTime.UtcNow-last<TimeSpan.FromSeconds(seconds))continue;
                                attempts[p.Key]=DateTime.UtcNow;
                                Refresh(store,p.Key,active.Contains(p.Key),CancellationToken.None).GetAwaiter().GetResult();
                            }state.Error=null;
                        }catch(Exception e){state.Error=Program.SafeError(e);}
                        Thread.Sleep(2000);
                    }
                }finally{state.Pid=0;state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("usage-worker.dpapi",state);}
                return 0;
            }
        }
    }
}
