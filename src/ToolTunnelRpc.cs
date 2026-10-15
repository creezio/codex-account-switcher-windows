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
        private readonly IHeadlessRpc client;
        private readonly HeadlessProcesses tracking;
        private string thread;
        private readonly string home;
        internal ToolTunnelRpc(string executable,string profile,string accountKey=null,RelayStore store=null)
        {
            home=Path.GetFullPath(profile);SafeFiles.RejectLinks(home);
            client=new HeadlessRpc(executable,home,accountKey??AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json"))).Key);
            try{tracking=new HeadlessProcesses(store,client,home,accountKey);}catch{client.Dispose();throw;}
        }
        internal ToolTunnelRpc(IHeadlessRpc connection,string profile){client=connection;home=Path.GetFullPath(profile);}
        private async Task<object> Rpc(string method,object args,CancellationToken token)
        {
            try{return await client.Call(method,args,token).ConfigureAwait(false);}
            catch(HeadlessInteractionException){throw new TunnelInteractionException();}
        }
        private async Task Ready(CancellationToken token)
        {
            if(thread!=null)return;
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
                        tools.Add(new TunnelTool{Server=serverName,Name=name,Title=Json.Str(Json.Get(raw,"title")),Group=Json.Str(Json.Get(meta,"connector_name")),ConnectorId=connector,Annotations=annotations,Description=Json.Str(Json.Get(raw,"description")),Schema=Json.Get(raw,"inputSchema"),ReadOnly=Object.Equals(Json.Get(annotations,"readOnlyHint"),true)&&!Object.Equals(Json.Get(annotations,"destructiveHint"),true)});
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
        public void Dispose(){try{client.Dispose();}finally{if(tracking!=null)tracking.Dispose();}}
    }
}
