using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class ResourceLinksTests
{
    private static object Obj(object o){return Json.Read<object>(Json.Write(o));}
    private static void Assert(bool ok,string text){if(!ok)throw new Exception(text);}
    private static void Refused(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected refusal");}
    private sealed class Fixture : IDisposable
    {
        internal RelayStore Store;internal ToolTunnel Tunnel;internal SharedResourceLinks Links;internal ResourceLinkServer Server;
        internal RelaySession Session;internal TunnelGrant Grant;internal TunnelOwner Owner;internal int Opened;internal object Target;
        internal Fixture(string root)
        {
            string dir=Path.Combine(root,"links-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);Store=new RelayStore(dir);
            Owner=new TunnelOwner{Id="owner",Name="Propriétaire",Home=Path.Combine(dir,"owner"),Account="account-owner"};
            Session=new RelaySession{Token="do-not-store-this-secret",Channel="source",Thread="chat",Home=Path.Combine(dir,"client"),Account="account-client"};
            Tunnel=new ToolTunnel(Store,i=>Owner,o=>{throw new Exception("No provider call expected for Page navigation");},s=>{});
            Grant=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Instance="owner",Name="DGD",Enabled=true,ResourceField="page_id",ResourceValues=new List<string>{"page-1"},ResourceLabels=new Dictionary<string,string>{{"page-1","DGD [éà😀]"}},Sources=new Dictionary<string,string>{{Session.Home,Session.Account}},Tools=new List<TunnelTool>{new TunnelTool{Server=SharedPages.Server,Name=SharedPages.ReadTool,ReadOnly=true,Schema=Obj(new{type="object",properties=new{page_id=new{type="string"}}})}}};Tunnel.SaveGrant(Grant);
            Links=new SharedResourceLinks(Store,Tunnel,(g,t,c)=>{Opened++;Target=Obj(t);return Task.FromResult<object>(new{requested=true});});Server=ResourceLinkServer.Start(Store,Links);
        }
        internal ResourceLinkInfo Link(){return Links.Create(Session,Grant.Id,"page-1");}
        internal string Id(ResourceLinkInfo link){return new Uri(link.Url).AbsolutePath.Substring(3);}
        internal string Request(string path,string method="GET",string host=null,string origin=null,string extra="")
        {
            using(var client=new TcpClient()){client.Connect("127.0.0.1",Server.Port);client.ReceiveTimeout=10000;var stream=client.GetStream();string authority="127.0.0.1:"+Server.Port;
                byte[] request=Encoding.ASCII.GetBytes(method+" "+path+" HTTP/1.1\r\nHost: "+(host??authority)+"\r\n"+(origin==null?"":"Origin: "+origin+"\r\n")+extra+"Connection: close\r\n\r\n");stream.Write(request,0,request.Length);using(var reader=new StreamReader(stream,Encoding.UTF8))return reader.ReadToEnd();}
        }
        public void Dispose(){Server.Dispose();}
    }
    private static TunnelCall Site(object result){return new TunnelCall{State="completed",Result=Obj(new{structuredContent=result})};}
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("resource links reuse opaque handles without retaining session secrets",()=>{using(var f=new Fixture(root)){var a=f.Link();var b=f.Link();Assert(a.Url==b.Url&&a.Markdown.Contains("DGD \\[éà😀\\]"),"link/title mismatch");var ticket=f.Store.ReadRecord<List<ResourceLinkTicket>>("resource-links.dpapi").Single();Assert(ticket.Source.Token==null&&!a.Url.Contains("page-1")&&!a.Url.Contains("account"),"credential/resource leaked");Assert(f.Opened==0,"creation opened UI");}});
        check("resource link rejects unshared IDs and changed source accounts",()=>{using(var f=new Fixture(root)){Refused(()=>f.Links.Create(f.Session,f.Grant.Id,"page-2"));f.Session.Account="other";Refused(()=>f.Link());}});
        check("resource link revocation applies to existing links",()=>{using(var f=new Fixture(root)){var id=f.Id(f.Link());f.Tunnel.Disable(f.Grant.Id);Refused(()=>f.Links.Open(id,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Opened==0,"revoked navigation");}});
        check("resource link pins owner identity even if grant is replaced",()=>{using(var f=new Fixture(root)){var id=f.Id(f.Link());f.Owner.Account="replacement";f.Tunnel.SaveGrant(f.Grant);Refused(()=>f.Links.Open(id,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Opened==0,"replacement account opened");}});
        check("resource link checks read permission again at click",()=>{using(var f=new Fixture(root)){var id=f.Id(f.Link());f.Grant.Tools[0].Name=SharedPages.EditTool;f.Grant.Tools[0].ReadOnly=false;f.Tunnel.SaveGrant(f.Grant);Refused(()=>f.Links.Open(id,CancellationToken.None).GetAwaiter().GetResult());}});
        check("resource links use native Page target without reading editing or prompting",()=>{using(var f=new Fixture(root)){f.Links.Open(f.Id(f.Link()),CancellationToken.None).GetAwaiter().GetResult();Assert(f.Opened==1&&Json.Str(Json.Get(f.Target,"type"))=="page"&&Json.Str(Json.Get(f.Target,"pageId"))=="page-1","wrong target");}});
        check("link server GET and HEAD never trigger navigation",()=>{using(var f=new Fixture(root)){string path=new Uri(f.Link().Url).AbsolutePath;Assert(f.Request(path).Contains("200 OK")&&f.Request(path,"HEAD").Contains("200 OK")&&f.Opened==0,"prefetch navigated");}});
        check("link server POST enforces same origin and Host against rebinding",()=>{using(var f=new Fixture(root)){string path=new Uri(f.Link().Url).AbsolutePath;Assert(f.Request(path,"POST").Contains("403 Error"),"missing origin accepted");Assert(f.Request(path,"POST",origin:"https://evil.example").Contains("403 Error"),"foreign origin accepted");Assert(f.Request(path,host:"evil.example").Contains("400 Error"),"rebinding accepted");Assert(f.Request(path,"POST",origin:"http://127.0.0.1:"+f.Server.Port,extra:"Sec-Fetch-Site: cross-site\r\n").Contains("403 Error"),"cross-site accepted");Assert(f.Opened==0,"unauthorized open");}});
        check("link server authorized POST opens once and denies unknown IDs",()=>{using(var f=new Fixture(root)){string path=new Uri(f.Link().Url).AbsolutePath;Assert(f.Request(path,"POST",origin:"http://127.0.0.1:"+f.Server.Port).Contains("200 OK")&&f.Opened==1,"no navigation");Assert(f.Request("/r/"+new string('a',64)).Contains("404 Error"),"unknown ID accepted");Assert(f.Request(path+"?token=x").Contains("400 Error"),"query accepted");}});
        check("link landing escapes titles and has no editor or page body",()=>{var html=ResourceLinkServer.Html(new ResourceLinkInfo{Title="</h1><script>bad()</script>",Owner="<img>"},"nonce");Assert(!html.Contains("<script>bad")&&html.Contains("&lt;img&gt;")&&!html.Contains("textarea")&&!html.Contains("contenteditable"),"unsafe/extra UI");Assert(html.Contains("visibilityState==='visible'")&&html.Contains("window.top===window")&&html.Contains("!document.prerendering"),"prefetch protection absent");});
        check("resource server keeps stable port and tickets across restart",()=>{using(var f=new Fixture(root)){var link=f.Link();int port=f.Server.Port;f.Server.Dispose();Refused(()=>ResourceLinkServer.Ready(f.Store));f.Server=ResourceLinkServer.Start(f.Store,f.Links);Assert(f.Server.Port==port&&f.Link().Url==link.Url,"old link broken after restart");}});
        check("Site navigation prefers attached native Page over URLs",()=>{var target=Obj(SharedResourceLinks.SiteTarget(Site(new{id="site",attached_page_id="page",current_live_url="https://example.com"}),"site"));Assert(Json.Str(Json.Get(target,"pageId"))=="page","wrong target");});
        check("Site navigation uses verified HTTPS URL only when no Page exists",()=>{var target=Obj(SharedResourceLinks.SiteTarget(Site(new{id="site",current_preview_url="https://example.com/preview"}),"site"));Assert(Json.Str(Json.Get(target,"url"))=="https://example.com/preview","wrong URL");foreach(var url in new[]{"javascript:bad()","http://example.com","https://user:secret@example.com","https://example.com/?token=secret","https://example.com/#secret"})Refused(()=>SharedResourceLinks.SiteTarget(Site(new{id="site",current_live_url=url}),"site"));});
        check("Site navigation rejects wrong ID unpublished Site and provider errors",()=>{Refused(()=>SharedResourceLinks.SiteTarget(Site(new{id="wrong",attached_page_id="page"}),"site"));Refused(()=>SharedResourceLinks.SiteTarget(Site(new{id="site",expected_url="https://example.com"}),"site"));Refused(()=>SharedResourceLinks.SiteTarget(new TunnelCall{State="tool_error"},"site"));});
        check("Site bypass bearer credentials are redacted from durable results",()=>{bool sensitive=false;var value=ToolTunnel.Redact(Obj(new{siwc_bypass_bearer_token="secret"}),ref sensitive);Assert(sensitive&&!Json.Write(value).Contains("secret"),"bearer persisted");});
    }
}
