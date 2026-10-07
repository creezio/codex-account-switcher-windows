using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class ResourceLinkState
    {
        public int Port {get;set;}
        public int Pid {get;set;}
        public long Started {get;set;}
    }
    // A loopback-only navigation bridge, not a content proxy or a Page editor.
    // GET is inert. Navigation requires a same-origin POST from the visible tab.
    internal sealed class ResourceLinkServer : IDisposable
    {
        private readonly RelayStore store;
        private readonly SharedResourceLinks links;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop=new CancellationTokenSource();
        private readonly SemaphoreSlim slots=new SemaphoreSlim(4);
        internal readonly int Port;
        private ResourceLinkServer(RelayStore data,SharedResourceLinks service)
        {
            store=data;links=service??new SharedResourceLinks(store);
            var saved=store.ReadRecord<ResourceLinkState>("resource-link-server.dpapi");
            listener=new TcpListener(IPAddress.Loopback,saved.Port);listener.ExclusiveAddressUse=true;
            try{listener.Start(8);}catch(SocketException){throw new InvalidOperationException("Le port local des liens est occupé. Fermez l'autre Switcher ou libérez ce port, puis relancez le Switcher.");}
            Port=((IPEndPoint)listener.LocalEndpoint).Port;
            using(var p=Process.GetCurrentProcess())store.WriteRecord("resource-link-server.dpapi",new ResourceLinkState{Port=Port,Pid=p.Id,Started=p.StartTime.ToUniversalTime().Ticks});
        }
        internal static ResourceLinkServer Start(RelayStore store,SharedResourceLinks service=null)
        {var server=new ResourceLinkServer(store,service);Task.Run(()=>server.Accept());return server;}
        internal static ResourceLinkState Ready(RelayStore store)
        {
            var s=store.ReadRecord<ResourceLinkState>("resource-link-server.dpapi");
            if(s.Port<1||s.Port>65535||!DesktopRuntime.SameProcess(s.Pid,s.Started))throw new InvalidOperationException("Ouvrez Account Switcher pour créer et utiliser les liens vers l'instance propriétaire.");return s;
        }
        internal static bool ValidId(string id){return Regex.IsMatch(id??"","\u005e[a-f0-9]{64}$");}
        private async Task Accept()
        {
            try{
                while(!stop.IsCancellationRequested){
                    await slots.WaitAsync(stop.Token).ConfigureAwait(false);TcpClient client;
                    try{client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);}catch{slots.Release();throw;}
                    // Serve catches per-client errors. No unbounded queued clients.
                    var unused=Serve(client);GC.KeepAlive(unused);
                }
            }catch(OperationCanceledException){}catch(ObjectDisposedException){}catch(SocketException){}
        }
        private async Task Serve(TcpClient client)
        {
            using(client)using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token)){
                timeout.CancelAfter(30000);
                using(timeout.Token.Register(()=>client.Close()))try{
                    var stream=client.GetStream();var bytes=new List<byte>();var one=new byte[1];
                    while(bytes.Count<16384){int n=await stream.ReadAsync(one,0,1,timeout.Token).ConfigureAwait(false);if(n==0)return;bytes.Add(one[0]);int k=bytes.Count;if(k>=4&&bytes[k-4]==13&&bytes[k-3]==10&&bytes[k-2]==13&&bytes[k-1]==10)break;}
                    string raw=Encoding.ASCII.GetString(bytes.ToArray());
                    if(!raw.EndsWith("\r\n\r\n",StringComparison.Ordinal)){await Reply(stream,400,"text/plain","Requête trop longue.",false,null,timeout.Token);return;}
                    var lines=raw.Split(new[]{"\r\n"},StringSplitOptions.None);var first=lines[0].Split(' ');var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);bool valid=first.Length==3&&first[2]=="HTTP/1.1";
                    for(int i=1;i<lines.Length&&lines[i].Length>0;i++){int colon=lines[i].IndexOf(':');if(colon<=0){valid=false;break;}string key=lines[i].Substring(0,colon),value=lines[i].Substring(colon+1).Trim();if(headers.ContainsKey(key)){valid=false;break;}headers[key]=value;}
                    string host,origin,length;string authority="127.0.0.1:"+Port;
                    valid=valid&&headers.TryGetValue("Host",out host)&&host==authority&&!headers.ContainsKey("Transfer-Encoding")&&(!headers.TryGetValue("Content-Length",out length)||length=="0");
                    if(!valid||first[1].Length!=67||!first[1].StartsWith("/r/",StringComparison.Ordinal)||!ValidId(first[1].Substring(3))){await Reply(stream,400,"text/plain","Lien ou requête invalide.",false,null,timeout.Token);return;}
                    string id=first[1].Substring(3),method=first[0];
                    if(method=="POST"){
                        string site;if(!headers.TryGetValue("Origin",out origin)||origin!="http://"+authority||(headers.TryGetValue("Sec-Fetch-Site",out site)&&site!="same-origin")){await Reply(stream,403,"text/plain","Origine refusée.",false,null,timeout.Token);return;}
                        string failure=null;try{await links.Open(id,timeout.Token).ConfigureAwait(false);}catch(Exception e){failure=Program.SafeError(e);}
                        await Reply(stream,failure==null?200:409,"text/plain",failure??"Ouverture demandée dans le Codex propriétaire. Vous pouvez fermer cet onglet.",false,null,timeout.Token);return;
                    }
                    if(method!="GET"&&method!="HEAD"){await Reply(stream,405,"text/plain","Méthode refusée.",false,null,timeout.Token);return;}
                    string error=null,html=null,nonce=Guid.NewGuid().ToString("N");try{
                        html=Html(links.Inspect(id),nonce);
                    }catch(Exception e){error=Program.SafeError(e);}
                    await Reply(stream,error==null?200:404,error==null?"text/html":"text/plain",error??html,method=="HEAD",error==null?nonce:null,timeout.Token);
                }catch(IOException){}catch(ObjectDisposedException){}catch(OperationCanceledException){}catch(SocketException){}finally{slots.Release();}
            }
        }
        private static async Task Reply(NetworkStream stream,int status,string type,string content,bool head,string nonce,CancellationToken token)
        {
            byte[] body=Encoding.UTF8.GetBytes(content);string csp="default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'; connect-src 'self'; style-src 'unsafe-inline'"+(nonce==null?"":"; script-src 'nonce-"+nonce+"'");
            byte[] headers=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" "+(status==200?"OK":"Error")+"\r\nContent-Type: "+type+"; charset=utf-8\r\nContent-Length: "+body.Length+"\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: "+csp+"\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers,0,headers.Length,token).ConfigureAwait(false);if(!head)await stream.WriteAsync(body,0,body.Length,token).ConfigureAwait(false);
        }
        internal static string Html(ResourceLinkInfo info,string nonce)
        {
            return "<!doctype html><html lang=fr><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>"+WebUtility.HtmlEncode(info.Title)+"</title><style>body{font:16px system-ui;margin:10vh auto;padding:24px;max-width:620px;color:#18332f;background:#f5f8f7}button{padding:12px 20px;border:0;border-radius:8px;background:#087f70;color:white;cursor:pointer}</style><h1>"+WebUtility.HtmlEncode(info.Title)+"</h1><p>Ouverture dans « "+WebUtility.HtmlEncode(info.Owner)+" »</p><p id=status role=status>Connexion au Codex propriétaire…</p><button id=open>Ouvrir dans Codex</button><noscript>Activez JavaScript pour ouvrir cette ressource dans Codex.</noscript><script nonce='"+nonce+"'>"+
                "const b=document.getElementById('open'),s=document.getElementById('status');let started=false,busy=false;async function open(){if(busy)return;busy=true;started=true;b.disabled=true;try{const r=await fetch(location.pathname,{method:'POST',credentials:'omit',cache:'no-store'});s.textContent=await r.text();}catch(e){s.textContent='Account Switcher est fermé ou indisponible. Ouvrez-le puis réessayez.';}finally{busy=false;b.disabled=false;}}b.addEventListener('click',open);function visible(){if(!started&&window.top===window&&!document.prerendering&&document.visibilityState==='visible')open();}document.addEventListener('visibilitychange',visible);document.addEventListener('prerenderingchange',visible);visible();</script></html>";
        }
        public void Dispose()
        {
            if(stop.IsCancellationRequested)return;stop.Cancel();listener.Stop();
            var state=store.ReadRecord<ResourceLinkState>("resource-link-server.dpapi");
            using(var p=Process.GetCurrentProcess())if(state.Pid==p.Id){state.Pid=0;state.Started=0;store.WriteRecord("resource-link-server.dpapi",state);}
        }
    }
}
