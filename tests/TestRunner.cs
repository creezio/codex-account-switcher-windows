using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Creezio.Switcher;

internal static class TestRunner
{
    private static int passed;
    private static string sandbox;
    private static void Assert(bool condition,string message) {if(!condition) throw new Exception(message);}
    private static void Check(string name,Action test) {try{test();}catch(Exception e){throw new Exception(name+": "+e.Message,e);}passed++;Console.WriteLine("PASS " + name);}
    private static void Throws(Action action) {try {action();} catch {return;} throw new Exception("Expected failure");}
    private static string Encode(object value) {return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static string FakeAuth(string user,string account)
    {
        var claims=new Dictionary<string,object> {{"sub",user},{"email",user+"@example.com"},{"https://api.openai.com/auth",new {chatgpt_account_id=account,chatgpt_plan_type="plus"}}};
        return Json.Write(new {auth_mode="chatgpt",tokens=new {access_token="fictional-access-token-"+user,id_token="e30."+Encode(claims)+".fake",refresh_token="fictional-refresh-token",account_id=account}});
    }
    private static string Folder(string name) {string path=Path.Combine(sandbox,name);Directory.CreateDirectory(path);return path;}
    public static int Main(string[] args)
    {
        sandbox=Path.GetFullPath(args.Length>0?args[0]:"work\\tests");
        if(Directory.Exists(sandbox) && Directory.GetFileSystemEntries(sandbox).Length>0) {Console.Error.WriteLine("Test directory must be empty.");return 2;}
        Directory.CreateDirectory(sandbox);
        try
        {
            string a=FakeAuth("alice","workspace-a"),b=FakeAuth("bob","workspace-b");
            Check("identity distinguishes workspace and user",delegate {Assert(AuthIdentity.Parse(a).Key!=AuthIdentity.Parse(FakeAuth("alice","workspace-b")).Key,"workspace collision");Assert(AuthIdentity.Parse(a).Key!=AuthIdentity.Parse(FakeAuth("bob","workspace-a")).Key,"user collision");});
            Check("invalid / API-key / mismatched auth rejected",delegate {Throws(()=>AuthIdentity.Parse("{}"));Throws(()=>AuthIdentity.Parse("{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fake\"}"));Throws(()=>AuthIdentity.Parse(a.Replace("\"account_id\":\"workspace-a\"","\"account_id\":\"wrong\"")));});
            Check("unknown quota is not 100 percent",delegate {var q=Quotas.Parse(Json.Read<object>("{\"rateLimits\":{\"primary\":{\"usedPercent\":null}}}"));Assert(!q[0].Primary.Remaining.HasValue,"null treated as zero");Assert(q[0].Secondary==null,"missing secondary fabricated");});
            Check("remaining clamps and multiple buckets take precedence",delegate {var q=Quotas.Parse(Json.Read<object>("{\"rateLimits\":{\"primary\":{\"usedPercent\":55}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":120},\"secondary\":{\"usedPercent\":-4}},\"other\":{}}}"));Assert(q.Count==2&&q[0].Primary.Remaining==0&&q[0].Secondary.Remaining==100,"bad quota calculation");});
            Check("recommendation excludes stale/error data",delegate {var p=new Profile {QuotaTimeUtc=DateTime.UtcNow.AddHours(-1).ToString("o"),Quotas=new List<QuotaBucket>{new QuotaBucket{Name="codex",Primary=new QuotaWindow{Remaining=80}}}};Assert(!p.Score.HasValue,"stale recommendation");p.QuotaTimeUtc=DateTime.UtcNow.ToString("o");Assert(p.Score==80,"fresh score missing");p.Error="offline";Assert(!p.Score.HasValue,"error recommended");});
            Check("DPAPI round trip and no cleartext credentials",delegate {var vault=new Vault(Folder("vault"));var d=new VaultData();d.Profiles.Add(new Profile{Key=AuthIdentity.Parse(a).Key,Label="Alice",AuthJson=a});vault.Save(d);Assert(vault.Load().Profiles[0].AuthJson==a,"vault mismatch");Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(vault.FilePath)).Contains("fictional-access-token"),"cleartext credentials");});
            Check("corrupt vault preserved",delegate {var vault=new Vault(Folder("corrupt"));File.WriteAllBytes(vault.FilePath,new byte[]{1,2,3,4});Throws(()=>vault.Load());Assert(File.ReadAllBytes(vault.FilePath).SequenceEqual(new byte[]{1,2,3,4}),"corrupt vault changed");});
            Check("atomic writer does not delete another writer's temp",delegate {string p=Path.Combine(Folder("collision"),"value");File.WriteAllText(p+".switcher-tmp","belongs to someone else");Throws(()=>SafeFiles.AtomicWrite(p,new byte[]{1}));Assert(File.ReadAllText(p+".switcher-tmp")=="belongs to someone else","deleted foreign temp");});
            Check("running Codex blocks switch without backup or writes",delegate {string home=Folder("busy");File.WriteAllText(Path.Combine(home,"auth.json"),a);bool backup=false;Throws(()=>new SwitchTransaction(home,()=>true).Execute(b,s=>backup=true,delegate{}));Assert(!backup&&File.ReadAllText(Path.Combine(home,"auth.json"))==a,"changed live state");});
            Check("keyring and auto are refused",delegate {foreach(string mode in new[]{"keyring","auto","ephemeral"}) {string home=Folder(mode);File.WriteAllText(Path.Combine(home,"config.toml"),"cli_auth_credentials_store = \""+mode+"\"\n");Throws(()=>new SwitchTransaction(home,()=>false).Execute(b,s=>{},delegate{}));Assert(!File.Exists(Path.Combine(home,"auth.json")),"created shadow auth");}});
            Check("quoted key and comments parsed conservatively",delegate {string home=Folder("quoted");File.WriteAllText(Path.Combine(home,"config.toml"),"'cli_auth_credentials_store' = 'auto' # comment\n");Throws(()=>CodexEnvironment.CheckFileStorage(home));File.WriteAllText(Path.Combine(home,"config.toml"),"cli_auth_credentials_store = 'file' # comment\n[profiles.test]\ncli_auth_credentials_store = 'keyring'\n");CodexEnvironment.CheckFileStorage(home);});
            Check("successful switch preserves config and exact previous auth",delegate {string home=Folder("switch");string path=Path.Combine(home,"auth.json");File.WriteAllText(path,a);File.WriteAllText(Path.Combine(home,"config.toml"),"model = 'example'\n");string backup=null;new SwitchTransaction(home,()=>false).Execute(b,s=>backup=s,delegate{});Assert(backup==a&&File.ReadAllText(path)==b,"switch mismatch");Assert(File.ReadAllText(Path.Combine(home,"config.toml"))=="model = 'example'\n","config changed");});
            Check("verification failure rolls back exact original bytes",delegate {string home=Folder("rollback");string path=Path.Combine(home,"auth.json");File.WriteAllText(path,a,new UTF8Encoding(true));byte[] before=File.ReadAllBytes(path);Throws(()=>new SwitchTransaction(home,()=>false).Execute(b,s=>{},()=>{throw new IOException();}));Assert(File.ReadAllBytes(path).SequenceEqual(before),"rollback changed bytes");});
            Check("backup failure leaves auth untouched",delegate {string home=Folder("backup-fails");string path=Path.Combine(home,"auth.json");File.WriteAllText(path,a);Throws(()=>new SwitchTransaction(home,()=>false).Execute(b,s=>{throw new IOException();},delegate{}));Assert(File.ReadAllText(path)==a,"write despite failed backup");});
            Check("rollback restores absent auth",delegate {string home=Folder("absent");Throws(()=>new SwitchTransaction(home,()=>false).Execute(b,s=>{},()=>{throw new IOException();}));Assert(!File.Exists(Path.Combine(home,"auth.json")),"left new auth after failure");});
            Check("concurrent writer before replacement preserved",delegate {string home=Folder("race-before");string path=Path.Combine(home,"auth.json");File.WriteAllText(path,a);Throws(()=>new SwitchTransaction(home,()=>false).Execute(b,s=>File.WriteAllText(path,"external change"),delegate{}));Assert(File.ReadAllText(path)=="external change","clobbered newer state");});
            Check("concurrent writer after replacement preserved",delegate {string home=Folder("race-after");string path=Path.Combine(home,"auth.json");File.WriteAllText(path,a);Throws(()=>new SwitchTransaction(home,()=>false).Execute(b,s=>{},()=>{File.WriteAllText(path,"external change");throw new IOException();}));Assert(File.ReadAllText(path)=="external change","rollback clobbered concurrent state");});
            Check("cleanup refuses root and outside paths",delegate {Throws(()=>SafeFiles.DeleteOwnedTree(sandbox,sandbox));Throws(()=>SafeFiles.DeleteOwnedTree(sandbox,Path.GetDirectoryName(sandbox)));});
            Check("previous API-key mode can be restored without accepting it as a profile",delegate {string home=Folder("api-restore");string api="{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fictional-api-key\"}";Throws(()=>new SwitchTransaction(home,()=>false).Execute(api,s=>{},delegate{}));new SwitchTransaction(home,()=>false,true).Execute(api,s=>{},delegate{});Assert(File.ReadAllText(Path.Combine(home,"auth.json"))==api,"API backup not restored");});
            Check("switch does not touch conversations",delegate {string home=Folder("history");Directory.CreateDirectory(Path.Combine(home,"sessions"));string history=Path.Combine(home,"sessions","conversation.jsonl");File.WriteAllText(history,"keep me");new SwitchTransaction(home,()=>false).Execute(a,s=>{},delegate{});Assert(File.ReadAllText(history)=="keep me","history changed");});
            ResetTests.RunAll(Check);
            LoginBrowserTests.RunAll(Check,sandbox);
            InstanceTests.RunAll(Check,sandbox,a,b);
            SimpleWorkflowTests.RunAll(Check,sandbox,a,b);
            ToolTunnelTests.RunAll(Check,sandbox);SharedPagesTests.RunAll(Check,sandbox);ResourceLinksTests.RunAll(Check,sandbox);
            ResourceCatalogTests.RunAll(Check,sandbox);
            RelayTests.RunAll(Check,sandbox);
            GeneralRelayTests.RunAll(Check,sandbox);
            ProductTests.RunAll(Check,sandbox);
            ConsoleTests.RunAll(Check,sandbox);
            RemoteTests.RunAll(Check,sandbox);
            AssistanceTests.RunAll(Check,sandbox);
            Console.WriteLine(passed+" tests passed.");
            return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine("FAIL "+ex.Message);return 1;}
        finally {SafeFiles.DeleteOwnedTree(Path.GetDirectoryName(sandbox),sandbox);}
    }
}
