using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class RemoteRequest
    {
        public int Version {get;set;}
        public string Id {get;set;}
        public string Grant {get;set;}
        public string Token {get;set;}
        public string Operation {get;set;}
        public string Channel {get;set;}
        public string Account {get;set;}
        public string Workspace {get;set;}
        public string Arguments {get;set;}
    }
    public sealed class RemoteReply
    {
        public string Id {get;set;}
        public string Result {get;set;}
        public string Error {get;set;}
    }
    public sealed class RemoteReceipt
    {
        public string Fingerprint {get;set;}
        public string Result {get;set;}
        public string Error {get;set;}
        public string State {get;set;}
    }
    internal static class RemoteWire
    {
        private static async Task Exact(Stream stream,byte[] data,CancellationToken token){int offset=0;while(offset<data.Length){int n=await stream.ReadAsync(data,offset,data.Length-offset,token);if(n==0)throw new EndOfStreamException();offset+=n;}}
        public static async Task<string> Read(Stream stream,CancellationToken token)
        {byte[] head=new byte[4];await Exact(stream,head,token);int length=BitConverter.ToInt32(head,0);if(length<1||length>1048576)throw new IOException("Trame distante invalide.");byte[] body=new byte[length];await Exact(stream,body,token);return new UTF8Encoding(false,true).GetString(body);}
        public static async Task Write(Stream stream,object value,CancellationToken token)
        {byte[] body=Encoding.UTF8.GetBytes(Json.Write(value));if(body.Length>1048576)throw new IOException("Réponse distante trop volumineuse.");byte[] head=BitConverter.GetBytes(body.Length);await stream.WriteAsync(head,0,head.Length,token);await stream.WriteAsync(body,0,body.Length,token);await stream.FlushAsync(token);}
    }
    internal sealed class RemoteGateway
    {
        private readonly RelayStore store;
        private readonly IRelayTransport transport;
        public RemoteGateway(RelayStore data,IRelayTransport adapter=null){store=data;transport=adapter??new DesktopRelayTransport();}
        internal RemoteGrant Authorize(RemoteRequest request)
        {
            if(!RemotePeers.Config(store).Enabled)throw new InvalidOperationException("Le partage de ce PC est désactivé.");
            if(request==null||request.Version!=1)throw new InvalidOperationException("Protocole distant incompatible.");RelayStore.MessageId(request.Id);RelayStore.MessageId(request.Grant);
            var grant=RemotePeers.Grants(store).FirstOrDefault(g=>g.Id==request.Grant);
            if(grant==null||grant.Revoked||!RemotePeers.Future(grant.Expires)||!RemotePeers.Equal(grant.TokenHash,RemotePeers.Hash(request.Token)))throw new InvalidOperationException("Association refusée, révoquée ou expirée.");return grant;
        }
        internal void CheckChannel(RemoteGrant grant,RelayChannel c,RemoteRequest r)
        {
            if(!AgentProviders.Codex(c)||!grant.Channels.Contains(c.Id)||!c.Enabled||!grant.Accounts.ContainsKey(c.Id)||grant.Accounts[c.Id]!=c.AccountKey||!RelayStore.SamePath(grant.Workspaces[c.Id],c.Workspace)||r.Account!=c.AccountKey||!RelayStore.SamePath(r.Workspace,c.Workspace))throw new InvalidOperationException("Canal, compte ou dossier hors du partage autorisé.");
        }
        private static bool ThreadAllowed(RemoteGrant grant,string channel,string id){string owner;return id!=null&&grant.Threads.TryGetValue(id,out owner)&&owner==channel;}
        private void AddThread(RemoteRequest r,string thread)
        {
            using(store.Lease("remote-settings")){var all=RemotePeers.Grants(store);var grant=all.Single(g=>g.Id==r.Grant);if(grant.Revoked||!RemotePeers.Future(grant.Expires))throw new InvalidOperationException("Partage révoqué pendant l'envoi ; résultat conservé pour vérification.");grant.Threads[thread]=r.Channel;store.WriteRecord("remote-grants.dpapi",all);}
        }
        public async Task<object> Handle(RemoteRequest r,CancellationToken token)
        {
            var grant=Authorize(r);
            if(r.Operation=="channels")return grant.Channels.Select(id=>store.Channel(id)).Where(c=>c.Enabled&&AgentProviders.Codex(c)&&grant.Accounts[c.Id]==c.AccountKey&&RelayStore.SamePath(grant.Workspaces[c.Id],c.Workspace)).Select(c=>new SharedChannel{Id=c.Id,Name=c.Name,Account=c.AccountKey,Workspace=c.Workspace,Anchor=ThreadAllowed(grant,c.Id,c.AnchorThreadId)?c.AnchorThreadId:null,CanSend=grant.CanSend,CanCreate=grant.CanCreate,RequireFullAccess=c.RequireFullAccess}).ToArray();
            var channel=store.Channel(r.Channel);CheckChannel(grant,channel,r);transport.Verify(channel);
            object args=String.IsNullOrEmpty(r.Arguments)?new Dictionary<string,object>():Json.Read<object>(r.Arguments);
            string thread=Json.Str(Json.Get(args,"threadId"));
            if(r.Operation=="assistance_list"||r.Operation=="assistance_reply"){
                if(!grant.CanAssist)throw new InvalidOperationException("Assistance non autorisée par ce partage.");
                var assistance=new Assistance(store);
                if(r.Operation=="assistance_list")return assistance.List().Where(t=>t.Channel==channel.Id&&t.Binding==AgentProviders.Binding(channel)&&ThreadAllowed(grant,channel.Id,t.Thread)).Take(100).ToArray();
                var ticket=assistance.Read(Json.Str(Json.Get(args,"id")));
                if(ticket.Channel!=channel.Id||ticket.Binding!=AgentProviders.Binding(channel)||!ThreadAllowed(grant,channel.Id,ticket.Thread))throw new InvalidOperationException("Demande d'assistance non partagée.");
                var answered=assistance.Answer(ticket.Id,Json.Str(Json.Get(args,"answer")),Json.Str(Json.Get(args,"access")));RelayWorker.Ensure(store);return answered;
            }
            if(r.Operation=="verify")return new{online=true};
            if(r.Operation=="verify_send"){
                if(!grant.CanSend||(thread.Length==0&&!grant.CanCreate)||(thread.Length>0&&!ThreadAllowed(grant,channel.Id,thread)))throw new InvalidOperationException("Envoi non autorisé par ce partage.");
                transport.VerifySend(channel,thread.Length==0?channel.AnchorThreadId:thread);return new{allowed=true};
            }
            if(r.Operation=="verify_files"){
                var files=Json.Read<Dictionary<string,string>>(Json.Write(Json.Get(args,"files")));RelayPolicies.VerifyFiles(channel.Workspace,files);return new{verified=true};
            }
            if(r.Operation=="list_threads"){
                var native=await transport.Call(channel,"list_threads",new{limit=100},token);Authorize(r);
                var rows=RelayEngine.Rows(Json.Get(native,"threads")).Concat(RelayEngine.Rows(Json.Get(native,"pinnedThreads")));
                return new{threads=rows.Where(t=>ThreadAllowed(grant,channel.Id,String.IsNullOrEmpty(Json.Str(Json.Get(t,"threadId")))?Json.Str(Json.Get(t,"id")):Json.Str(Json.Get(t,"threadId")))).Select(t=>new{threadId=String.IsNullOrEmpty(Json.Str(Json.Get(t,"threadId")))?Json.Str(Json.Get(t,"id")):Json.Str(Json.Get(t,"threadId")),title=Json.Str(Json.Get(t,"title")),source="codex"}).ToArray(),pinnedThreads=new object[0]};
            }
            if(r.Operation=="permissions"){
                if(thread.Length==0){if(!grant.CanCreate)throw new InvalidOperationException("Création non autorisée.");thread=channel.AnchorThreadId;}
                else if(!ThreadAllowed(grant,channel.Id,thread))throw new InvalidOperationException("Conversation non partagée.");
                return new{permission=RelayPermissions.Read(channel.Home,thread)};
            }
            if(r.Operation=="read_thread"){
                if(!ThreadAllowed(grant,channel.Id,thread))throw new InvalidOperationException("Conversation non partagée.");
                int limit=(int)(Json.Number(Json.Get(args,"turnLimit"))??8);string cursor=Json.Str(Json.Get(args,"cursor"));
                if(limit<1||limit>8||cursor.Length>4096)throw new InvalidOperationException("Page invalide.");
                var safe=new Dictionary<string,object>{{"threadId",thread},{"turnLimit",limit},{"includeOutputs",true},{"maxOutputCharsPerItem",20000}};if(cursor.Length>0)safe["cursor"]=cursor;
                var result=await transport.Call(channel,"read_thread",safe,token);Authorize(r);return ConversationOnly(result);
            }
            if(!new[]{"create_thread","send_message_to_thread","navigate_to_codex_page"}.Contains(r.Operation))throw new InvalidOperationException("Opération distante interdite.");
            if(!grant.CanSend||(r.Operation=="create_thread"&&!grant.CanCreate))throw new InvalidOperationException("Ce partage autorise uniquement la lecture.");
            if(r.Operation!="create_thread"&&!ThreadAllowed(grant,channel.Id,thread))throw new InvalidOperationException("Conversation non partagée.");
            string fingerprint=RemotePeers.Hash(r.Grant+"\n"+r.Operation+"\n"+r.Channel+"\n"+r.Account+"\n"+r.Workspace+"\n"+r.Arguments);
            string receiptName="remote-receipt-"+r.Id+".dpapi";
            using(store.Lease("remote-call-"+r.Id)){
                var receipt=store.ReadRecord<RemoteReceipt>(receiptName);
                if(receipt.Fingerprint!=null){
                    if(receipt.Fingerprint!=fingerprint)throw new InvalidOperationException("Identifiant déjà utilisé pour un autre envoi.");
                    if(receipt.State=="completed")return Json.Read<object>(receipt.Result);
                    throw new InvalidOperationException("Envoi précédent incertain ; aucun renvoi automatique. Vérifiez le chat partagé.");
                }
                object safeArgs;
                if(r.Operation=="navigate_to_codex_page")safeArgs=new{threadId=thread};else{
                    string prompt=Json.Str(Json.Get(args,"prompt"));if(String.IsNullOrWhiteSpace(prompt)||prompt.Length>64000)throw new InvalidOperationException("Prompt absent ou trop long.");
                    transport.VerifySend(channel,r.Operation=="create_thread"?channel.AnchorThreadId:thread);
                    if(r.Operation=="create_thread"){
                        string title=Json.Str(Json.Get(args,"title"));if(title.Length>200)throw new InvalidOperationException("Titre trop long.");
                        object target=String.IsNullOrEmpty(channel.CodexProjectId)?(object)new{type="projectless"}:new{type="project",projectId=channel.CodexProjectId,environment=new{type="local"}};
                        safeArgs=new{title=title,prompt=prompt,target=target};
                    }else{
                        var snapshot=await transport.Call(channel,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token);
                        DirectMessages.RequireThread(snapshot,thread);if(DirectMessages.Busy(snapshot))throw new InvalidOperationException("Conversation occupée ; aucun envoi effectué.");safeArgs=new{threadId=thread,prompt=prompt};
                    }
                }
                grant=Authorize(r);CheckChannel(grant,channel,r);
                receipt.Fingerprint=fingerprint;receipt.State="sending";store.WriteRecord(receiptName,receipt);
                try{
                    object result=await transport.Call(channel,r.Operation,safeArgs,token);receipt.Result=Json.Write(result);
                    if(r.Operation=="create_thread"){
                        string created=Json.Str(Json.Get(result,"threadId"));if(created.Length==0)throw new InvalidOperationException("Création sans identifiant stable ; vérification requise.");AddThread(r,created);
                    }
                    receipt.State="completed";store.WriteRecord(receiptName,receipt);return result;
                }catch{receipt.State="uncertain";receipt.Error="Vérification dans Codex requise.";store.WriteRecord(receiptName,receipt);throw;}
            }
        }
        internal static object ConversationOnly(object snapshot)
        {
            return new{thread=new{id=Json.Get(Json.Get(snapshot,"thread"),"id"),status=Json.Get(Json.Get(snapshot,"thread"),"status")},page=Json.Get(snapshot,"page"),nextCursor=Json.Get(snapshot,"nextCursor"),turns=RelayEngine.Rows(Json.Get(snapshot,"turns")).Select(t=>new{id=Json.Get(t,"id"),status=Json.Get(t,"status"),items=RelayEngine.Rows(Json.Get(t,"items")).Select(SharedItem).Where(i=>i!=null).ToArray()}).ToArray()};
        }
        private static object SharedItem(object item)
        {
            string type=Json.Str(Json.Get(item,"type"));
            if(type=="userMessage"||type=="agentMessage")return item;
            if(type!="functionCallOutput"||Json.Str(Json.Get(item,"namespace"))!="codex_app"||!new[]{"create_thread","send_message_to_thread"}.Contains(Json.Str(Json.Get(item,"name"))))return null;
            string body=Json.Str(Json.Get(Json.Get(item,"output"),"text"));int start=body.IndexOf("<input>[CREEZIO_",StringComparison.Ordinal);int end=body.LastIndexOf("</input>",StringComparison.Ordinal);
            if(start<0||end<=start)return null;
            return new{type="userMessage",content=new[]{new{type="text",text=body.Substring(start+7,end-start-7)}}};
        }
        internal async Task ServeConnection(TcpClient socket,X509Certificate2 certificate,CancellationToken token)
        {
            using(socket)using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                deadline.CancelAfter(TimeSpan.FromSeconds(65));using(deadline.Token.Register(()=>socket.Close()))using(var tls=new SslStream(socket.GetStream(),false)){
                    await tls.AuthenticateAsServerAsync(certificate,false,SslProtocols.Tls12,false);
                    var request=Json.Read<RemoteRequest>(await RemoteWire.Read(tls,deadline.Token));var reply=new RemoteReply{Id=request==null?null:request.Id};
                    try{reply.Result=Json.Write(await Handle(request,deadline.Token));}catch(Exception e){reply.Error=Program.SafeError(e);}
                    await RemoteWire.Write(tls,reply,deadline.Token);
                }
            }
        }
        public async Task Listen(RemoteConfig config,CancellationToken token)
        {
            // Windows Schannel requires a user key container. Disposal removes this
            // temporary container; PersistKeySet would leave a key behind each run.
            using(var certificate=new X509Certificate2(Convert.FromBase64String(config.Certificate),"",X509KeyStorageFlags.UserKeySet))using(var slots=new SemaphoreSlim(8)){
                var listener=new TcpListener(IPAddress.Parse(config.BindAddress),config.Port);listener.Start();var pending=new List<Task>();
                try{while(!token.IsCancellationRequested){
                    pending.RemoveAll(t=>t.IsCompleted);
                    if(!listener.Pending()){await Task.Delay(100,token);continue;}
                    await slots.WaitAsync(token);var socket=await listener.AcceptTcpClientAsync();
                    pending.Add(Task.Run(async()=>{try{await ServeConnection(socket,certificate,token);}catch(Exception){socket.Close();}finally{slots.Release();}},CancellationToken.None));
                }}finally{listener.Stop();try{Task.WhenAll(pending).GetAwaiter().GetResult();}catch(OperationCanceledException){}}
            }
        }
        public static RelayWorkerState Status(RelayStore store){return store.ReadRecord<RelayWorkerState>("remote-worker.dpapi");}
        public static void Start(RelayStore store)
        {
            var config=RemotePeers.Config(store);if(!config.Enabled)throw new InvalidOperationException("Activez le partage de ce PC.");var state=Status(store);if(DesktopRuntime.SameProcess(state.Pid,state.Started))return;
            var start=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe"),"--remote-worker "+RelayWorker.Quote(store.Root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};start.EnvironmentVariables.Remove("CODEX_THREAD_ID");start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");using(var process=Process.Start(start)){}
        }
        public static int Run(string root)
        {
            var store=new RelayStore(root);FileStream lease;try{lease=store.Lease("remote-worker");}catch(IOException){return 0;}
            using(lease)using(var quit=new CancellationTokenSource())using(var process=Process.GetCurrentProcess()){
                var config=RemotePeers.Config(store);if(!config.Enabled)return 0;var state=new RelayWorkerState{Pid=process.Id,Started=process.StartTime.ToUniversalTime().Ticks,Version=RelayWorker.Version};store.WriteRecord("remote-worker.dpapi",state);
                using(var timer=new Timer(o=>{try{var now=RemotePeers.Config(store);if(!now.Enabled||now.Port!=config.Port||now.BindAddress!=config.BindAddress)quit.Cancel();}catch{quit.Cancel();}},null,1000,1000)){
                    try{new RemoteGateway(store).Listen(config,quit.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){}catch(Exception e){state.Error=Program.SafeError(e);}
                }
                state.Pid=0;store.WriteRecord("remote-worker.dpapi",state);return state.Error==null?0:1;
            }
        }
    }
    internal sealed class RemoteClient
    {
        public static async Task<object> Call(RemotePeer peer,string operation,string channel,string account,string workspace,object args,CancellationToken token,string requestId=null)
        {
            RemotePeers.ValidatePeer(peer);if(!peer.Enabled)throw new InvalidOperationException("Connexion distante désactivée.");
            var request=new RemoteRequest{Version=1,Id=requestId??Guid.NewGuid().ToString("N"),Grant=peer.Id,Token=peer.Token,Operation=operation,Channel=channel,Account=account,Workspace=workspace,Arguments=Json.Write(args)};
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))using(var socket=new TcpClient()){
                deadline.CancelAfter(TimeSpan.FromSeconds(65));using(deadline.Token.Register(()=>socket.Close())){
                    var connect=socket.ConnectAsync(peer.Host,peer.Port);if(await Task.WhenAny(connect,Task.Delay(8000,deadline.Token))!=connect)throw new IOException("PC distant injoignable.");await connect;
                    using(var tls=new SslStream(socket.GetStream(),false,(sender,cert,chain,errors)=>cert!=null&&RemotePeers.Equal(RemotePeers.CertificatePin(cert),peer.Pin))){
                        await tls.AuthenticateAsClientAsync("Account Switcher",null,SslProtocols.Tls12,false);
                        await RemoteWire.Write(tls,request,deadline.Token);var reply=Json.Read<RemoteReply>(await RemoteWire.Read(tls,deadline.Token));
                        if(reply==null||reply.Id!=request.Id)throw new IOException("Réponse distante non reconnue.");if(reply.Error!=null)throw new InvalidOperationException(reply.Error);return Json.Read<object>(reply.Result);
                    }
                }
            }
        }
    }
    internal sealed class RemoteTransport : IRelayTransport, IRelayPermissionTransport
    {
        private readonly RelayStore store;
        public RemoteTransport(RelayStore data){store=data;}
        private RemotePeer Peer(RelayChannel c){if(!c.Enabled)throw new InvalidOperationException("Canal désactivé.");var p=RemotePeers.Peers(store).SingleOrDefault(x=>x.Id==c.PeerId);if(p==null)throw new InvalidOperationException("PC distant non associé.");return p;}
        public void Verify(RelayChannel c){RemoteClient.Call(Peer(c),"verify",c.RemoteChannel,c.RemoteAccount,c.RemoteWorkspace,new{},CancellationToken.None).GetAwaiter().GetResult();}
        public string Permissions(RelayChannel c,string thread){return Json.Str(Json.Get(RemoteClient.Call(Peer(c),"permissions",c.RemoteChannel,c.RemoteAccount,c.RemoteWorkspace,new{threadId=thread},CancellationToken.None).GetAwaiter().GetResult(),"permission"));}
        public void VerifySend(RelayChannel c,string thread){RemoteClient.Call(Peer(c),"verify_send",c.RemoteChannel,c.RemoteAccount,c.RemoteWorkspace,new{threadId=thread},CancellationToken.None).GetAwaiter().GetResult();}
        public Task<object> Call(RelayChannel c,string tool,object args,CancellationToken token){return RemoteClient.Call(Peer(c),tool,c.RemoteChannel,c.RemoteAccount,c.RemoteWorkspace,args,token);}
    }
}
