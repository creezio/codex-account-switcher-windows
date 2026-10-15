using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    // Private, read-only access to an existing owned server. No second Codex engine,
    // arbitrary RPC method, approval or credential ever crosses this endpoint.
    internal sealed class HeadlessSourceBroker:IDisposable
    {
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint id);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint id);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32First(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32Next(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Entry {public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
        private const int RequestLimit=8192,ResponseLimit=4000000;
        private readonly RelayStore store;private readonly HeadlessRuntimeState owner;private readonly Func<RelayChannel,string,CancellationToken,Task<object>> read;
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();private readonly SemaphoreSlim slots=new SemaphoreSlim(4,4);
        private readonly ConcurrentDictionary<NamedPipeServerStream,byte> pipes=new ConcurrentDictionary<NamedPipeServerStream,byte>();private int disposed;
        internal readonly string Name="creezio-headless-"+Guid.NewGuid().ToString("N");
        internal HeadlessSourceBroker(RelayStore store,HeadlessRuntimeState owner,Func<RelayChannel,string,CancellationToken,Task<object>> read)
        {this.store=store;this.owner=owner;this.read=read;Task.Run((Func<Task>)Listen);}
        private static long Started(int pid){using(var process=Process.GetProcessById(pid))return process.StartTime.ToUniversalTime().Ticks;}
        internal static bool Descends(int child,int root,long rootStarted,Func<int,int> parent,Func<int,long> started)
        {
            try{if(child<=0||root<=0||rootStarted<=0||started(root)!=rootStarted)return false;var seen=new HashSet<int>();long younger=DateTime.UtcNow.Ticks;
                for(int depth=0;depth<48&&child>0&&seen.Add(child);depth++){long time=started(child);if(time<rootStarted||time>younger)return false;if(child==root)return time==rootStarted;younger=time;child=parent(child);}return false;
            }catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return false;}
        }
        private static bool Descends(int child,HeadlessRuntimeState runtime)
        {
            var parents=new Dictionary<int,int>();IntPtr handle=CreateToolhelp32Snapshot(2,0);if(handle==new IntPtr(-1))return false;
            try{var entry=new Entry{Size=(uint)Marshal.SizeOf(typeof(Entry))};if(Process32First(handle,ref entry))do{parents[(int)entry.Pid]=(int)entry.Parent;}while(Process32Next(handle,ref entry));}finally{CloseHandle(handle);}
            return Descends(child,runtime.ProcessId,runtime.ProcessStartedTicks,pid=>{int parent;return parents.TryGetValue(pid,out parent)?parent:0;},Started);
        }
        // Lifecycle classification only. This grants no session, RPC or account
        // access; children must not spawn long-lived workers inside the native job.
        internal static bool IsOwnedChild(RelayStore store)
        {
            var runtimes=new List<HeadlessRuntimeState>();
            foreach(string path in Directory.GetFiles(store.Root,"headless-runtime-*.dpapi",SearchOption.TopDirectoryOnly)){
                SafeFiles.RejectLinks(path);var runtime=store.ReadRecord<HeadlessRuntimeState>(Path.GetFileName(path));
                if(!String.IsNullOrEmpty(runtime.AccountKey)&&Path.GetFileName(path)==HeadlessRelayTransport.RuntimeName(runtime.AccountKey))runtimes.Add(runtime);
            }
            using(var current=Process.GetCurrentProcess())return IsOwnedChildCore(runtimes,DesktopRuntime.SameProcess,runtime=>Descends(current.Id,runtime));
        }
        internal static bool IsOwnedChildCore(IEnumerable<HeadlessRuntimeState> runtimes,Func<int,long,bool> alive,Func<HeadlessRuntimeState,bool> descends)
        {
            foreach(var runtime in runtimes){
                if(runtime==null||!runtime.Running||runtime.Revoked||String.IsNullOrEmpty(runtime.AccountKey)||String.IsNullOrEmpty(runtime.RuntimeId)||String.IsNullOrEmpty(runtime.Home)||!Path.IsPathRooted(runtime.Home)||runtime.ProcessId<=0||runtime.ProcessStartedTicks<=0||runtime.WorkerPid<=0||runtime.WorkerStarted<=0)continue;
                try{if(alive(runtime.WorkerPid,runtime.WorkerStarted)&&alive(runtime.ProcessId,runtime.ProcessStartedTicks)&&descends(runtime))return true;}catch(System.ComponentModel.Win32Exception){}catch(ArgumentException){}catch(InvalidOperationException){}
            }return false;
        }
        internal static HeadlessRuntimeState RequireRuntime(RelayStore store,RelayChannel channel,string thread,string expectedRuntime=null)
        {
            HeadlessRelayTransport.Validate(store,channel);var receipt=HeadlessRelayTransport.RequireOwned(store,channel,thread);var runtime=store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(channel.AccountKey));
            if(!runtime.Running||runtime.Revoked||runtime.AccountKey!=channel.AccountKey||String.IsNullOrEmpty(runtime.RuntimeId)||runtime.RuntimeId!=receipt.RuntimeId||(expectedRuntime!=null&&runtime.RuntimeId!=expectedRuntime)||!RelayStore.SamePath(runtime.Home,channel.Home)||!DesktopRuntime.SameProcess(runtime.WorkerPid,runtime.WorkerStarted)||!DesktopRuntime.SameProcess(runtime.ProcessId,runtime.ProcessStartedTicks)||!System.Text.RegularExpressions.Regex.IsMatch(runtime.BrokerPipe??"","^creezio-headless-[a-f0-9]{32}$"))throw new HeadlessRuntimeLostException();
            return runtime;
        }
        internal static HeadlessRuntimeState RequireCaller(RelayStore store,RelayChannel channel,string thread,string expectedRuntime=null)
        {
            // Native app-server may inject its own pipe or environment into MCP
            // children. Those strings neither identify Desktop nor grant authority.
            // The caller must belong to the exact verified native process below.
            var runtime=RequireRuntime(store,channel,thread,expectedRuntime);using(var current=Process.GetCurrentProcess())if(!Descends(current.Id,runtime))throw new InvalidOperationException("Cette commande n'est pas issue du moteur propriétaire de ce chat.");return runtime;
        }
        private NamedPipeServerStream Server()
        {
#if NETCOREAPP
            return new NamedPipeServerStream(Name,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
#else
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);using(var identity=WindowsIdentity.GetCurrent())security.AddAccessRule(new PipeAccessRule(identity.User,PipeAccessRights.FullControl,AccessControlType.Allow));
            return new NamedPipeServerStream(Name,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,0,security);
#endif
        }
        private async Task Listen()
        {
            try{while(!lifetime.IsCancellationRequested){await slots.WaitAsync(lifetime.Token).ConfigureAwait(false);NamedPipeServerStream pipe=null;bool handed=false;
                try{pipe=Server();pipes.TryAdd(pipe,0);using(lifetime.Token.Register(()=>pipe.Dispose()))await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);var accepted=pipe;handed=true;StartHandler(accepted);}
                finally{if(!handed){if(pipe!=null){byte unused;pipes.TryRemove(pipe,out unused);pipe.Dispose();}slots.Release();}}}
            }catch(OperationCanceledException){}catch(ObjectDisposedException){}catch(IOException){}
        }
        private void StartHandler(NamedPipeServerStream pipe){Task.Run(()=>Handle(pipe));}
        private async Task Handle(NamedPipeServerStream pipe)
        {
            try{bool failure=false;try{using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)){timeout.CancelAfter(TimeSpan.FromSeconds(60));using(timeout.Token.Register(()=>pipe.Dispose())){
                uint pid;if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out pid)||!Descends((int)pid,owner))throw new InvalidOperationException("Client local non autorisé.");
                var input=await Receive(pipe,RequestLimit,timeout.Token).ConfigureAwait(false);string channelId=Json.Str(Json.Get(input,"channel")),thread=Json.Str(Json.Get(input,"thread"));var channel=store.Channel(channelId);
                if(Json.Obj(input).Count!=4||Json.Obj(input).Keys.Except(new[]{"channel","thread","runtime","account"},StringComparer.Ordinal).Any())throw new InvalidOperationException("Opération locale non prise en charge.");
                if(Json.Str(Json.Get(input,"runtime"))!=owner.RuntimeId||Json.Str(Json.Get(input,"account"))!=owner.AccountKey||channel.AccountKey!=owner.AccountKey||!RelayStore.SamePath(channel.Home,owner.Home))throw new InvalidOperationException("Source hors du moteur autorisé.");
                RequireRuntime(store,channel,thread,owner.RuntimeId);var result=await read(channel,thread,timeout.Token).ConfigureAwait(false);DirectMessages.RequireThread(result,thread);RequireRuntime(store,channel,thread,owner.RuntimeId);
                await Send(pipe,new{ok=true,result=result},ResponseLimit,timeout.Token).ConfigureAwait(false);
            }}}catch{failure=true;}
            if(failure)try{using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2)))await Send(pipe,new{ok=false,error="La preuve du chat source ou de son moteur n'est plus valide. Aucun autre moteur n'a été démarré."},ResponseLimit,timeout.Token).ConfigureAwait(false);}catch{}
            }
            finally{byte unused;pipes.TryRemove(pipe,out unused);pipe.Dispose();slots.Release();}
        }
        private static async Task Exact(Stream pipe,byte[] value,CancellationToken token)
        {int offset=0;while(offset<value.Length){int count=await pipe.ReadAsync(value,offset,value.Length-offset,token).ConfigureAwait(false);if(count==0)throw new EndOfStreamException();offset+=count;}}
        private static async Task<object> Receive(Stream pipe,int max,CancellationToken token)
        {var header=new byte[4];await Exact(pipe,header,token).ConfigureAwait(false);int size=BitConverter.ToInt32(header,0);if(size<=0||size>max)throw new IOException("Trame locale invalide.");var body=new byte[size];await Exact(pipe,body,token).ConfigureAwait(false);return Json.Read<object>(new UTF8Encoding(false,true).GetString(body));}
        private static async Task Send(Stream pipe,object value,int max,CancellationToken token)
        {byte[] body=Encoding.UTF8.GetBytes(Json.Write(value));if(body.Length>max)throw new IOException("Réponse locale trop volumineuse.");byte[] header=BitConverter.GetBytes(body.Length);await pipe.WriteAsync(header,0,header.Length,token).ConfigureAwait(false);await pipe.WriteAsync(body,0,body.Length,token).ConfigureAwait(false);await pipe.FlushAsync(token).ConfigureAwait(false);}
        internal static async Task<object> Read(RelayStore store,RelayChannel channel,string thread,CancellationToken token)
        {
            var runtime=RequireCaller(store,channel,thread);using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){timeout.CancelAfter(TimeSpan.FromSeconds(65));
                using(var pipe=new NamedPipeClientStream(".",runtime.BrokerPipe,PipeDirection.InOut,PipeOptions.Asynchronous))using(timeout.Token.Register(()=>pipe.Dispose())){
                    await pipe.ConnectAsync(3000,timeout.Token).ConfigureAwait(false);uint pid;if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid)||(int)pid!=runtime.WorkerPid||Started((int)pid)!=runtime.WorkerStarted)throw new InvalidOperationException("Le propriétaire du canal local a changé.");
                    await Send(pipe,new{channel=channel.Id,thread=thread,runtime=runtime.RuntimeId,account=channel.AccountKey},RequestLimit,timeout.Token).ConfigureAwait(false);var response=await Receive(pipe,ResponseLimit,timeout.Token).ConfigureAwait(false);
                    RequireCaller(store,channel,thread,runtime.RuntimeId);if(!Object.Equals(Json.Get(response,"ok"),true))throw new InvalidOperationException("Le moteur propriétaire a refusé la lecture de ce chat.");var result=Json.Get(response,"result");DirectMessages.RequireThread(result,thread);return result;
                }
            }
        }
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;lifetime.Cancel();foreach(var pipe in pipes.Keys)try{pipe.Dispose();}catch(IOException){};}
    }
}
