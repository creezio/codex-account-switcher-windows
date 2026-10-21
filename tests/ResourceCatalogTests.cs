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
        internal int Pages,Calls;internal bool Repeat,Fail,Drift,InventoryFail;internal Action ChangeOwner;
        internal Func<string,object,object> Response;
        internal TunnelTool[] Tools={
            Tool("chatgpt_space.list_pages",true,""),Tool("chatgpt_space.read_page",true,"page_id"),Tool("chatgpt_space.edit_page",false,"page_id"),Tool("chatgpt_space.delete_page",false,"page_id"),
            new TunnelTool{Server="private_mcp",Name="query",Group="Mon plugin",Schema=Obj(new{type="object",properties=new{}}),ReadOnly=true}
        };
        private static TunnelTool Tool(string name,bool read,string field){return new TunnelTool{Server="codex_apps",Name=name,Group="Pages",ConnectorId="pages",ReadOnly=read,Description=name,Schema=Obj(new{type="object",properties=String.IsNullOrEmpty(field)?new Dictionary<string,object>():new Dictionary<string,object>{{field,new{type="string"}}}})};}
        public Task<TunnelTool[]> Inventory(CancellationToken token){if(InventoryFail)throw new InvalidOperationException("tool inventory unavailable");return Task.FromResult(Json.Read<TunnelTool[]>(Json.Write(Tools)));}
        public Task<object> Invoke(string server,string name,object args,CancellationToken token)
        {
            Calls++;if(Response!=null)return Task.FromResult(Response(name,args));if(name!="chatgpt_space.list_pages")return Task.FromResult(Obj(new{structuredContent=new{ok=true}}));Pages++;
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
    private static void SiteCatalog(Fixture fixture)
    {
        var tools=new List<TunnelTool>();foreach(string name in new[]{"sites.get_site","sites.list_site_versions","sites.update_site_metadata","sites.create_source_repository_write_credential","sites.save_site_version","sites.save_version_and_deploy_private","sites.deploy_site_version","sites.deploy_private_site_version","sites.delete_site","sites.update_site_access","sites.list_sites"}){
            bool read=name=="sites.get_site"||name=="sites.list_site_versions"||name=="sites.list_sites";
            var properties=new Dictionary<string,object>();if(name!="sites.list_sites")properties["project_id"]=new{type="string"};
            object schema=Obj(new{type="object",properties});
            if(name=="sites.save_site_version"||name=="sites.save_version_and_deploy_private")schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"commit_sha\":{\"type\":\"string\"},\"archive\":{\"type\":\"string\",\"description\":\"Omit it only when local packaging cannot complete and remote build fallback is required.\"}},\"required\":[\"project_id\",\"commit_sha\"]}");
            tools.Add(new TunnelTool{Server="codex_apps",Name=name,Group="Sites",ConnectorId="sites",ReadOnly=read,Description=name,Schema=schema});
        }
        fixture.Connection.Tools=tools.ToArray();string plugin=InstanceResources.PluginKey(tools[0]);fixture.Inventory=new InstanceInventory{Instance=fixture.Owner.Id,Account=fixture.Owner.Account,Home=fixture.Owner.Home,Updated=DateTime.UtcNow.ToString("o")};
        fixture.Inventory.Resources.Add(new InstanceResource{Key=InstanceResources.ResourceKey(plugin,"project_id","site-a"),Kind="Site",Title="Site A",Field="project_id",Value="site-a",Plugin=plugin,CanModify=true});
        fixture.Inventory.Resources.Add(new InstanceResource{Key=InstanceResources.ResourceKey(plugin,"project_id","site-b"),Kind="Site",Title="Site B",Field="project_id",Value="site-b",Plugin=plugin,CanModify=true});
    }
    private static object SitePage(string[] ids,string cursor=null,bool partial=false)
    {return Obj(new{structuredContent=new{items=ids.Select(id=>new{id,title="Title "+id}).ToArray(),cursor,partial_results=partial}});}
    private static void DiscoverSites(Fixture fixture)
    {
        SiteCatalog(fixture);fixture.Connection.Response=(name,args)=>SitePage(Json.Str(Json.Get(args,"role"))=="owner"?new[]{"site-a","site-b"}:new string[0]);fixture.Discover();
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("resource discovery paginates and never creates a grant",delegate{var f=new Fixture(root);f.Discover();Assert(f.Connection.Pages==2&&f.Inventory.Resources.Count==2,"pagination incomplete");Assert(f.Tunnel.Grants().Count==0,"discovery granted access");Assert(!Json.Write(f.Inventory).Contains("never retain"),"unrelated provider fields retained");});
        check("resource discovery reports unsupported private plugins honestly",delegate{var f=new Fixture(root);f.Discover();Assert(f.Inventory.Plugins.Single(p=>p.Name=="Mon plugin").State=="unsupported","fake catalog claimed");});
        check("resource discovery bounds repeated cursors and deduplicates IDs",delegate{var f=new Fixture(root);f.Connection.Repeat=true;f.Discover();Assert(f.Connection.Pages==2&&f.Inventory.Resources.Count==2&&f.Inventory.Plugins.Single(p=>p.Name=="Pages").State=="partial","repeated cursor loop");});
        check("resource catalog belongs to the exact owner identity",delegate{var f=new Fixture(root);f.Discover();Assert(f.Catalog.Cached("owner","another",f.Owner.Home).Resources.Count==0,"account bleed");Assert(f.Catalog.Cached("owner",f.Owner.Account,f.Owner.Home).Resources.Count==2,"cache lost");});
        check("resource discovery rejects owner changes before cache publication",delegate{var f=new Fixture(root);f.Connection.Drift=true;f.Connection.ChangeOwner=()=>f.Owner.Account="other";Refused(()=>f.Discover());Assert(!File.Exists(Path.Combine(f.Store.Root,InstanceResources.CacheName("owner"))),"wrong account cached");});
        check("resource provider failure is reported without deleting shares",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});f.Connection.Fail=true;f.Discover();Assert(f.Inventory.Plugins.Single(p=>p.Name=="Pages").State=="error"&&f.Tunnel.Grants().Count==1,"failure erased grant");});
        check("Sites discovery paginates owner and editor roles without duplicate resources",delegate{
            var f=new Fixture(root);SiteCatalog(f);f.Connection.Response=(name,args)=>{
                string role=Json.Str(Json.Get(args,"role")),cursor=Json.Str(Json.Get(args,"cursor"));
                Assert(Convert.ToInt32(Json.Get(args,"limit"))==50,"Site page size changed");
                return role=="owner"?(cursor.Length==0?SitePage(new[]{"site-a"},"next"):SitePage(new[]{"site-b"})):SitePage(new[]{"site-a","site-c"});
            };f.Discover();Assert(f.Connection.Calls==3&&f.Inventory.Resources.Count==3&&f.Inventory.Resources.All(r=>!r.CacheRetained),"Site pagination or deduplication incomplete");Assert(f.Inventory.Resources.Single(r=>r.Value=="site-a").Access=="Propriétaire"&&f.Inventory.Resources.Single(r=>r.Value=="site-c").Access=="Éditeur","Site role lost");
        });
        check("later Site page failure retains old missing rows and leaves access bytes unchanged",delegate{
            var f=new Fixture(root);DiscoverSites(f);var old=f.Inventory;f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});string grants=Path.Combine(f.Store.Root,"tool-grants.dpapi");var before=File.ReadAllBytes(grants);
            f.Connection.Response=(name,args)=>{if(Json.Str(Json.Get(args,"cursor")).Length>0)throw new InvalidOperationException("later page failed");return SitePage(new[]{"site-a","site-new"},"next");};f.Discover();
            Assert(f.Inventory.Resources.Count==3&&f.Inventory.Resources.Select(r=>r.Key).Distinct().Count()==3,"failed page removed old rows or introduced duplicates");Assert(f.Inventory.Resources.Single(r=>r.Value=="site-b").CacheRetained&&f.Inventory.Resources.Where(r=>r.Value!="site-b").All(r=>!r.CacheRetained),"fresh and retained rows confused");Assert(old.Resources.All(r=>!r.CacheRetained),"previous in-memory snapshot changed");Assert(f.Inventory.Plugins.Single().State=="error"&&f.Inventory.Plugins.Single().Message.Contains("Dernier inventaire conservé"),"preserved inventory not identified");Assert(before.SequenceEqual(File.ReadAllBytes(grants)),"discovery rewrote access registry");
            Refused(()=>f.Save(new Dictionary<string,string>{{f.Inventory.Resources.Single(r=>r.Value=="site-b").Key,"edit"}}));Assert(before.SequenceEqual(File.ReadAllBytes(grants)),"retained resource enabled access");
            f.Discover();Assert(f.Inventory.Resources.Count==3&&f.Catalog.Cached(f.Owner.Id,f.Owner.Account,f.Owner.Home).Resources.Single(r=>r.Value=="site-b").CacheRetained,"repeated failure lost retained rows");
        });
        check("a complete empty Site response replaces old catalog without changing existing shares",delegate{
            var f=new Fixture(root);DiscoverSites(f);f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"read"}});string grants=Path.Combine(f.Store.Root,"tool-grants.dpapi");var before=File.ReadAllBytes(grants);f.Connection.Response=(name,args)=>SitePage(new string[0]);f.Discover();Assert(f.Inventory.Resources.Count==0&&f.Inventory.Plugins.Single().State=="ready","successful empty inventory was not applied");Assert(before.SequenceEqual(File.ReadAllBytes(grants)),"complete refresh changed existing permissions");
        });
        check("partial and repeated Site cursors retain previous rows until a complete confirmation",delegate{
            var f=new Fixture(root);DiscoverSites(f);f.Connection.Response=(name,args)=>SitePage(Json.Str(Json.Get(args,"role"))=="owner"?new[]{"site-a"}:new string[0],null,true);f.Discover();Assert(f.Inventory.Resources.Count==2&&f.Inventory.Plugins.Single().State=="partial"&&f.Inventory.Resources.Single(r=>r.Value=="site-b").CacheRetained,"provider partial result erased old resource");
            f.Connection.Response=(name,args)=>Json.Str(Json.Get(args,"role"))=="owner"?SitePage(new[]{"site-a"},"repeat"):SitePage(new string[0]);f.Discover();Assert(f.Inventory.Resources.Count==2&&f.Inventory.Plugins.Single().State=="partial","cursor repetition erased previous inventory");
            f.Connection.Response=(name,args)=>SitePage(Json.Str(Json.Get(args,"role"))=="owner"?new[]{"site-b"}:new string[0]);f.Discover();Assert(f.Inventory.Resources.Count==1&&f.Inventory.Resources.Single().Value=="site-b"&&!f.Inventory.Resources.Single().CacheRetained&&f.Inventory.Plugins.Single().State=="ready","complete refresh did not reset retained state");
        });
        check("malformed Site item collections never turn a valid catalog into an empty one",delegate{
            foreach(object items in new object[]{"invalid",new{unexpected="shape"}}){var f=new Fixture(root);DiscoverSites(f);f.Connection.Response=(name,args)=>Obj(new{structuredContent=new{items}});f.Discover();Assert(f.Inventory.Resources.Count==2&&f.Inventory.Resources.All(r=>r.CacheRetained)&&f.Inventory.Plugins.Single().State=="error","invalid provider shape evicted old catalog");}
        });
        check("old resource fallback never crosses owner account or home boundaries",delegate{
            foreach(bool changeAccount in new[]{true,false}){var f=new Fixture(root);DiscoverSites(f);if(changeAccount)f.Owner.Account="other-account";else f.Owner.Home=Path.Combine(f.Owner.Home,"other");f.Connection.Response=(name,args)=>{throw new InvalidOperationException("catalog unavailable");};f.Discover();Assert(f.Inventory.Resources.Count==0,"old catalog crossed an owner boundary");}
        });
        check("global discovery failure and owner drift leave the last encrypted catalog intact",delegate{
            var f=new Fixture(root);DiscoverSites(f);string path=Path.Combine(f.Store.Root,InstanceResources.CacheName(f.Owner.Id));var before=File.ReadAllBytes(path);f.Connection.InventoryFail=true;Refused(()=>f.Discover());Assert(before.SequenceEqual(File.ReadAllBytes(path)),"failed tool inventory rewrote cached resources");f.Connection.InventoryFail=false;
            f.Connection.Response=(name,args)=>{f.Owner.Account="other-account";return SitePage(new string[0]);};Refused(()=>f.Discover());Assert(before.SequenceEqual(File.ReadAllBytes(path)),"owner drift published invalid catalog");
        });
        check("cancellation keeps the exact last encrypted catalog",delegate{
            var f=new Fixture(root);DiscoverSites(f);string path=Path.Combine(f.Store.Root,InstanceResources.CacheName(f.Owner.Id));var before=File.ReadAllBytes(path);f.Connection.Response=(name,args)=>{throw new OperationCanceledException();};bool cancelled=false;try{f.Discover();}catch(OperationCanceledException){cancelled=true;}Assert(cancelled&&before.SequenceEqual(File.ReadAllBytes(path)),"cancellation replaced the cached catalog");
        });
        check("resource selections form one exact allowlist without future resources",delegate{var f=new Fixture(root);f.Discover();f.Save(f.Inventory.Resources.ToDictionary(r=>r.Key,r=>"read"));var g=f.Tunnel.Grants().Single();Assert(g.ResourceValues.Count==2&&g.Tools.All(t=>t.ReadOnly),"broad grant");ToolTunnel.CheckResource(g,Obj(new{page_id="page-a"}));Refused(()=>ToolTunnel.CheckResource(g,Obj(new{page_id="future-page"})));Refused(()=>ToolTunnel.CheckResource(g,Obj(new{page_id=new[]{"page-a"}})));});
        check("basic edit permission does not grant deletion or access management",delegate{var f=new Fixture(root);f.Discover();f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"edit"}});var g=f.Tunnel.Grants().Single();Assert(g.Tools.Any(t=>t.Name.EndsWith("edit_page"))&&!g.Tools.Any(t=>t.Name.EndsWith("delete_page")),"edit escalated to destructive permission");});
        check("Site publication is an explicit separate grant and preserves metadata-only edit",delegate{
            var f=new Fixture(root);SiteCatalog(f);foreach(string mode in new[]{"read","edit"}){f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,mode}});var previous=f.Tunnel.Grants().Single();Assert(!previous.Tools.Any(InstanceResources.SitePublishingTool)&&InstanceResources.Mode(previous)==mode,"ordinary Site permission gained publishing");}
            f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"publish"}});var grant=f.Tunnel.Grants().Single();Assert(grant.Tools.Count(InstanceResources.SitePublishingTool)==5&&grant.Tools.Any(t=>t.Name=="sites.update_site_metadata")&&InstanceResources.Mode(grant)=="publish","explicit publishing tools missing");Assert(grant.ResourceField=="project_id"&&grant.ResourceValues.SequenceEqual(new[]{"site-a"})&&grant.Name.Contains("publication"),"publishing lost the exact selected Site");
            Assert(!grant.Tools.Any(t=>t.Name=="sites.list_sites"||t.Name=="sites.delete_site"||t.Name=="sites.update_site_access"),"publication widened resource or access scope");Refused(()=>ToolTunnel.CheckResource(grant,Obj(new{project_id="site-b"})));Assert(f.Catalog.Selections(f.Inventory,"client")[f.Inventory.Resources[0].Key]=="publish","publishing choice restored as ordinary edit");
        });
        check("Site publication does not rewrite existing shares until explicit save",delegate{
            var f=new Fixture(root);SiteCatalog(f);f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"edit"}});var old=f.Tunnel.Grants().Single();var selected=f.Catalog.Selections(f.Inventory,"client");Assert(selected.Single().Value=="edit"&&f.Tunnel.Grants().Single().Revision==old.Revision&&!f.Tunnel.Grants().Single().Tools.Any(InstanceResources.SitePublishingTool),"read upgraded the share");
            f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"publish"},{f.Inventory.Resources[1].Key,"read"}});var grants=f.Tunnel.Grants();Assert(grants.Count==2&&grants.Single(g=>g.ResourceValues.Contains("site-b")).Tools.All(t=>t.ReadOnly),"publication expanded to another selected read-only Site");
        });
        check("Site publication refuses other resource kinds and unavailable native save contracts",delegate{
            var f=new Fixture(root);f.Discover();Refused(()=>f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"publish"}}));Assert(f.Tunnel.Grants().Count==0,"Page gained Site publication");
            f=new Fixture(root);SiteCatalog(f);foreach(var t in f.Connection.Tools.Where(t=>t.Name=="sites.save_site_version"||t.Name=="sites.save_version_and_deploy_private"))Json.Obj(t.Schema)["required"]=new[]{"project_id","commit_sha","archive"};f.Save(new Dictionary<string,string>{{f.Inventory.Resources[0].Key,"publish"}});Assert(!f.Tunnel.Grants().Single().Tools.Any(t=>t.Name=="sites.save_site_version"||t.Name=="sites.save_version_and_deploy_private"),"unsupported archive upload was granted");
        });
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
