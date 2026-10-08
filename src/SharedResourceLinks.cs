using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class ResourceLinkTicket
    {
        public string Id {get;set;}
        public string Share {get;set;}
        public string Resource {get;set;}
        public string Field {get;set;}
        public string Owner {get;set;}
        public string OwnerAccount {get;set;}
        public string OwnerHome {get;set;}
        public RelaySession Source {get;set;}
    }
    public sealed class ResourceLinkInfo
    {
        public string Title {get;set;}
        public string Owner {get;set;}
        public string Kind {get;set;}
        public string Url {get;set;}
        public string Markdown {get;set;}
    }
    // Links carry an opaque local handle, never a Codex credential, Page body or
    // provider URL. Access is revalidated on every click against the live grant.
    internal sealed class SharedResourceLinks
    {
        internal const string SiteRead="sites.get_site";
        private readonly RelayStore store;
        private readonly ToolTunnel tunnel;
        private readonly Func<TunnelGrant,object,CancellationToken,Task<object>> navigate;
        internal SharedResourceLinks(RelayStore data,ToolTunnel transport=null,Func<TunnelGrant,object,CancellationToken,Task<object>> opener=null)
        {store=data;tunnel=transport??new ToolTunnel(data);navigate=opener??Navigate;}
        private TunnelGrant Grant(RelaySession session,string share,string resource)
        {
            ToolTunnel.Bound(resource,512,"Ressource");var g=tunnel.Authorize(session,share);
            string read=g.ResourceField=="page_id"?SharedPages.ReadTool:g.ResourceField=="project_id"?SiteRead:null;
            if(read==null||!g.Tools.Any(t=>t.Server==SharedPages.Server&&t.Name==read&&t.ReadOnly))
                throw new InvalidOperationException("Ce lien nécessite un partage de Page ou de Site avec son outil de lecture autorisé.");
            ToolTunnel.CheckResource(g,new Dictionary<string,object>{{g.ResourceField,resource}});
            ToolTunnel.CheckOwner(new TunnelOwner{Id=g.Instance,Account=g.Account,Home=g.Home},tunnel.Owner(g.Instance));return g;
        }
        private static string Label(TunnelGrant g,string resource)
        {string label;return g.ResourceLabels!=null&&g.ResourceLabels.TryGetValue(resource,out label)?label:g.Name;}
        internal ResourceLinkInfo Create(RelaySession session,string share,string resource)
        {
            var g=Grant(session,share,resource);var state=ResourceLinkServer.Ready(store);
            ResourceLinkTicket ticket;
            using(store.Lease("resource-links")){
                var links=store.ReadRecord<List<ResourceLinkTicket>>("resource-links.dpapi");
                ticket=links.FirstOrDefault(x=>x.Share==share&&x.Resource==resource&&x.Owner==g.Instance&&x.OwnerAccount==g.Account&&RelayStore.SamePath(x.OwnerHome,g.Home)&&x.Source.Account==session.Account&&RelayStore.SamePath(x.Source.Home,session.Home));
                if(ticket==null){
                    if(links.Count>=1000)throw new InvalidOperationException("La limite de liens locaux est atteinte.");
                    ticket=new ResourceLinkTicket{Id=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N"),Share=share,Resource=resource,Field=g.ResourceField,Owner=g.Instance,OwnerAccount=g.Account,OwnerHome=g.Home};links.Add(ticket);
                }
                // Do not retain the MCP session token. A local click is authorized
                // by the current account/channel and grant, not by a copied token.
                ticket.Source=new RelaySession{Home=session.Home,Account=session.Account,Channel=session.Channel,Thread=session.Thread};
                store.WriteRecord("resource-links.dpapi",links);
            }
            string title=Label(g,resource),url="http://127.0.0.1:"+state.Port+"/r/"+ticket.Id;
            return new ResourceLinkInfo{Title=title,Owner=tunnel.CurrentName(g),Kind=g.ResourceField=="page_id"?"Page":"Site",Url=url,Markdown="["+EscapeLabel(title)+"]("+url+")"};
        }
        internal static string EscapeLabel(string value)
        {return value.Replace("\\","\\\\").Replace("[","\\[").Replace("]","\\]").Replace("\r"," ").Replace("\n"," ").Replace("<","&lt;").Replace(">","&gt;");}
        private ResourceLinkTicket Ticket(string id)
        {
            if(!ResourceLinkServer.ValidId(id))throw new InvalidOperationException("Lien inconnu.");
            var ticket=store.ReadRecord<List<ResourceLinkTicket>>("resource-links.dpapi").SingleOrDefault(x=>x.Id==id);
            if(ticket==null)throw new InvalidOperationException("Lien inconnu.");return ticket;
        }
        private TunnelGrant Validate(ResourceLinkTicket ticket)
        {
            var g=Grant(ticket.Source,ticket.Share,ticket.Resource);
            if(g.ResourceField!=ticket.Field)throw new InvalidOperationException("Le type de ressource a changé.");
            ToolTunnel.CheckOwner(new TunnelOwner{Id=ticket.Owner,Account=ticket.OwnerAccount,Home=ticket.OwnerHome},new TunnelOwner{Id=g.Instance,Account=g.Account,Home=g.Home});return g;
        }
        internal ResourceLinkInfo Inspect(string id)
        {var t=Ticket(id);var g=Validate(t);return new ResourceLinkInfo{Title=Label(g,t.Resource),Owner=tunnel.CurrentName(g),Kind=g.ResourceField=="page_id"?"Page":"Site"};}
        internal async Task<object> Open(string id,CancellationToken token)
        {
            var t=Ticket(id);var g=Validate(t);object target;
            if(g.ResourceField=="page_id")target=new{type="page",pageId=t.Resource};
            else{
                var call=await tunnel.Invoke(t.Source,Guid.NewGuid().ToString("N"),g.Id,SharedPages.Server,SiteRead,new Dictionary<string,object>{{"project_id",t.Resource}},token).ConfigureAwait(false);
                target=SiteTarget(call,t.Resource);
            }
            var current=Validate(t);if(current.Revision!=g.Revision)throw new InvalidOperationException("Les accès ont changé pendant l'ouverture. Cliquez de nouveau sur le lien.");
            return await navigate(current,target,token).ConfigureAwait(false);
        }
        internal static object SiteTarget(TunnelCall call,string project)
        {
            if(call.State!="completed"||Object.Equals(Json.Get(call.Result,"isError"),true))throw new InvalidOperationException("Le Site n'a pas pu être relu auprès du propriétaire.");
            object value=Json.Get(call.Result,"structuredContent");
            if(value==null)foreach(var row in RelayEngine.Rows(Json.Get(call.Result,"content")))if(Json.Str(Json.Get(row,"type"))=="text")try{value=Json.Read<object>(Json.Str(Json.Get(row,"text")));break;}catch(ArgumentException){}
            if(Json.Str(Json.Get(value,"id"))!=project||Json.Get(value,"error")!=null)throw new InvalidOperationException("La réponse ne confirme pas l'identité du Site.");
            string page=Json.Str(Json.Get(value,"attached_page_id"));
            if(!String.IsNullOrWhiteSpace(page))return new{type="page",pageId=page};
            string url=Json.Str(Json.Get(value,"current_live_url"));if(String.IsNullOrWhiteSpace(url))url=Json.Str(Json.Get(value,"current_preview_url"));Uri parsed;
            if(!Uri.TryCreate(url,UriKind.Absolute,out parsed)||parsed.Scheme!="https"||!String.IsNullOrEmpty(parsed.UserInfo)||!String.IsNullOrEmpty(parsed.Query)||!String.IsNullOrEmpty(parsed.Fragment))throw new InvalidOperationException("Le Site ne fournit pas de Page native ni d'adresse publiée ouvrable sans jeton.");
            return new{type="browser",url=parsed.AbsoluteUri};
        }
        private async Task<object> Navigate(TunnelGrant g,object target,CancellationToken token)
        {
            var c=store.Channels().Where(x=>x.Enabled&&AgentProviders.Codex(x)&&x.AccountKey==g.Account&&RelayStore.SamePath(x.Home,g.Home)&&DesktopRuntime.SameProcess(x.ServerPid,x.ServerStartTicks)&&!String.IsNullOrEmpty(x.AnchorThreadId)).OrderByDescending(x=>x.ConnectedUtc).FirstOrDefault();
            if(c==null)throw new InvalidOperationException("Ouvrez le Codex propriétaire et connectez son relais dans un chat existant.");
            var transport=new DesktopRelayTransport();var result=await transport.Call(c,"open_in_codex",new{target=target},token).ConfigureAwait(false);
            if(Object.Equals(Json.Get(result,"isError"),true))throw new InvalidOperationException("Codex a refusé l'ouverture de la ressource.");
            await transport.Call(c,"navigate_to_codex_page",new{threadId=c.AnchorThreadId},token).ConfigureAwait(false);
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,g.Instance);
            var window=DesktopWindows.Find(accounts.Vault.Root,instance);if(window!=null)DesktopWindows.Focus(window);
            return new{requested=true,owner=tunnel.CurrentName(g),navigation=result};
        }
    }
}
