using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;
using Microsoft.Win32.SafeHandles;

// All children here are in-memory fakes. No Codex, MCP, model, or user process
// is started, stopped, queried, or sent an agent prompt by these regressions.
internal static class RelayMcpBootstrapTests
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputSize,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static object Read(string value){return value==null?null:Json.Read<object>(value);}
    private static void Invalid(Action action){try{action();}catch(InvalidOperationException){return;}catch(IOException){return;}throw new Exception("Expected bootstrap binding or target rejection");}
    private static bool Failed(Func<string> action){try{var result=Read(action());return Json.Get(result,"error")!=null||Object.Equals(Json.Get(Json.Get(result,"result"),"isError"),true);}catch(InvalidOperationException){return true;}catch(IOException){return true;}}
    private static string Request(string method,int id,string name="fixture"){return Json.Write(new{jsonrpc="2.0",id=id,method=method,@params=new{protocolVersion="2025-11-25",capabilities=new{},clientInfo=new{name="bootstrap-fixture",version="1"},name=name,arguments=new{id="fictional-job-id"}}});}
    private const string Initialized="{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";
    private sealed class Child : IRelayMcpChild
    {
        internal readonly string Version;internal readonly List<string> Methods=new List<string>(),Notifications=new List<string>();internal int Calls,Disposed;internal bool CrashCall,FailInitialize,WrongInitializeId,LoseResponse;internal Action DuringCall;private bool alive=true;
        internal Child(string version){Version=version;}
        public bool Alive {get{return alive;}}
        public string Exchange(string request)
        {
            if(!alive)throw new IOException("Fictional dead child");var value=Read(request);string method=Json.Str(Json.Get(value,"method"));Methods.Add(method);
            if(method=="initialize"){if(FailInitialize)throw new IOException("Fictional handshake failure");return Json.Write(new{jsonrpc="2.0",id=WrongInitializeId?(object)999:Json.Get(value,"id"),result=new{protocolVersion="2025-11-25",capabilities=new{tools=new{}},serverInfo=new{name="creezio-relay",version=Version}}});}
            if(method=="tools/call"){Calls++;if(DuringCall!=null)DuringCall();if(CrashCall){alive=false;throw new IOException("Fictional child crashed after accepting tool call");}if(LoseResponse)throw new IOException("Fictional accepted request with a response still in flight");}
            return Json.Write(new{jsonrpc="2.0",id=Json.Get(value,"id"),result=new{engine=Version,method=method,tools=new object[0],ok=true}});
        }
        public void Notify(string notification){if(!alive)throw new IOException("Fictional dead child");Notifications.Add(Json.Str(Json.Get(Read(notification),"method")));}
        internal void Exit(){alive=false;}
        public void Dispose(){Disposed++;alive=false;}
    }
    private sealed class Fixture : IDisposable
    {
        internal RelayMcpEnginePointer Next;internal readonly List<Child> Children=new List<Child>();internal readonly List<string> OutboundNotifications=new List<string>();internal readonly RelayMcpBootstrap.Session Session;internal Func<RelayMcpEnginePointer,Child> OverrideCreate;
        internal Fixture(string root,string name,string version="0.28.0-beta.1")
        {
            string folder=Path.Combine(Path.GetFullPath(root),"mcp-bootstrap-tests",name+"-"+Guid.NewGuid().ToString("N").Substring(0,8)),home=Path.Combine(folder,"home");Directory.CreateDirectory(home);Next=Pointer(home,Path.Combine(folder,"CreezioRelay.exe"),version,"fictional-account");
            Session=new RelayMcpBootstrap.Session(()=>Next,p=>{var child=OverrideCreate==null?new Child(p.Version):OverrideCreate(p);Children.Add(child);return child;},null,line=>OutboundNotifications.Add(Json.Str(Json.Get(Read(line),"method"))));
        }
        internal static RelayMcpEnginePointer Pointer(string home,string executable,string version,string account){return new RelayMcpEnginePointer{Home=home,Executable=executable,Version=version,AccountKey=account,Sha256=new string('a',64),UpdatedUtc=DateTime.UtcNow.ToString("o")};}
        internal void Initialize(){string response=Session.Handle(Request("initialize",1));Assert(Json.Get(Read(response),"error")==null,"Initial handshake failed");Session.Handle(Initialized);}
        internal void Upgrade(string version){Next=Pointer(Next.Home,Next.Executable,version,Next.AccountKey);}
        public void Dispose(){Session.Dispose();}
    }
    private static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
    private sealed class ObservationFixture
    {
        internal readonly string Root,Home,Account,Instance=new string('a',32),IntegrationName;
        internal readonly RelayStore Store;internal readonly Vault Vault;
        internal ObservationFixture(string root)
        {
            Root=Path.Combine(Path.GetFullPath(root),"mcp-observation-"+Guid.NewGuid().ToString("N").Substring(0,6));Home=InstancePaths.Home(Root,Instance);Directory.CreateDirectory(Home);Store=new RelayStore(Path.Combine(Root,"relay"));Vault=new Vault(Root);
            string claims=Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(new{ sub="fictional-observation-user",email="fictional@example.invalid"}))).TrimEnd('=').Replace('+','-').Replace('/','_');
            string auth=Json.Write(new{tokens=new{access_token="fictional-unused-access",id_token="e30."+claims+".fixture",account_id="fictional-observation-account"}});Account=AuthIdentity.Parse(auth).Key;File.WriteAllText(Path.Combine(Home,"auth.json"),auth);
            var data=new VaultData();data.Profiles.Add(new Profile{Key=Account,AuthJson=auth,InstanceIds=new List<string>{Instance}});data.Instances.Add(new DesktopInstance{Id=Instance,Name="Fictional managed instance",AccountKey=Account,AccountLocked=true});Vault.Save(data);Vault.SaveSettings(new Settings{CodexHome=Home,CodexExecutable=typeof(RelayMcpBootstrapTests).Assembly.Location});
            IntegrationName="integration-"+RelayIntegration.HomeKey(Home)+".dpapi";Store.WriteRecord(IntegrationName,new RelayIntegrationState{Healthy=true,Status="Installed fixture",InstalledVersion=RelayWorker.Version+"+fixture",BootstrapConfigured=true,RuntimeStatus="confirmed",RuntimeVersion="stale",RuntimeUpdatedUtc="stale",BootstrapConnections=99});
            Receipt(11,Account,Home);Receipt(21,Account,Home);Receipt(31,"foreign-account",Home);Receipt(41,Account,Path.Combine(Root,"foreign-home"));
        }
        private void Receipt(int pid,string account,string home){Store.WriteRecord("mcp-bootstrap-"+RelayIntegration.HomeKey(Home)+"-"+pid+".dpapi",new RelayMcpBootstrapRuntime{Origin="desktop",Home=home,AccountKey=account,Version=RelayWorker.Version,State="confirmed",UpdatedUtc=DateTime.UtcNow.ToString("o"),BootstrapPid=pid,BootstrapStarted=pid+100,DesktopPid=pid+1,DesktopStarted=pid+101,ChildPid=pid+2,ChildStarted=pid+102});}
        internal string Fingerprint(){return String.Join("|",Directory.GetFiles(Root,"*",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal).Select(p=>p+":"+Hash(p)));}
        internal void AssertIdentity(){var data=Vault.Load();Assert(data.Profiles.Count==1&&data.Profiles.Single().Key==Account&&data.Instances.Single(i=>i.Id==Instance).AccountKey==Account&&data.Instances.Single(i=>i.Id==Instance).AccountLocked,"Runtime diagnostics changed account or instance identity");}
    }
    private static void Junction(string path,string target)
    {
        Directory.CreateDirectory(path);string substitute=@"\??\"+Path.GetFullPath(target),print=Path.GetFullPath(target);byte[] sub=Encoding.Unicode.GetBytes(substitute),display=Encoding.Unicode.GetBytes(print);int length=8+sub.Length+2+display.Length+2;var data=new byte[8+length];
        Array.Copy(BitConverter.GetBytes(0xA0000003u),0,data,0,4);Array.Copy(BitConverter.GetBytes((ushort)length),0,data,4,2);Array.Copy(BitConverter.GetBytes((ushort)sub.Length),0,data,10,2);Array.Copy(BitConverter.GetBytes((ushort)(sub.Length+2)),0,data,12,2);Array.Copy(BitConverter.GetBytes((ushort)display.Length),0,data,14,2);Array.Copy(sub,0,data,16,sub.Length);Array.Copy(display,0,data,18+sub.Length,display.Length);
        using(var handle=CreateFile(path,0x40000000,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero)){int returned;if(handle.IsInvalid||!DeviceIoControl(handle,0x000900A4,data,data.Length,IntPtr.Zero,0,out returned,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("MCP integration status preserves its inventory when a pinned process inspection is denied",()=>{
            foreach(bool unauthorized in new[]{false,true})foreach(int denied in new[]{11,12,13}){
                var f=new ObservationFixture(root);string before=f.Fingerprint();var inspected=new List<int>();
                var healthy=RelayIntegration.Status(f.Store,f.Home,(pid,started)=>{inspected.Add(pid);Assert(started==pid+100,"Process start pin was lost");return true;});Assert(healthy.RuntimeStatus=="confirmed"&&healthy.BootstrapConnections==2&&!inspected.Any(pid=>pid>=31),"Unrelated account or home entered runtime confirmation");
                inspected.Clear();var state=RelayIntegration.Status(f.Store,f.Home,(pid,started)=>{inspected.Add(pid);Assert(started==pid+100,"Process start pin changed");if(pid==denied){if(unauthorized)throw new UnauthorizedAccessException("fictional private credential must not escape");throw new Win32Exception(5,"fictional private credential must not escape");}return true;});
                Assert(state.Healthy&&state.Status=="Installed fixture"&&state.InstalledVersion==RelayWorker.Version+"+fixture"&&state.BootstrapConfigured,"Inspection failure erased verified disk installation");Assert(state.RuntimeStatus=="unknown"&&state.BootstrapConnections==0&&state.RuntimeVersion==null&&state.RuntimeUpdatedUtc==null,"Denied inspection retained a confirmed or stale runtime");Assert(inspected.Contains(denied)&&!inspected.Any(pid=>pid>=31)&&!Json.Write(state).Contains("private credential"),"Identity filters were bypassed or exception content leaked");f.AssertIdentity();Assert(before==f.Fingerprint(),"Runtime inventory observation changed encrypted receipts or credentials");
            }
        });
        check("MCP process diagnostics preserve strict errors outside inaccessible process metadata",()=>{
            var f=new ObservationFixture(root);string before=f.Fingerprint();bool failed=false;try{RelayIntegration.Status(f.Store,f.Home,(pid,started)=>{throw new CryptographicException("Fictional non-process failure");});}catch(CryptographicException){failed=true;}Assert(failed,"Unrelated corruption was silently classified as an inaccessible process");
            File.WriteAllBytes(Path.Combine(f.Store.Root,f.IntegrationName),new byte[]{1,2,3});byte[] corrupt=File.ReadAllBytes(Path.Combine(f.Store.Root,f.IntegrationName));failed=false;try{RelayIntegration.Status(f.Store,f.Home,(pid,started)=>true);}catch(CryptographicException){failed=true;}Assert(failed&&File.ReadAllBytes(Path.Combine(f.Store.Root,f.IntegrationName)).SequenceEqual(corrupt),"Unreadable integration receipt was overwritten or hidden by a broad catch");Assert(before!=f.Fingerprint(),"Corruption fixture was not exercised");f.AssertIdentity();
        });
        check("MCP bootstrap semver ordering never treats beta ten as older than beta two",()=>{
            Assert(RelayMcpBootstrap.CompareVersion("0.28.0-beta.10","0.28.0-beta.2")>0,"Prerelease identifiers compared lexically");Assert(RelayMcpBootstrap.CompareVersion("0.28.0","0.28.0-beta.10")>0,"Stable release precedes prerelease");Assert(RelayMcpBootstrap.CompareVersion("0.29.0-beta.1","0.28.99")>0,"Numeric release ordering incorrect");
        });
        check("MCP bootstrap rotates only its owned child and replays handshake only",()=>{
            using(var f=new Fixture(root,"handshake")){f.Initialize();f.Session.Handle(Request("tools/list",2));f.Session.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":99}}");var old=f.Children.Single();f.Upgrade("0.29.0-beta.1");var reply=Read(f.Session.Handle(Request("tools/call",3)));Assert(Json.Get(reply,"error")==null&&f.Children.Count==2,"New child not usable after upgrade");var current=f.Children.Last();Assert(old.Disposed==1&&current.Calls==1&&old.Calls==0,"Upgrade interrupted unrelated work or replayed a tool");Assert(current.Methods.SequenceEqual(new[]{"initialize","tools/call"})&&current.Notifications.SequenceEqual(new[]{"notifications/initialized"}),"Replay included tools/list cancellation or another non-handshake request");}
        });
        check("MCP bootstrap child crash never replays an accepted tool call",()=>{
            using(var f=new Fixture(root,"crash")){f.Initialize();f.Children.Single().CrashCall=true;Assert(Failed(()=>f.Session.Handle(Request("tools/call",2))),"Child crash looked successful");Assert(f.Children.Sum(c=>c.Calls)==1,"Failed tool call automatically replayed");f.Session.Handle(Request("tools/list",3));Assert(f.Children.Count==2&&f.Children.Sum(c=>c.Calls)==1&&f.Children.Last().Methods.SequenceEqual(new[]{"initialize","tools/list"}),"Crash recovery replayed work instead of only handshake");}
        });
        check("MCP bootstrap does not stop or replace an owned child while an exchange is busy",()=>{
            using(var f=new Fixture(root,"busy"))using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim()){
                f.Initialize();var old=f.Children.Single();old.DuringCall=()=>{entered.Set();if(!release.Wait(5000))throw new IOException("Fixture busy exchange timeout");};var call=Task.Run(()=>f.Session.Handle(Request("tools/call",2)));
                try{Assert(entered.Wait(2000),"Fixture never entered tool exchange");f.Upgrade("0.29.0-beta.1");var refresh=Task.Run(()=>f.Session.Refresh());Assert(refresh.Wait(1000),"Refresh blocked waiting for active call");Assert(old.Disposed==0&&f.Children.Count==1,"Active child stopped or replaced before response");}
                finally{release.Set();}
                Assert(call.Wait(2000)&&Json.Get(Read(call.Result),"error")==null,"Busy call lost its response");f.Session.Refresh();Assert(old.Disposed==1&&f.Children.Count==2&&f.Children.Sum(c=>c.Calls)==1,"Completed call was lost or repeated at rotation");
            }
        });
        check("MCP bootstrap refuses version downgrade while keeping the running newer child",()=>{
            using(var f=new Fixture(root,"downgrade","0.29.0-beta.10")){f.Initialize();var current=f.Children.Single();f.Upgrade("0.29.0-beta.2");try{f.Session.Refresh();}catch(InvalidOperationException){}var response=Read(f.Session.Handle(Request("tools/list",2)));Assert(f.Children.Count==1&&current.Disposed==0&&Json.Str(Json.Get(Json.Get(response,"result"),"engine"))=="0.29.0-beta.10","Bootstrap downgraded or stopped a newer child");}
        });
        check("MCP bootstrap never reuses a new target from another profile or account",()=>{
            foreach(bool changeHome in new[]{false,true})using(var f=new Fixture(root,"profile-binding")){f.Initialize();var current=f.Children.Single();string home=changeHome?Path.Combine(f.Next.Home,"other"):f.Next.Home;f.Next=Fixture.Pointer(home,f.Next.Executable,"0.29.0-beta.1",changeHome?f.Next.AccountKey:"another-account");try{f.Session.Refresh();}catch(InvalidOperationException){}Assert(f.Children.Count==1&&current.Disposed==0,"A different profile or account was started");}
        });
        check("MCP bootstrap validates the child handshake version before forwarding tools",()=>{
            using(var f=new Fixture(root,"child-version")){f.OverrideCreate=p=>new Child("0.1.0");Assert(Failed(()=>f.Session.Handle(Request("initialize",1))),"Child version disagreed with verified target but was accepted");Assert(f.Children.Sum(c=>c.Calls)==0,"Mismatched executable received a tool invocation");}
        });
        check("MCP bootstrap failed handshake cannot accidentally initialize a tool session",()=>{
            using(var f=new Fixture(root,"handshake-failure")){f.OverrideCreate=p=>new Child(p.Version){FailInitialize=true};Assert(Failed(()=>f.Session.Handle(Request("initialize",1))),"Handshake failure reported success");Assert(Failed(()=>f.Session.Handle(Request("tools/call",2))),"Tool invocation proceeded without valid initialize");Assert(f.Children.Sum(c=>c.Calls)==0,"Tool was invoked after failed initialize");}
        });
        check("MCP bootstrap updating one profile never disposes a different profile child",()=>{
            using(var a=new Fixture(root,"profile-a"))using(var b=new Fixture(root,"profile-b")){a.Initialize();b.Initialize();var other=b.Children.Single();a.Upgrade("0.29.0-beta.1");a.Session.Refresh();Assert(other.Disposed==0&&b.Children.Count==1&&Json.Get(Read(b.Session.Handle(Request("tools/list",2))),"error")==null,"Another account or MCP child was interrupted");}
        });
        check("MCP bootstrap unchanged version reuses a single healthy child without repeated initialize",()=>{
            using(var f=new Fixture(root,"healthy")){f.Initialize();for(int i=2;i<6;i++){f.Session.Refresh();f.Session.Handle(Request("tools/list",i));}Assert(f.Children.Count==1&&f.Children.Single().Disposed==0&&f.Children.Single().Methods.Count(m=>m=="initialize")==1,"Healthy MCP repeatedly reconnected");}
        });
        check("MCP bootstrap unchanged executable cannot bypass a reassociated profile or account",()=>{
            foreach(bool changeHome in new[]{false,true})using(var f=new Fixture(root,"unchanged-binding")){f.Initialize();var old=f.Children.Single();f.Next=Fixture.Pointer(changeHome?Path.Combine(f.Next.Home,"other"):f.Next.Home,f.Next.Executable,f.Next.Version,changeHome?f.Next.AccountKey:"another-account");Assert(Failed(()=>f.Session.Handle(Request("tools/call",2))),"Changed profile binding was silently retained by unchanged version shortcut");Assert(old.Calls==0&&old.Disposed==0&&f.Children.Count==1,"Account mismatch invoked work or stopped old child");}
        });
        check("MCP bootstrap unconfirmed newer child blocks work instead of silently using older engine",()=>{
            using(var f=new Fixture(root,"failed-upgrade")){f.Initialize();var old=f.Children.Single();f.OverrideCreate=p=>new Child(p.Version){FailInitialize=true};f.Upgrade("0.29.0-beta.1");Assert(Failed(()=>f.Session.Handle(Request("tools/call",2))),"Newer handshake failure fell back silently to older engine");Assert(old.Disposed==0&&old.Calls==0&&f.Children.Sum(c=>c.Calls)==0,"Failed upgrade interrupted or invoked old engine");for(int i=3;i<10;i++)Assert(Failed(()=>f.Session.Handle(Request("tools/list",i))),"Unconfirmed engine resumed without a handshake");Assert(f.Children.Count==4,"Repeated refresh exceeded three automatic candidate attempts");}
        });
        check("MCP bootstrap wrong initialize response id cannot authorize a session",()=>{
            using(var f=new Fixture(root,"wrong-receipt")){f.OverrideCreate=p=>new Child(p.Version){WrongInitializeId=true};Assert(Failed(()=>f.Session.Handle(Request("initialize",1))),"A response for another initialize request was accepted");Assert(Failed(()=>f.Session.Handle(Request("tools/call",2)))&&f.Children.Sum(c=>c.Calls)==0,"Wrong initialize receipt allowed a tool call");}
        });
        check("MCP bootstrap uncertain live child is quarantined without interruption or replay",()=>{
            using(var f=new Fixture(root,"uncertain-live")){f.Initialize();var old=f.Children.Single();old.LoseResponse=true;Assert(Failed(()=>f.Session.Handle(Request("tools/call",2))),"Lost response looked successful");f.Upgrade("0.29.0-beta.1");f.Session.Refresh();Assert(Failed(()=>f.Session.Handle(Request("tools/list",3)))&&old.Disposed==0&&f.Children.Count==1&&old.Calls==1,"Uncertain live work was interrupted replayed or raced by a new request");old.Exit();Assert(!Failed(()=>f.Session.Handle(Request("tools/list",4)))&&f.Children.Count==2&&f.Children.Sum(c=>c.Calls)==1,"Exited uncertain child did not recover through handshake only");}
        });
        check("MCP bootstrap publishes list change only after verified engine replacement",()=>{
            using(var f=new Fixture(root,"list-change")){var initial=Read(f.Session.Handle(Request("initialize",1)));Assert(Object.Equals(Json.Get(Json.Get(Json.Get(Json.Get(initial,"result"),"capabilities"),"tools"),"listChanged"),true),"Bootstrap failed to advertise its tool list change notification");f.Session.Handle(Initialized);Assert(f.OutboundNotifications.Count==0,"Initial setup announced a nonexistent replacement");f.Upgrade("0.29.0-beta.1");f.Session.Refresh();Assert(f.OutboundNotifications.SequenceEqual(new[]{"notifications/tools/list_changed"}),"Verified replacement omitted or duplicated tool-list update");f.Session.Refresh();Assert(f.OutboundNotifications.Count==1,"Unchanged engine repeatedly announced replacement");}
        });
        check("MCP bootstrap validates executable bytes product version profile path and reparse ancestors",()=>{
            string folder=Path.Combine(Path.GetFullPath(root),"mcp-bootstrap-target-validation"),home=Path.Combine(folder,"home"),exe=Path.Combine(folder,"CreezioRelay.exe");Directory.CreateDirectory(home);SafeFiles.RejectLinks(folder);File.Copy(typeof(RelayMcpBootstrapTests).Assembly.Location,exe,true);string sha=Hash(exe),version=FileVersionInfo.GetVersionInfo(exe).ProductVersion;var good=Fixture.Pointer(home,exe,version,"fictional-account");good.Sha256=sha;RelayMcpBootstrap.ValidateTarget(good,home,"fictional-account");
            Invalid(()=>RelayMcpBootstrap.ValidateTarget(good,home,"another-account"));Invalid(()=>RelayMcpBootstrap.ValidateTarget(good,Path.Combine(home,"other"),"fictional-account"));good.Sha256=new string('0',64);Invalid(()=>RelayMcpBootstrap.ValidateTarget(good,home,"fictional-account"));good.Sha256=sha;good.Version="99.0.0";Invalid(()=>RelayMcpBootstrap.ValidateTarget(good,home,"fictional-account"));good.Version=version;
            foreach(string bad in new[]{"CreezioRelay.exe",@"\\localhost\share\CreezioRelay.exe",Path.Combine(folder,"OtherRelay.exe")}){good.Executable=bad;Invalid(()=>RelayMcpBootstrap.ValidateTarget(good,home,"fictional-account"));}
            string junction=Path.Combine(folder,"engine-junction-"+Guid.NewGuid().ToString("N"));bool created=false;
            try{Junction(junction,folder);created=true;Assert((File.GetAttributes(junction)&FileAttributes.ReparsePoint)!=0,"Reparse fixture was not created");good.Executable=Path.Combine(junction,"CreezioRelay.exe");Invalid(()=>RelayMcpBootstrap.ValidateTarget(good,home,"fictional-account"));good.Executable=exe;good.Home=junction;Invalid(()=>RelayMcpBootstrap.ValidateTarget(good,junction,"fictional-account"));}
            finally{if(created){Assert((File.GetAttributes(junction)&FileAttributes.ReparsePoint)!=0,"Owned reparse fixture changed before unlink");Directory.Delete(junction,false);Assert(!Directory.Exists(junction),"Owned junction was not unlinked");}}
        });
    }
}
