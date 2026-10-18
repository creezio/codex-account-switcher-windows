using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class WebhookHttpRequest
    {
        internal string Method,Path,Body;
        internal Dictionary<string,string> Headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    }
    internal sealed class WebhookHttpResponse
    {
        internal int Status;
        internal string Body,Location;
    }
    // TCP loopback avoids HTTP.sys URL ACLs and administrative installation.
    // There are no request logs: headers, prompts and keys remain in memory.
    internal sealed class WebhookHttp : IDisposable
    {
        private readonly RelayStore store;
        private readonly TcpListener listener;
        private readonly SemaphoreSlim capacity=new SemaphoreSlim(8,8);
        private readonly SemaphoreSlim streams=new SemaphoreSlim(4,4);
        private readonly SemaphoreSlim uploads=new SemaphoreSlim(1,1);
        private readonly CancellationTokenSource stop=new CancellationTokenSource();
        private readonly List<Task> clients=new List<Task>();
        private readonly HashSet<TcpClient> sockets=new HashSet<TcpClient>();
        private Task accepting;
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
        private static void PreventInheritance(Socket socket)
        {
            // Framework sockets can be inherited when Process.Start redirects
            // standard streams. A relay child must never keep this listening
            // port or an authenticated client connection alive after shutdown.
            if(!SetHandleInformation(socket.Handle,1,0))throw new IOException("Impossible de proteger le transport HTTP contre l'heritage de processus.");
        }
        internal WebhookHttp(RelayStore store,int port)
        {this.store=store;listener=new TcpListener(IPAddress.Loopback,port);try{PreventInheritance(listener.Server);}catch{listener.Stop();throw;}}
        internal int Port {get{return ((IPEndPoint)listener.LocalEndpoint).Port;}}
        internal void Start(){try{PreventInheritance(listener.Server);listener.Start(16);PreventInheritance(listener.Server);accepting=Accept();}catch{listener.Stop();throw;}}
        private async Task Accept()
        {
            while(!stop.IsCancellationRequested){
                TcpClient client;
                try{client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);}catch(ObjectDisposedException){break;}catch(SocketException){if(stop.IsCancellationRequested)break;throw;}
                try{PreventInheritance(client.Client);}catch(IOException){client.Close();continue;}
                if(!capacity.Wait(0)){client.Close();continue;}
                lock(sockets){if(stop.IsCancellationRequested){client.Close();capacity.Release();break;}sockets.Add(client);}
                Task task=Task.Run(()=>Serve(client));lock(clients){clients.RemoveAll(t=>t.IsCompleted);clients.Add(task);}
            }
        }
        private void Serve(TcpClient client)
        {
            bool httpSlot=true,streamSlot=false,uploadSlot=false,upgraded=false;
            using(client){
                try{client.ReceiveTimeout=10000;client.SendTimeout=10000;client.NoDelay=true;using(var stream=client.GetStream()){
                    WebhookHttpResponse response;
                    try{
                        var request=ReadRequest(stream,headers=>{
                            ValidateHeaders(store,headers);
                            if(headers.Method=="POST"&&(AttachmentPath(headers.Path).Success||PortalManagementHttp.IsArchiveImport(headers.Path))){if(!uploads.Wait(0))throw new WebhookException(429,"Un fichier est deja en cours d'ajout. Reessayez avec la meme cle d'idempotence.");uploadSlot=true;}
                        });
                        string slug,chatId,instanceId;
                        if(WebhookWebSocket.IsEventsPath(request.Path,out slug,out chatId)&&request.Headers.ContainsKey("Upgrade")){
                            string accept=WebhookWebSocket.AcceptKey(request);
                            // Resolve and authorize the exact chat before switching protocols.
                            // Long lived streams have their own limit and never occupy HTTP slots.
                            string portalSlug,portalChat;
                            bool portal=WebhookWebSocket.IsPortalEventsPath(request.Path,out portalSlug,out instanceId,out portalChat);
                            Func<object> snapshot=portal?(Func<object>)(()=>Portal.Snapshot(store,slug,Bearer(request),instanceId,chatId)):(()=>WebhookChats.Snapshot(store,slug,Bearer(request),chatId));
                            object initial=snapshot();
                            if(!streams.Wait(0))throw new WebhookException(429,"Trop de discussions en direct sont ouvertes.");
                            streamSlot=true;capacity.Release();httpSlot=false;
                            WebhookWebSocket.WriteHandshake(stream,accept);upgraded=true;
                            WebhookWebSocket.Run(client,stream,snapshot,initial,stop.Token);
                            return;
                        }
                        response=Handle(store,request);
                    }catch(WebhookException ex){if(upgraded)return;response=Error(ex.StatusCode,ex.Message);}catch(Exception){if(upgraded)return;response=Error(500,"Le service ne peut pas traiter cette demande.");}
                    WriteResponse(stream,response);
                }}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}finally{
                    lock(sockets)sockets.Remove(client);if(httpSlot)capacity.Release();if(streamSlot)streams.Release();if(uploadSlot)uploads.Release();
                }
            }
        }
        private static string Line(Stream stream,ref int remaining,DateTime deadline)
        {
            var text=new StringBuilder();bool carriage=false;
            while(true){
                if(--remaining<0)throw new WebhookException(431,"Les en-tetes HTTP sont trop longs.");
                ReadDeadline(stream,deadline);
                int value=stream.ReadByte();if(value<0)throw new WebhookException(400,"Demande HTTP incomplete.");
                if(carriage){if(value!=10)throw new WebhookException(400,"Separateur HTTP invalide.");return text.ToString();}
                if(value==13){carriage=true;continue;}if(value<32||value>126)throw new WebhookException(400,"En-tete HTTP invalide.");text.Append((char)value);
            }
        }
        internal static WebhookHttpRequest ReadRequest(Stream stream)
        {return ReadRequest(stream,null);}
        private static void ReadDeadline(Stream stream,DateTime deadline)
        {
            double remaining=(deadline-DateTime.UtcNow).TotalMilliseconds;
            if(remaining<=0)throw new WebhookException(408,"La demande HTTP a expire.");
            // A drip-fed upload must not refresh the receive timeout forever.
            // Each blocking operation shares the initial request deadline.
            if(stream.CanTimeout)stream.ReadTimeout=Math.Max(1,(int)Math.Ceiling(remaining));
        }
        internal static void ValidateHeaders(RelayStore store,WebhookHttpRequest request)
        {
            if(request.Method=="GET"&&request.Path=="/health")return;
            if(PortalAdministration.Path(request.Path).Success){PortalAdministration.ValidateHeaders(store,request);return;}
            var upload=AttachmentPath(request.Path);
            if(upload.Success){AttachmentGrant(store,upload,Bearer(request));return;}
            var portal=PortalPath(request.Path);
            if(portal.Success){
                string slug=portal.Groups[1].Value,instance=portal.Groups[2].Value;
                if(instance.Length==0)Portal.ListInstances(store,slug,Bearer(request));
                else Portal.RequireGrant(store,slug,Bearer(request),instance);
                return;
            }
            var match=Regex.Match(request.Path??"",@"\A/v1/hooks/([a-z0-9][a-z0-9-]{1,62})/(?:jobs(?:/([a-f0-9]{32}))?|chats(?:/([a-f0-9]{32})(?:/(?:messages|events))?)?)\z");
            if(!match.Success)throw new WebhookException(404,"Adresse introuvable.");
            Webhooks.ValidateAuthorization(store,match.Groups[1].Value,Bearer(request));
        }
        internal static string Bearer(WebhookHttpRequest request)
        {string authorization;request.Headers.TryGetValue("Authorization",out authorization);return authorization!=null&&authorization.StartsWith("Bearer ",StringComparison.Ordinal)?authorization.Substring(7):null;}
        internal static WebhookHttpRequest ReadRequest(Stream stream,Action<WebhookHttpRequest> authorizeHeaders)
        {
            int remaining=16384;DateTime deadline=DateTime.UtcNow.AddSeconds(10);string first=Line(stream,ref remaining,deadline);
            var match=Regex.Match(first,@"\A(GET|POST) (/[^ ]*) HTTP/1\.1\z");if(!match.Success)throw new WebhookException(400,"Ligne HTTP invalide.");
            var request=new WebhookHttpRequest{Method=match.Groups[1].Value,Path=match.Groups[2].Value};
            if(request.Path.IndexOfAny(new[]{'?','#','%','\\'})>=0)throw new WebhookException(400,"Les parametres d'URL et chemins encodes ne sont pas acceptes.");
            for(int count=0;;count++){
                string line=Line(stream,ref remaining,deadline);if(line.Length==0)break;if(count>=48)throw new WebhookException(431,"Trop d'en-tetes HTTP.");
                int split=line.IndexOf(':');if(split<=0)throw new WebhookException(400,"En-tete HTTP invalide.");string name=line.Substring(0,split);
                if(!Regex.IsMatch(name,@"\A[A-Za-z0-9!#$&'*+.^_`|~-]+\z")||request.Headers.ContainsKey(name))throw new WebhookException(400,"En-tete HTTP duplique ou invalide.");
                string value=line.Substring(split+1).Trim(' ');request.Headers.Add(name,value);
            }
            string host;if(!request.Headers.TryGetValue("Host",out host)||String.IsNullOrEmpty(host)||host.IndexOf(' ')>=0)throw new WebhookException(400,"Host HTTP requis.");
            string streamSlug,streamChat;bool events=WebhookWebSocket.IsEventsPath(request.Path,out streamSlug,out streamChat)&&request.Method=="GET";
            if(request.Headers.ContainsKey("Transfer-Encoding")||request.Headers.ContainsKey("Expect")||(request.Headers.ContainsKey("Upgrade")&&!events))throw new WebhookException(400,"Ce transport HTTP n'est pas accepte.");
            int length=0;string raw;
            if(request.Headers.TryGetValue("Content-Length",out raw)&&(!Regex.IsMatch(raw,@"\A[0-9]{1,8}\z")||!Int32.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out length)))throw new WebhookException(400,"Content-Length invalide.");
            bool uploadBody=request.Method=="POST"&&AttachmentPath(request.Path).Success;
            if(length>(PortalAdministration.Path(request.Path).Success?PortalAdministration.MaximumBodyFor(request.Path):uploadBody?12*1024*1024:Webhooks.MaximumBodyBytes))throw new WebhookException(413,uploadBody?"Le fichier depasse la limite d'ajout.":"Le corps JSON depasse la limite autorisee.");
            if(request.Method=="POST"){
                if(!request.Headers.ContainsKey("Content-Length"))throw new WebhookException(411,"Content-Length est obligatoire.");
                string contentType;if(!request.Headers.TryGetValue("Content-Type",out contentType)||!Regex.IsMatch(contentType,@"\Aapplication/json(?:; *charset=utf-8)?\z",RegexOptions.IgnoreCase))throw new WebhookException(415,"Content-Type application/json est obligatoire.");
            }else if(length!=0)throw new WebhookException(400,"Un GET ne doit pas contenir de corps.");
            // Authenticate the complete, unambiguous header set before either
            // allocating or waiting for a potentially hostile request body.
            // Admission performs authorization again after reading the body.
            if(authorizeHeaders!=null)authorizeHeaders(request);
            if(uploadBody||PortalManagementHttp.IsArchiveImport(request.Path))deadline=DateTime.UtcNow.AddSeconds(60);
            ReadDeadline(stream,deadline);byte[] bytes=new byte[length];int at=0;
            while(at<length){ReadDeadline(stream,deadline);int read=stream.Read(bytes,at,length-at);if(read==0)throw new WebhookException(400,"Corps HTTP incomplet.");at+=read;}
            try{request.Body=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){throw new WebhookException(400,"Le corps doit etre encode en UTF-8 valide.");}finally{Array.Clear(bytes,0,bytes.Length);}
            return request;
        }
        private static WebhookHttpResponse Error(int status,string message){return new WebhookHttpResponse{Status=status,Body=Json.Write(new{error=message})};}
        private static Match AttachmentPath(string path)
        {return Regex.Match(path??"",@"\A/v1/hooks/([a-z0-9][a-z0-9-]{1,62})/(?:instances/(local|[a-f0-9]{32})/)?attachments\z");}
        internal static PortalInstanceGrant RouteGrant(WebhookRoute route)
        {return new PortalInstanceGrant{InstanceId=route.InstanceId,AccountKey=route.AccountKey,Home=route.Home,Workspace=route.Workspace,Access=route.Access,Model=route.Model,ReasoningEffort=route.ReasoningEffort,PermissionProfile=route.PermissionProfile};}
        private static PortalInstanceGrant AttachmentGrant(RelayStore store,Match path,string bearer)
        {
            string slug=path.Groups[1].Value,instance=path.Groups[2].Value;
            if(instance.Length>0)return Portal.RequireGrant(store,slug,bearer,instance);
            var route=Webhooks.Authorize(store,slug,bearer);var grant=RouteGrant(route);new PortalNative(store).Verify(grant);return grant;
        }
        private static WebhookHttpResponse AttachmentHandle(RelayStore store,WebhookHttpRequest request,Match path)
        {
            try{
                var route=Webhooks.Authorize(store,path.Groups[1].Value,Bearer(request));var grant=AttachmentGrant(store,path,Bearer(request));
                if(request.Method!="POST")return Error(405,"Methode non autorisee.");
                string key;request.Headers.TryGetValue("Idempotency-Key",out key);
                var result=PortalAttachments.Upload(store,route,grant,key,request.Body);
                return new WebhookHttpResponse{Status=201,Body=Json.Write(result)};
            }catch(WebhookException ex){return Error(ex.StatusCode,ex.Message);}catch(IOException){return Error(507,"Le fichier n'a pas pu etre conserve. Verifiez l'espace et les droits du dossier de pieces jointes du Switcher.");}catch(InvalidOperationException){return Error(409,"L'instance ou l'autorisation du fichier a change. Actualisez avant de reessayer.");}
        }
        private static Match PortalPath(string path)
        {return Regex.Match(path??"",@"\A/v1/hooks/([a-z0-9][a-z0-9-]{1,62})/instances(?:/(local|[a-f0-9]{32})(?:/(capabilities|chats)(?:/([a-f0-9]{32}|[a-f0-9]{8}-[a-f0-9]{4}-7[a-f0-9]{3}-[a-f0-9]{4}-[a-f0-9]{12})(?:/(messages|events))?)?|/tools/([a-f0-9]{64})/(widget|call))?)?\z");}
        private static WebhookHttpResponse PortalHandle(RelayStore store,WebhookHttpRequest request,Match path)
        {
            string slug=path.Groups[1].Value,instance=path.Groups[2].Value,area=path.Groups[3].Value,chat=path.Groups[4].Value,action=path.Groups[5].Value,tool=path.Groups[6].Value,toolAction=path.Groups[7].Value,bearer=Bearer(request);
            try{
                if(instance.Length==0){var value=Portal.ListInstances(store,slug,bearer);return request.Method=="GET"?new WebhookHttpResponse{Status=200,Body=Json.Write(value)}:Error(405,"Methode non autorisee.");}
                Portal.RequireGrant(store,slug,bearer,instance);
                if(request.Method=="GET"&&area=="chats"&&chat.Length==0)return new WebhookHttpResponse{Status=200,Body=Json.Write(Portal.ListChats(store,slug,bearer,instance))};
                if(request.Method=="GET"&&area=="chats"&&chat.Length>0&&action.Length==0)return new WebhookHttpResponse{Status=200,Body=Json.Write(Portal.Snapshot(store,slug,bearer,instance,chat))};
                if(request.Method=="GET"&&area=="chats"&&action=="events")return Error(426,"Cette adresse demande une connexion WebSocket authentifiee.");
                if(request.Method=="POST"&&area=="chats"&&(chat.Length==0||action=="messages")){
                    string key;request.Headers.TryGetValue("Idempotency-Key",out key);
                    var job=chat.Length==0?Portal.Create(store,slug,bearer,instance,key,request.Body):Portal.Send(store,slug,bearer,instance,chat,key,request.Body);
                    return new WebhookHttpResponse{Status=202,Body=PublicJob(job),Location="/v1/hooks/"+slug+"/jobs/"+job.Id};
                }
                using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60))){
                    object result=null;
                    if(request.Method=="GET"&&area=="capabilities"&&chat.Length==0)result=PortalMcpCapabilities.Get(store,slug,bearer,instance,deadline.Token).GetAwaiter().GetResult();
                    else if(request.Method=="GET"&&toolAction=="widget")result=PortalMcpCapabilities.Widget(store,slug,bearer,instance,tool,deadline.Token).GetAwaiter().GetResult();
                    else if(request.Method=="POST"&&toolAction=="call"){
                        Dictionary<string,object> body;try{body=Json.Obj(Json.Read<object>(request.Body));}catch(Exception){throw new WebhookException(400,"Arguments MCP invalides.");}
                        if(body.Count!=2||!body.ContainsKey("toolName")||!body.ContainsKey("args")||!(body["toolName"] is string)||!Regex.IsMatch(Json.Str(body["toolName"]),@"\A[A-Za-z0-9_.:/-]{1,256}\z")||!(body["args"] is IDictionary<string,object>))throw new WebhookException(400,"Indiquez uniquement un toolName valide et un objet args pour cet outil.");
                        result=PortalMcpCapabilities.CallReadOnly(store,slug,bearer,instance,tool,Json.Str(body["toolName"]),body["args"],deadline.Token).GetAwaiter().GetResult();
                    }
                    if(result!=null)return new WebhookHttpResponse{Status=200,Body=Json.Write(result)};
                }
                return Error(405,"Methode non autorisee pour cette adresse.");
            }catch(WebhookException ex){return Error(ex.StatusCode,ex.Message);}catch(OperationCanceledException){return Error(504,"La connexion a cette instance a expire. Reessayez.");}catch(TunnelInteractionException){return Error(409,"Cette action attend une intervention native dans Codex.");}catch(HeadlessRpcException){return Error(502,"Codex n'a pas pu charger cette ressource. Verifiez la connexion du plugin dans cette instance.");}catch(InvalidOperationException){return Error(409,"Cette ressource ou action n'est pas disponible pour cette instance. Verifiez sa connexion et ses droits dans Codex.");}
        }
        internal static WebhookHttpResponse Handle(RelayStore store,WebhookHttpRequest request)
        {
            if(request.Method=="GET"&&request.Path=="/health")return new WebhookHttpResponse{Status=200,Body="{\"status\":\"ok\"}"};
            if(PortalAdministration.Path(request.Path).Success)return PortalAdministrationHttp.Handle(store,request);
            var attachment=AttachmentPath(request.Path);if(attachment.Success)return AttachmentHandle(store,request,attachment);
            var portal=PortalPath(request.Path);if(portal.Success)return PortalHandle(store,request,portal);
            var chat=Regex.Match(request.Path??"",@"\A/v1/hooks/([a-z0-9][a-z0-9-]{1,62})/chats(?:/([a-f0-9]{32})(?:/(messages|events))?)?\z");
            if(chat.Success){
                string slug=chat.Groups[1].Value,id=chat.Groups[2].Value,action=chat.Groups[3].Value,chatBearer=Bearer(request);
                try{
                    // Keep method errors behind authentication as well.
                    Webhooks.ValidateAuthorization(store,slug,chatBearer);
                    if(request.Method=="GET"&&!chat.Groups[2].Success)return new WebhookHttpResponse{Status=200,Body=Json.Write(WebhookChats.List(store,slug,chatBearer))};
                    if(request.Method=="GET"&&chat.Groups[2].Success&&action=="")return new WebhookHttpResponse{Status=200,Body=Json.Write(WebhookChats.Snapshot(store,slug,chatBearer,id))};
                    if(request.Method=="POST"&&action=="messages"){
                        string idempotency;request.Headers.TryGetValue("Idempotency-Key",out idempotency);
                        var followup=WebhookChats.Send(store,slug,chatBearer,id,idempotency,request.Body);
                        return new WebhookHttpResponse{Status=202,Body=PublicJob(followup),Location="/v1/hooks/"+slug+"/jobs/"+followup.Id};
                    }
                    if(request.Method=="GET"&&action=="events")return Error(426,"Cette adresse demande une connexion WebSocket authentifiee.");
                    return Error(405,"Methode non autorisee pour cette adresse.");
                }catch(WebhookException ex){return Error(ex.StatusCode,ex.Message);}
            }
            var match=Regex.Match(request.Path??"",@"\A/v1/hooks/([a-z0-9][a-z0-9-]{1,62})/jobs(?:/([a-f0-9]{32}))?\z");
            if(!match.Success)return Error(404,"Adresse introuvable.");
            string authorization;request.Headers.TryGetValue("Authorization",out authorization);
            string bearer=authorization!=null&&authorization.StartsWith("Bearer ",StringComparison.Ordinal)?authorization.Substring(7):null;
            try{
                WebhookJob job;
                if(request.Method=="POST"&&!match.Groups[2].Success){string idempotency;request.Headers.TryGetValue("Idempotency-Key",out idempotency);job=Webhooks.Submit(store,match.Groups[1].Value,bearer,idempotency,request.Body);return new WebhookHttpResponse{Status=202,Body=PublicJob(job),Location="/v1/hooks/"+match.Groups[1].Value+"/jobs/"+job.Id};}
                if(request.Method=="GET"&&match.Groups[2].Success){job=Webhooks.ReadAuthorized(store,match.Groups[1].Value,bearer,match.Groups[2].Value);return new WebhookHttpResponse{Status=200,Body=PublicJob(job)};}
                return Error(405,"Methode non autorisee pour cette adresse.");
            }catch(WebhookException ex){return Error(ex.StatusCode,ex.Message);}
        }
        private static string PublicJob(WebhookJob job)
        {
            string result=job.Result;if(result!=null&&result.Length>32768)result=result.Substring(0,32768);
            return Json.Write(new{id=job.Id,title=job.Title,state=job.State,threadId=job.ThreadId,relayJobId=job.RelayJobId,createdUtc=job.CreatedUtc,updatedUtc=job.UpdatedUtc,error=job.Error,result=result,options=job.Options==null?null:Json.Read<object>(job.Options.Canonical())});
        }
        private static void WriteResponse(Stream stream,WebhookHttpResponse response)
        {
            byte[] body=Encoding.UTF8.GetBytes(response.Body??"{}");string reason=response.Status==200?"OK":response.Status==201?"Created":response.Status==202?"Accepted":"Error";
            var header=new StringBuilder("HTTP/1.1 "+response.Status.ToString(CultureInfo.InvariantCulture)+" "+reason+"\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\nContent-Length: "+body.Length.ToString(CultureInfo.InvariantCulture)+"\r\n");
            if(response.Location!=null)header.Append("Location: "+response.Location+"\r\n");if(response.Status==401)header.Append("WWW-Authenticate: Bearer\r\n");if(response.Status==429)header.Append("Retry-After: 60\r\n");if(response.Status==426)header.Append("Upgrade: websocket\r\nSec-WebSocket-Version: 13\r\n");header.Append("\r\n");
            byte[] bytes=Encoding.ASCII.GetBytes(header.ToString());stream.Write(bytes,0,bytes.Length);stream.Write(body,0,body.Length);stream.Flush();
        }
        public void Dispose()
        {
            stop.Cancel();listener.Stop();lock(sockets)foreach(var socket in sockets)try{socket.Close();}catch(SocketException){}
            if(accepting!=null)try{accepting.Wait(2000);}catch(AggregateException){}
            Task[] pending;lock(clients)pending=clients.ToArray();try{Task.WaitAll(pending,2000);}catch(AggregateException){}
            // A client can finish after the bounded wait; its release remains safe.
            stop.Dispose();
        }
    }
}
