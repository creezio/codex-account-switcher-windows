using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Creezio.Switcher;

// Every writable record belongs to this fixture. Startup and installed-pointer
// reads/writes use explicit seams: no real registry, install root or process launch.
internal static class SwitcherLifecycleTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(Action action){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}catch(IOException){rejected=true;}Assert(rejected,"Unsafe installation pointer was accepted");}
    private sealed class Fixture
    {
        internal readonly string Root;internal readonly RelayStore Store;internal readonly int Pid;internal readonly long Started;
        internal Fixture(string sandbox){Root=Path.Combine(sandbox,"l"+Guid.NewGuid().ToString("N").Substring(0,6));Directory.CreateDirectory(Root);Store=new RelayStore(Path.Combine(Root,"relay"));using(var self=Process.GetCurrentProcess()){Pid=self.Id;Started=self.StartTime.ToUniversalTime().Ticks;}}
        internal ApplicationUpdateStatus Read(){return ApplicationUpdates.Read(Store,()=>null,()=>false);}
        internal string EnrollmentName {get{return "portal-enrollment-"+new string('a',24)+"-"+new string('b',32)+".dpapi";}}
        internal string PluginName {get{return "portal-plugin-install-"+new string('c',32)+".dpapi";}}
    }
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("updater lifecycle empty fixture reads preferences without touching the machine install or startup",()=>{
            var f=new Fixture(sandbox);var state=f.Read();Assert(state.InstalledVersion==RelayWorker.Version&&state.Phase=="idle"&&state.AutoUpdate&&!state.StartWithWindows&&!state.StableInstalled&&!state.CanApply&&!state.CanDownload&&state.BusyReason==null,"Fresh isolated state depends on machine installation or fabricated work");
        });
        check("updater lifecycle relay work and pending result delivery defer restart while completed work does not",()=>{
            foreach(string state in new[]{"queued","sending","waiting","uncertain","attention","return","completed"}){
                var f=new Fixture(sandbox);f.Store.Save(new RelayMessage{Id=Guid.NewGuid().ToString("N"),SchemaVersion=4,State=state=="return"?"completed":state,ReturnToSource=state=="return",ReturnState=state=="return"?"pending":"none",CreatedUtc=DateTime.UtcNow.ToString("o")});Assert((ApplicationUpdates.BusyReason(f.Store)!=null)==(state!="completed"),"Restart safety ignored relay state "+state);
            }
        });
        check("updater lifecycle headless activity requires an exact live owner PID and start time",()=>{
            foreach(string owner in new[]{"live","dead","reused","stopped"}){var f=new Fixture(sandbox);f.Store.WriteRecord("headless-runtime-fictional.dpapi",new HeadlessRuntimeState{Running=owner!="stopped",WorkerPid=owner=="dead"?Int32.MaxValue:f.Pid,WorkerStarted=owner=="reused"?f.Started+1:f.Started});Assert((ApplicationUpdates.BusyReason(f.Store)!=null)==(owner=="live"),"Restart blocked stale owner or ignored live headless account");}
        });
        check("updater lifecycle account enrollment blocks only active records whose exact owner is alive",()=>{
            foreach(string state in new[]{"starting","awaiting-auth","awaiting-confirmation","provisioning","ready","cancelled","expired","interrupted","failed"}){var f=new Fixture(sandbox);f.Store.WriteRecord(f.EnrollmentName,new PortalEnrollmentRecord{State=state,OwnerPid=f.Pid,OwnerStarted=f.Started});Assert((ApplicationUpdates.BusyReason(f.Store)!=null)==!new[]{"ready","cancelled","expired","interrupted","failed"}.Contains(state),"Enrollment restart classification changed for "+state);}
            var dead=new Fixture(sandbox);dead.Store.WriteRecord(dead.EnrollmentName,new PortalEnrollmentRecord{State="provisioning",OwnerPid=Int32.MaxValue,OwnerStarted=1});Assert(ApplicationUpdates.BusyReason(dead.Store)==null,"Abandoned enrollment permanently blocked application update");
        });
        check("updater lifecycle plugin installation blocks a live owner and preserves terminal or uncertain receipts",()=>{
            foreach(string state in new[]{"installing","ready","needs-auth","uncertain","failed","interrupted"}){var f=new Fixture(sandbox);f.Store.WriteRecord(f.PluginName,new PortalPluginOperation{State=state,OwnerPid=f.Pid,OwnerStarted=f.Started});Assert((ApplicationUpdates.BusyReason(f.Store)!=null)==(state=="installing"),"Plugin restart classification changed for "+state);Assert(f.Store.ReadRecord<PortalPluginOperation>(f.PluginName).State==state,"Activity observation rewrote an installation receipt");}
            var dead=new Fixture(sandbox);dead.Store.WriteRecord(dead.PluginName,new PortalPluginOperation{State="installing",OwnerPid=Int32.MaxValue,OwnerStarted=1});Assert(ApplicationUpdates.BusyReason(dead.Store)==null,"An absent plugin worker permanently blocked restart");
        });
        check("updater lifecycle damaged activity records fail closed without deleting evidence",()=>{
            var f=new Fixture(sandbox);string path=Path.Combine(f.Store.Root,f.PluginName);byte[] original={1,2,3,4};File.WriteAllBytes(path,original);Assert(ApplicationUpdates.BusyReason(f.Store)!=null&&File.ReadAllBytes(path).SequenceEqual(original),"Damaged activity metadata allowed a restart or was removed");
        });
        check("updater lifecycle stale downloads are reported interrupted without restarting or replaying them",()=>{
            foreach(string owner in new[]{"live","dead","reused"}){var f=new Fixture(sandbox);f.Store.WriteRecord(ApplicationUpdates.RecordName,new ApplicationUpdateRecord{Phase="downloading",Message="Fictional ongoing download",OperationPid=owner=="dead"?Int32.MaxValue:f.Pid,OperationStarted=owner=="reused"?f.Started+1:f.Started});byte[] before=File.ReadAllBytes(Path.Combine(f.Store.Root,ApplicationUpdates.RecordName));var state=f.Read();Assert(state.Phase==(owner=="live"?"downloading":"error")&&!state.CanApply&&!state.CanDownload,"Stale download was resumed or live owner was interrupted");Assert(File.ReadAllBytes(Path.Combine(f.Store.Root,ApplicationUpdates.RecordName)).SequenceEqual(before),"Read changed the durable download receipt");}
        });
        check("updater lifecycle startup preference uses its injected writer and preserves preferences on writer failure",()=>{
            var f=new Fixture(sandbox);int writes=0;bool requested=false;ApplicationUpdates.SavePreferences(f.Store,false,true,value=>{writes++;requested=value;});Assert(writes==1&&requested&&!f.Store.ReadRecord<ApplicationUpdatePreferences>(ApplicationUpdates.PreferencesName).Automatic,"Startup seam or automatic preference was ignored");byte[] before=File.ReadAllBytes(Path.Combine(f.Store.Root,ApplicationUpdates.PreferencesName));bool failed=false;try{ApplicationUpdates.SavePreferences(f.Store,true,false,value=>{throw new IOException("Fictional registry failure");});}catch(IOException){failed=true;}Assert(failed&&File.ReadAllBytes(Path.Combine(f.Store.Root,ApplicationUpdates.PreferencesName)).SequenceEqual(before),"Failed startup registration falsely saved a new preference");
        });
        check("updater lifecycle installation pointer is confined and does not activate an unverified payload",()=>{
            var f=new Fixture(sandbox);string root=Path.Combine(f.Root,"install");Directory.CreateDirectory(root);var pointer=new SwitcherInstallationPointer{SchemaVersion=1,Version=RelayWorker.Version,Directory=SwitcherInstallation.VersionDirectory(root,RelayWorker.Version),ManifestSha256=new string('a',64)};SwitcherInstallation.Write(root,pointer);byte[] before=File.ReadAllBytes(Path.Combine(root,"current.json"));Assert(Json.Write(SwitcherInstallation.Read(root,false))==Json.Write(pointer),"Confined pointer did not round trip");Reject(()=>SwitcherInstallation.Read(root,true));Assert(File.ReadAllBytes(Path.Combine(root,"current.json")).SequenceEqual(before),"Failed payload validation modified current pointer");foreach(string error in new[]{"schema","outside","version"}){var invalid=new SwitcherInstallationPointer{SchemaVersion=error=="schema"?2:1,Version=RelayWorker.Version,Directory=error=="outside"?f.Root:error=="version"?SwitcherInstallation.VersionDirectory(root,"1.2.3"):pointer.Directory,ManifestSha256=pointer.ManifestSha256};Reject(()=>SwitcherInstallation.Write(root,invalid));Assert(File.ReadAllBytes(Path.Combine(root,"current.json")).SequenceEqual(before),"Rejected pointer changed the active version");}
        });
        check("updater lifecycle version paths reject traversal rooted and noncanonical versions",()=>{
            var f=new Fixture(sandbox);foreach(string version in new[]{"../outside","C:/outside","v1.2.3","1.2","1.2.3/next","01.2.3","1.2.3-beta.01",""})Reject(()=>SwitcherInstallation.VersionDirectory(f.Root,version));
        });
        check("updater lifecycle restoration replays only admission state and is idempotent in a local fixture",()=>{
            var f=new Fixture(sandbox);var dispatch=new DispatchState{Mode="drain",Accepted=new List<string>{new string('d',32)}};f.Store.WriteRecord("dispatch-state.dpapi",new DispatchState{Mode="paused"});Webhooks.SaveConfig(f.Store,new WebhookConfig{Enabled=false,Port=18888});var transaction=new ApplicationUpdateTransaction{Id=Guid.NewGuid().ToString("N"),Phase="starting",Frozen=true,Dispatch=dispatch,BrokerEnabled=true};SwitcherLauncher.RestoreAdmissions(f.Store,transaction);Assert(Json.Write(RelayDispatch.Read(f.Store))==Json.Write(dispatch)&&Webhooks.Config(f.Store).Enabled&&!transaction.Frozen,"Recovery failed to restore recorded admission exactly");byte[] restored=File.ReadAllBytes(Path.Combine(f.Store.Root,"dispatch-state.dpapi"));SwitcherLauncher.RestoreAdmissions(f.Store,transaction);Assert(File.ReadAllBytes(Path.Combine(f.Store.Root,"dispatch-state.dpapi")).SequenceEqual(restored),"Repeated recovery changed completed restoration");
        });
        check("updater lifecycle restoration preserves a newer human dispatch decision",()=>{
            var f=new Fixture(sandbox);var current=new DispatchState{Mode="running",Accepted=new List<string>{new string('e',32)}};f.Store.WriteRecord("dispatch-state.dpapi",current);Webhooks.SaveConfig(f.Store,new WebhookConfig{Enabled=false,Port=18888});var transaction=new ApplicationUpdateTransaction{Id=Guid.NewGuid().ToString("N"),Frozen=true,Dispatch=new DispatchState{Mode="drain"},BrokerEnabled=false};SwitcherLauncher.RestoreAdmissions(f.Store,transaction);Assert(Json.Write(RelayDispatch.Read(f.Store))==Json.Write(current)&&!Webhooks.Config(f.Store).Enabled,"Recovery overwrote a later operator admission decision");
        });
        check("updater lifecycle portal mutation admission is exclusive while read only status stays available",()=>{
            var f=new Fixture(sandbox);using(ApplicationUpdates.AdmitPortalMutation(f.Store)){Reject(()=>{using(ApplicationUpdates.AdmitPortalMutation(f.Store)){};});Assert(f.Read().Phase=="idle","A mutation gate blocked read only status");}using(ApplicationUpdates.AdmitPortalMutation(f.Store)){};
        });
        check("updater lifecycle frozen portal mutation returns retryable refusal and releases the lease after failure",()=>{
            var f=new Fixture(sandbox);f.Store.WriteRecord(ApplicationUpdates.TransactionName,new ApplicationUpdateTransaction{Id=Guid.NewGuid().ToString("N"),Frozen=true,Phase="starting"});bool refused=false;try{using(ApplicationUpdates.AdmitPortalMutation(f.Store)){};}catch(WebhookException error){refused=error.StatusCode==503;}Assert(refused,"Frozen update admitted a new native mutation");f.Store.WriteRecord(ApplicationUpdates.TransactionName,new ApplicationUpdateTransaction{Frozen=false,Phase="failed"});using(ApplicationUpdates.AdmitPortalMutation(f.Store)){};
        });
    }
}
