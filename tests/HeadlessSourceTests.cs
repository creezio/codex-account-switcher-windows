using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class HeadlessSourceTests
{
    private static void Assert(bool value,string why){if(!value)throw new Exception(why);}
    private static void Refuse(Action action){try{action();}catch{return;}throw new Exception("Expected owned source rejection");}
    private sealed class Fixture:IDisposable
    {
        internal readonly HeadlessTransportFixture Inner;internal readonly HeadlessRuntimeState State;internal readonly HeadlessSourceBroker Broker;
        private readonly string oldHome,oldPipe,oldThread;internal int Reads;internal bool WrongNativeThread;
        internal Fixture(string root,int number,string auth)
        {
            Inner=new HeadlessTransportFixture(root,number+30000,auth);using(var process=Process.GetCurrentProcess())State=new HeadlessRuntimeState{AccountKey=Inner.Channel.AccountKey,Home=Inner.Channel.Home,RuntimeId=Guid.NewGuid().ToString("N"),Running=true,WorkerPid=process.Id,WorkerStarted=process.StartTime.ToUniversalTime().Ticks,ProcessId=process.Id,ProcessStartedTicks=process.StartTime.ToUniversalTime().Ticks};
            var receipt=Inner.Own("running");receipt.RuntimeId=State.RuntimeId;Inner.SaveReceipt(receipt);
            Broker=new HeadlessSourceBroker(Inner.Store,State,(channel,thread,token)=>{Interlocked.Increment(ref Reads);return Task.FromResult(HeadlessTransportFixture.Obj(new{thread=new{id=WrongNativeThread?Guid.NewGuid().ToString():thread,status=new{type="active"}},turns=new object[0]}));});State.BrokerPipe=Broker.Name;Save();
            oldHome=Environment.GetEnvironmentVariable("CODEX_HOME");oldPipe=Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");oldThread=Environment.GetEnvironmentVariable("CODEX_THREAD_ID");Environment.SetEnvironmentVariable("CODEX_HOME",Inner.Channel.Home);Environment.SetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH",null);Environment.SetEnvironmentVariable("CODEX_THREAD_ID",Inner.ThreadId);
        }
        internal void Save(){Inner.Store.WriteRecord(HeadlessRelayTransport.RuntimeName(State.AccountKey),State);}
        internal RelaySession Bind(){return RelaySessions.BindHeadless(Inner.Store,Inner.ThreadId,Inner.Channel.Home,CancellationToken.None).GetAwaiter().GetResult();}
        internal RelayMessage Parent()
        {
            Inner.Store.Register(new RelayChannel{Id="origin",Name="Origin",Home=Inner.Channel.Home,Workspace=Inner.Channel.Workspace,AccountKey="origin",Enabled=true});
            var parent=Inner.Store.Enqueue("origin","origin-chat",Inner.Channel.Id,"Source fixture","Bounded fixture",null,false,null);parent.TargetThreadId=Inner.ThreadId;parent.TargetTurnId=Inner.TurnId;parent.State="waiting";parent.SchemaVersion=4;parent.Job=new RelayJobSpec{From="origin",To=Inner.Channel.Id,Project=null,Access="read",Prompt="Bounded fixture",Title="Source fixture",ExplicitDelegation=true};parent.RootJobId=parent.Id;AgentProviders.Pin(parent,Inner.Channel);Inner.Store.Save(parent);return parent;
        }
        public void Dispose(){State.Running=false;Save();Broker.Dispose();Environment.SetEnvironmentVariable("CODEX_HOME",oldHome);Environment.SetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH",oldPipe);Environment.SetEnvironmentVariable("CODEX_THREAD_ID",oldThread);Inner.Dispose();}
    }
    private sealed class RouterAdapter:IRelayTransport
    {
        private readonly Fixture fixture;internal RouterAdapter(Fixture fixture){this.fixture=fixture;}
        public void Verify(RelayChannel c){if(AgentProviders.Kind(c)=="codex-headless")HeadlessRelayTransport.Validate(fixture.Inner.Store,c);}
        public void VerifySend(RelayChannel c,string thread){Verify(c);}
        public Task<object> Call(RelayChannel c,string tool,object args,CancellationToken token){if(c.Id!=fixture.Inner.Channel.Id||tool!="read_thread")throw new InvalidOperationException("Unexpected source fixture call");return HeadlessSourceBroker.Read(fixture.Inner.Store,c,fixture.Inner.ThreadId,token);}
    }
    internal static void RunAll(Action<string,Action> check,string root,string auth)
    {
        int number=0;
        check("headless source ancestry rejects recycled IDs, older children, cycles and unrelated processes",()=>{
            long rootAt=DateTime.UtcNow.AddMinutes(-2).Ticks;var parents=new Dictionary<int,int>{{3,2},{2,1},{1,0},{4,0}};var times=new Dictionary<int,long>{{1,rootAt},{2,rootAt+10},{3,rootAt+20},{4,rootAt+25}};
            Assert(HeadlessSourceBroker.Descends(3,1,rootAt,p=>parents[p],p=>times[p]),"owned ancestry refused");Assert(!HeadlessSourceBroker.Descends(4,1,rootAt,p=>parents[p],p=>times[p]),"unrelated client accepted");
            Assert(!HeadlessSourceBroker.Descends(3,1,rootAt-1,p=>parents[p],p=>times[p]),"recycled root accepted");times[2]=rootAt+30;Assert(!HeadlessSourceBroker.Descends(3,1,rootAt,p=>parents[p],p=>times[p]),"recycled intermediate parent accepted");times[2]=rootAt+10;parents[2]=3;Assert(!HeadlessSourceBroker.Descends(3,1,rootAt,p=>parents[p],p=>times[p]),"parent cycle accepted");
        });
        check("headless source binds through an existing private read-only broker without a shell or new engine",()=>{
            using(var f=new Fixture(root,++number,auth)){var session=f.Bind();Assert(session.Thread==f.Inner.ThreadId&&session.Channel==f.Inner.Channel.Id&&session.Account==f.Inner.Channel.AccountKey&&session.RuntimeId==f.State.RuntimeId&&f.Reads==1,"source identity not pinned");Assert(RelaySessions.Resolve(f.Inner.Store,session.Token,f.Inner.Channel.Home).RuntimeId==f.State.RuntimeId,"live session lost");Assert(f.Inner.Connections.Count==0,"binding started a second engine");var raw=System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Inner.Store.Root,"session-"+session.Token+".dpapi")));Assert(!raw.Contains(session.Token)&&!raw.Contains(f.Inner.ThreadId),"session was stored in plaintext");}
        });
        check("headless source rejects foreign chats and a mismatched native read before minting a session",()=>{
            using(var f=new Fixture(root,++number,auth)){Refuse(()=>RelaySessions.BindHeadless(f.Inner.Store,Guid.NewGuid().ToString(),f.Inner.Channel.Home,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Reads==0,"foreign chat reached native source");f.WrongNativeThread=true;Refuse(()=>f.Bind());Assert(Directory.GetFiles(f.Inner.Store.Root,"session-*.dpapi").Length==0,"mismatched native response minted session");}
        });
        check("headless source scope requires exact account, runtime, home and process start times",()=>{
            foreach(string changed in new[]{"account","runtime","home","worker-start","native-start","broker"})using(var f=new Fixture(root,++number,auth)){
                if(changed=="account"){var receipt=f.Inner.Receipt();receipt.AccountKey="other-account";f.Inner.SaveReceipt(receipt);}else if(changed=="runtime")f.State.RuntimeId=Guid.NewGuid().ToString("N");else if(changed=="home")f.State.Home=root;else if(changed=="worker-start")f.State.WorkerStarted++;else if(changed=="native-start")f.State.ProcessStartedTicks++;else f.State.BrokerPipe="codex-browser-use-fake";f.Save();Refuse(()=>f.Bind());Assert(f.Reads==0,"invalid source reached native read");
            }
        });
        check("headless source sessions stop authorizing operations after runtime termination or replacement",()=>{
            using(var f=new Fixture(root,++number,auth)){var session=f.Bind();f.State.Running=false;f.Save();Refuse(()=>RelaySessions.Resolve(f.Inner.Store,session.Token,f.Inner.Channel.Home));f.State.Running=true;f.State.RuntimeId=Guid.NewGuid().ToString("N");f.Save();Refuse(()=>RelaySessions.Resolve(f.Inner.Store,session.Token,f.Inner.Channel.Home));}
        });
        check("headless source identity cannot be changed by inherited pipe or home environment strings",()=>{
            foreach(string changed in new[]{"pipe","profile"})using(var f=new Fixture(root,++number,auth)){
                if(changed=="pipe")Environment.SetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH","fake-desktop");else Environment.SetEnvironmentVariable("CODEX_HOME",root);var bound=f.Bind();Assert(bound.Home==f.Inner.Channel.Home&&bound.Account==f.Inner.Channel.AccountKey,"environment selected a different account");Refuse(()=>RelaySessions.BindHeadless(f.Inner.Store,f.Inner.ThreadId,root,CancellationToken.None).GetAwaiter().GetResult());Refuse(()=>RelaySessions.Resolve(f.Inner.Store,bound.Token,root));
            }
            using(var f=new Fixture(root,++number,auth)){File.WriteAllText(Path.Combine(f.Inner.Channel.Home,"auth.json"),"{}");Refuse(()=>f.Bind());Assert(f.Reads==0,"revoked account reached native read");}
        });
        check("headless recipient stores a structured result only for its actual waiting mission",()=>{
            using(var f=new Fixture(root,++number,auth)){var parent=f.Parent();var session=f.Bind();RelaySessions.Authorize(f.Inner.Store,session,parent.Id,false);f.Inner.Store.Report(parent,session,"succeeded","Verified source fixture",new Dictionary<string,string>());Assert(f.Inner.Store.Reported(parent).ThreadId==f.Inner.ThreadId&&f.Inner.Store.Message(parent.Id).State=="waiting","result changed execution state or lost recipient");var foreign=new RelaySession{Channel=session.Channel,Thread=Guid.NewGuid().ToString()};Refuse(()=>f.Inner.Store.Report(parent,foreign,"succeeded","forged",new Dictionary<string,string>()));}
        });
        check("headless parent submits a bounded child through its owned source broker without new inference",()=>{
            using(var f=new Fixture(root,++number,auth)){var parent=f.Parent();var session=f.Bind();f.Inner.Store.Register(new RelayChannel{Id="child-target",Home=f.Inner.Channel.Home,Workspace=f.Inner.Channel.Workspace,AccountKey="child-account",Enabled=true});var spec=new RelayJobSpec{From=session.Channel,To="child-target",Title="Child fixture",Prompt="Bounded child",Access="read",ExplicitDelegation=true,Parent=parent.Id,ReturnToSource=false};var child=new RelayRouter(f.Inner.Store,new RouterAdapter(f),a=>100).Submit(spec,session.Thread,CancellationToken.None).GetAwaiter().GetResult();Assert(child.SourceThreadId==f.Inner.ThreadId&&child.Depth==1&&child.RootJobId==parent.Id&&child.State=="queued"&&f.Reads==2&&f.Inner.Connections.Count==0,"child bypassed source proof or started inference");}
        });
        check("headless source broker disposal closes its private endpoint",()=>{
            using(var f=new Fixture(root,++number,auth)){f.Bind();f.Broker.Dispose();Thread.Sleep(20);using(var pipe=new NamedPipeClientStream(".",f.Broker.Name,PipeDirection.InOut))Refuse(()=>pipe.Connect(100));}
        });
        check("headless file approvals require exact native thread turn and item details before accepting",()=>{
            using(var f=new Fixture(root,++number,auth))using(var cancel=new CancellationTokenSource()){
                var a=new HeadlessApprovals(f.Inner.Store);var args=HeadlessTransportFixture.Obj(new{threadId=f.Inner.ThreadId,turnId=f.Inner.TurnId,itemId="fixture-file-item",reason="Bounded fixture",cwd=f.Inner.Channel.Workspace});var waiting=a.Wait(f.Inner.Channel,"item/fileChange/requestApproval",args,cancel.Token,f.State.RuntimeId);var request=a.Pending().Single();
                Refuse(()=>a.Respond(request.Id,"accept"));Assert(!waiting.IsCompleted&&!request.DetailsVerified,"opaque file approval was auto-granted");var item=HeadlessTransportFixture.Obj(new{type="fileChange",id="fixture-file-item",changes=new[]{new{path=Path.Combine(f.Inner.Channel.Workspace,"fixture.txt"),kind=new{type="add"},diff="+api_key=PRIVATE_FILE_CHANGE_SECRET"}}});
                a.ObserveFileChange(Guid.NewGuid().ToString(),f.Inner.TurnId,item);a.ObserveFileChange(f.Inner.ThreadId,Guid.NewGuid().ToString(),item);a.ObserveFileChange(f.Inner.ThreadId,f.Inner.TurnId,HeadlessTransportFixture.Obj(new{type="fileChange",id="other-item",changes=Json.Get(item,"changes")}));Assert(!a.Pending().Single().DetailsVerified,"foreign item details authorized a different change");
                a.ObserveFileChange(f.Inner.ThreadId,f.Inner.TurnId,item);request=a.Pending().Single();Assert(request.DetailsVerified&&request.Summary.Contains("fixture.txt")&&!request.Summary.Contains("PRIVATE_FILE_CHANGE_SECRET")&&request.Summary.Contains("[masqué]"),"native details missing or credential leaked");a.Respond(request.Id,"accept");Assert(Json.Str(Json.Get(waiting.GetAwaiter().GetResult(),"decision"))=="accept","explicit native decision lost");
            }
        });
    }
}
