using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class HeadlessToolTests
{
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}catch(InvalidDataException){return;}throw new Exception("Expected rejection");}
    private sealed class Fake:IHeadlessRpc
    {
        internal readonly List<string> Methods=new List<string>();
        internal bool Disposed,Interact,Repeated;
        public int ProcessId {get{return 0;}}
        public long ProcessStartedTicks {get{return 0;}}
        public Func<string,object,CancellationToken,Task<object>> RequestHandler {get;set;}
        public event Action<string,object> Notification {add{}remove{}}
        public event Action<HeadlessInteractionException> InteractionRequired {add{}remove{}}
        public Task<object> Call(string method,object args,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Methods.Add(method);
            args=Json.Read<object>(Json.Write(args));
            if(method=="thread/start")return Task.FromResult<object>(new Dictionary<string,object>{{"thread",new Dictionary<string,object>{{"id","owned-ephemeral"}}}});
            if(method=="app/installed")return Task.FromResult(Json.Read<object>("{\"apps\":[{\"id\":\"connected\",\"enabled\":true,\"callable\":true},{\"id\":\"disabled\",\"enabled\":false,\"callable\":true}]}"));
            if(method=="mcpServerStatus/list")return Task.FromResult(Json.Read<object>("{\"data\":[{\"name\":\"codex_apps\",\"tools\":{\"one\":{\"name\":\"pages.read\",\"inputSchema\":{},\"annotations\":{\"readOnlyHint\":true},\"_meta\":{\"connector_id\":\"connected\"}},\"two\":{\"name\":\"disabled.read\",\"inputSchema\":{},\"_meta\":{\"connector_id\":\"disabled\"}}}},{\"name\":\"codex_app\",\"tools\":{\"control\":{\"name\":\"send_message\"}}}],\"nextCursor\":"+(Repeated?"\"again\"":"null")+"}"));
            if(method=="mcpServer/tool/call"){
                Assert(Json.Str(Json.Get(args,"threadId"))=="owned-ephemeral","wrong context");
                if(Interact)throw new HeadlessInteractionException("request","tool/requestUserInput","owned-ephemeral");
                return Task.FromResult(Json.Read<object>("{\"content\":[{\"type\":\"text\",\"text\":\"fixture-only\"}]}"));
            }
            throw new Exception("Unexpected method "+method);
        }
        public void Dispose(){Disposed=true;}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("headless tools use one ephemeral context and no model turns",delegate{
            var fake=new Fake();using(var tools=new ToolTunnelRpc(fake,root)){
                Assert(tools.Inventory(CancellationToken.None).GetAwaiter().GetResult().Single().Name=="pages.read","uncallable or control tool exported");
                tools.Invoke("codex_apps","pages.read",new{},CancellationToken.None).GetAwaiter().GetResult();
            }
            Assert(fake.Disposed&&fake.Methods.Count(m=>m=="thread/start")==1&&!fake.Methods.Contains("turn/start")&&!fake.Methods.Contains("thread/resume"),"model, context or lifetime regression");
        });
        check("headless tool interaction stays explicit instead of auto approval",delegate{
            var fake=new Fake{Interact=true};using(var tools=new ToolTunnelRpc(fake,root)){
                bool refused=false;try{tools.Invoke("codex_apps","pages.read",new{},CancellationToken.None).GetAwaiter().GetResult();}catch(TunnelInteractionException){refused=true;}
                Assert(refused&&fake.RequestHandler==null,"background interaction approved");
            }
        });
        check("headless tool inventory rejects repeated cursors",delegate{var fake=new Fake{Repeated=true};using(var tools=new ToolTunnelRpc(fake,root)){bool refused=false;try{tools.Inventory(CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){refused=true;}Assert(refused,"pagination loop");}});
        check("older instance records receive bounded headless permission defaults",delegate{
            var instance=Json.Read<DesktopInstance>("{\"Id\":\"local\",\"Name\":\"Original\",\"AccountKey\":\"owner\"}");
            Assert(instance.HeadlessEnabled&&instance.HeadlessSandbox=="workspace-write"&&instance.HeadlessApprovalPolicy=="on-request"&&instance.AccountKey=="owner","migration elevated or changed account");
            instance.HeadlessEnabled=false;var restored=Json.Read<DesktopInstance>(Json.Write(instance));Assert(!restored.HeadlessEnabled,"explicit opt out lost");
        });
        check("invalid background permission settings cannot enter the vault",delegate{
            var data=new VaultData();data.Instances.Add(new DesktopInstance{Id="local",Name="Original",HeadlessSandbox="unrestricted-unknown"});Reject(()=>InstanceRules.Normalize(data));
            data.Instances[0].HeadlessSandbox="workspace-write";data.Instances[0].HeadlessApprovalPolicy="auto-accept";Reject(()=>InstanceRules.Normalize(data));
        });
        check("headless metrics prune only exact dead helper records and retain live ownership",delegate{
            var store=new RelayStore(Path.Combine(root,"headless-metrics"));
            using(var process=System.Diagnostics.Process.GetCurrentProcess()){
                var live=new HeadlessProcessRecord{AccountKey="fixture",Home=root,Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks};
                string name="headless-tools-"+live.Pid+"-"+live.Started+".dpapi";
                store.WriteRecord(name,live);
                store.WriteRecord("headless-tools-2147483647-1.dpapi",new HeadlessProcessRecord{AccountKey="fixture",Home=root,Pid=Int32.MaxValue,Started=1});
                store.WriteRecord("headless-tools-unrelated.dpapi",new HeadlessProcessRecord{AccountKey="fixture",Home=root,Pid=Int32.MaxValue,Started=1});
                Assert(HeadlessProcesses.Roots(store,root,"fixture").Single().Pid==process.Id,"live owned helper missing");
                Assert(!File.Exists(Path.Combine(store.Root,"headless-tools-2147483647-1.dpapi"))&&File.Exists(Path.Combine(store.Root,name))&&File.Exists(Path.Combine(store.Root,"headless-tools-unrelated.dpapi")),"cleanup exceeded known exact record or removed live helper");
                Assert(HeadlessProcesses.Roots(store,root,"other").Length==0,"other account acquired helper");
            }
        });
    }
}
