using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

// The proxy is exercised through its actual envelope/multiplex/CAS core only.
// No native process, account, pipe, model, or real launch is opened by this suite.
internal static class DesktopGoalProxyTests
{
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static object Shape(object value){return Json.Read<object>(Json.Write(value));}
    private static string Str(object value,string name){return Json.Str(Json.Get(value,name));}
    private static void Reject(Action action,string code="refused")
    {try{action();}catch(DesktopGoalProxyException error){if(error.Code==code)return;throw;}throw new Exception("Expected bounded metadata refusal: "+code);}
    private sealed class Fixture:IDisposable
    {
        internal readonly string Thread=Guid.NewGuid().ToString("D"),Workspace;
        internal readonly List<string> Native=new List<string>(),Desktop=new List<string>();
        internal readonly DesktopGoalMultiplexer Mux;
        internal object Goal;internal bool Respond=true,Revoked,WrongThread,WrongWorkspace;internal int Sets,Readies,Verifies;
        internal Action<string,object> Observe;
        internal Func<string,bool> Suppress;
        internal Fixture(string sandbox)
        {
            Workspace=Path.GetFullPath(sandbox);
            Goal=Shape(new{threadId=Thread,objective="Fictional proxy objective",status="paused",tokenBudget=(long?)1000,tokensUsed=3L,timeUsedSeconds=4L,createdAt=1L,updatedAt=2L});
            Mux=new DesktopGoalMultiplexer(Send,line=>{lock(Desktop)Desktop.Add(line);},()=>{Verifies++;if(Revoked)throw new InvalidOperationException("Fixture revoked");},thread=>Workspace,account=>{Assert(Str(Json.Get(account,"account"),"type")=="chatgpt","Account verification was omitted");},()=>Readies++);
        }
        private void Send(string line)
        {
            lock(Native)Native.Add(line);if(!Respond)return;var request=Json.Read<object>(line);string method=Str(request,"method");object id=Json.Get(request,"id");if(id==null||method.Length==0)return;
            if(Observe!=null)Observe(method,request);
            if(Suppress!=null&&Suppress(method))return;
            object result;
            if(method=="initialize")result=new{};
            else if(method=="account/read")result=new{account=new{type="chatgpt"}};
            else if(method=="thread/read")result=new{thread=new{id=WrongThread?Guid.NewGuid().ToString("D"):Thread,cwd=WrongWorkspace?Path.GetPathRoot(Workspace):Workspace}};
            else if(method=="thread/goal/get")result=new{goal=Goal};
            else if(method=="thread/goal/clear"){Sets++;Goal=null;result=new{};}
            else if(method=="thread/goal/set"){
                Sets++;var values=Json.Obj(Json.Get(request,"params"));var next=Goal==null?new Dictionary<string,object>{{"threadId",Thread},{"objective","new"},{"status","paused"},{"tokenBudget",null},{"tokensUsed",0L},{"timeUsedSeconds",0L},{"createdAt",1L},{"updatedAt",1L}}:Json.Obj(Shape(Goal));
                foreach(string key in new[]{"objective","status","tokenBudget"})if(values.ContainsKey(key))next[key]=values[key];next["updatedAt"]=Convert.ToInt64(next["updatedAt"])+1;Goal=Shape(next);result=new{goal=Goal};
            }else{result=new{unchanged=true};}
            Mux.FromNative(Json.Write(new{id=id,result=result}));
        }
        internal void Ready()
        {Mux.FromDesktop("{\"id\":7,\"method\":\"initialize\",\"params\":{}}").GetAwaiter().GetResult();Mux.FromDesktop("{\"method\":\"initialized\"}").GetAwaiter().GetResult();Assert(Mux.Ready&&Readies==1,"The existing desktop initialization was not observed");}
        internal string Version(){return PortalConversationManagement.NativeGoalVersion(Shape(new{goal=Goal}),Thread);}
        internal object Call(string method,object parameters,string version=null,CancellationToken token=default(CancellationToken))
        {return Mux.Private(method,Shape(parameters),version,token).GetAwaiter().GetResult();}
        internal string[] Methods(){lock(Native)return Native.Select(line=>DesktopGoalEnvelope.Read(line)).Where(e=>e!=null&&e.Method!=null).Select(e=>e.Method).ToArray();}
        public void Dispose(){Mux.Dispose();}
    }
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("desktop goal proxy remaps requests and preserves desktop initialization",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();Assert(f.Desktop.Count==1&&Str(Json.Read<object>(f.Desktop[0]),"method")==""&&Convert.ToInt32(Json.Get(Json.Read<object>(f.Desktop[0]),"id"))==7,"Desktop request ID changed");Assert(f.Native[0].Contains("creezio-metadata-")&&!f.Native[0].Contains("\"id\":7"),"Desktop ID was not reserved safely");Assert(f.Methods().SequenceEqual(new[]{"initialize","initialized"}),"A second initialize or model request was injected");}
        });
        check("desktop goal proxy private endpoint stays unavailable before native initialize",()=>{
            using(var f=new Fixture(sandbox)){Reject(()=>f.Call("thread/goal/get",new{threadId=f.Thread}),"unavailable");Assert(f.Native.Count==0,"Private request initialized another engine");}
        });
        check("desktop goal proxy preserves native callbacks and goal notifications verbatim",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();string callback="{\"method\":\"item/tool/requestUserInput\",\"id\":17,\"params\":{\"text\":\"fictitious\"}}",reply="{\"id\":17,\"result\":{\"ok\":true}}",notification="{\"method\":\"thread/goal/updated\",\"params\":{\"goal\":null}}";f.Mux.FromNative(callback);f.Mux.FromDesktop(reply).GetAwaiter().GetResult();f.Mux.FromNative(notification);Assert(f.Desktop.Contains(callback)&&f.Desktop.Contains(notification)&&f.Native.Contains(reply),"A reverse callback or notification changed");}
        });
        check("desktop goal proxy opaque native frames exceed private JSON limits without truncation",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();f.Respond=false;f.Mux.FromDesktop("{\"method\":\"thread/read\",\"id\":\"desktop-big\",\"params\":{}}").GetAwaiter().GetResult();string reserved=DesktopGoalEnvelope.Read(f.Native.Last()).Id;string suffix=",\"result\":{\"opaque\":\""+new string('x',4194305)+"\\\"id\\\":17\"}}";f.Mux.FromNative("{\"id\":"+reserved+suffix);Assert(f.Desktop.Last()=="{\"id\":\"desktop-big\""+suffix,"Native desktop payload was parsed, capped or changed");}
        });
        check("desktop goal proxy response IDs remain isolated across out of order replies",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();f.Respond=false;f.Mux.FromDesktop("{\"id\":1,\"method\":\"example/a\",\"params\":{}}").GetAwaiter().GetResult();string first=DesktopGoalEnvelope.Read(f.Native.Last()).Id;f.Mux.FromDesktop("{\"id\":\"one\",\"method\":\"example/b\",\"params\":{}}").GetAwaiter().GetResult();string second=DesktopGoalEnvelope.Read(f.Native.Last()).Id;Assert(first!=second,"Reserved IDs collided");f.Mux.FromNative("{\"id\":"+second+",\"result\":2}");f.Mux.FromNative("{\"id\":"+first+",\"result\":1}");Assert(f.Desktop[f.Desktop.Count-2]=="{\"id\":\"one\",\"result\":2}"&&f.Desktop.Last()=="{\"id\":1,\"result\":1}","Out of order response reached the wrong caller");}
        });
        check("desktop goal proxy private reads verify account thread and workspace without model work",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();int desktop=f.Desktop.Count;var result=f.Call("thread/goal/get",new{threadId=f.Thread});Assert(Json.Get(result,"goal")!=null&&f.Desktop.Count==desktop,"Private metadata leaked to the desktop response channel");Assert(f.Methods().Skip(2).SequenceEqual(new[]{"account/read","thread/read","thread/goal/get"}),"Private metadata started a thread, model or second engine");Assert(f.Verifies>=3,"Identity was not rechecked");}
        });
        check("desktop goal proxy rejects methods turns and parameter escalation before native traffic",()=>{
            string thread=Guid.NewGuid().ToString("D");foreach(string method in new[]{"turn/start","thread/resume","thread/start","account/login/start","initialize","plugins/install"})Reject(()=>DesktopGoalMultiplexer.Validate(method,Shape(new{threadId=thread}),null));
            Reject(()=>DesktopGoalMultiplexer.Validate("thread/read",Shape(new{threadId=thread,includeTurns=true}),null));Reject(()=>DesktopGoalMultiplexer.Validate("thread/goal/get",Shape(new{threadId=thread,home=sandbox}),null));Reject(()=>DesktopGoalMultiplexer.Validate("thread/goal/set",Shape(new{threadId=thread,origin="agent",status="paused"}),""));Reject(()=>DesktopGoalMultiplexer.Validate("thread/goal/set",Shape(new{threadId=thread,origin="user",status="budgetLimited"}),""));Reject(()=>DesktopGoalMultiplexer.Validate("thread/goal/set",Shape(new{threadId=thread,origin="user",tokenBudget=1.5}),""));Reject(()=>DesktopGoalMultiplexer.Validate("thread/goal/clear",Shape(new{threadId=thread,origin="user"}),null));
        });
        check("desktop goal proxy rejects native thread and workspace mismatches before goal writes",()=>{
            foreach(bool wrongThread in new[]{true,false})using(var f=new Fixture(sandbox)){f.Ready();f.WrongThread=wrongThread;f.WrongWorkspace=!wrongThread;Reject(()=>f.Call("thread/goal/set",new{threadId=f.Thread,origin="user",status="paused"},f.Version()),wrongThread?"unavailable":"refused");Assert(f.Sets==0,"Unowned metadata mutated a goal");}
        });
        check("desktop goal proxy CAS rejects an independently changed native goal",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();string old=f.Version();var next=Json.Obj(f.Goal);next["updatedAt"]=99L;f.Goal=next;Reject(()=>f.Call("thread/goal/set",new{threadId=f.Thread,origin="user",status="complete"},old),"conflict");Assert(f.Sets==0,"Stale goal version overwrote native state");}
        });
        check("desktop goal proxy serializes GUI goal writes around private compare and set",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();string before=f.Version();Task queued=null;bool injected=false;f.Observe=(method,request)=>{if(method!="thread/goal/get"||injected)return;injected=true;queued=f.Mux.FromDesktop(Json.Write(new{id="gui-goal",method="thread/goal/set",@params=new{threadId=f.Thread,origin="user",status="complete"}}));Assert(!queued.IsCompleted,"GUI goal write entered the private CAS critical section");string callback="{\"method\":\"item/tool/requestUserInput\",\"id\":198,\"params\":{}}",answer="{\"id\":198,\"result\":{\"ok\":true}}";f.Mux.FromNative(callback);var replied=f.Mux.FromDesktop(answer);Assert(replied.IsCompleted&&f.Native.Contains(answer)&&f.Desktop.Contains(callback),"The goal gate blocked the desktop callback path");};f.Call("thread/goal/set",new{threadId=f.Thread,origin="user",status="blocked"},before);Assert(queued!=null&&queued.Wait(3000),"GUI goal write did not resume after private commit");Assert(f.Sets==2&&Str(f.Goal,"status")=="complete","Goal writes interleaved or GUI write was lost");}
        });
        check("desktop goal proxy mutations revalidate ownership and report lost confirmation uncertain",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();f.Observe=(method,request)=>{if(method=="thread/goal/set")f.Revoked=true;};Reject(()=>f.Call("thread/goal/set",new{threadId=f.Thread,origin="user",status="paused"},f.Version()),"uncertain");Assert(f.Sets==1,"Fixture did not reach the owner exactly once");}
            using(var f=new Fixture(sandbox)){f.Ready();f.Revoked=true;int before=f.Native.Count;Reject(()=>f.Call("thread/goal/get",new{threadId=f.Thread}),"unavailable");Assert(f.Native.Count==before,"Revoked owner reached the native server");}
        });
        check("desktop goal proxy late private replies never leak into desktop output",()=>{
            using(var f=new Fixture(sandbox))using(var stop=new CancellationTokenSource()){f.Ready();string late=null;f.Observe=(method,request)=>{if(method=="thread/goal/set"){late=DesktopGoalEnvelope.Read(Json.Write(request)).Id;stop.Cancel();}};int desktop=f.Desktop.Count;Reject(()=>f.Call("thread/goal/set",new{threadId=f.Thread,origin="user",status="paused"},f.Version(),stop.Token),"uncertain");Assert(late!=null,"Cancellation happened before the mutation");f.Mux.FromNative("{\"id\":"+late+",\"result\":{\"goal\":null}}");Assert(f.Desktop.Count==desktop,"Late private response crossed into desktop stdio");}
        });
        check("desktop goal proxy canceled in flight write retains CAS barrier until native completion",()=>{
            using(var f=new Fixture(sandbox))using(var stop=new CancellationTokenSource())using(var secondStop=new CancellationTokenSource(TimeSpan.FromSeconds(3))){
                f.Ready();string version=f.Version(),late=null;f.Observe=(method,request)=>{if(method=="thread/goal/set"&&late==null){late=DesktopGoalEnvelope.Read(Json.Write(request)).Id;stop.Cancel();}};f.Suppress=method=>method=="thread/goal/set";
                Reject(()=>f.Call("thread/goal/set",new{threadId=f.Thread,origin="user",status="blocked"},version,stop.Token),"uncertain");int count=f.Native.Count;
                var second=f.Mux.Private("thread/goal/set",Shape(new{threadId=f.Thread,origin="user",status="complete"}),version,secondStop.Token);Assert(!second.IsCompleted&&f.Native.Count==count,"A second goal write passed the still-running mutation");
                var changed=Json.Obj(f.Goal);changed["status"]="blocked";changed["updatedAt"]=12L;f.Goal=changed;int desktop=f.Desktop.Count;f.Mux.FromNative("{\"id\":"+late+",\"result\":"+Json.Write(new{goal=f.Goal})+"}");Reject(()=>second.GetAwaiter().GetResult(),"conflict");Assert(f.Sets==0&&f.Desktop.Count==desktop,"Canceled mutation response leaked or stale second mutation dispatched");
            }
        });
        check("desktop goal proxy native CLI forwarding preserves quotes trailing slashes and environment",()=>{
            Assert(DesktopGoalProxy.QuoteArgument("")=="\"\""&&DesktopGoalProxy.QuoteArgument("a\\")=="\"a\\\\\""&&DesktopGoalProxy.QuoteArgument("a\"b")=="\"a\\\"b\"","Windows argv quoting changed an argument");
            var start=DesktopGoalProxy.StartInfo(Path.Combine(sandbox,"native.exe"),sandbox,new[]{"plugin","list","--json","C:\\space here\\","a\"b"},false);Assert(start.Arguments.Contains("\"C:\\space here\\\\\"")&&!start.RedirectStandardOutput&&start.EnvironmentVariables["CODEX_CLI_PATH"]==start.FileName&&start.EnvironmentVariables["CODEX_HOME"]==sandbox,"Native CLI was redirected or recursively bridged");Assert(start.EnvironmentVariables.Keys.Cast<string>().All(key=>!key.StartsWith("CREEZIO_DESKTOP_BRIDGE_",StringComparison.OrdinalIgnoreCase)),"Bridge environment leaked into native child");
            Assert(DesktopGoalProxy.AppServer(new[]{"-c","x=true","app-server"})&&DesktopGoalProxy.AppServer(new[]{"app-server","--listen","stdio://"})&&!DesktopGoalProxy.AppServer(new[]{"app-server","--help"})&&!DesktopGoalProxy.AppServer(new[]{"app-server","--listen=wss://elsewhere"})&&!DesktopGoalProxy.AppServer(new[]{"exec","--","app-server"})&&!DesktopGoalProxy.AppServer(new[]{"app-server","generate-json-schema","--out","fixture"})&&!DesktopGoalProxy.AppServer(new[]{"app-server","generate-ts"}),"Non-stdio CLI changed into a second bridge");
        });
        check("desktop goal proxy private framing rejects truncated and oversized input",()=>{
            Assert(DesktopGoalProxy.NumberEquals(123,123)&&DesktopGoalProxy.NumberEquals(123L,123)&&!DesktopGoalProxy.NumberEquals("123",123)&&!DesktopGoalProxy.NumberEquals(true,1)&&!DesktopGoalProxy.NumberEquals(123.5,123),"Process pins accepted coerced or lossy numeric values");
            foreach(int length in new[]{0,-1,DesktopGoalProxy.RequestLimit+1})using(var stream=new MemoryStream(BitConverter.GetBytes(length)))try{DesktopGoalProxy.Receive(stream,DesktopGoalProxy.RequestLimit,CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Oversize frame accepted");}catch(InvalidDataException){}
            using(var stream=new MemoryStream(new byte[]{2,0,0,0,123}))try{DesktopGoalProxy.Receive(stream,DesktopGoalProxy.RequestLimit,CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Truncated frame accepted");}catch(EndOfStreamException){}
        });
        check("desktop goal proxy auxiliary CLI fallback is confined and never starts an unpinned server",()=>{
            string instance=Guid.NewGuid().ToString("N"),root=Path.Combine(sandbox,"proxy-fallback"),home=InstancePaths.Home(root,instance),path=Path.Combine(InstancePaths.Folder(root,instance),"launch.json");
            Assert(DesktopGoalProxy.ResolveLaunchPath(null,home,new[]{"--version"})==path&&DesktopGoalProxy.ResolveLaunchPath(null,home,new[]{"exec","--","app-server"})==path&&DesktopGoalProxy.ResolveLaunchPath(null,home,new[]{"app-server","generate-json-schema"})==path,"Auxiliary CLI did not resolve its exact launch");
            foreach(string bad in new[]{sandbox,Path.Combine(root,"instances",instance,"other-home"),Path.Combine(root,"other",instance,"codex-home")})try{DesktopGoalProxy.ResolveLaunchPath(null,bad,new[]{"--version"});throw new Exception("Unconfined home accepted");}catch(InvalidOperationException){}
            foreach(var args in new[]{new[]{"app-server"},new[]{"-c","x=true","app-server"},new[]{"app-server","--listen=wss://fixture"}})try{DesktopGoalProxy.ResolveLaunchPath(null,home,args);throw new Exception("Server accepted inferred launch proof");}catch(InvalidOperationException){}
            Assert(DesktopGoalProxy.ResolveLaunchPath(path,home,new[]{"app-server"})==path,"Explicit server launch proof changed");
        });
        check("desktop goal proxy disposing owner fails private calls without starting a replacement",()=>{
            using(var f=new Fixture(sandbox)){f.Ready();f.Respond=false;var pending=f.Mux.Private("thread/goal/get",Shape(new{threadId=f.Thread}),null,CancellationToken.None);f.Mux.Dispose();Reject(()=>pending.GetAwaiter().GetResult(),"unavailable");Assert(f.Methods().All(method=>method!="thread/start"&&method!="turn/start"),"Owner loss started replacement work");}
        });
    }
}
