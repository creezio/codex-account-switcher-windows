using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class ResetTests
{
    private static readonly DateTime Epoch=new DateTime(2030,1,1,0,0,0,DateTimeKind.Utc);
    private static long Unix(DateTime date) {return (long)(date-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;}
    private static void Assert(bool test,string message) {if(!test) throw new Exception(message);}
    private static void Throws(Action action) {try{action();}catch{return;}throw new Exception("Expected failure");}
    private static UsageSnapshot Snapshot(double? remaining,int? credits)
    {
        return new UsageSnapshot {
            Buckets=new List<QuotaBucket>{new QuotaBucket{Name="codex",Primary=new QuotaWindow{Remaining=remaining,Minutes=300,ResetsAt=Unix(Epoch.AddHours(5))},Secondary=new QuotaWindow{Remaining=50,Minutes=10080,ResetsAt=Unix(Epoch.AddDays(3))}}},
            ResetCredits=new ResetCredits{AvailableCount=credits,Credits=credits>0?new List<ResetCredit>{Credit("later",Epoch.AddDays(3)),Credit("first",Epoch.AddDays(1))}:new List<ResetCredit>()}
        };
    }
    private static ResetCredit Credit(string id,DateTime expires) {return new ResetCredit{Id=id,Status="available",ResetType="codexRateLimits",ExpiresAt=Unix(expires)};}
    private static Profile Profile(double? remaining=1,int? count=2) {var p=new Profile{Key="test-account",Label="Test"};Snapshot(remaining,count).Apply(p,Epoch);return p;}
    private static void Run(ResetController controller,Profile profile,Fake gateway,Action save=null,Func<bool> active=null)
    {controller.Run(profile,active??(()=>true),gateway,save??delegate{},CancellationToken.None).GetAwaiter().GetResult();}
    private sealed class Fake : IResetGateway
    {
        public readonly List<string> Keys=new List<string>(), Credits=new List<string>();
        public string Outcome="reset";
        public bool FailConsume,FailRead;
        public UsageSnapshot After=Snapshot(100,1);
        public int Reads;
        public Action OnConsume;
        public TaskCompletionSource<string> Delayed;
        public Task<string> Consume(string key,string credit,CancellationToken token)
        {
            Keys.Add(key);Credits.Add(credit);if(OnConsume!=null) OnConsume();
            if(FailConsume) throw new IOException("network lost");
            return Delayed==null?Task.FromResult(Outcome):Delayed.Task;
        }
        public Task<UsageSnapshot> Read(CancellationToken token)
        {Reads++;if(FailRead) throw new IOException();return Task.FromResult(After);}
    }
    public static void RunAll(Action<string,Action> check)
    {
        check("reset metadata distinct from purchased balance / absent is unknown",delegate {
            var s=UsageSnapshot.Parse(Json.Read<object>("{\"rateLimits\":{\"credits\":{\"balance\":999}}}"));Assert(s.ResetCredits==null,"purchased balance treated as a reset");
            s=UsageSnapshot.Parse(Json.Read<object>("{\"rateLimitResetCredits\":{\"availableCount\":null,\"credits\":null}}"));Assert(!s.ResetCredits.CanConsume(Epoch),"unknown count spend");
        });
        check("reset count-only and partial details parsed",delegate {
            var s=UsageSnapshot.Parse(Json.Read<object>("{\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":null}}"));Assert(s.ResetCredits.CanConsume(Epoch)&&s.ResetCredits.Credits==null,"count-only lost");
            s=UsageSnapshot.Parse(Json.Read<object>("{\"rateLimitResetCredits\":{\"availableCount\":7,\"credits\":[{\"id\":\"x\",\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":null}]}}"));Assert(s.ResetCredits.AvailableCount==7&&s.ResetCredits.Next(Epoch).Id=="x","partial detail count overwritten");
        });
        check("malformed credit list/expiry/count never authorizes spending",delegate {
            foreach(string payload in new[]{"{\"availableCount\":2,\"credits\":{}}","{\"availableCount\":1.5,\"credits\":null}","{\"availableCount\":2,\"credits\":[{\"id\":\"x\",\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":\"invalid\"}]}"})
                Assert(!UsageSnapshot.Parse(Json.Read<object>("{\"rateLimitResetCredits\":"+payload+"}")).ResetCredits.CanConsume(Epoch),"malformed metadata authorized");
        });
        check("exact 1 percent resets earliest eligible credit and rereads",delegate {
            var p=Profile();var f=new Fake();string durable=null;
            f.OnConsume=delegate {Assert(durable!=null&&durable.Contains(p.ResetAttempt.IdempotencyKey),"intent not durable before request");};
            Run(new ResetController(()=>Epoch),p,f,()=>durable=Json.Write(p));
            Assert(f.Keys.Count==1&&f.Credits[0]=="first"&&f.Reads==1&&p.ResetAttempt.State=="recovered"&&p.ResetCredits.AvailableCount==1,"reset failed");
        });
        check("zero percent eligible / values above 1 never rounded down",delegate {
            var f=new Fake();Run(new ResetController(()=>Epoch),Profile(0),f);Assert(f.Keys.Count==1,"zero not eligible");
            foreach(double? value in new double?[]{1.0001,1.4,2,null}) {f=new Fake();Run(new ResetController(()=>Epoch),Profile(value),f);Assert(f.Keys.Count==0,"rounded/missing quota consumed");}
        });
        check("weekly Codex threshold works but unrelated bucket does not",delegate {
            var p=Profile(90);p.Quotas[0].Secondary.Remaining=1;var f=new Fake();Run(new ResetController(()=>Epoch),p,f);Assert(f.Keys.Count==1,"weekly quota ignored");
            p=Profile();p.Quotas[0].Name="other_model";f=new Fake();Run(new ResetController(()=>Epoch),p,f);Assert(f.Keys.Count==0,"unrelated bucket spent a credit");
        });
        check("legacy single-bucket identity is preserved for reset eligibility",delegate {
            var p=Profile();p.Quotas=Quotas.Parse(Json.Read<object>("{\"rateLimits\":{\"limitId\":\"other_model\",\"primary\":{\"usedPercent\":100}}}"));
            var f=new Fake();Run(new ResetController(()=>Epoch),p,f);Assert(f.Keys.Count==0,"legacy model misidentified as codex");
        });
        check("inactive/disabled/stale/failed reads cannot consume",delegate {
            foreach(string mode in new[]{"inactive","stale","failed","future"}) {var p=Profile();var f=new Fake();if(mode=="stale") p.QuotaTimeUtc=Epoch.AddMinutes(-2).ToString("o");if(mode=="future") p.QuotaTimeUtc=Epoch.AddMinutes(1).ToString("o");if(mode=="failed")p.Error="offline";Run(new ResetController(()=>Epoch),p,f,null,()=>mode!="inactive");Assert(f.Keys.Count==0,"unsafe reset "+mode);}
        });
        check("account identity is checked again immediately before send",delegate {
            int checks=0;var f=new Fake();Run(new ResetController(()=>Epoch),Profile(),f,null,()=>++checks<3);Assert(f.Keys.Count==0,"reset after account changed");
        });
        check("expired credits / wrong type / zero / unknown counts refused",delegate {
            foreach(string mode in new[]{"expired","type","status","zero","unknown","empty"}) {var p=Profile();if(mode=="expired") foreach(var c in p.ResetCredits.Credits)c.ExpiresAt=Unix(Epoch.AddSeconds(-1));if(mode=="type")foreach(var c in p.ResetCredits.Credits)c.ResetType="unknown";if(mode=="status")foreach(var c in p.ResetCredits.Credits)c.Status="redeemed";if(mode=="zero")p.ResetCredits.AvailableCount=0;if(mode=="unknown")p.ResetCredits.AvailableCount=null;if(mode=="empty")p.ResetCredits.Credits.Clear();var f=new Fake();Run(new ResetController(()=>Epoch),p,f);Assert(f.Keys.Count==0,"invalid credit consumed "+mode);}
        });
        check("expired quota window does not trigger a spend",delegate {var p=Profile();p.Quotas[0].Primary.ResetsAt=Unix(Epoch);var f=new Fake();Run(new ResetController(()=>Epoch),p,f);Assert(f.Keys.Count==0,"elapsed window reset");});
        check("count-only availability lets server choose the credit",delegate {var p=Profile();p.ResetCredits.Credits=null;var f=new Fake();Run(new ResetController(()=>Epoch),p,f);Assert(f.Keys.Count==1&&f.Credits[0]==null,"count-only reset blocked");});
        check("failed durable save prevents any consumption",delegate {var f=new Fake();Throws(()=>Run(new ResetController(()=>Epoch),Profile(),f,()=>{throw new IOException();}));Assert(f.Keys.Count==0,"sent before persistence");});
        check("timeout restart retries the same persisted key and credit",delegate {
            var p=Profile();var f=new Fake{FailConsume=true};string persisted=null;Run(new ResetController(()=>Epoch),p,f,()=>persisted=Json.Write(p));
            var restarted=Json.Read<Profile>(persisted);restarted.QuotaTimeUtc=Epoch.AddSeconds(61).ToString("o");f.FailConsume=false;f.Outcome="alreadyRedeemed";
            Run(new ResetController(()=>Epoch.AddSeconds(61)),restarted,f);
            Assert(f.Keys.Count==2&&f.Keys[0]==f.Keys[1]&&f.Credits[0]==f.Credits[1]&&restarted.ResetAttempt.State=="recovered","duplicate reset intent");
        });
        check("pending retries have a 60 second interval and a three-attempt cap",delegate {
            DateTime now=Epoch;var p=Profile();var f=new Fake{FailConsume=true};var controller=new ResetController(()=>now);Run(controller,p,f);Run(controller,p,f);Assert(f.Keys.Count==1,"immediate retry");
            for(int i=0;i<5;i++) {now=now.AddSeconds(61);p.QuotaTimeUtc=now.ToString("o");Run(controller,p,f);}
            Assert(f.Keys.Count==3&&f.Keys.Distinct().Count()==1,"unbounded or new-key retry");
        });
        check("accepted reset with failed readback cannot consume again",delegate {
            DateTime now=Epoch;var p=Profile();var f=new Fake{FailRead=true};var controller=new ResetController(()=>now);Run(controller,p,f);Assert(p.ResetAttempt.State=="accepted","accepted status lost");
            now=now.AddMinutes(10);p.QuotaTimeUtc=now.ToString("o");Run(controller,p,f);Assert(f.Keys.Count==1,"second credit consumed before verification");
            Snapshot(90,1).Apply(p,now);Run(controller,p,f);Assert(p.ResetAttempt.State=="recovered","verification never completes");
        });
        check("unchanged quota after accepted reset remains latched",delegate {var p=Profile();var f=new Fake{After=Snapshot(1,1)};var controller=new ResetController(()=>Epoch);Run(controller,p,f);Run(controller,p,f);Assert(f.Keys.Count==1&&p.ResetAttempt.State=="accepted","consumed repeatedly while waiting");});
        check("five minute cooldown prevents immediate second reset after recovery",delegate {
            DateTime now=Epoch;var p=Profile();var f=new Fake();var controller=new ResetController(()=>now);Run(controller,p,f);
            now=Epoch.AddMinutes(1);Snapshot(1,1).Apply(p,now);Run(controller,p,f);Assert(f.Keys.Count==1,"cooldown bypassed");
            now=Epoch.AddMinutes(6);Snapshot(1,1).Apply(p,now);Run(controller,p,f);Assert(f.Keys.Count==2&&f.Keys[0]!=f.Keys[1],"new incident never rearmed");
        });
        check("nothingToReset waits for a changed quota instead of consuming in a loop",delegate {
            DateTime now=Epoch;var p=Profile();var f=new Fake{Outcome="nothingToReset"};var controller=new ResetController(()=>now);Run(controller,p,f);Run(controller,p,f);Assert(f.Keys.Count==1,"negative outcome loop");
            now=now.AddMinutes(1);Snapshot(0,2).Apply(p,now);f.Outcome="reset";Run(controller,p,f);Assert(f.Keys.Count==2&&f.Keys[0]!=f.Keys[1],"zero percent not retried");
        });
        check("noCredit waits and rearms after availability returns",delegate {
            var p=Profile();var f=new Fake{Outcome="noCredit"};var controller=new ResetController(()=>Epoch);Run(controller,p,f);Run(controller,p,f);Assert(f.Keys.Count==1,"noCredit loop");
            Snapshot(1,0).Apply(p,Epoch);Run(controller,p,f);Snapshot(1,2).Apply(p,Epoch);f.Outcome="reset";Run(controller,p,f);Assert(f.Keys.Count==2,"newly available credits ignored");
        });
        check("unknown outcome never declares success or sends another reset",delegate {var p=Profile();var f=new Fake{Outcome="future-status"};var controller=new ResetController(()=>Epoch);Run(controller,p,f);Run(controller,p,f);Assert(f.Keys.Count==1&&p.ResetAttempt.State=="unsupported"&&f.Reads==0,"unknown outcome treated as success");});
        check("concurrent polls serialize into one consumption",delegate {
            var p=Profile();var f=new Fake{Delayed=new TaskCompletionSource<string>()};var controller=new ResetController(()=>Epoch);
            var first=controller.Run(p,()=>true,f,delegate{},CancellationToken.None);var second=controller.Run(p,()=>true,f,delegate{},CancellationToken.None);f.Delayed.SetResult("reset");Task.WhenAll(first,second).GetAwaiter().GetResult();Assert(f.Keys.Count==1,"concurrent duplicate");
        });
        check("cancelled operation cannot consume",delegate {var f=new Fake();var cts=new CancellationTokenSource();cts.Cancel();Throws(()=>new ResetController(()=>Epoch).Run(Profile(),()=>true,f,delegate{},cts.Token).GetAwaiter().GetResult());Assert(f.Keys.Count==0,"cancelled request sent");cts.Dispose();});
        check("older settings enable requested default; explicit disable persists",delegate {Assert(Json.Read<Settings>("{\"AutoRefresh\":false}").AutoResetCredits,"missing setting default wrong");Assert(!Json.Read<Settings>("{\"AutoResetCredits\":false}").AutoResetCredits,"disabled preference lost");});
    }
}
