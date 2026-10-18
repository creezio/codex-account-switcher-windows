using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Creezio.Switcher;

internal static class CloudflareTunnelTests
{
    private const string Secret="FAKE_CLOUDFLARE_TOKEN_0123456789_PRIVATE";
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected Cloudflare configuration rejection");}
    private sealed class FakeProcess:ICloudflareTunnelProcess
    {
        internal int Starts,Stops;internal bool Alive,Owner=true,CanStop=true;internal ProcessStartInfo Info;internal Action<string> Output;internal CloudflareTunnelRecord Identity;internal Exception Error;
        public CloudflareTunnelRecord Start(ProcessStartInfo start,Action<string> output)
        {
            Starts++;Info=start;Output=output;if(Error!=null)throw Error;
            Identity=new CloudflareTunnelRecord{Pid=8000+Starts,Started=638000000000000000L+Starts,Executable=start.FileName,Arguments=start.Arguments,OwnerPid=7000,OwnerStarted=637000000000000000L};Alive=true;
            return Json.Read<CloudflareTunnelRecord>(Json.Write(Identity));
        }
        public bool Matches(CloudflareTunnelRecord record){return Alive&&Identity!=null&&record.Pid==Identity.Pid&&record.Started==Identity.Started&&record.Executable==Identity.Executable&&record.Arguments==Identity.Arguments;}
        public bool OwnerAlive(CloudflareTunnelRecord record){return Owner;}
        public bool Stop(CloudflareTunnelRecord record){Assert(Matches(record),"attempted to stop a foreign process");Stops++;if(CanStop)Alive=false;return CanStop;}
    }
    private sealed class Fixture
    {
        internal RelayStore Store;internal readonly FakeProcess Process=new FakeProcess();internal CloudflareTunnelManager Manager;internal DateTime Now=new DateTime(2026,10,17,12,0,0,DateTimeKind.Utc);
        internal Fixture(string root,string name){Store=new RelayStore(Path.Combine(root,"cloudflare-tests",name));Manager=new CloudflareTunnelManager(Store,Process,()=>Now);CloudflareTunnel.SaveConfig(Store,Quick());Store.WriteRecord(CloudflareTunnel.RuntimeRecord,new CloudflareTunnelRecord());}
        internal CloudflareTunnelConfig Quick(){return new CloudflareTunnelConfig{Enabled=true,Mode="quick",Executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"where.exe")};}
        internal CloudflareTunnelConfig Named(){var value=Quick();value.Mode="named";value.Hostname="https://hooks.example.com";value.Token=Secret;return value;}
        internal CloudflareTunnelStatus Tick(){return Manager.Ensure(18721);}
        internal CloudflareTunnelRecord Record(){return Store.ReadRecord<CloudflareTunnelRecord>(CloudflareTunnel.RuntimeRecord);}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("Cloudflare saves config with DPAPI and preserves an unchanged named token",()=>{
            var f=new Fixture(root,"dpapi");CloudflareTunnel.SaveConfig(f.Store,f.Named());var config=CloudflareTunnel.Config(f.Store);Assert(config.Token==Secret,"token did not roundtrip");
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,CloudflareTunnel.ConfigRecord))).Contains(Secret),"plaintext token stored");config.Token=null;CloudflareTunnel.SaveConfig(f.Store,config);Assert(CloudflareTunnel.Config(f.Store).Token==Secret,"null token overwrote saved token");
            config.Token="";Reject(()=>CloudflareTunnel.SaveConfig(f.Store,config));Assert(CloudflareTunnel.Config(f.Store).Token==Secret,"invalid save destroyed previous configuration");
        });
        check("Cloudflare quick mode removes credentials and isolates user configuration",()=>{
            var f=new Fixture(root,"quick-info");var config=f.Quick();config.Token=Secret;CloudflareTunnel.SaveConfig(f.Store,config);f.Tick();
            Assert(!f.Process.Info.Arguments.Contains(Secret)&&!f.Process.Info.EnvironmentVariables.ContainsKey("TUNNEL_TOKEN"),"quick connector inherited a token");
            Assert(f.Process.Info.Arguments.Contains("--url http://127.0.0.1:18721")&&f.Process.Info.Arguments.Contains("--config ")&&f.Process.Info.Arguments.Contains("--no-autoupdate"),"quick connector not isolated/bound to loopback");
            Assert(File.ReadAllText(Path.Combine(f.Store.Root,"cloudflare-webhooks","empty-config.yml"))=="{}\n","nonempty/user config used");
        });
        check("Cloudflare named mode sends token only in child environment",()=>{
            var f=new Fixture(root,"named-info");CloudflareTunnel.SaveConfig(f.Store,f.Named());f.Tick();
            Assert(!f.Process.Info.Arguments.Contains(Secret)&&f.Process.Info.Arguments.EndsWith(" run",StringComparison.Ordinal)&&f.Process.Info.EnvironmentVariables["TUNNEL_TOKEN"]==Secret,"named token exposed or missing");
            Assert(!Json.Write(f.Record()).Contains(Secret)&&!Json.Write(CloudflareTunnel.SafeStatus(f.Record())).Contains(Secret),"token persisted in runtime/status");
        });
        check("Cloudflare strict quick URL accepts only an HTTPS trycloudflare origin",()=>{
            Assert(CloudflareTunnel.QuickUrl("| https://calm-lake-12.trycloudflare.com |") =="https://calm-lake-12.trycloudflare.com","valid quick URL missing");
            Assert(CloudflareTunnel.QuickUrl("{\"message\":\"https://calm-lake-12.trycloudflare.com\"}")=="https://calm-lake-12.trycloudflare.com","JSON quick URL missing");
            foreach(string value in new[]{"http://calm.trycloudflare.com","https://calm.trycloudflare.com.evil.test","https://calm.trycloudflare.com/private","https://calm.trycloudflare.com?token=secret","https://calm.trycloudflare.com:443","https://user@calm.trycloudflare.com","https://calm.trycloudflare.com@evil.test","https://a.b.trycloudflare.com"})Assert(CloudflareTunnel.QuickUrl(value)==null,"unsafe quick URL accepted: "+value);
            Assert(CloudflareTunnel.QuickUrl(new string('x',17000))==null,"oversized output accepted");
        });
        check("Cloudflare validates named HTTPS hostnames",()=>{
            Assert(CloudflareTunnel.NamedUrl("https://Hooks.Example.com/")=="https://hooks.example.com","hostname normalization failed");
            foreach(string value in new[]{"http://example.com","https://example.com/x","https://example.com?token=private","https://x@example.com","https://example.com:444","https://127.0.0.1","https://localhost","https://example.com#token","https://example.com."})Reject(()=>CloudflareTunnel.NamedUrl(value));
        });
        check("Cloudflare rejects malformed mode executable token and port",()=>{
            var f=new Fixture(root,"invalid");var config=f.Quick();config.Mode="shell";Reject(()=>CloudflareTunnel.SaveConfig(f.Store,config));config=f.Quick();config.Executable="cloudflared.exe";Reject(()=>CloudflareTunnel.SaveConfig(f.Store,config));config=f.Named();config.Token="bad\nsecret";Reject(()=>CloudflareTunnel.SaveConfig(f.Store,config));
            Reject(()=>CloudflareTunnel.StartInfo(f.Store,f.Quick(),0));Reject(()=>CloudflareTunnel.StartInfo(f.Store,f.Quick(),65536));Assert(f.Process.Starts==0,"invalid config started a process");
        });
        check("Cloudflare repeated Ensure starts one pinned child",()=>{
            var f=new Fixture(root,"single");var first=f.Tick();var second=f.Tick();Assert(f.Process.Starts==1&&first.Pid==second.Pid&&first.Started==second.Started,"duplicate tunnel process");
            var another=new CloudflareTunnelManager(f.Store,f.Process,()=>f.Now);another.Ensure(18721);Assert(f.Process.Starts==1,"second controller duplicated active owner");
        });
        check("Cloudflare recognizes URL assignment separately from established connection",()=>{
            var f=new Fixture(root,"connection");f.Tick();f.Process.Output("https://calm-lake.trycloudflare.com");var waiting=f.Tick();Assert(waiting.State=="starting"&&waiting.Url=="https://calm-lake.trycloudflare.com","URL assignment reported connected prematurely");
            f.Process.Output("INF Registered tunnel connection connIndex=0");Assert(f.Tick().State=="connected","connection not observed");
        });
        check("Cloudflare named URL appears after connection registration",()=>{
            var f=new Fixture(root,"named-connected");CloudflareTunnel.SaveConfig(f.Store,f.Named());Assert(f.Tick().Url==null,"named endpoint advertised before connection");f.Process.Output("{\"message\":\"Registered tunnel connection\"}");var state=f.Tick();Assert(state.State=="connected"&&state.Url=="https://hooks.example.com","named URL not published after connection");
        });
        check("Cloudflare tracks separate connections and reports loss of the last edge",()=>{
            var f=new Fixture(root,"connections-lost");CloudflareTunnel.SaveConfig(f.Store,f.Named());f.Tick();f.Process.Output("Registered tunnel connection connIndex=0");f.Process.Output("Registered tunnel connection connIndex=1");Assert(f.Tick().State=="connected","registration missing");
            f.Process.Output("Unregistered tunnel connection connIndex=0");Assert(f.Tick().State=="connected","one lost replica marked entire tunnel offline");
            f.Process.Output("{\"message\":\"Unregistered tunnel connection\",\"connIndex\":1}");var state=f.Tick();Assert(state.State=="starting"&&state.Url==null&&state.Error!=null,"last disconnected edge remained connected");
        });
        check("Cloudflare stops and backs off a connector that never establishes an edge",()=>{
            var f=new Fixture(root,"connection-timeout");f.Tick();f.Now=f.Now.AddSeconds(121);var state=f.Tick();Assert(state.State=="retrying"&&f.Process.Stops==1&&f.Process.Starts==1,"startup deadline did not stop only its child");f.Now=f.Now.AddSeconds(6);f.Tick();Assert(f.Process.Starts==2,"startup timeout was not retried after backoff");
        });
        check("Cloudflare token errors are fixed diagnostics without raw output",()=>{
            var f=new Fixture(root,"redact");CloudflareTunnel.SaveConfig(f.Store,f.Named());f.Tick();f.Process.Output("Invalid tunnel token "+Secret+" Authorization Bearer PRIVATE");var state=f.Tick();Assert(state.Error.Contains("jeton")&&!Json.Write(state).Contains(Secret)&&!Json.Write(f.Record()).Contains("PRIVATE"),"raw provider diagnostics persisted");
        });
        check("Cloudflare bounded output queue still retains current useful diagnostics",()=>{
            var f=new Fixture(root,"bounded");f.Tick();for(int i=0;i<500;i++)f.Process.Output("private log "+i);f.Process.Output("https://bounded.trycloudflare.com");f.Process.Output("Registered tunnel connection");var state=f.Tick();Assert(state.State=="connected"&&state.Url=="https://bounded.trycloudflare.com"&&!Json.Write(f.Record()).Contains("private log"),"bounded queue lost valid current lines or persisted raw logs");
        });
        check("Cloudflare restart uses backoff and stops after six failures",()=>{
            var f=new Fixture(root,"backoff");f.Process.Error=new InvalidOperationException(Secret);var state=f.Tick();Assert(state.State=="retrying"&&f.Process.Starts==1&&!state.Error.Contains(Secret),"start error leaked or not retried");
            f.Tick();Assert(f.Process.Starts==1,"backoff ignored");for(int i=1;i<6;i++){f.Now=f.Now.AddMinutes(5);state=f.Tick();}Assert(f.Process.Starts==6&&state.State=="failed","restart budget not bounded");f.Now=f.Now.AddDays(1);f.Tick();Assert(f.Process.Starts==6,"faulted connector restarted indefinitely");
            CloudflareTunnel.SaveConfig(f.Store,f.Quick());f.Tick();Assert(f.Process.Starts==7,"explicit configuration save did not reset restart budget");
        });
        check("Cloudflare changed port replaces only its own child",()=>{
            var f=new Fixture(root,"port-change");f.Tick();f.Manager.Ensure(18722);Assert(f.Process.Starts==2&&f.Process.Stops==1&&f.Process.Info.Arguments.Contains("18722"),"old origin port retained or process duplicated");
        });
        check("Cloudflare recovers its verified orphan after owner exits",()=>{
            var f=new Fixture(root,"orphan");f.Tick();f.Process.Owner=false;f.Tick();Assert(f.Process.Starts==2&&f.Process.Stops==1,"orphan not replaced under exact child pin");
        });
        check("Cloudflare unexpected exit schedules retry without immediate respawn",()=>{
            var f=new Fixture(root,"exit");f.Tick();f.Process.Alive=false;Assert(f.Tick().State=="retrying"&&f.Process.Starts==1,"crashed process immediately looped");f.Now=f.Now.AddSeconds(6);f.Tick();Assert(f.Process.Starts==2,"crashed process did not restart after backoff");
        });
        check("Cloudflare stop uses PID start path and arguments and refuses foreign identity",()=>{
            foreach(string field in new[]{"pid","started","path","arguments"}){
                var f=new Fixture(root,"foreign-"+field);f.Tick();var record=f.Record();if(field=="pid")record.Pid++;else if(field=="started")record.Started++;else if(field=="path")record.Executable+="-other";else record.Arguments+=" --foreign";f.Store.WriteRecord(CloudflareTunnel.RuntimeRecord,record);var state=f.Manager.StopOwned();Assert(f.Process.Stops==0&&f.Process.Alive&&state.Error!=null,"foreign identity was terminated: "+field);
            }
        });
        check("Cloudflare disabling stops a verified child without touching other processes",()=>{
            var f=new Fixture(root,"disable");f.Tick();var config=f.Quick();config.Enabled=false;CloudflareTunnel.SaveConfig(f.Store,config);var state=f.Tick();Assert(f.Process.Stops==1&&state.Pid==0&&state.State=="disabled"&&state.Error==null,"disable failed or reported owned child as foreign");
        });
        check("Cloudflare failure to stop never starts a competing child",()=>{
            var f=new Fixture(root,"stop-refused");f.Tick();f.Process.CanStop=false;var state=f.Manager.Ensure(18722);Assert(state.State=="failed"&&f.Process.Starts==1&&f.Process.Stops==1,"failed stop spawned duplicate child");
        });
    }
}
