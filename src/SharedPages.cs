using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class SharedPageBlock
    {
        public string Id {get;set;}
        public string Kind {get;set;}
        public string Hash {get;set;}
        public string Markdown {get;set;}
    }
    public sealed class SharedPageDocument
    {
        public string Share {get;set;}
        public string Page {get;set;}
        public string Title {get;set;}
        public string Owner {get;set;}
        public string ReadId {get;set;}
        public string ReadAt {get;set;}
        public bool CanEdit {get;set;}
        public SharedPageBlock[] Blocks {get;set;}
    }
    public sealed class SharedPageEntry
    {
        public string Share {get;set;}
        public string Page {get;set;}
        public string Title {get;set;}
        public string Owner {get;set;}
        public bool CanEdit {get;set;}
    }
    public sealed class SharedPageSave
    {
        public string Id {get;set;}
        public string State {get;set;}
        public string Message {get;set;}
        public SharedPageDocument Document {get;set;}
    }
    // One service for the desktop reader and MCP App. Every provider operation goes
    // through the same authenticated, resource-scoped, durable tunnel as agent calls.
    internal sealed class SharedPages
    {
        internal const string Server="codex_apps",ReadTool="chatgpt_space.read_page",EditTool="chatgpt_space.edit_page";
        private readonly RelayStore store;
        private readonly ToolTunnel tunnel;
        internal SharedPages(RelayStore data,ToolTunnel transport=null){store=data;tunnel=transport??new ToolTunnel(data);}
        private static object ObjectValue(object value){return Json.Read<object>(Json.Write(value));}
        private static bool Readable(TunnelGrant g){return g.Tools.Any(t=>t.Server==Server&&t.Name==ReadTool&&t.ReadOnly);}
        private static bool Editable(TunnelGrant g){return g.Tools.Any(t=>t.Server==Server&&t.Name==EditTool&&!t.ReadOnly);}
        internal SharedPageEntry[] List(RelaySession session)
        {
            // List verifies the source even when there are no matching grants.
            tunnel.List(session);
            return tunnel.Grants().Where(g=>ToolTunnel.CanUse(g,session)&&Readable(g)&&g.ResourceField=="page_id")
                .SelectMany(g=>(g.ResourceValues!=null&&g.ResourceValues.Count>0?g.ResourceValues:new List<string>{g.ResourceValue})
                    .Where(p=>!String.IsNullOrEmpty(p)).Select(p=>new SharedPageEntry{Share=g.Id,Page=p,Title=g.ResourceLabels!=null&&g.ResourceLabels.ContainsKey(p)?g.ResourceLabels[p]:g.Name,Owner=tunnel.CurrentName(g),CanEdit=Editable(g)}))
                .OrderBy(p=>p.Owner).ThenBy(p=>p.Title).ToArray();
        }
        private TunnelGrant Grant(RelaySession session,string share,string page)
        {
            ToolTunnel.Bound(page,512,"Page");var g=tunnel.Authorize(session,share);
            ToolTunnel.CheckResource(g,new Dictionary<string,object>{{"page_id",page}});
            if(!Readable(g))throw new InvalidOperationException("La lecture de cette Page n'est pas partagée avec cette instance.");
            ToolTunnel.CheckOwner(new TunnelOwner{Id=g.Instance,Account=g.Account,Home=g.Home},tunnel.Owner(g.Instance));return g;
        }
        private static object Payload(TunnelCall call)
        {
            if(call.State!="completed")throw new InvalidOperationException(call.Error??"La lecture de la Page a été refusée par le fournisseur.");
            var result=call.Result;object value=Json.Get(result,"structuredContent");
            if(value==null)foreach(var item in RelayEngine.Rows(Json.Get(result,"content")))if(Json.Str(Json.Get(item,"type"))=="text")try{value=Json.Read<object>(Json.Str(Json.Get(item,"text")));break;}catch(ArgumentException){}
            if(value==null||Object.Equals(Json.Get(result,"isError"),true)||Json.Get(value,"error")!=null)throw new InvalidOperationException("Le fournisseur n'a pas renvoyé de Page exploitable. Ouvrez-la dans l'instance propriétaire.");
            return value;
        }
        private static object Content(TunnelCall read,string page)
        {
            if(read.Tool!=Server+"/"+ReadTool)throw new InvalidOperationException("Une lecture complète de la Page est requise.");
            var value=Payload(read);var content=Json.Get(value,"content");var metadata=Json.Get(value,"metadata");
            if(Json.Str(Json.Get(content,"page_id"))!=page||Json.Str(Json.Get(metadata,"stream_kind"))=="scratch"||Json.Get(content,"blocks")==null||Json.Get(value,"block_excerpts")!=null||Json.Get(value,"selection")!=null)
                throw new InvalidOperationException("Cette vue nécessite une Page textuelle complète. Utilisez l'éditeur de l'instance propriétaire pour ce document.");
            return content;
        }
        private SharedPageDocument Document(RelaySession session,TunnelGrant grant,string page,TunnelCall read)
        {
            var c=Content(read,page);
            var blocks=RelayEngine.Rows(Json.Get(c,"blocks")).Select(b=>new SharedPageBlock{Id=Json.Str(Json.Get(b,"id")),Hash=Json.Str(Json.Get(b,"hash")),Kind=Json.Str(Json.Get(b,"kind")),Markdown=Json.Str(Json.Get(b,"markdown"))}).ToArray();
            if(blocks.Any(b=>String.IsNullOrEmpty(b.Id)||String.IsNullOrEmpty(b.Hash))||blocks.Select(b=>b.Id).Distinct().Count()!=blocks.Length)throw new InvalidOperationException("Le format de la Page a changé. Ouvrez l'éditeur propriétaire.");
            // Recheck on the way out: revocation during a read must not disclose its body.
            var current=Grant(session,grant.Id,page);
            if(current.Revision!=grant.Revision)throw new InvalidOperationException("Les accès ont changé pendant la lecture. Actualisez la liste.");
            return new SharedPageDocument{Share=grant.Id,Page=page,Title=Json.Str(Json.Get(c,"title")),Owner=tunnel.CurrentName(current),ReadId=read.Id,ReadAt=read.Updated,CanEdit=Editable(current),Blocks=blocks};
        }
        internal async Task<SharedPageDocument> Read(RelaySession session,string share,string page,CancellationToken token)
        {
            var g=Grant(session,share,page);
            var read=await tunnel.Invoke(session,Guid.NewGuid().ToString("N"),share,Server,ReadTool,new Dictionary<string,object>{{"page_id",page},{"view","full"}},token).ConfigureAwait(false);
            return Document(session,g,page,read);
        }
        internal async Task<SharedPageSave> Save(RelaySession session,string id,string share,string page,string readId,string blockId,string markdown,CancellationToken token)
        {
            RelayStore.MessageId(id);if(markdown==null||String.IsNullOrWhiteSpace(markdown)||Encoding.UTF8.GetByteCount(markdown)>80000)throw new InvalidOperationException("Saisissez entre 1 et 80 Ko de texte. La suppression de blocs se fait dans l'éditeur propriétaire.");
            var g=Grant(session,share,page);if(!Editable(g))throw new InvalidOperationException("Cette Page est partagée en lecture seule.");
            var read=tunnel.Read(session,readId);if(read.Grant!=share)throw new InvalidOperationException("La lecture appartient à un autre partage.");
            var content=Content(read,page);var blocks=RelayEngine.Rows(Json.Get(content,"blocks")).ToArray();
            object operation;
            if(String.IsNullOrEmpty(blockId)){
                operation=new{op="insert_markdown",markdown=markdown,at=new{kind="between_blocks",preceding_block_id=blocks.Length==0?null:Json.Str(Json.Get(blocks.Last(),"id")),following_block_id=(string)null}};
            }else{
                var block=blocks.SingleOrDefault(b=>Json.Str(Json.Get(b,"id"))==blockId);
                if(block==null||Json.Str(Json.Get(block,"kind"))!="markdown"||String.IsNullOrEmpty(Json.Str(Json.Get(block,"hash"))))throw new InvalidOperationException("Ce bloc n'est pas modifiable dans cette vue.");
                if(Json.Str(Json.Get(block,"markdown"))==markdown)throw new InvalidOperationException("Le texte n'a pas changé.");
                operation=new{op="replace_block_markdown",block_id=blockId,expected_hash=Json.Str(Json.Get(block,"hash")),markdown=markdown};
            }
            var args=new Dictionary<string,object>{{"page_id",page},{"stream_kind","content"},{"operations",new[]{operation}}};
            if(Json.Get(content,"through_sequence")!=null)args["base_sequence"]=Json.Get(content,"through_sequence");
            var call=await tunnel.Invoke(session,id,share,Server,EditTool,ObjectValue(args),token).ConfigureAwait(false);
            if(call.Result==null&&call.HasResult)call=tunnel.Read(session,id);
            var outcome=new SharedPageSave{Id=id,State=call.State,Message=call.Error};
            if(call.State!="completed"){outcome.Message=call.Error??"Enregistrement refusé ou conflit de version. Votre brouillon est conservé. Relisez la Page avant une nouvelle modification.";return outcome;}
            object receipt;
            try{receipt=Payload(call);}catch(InvalidOperationException e){outcome.State="uncertain";outcome.Message=e.Message;return outcome;}
            var ops=RelayEngine.Rows(Json.Get(receipt,"operation_results")).ToArray();
            if(ops.Length!=1||ops.Any(o=>(Json.Get(o,"status")!=null&&Json.Str(Json.Get(o,"status"))!="applied")||Json.Get(o,"ignored_reason")!=null||Json.Get(o,"error_code")!=null)){
                outcome.State="uncertain";outcome.Message="Le reçu ne confirme pas l'application du changement. Relisez la Page ; aucun renvoi automatique.";return outcome;
            }
            outcome.State="saved";outcome.Message="Enregistrement confirmé sur la Page originale.";
            try{outcome.Document=await Read(session,share,page,token).ConfigureAwait(false);outcome.Message+=" Vue actualisée.";}
            catch(Exception e){outcome.State="saved_unverified";outcome.Message+=" La relecture a échoué : "+Program.SafeError(e)+" Ne renvoyez pas cette modification.";}
            return outcome;
        }
        internal RelaySession DesktopSession(string instanceId)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,instanceId);NamedInstances.VerifyAccount(accounts,instance);
            string home=accounts.Instances.Home(instance);var channel=store.Channels().Where(c=>c.Enabled&&AgentProviders.Codex(c)&&RelayStore.SamePath(c.Home,home)&&c.AccountKey==instance.AccountKey&&DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks)).OrderByDescending(c=>c.ConnectedUtc).FirstOrDefault();
            if(channel==null)throw new InvalidOperationException("Ouvrez cette instance et connectez une fois le relais depuis son chat pour consulter les Pages reçues.");
            new DesktopRelayTransport().Verify(channel);
            return new RelaySession{Channel=channel.Id,Home=home,Account=instance.AccountKey,Thread="switcher-viewer-"+instance.Id};
        }
        internal async Task<object> OpenOwner(RelaySession session,string share,string page,CancellationToken token)
        {
            var g=Grant(session,share,page);var channel=store.Channels().Where(c=>c.Enabled&&AgentProviders.Codex(c)&&c.AccountKey==g.Account&&RelayStore.SamePath(c.Home,g.Home)&&DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks)&&!String.IsNullOrEmpty(c.AnchorThreadId)).OrderByDescending(c=>c.ConnectedUtc).FirstOrDefault();
            if(channel==null)throw new InvalidOperationException("Connectez une fois le relais dans un chat de l'instance propriétaire pour y ouvrir la Page.");
            var result=await new DesktopRelayTransport().Call(channel,"open_in_codex",new{target=new{type="page",pageId=page}},token).ConfigureAwait(false);
            Grant(session,share,page);
            await new DesktopRelayTransport().Call(channel,"navigate_to_codex_page",new{threadId=channel.AnchorThreadId},token).ConfigureAwait(false);
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,g.Instance);
            var window=DesktopWindows.Find(accounts.Vault.Root,instance);if(window!=null)DesktopWindows.Focus(window);
            return new{opened=true,instance=tunnel.CurrentName(g),navigation=result};
        }
    }
}
