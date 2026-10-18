using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CodexCompatibilityTests
{
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private sealed class Fixture
    {
        internal readonly RelayStore Store;
        internal CodexCompatibility Checks;
        internal CodexCompatibilityInventory Inventory;
        internal DateTime Now=new DateTime(2026,10,17,12,0,0,DateTimeKind.Utc);
        internal int Detections,Probes,VersionReads;
        internal string ProbeState="verified";
        internal Exception ProbeError,InventoryError,VersionError;
        internal Action BeforeProbe;
        internal Fixture(string root,string name)
        {
            Store=new RelayStore(Path.Combine(root,"codex-compatibility",name,"relay"));
            Inventory=new CodexCompatibilityInventory{Fingerprint="installed-v1-running-v1",InstalledVersion="26.1013.1.0",InstalledPath="C:\\fixture\\Codex.exe",CliPath="C:\\fixture\\codex.exe"};
            Inventory.Targets.Add(new CodexCompatibilityTarget{Id=new String('a',32),Name="Fixture",Version="26.1013.1.0",Running=true,Pid=12345,Started=123456789,Accounts=null,Instance=null});
            Checks=Create();
        }
        internal CodexCompatibility Create()
        {
            return new CodexCompatibility(Store,token=>{Detections++;if(InventoryError!=null)throw InventoryError;return Task.FromResult(Inventory);},
                (target,token)=>{Probes++;if(BeforeProbe!=null)BeforeProbe();if(ProbeError!=null)throw ProbeError;return Task.FromResult(new CodexCompatibilityCheck{State=ProbeState,Detail="Fixture read-only check"});},
                (path,token)=>{VersionReads++;if(VersionError!=null)throw VersionError;return Task.FromResult("codex-cli fixture");},()=>Now);
        }
        internal CodexCompatibilityReport Refresh(bool force=false){return Checks.Refresh(CancellationToken.None,force).GetAwaiter().GetResult();}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("Codex compatibility unchanged installation does not repeat native RPC checks",()=>{
            var f=new Fixture(root,"unchanged");var original=f.Refresh();f.Now=f.Now.AddMinutes(1);var next=f.Refresh();
            Assert(original.State=="verified"&&next.Fingerprint==original.Fingerprint&&f.Probes==1&&f.VersionReads==1&&f.Detections==2,"unchanged healthy version repeated RPC/CLI probing or lost its verified report");
        });
        check("Codex compatibility rechecks changed installed and running versions",()=>{
            var f=new Fixture(root,"versions");f.Refresh();f.Inventory.InstalledVersion="26.1013.2.0";f.Inventory.Fingerprint="installed-v2-running-v1";var installed=f.Refresh();
            Assert(installed.InstalledVersion=="26.1013.2.0"&&f.Probes==2,"installed-version change was not checked");
            f.Inventory.Targets[0].Version="26.1013.2.0";f.Inventory.Fingerprint="installed-v2-running-v2";var running=f.Refresh();
            Assert(running.Checks.Single().RunningVersion=="26.1013.2.0"&&f.Probes==3,"running-version change was not checked");
        });
        check("Codex compatibility closed instances are not opened for a check",()=>{
            var f=new Fixture(root,"closed");f.Inventory.Targets[0].Running=false;var state=f.Refresh();
            Assert(f.Probes==0&&state.State=="not-tested"&&state.Checks.Single().State=="not-tested","a closed instance was probed or declared verified");
            Assert(f.Store.Messages().Count==0,"compatibility control created a mission");
        });
        check("Codex compatibility missing native commands are incompatible and namespaced",()=>{
            string[] names={"read_thread","list_threads","list_projects","create_thread","send_message_to_thread","navigate_to_codex_page"};
            Assert(CodexCompatibility.MissingCommands(Json.Read<object>(Json.Write(new{tools=names.Select(n=>new{ @namespace="codex_app",name=n}).ToArray()}))).Length==0,"complete catalog rejected");
            var missing=CodexCompatibility.MissingCommands(Json.Read<object>(Json.Write(new{tools=names.Where(n=>n!="send_message_to_thread").Select(n=>new{ @namespace="codex_app",name=n}).Concat(new[]{new{ @namespace="other_app",name="send_message_to_thread"}}).ToArray()})));
            Assert(missing.SequenceEqual(new[]{"send_message_to_thread"}),"foreign namespace satisfied a missing Codex command");
            var f=new Fixture(root,"missing-commands");f.ProbeState="incompatible";var state=f.Refresh();
            Assert(state.NeedsAttention&&state.State=="attention","missing command report was not actionable");
        });
        check("Codex compatibility native read failure never reports a healthy version",()=>{
            var f=new Fixture(root,"read-failure");f.ProbeError=new IOException("PRIVATE_COMPATIBILITY_ERROR");var state=f.Refresh();
            Assert(state.NeedsAttention&&state.State=="attention"&&state.Checks.Single().State=="unavailable","unreadable native chat was declared compatible");
            Assert(!Json.Write(state).Contains("PRIVATE_COMPATIBILITY_ERROR"),"private error leaked into the compatibility notification");
        });
        check("Codex compatibility bounds a native probe that ignores cancellation",()=>{
            var f=new Fixture(root,"probe-timeout");var pending=new TaskCompletionSource<CodexCompatibilityCheck>();
            f.Checks=new CodexCompatibility(f.Store,token=>Task.FromResult(f.Inventory),(target,token)=>pending.Task,(path,token)=>Task.FromResult("fixture CLI"),()=>f.Now);
            f.Checks.ProbeTimeout=TimeSpan.FromMilliseconds(15);
            try{
                var read=f.Checks.Refresh(CancellationToken.None);
                Assert(read.Wait(TimeSpan.FromSeconds(1)),"a native probe ignoring cancellation blocked compatibility supervision");
                var state=read.GetAwaiter().GetResult();Assert(state.NeedsAttention&&state.State=="attention"&&state.Checks.Single().State=="unavailable","timed-out native control was declared compatible");
            }finally{pending.TrySetResult(new CodexCompatibilityCheck{State="verified",Detail="Late fixture response"});}
        });
        check("Codex compatibility retries after five minutes and deduplicates persisted alerts",()=>{
            var f=new Fixture(root,"retry-dedup");f.ProbeState="incompatible";var initial=f.Refresh();f.Checks.MarkNotified(initial.EpisodeId);
            f.Checks=f.Create();f.Now=f.Now.AddMinutes(4);f.Refresh();Assert(f.Probes==1,"retry ignored the five minute backoff");
            f.Now=f.Now.AddMinutes(1);var repeated=f.Refresh();
            Assert(f.Probes==2&&repeated.EpisodeId==initial.EpisodeId&&!String.IsNullOrEmpty(repeated.NotifiedUtc),"same failed version created a duplicate alert after restart/retry");
        });
        check("Codex compatibility recovery and a later new failure form separate episodes",()=>{
            var f=new Fixture(root,"recovery");f.ProbeState="unavailable";var failed=f.Refresh();f.Checks.MarkNotified(failed.EpisodeId);
            f.ProbeState="verified";f.Now=f.Now.AddMinutes(5);var recovered=f.Refresh();Assert(!recovered.NeedsAttention&&recovered.State=="verified","successful retry did not clear the failure");
            f.ProbeState="incompatible";f.Inventory.Fingerprint="new-incompatible-version";var later=f.Refresh();
            Assert(later.NeedsAttention&&later.EpisodeId!=failed.EpisodeId&&String.IsNullOrEmpty(later.NotifiedUtc),"new incompatible version reused an old delivered notification");
        });
        check("Codex compatibility detection and CLI failures remain visible",()=>{
            var f=new Fixture(root,"detection-failure");f.Refresh();f.InventoryError=new IOException("PRIVATE_INVENTORY_ERROR");var missing=f.Refresh();
            Assert(missing.NeedsAttention&&missing.State=="attention"&&missing.Fingerprint==null,"failed detection retained a healthy compatibility claim");
            f.InventoryError=null;Assert(f.Refresh().State=="verified"&&f.Probes==2,"inventory recovery was not rechecked");
            var cli=new Fixture(root,"cli-failure");cli.VersionError=new IOException("PRIVATE_CLI_ERROR");var state=cli.Refresh();
            Assert(state.NeedsAttention&&state.Checks.Any(c=>c.Name=="Codex CLI"&&c.State=="unavailable"),"missing CLI control was hidden");
        });
        check("Codex compatibility notification delivery during a retry is not overwritten",()=>{
            var f=new Fixture(root,"notify-race");f.ProbeState="incompatible";var first=f.Refresh();f.Now=f.Now.AddMinutes(5);
            f.BeforeProbe=()=>f.Checks.MarkNotified(first.EpisodeId);f.Refresh();
            Assert(!String.IsNullOrEmpty(f.Checks.Read().NotifiedUtc),"an in-flight compatibility read lost notification delivery and will spam it again");
        });
        check("Codex compatibility parses installed package versions without launching processes",()=>{
            Assert(CodexCompatibility.PackageVersion(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__fixture\app\ChatGPT.exe")=="26.1013.4894.0","package version not parsed");
            Assert(CodexCompatibility.PackageVersion(@"C:\custom\app\ChatGPT.exe")=="Installation personnalisée","custom package version invented");
        });
    }
}
