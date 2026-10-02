using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly object writeLock = new object();
        private int sequence;
        private bool disposed;
        public readonly string Home;
        private readonly string scratchRoot;
        public RpcClient(string executable, string root)
        {
            scratchRoot = Path.Combine(root, "runtime");
            SafeFiles.PrivateDirectory(scratchRoot);
            Home = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
            SafeFiles.PrivateDirectory(Home);
            SafeFiles.AtomicWrite(Path.Combine(Home, "config.toml"), Encoding.UTF8.GetBytes("cli_auth_credentials_store = \"file\"\n[analytics]\nenabled = false\n"));
            var start = new ProcessStartInfo(executable, "app-server --listen stdio://") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=Home, StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8 };
            start.EnvironmentVariables["CODEX_HOME"] = Home;
            // Never inherit an alternative authentication identity from the host process.
            foreach (string key in new[] {"OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_IDENTITY_TOKEN", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_IDENTITY_PROVIDER_ID", "OPENAI_ORGANIZATION", "CHATGPT_BASE_URL", "CODEX_CHATGPT_BASE_URL"}) start.EnvironmentVariables.Remove(key);
            try
            {
                process = new Process { StartInfo=start, EnableRaisingEvents=true };
                process.OutputDataReceived += OnOutput;
                process.ErrorDataReceived += delegate { /* Do not log server output: it can contain credentials. */ };
                process.Exited += delegate { FailPending(new IOException("Le serveur Codex s'est arrêté.")); };
                process.Start();
                job = new ProcessJob(process);
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
        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (String.IsNullOrWhiteSpace(args.Data)) return;
            try
            {
                var message = Json.Read<Dictionary<string,object>>(args.Data);
                if (Json.Get(message,"method") != null)
                {
                    string method=Json.Str(Json.Get(message,"method"));
                    if (method=="account/login/completed") login.TrySetResult(Json.Get(message,"params"));
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
        private void FailPending(Exception error) { foreach (var pair in pending) pair.Value.TrySetException(error); login.TrySetException(error); }
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
            try { SafeFiles.DeleteOwnedTree(scratchRoot, Home); } catch { /* Restricted runtime directory retained if Windows still holds a handle. */ }
        }
    }
}
