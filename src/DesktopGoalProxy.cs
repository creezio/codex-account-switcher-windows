using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class DesktopGoalProxyState
    {
        public int SchemaVersion {get;set;}
        public string InstanceId {get;set;} public string LaunchId {get;set;}
        public string AccountKey {get;set;} public string Home {get;set;} public string PipeName {get;set;}
        public int ProxyPid {get;set;} public long ProxyStartedTicks {get;set;}
        public int NativePid {get;set;} public long NativeStartedTicks {get;set;}
        public int HostPid {get;set;} public long HostStartedTicks {get;set;}
        public int DesktopPid {get;set;} public long DesktopStartedTicks {get;set;}
        public bool Running {get;set;} public bool Ready {get;set;}
    }
    internal sealed class DesktopGoalProxyException:InvalidOperationException
    {
        internal readonly string Code;
        internal DesktopGoalProxyException(string code):base("Le proprietaire natif a refuse ou interrompu cette operation de metadonnees."){Code=code;}
    }

    // Only the top-level envelope is scanned. Arbitrarily large native payloads
    // remain opaque and retain their bytes, including server callbacks and events.
    internal sealed class DesktopGoalEnvelope
    {
        internal string Method,Id;internal int IdAt,IdLength;internal bool Error;
        internal string ReplaceId(string line,string id){return line.Substring(0,IdAt)+id+line.Substring(IdAt+IdLength);}
        private static void White(string text,ref int at){while(at<text.Length&&Char.IsWhiteSpace(text[at]))at++;}
        private static int StringEnd(string text,int at)
        {for(int i=at+1;i<text.Length;i++){if(text[i]=='\\'){i++;continue;}if(text[i]=='"')return i+1;}throw new InvalidDataException();}
        private static int ValueEnd(string text,int at)
        {
            if(at>=text.Length)throw new InvalidDataException();if(text[at]=='"')return StringEnd(text,at);
            if(text[at]=='{'||text[at]=='['){int depth=0;for(int i=at;i<text.Length;i++){char c=text[i];if(c=='"'){i=StringEnd(text,i)-1;continue;}if(c=='{'||c=='[')depth++;else if(c=='}'||c==']'){if(--depth==0)return i+1;}}throw new InvalidDataException();}
            int end=at;while(end<text.Length&&text[end]!=','&&text[end]!='}'&&!Char.IsWhiteSpace(text[end]))end++;if(end==at)throw new InvalidDataException();return end;
        }
        internal static DesktopGoalEnvelope Read(string line)
        {
            try{int at=0;White(line,ref at);if(at>=line.Length||line[at++]!='{')return null;var result=new DesktopGoalEnvelope();
                for(;;){White(line,ref at);if(at>=line.Length)return null;if(line[at]=='}')return result;if(line[at]!='"')return null;int end=StringEnd(line,at);string key=end-at<=256?Json.Read<string>(line.Substring(at,end-at)):"";at=end;White(line,ref at);if(at>=line.Length||line[at++]!=':')return null;White(line,ref at);int start=at;at=ValueEnd(line,at);
                    if(key=="id"){if(result.Id!=null)return null;result.Id=line.Substring(start,at-start);result.IdAt=start;result.IdLength=at-start;}
                    else if(key=="method"){if(result.Method!=null)return null;result.Method=at-start<=512?Json.Read<string>(line.Substring(start,at-start)):"opaque-native-method";}
                    else if(key=="error")result.Error=true;
                    White(line,ref at);if(at>=line.Length)return null;if(line[at]=='}')return result;if(line[at++]!=',')return null;
                }
            }catch(ArgumentException){return null;}catch(InvalidOperationException){return null;}catch(InvalidDataException){return null;}
        }
    }

    // One child app-server serves both the original desktop stream and this
    // metadata endpoint. The gate includes all explicit desktop goal writes, so
    // a private read/compare/write cannot race another client goal mutation.
    internal sealed class DesktopGoalMultiplexer:IDisposable
    {
        private sealed class Pending
        {
            internal string OriginalId,Method;internal bool Gate;
            internal TaskCompletionSource<object> Completion;
        }
        private readonly Action<string> native,desktop;private readonly Action verify;
        private readonly Func<string,string> workspace;private readonly Action<object> verifyAccount;
        private readonly Action ready;private readonly SemaphoreSlim goalGate=new SemaphoreSlim(1,1);
        private readonly ConcurrentDictionary<string,Pending> pending=new ConcurrentDictionary<string,Pending>();
        private readonly string prefix="creezio-metadata-"+Guid.NewGuid().ToString("N")+"-";
        private long sequence;private int disposed,announced;private volatile bool initializeSucceeded;
        internal bool Ready {get{return Volatile.Read(ref announced)!=0&&Volatile.Read(ref disposed)==0;}}
        internal DesktopGoalMultiplexer(Action<string> native,Action<string> desktop,Action verify,Func<string,string> workspace,Action<object> verifyAccount,Action ready=null)
        {this.native=native;this.desktop=desktop;this.verify=verify;this.workspace=workspace;this.verifyAccount=verifyAccount;this.ready=ready;}
        private string Id(){return Json.Write(prefix+Interlocked.Increment(ref sequence).ToString(CultureInfo.InvariantCulture));}
        private static bool Mutation(string method){return method=="thread/goal/set"||method=="thread/goal/clear";}
        // The desktop completes its handshake on the native initialize reply.
        // Its optional initialized notification is still forwarded verbatim.
        private void Announce(){if(initializeSucceeded&&Interlocked.CompareExchange(ref announced,1,0)==0&&ready!=null)ready();}
        internal async Task FromDesktop(string line)
        {
            if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException("DesktopGoalMultiplexer");var envelope=DesktopGoalEnvelope.Read(line);
            if(envelope==null||envelope.Method==null||envelope.Id==null){native(line);return;}
            bool gate=Mutation(envelope.Method);if(gate){await goalGate.WaitAsync().ConfigureAwait(false);if(Volatile.Read(ref disposed)!=0){goalGate.Release();throw new ObjectDisposedException("DesktopGoalMultiplexer");}}
            string id=Id();var item=new Pending{OriginalId=envelope.Id,Method=envelope.Method,Gate=gate};pending[id]=item;
            try{native(envelope.ReplaceId(line,id));}catch{Pending removed;if(pending.TryRemove(id,out removed)&&removed.Gate)goalGate.Release();throw;}
        }
        internal void FromNative(string line)
        {
            var envelope=DesktopGoalEnvelope.Read(line);Pending item;
            if(envelope==null||envelope.Method!=null||envelope.Id==null){desktop(line);return;}
            if(!pending.TryRemove(envelope.Id,out item)){if(!envelope.Id.StartsWith("\""+prefix,StringComparison.Ordinal))desktop(line);return;}
            if(item.Completion==null){
                try{desktop(envelope.ReplaceId(line,item.OriginalId));if(item.Method=="initialize"&&!envelope.Error){initializeSucceeded=true;Announce();}}
                finally{if(item.Gate)goalGate.Release();}return;
            }
            try{if(line.Length>4194304)throw new InvalidDataException();var response=Json.Read<object>(line);if(Json.Get(response,"error")!=null)throw new DesktopGoalProxyException("refused");item.Completion.TrySetResult(Json.Get(response,"result"));}
            catch(Exception){item.Completion.TrySetException(new DesktopGoalProxyException("unavailable"));}
            finally{if(item.Gate)goalGate.Release();}
        }
        private async Task<object> Rpc(string method,object parameters,CancellationToken token,Action sent=null,bool retainGate=false)
        {
            token.ThrowIfCancellationRequested();if(Volatile.Read(ref disposed)!=0)throw new DesktopGoalProxyException("unavailable");string id=Id();var completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);var entry=new Pending{Method=method,Completion=completion};pending[id]=entry;
            bool attempted=false;
            try{using(token.Register(()=>completion.TrySetCanceled())){token.ThrowIfCancellationRequested();entry.Gate=retainGate;if(sent!=null)sent();attempted=true;native("{\"id\":"+id+",\"method\":"+Json.Write(method)+",\"params\":"+Json.Write(parameters)+"}");return await completion.Task.ConfigureAwait(false);}}
            // A canceled mutation may still be executing in the native server.
            // Its terminal response, not cancellation, releases the write barrier.
            finally{if(!retainGate||!attempted){Pending ignored;pending.TryRemove(id,out ignored);}}
        }
        internal static string Validate(string method,object parameters,string expectedVersion)
        {
            var args=Json.Obj(parameters);string thread=Json.Str(Json.Get(args,"threadId"));Guid parsed;
            if(!Guid.TryParse(thread,out parsed)||thread!=parsed.ToString("D"))throw new DesktopGoalProxyException("refused");
            string[] allowed;
            if(method=="thread/read"){
                allowed=new[]{"threadId","includeTurns"};if(!Object.Equals(Json.Get(args,"includeTurns"),false))throw new DesktopGoalProxyException("refused");
            }else if(method=="thread/goal/get")allowed=new[]{"threadId"};
            else if(method=="thread/goal/clear"){
                allowed=new[]{"threadId","origin"};if(Json.Str(Json.Get(args,"origin"))!="user")throw new DesktopGoalProxyException("refused");
            }else if(method=="thread/goal/set"){
                allowed=new[]{"threadId","origin","objective","status","tokenBudget"};
                if(Json.Str(Json.Get(args,"origin"))!="user"||args.Count<3)throw new DesktopGoalProxyException("refused");
                if(args.ContainsKey("objective")){string text=args["objective"] as string;if(String.IsNullOrWhiteSpace(text)||text.Length>4000||text.Any(c=>Char.IsControl(c)&&c!='\n'&&c!='\r'&&c!='\t'))throw new DesktopGoalProxyException("refused");}
                if(args.ContainsKey("status")&&!new[]{"active","paused","blocked","complete"}.Contains(Json.Str(args["status"])))throw new DesktopGoalProxyException("refused");
                if(args.ContainsKey("tokenBudget")&&args["tokenBudget"]!=null){object number=args["tokenBudget"];decimal value;try{if(number is bool||number is string)throw new FormatException();value=Convert.ToDecimal(number,CultureInfo.InvariantCulture);}catch(Exception){throw new DesktopGoalProxyException("refused");}if(value<1||value>PortalConversationManagement.MaximumTokenBudget||value!=Decimal.Truncate(value))throw new DesktopGoalProxyException("refused");}
            }else throw new DesktopGoalProxyException("refused");
            if(args.Keys.Any(k=>!allowed.Contains(k,StringComparer.Ordinal)))throw new DesktopGoalProxyException("refused");
            if(Mutation(method)){if(expectedVersion==null||!Regex.IsMatch(expectedVersion,@"\A(?:[a-f0-9]{64})?\z"))throw new DesktopGoalProxyException("refused");}
            else if(expectedVersion!=null)throw new DesktopGoalProxyException("refused");
            return thread;
        }
        internal async Task<object> Private(string method,object parameters,string expectedVersion,CancellationToken token)
        {
            string thread=Validate(method,parameters,expectedVersion);bool mutation=Mutation(method),held=false,sent=false;
            try{
                if(!Ready)throw new DesktopGoalProxyException("unavailable");verify();
                if(mutation){await goalGate.WaitAsync(token).ConfigureAwait(false);held=true;verify();}
                verifyAccount(await Rpc("account/read",new{refreshToken=false},token).ConfigureAwait(false));verify();
                string expectedWorkspace=workspace(thread);var metadata=await Rpc("thread/read",new{threadId=thread,includeTurns=false},token).ConfigureAwait(false);DirectMessages.RequireThread(metadata,thread);
                string actual=RolloutSnapshot.NativePath(Json.Str(Json.Get(Json.Get(metadata,"thread"),"cwd")));if(String.IsNullOrEmpty(actual)||!RelayStore.SamePath(expectedWorkspace,actual))throw new DesktopGoalProxyException("refused");verify();
                if(method=="thread/read")return metadata;
                if(mutation){var prior=await Rpc("thread/goal/get",new{threadId=thread},token).ConfigureAwait(false);if(PortalConversationManagement.NativeGoalVersion(prior,thread)!=expectedVersion)throw new DesktopGoalProxyException("conflict");verify();}
                var result=await Rpc(method,parameters,token,mutation?(Action)(()=>{sent=true;held=false;}):null,mutation).ConfigureAwait(false);verify();return result;
            }catch(Exception error){if(sent)throw new DesktopGoalProxyException("uncertain");var known=error as DesktopGoalProxyException;if(known!=null)throw;throw new DesktopGoalProxyException("unavailable");}
            finally{if(held)goalGate.Release();}
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;foreach(var key in pending.Keys){Pending entry;if(!pending.TryRemove(key,out entry))continue;if(entry.Completion!=null)entry.Completion.TrySetException(new DesktopGoalProxyException("unavailable"));if(entry.Gate)goalGate.Release();}
        }
    }

    internal static class DesktopGoalProxy
    {
        internal const string LaunchEnvironment="CREEZIO_DESKTOP_BRIDGE_LAUNCH_PATH";
        internal const int RequestLimit=65536,ResponseLimit=1048576;
        internal static string RecordName(string id){RelayStore.MessageId(id);return "desktop-goal-proxy-"+id+".dpapi";}
        internal static bool NumberEquals(object value,long expected)
        {if(value==null||value is string||value is bool)return false;try{return Convert.ToDecimal(value,CultureInfo.InvariantCulture)==expected;}catch{return false;}}
        public static int Main(string[] args){return Run(args);}
        private static int CommandIndex(string[] args)
        {
            if(args==null||args.Length>512||args.Any(a=>a==null))return -1;
            string[] values={"-c","--config","--enable","--disable","-m","--model","-p","--profile","-s","--sandbox","-a","--ask-for-approval","-C","--cd","--add-dir","--local-provider"};
            string[] flags={"--oss","--search","--full-auto","--dangerously-bypass-approvals-and-sandbox","--no-alt-screen"};
            for(int at=0;at<args.Length;at++){string arg=args[at];if(values.Contains(arg)){if(++at>=args.Length)return -1;continue;}if(flags.Contains(arg)||arg.StartsWith("--",StringComparison.Ordinal)&&arg.Contains("="))continue;return arg.StartsWith("-",StringComparison.Ordinal)?-1:at;}return -1;
        }
        private static bool ServerSession(string[] args)
        {
            int at=CommandIndex(args);if(at<0)return args!=null&&args.Contains("app-server");if(args[at]!="app-server")return false;
            return !args.Skip(at+1).Any(a=>new[]{"--help","-h","--version","-V","generate-ts","generate-json-schema"}.Contains(a));
        }
        internal static bool AppServer(string[] args)
        {
            if(!ServerSession(args))return false;int at=CommandIndex(args);if(at<0)return false;
            for(at++;at<args.Length;at++){string arg=args[at];if(arg=="--listen"){if(++at>=args.Length||args[at]!="stdio://")return false;continue;}if(arg.StartsWith("--listen=",StringComparison.Ordinal)){if(arg!="--listen=stdio://")return false;continue;}if(new[]{"-c","--config","--enable","--disable"}.Contains(arg)){if(++at>=args.Length)return false;continue;}if(!arg.StartsWith("-",StringComparison.Ordinal))return false;}
            return true;
        }
        internal static string ResolveLaunchPath(string explicitPath,string codeHome,string[] args)
        {
            if(!String.IsNullOrWhiteSpace(explicitPath)){if(!Path.IsPathRooted(explicitPath))throw new InvalidOperationException();return Path.GetFullPath(explicitPath);}
            if(ServerSession(args)||String.IsNullOrWhiteSpace(codeHome)||!Path.IsPathRooted(codeHome))throw new InvalidOperationException("Un serveur natif exige la preuve explicite de son lancement.");
            string home=Path.GetFullPath(codeHome).TrimEnd('\\','/'),folder=Path.GetDirectoryName(home),instances=Path.GetDirectoryName(folder),root=Path.GetDirectoryName(instances),id=Path.GetFileName(folder);
            if(!String.Equals(Path.GetFileName(instances),"instances",StringComparison.OrdinalIgnoreCase)||!InstanceRules.ValidId(id)||!Same(home,InstancePaths.Home(root,id)))throw new InvalidOperationException("Le profil CLI ne correspond pas a une instance isolee.");
            SafeFiles.RejectLinks(home);return Path.Combine(folder,"launch.json");
        }
        internal static string QuoteArgument(string value)
        {
            var text=new StringBuilder().Append('"');int slashes=0;foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')text.Append('\\',slashes*2+1).Append(c);else text.Append('\\',slashes).Append(c);slashes=0;}return text.Append('\\',slashes*2).Append('"').ToString();
        }
        internal static ProcessStartInfo StartInfo(string executable,string home,string[] args,bool redirect)
        {
            if(args==null||args.Any(a=>a==null||a.IndexOf('\0')>=0))throw new InvalidOperationException("Arguments CLI invalides.");
            var start=new ProcessStartInfo(executable,String.Join(" ",args.Select(QuoteArgument))){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Environment.CurrentDirectory,RedirectStandardInput=redirect,RedirectStandardOutput=redirect,RedirectStandardError=redirect};
            if(redirect){start.StandardOutputEncoding=Encoding.UTF8;start.StandardErrorEncoding=Encoding.UTF8;}
            foreach(string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray())if(key.StartsWith("CREEZIO_DESKTOP_BRIDGE_",StringComparison.OrdinalIgnoreCase))start.EnvironmentVariables.Remove(key);
            start.EnvironmentVariables["CODEX_CLI_PATH"]=executable;start.EnvironmentVariables["CODEX_HOME"]=home;start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;return start;
        }
        private static T Read<T>(string path)
        {
            using(var stream=AtomicFiles.OpenRead(path,false,true)){if(stream.Length>1048576)throw new InvalidDataException();using(var reader=new StreamReader(stream,new UTF8Encoding(false,true)))return Json.Read<T>(reader.ReadToEnd());}
        }
        private static bool Same(string a,string b){return !String.IsNullOrEmpty(a)&&!String.IsNullOrEmpty(b)&&RelayStore.SamePath(a,b);}
        private static InstanceLaunch Launch(string path)
        {
            SafeFiles.RejectLinks(path);var launch=Read<InstanceLaunch>(path);
            if(launch==null||!InstanceRules.ValidId(launch.InstanceId)||!InstanceRules.ValidId(launch.LaunchId)||!Same(path,Path.Combine(InstancePaths.Folder(launch.Root,launch.InstanceId),"launch.json"))||String.IsNullOrEmpty(launch.AccountKey)||!Path.IsPathRooted(launch.NativeCodexExecutable??"")||!File.Exists(launch.NativeCodexExecutable)||!Path.IsPathRooted(launch.MetadataBridgeExecutable??""))throw new InvalidOperationException("Demande de proxy native invalide.");
            using(var process=Process.GetCurrentProcess())if(!Same(process.MainModule.FileName,launch.MetadataBridgeExecutable)||Same(launch.NativeCodexExecutable,launch.MetadataBridgeExecutable))throw new InvalidOperationException("Identite du proxy native invalide.");
            SafeFiles.RejectLinks(launch.NativeCodexExecutable);return launch;
        }
        internal static int Run(string[] args)
        {
            try{
                string path=ResolveLaunchPath(Environment.GetEnvironmentVariable(LaunchEnvironment),Environment.GetEnvironmentVariable("CODEX_HOME"),args);
                var launch=Launch(path);string home=InstancePaths.Home(launch.Root,launch.InstanceId);HeadlessRpc.ReadProfile(home,launch.AccountKey);
                if(ServerSession(args)&&!AppServer(args))return 2;
                if(!AppServer(args)){using(var process=Process.Start(StartInfo(launch.NativeCodexExecutable,home,args,false))){process.WaitForExit();return process.ExitCode;}}
                using(var host=new Host(path,launch,home))return host.Run(args);
            }catch{return 1;}
        }
        private sealed class Host:IDisposable
        {
            private readonly string path,home;private readonly InstanceLaunch launch;private readonly RelayStore store;
            private readonly DesktopGoalProxyState state;private readonly CancellationTokenSource stop=new CancellationTokenSource();
            private readonly object nativeWrite=new object(),desktopWrite=new object();private readonly ConcurrentDictionary<NamedPipeServerStream,byte> pipes=new ConcurrentDictionary<NamedPipeServerStream,byte>();
            private readonly SemaphoreSlim slots=new SemaphoreSlim(4,4);private Process child;private ProcessJob job;private DesktopGoalMultiplexer mux;private FileStream ownerLease;
            private int publishing,disposed;
            internal Host(string path,InstanceLaunch launch,string home)
            {
                this.path=path;this.launch=launch;this.home=home;store=new RelayStore(Path.Combine(launch.Root,"relay"));
                using(var self=Process.GetCurrentProcess())state=new DesktopGoalProxyState{SchemaVersion=1,InstanceId=launch.InstanceId,LaunchId=launch.LaunchId,AccountKey=launch.AccountKey,Home=home,PipeName="creezio-desktop-goal-"+Guid.NewGuid().ToString("N"),ProxyPid=self.Id,ProxyStartedTicks=self.StartTime.ToUniversalTime().Ticks};
            }
            private InstanceState Runtime(){return Read<InstanceState>(Path.Combine(InstancePaths.Folder(launch.Root,launch.InstanceId),"state.json"));}
            private void Pin()
            {
                var runtime=Runtime();if(runtime.LaunchId!=launch.LaunchId||!runtime.Running||runtime.Phase=="stopping"||runtime.Phase=="error"||runtime.Phase=="unknown"||!DesktopRuntime.SameProcess(runtime.HostPid,runtime.HostStartTicks)||!DesktopRuntime.SameProcess(runtime.DesktopPid,runtime.DesktopStartTicks)||!Descends(state.ProxyPid,runtime.DesktopPid,runtime.DesktopStartTicks)||!Descends(runtime.DesktopPid,runtime.HostPid,runtime.HostStartTicks))throw new InvalidOperationException();
                state.HostPid=runtime.HostPid;state.HostStartedTicks=runtime.HostStartTicks;state.DesktopPid=runtime.DesktopPid;state.DesktopStartedTicks=runtime.DesktopStartTicks;
            }
            private void Verify()
            {
                var current=Read<InstanceLaunch>(path);var runtime=Runtime();
                if(current==null||current.InstanceId!=state.InstanceId||current.LaunchId!=state.LaunchId||current.AccountKey!=state.AccountKey||!Same(current.Root,launch.Root)||!Same(current.NativeCodexExecutable,launch.NativeCodexExecutable)||!Same(current.MetadataBridgeExecutable,launch.MetadataBridgeExecutable)||runtime.LaunchId!=state.LaunchId||!runtime.Running||runtime.Phase=="stopping"||runtime.Phase=="error"||runtime.Phase=="unknown"||runtime.HostPid!=state.HostPid||runtime.HostStartTicks!=state.HostStartedTicks||runtime.DesktopPid!=state.DesktopPid||runtime.DesktopStartTicks!=state.DesktopStartedTicks||!DesktopRuntime.SameProcess(state.HostPid,state.HostStartedTicks)||!DesktopRuntime.SameProcess(state.DesktopPid,state.DesktopStartedTicks)||!DesktopRuntime.SameProcess(state.NativePid,state.NativeStartedTicks))throw new InvalidOperationException();
                var accounts=new AccountService(launch.Root);var instance=NamedInstances.Resolve(accounts,state.InstanceId);if(instance.AccountKey!=state.AccountKey||!Same(accounts.Instances.Home(instance),state.Home))throw new InvalidOperationException();NamedInstances.VerifyAccount(accounts,instance);
            }
            private void Publish()
            {
                if(Interlocked.Exchange(ref publishing,1)!=0)return;
                Task.Run(async()=>{try{for(int i=0;i<50&&!stop.IsCancellationRequested;i++){try{Pin();Verify();state.Running=true;state.Ready=true;store.WriteRecord(RecordName(state.InstanceId),state);await Listen().ConfigureAwait(false);return;}catch(IOException){}catch(InvalidOperationException){}await Task.Delay(100,stop.Token).ConfigureAwait(false);}}catch(OperationCanceledException){}catch{};});
            }
            internal int Run(string[] args)
            {
                Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
                ownerLease=store.Lease("desktop-goal-proxy-"+launch.InstanceId);
                child=new Process{StartInfo=StartInfo(launch.NativeCodexExecutable,home,args,true)};child.Start();job=new ProcessJob(child);state.NativePid=child.Id;state.NativeStartedTicks=child.StartTime.ToUniversalTime().Ticks;
                mux=new DesktopGoalMultiplexer(line=>{lock(nativeWrite){child.StandardInput.WriteLine(line);child.StandardInput.Flush();}},line=>{lock(desktopWrite){Console.Out.WriteLine(line);Console.Out.Flush();}},Verify,thread=>WebhookChats.NativeWorkspace(home,thread),reply=>HeadlessRpc.VerifyAccount(reply,HeadlessRpc.ReadProfile(home,state.AccountKey)),Publish);
                // The native stderr stream belongs to the desktop caller, never to a log.
                var errors=child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
                var output=Task.Run(()=>{try{string line;while((line=child.StandardOutput.ReadLine())!=null)mux.FromNative(line);}finally{mux.Dispose();stop.Cancel();}});
                Task.Run(()=>{try{string line;while((line=Console.In.ReadLine())!=null){var forwarded=mux.FromDesktop(line);forwarded.ContinueWith(task=>{var ignored=task.Exception;},TaskContinuationOptions.OnlyOnFaulted);}}catch{}finally{try{child.StandardInput.Close();}catch{}}});
                child.WaitForExit();try{output.GetAwaiter().GetResult();errors.GetAwaiter().GetResult();}catch{}return child.ExitCode;
            }
            private NamedPipeServerStream Server()
            {
#if NETCOREAPP
                return new NamedPipeServerStream(state.PipeName,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
#else
                var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);using(var identity=WindowsIdentity.GetCurrent())security.AddAccessRule(new PipeAccessRule(identity.User,PipeAccessRights.FullControl,AccessControlType.Allow));
                return new NamedPipeServerStream(state.PipeName,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,0,security);
#endif
            }
            private async Task Listen()
            {
                try{while(!stop.IsCancellationRequested){await slots.WaitAsync(stop.Token).ConfigureAwait(false);NamedPipeServerStream pipe=null;bool handed=false;try{pipe=Server();pipes.TryAdd(pipe,0);using(stop.Token.Register(()=>pipe.Dispose()))await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);var accepted=pipe;handed=true;StartHandler(accepted);}finally{if(!handed){if(pipe!=null){byte unused;pipes.TryRemove(pipe,out unused);pipe.Dispose();}slots.Release();}}}}
                catch(OperationCanceledException){}catch(ObjectDisposedException){}catch(IOException){}
            }
            private void StartHandler(NamedPipeServerStream pipe){Task.Run(()=>Handle(pipe));}
            private async Task Handle(NamedPipeServerStream pipe)
            {
                object response=null;try{using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token)){timeout.CancelAfter(TimeSpan.FromSeconds(25));using(timeout.Token.Register(()=>pipe.Dispose())){
                    var input=await Receive(pipe,RequestLimit,timeout.Token).ConfigureAwait(false);var fields=Json.Obj(input);string method=Json.Str(Json.Get(input,"method"));
                    string[] keys={"instanceId","launchId","accountKey","home","proxyPid","proxyStartedTicks","threadId","method","parameters","expectedVersion"};
                    if(fields.Count<9||fields.Keys.Any(k=>!keys.Contains(k,StringComparer.Ordinal))||Json.Str(Json.Get(input,"instanceId"))!=state.InstanceId||Json.Str(Json.Get(input,"launchId"))!=state.LaunchId||Json.Str(Json.Get(input,"accountKey"))!=state.AccountKey||!Same(Json.Str(Json.Get(input,"home")),state.Home)||!NumberEquals(Json.Get(input,"proxyPid"),state.ProxyPid)||!NumberEquals(Json.Get(input,"proxyStartedTicks"),state.ProxyStartedTicks)||Json.Str(Json.Get(input,"threadId"))!=Json.Str(Json.Get(Json.Get(input,"parameters"),"threadId")))throw new DesktopGoalProxyException("refused");
                    Verify();var result=await mux.Private(method,Json.Get(input,"parameters"),fields.ContainsKey("expectedVersion")?fields["expectedVersion"] as string:null,timeout.Token).ConfigureAwait(false);Verify();response=new{ok=true,result=result};
                }}}
                catch(Exception error){var known=error as DesktopGoalProxyException;response=new{ok=false,error=known==null?"unavailable":known.Code};}
                try{using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2)))await Send(pipe,response,ResponseLimit,timeout.Token).ConfigureAwait(false);}catch{}
                finally{byte unused;pipes.TryRemove(pipe,out unused);pipe.Dispose();slots.Release();}
            }
            public void Dispose()
            {
                if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();foreach(var pipe in pipes.Keys)try{pipe.Dispose();}catch{}if(mux!=null)mux.Dispose();
                try{var current=store.ReadRecord<DesktopGoalProxyState>(RecordName(state.InstanceId));if(current.LaunchId==state.LaunchId&&current.ProxyPid==state.ProxyPid&&current.ProxyStartedTicks==state.ProxyStartedTicks){state.Ready=false;state.Running=false;store.WriteRecord(RecordName(state.InstanceId),state);}}catch{}
                if(job!=null)job.Dispose();if(child!=null)child.Dispose();if(ownerLease!=null)ownerLease.Dispose();
            }
        }
        internal static async Task Exact(Stream pipe,byte[] bytes,CancellationToken token)
        {int at=0;while(at<bytes.Length){int count=await pipe.ReadAsync(bytes,at,bytes.Length-at,token).ConfigureAwait(false);if(count==0)throw new EndOfStreamException();at+=count;}}
        internal static async Task<object> Receive(Stream pipe,int limit,CancellationToken token)
        {var header=new byte[4];await Exact(pipe,header,token).ConfigureAwait(false);int size=BitConverter.ToInt32(header,0);if(size<=0||size>limit)throw new InvalidDataException();var body=new byte[size];await Exact(pipe,body,token).ConfigureAwait(false);return Json.Read<object>(new UTF8Encoding(false,true).GetString(body));}
        internal static async Task Send(Stream pipe,object response,int limit,CancellationToken token)
        {byte[] body=Encoding.UTF8.GetBytes(Json.Write(response));if(body.Length>limit)throw new InvalidDataException();byte[] header=BitConverter.GetBytes(body.Length);await pipe.WriteAsync(header,0,header.Length,token).ConfigureAwait(false);await pipe.WriteAsync(body,0,body.Length,token).ConfigureAwait(false);await pipe.FlushAsync(token).ConfigureAwait(false);}

        [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32First(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32Next(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Entry{public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
        private static bool Descends(int pid,int ancestor,long ticks)
        {
            var parents=new Dictionary<int,int>();IntPtr handle=CreateToolhelp32Snapshot(2,0);if(handle==new IntPtr(-1))return false;
            try{var entry=new Entry{Size=(uint)Marshal.SizeOf(typeof(Entry))};if(Process32First(handle,ref entry))do{parents[(int)entry.Pid]=(int)entry.Parent;}while(Process32Next(handle,ref entry));}finally{CloseHandle(handle);}
            return HeadlessSourceBroker.Descends(pid,ancestor,ticks,value=>{int parent;return parents.TryGetValue(value,out parent)?parent:0;},value=>{using(var process=Process.GetProcessById(value))return process.StartTime.ToUniversalTime().Ticks;});
        }
    }
}
