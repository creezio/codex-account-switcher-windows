using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal interface IHeadlessRpc : IDisposable
    {
        Task<object> Call(string method,object arguments,CancellationToken token);
        Func<string,object,CancellationToken,Task<object>> RequestHandler {get;set;}
        int ProcessId {get;}
        long ProcessStartedTicks {get;}
        event Action<string,object> Notification;
        event Action<HeadlessInteractionException> InteractionRequired;
    }
    internal class HeadlessRpcException : InvalidOperationException
    {
        internal string Code {get;private set;}
        internal string Method {get;private set;}
        internal string Diagnostic {get;private set;}
        internal bool RequestMayHaveBeenSent {get;private set;}
        internal HeadlessRpcException(string code,string method,bool sent,string diagnostic=null):base("Le moteur Codex sans interface a refusé ou interrompu cette opération ("+code+"). Aucun renvoi automatique.")
        {Code=code;Method=method;RequestMayHaveBeenSent=sent;Diagnostic=diagnostic!=null&&Regex.IsMatch(diagnostic,"^[A-Za-z0-9_.:-]{1,120}$")?diagnostic:null;}
    }
    internal sealed class HeadlessInteractionException : HeadlessRpcException
    {
        public string RequestId {get;private set;}
        public string ThreadId {get;private set;}
        internal HeadlessInteractionException(string id,string method,string thread):base("interaction-required",method,true){RequestId=id;ThreadId=thread;}
    }
    internal interface IHeadlessWire : IDisposable
    {
        event Action<string> Message;
        event Action Closed;
        void Send(string value);
    }
    internal interface IHeadlessWireDiagnostic{string FailureCode{get;}}
    internal sealed class HeadlessProcessWire : IHeadlessWire,IHeadlessWireDiagnostic
    {
        internal const int MaxFrame=32*1024*1024;
        private readonly Process process;
        private readonly ProcessJob job;
        private readonly StreamWriter input;
        private readonly IDisposable profileLease;
        private readonly object sync=new object();
        private volatile bool disposed;
        private string failureCode;
        private volatile int nativeExit=Int32.MinValue,errorMarkers;
        private Task errorDrain;
        public string FailureCode{get{
            string code=nativeExit==Int32.MinValue?failureCode:"process-exit-"+nativeExit.ToString(System.Globalization.CultureInfo.InvariantCulture);if(code==null)return null;
            int markers=errorMarkers;if((markers&1)!=0)code+=".stderr-panic-marker";if((markers&2)!=0)code+=".stderr-fatal-marker";if((markers&4)!=0)code+=".stderr-auth-marker";return code;
        }}
        public event Action<string> Message;
        public event Action Closed;
        internal int ProcessId {get{try{return process.Id;}catch{return 0;}}}
        internal long ProcessStartedTicks {get{try{return process.StartTime.ToUniversalTime().Ticks;}catch{return 0;}}}
        internal static ProcessStartInfo StartInfo(string executable,string home)
        {
            var start=new ProcessStartInfo(executable,"-c "+RelayWorker.Quote("cli_auth_credentials_store=\"file\"")+" app-server --listen stdio://"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=home};
            foreach(string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if(key.StartsWith("CODEX_",StringComparison.OrdinalIgnoreCase)||key.StartsWith("OPENAI_",StringComparison.OrdinalIgnoreCase)||key.StartsWith("CHATGPT_",StringComparison.OrdinalIgnoreCase)||key.StartsWith("OAI_",StringComparison.OrdinalIgnoreCase)||new[]{"NODE_OPTIONS","ELECTRON_RUN_AS_NODE","RUST_LOG"}.Contains(key))start.EnvironmentVariables.Remove(key);
            start.EnvironmentVariables["CODEX_HOME"]=home;start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;
            return start;
        }
        internal static StreamWriter Utf8Input(Stream stream){return new StreamWriter(stream,new UTF8Encoding(false)){AutoFlush=true};}
        internal HeadlessProcessWire(string executable,string home,CancellationToken token)
        {
            if(!Path.IsPathRooted(executable)||!File.Exists(executable))throw new HeadlessRpcException("executable",null,false);
            profileLease=AcquireProfileLease(home,token);
            process=new Process{StartInfo=StartInfo(executable,home),EnableRaisingEvents=true};
            process.Exited+=delegate{RecordExit();Task.Run((Func<Task>)FinishExit);};
            try{process.Start();job=new ProcessJob(process);input=Utf8Input(process.StandardInput.BaseStream);errorDrain=Task.Run((Func<Task>)DrainError);Task.Run((Func<Task>)ReadOutput);}
            catch{Dispose();throw new HeadlessRpcException("startup",null,false);}
        }
        internal static IDisposable AcquireProfileLease(string home,CancellationToken token)
        {
            home=Path.GetFullPath(home);SafeFiles.RejectLinks(home);if(!Directory.Exists(home))throw new HeadlessRpcException("profile",null,false);
            string path=Path.Combine(home,".creezio-headless-engine.lock");var end=DateTime.UtcNow.AddMilliseconds(500);
            do{
                token.ThrowIfCancellationRequested();
                try{SafeFiles.RejectLinks(path);return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException error){if((error.HResult&0xffff)!=32&&(error.HResult&0xffff)!=33)throw new HeadlessRpcException("profile",null,false);}
                if(token.WaitHandle.WaitOne(25))token.ThrowIfCancellationRequested();
            }while(DateTime.UtcNow<end);
            throw new HeadlessRpcException("engine-busy",null,false);
        }
        private async Task ReadOutput()
        {
            try{
                var buffer=new char[8192];var frame=new StringBuilder();int count;
                while((count=await process.StandardOutput.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false))>0){
                    for(int i=0;i<count;i++){
                        char value=buffer[i];if(value=='\n'){string line=frame.ToString().TrimEnd('\r');frame.Clear();if(line.Length>0){var handler=Message;if(handler!=null)handler(line);}}
                        else {if(frame.Length>=MaxFrame)throw new IOException();frame.Append(value);}
                    }
                }
            }catch(Exception error){Fail("stdout-"+error.GetType().Name);}
            // EOF can precede the native exit event. Give that exit and its
            // stderr drain a bounded grace period before our own cleanup.
            if(!disposed){try{if(process.WaitForExit(150))RecordExit();}catch{}await DrainGrace().ConfigureAwait(false);Fail("stdout-eof");}
        }
        private async Task DrainError(){try{
            var buffer=new char[4096];int count;string tail="";
            while((count=await process.StandardError.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false))>0){
                string chunk=tail+new string(buffer,0,count);
                if(chunk.IndexOf("panic",StringComparison.OrdinalIgnoreCase)>=0)errorMarkers|=1;
                if(chunk.IndexOf("fatal",StringComparison.OrdinalIgnoreCase)>=0)errorMarkers|=2;
                if(new[]{"unauthorized","unauthenticated","token has expired"}.Any(marker=>chunk.IndexOf(marker,StringComparison.OrdinalIgnoreCase)>=0))errorMarkers|=4;
                // Only marker bits survive. No stderr content is exposed or kept
                // after this small cross-chunk matching window is replaced.
                tail=chunk.Substring(Math.Max(0,chunk.Length-32));
            }
        }catch{}}
        private void RecordExit(){if(disposed)return;try{nativeExit=process.ExitCode;}catch{}}
        private async Task DrainGrace(){var task=errorDrain;if(task!=null)try{await Task.WhenAny(task,Task.Delay(100)).ConfigureAwait(false);}catch{}}
        private async Task FinishExit(){if(disposed)return;await DrainGrace().ConfigureAwait(false);if(!disposed)Fail("process-exit");}
        private void Fail(string code){Interlocked.CompareExchange(ref failureCode,code,null);OnClosed();}
        private void OnClosed(){var handler=Closed;if(handler!=null)handler();}
        public void Send(string value){lock(sync){if(disposed||value.Length>MaxFrame)throw new IOException();input.WriteLine(value);}}
        public void Dispose()
        {
            lock(sync){if(disposed)return;disposed=true;}
            try{if(input!=null)input.Dispose();else process.StandardInput.Close();}catch{}
            try{if(job!=null)job.Dispose();}catch{}
            try{if(!process.WaitForExit(1000)){process.Kill();process.WaitForExit(1000);}}catch{}
            process.Dispose();if(profileLease!=null)profileLease.Dispose();OnClosed();
        }
    }
    // Managed authentication stays in the original profile; no credentials enter RPC bodies.
    internal sealed class HeadlessRpc : IHeadlessRpc
    {
        private readonly IHeadlessWire wire;
        private readonly Func<AuthIdentity> readIdentity;
        private readonly string expectedKey;
        private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        private readonly ConcurrentDictionary<int,TaskCompletionSource<object>> pending=new ConcurrentDictionary<int,TaskCompletionSource<object>>();
        private readonly ConcurrentDictionary<int,string> methods=new ConcurrentDictionary<int,string>();
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private bool initialized;
        private volatile bool disposed;
        private int sequence,requests,disposeStarted;
        private string closeDiagnostic;
        private HeadlessInteractionException interaction;
        internal bool AllowAccountRefresh {get;set;}
        public Func<string,object,CancellationToken,Task<object>> RequestHandler {get;set;}
        public int ProcessId {get{var process=wire as HeadlessProcessWire;return process==null?0:process.ProcessId;}}
        public long ProcessStartedTicks {get{var process=wire as HeadlessProcessWire;return process==null?0:process.ProcessStartedTicks;}}
        public event Action<string,object> Notification;
        public event Action<HeadlessInteractionException> InteractionRequired;
        public HeadlessRpc(string executable,string home,string expectedAccountKey)
            :this(executable,home,expectedAccountKey,CancellationToken.None){}
        public HeadlessRpc(string executable,string home,string expectedAccountKey,CancellationToken token)
            :this(CreateWire(executable,home,expectedAccountKey,token),()=>ReadProfile(home,expectedAccountKey),expectedAccountKey){}
        internal HeadlessRpc(IHeadlessWire wire,Func<AuthIdentity> read,string expectedAccountKey)
        {
            if(wire==null||read==null||String.IsNullOrWhiteSpace(expectedAccountKey))throw new ArgumentException("Association Codex absente.");
            this.wire=wire;readIdentity=read;expectedKey=expectedAccountKey;wire.Message+=Output;wire.Closed+=Closed;
        }
        private static IHeadlessWire CreateWire(string executable,string home,string key,CancellationToken token){ReadProfile(home,key);return new HeadlessProcessWire(executable,Path.GetFullPath(home),token);}
        internal static AuthIdentity ReadProfile(string home,string key)
        {
            try{home=Path.GetFullPath(home);CodexEnvironment.CheckFileStorage(home);var value=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json")));if(String.IsNullOrEmpty(key)||value.Key!=key)throw new InvalidOperationException();return value;}
            catch{throw new HeadlessRpcException("identity",null,false);}
        }
        private AuthIdentity Identity(){try{var value=readIdentity();if(value==null||value.Key!=expectedKey)throw new InvalidOperationException();return value;}catch{throw new HeadlessRpcException("identity",null,false);}}
        internal static void VerifyAccount(object response,AuthIdentity identity)
        {
            var account=Json.Get(response,"account");
            if(Json.Str(Json.Get(account,"type"))!="chatgpt"||!String.Equals(Json.Str(Json.Get(account,"email")),identity.Email,StringComparison.OrdinalIgnoreCase))throw new HeadlessRpcException("server-identity","account/read",false);
            string id=Json.Str(Json.Get(account,"id"));
            if(id.Length>0&&id!=identity.AccountId)throw new HeadlessRpcException("server-identity","account/read",false);
            // Older account/read responses have no workspace ID: the immutable profile
            // identity and its access-token claims remain the workspace binding.
        }
        public async Task<object> Call(string method,object arguments,CancellationToken token)
        {
            if(String.IsNullOrWhiteSpace(method)||method.Length>180||!Regex.IsMatch(method,"^[A-Za-z0-9_/-]+$")||method=="initialize"||method=="initialized")throw new HeadlessRpcException("method",null,false);
            if(method.StartsWith("account/login/",StringComparison.Ordinal)||method=="account/logout")throw new HeadlessRpcException("permanent-account",method,false);
            bool refresh=method=="account/read"&&Object.Equals(Json.Get(Json.Read<object>(Json.Write(arguments)),"refreshToken"),true);
            if(refresh&&!AllowAccountRefresh)throw new HeadlessRpcException("refresh-coordination",method,false);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try{
                if(disposed)throw new ObjectDisposedException("HeadlessRpc");Identity();
                if(!initialized){await Rpc("initialize",new{clientInfo=new{name="creezio_account_switcher",title="Creezio Account Switcher",version=RelayWorker.Version},capabilities=new{experimentalApi=true}},token).ConfigureAwait(false);wire.Send(Json.Write(new{method="initialized"}));initialized=true;}
                var before=Identity();
                if(!refresh){var account=await Rpc("account/read",new{refreshToken=false},token).ConfigureAwait(false);VerifyAccount(account,Identity());}
                if(interaction!=null&&!ReadOnly(method)&&RequestHandler==null)throw interaction;
                var result=await Rpc(method,arguments,token).ConfigureAwait(false);AuthIdentity after;
                try{after=Identity();}catch{throw new HeadlessRpcException(ReadOnly(method)?"identity":"uncertain",method,true);}
                if(before.AccountId!=after.AccountId||before.Key!=after.Key)throw new HeadlessRpcException("identity",method,true);
                if(method=="account/read")VerifyAccount(result,after);
                return result;
            }finally{gate.Release();}
        }
        private async Task<object> Rpc(string method,object arguments,CancellationToken token)
        {
            int id=Interlocked.Increment(ref sequence);bool sent=false;var completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);methods[id]=method;pending[id]=completion;
            try{
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token)){
                    deadline.CancelAfter(TimeSpan.FromSeconds(50));token.ThrowIfCancellationRequested();sent=true;wire.Send(Json.Write(new{id=id,method=method,@params=arguments}));
                    using(deadline.Token.Register(()=>completion.TrySetCanceled()))return await completion.Task.ConfigureAwait(false);
                }
            }catch(OperationCanceledException){Dispose();if(sent&&!ReadOnly(method))throw new HeadlessRpcException("uncertain",method,true);if(token.IsCancellationRequested)throw new OperationCanceledException(token);throw new HeadlessRpcException("timeout",method,sent);}
            catch(HeadlessRpcException error){if(sent&&!ReadOnly(method)&&error.Code=="transport"){Dispose();throw new HeadlessRpcException("uncertain",method,true,error.Diagnostic);}throw;}
            catch(Exception error){Dispose();throw new HeadlessRpcException(sent&&!ReadOnly(method)?"uncertain":"transport",method,sent,"local-"+error.GetType().Name);}
            finally{TaskCompletionSource<object> ignored;pending.TryRemove(id,out ignored);string ignoredMethod;methods.TryRemove(id,out ignoredMethod);}
        }
        private static bool ReadOnly(string method){return method.EndsWith("/read",StringComparison.Ordinal)||method.EndsWith("/list",StringComparison.Ordinal)||new[]{"initialize","app/installed","plugin/installed","skills/list","model/list"}.Contains(method);}
        private void Output(string frame)
        {
            try{
                if(frame.Length>HeadlessProcessWire.MaxFrame)throw new IOException();
                object message;
#if NETCOREAPP
                message=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,object>>(frame,Desktop.JsonCompatibility.Options);
#else
                message=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=HeadlessProcessWire.MaxFrame}.Deserialize<Dictionary<string,object>>(frame);
#endif
                string method=Json.Str(Json.Get(message,"method"));var id=Json.Get(message,"id");
                if(method.Length>0){
                    if(method.Length>180||method.Any(Char.IsControl))throw new IOException();
                    if(id!=null){if(Interlocked.Increment(ref requests)>32)throw new IOException();Task.Run(()=>ServerRequest(id,method,Json.Get(message,"params")));}
                    else {var notify=Notification;if(notify!=null)try{notify(method,Json.Get(message,"params"));}catch{}}
                    return;
                }
                int value;if(id==null||!Int32.TryParse(Convert.ToString(id,System.Globalization.CultureInfo.InvariantCulture),out value))return;
                TaskCompletionSource<object> target;if(!pending.TryGetValue(value,out target))return;
                var error=Json.Get(message,"error");
                if(error!=null){string code=Convert.ToString(Json.Get(error,"code"),System.Globalization.CultureInfo.InvariantCulture);if(!Regex.IsMatch(code??"","^-?[0-9]{1,12}$"))code="unknown";string methodName;methods.TryGetValue(value,out methodName);target.TrySetException(new HeadlessRpcException("rpc-"+code,methodName,true));}
                else if(Json.Obj(message).ContainsKey("result"))target.TrySetResult(Json.Get(message,"result"));else throw new IOException();
            }catch(Exception error){Interlocked.CompareExchange(ref closeDiagnostic,"protocol-"+error.GetType().Name,null);Closed();}
        }
        private async Task ServerRequest(object id,string method,object arguments)
        {
            try{
                string requestId=Convert.ToString(id,System.Globalization.CultureInfo.InvariantCulture);if(requestId==null||requestId.Length>256||requestId.Any(Char.IsControl))throw new IOException();
                string thread=Json.Str(Json.Get(arguments,"threadId"));if(thread.Length>512||thread.Any(Char.IsControl))thread=null;
                var notice=new HeadlessInteractionException(requestId,method,thread);var handler=RequestHandler;
                if(handler==null||method.StartsWith("account/",StringComparison.Ordinal)){
                    interaction=notice;var notify=InteractionRequired;if(notify!=null)try{notify(notice);}catch{}
                    foreach(var target in pending.Values)target.TrySetException(notice);
                    wire.Send(Json.Write(new{id=id,error=new{code=-32000,message="User interaction is required. No approval or credentials were supplied by the background client."}}));
                    return;
                }
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)){
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));Identity();var result=await handler(method,arguments,deadline.Token).ConfigureAwait(false);Identity();wire.Send(Json.Write(new{id=id,result=result}));
                }
            }catch{try{wire.Send(Json.Write(new{id=id,error=new{code=-32000,message="The explicit user response was not available. No approval was supplied."}}));}catch{}}
            finally{Interlocked.Decrement(ref requests);}
        }
        private void FailPending(){var diagnostic=wire as IHeadlessWireDiagnostic;string code=closeDiagnostic??(diagnostic==null?null:diagnostic.FailureCode);foreach(var pair in pending){string method;methods.TryGetValue(pair.Key,out method);pair.Value.TrySetException(new HeadlessRpcException("transport",method,true,code));}}
        private void Closed(){FailPending();Dispose();}
        public void Dispose()
        {
            // The stdout pump, Process.Exited and the caller can all close the
            // connection concurrently. Only one owner may release its resources.
            if(Interlocked.Exchange(ref disposeStarted,1)!=0)return;
            disposed=true;
            try{lifetime.Cancel();wire.Message-=Output;wire.Closed-=Closed;wire.Dispose();}
            finally{FailPending();lifetime.Dispose();}
        }
    }
}
