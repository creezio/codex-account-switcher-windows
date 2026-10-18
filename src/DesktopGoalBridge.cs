using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    // A client of the shim already owned by this desktop launch. This class
    // never starts a process, changes a profile or falls back to another server.
    internal sealed class DesktopGoalBridge : IThreadProfileRpc
    {
        internal const string RestartRequired="Cette instance Codex a ete ouverte sans le pont d'objectifs. Relancez-la une fois explicitement depuis Switcher pour gerer son objectif sans fermer les autres instances.";
        private const string Unavailable="Le pont d'objectifs de cette instance n'est plus joignable ou son proprietaire a change. Actualisez son etat ; aucune nouvelle connexion native n'a ete demarree.";
        private readonly DesktopGoalProxyState pin;
        private readonly string thread;
        private readonly Action verify;
        private readonly Func<object,CancellationToken,Task<object>> exchange;
        private string expectedVersion;
        private bool disposed;

        private sealed class Context
        {
            internal DesktopGoalProxyState State;
            internal string ProxyExecutable;
        }

        internal DesktopGoalBridge(DesktopGoalProxyState state,string threadId,Action validate,Func<object,CancellationToken,Task<object>> transport)
        {pin=Json.Read<DesktopGoalProxyState>(Json.Write(state));thread=threadId;verify=validate;exchange=transport;}

        internal static bool SameOwner(DesktopGoalProxyState first,DesktopGoalProxyState second)
        {
            return first!=null&&second!=null&&first.SchemaVersion==1&&second.SchemaVersion==1&&first.Ready&&second.Ready&&first.Running&&second.Running
                &&first.InstanceId==second.InstanceId&&first.LaunchId==second.LaunchId&&first.AccountKey==second.AccountKey&&Same(first.Home,second.Home)&&first.PipeName==second.PipeName
                &&first.ProxyPid==second.ProxyPid&&first.ProxyStartedTicks==second.ProxyStartedTicks&&first.NativePid==second.NativePid&&first.NativeStartedTicks==second.NativeStartedTicks
                &&first.HostPid==second.HostPid&&first.HostStartedTicks==second.HostStartedTicks&&first.DesktopPid==second.DesktopPid&&first.DesktopStartedTicks==second.DesktopStartedTicks;
        }
        private static bool Same(string first,string second)
        {return !String.IsNullOrEmpty(first)&&!String.IsNullOrEmpty(second)&&RelayStore.SamePath(first,second);}

        private static void ProcessOwner(int pid,long started,string executable=null)
        {
            if(pid<=0||started<=0||!DesktopRuntime.SameProcess(pid,started))throw new InvalidOperationException(Unavailable);
            if(executable!=null)using(var process=Process.GetProcessById(pid))if(!Same(process.MainModule.FileName,executable))throw new InvalidOperationException(Unavailable);
        }

        private static Context Capture(RelayStore store,AccountService accounts,DesktopInstance instance,InstanceState state)
        {
            if(instance.IsLocal||state==null||!state.Running||state.Phase=="unknown"||state.Phase=="starting"||state.Phase=="stopping"||String.IsNullOrEmpty(state.LaunchId))throw new InvalidOperationException(RestartRequired);
            NamedInstances.VerifyAccount(accounts,instance);
            string root=Path.GetDirectoryName(store.Root),folder=InstancePaths.Folder(root,instance.Id),launchPath=Path.Combine(folder,"launch.json");
            SafeFiles.RejectLinks(launchPath);
            if(!File.Exists(launchPath)||new FileInfo(launchPath).Length>65536)throw new InvalidOperationException(RestartRequired);
            var launch=Json.Read<InstanceLaunch>(SafeFiles.ReadText(launchPath));
            if(launch==null||launch.InstanceId!=instance.Id||launch.LaunchId!=state.LaunchId||launch.AccountKey!=instance.AccountKey||!Same(launch.Root,root)||String.IsNullOrWhiteSpace(launch.MetadataBridgeExecutable)||String.IsNullOrWhiteSpace(launch.NativeCodexExecutable))throw new InvalidOperationException(RestartRequired);
            var record=store.ReadRecord<DesktopGoalProxyState>(DesktopGoalProxy.RecordName(instance.Id));
            if(record.SchemaVersion!=1||!record.Running||!record.Ready||record.InstanceId!=instance.Id||record.LaunchId!=launch.LaunchId||record.AccountKey!=instance.AccountKey||!Same(record.Home,accounts.Instances.Home(instance))
                ||record.DesktopPid!=state.DesktopPid||record.DesktopStartedTicks!=state.DesktopStartTicks||record.HostPid!=state.HostPid||record.HostStartedTicks!=state.HostStartTicks
                ||!Regex.IsMatch(record.PipeName??"",@"\Acreezio-desktop-goal-[a-f0-9]{32}\z"))throw new InvalidOperationException(Unavailable);
            ProcessOwner(record.DesktopPid,record.DesktopStartedTicks,launch.DesktopExecutable);
            ProcessOwner(record.HostPid,record.HostStartedTicks);
            ProcessOwner(record.ProxyPid,record.ProxyStartedTicks,launch.MetadataBridgeExecutable);
            ProcessOwner(record.NativePid,record.NativeStartedTicks,launch.NativeCodexExecutable);
            return new Context{State=record,ProxyExecutable=launch.MetadataBridgeExecutable};
        }

        internal static string Issue(RelayStore store,AccountService accounts,DesktopInstance instance,InstanceState state)
        {
            try{Capture(store,accounts,instance,state);return null;}
            catch(IOException){return Unavailable;}catch(InvalidOperationException error){return error.Message==RestartRequired?RestartRequired:Unavailable;}
            catch(Win32Exception){return Unavailable;}catch(UnauthorizedAccessException){return Unavailable;}catch(ArgumentException){return Unavailable;}
        }

        internal static IThreadProfileRpc Open(RelayStore store,PortalConversationTarget target,Action authorize)
        {
            string root=Path.GetDirectoryName(store.Root);var accounts=new AccountService(root);var instance=NamedInstances.Resolve(accounts,target.InstanceId);
            var context=Capture(store,accounts,instance,accounts.Instances.Runtime.Probe(instance));
            Action validate=()=>{
                authorize();var currentAccounts=new AccountService(root);var selected=NamedInstances.Resolve(currentAccounts,target.InstanceId);
                if(selected.AccountKey!=target.AccountKey||!Same(currentAccounts.Instances.Home(selected),target.Home))throw new InvalidOperationException(Unavailable);
                var current=Capture(store,currentAccounts,selected,currentAccounts.Instances.Runtime.Probe(selected));
                if(!SameOwner(context.State,current.State)||!Same(context.ProxyExecutable,current.ProxyExecutable))throw new InvalidOperationException(Unavailable);
            };
            validate();
            return new DesktopGoalBridge(context.State,target.ThreadId,validate,(request,token)=>Exchange(context,request,token));
        }

        private static Dictionary<string,object> Parameters(string method,object parameters,string expectedThread)
        {
            var body=Json.Obj(Json.Read<object>(Json.Write(parameters)));
            string[] allowed;
            if(method=="thread/read")allowed=new[]{"threadId","includeTurns"};
            else if(method=="thread/goal/get")allowed=new[]{"threadId"};
            else if(method=="thread/goal/set")allowed=new[]{"threadId","origin","objective","status","tokenBudget"};
            else if(method=="thread/goal/clear")allowed=new[]{"threadId","origin"};
            else throw new InvalidOperationException("Ce pont accepte uniquement la lecture et la gestion des objectifs natifs.");
            if(body.Keys.Any(key=>!allowed.Contains(key))||Json.Str(Json.Get(body,"threadId"))!=expectedThread)throw new InvalidOperationException("La demande d'objectif ne correspond pas a la discussion verifiee.");
            if(method=="thread/read"&&!Object.Equals(Json.Get(body,"includeTurns"),false))throw new InvalidOperationException("Ce pont ne lit pas le contenu des tours.");
            if((method=="thread/goal/set"||method=="thread/goal/clear")&&Json.Str(Json.Get(body,"origin"))!="user")throw new InvalidOperationException("La mutation d'objectif exige une intention utilisateur explicite.");
            return body;
        }

        public async Task<object> Call(string method,object parameters,CancellationToken token)
        {
            if(disposed)throw new ObjectDisposedException("DesktopGoalBridge");token.ThrowIfCancellationRequested();
            var body=Parameters(method,parameters,thread);bool mutation=method=="thread/goal/set"||method=="thread/goal/clear";
            if(mutation&&expectedVersion==null)throw new InvalidOperationException("Relisez l'objectif natif avant de le modifier.");
            verify();
            var request=new Dictionary<string,object>{{"instanceId",pin.InstanceId},{"launchId",pin.LaunchId},{"accountKey",pin.AccountKey},{"home",pin.Home},{"proxyPid",pin.ProxyPid},{"proxyStartedTicks",pin.ProxyStartedTicks},{"threadId",thread},{"method",method},{"parameters",body}};
            if(mutation)request["expectedVersion"]=expectedVersion;
            var response=await exchange(request,token).ConfigureAwait(false);
            if(!Object.Equals(Json.Get(response,"ok"),true)){
                string code=Json.Str(Json.Get(response,"error"));
                if(code=="conflict")throw new WebhookException(409,"L'objectif a change dans Codex. Actualisez avant de l'enregistrer.");
                if(code=="uncertain")throw new IOException("La reponse native de modification d'objectif est incertaine. Relisez son etat ; cette demande ne sera pas renvoyee automatiquement.");
                throw new InvalidOperationException(Unavailable);
            }
            verify();var result=Json.Get(response,"result");
            if(method=="thread/goal/get")expectedVersion=PortalConversationManagement.NativeGoalVersion(result,thread);
            return result;
        }

        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
        private static async Task Exact(Stream stream,byte[] bytes,CancellationToken token)
        {int offset=0;while(offset<bytes.Length){int count=await stream.ReadAsync(bytes,offset,bytes.Length-offset,token).ConfigureAwait(false);if(count==0)throw new EndOfStreamException("Le pont d'objectifs s'est ferme.");offset+=count;}}
        private static async Task<object> Exchange(Context context,object request,CancellationToken token)
        {
            using(var pipe=new NamedPipeClientStream(".",context.State.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous))
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                deadline.CancelAfter(TimeSpan.FromSeconds(15));deadline.Token.ThrowIfCancellationRequested();pipe.Connect(2000);
                uint owner;if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out owner)||owner!=(uint)context.State.ProxyPid)throw new InvalidOperationException(Unavailable);
                ProcessOwner(context.State.ProxyPid,context.State.ProxyStartedTicks,context.ProxyExecutable);
                using(deadline.Token.Register(()=>pipe.Dispose())){
                    byte[] bytes=Encoding.UTF8.GetBytes(Json.Write(request));if(bytes.Length>65536)throw new InvalidOperationException("Demande d'objectif trop volumineuse.");
                    byte[] header=BitConverter.GetBytes(bytes.Length);await pipe.WriteAsync(header,0,header.Length,deadline.Token).ConfigureAwait(false);await pipe.WriteAsync(bytes,0,bytes.Length,deadline.Token).ConfigureAwait(false);await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                    await Exact(pipe,header,deadline.Token).ConfigureAwait(false);int length=BitConverter.ToInt32(header,0);if(length<=0||length>1048576)throw new IOException("Reponse d'objectif native trop volumineuse ou invalide.");
                    bytes=new byte[length];await Exact(pipe,bytes,deadline.Token).ConfigureAwait(false);return Json.Read<object>(new UTF8Encoding(false,true).GetString(bytes));
                }
            }
        }
        public void Dispose(){disposed=true;}
    }
}
