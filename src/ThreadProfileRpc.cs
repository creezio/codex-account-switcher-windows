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
    internal sealed class ThreadProfileRpcException:InvalidOperationException
    { internal readonly string Detail;internal ThreadProfileRpcException(object error):base("Codex refuse cette opération de conversation (code "+Convert.ToString(Json.Get(error,"code"))+"). Le format peut être incompatible avec cette version de Codex."){Detail=Json.Str(Json.Get(error,"message"));} }
    internal interface IThreadProfileRpc:IDisposable
    { Task<object> Call(string method,object parameters,CancellationToken token); }
    // Each connection uses exactly one instance's home and own credentials. No model
    // turn is started by the import service; native Codex handles subsequent work.
    internal sealed class ThreadProfileRpc:IThreadProfileRpc
    {
        private readonly Process process;
        private readonly ProcessJob job;
        private readonly StreamWriter input;
        private readonly ConcurrentDictionary<int,TaskCompletionSource<object>> pending=new ConcurrentDictionary<int,TaskCompletionSource<object>>();
        private readonly object writeLock=new object();
        private int sequence;
        private bool disposed,ready;
        internal ThreadProfileRpc(string executable,string home)
        {
            SafeFiles.RejectLinks(home);
            var start=new ProcessStartInfo(executable,"app-server --listen stdio://") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=home};
            ConfigureHome(start,home);
            process=new Process{StartInfo=start,EnableRaisingEvents=true};process.OutputDataReceived+=Output;process.ErrorDataReceived+=delegate{};
            process.Exited+=delegate{Fail(new IOException("Le service de conversations Codex s’est arrêté."));};
            try{process.Start();job=new ProcessJob(process);input=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false)){AutoFlush=true};process.BeginOutputReadLine();process.BeginErrorReadLine();}catch{Dispose();throw;}
        }
        internal static void ConfigureHome(ProcessStartInfo start,string home)
        {
            start.EnvironmentVariables["CODEX_HOME"]=home;start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;
            foreach(string key in new[]{"CODEX_THREAD_ID","CODEX_APP_TOOLS_PIPE_PATH","OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_IDENTITY_TOKEN","OPENAI_IDENTITY_TOKEN_FILE","OPENAI_IDENTITY_PROVIDER_ID","OPENAI_ORGANIZATION","CHATGPT_BASE_URL","CODEX_CHATGPT_BASE_URL"})start.EnvironmentVariables.Remove(key);
        }
        private void Send(object message){lock(writeLock){if(disposed)throw new ObjectDisposedException("ThreadProfileRpc");input.WriteLine(Json.Write(message));}}
        public async Task<object> Call(string method,object parameters,CancellationToken token)
        {
            if(!ready){await Request("initialize",new{clientInfo=new{name="creezio_thread_copy",version=RelayWorker.Version},capabilities=new{experimentalApi=true}},token).ConfigureAwait(false);Send(new{method="initialized"});ready=true;}
            return await Request(method,parameters,token).ConfigureAwait(false);
        }
        private async Task<object> Request(string method,object parameters,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();int id=Interlocked.Increment(ref sequence);var result=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=result;
            try{Send(new{id=id,method=method,@params=parameters});using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){timeout.CancelAfter(TimeSpan.FromSeconds(60));using(timeout.Token.Register(()=>result.TrySetCanceled()))return await result.Task.ConfigureAwait(false);}}
            finally{TaskCompletionSource<object> ignored;pending.TryRemove(id,out ignored);}
        }
        private void Output(object sender,DataReceivedEventArgs args)
        {
            if(String.IsNullOrEmpty(args.Data))return;
            try{
                if(args.Data.Length>64*1024*1024)throw new IOException("Historique trop volumineux pour cet import.");
#if NETCOREAPP
                var message=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,object>>(args.Data,Desktop.JsonCompatibility.Options);
#else
                var message=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=64*1024*1024}.Deserialize<Dictionary<string,object>>(args.Data);
#endif
                if(Json.Get(message,"method")!=null){if(Json.Get(message,"id")!=null)Send(new{id=Json.Get(message,"id"),error=new{code=-32000,message="Interactive requests must be handled in native Codex."}});return;}
                TaskCompletionSource<object> result;int id=Convert.ToInt32(Json.Get(message,"id"));if(!pending.TryGetValue(id,out result))return;
                var error=Json.Get(message,"error");
                if(error!=null)result.TrySetException(new ThreadProfileRpcException(error));
                else result.TrySetResult(Json.Get(message,"result"));
            }catch(Exception error){Fail(new IOException("Réponse de conversation invalide ("+error.GetType().Name+")."));}
        }
        private void Fail(Exception error){foreach(var pair in pending)pair.Value.TrySetException(error);}
        public void Dispose(){if(disposed)return;disposed=true;try{if(input!=null)input.Close();if(!process.WaitForExit(1500))process.Kill();}catch{}if(job!=null)job.Dispose();process.Dispose();Fail(new ObjectDisposedException("ThreadProfileRpc"));}
    }
}
