using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class RpcClient : IDisposable
    {
        private Process process;
        private ProcessJob job;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<object>> pending = new ConcurrentDictionary<int, TaskCompletionSource<object>>();
        private readonly TaskCompletionSource<object> login = new TaskCompletionSource<object>();
        private readonly ConcurrentDictionary<string,TaskCompletionSource<object>> correlatedLogins=new ConcurrentDictionary<string,TaskCompletionSource<object>>();
        private readonly ConcurrentDictionary<string,object> completedLogins=new ConcurrentDictionary<string,object>();
        private readonly object writeLock = new object();
        private int sequence;
        private bool disposed;
        public readonly string Home;
        private readonly string scratchRoot;
        private readonly OwnedTemporary temporary;
        private readonly IDisposable profileLease;
        internal int ProcessId{get{try{return process==null?0:process.Id;}catch(InvalidOperationException){return 0;}}}
        internal long ProcessStartedTicks{get{try{return process==null?0:process.StartTime.ToUniversalTime().Ticks;}catch(InvalidOperationException){return 0;}}}
        public RpcClient(string executable, string root):this(executable,root,null){}
        internal static RpcClient OpenEnrollment(string executable,string root,string home)
        {
            home=Path.GetFullPath(home);string folder=Path.GetDirectoryName(home),id=Path.GetFileName(folder);
            if(!InstanceRules.ValidId(id)||!RelayStore.SamePath(home,InstancePaths.Home(root,id))||File.Exists(Path.Combine(home,"auth.json")))throw new InvalidOperationException("La connexion nécessite un nouveau profil d'instance réservé.");
            return new RpcClient(executable,root,home);
        }
        internal static ProcessStartInfo EnrollmentStartInfo(string executable,string home)
        {
            // This fresh profile only authenticates. Plugin discovery can clone
            // marketplaces during initialize, even without a model turn. Keep
            // those features off for this process, never in the saved profile.
            var start=HeadlessProcessWire.StartInfo(executable,home);
            string[] disabled={"plugins","remote_plugin","apps","skill_mcp_dependency_install","skill_search"};
            start.Arguments=String.Join(" ",disabled.Select(feature=>"-c "+RelayWorker.Quote("features."+feature+"=false")))+" -c "+RelayWorker.Quote("features.skip_host_skill_discovery=true")+" "+start.Arguments;
            return start;
        }
        private RpcClient(string executable, string root,string reservedHome)
        {
            scratchRoot=reservedHome==null?Path.Combine(root,"runtime"):null;
            if(scratchRoot!=null)SafeFiles.PrivateDirectory(scratchRoot);
            Home=reservedHome??Path.Combine(scratchRoot,Guid.NewGuid().ToString("N"));
            SafeFiles.PrivateDirectory(Home);
            if(reservedHome==null)SafeFiles.AtomicWrite(Path.Combine(Home,"config.toml"),Encoding.UTF8.GetBytes("cli_auth_credentials_store = \"file\"\n[analytics]\nenabled = false\n"));
            var start=reservedHome==null?HeadlessProcessWire.StartInfo(executable,Home):EnrollmentStartInfo(executable,Home);
            try
            {
                if(reservedHome!=null)profileLease=HeadlessProcessWire.AcquireProfileLease(Home,CancellationToken.None);
                temporary=new OwnedTemporary(root);temporary.Apply(start);
                process = new Process { StartInfo=start, EnableRaisingEvents=true };
                process.OutputDataReceived += OnOutput;
                process.ErrorDataReceived += delegate { /* Do not log server output: it can contain credentials. */ };
                process.Exited += delegate { FailPending(new IOException("Le serveur Codex s'est arrêté.")); };
                process.Start();
                job = new ProcessJob(process);
                CacheMaintenance.Mark(Home,process.Id,process.StartTime.ToUniversalTime().Ticks);
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            catch { Dispose(); throw new InvalidOperationException("Le serveur Codex n'a pas pu démarrer. Vérifiez le chemin de codex.exe dans les paramètres."); }
        }
        public async Task Initialize(CancellationToken token)
        {
            await Call("initialize", new { clientInfo = new { name="creezio_account_switcher", title="Creezio Account Switcher", version=RelayWorker.Version }, capabilities=new { experimentalApi=true } }, token);
            Send(new { method="initialized" });
        }
        private void Send(object value)
        {
            lock (writeLock) { if (disposed) throw new ObjectDisposedException("RpcClient"); process.StandardInput.WriteLine(Json.Write(value)); process.StandardInput.Flush(); }
        }
        public async Task<object> Call(string method, object parameters, CancellationToken token)
        {
            int id = Interlocked.Increment(ref sequence);
            var completion = new TaskCompletionSource<object>();
            pending[id] = completion;
            try
            {
                Send(new Dictionary<string, object> { {"id",id}, {"method",method}, {"params",parameters} });
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(35));
                    using (deadline.Token.Register(() => completion.TrySetCanceled())) return await completion.Task;
                }
            }
            finally { TaskCompletionSource<object> ignored; pending.TryRemove(id, out ignored); }
        }
        public async Task WaitLogin(CancellationToken token)
        {
            using (var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                using (deadline.Token.Register(() => login.TrySetCanceled()))
                {
                    var result = await login.Task;
                    if (!Object.Equals(Json.Get(result,"success"),true)) throw new InvalidOperationException("Connexion annulée ou refusée. Vous pouvez réessayer.");
                }
            }
        }
        internal async Task<object> WaitLoginResult(string expectedLoginId,CancellationToken token)
        {
            Guid parsed;if(!Guid.TryParse(expectedLoginId,out parsed))throw new InvalidOperationException("Identifiant de connexion natif invalide.");
            var completion=correlatedLogins.GetOrAdd(expectedLoginId,key=>new TaskCompletionSource<object>());object previous;
            if(completedLogins.TryGetValue(expectedLoginId,out previous))completion.TrySetResult(previous);
            try{using(token.Register(()=>completion.TrySetCanceled()))return await completion.Task.ConfigureAwait(false);}
            finally{TaskCompletionSource<object> ignored;correlatedLogins.TryRemove(expectedLoginId,out ignored);}
        }
        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (String.IsNullOrWhiteSpace(args.Data)) return;
            try
            {
                var message = Json.Read<Dictionary<string,object>>(args.Data);
                if (Json.Get(message,"method") != null)
                {
                    string method=Json.Str(Json.Get(message,"method"));
                    if(method=="account/login/completed"){
                        var value=Json.Get(message,"params");login.TrySetResult(value);string nativeId=Json.Str(Json.Get(value,"loginId"));Guid parsed;
                        if(Guid.TryParse(nativeId,out parsed)){if(completedLogins.Count<8)completedLogins.TryAdd(nativeId,value);TaskCompletionSource<object> waiting;if(correlatedLogins.TryGetValue(nativeId,out waiting))waiting.TrySetResult(value);}
                    }
                    if (Json.Get(message,"id") != null) Send(new { id=Json.Get(message,"id"), error=new { code=-32000, message="Reconnect this account in the switcher." } });
                    return;
                }
                int id=Convert.ToInt32(Json.Get(message,"id"));
                TaskCompletionSource<object> completion;
                if (!pending.TryGetValue(id,out completion)) return;
                if (Json.Get(message,"error") != null) completion.TrySetException(new InvalidOperationException("Codex n'a pas pu effectuer la demande. Reconnectez ce compte si sa connexion a expiré, puis réessayez."));
                else completion.TrySetResult(Json.Get(message,"result"));
            }
            catch { /* Unexpected protocol messages are never rendered or logged. */ }
        }
        private void FailPending(Exception error) { foreach (var pair in pending) pair.Value.TrySetException(error); login.TrySetException(error);foreach(var pair in correlatedLogins)pair.Value.TrySetException(error); }
        public void Dispose()
        {
            if (disposed) return;
            disposed=true;
            if (process != null)
            {
                try { process.StandardInput.Close(); } catch { }
                try { if (!process.WaitForExit(1500)) process.Kill(); } catch { }
                if (job != null) job.Dispose();
                try { process.WaitForExit(1500); } catch { }
                process.Dispose();
            }
            FailPending(new ObjectDisposedException("RpcClient"));
            if(temporary!=null)temporary.Dispose();
            if(profileLease!=null)profileLease.Dispose();
            if(scratchRoot!=null)try { SafeFiles.DeleteOwnedTree(scratchRoot, Home); } catch { /* Restricted runtime directory retained if Windows still holds a handle. */ }
        }
    }
}
