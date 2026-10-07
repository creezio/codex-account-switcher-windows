using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal interface IToolTunnelConnection : IDisposable
    {
        Task<TunnelTool[]> Inventory(CancellationToken token);
        Task<object> Invoke(string server,string tool,object arguments,CancellationToken token);
    }
    internal sealed class TunnelInteractionException : InvalidOperationException
    { public TunnelInteractionException():base("Le connecteur demande une interaction native. Aucune approbation n'a été accordée par le tunnel. Effectuez cette opération dans Codex."){} }

    // A short-lived official app-server client. Never starts a model turn or resumes a user's chat.
    internal sealed class ToolTunnelRpc : IToolTunnelConnection
    {
        private readonly Process process;
        private readonly ProcessJob job;
        private readonly StreamWriter input;
        private readonly object writeLock=new object();
        private readonly ConcurrentDictionary<int,TaskCompletionSource<object>> pending=new ConcurrentDictionary<int,TaskCompletionSource<object>>();
        private int sequence;
        private bool disposed,interaction;
        private string thread;
        private readonly string home;
        public ToolTunnelRpc(string executable,string profile)
        {
            home=Path.GetFullPath(profile);SafeFiles.RejectLinks(home);
            var start=new ProcessStartInfo(executable,"app-server --listen stdio://"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=home};
            start.EnvironmentVariables["CODEX_HOME"]=home;start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;
            foreach(string key in new[]{"CODEX_THREAD_ID","CODEX_APP_TOOLS_PIPE_PATH","OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_IDENTITY_TOKEN","OPENAI_IDENTITY_TOKEN_FILE","OPENAI_IDENTITY_PROVIDER_ID","OPENAI_ORGANIZATION","CHATGPT_BASE_URL","CODEX_CHATGPT_BASE_URL"})start.EnvironmentVariables.Remove(key);
            process=new Process{StartInfo=start,EnableRaisingEvents=true};
            process.OutputDataReceived+=Output;process.ErrorDataReceived+=delegate{};
            process.Exited+=delegate{Fail(new IOException("Le serveur d'outils Codex s'est arrêté."));};
            try{process.Start();job=new ProcessJob(process);input=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false)){AutoFlush=true};process.BeginOutputReadLine();process.BeginErrorReadLine();}
            catch{Dispose();throw;}
        }
        private void Send(object value){lock(writeLock){if(disposed)throw new ObjectDisposedException("ToolTunnelRpc");input.WriteLine(Json.Write(value));}}
        private async Task<object> Rpc(string method,object args,CancellationToken token)
        {
            int id=Interlocked.Increment(ref sequence);var completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=completion;
            try{
                Send(new{id=id,method=method,@params=args});
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    deadline.CancelAfter(TimeSpan.FromSeconds(50));
                    using(deadline.Token.Register(()=>completion.TrySetCanceled()))return await completion.Task.ConfigureAwait(false);
                }
            }finally{TaskCompletionSource<object> ignored;pending.TryRemove(id,out ignored);}
        }
        private void Output(object sender,DataReceivedEventArgs e)
        {
            if(String.IsNullOrWhiteSpace(e.Data))return;
            try{
                // Connector inventories may exceed the normal 4 MiB record limit; never persist them wholesale.
                if(e.Data.Length>32*1024*1024)throw new IOException("Inventaire d'outils trop volumineux.");
#if NETCOREAPP
                var value=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,object>>(e.Data,Desktop.JsonCompatibility.Options);
#else
                var value=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Deserialize<Dictionary<string,object>>(e.Data);
#endif
                if(Json.Get(value,"method")!=null){
                    if(Json.Get(value,"id")!=null){interaction=true;Send(new{id=Json.Get(value,"id"),error=new{code=-32000,message="Interactive approval must be handled in Codex; the tool tunnel cannot grant it."}});}
                    return;
                }
                TaskCompletionSource<object> completion;int id=Convert.ToInt32(Json.Get(value,"id"));if(!pending.TryGetValue(id,out completion))return;
                if(interaction)completion.TrySetException(new TunnelInteractionException());
                else if(Json.Get(value,"error")!=null)completion.TrySetException(new InvalidOperationException("Codex a refusé l'appel d'outil (code "+Convert.ToString(Json.Get(Json.Get(value,"error"),"code"))+"). Vérifiez la connexion et les autorisations du plugin dans l'instance propriétaire."));
                else completion.TrySetResult(Json.Get(value,"result"));
            }catch(Exception error){Fail(new IOException("Réponse du serveur d'outils invalide ("+error.GetType().Name+")."));}
        }
        private void Fail(Exception error){foreach(var p in pending)p.Value.TrySetException(error);}
        private async Task Ready(CancellationToken token)
        {
            if(thread!=null)return;
            await Rpc("initialize",new{clientInfo=new{name="creezio_tool_tunnel",version=RelayWorker.Version},capabilities=new{experimentalApi=true}},token).ConfigureAwait(false);
            Send(new{method="initialized"});
            var context=await Rpc("thread/start",new{cwd=home,ephemeral=true},token).ConfigureAwait(false);
            thread=Json.Str(Json.Get(Json.Get(context,"thread"),"id"));if(String.IsNullOrEmpty(thread))throw new IOException("Contexte d'outils Codex absent.");
        }
        public async Task<TunnelTool[]> Inventory(CancellationToken token)
        {
            await Ready(token).ConfigureAwait(false);var tools=new List<TunnelTool>();var seen=new HashSet<string>();string cursor=null;
            var installed=await Rpc("app/installed",new{threadId=thread,forceRefresh=false},token).ConfigureAwait(false);
            var callable=new HashSet<string>(RelayEngine.Rows(Json.Get(installed,"apps")).Where(a=>Object.Equals(Json.Get(a,"enabled"),true)&&Object.Equals(Json.Get(a,"callable"),true)).Select(a=>Json.Str(Json.Get(a,"id"))));
            do{
                var result=await Rpc("mcpServerStatus/list",new{threadId=thread,detail="toolsAndAuthOnly",cursor=cursor},token).ConfigureAwait(false);
                foreach(var server in RelayEngine.Rows(Json.Get(result,"data"))){
                    string serverName=Json.Str(Json.Get(server,"name"));
                    // Desktop control, shell interpreters and this relay are never exported recursively.
                    if(!ToolTunnel.AllowedServer(serverName))continue;
                    foreach(var pair in Json.Obj(Json.Get(server,"tools"))){
                        var raw=pair.Value;string name=Json.Str(Json.Get(raw,"name"));if(String.IsNullOrEmpty(name))continue;
                        var meta=Json.Get(raw,"_meta");var annotations=Json.Get(raw,"annotations");
                        string connector=Json.Str(Json.Get(meta,"connector_id"));if(serverName=="codex_apps"&&!callable.Contains(connector))continue;
                        tools.Add(new TunnelTool{Server=serverName,Name=name,Group=Json.Str(Json.Get(meta,"connector_name")),ConnectorId=connector,Annotations=annotations,Description=Json.Str(Json.Get(raw,"description")),Schema=Json.Get(raw,"inputSchema"),ReadOnly=Object.Equals(Json.Get(annotations,"readOnlyHint"),true)&&!Object.Equals(Json.Get(annotations,"destructiveHint"),true)});
                    }
                }
                cursor=Json.Str(Json.Get(result,"nextCursor"));if(cursor.Length>0&&!seen.Add(cursor))throw new IOException("Pagination Codex répétée.");
                if(tools.Count>20000||seen.Count>100)throw new IOException("Inventaire Codex trop volumineux.");
            }while(cursor.Length>0);
            foreach(var t in tools)if(String.IsNullOrWhiteSpace(t.Group))t.Group=t.Server;
            return tools.OrderBy(t=>t.Group).ThenBy(t=>t.Name).ToArray();
        }
        public async Task<object> Invoke(string server,string tool,object arguments,CancellationToken token)
        {await Ready(token).ConfigureAwait(false);return await Rpc("mcpServer/tool/call",new{threadId=thread,server=server,tool=tool,arguments=arguments},token).ConfigureAwait(false);}
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            try{if(input!=null)input.Dispose();else process.StandardInput.Close();}catch{}
            try{if(!process.WaitForExit(1500))process.Kill();}catch{}
            if(job!=null)job.Dispose();process.Dispose();Fail(new ObjectDisposedException("ToolTunnelRpc"));
        }
    }
}
