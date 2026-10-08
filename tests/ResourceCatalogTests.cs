using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class ResourceCatalogTests
{
    private static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    private static void Refused(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected rejection");}
    private static object Obj(object x){return Json.Read<object>(Json.Write(x));}
    private sealed class Connection:IToolTunnelConnection
    {
        internal int Pages,Calls;internal bool Repeat,Fail,Drift;internal Action ChangeOwner;
        internal TunnelTool[] Tools={
            Tool("chatgpt_space.list_pages",true,""),Tool("chatgpt_space.read_page",true,"page_id"),Tool("chatgpt_space.edit_page",false,"page_id"),Tool("chatgpt_space.delete_page",false,"page_id"),
            new TunnelTool{Server="private_mcp",Name="query",Group="Mon plugin",Schema=Obj(new{type="object",properties=new{}}),ReadOnly=true}
        };
        private static TunnelTool Tool(string name,bool read,string field){return new TunnelTool{Server="codex_apps",Name=name,Group="Pages",ConnectorId="pages",ReadOnly=read,Description=name,Schema=Obj(new{type="object",properties=String.IsNullOrEmpty(field)?new Dictionary<string,object>():new Dictionary<string,object>{{field,new{type="string"}}}})};}
        public Task<TunnelTool[]> Inventory(CancellationToken token){return Task.FromResult(Json.Read<TunnelTool[]>(Json.Write(Tools)));}
        public Task<object> Invoke(string server,string name,object args,CancellationToken token)
        {
            Calls++;if(name!="chatgpt_space.list_pages")return Task.FromResult(Obj(new{structuredContent=new{ok=true}}));Pages++;
            if(Fail)throw new InvalidOperationException("catalog unavailable");if(Drift&&ChangeOwner!=null)ChangeOwner();
            var response=new{items=new[]{new{page_id=Pages==1?"page-a":"page-b",title=Pages==1?"Page A":"Page B",access=new{can_read=true,can_write=true},token="never retain this unrelated field"}},next_cursor=Repeat||Pages==1?"next":null};return Task.FromResult(Obj(new{structuredContent=response}));
        }
        public void Dispose(){}
    }
    private sealed class Fixture
    {
        internal RelayStore Store;internal ToolTunnel Tunnel;internal InstanceResources Catalog;internal Connection Connection=new Connection();internal TunnelOwner Owner;internal InstanceInventory Inventory;
        internal string ClientHome;internal RelaySession Session;
        internal Fixture(string root)
        {
            string path=Path.Combine(root,"resources-"+Guid.NewGuid().ToString("N"));Store=new RelayStore(path);Owner=new TunnelOwner{Id="owner",Name="Principal",Home=Path.Combine(path,"owner"),Account="owner-account"};ClientHome=Path.Combine(path,"client");
            Tunnel=new ToolTunnel(Store,id=>new TunnelOwner{Id=Owner.Id,Name=Owner.Name,Home=Owner.Home,Account=Owner.Account},o=>Connection,s=>{});Catalog=new InstanceResources(Store,Tunnel);Session=new RelaySession{Account="client-account",Home=ClientHome,Thread="chat",Channel="source"};
        }
        internal void Discover(){Inventory=Catalog.Discover(Owner.Id,CancellationToken.None).GetAwaiter().GetResult();}
        internal void Save(Dictionary<string,string> selected,string revision=null){Catalog.Save(Inventory,"client",ClientHome,"client-account",selected,revision??Catalog.Revision("owner","client"),CancellationToken.None).GetAwaiter().GetResult();}
        internal PluginAccess Plugin {get{return new PluginAccess(Store,Tunnel);}}
        internal PluginAccessState LoadPlugin(){return Plugin.Load(Owner.Id,Owner.Account,Owner.Home,"client",ClientHome,"client-account",InstanceResources.PluginKey(Connection.Tools.Last()),CancellationToken.None).GetAwaiter().GetResult();}
        internal void SavePlugin(PluginAccessState state,params string[] selected){Plugin.Save(state,selected,CancellationToken.None).GetAwaiter().GetResult();}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("resource discovery paginates and never creates a grant",delegate{var f=new Fixture(root);f.Discover();Assert(f.Connection.Pages==2&&f.Inventory.Resources.Count==2,"pagination incomplete");Assert(f.Tunnel.Grants().Count==0,"discovery granted access");Assert(!Json.Write(f.Inventory).Contains("never retain"),"unrelated provider fields retained");});
        check("resource discovery reports unsupported private plugins honestly",delegate{var f=new Fixture(root);f.Discover();Assert(f.Inventory.Plugins.Single(p=>p.Name=="Mon plugin").State=="unsupported","fake catalog claimed");});
        check("resource discovery bounds repeated cursors and deduplicates IDs",delegate{var f=new Fixture(root);f.Connection.Repeat=true;f.Discover();Assert(f.Connection.Pages==2&&f.Inventory.Resources.Count==2&&f.Inventory.Plugins.Single(p=>p.Name=="Pages").State=="partial","repeated cursor loop");});
        check("resource catalog belongs to the exact owner identity",delegate{var f=new Fixture(root);f.Discover();Assert(f.Catalog.Cached("owner","another",f.Owner.Home).Resources.Count==0,"account bleed");Assert(f.Catalog.Cached("owner",f.Owner.Account,f.Owner.Home).Resources.Count==2,"cache lost");});
        check("resource discovery rejects owner changes before cache publication",delegate{var f=new Fixture(root);f.Connection.Drift=true;f.Connection.ChangeOwner=()=>f.Owner.Account="other";Refused(()=>f.Discover());Assert(!File.Exists(Path.Combine(f.Store.Root,InstanceResources.CacheName("owner"))),"wrong account cached");});
        check("resource provider failure is reported without deleting shares",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});f.Connection.Fail=true;f.Discover();Assert(f.Inventory.Plugins.Single(p=>p.Name=="Pages").State=="error"&&f.Tunnel.Grants().Count==1,"failure erased grant");});
        check("resource selections form one exact allowlist without future resources",delegate{var f=new Fixture(root);f.Discover();f.Save(f.Inventory.Resources.ToDictionary(r=>r.Key,r=>"read"));var g=f.Tunnel.Grants().Single();Assert(g.ResourceValues.Count==2&&g.Tools.All(t=>t.ReadOnly),"broad grant");ToolTunnel.CheckResource(g,Obj(new{page_id="page-a"}));Refused(()=>ToolTunnel.CheckResource(g,Obj(new{page_id="future-page"})));Refused(()=>ToolTunnel.CheckResource(g,Obj(new{page_id=new[]{"page-a"}})));});
        check("basic edit permission does not grant deletion or access management",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"edit"}});var g=f.Tunnel.Grants().Single();Assert(g.Tools.Any(t=>t.Name.EndsWith("edit_page"))&&!g.Tools.Any(t=>t.Name.EndsWith("delete_page")),"edit escalated to destructive permission");});
        check("resource selection persists and restores per client",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"edit"}});Assert(f.Catalog.Selections(f.Inventory,"client").Single().Value=="edit"&&f.Catalog.Selections(f.Inventory,"other-client").Count==0,"client scope bleed");});
        check("shared resources expose names to agents without expanding the allowlist",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});var g=f.Tunnel.Grants().Single();Assert(g.ResourceLabels["page-a"]=="Page A"&&Json.Write(f.Tunnel.List(f.Session)).Contains("Page A"),"agent cannot resolve the resource name");g.ResourceLabels["future-page"]="Injected";Refused(()=>f.Tunnel.SaveGrant(g));Assert(!f.Tunnel.Grants().Single().ResourceLabels.ContainsKey("future-page"),"invalid label persisted");});
        check("saving one recipient never changes another recipient",delegate{var f=new Fixture(root);f.Discover();var selected=new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}};f.Catalog.Save(f.Inventory,"another",Path.Combine(f.ClientHome,"other"),"other-account",selected,f.Catalog.Revision("owner","another"),CancellationToken.None).GetAwaiter().GetResult();var before=f.Tunnel.Grants().Single();f.Save(selected);f.Save(new Dictionary<string,string>());Assert(f.Tunnel.Grants().Single().Revision==before.Revision,"other recipient altered");});
        check("unchecking a resource revokes that resource only",delegate{var f=new Fixture(root);f.Discover();f.Save(f.Inventory.Resources.ToDictionary(r=>r.Key,r=>"read"));f.Save(new Dictionary<string,string>{{f.Inventory.Resources[1].Key,"read"}});var g=f.Tunnel.Grants().Single();Refused(()=>ToolTunnel.CheckResource(g,Obj(new{page_id="page-a"})));ToolTunnel.CheckResource(g,Obj(new{page_id="page-b"}));});
        check("catalog batch refuses concurrent edits atomically",delegate{var f=new Fixture(root);f.Discover();string revision=f.Catalog.Revision("owner","client");f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}},revision);Refused(()=>f.Save(new Dictionary<string,string>{{f.Inventory.Resources[1].Key,"edit"}},revision));Assert(f.Tunnel.Grants().Single().ResourceValues.Single()=="page-a","concurrent edit overwritten");});
        check("catalog preserves advanced grants and other clients",delegate{var f=new Fixture(root);f.Discover();var old=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Instance="owner",Name="advanced",Enabled=true,Sources=new Dictionary<string,string>{{f.ClientHome,"client-account"}},Tools=new List<TunnelTool>{f.Connection.Tools[1]},ResourceField="page_id",ResourceValue="page-c"};f.Tunnel.SaveGrant(old);f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});f.Save(new Dictionary<string,string>());Assert(f.Tunnel.Grants().Single().Id==old.Id,"advanced grants deleted");});
        check("catalog forbids native editors unknown IDs and unavailable writes",delegate{var f=new Fixture(root);f.Discover();Refused(()=>f.Save(new Dictionary<string,string>{{"invented","read"}}));f.Inventory.Resources[0].CanModify=false;Refused(()=>f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"edit"}}));f.Inventory.Resources[0].NativeEditor=true;Refused(()=>f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}}));Assert(f.Tunnel.Grants().Count==0,"invalid plan partially saved");});
        check("stale catalog cannot enable new permissions but can revoke all",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});f.Inventory.Updated=DateTime.UtcNow.AddHours(-1).ToString("o");Refused(()=>f.Save(new Dictionary<string,string>{{f.Inventory.Resources[1].Key,"read"}}));f.Save(new Dictionary<string,string>());Assert(f.Tunnel.Grants().Count==0,"revocation unavailable");});
        check("plugin editor loads only its exact plugin without implicit permission",delegate{var f=new Fixture(root);var p=f.LoadPlugin();Assert(p.Tools.Length==1&&p.Tools[0].Name=="query"&&p.Selected.Count==0&&f.Tunnel.Grants().Count==0,"plugin context lost");});
        check("plugin selection creates an exact account and action grant",delegate{var f=new Fixture(root);var p=f.LoadPlugin();f.SavePlugin(p,"private_mcp/query");var g=f.Tunnel.Grants().Single();Assert(g.CatalogPlugin==p.Plugin&&g.CatalogClient=="client"&&g.Sources[f.ClientHome]=="client-account"&&g.Tools.Single().Name=="query","incorrect scoped plugin access");Assert(String.IsNullOrEmpty(g.ResourceField),"invented site restriction");});
        check("plugin editor refuses actions from another plugin or invented actions",delegate{var f=new Fixture(root);var p=f.LoadPlugin();Refused(()=>f.SavePlugin(p,"codex_apps/chatgpt_space.read_page"));Refused(()=>f.SavePlugin(p,"private_mcp/invented"));Assert(f.Tunnel.Grants().Count==0,"cross-plugin escalation");});
        check("plugin save refuses a changed contract",delegate{var f=new Fixture(root);var p=f.LoadPlugin();f.Connection.Tools.Last().Description="changed contract";Refused(()=>f.SavePlugin(p,"private_mcp/query"));Assert(f.Tunnel.Grants().Count==0,"stale schema accepted");});
        check("plugin save rejects account drift",delegate{var f=new Fixture(root);var p=f.LoadPlugin();f.Owner.Account="another-account";Refused(()=>f.SavePlugin(p,"private_mcp/query"));});
        check("plugin and resource selections never erase each other",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});var p=f.LoadPlugin();f.SavePlugin(p,"private_mcp/query");f.Save(new Dictionary<string,string>());Assert(f.Tunnel.Grants().Single().CatalogPlugin==p.Plugin,"resource save erased plugin");f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});f.SavePlugin(f.LoadPlugin());Assert(f.Tunnel.Grants().Single().ResourceValues.Single()=="page-a","plugin revoke erased resource");});
        check("plugin save protects concurrent changes",delegate{var f=new Fixture(root);var p=f.LoadPlugin();f.SavePlugin(p,"private_mcp/query");Refused(()=>f.SavePlugin(p));Assert(f.Tunnel.Grants().Count==1,"concurrent share lost");});
        check("plugin editor reports existing scoped rules without broadening them",delegate{var f=new Fixture(root);var t=f.Connection.Tools.Last();t.Schema=Obj(new{type="object",properties=new{record_id=new{type="string"}}});f.Tunnel.SaveGrant(new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Instance="owner",Name="Existing limited access",Enabled=true,Sources=new Dictionary<string,string>{{f.ClientHome,"client-account"}},Tools=new List<TunnelTool>{t},ResourceField="record_id",ResourceValue="r1"});var p=f.LoadPlugin();Assert(p.ExistingRules.Length==1&&p.Selected.Count==0,"existing scoped access silently expanded");f.SavePlugin(p);Assert(f.Tunnel.Grants().Single().ResourceValue=="r1","advanced rule erased");});
        check("plugin labels prefer provider titles and never invent resource semantics",delegate{var t=new TunnelTool{Name="workspace_site_probe",Title="Vérifier une connexion"};Assert(PluginAccess.Label(t)==t.Title,"provider title lost");t.Title=null;t.Annotations=Obj(new{title="Lire le dossier"});Assert(PluginAccess.Label(t)=="Lire le dossier","annotation ignored");t.Annotations=null;Assert(PluginAccess.Label(t)==t.Name,"unknown action renamed speculatively");});
        check("plugin revocation remains possible after the owner changes",delegate{var f=new Fixture(root);f.SavePlugin(f.LoadPlugin(),"private_mcp/query");var p=f.LoadPlugin();f.Owner.Account="other-account";f.SavePlugin(p);Assert(f.Tunnel.Grants().Count==0,"revoke required connected owner");});
    }
}
