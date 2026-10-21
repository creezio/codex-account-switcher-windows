using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class GlobalToolAccessTests
{
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Refused(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected refusal.");}
    private static TunnelTool Tool(string name,bool readOnly)
    {return new TunnelTool{Server="future-native-provider",Name=name,Group="Future connector",ConnectorId="unknown-provider",ReadOnly=readOnly,Annotations=new{readOnlyHint=readOnly,destructiveHint=!readOnly},Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"resource\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}},\"required\":[\"resource\"],\"additionalProperties\":false}")};}
    private sealed class Connection : IToolTunnelConnection
    {
        internal readonly List<TunnelTool> Tools=new List<TunnelTool>{Tool("unseen_read",true),Tool("unseen_publish_or_delete",false)};
        internal int Inventories,Calls;
        internal bool Offline;
        internal Action OnInventory,OnInvoke;
        internal Func<string,object,object> Result;
        public Task<TunnelTool[]> Inventory(CancellationToken token){Inventories++;if(Offline)throw new IOException("mock account unavailable");if(OnInventory!=null)OnInventory();return Task.FromResult(Tools.ToArray());}
        public Task<object> Invoke(string server,string tool,object arguments,CancellationToken token){Calls++;if(OnInvoke!=null)OnInvoke();return Task.FromResult(Result==null?(object)new{structuredContent=new{resource=Json.Str(Json.Get(arguments,"resource")),saved=true}}:Result(tool,arguments));}
        public void Dispose(){}
    }
    private static void DocumentTools(Fixture f)
    {
        var connection=f.Connections[f.Owners[1].Id];string text="Original",hash="h1";int sequence=1;
        connection.Tools.Add(new TunnelTool{Server=SharedPages.Server,Name=SharedPages.ReadTool,ReadOnly=true,Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"page_id\":{\"type\":\"string\"},\"view\":{\"type\":\"string\"}}}")});
        connection.Tools.Add(new TunnelTool{Server=SharedPages.Server,Name=SharedPages.EditTool,ReadOnly=false,Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"page_id\":{\"type\":\"string\"},\"operations\":{\"type\":\"array\"}}}")});
        connection.Tools.Add(new TunnelTool{Server=SharedPages.Server,Name=SharedResourceLinks.SiteRead,ReadOnly=true,Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"}}}")});
        connection.Result=(name,args)=>{
            if(name==SharedPages.ReadTool)return Json.Read<object>(Json.Write(new{structuredContent=new{content=new{page_id=Json.Str(Json.Get(args,"page_id")),title="Future Page",through_sequence=sequence,blocks=new[]{new{id="block",kind="markdown",markdown=text,hash=hash}}},metadata=new{stream_kind="content"}}}));
            if(name==SharedResourceLinks.SiteRead)return Json.Read<object>(Json.Write(new{structuredContent=new{id=Json.Str(Json.Get(args,"project_id")),attached_page_id="native-attached-page"}}));
            var op=RelayEngine.Rows(Json.Get(args,"operations")).Single();Assert(Json.Str(Json.Get(op,"expected_hash"))==hash,"concurrent Page edit guard missing");text=Json.Str(Json.Get(op,"markdown"));sequence++;hash="h"+sequence;return Json.Read<object>(Json.Write(new{structuredContent=new{operation_results=new[]{new{status="applied",operation_index=0}}}}));
        };
    }
    private sealed class Fixture
    {
        internal readonly RelayStore Store;
        internal readonly ToolTunnel Tunnel;
        internal readonly List<TunnelOwner> Owners=new List<TunnelOwner>();
        internal readonly Dictionary<string,Connection> Connections=new Dictionary<string,Connection>();
        internal bool SourceValid=true;
        internal int Resolves;
        internal Func<TunnelOwner,TunnelOwner> ResolveOverride;
        internal Fixture(string root)
        {
            string folder=Path.Combine(root,"global-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);Store=new RelayStore(Path.Combine(folder,"relay"));
            AddOwner("first");AddOwner("second");AddOwner("third");
            Tunnel=new ToolTunnel(Store,id=>{Resolves++;var owner=Owners.Single(o=>o.Id==id);return ResolveOverride==null?owner:ResolveOverride(owner);},o=>Connections[o.Id],s=>{if(!SourceValid||!Owners.Any(o=>RelayStore.SamePath(o.Home,s.Home)&&o.Account==s.Account))throw new InvalidOperationException("mock source identity changed");},()=>Owners.ToArray());
        }
        internal TunnelOwner AddOwner(string name)
        {
            var owner=new TunnelOwner{Id=Guid.NewGuid().ToString("N"),Name=name,Home=Path.Combine(Store.Root,name),Account="mock-account-"+name};Owners.Add(owner);Connections.Add(owner.Id,new Connection());return owner;
        }
        internal RelaySession Session(int index=0){var o=Owners[index];return new RelaySession{Channel="mock",Thread="chat-"+index,Home=o.Home,Account=o.Account};}
        internal ToolAccessPolicyState Enable(){return ToolAccessPolicy.Save(Store,true,ToolAccessPolicy.Read(Store).Revision);}
        internal object[] Shares(RelaySession session=null){return RelayEngine.Rows(Json.Read<object>(Json.Write(Tunnel.List(session??Session())))).ToArray();}
        internal string Share(int ownerIndex=1,RelaySession source=null){return Json.Str(Json.Get(Shares(source).Single(r=>Json.Str(Json.Get(r,"instance"))==Owners[ownerIndex].Id&&ToolTunnel.GlobalGrantId(Json.Str(Json.Get(r,"id")))),"id"));}
        internal TunnelCall Call(string share,string name="unseen_publish_or_delete",RelaySession source=null,string id=null,object args=null,CancellationToken? token=null)
        {return Tunnel.Invoke(source??Session(),id??Guid.NewGuid().ToString("N"),share,"future-native-provider",name,args??new Dictionary<string,object>{{"resource","never-in-a-catalog-"+Guid.NewGuid().ToString("N")}},token??CancellationToken.None).GetAwaiter().GetResult();}
        internal TunnelGrant Explicit()
        {
            var owner=Owners[1];var session=Session();var grant=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Name="Customized exact access",Instance=owner.Id,Enabled=true,ResourceField="resource",ResourceValue="exact-old-resource",Sources=new Dictionary<string,string>{{session.Home,session.Account}},Tools=new List<TunnelTool>{Connections[owner.Id].Tools[0]}};Tunnel.SaveGrant(grant);return grant;
        }
        internal int Inventories{get{return Connections.Values.Sum(c=>c.Inventories);}}
        internal int Calls{get{return Connections.Values.Sum(c=>c.Calls);}}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("global resource access is disabled by default and reads never create policy or start owners",delegate{
            var f=new Fixture(root);Assert(!ToolAccessPolicy.Read(f.Store).Enabled&&ToolAccessPolicy.Read(f.Store).Revision=="initial","implicit access");Assert(f.Shares().Length==0&&f.Resolves==0&&f.Inventories==0,"default list contacted owners");Assert(!File.Exists(Path.Combine(f.Store.Root,ToolAccessPolicy.FileName)),"read wrote policy");
        });
        check("global resource policy requires current revision and repeated same-state save is stable",delegate{
            var f=new Fixture(root);Refused(()=>ToolAccessPolicy.Save(f.Store,true,null));var enabled=f.Enable();byte[] before=File.ReadAllBytes(Path.Combine(f.Store.Root,ToolAccessPolicy.FileName));Refused(()=>ToolAccessPolicy.Save(f.Store,false,"initial"));Assert(ToolAccessPolicy.Save(f.Store,true,enabled.Revision).Revision==enabled.Revision&&before.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Store.Root,ToolAccessPolicy.FileName))),"duplicate save changed policy");
        });
        check("global metadata listing includes every other instance without inventory or owner resolution",delegate{
            var f=new Fixture(root);f.Enable();foreach(var connection in f.Connections.Values)connection.Offline=true;var shares=f.Shares();Assert(shares.Length==2&&shares.All(r=>Object.Equals(Json.Get(r,"allResources"),true)&&Json.Str(Json.Get(r,"access"))=="read-write"),"incomplete global list");Assert(f.Resolves==0&&f.Inventories==0&&f.Calls==0,"list started an owner");Assert(shares.All(r=>!RelayEngine.Rows(Json.Get(r,"tools")).Any()&&Json.Str(Json.Get(Json.Get(r,"discovery"),"tool"))=="*"),"lazy discovery missing");
        });
        check("global mode permits reads and destructive writes for all directed instance pairs and unknown resources",delegate{
            var f=new Fixture(root);f.Enable();for(int source=0;source<3;source++)for(int owner=0;owner<3;owner++)if(source!=owner){var session=f.Session(source);string share=f.Share(owner,session);foreach(string tool in new[]{"unseen_read","unseen_publish_or_delete"})Assert(f.Call(share,tool,session).State=="completed","pair rejected");}Assert(f.Calls==12,"missing or duplicated calls");Assert(f.Tunnel.Grants().Count==0,"synthetic policy wrote grants");
        });
        check("global mode discovers tools only for the selected owner and includes native schemas",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share();var all=Json.Read<object>(Json.Write(f.Tunnel.DescribeAsync(f.Session(),share,"*","*",CancellationToken.None).GetAwaiter().GetResult()));Assert(RelayEngine.Rows(Json.Get(all,"tools")).Count()==2&&f.Inventories==1&&f.Connections[f.Owners[1].Id].Inventories==1&&f.Calls==0,"inventory fanout or invocation");var single=Json.Read<object>(Json.Write(f.Tunnel.Describe(f.Session(),share,"future-native-provider","unseen_publish_or_delete")));Assert(Json.Get(Json.Get(single,"tool"),"Schema")!=null&&Object.Equals(Json.Get(single,"allResources"),true),"native schema missing");
        });
        check("global policy includes future instances and tools without reconfiguration or a cached resource catalog",delegate{
            var f=new Fixture(root);var policy=f.Enable();var future=f.AddOwner("future");var shares=f.Shares();Assert(shares.Length==3&&f.Inventories==0,"future owner absent");string share=f.Share(3);f.Connections[future.Id].Tools.Add(Tool("tool_installed_after_policy",false));Assert(f.Call(share,"tool_installed_after_policy").State=="completed"&&ToolAccessPolicy.Read(f.Store).Revision==policy.Revision,"future tool needs new grant");
        });
        check("disabling global access preserves exact customized grant bytes and original restrictions",delegate{
            var f=new Fixture(root);var grant=f.Explicit();string path=Path.Combine(f.Store.Root,"tool-grants.dpapi");byte[] before=File.ReadAllBytes(path);f.Enable();string share=f.Share();Assert(f.Call(share).State=="completed","global denied");var policy=ToolAccessPolicy.Read(f.Store);ToolAccessPolicy.Save(f.Store,false,policy.Revision);Assert(before.SequenceEqual(File.ReadAllBytes(path)),"custom grants rewritten");Refused(()=>f.Call(share));Assert(f.Shares().Length==1,"custom grants disappeared");Refused(()=>f.Call(grant.Id,"unseen_read",args:new Dictionary<string,object>{{"resource","outside-old-scope"}}));Assert(f.Call(grant.Id,"unseen_read",args:new Dictionary<string,object>{{"resource","exact-old-resource"}}).State=="completed","custom grant no longer works");
        });
        check("global off then on invalidates stale share ids and cannot resend an old idempotency key",delegate{
            var f=new Fixture(root);f.Enable();string old=f.Share(),id=Guid.NewGuid().ToString("N");var args=new Dictionary<string,object>{{"resource","resource"}};Assert(f.Call(old,id:id,args:args).State=="completed","first call");var p=ToolAccessPolicy.Read(f.Store);ToolAccessPolicy.Save(f.Store,false,p.Revision);f.Enable();string fresh=f.Share();Assert(fresh!=old,"global generation reused");Refused(()=>f.Call(old,id:id,args:args));Refused(()=>f.Call(fresh,id:id,args:args));Assert(f.Calls==1,"write repeated");
        });
        check("global completed calls replay without contacting an unavailable owner and never invoke twice",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share(),id=Guid.NewGuid().ToString("N");var args=new Dictionary<string,object>{{"resource","r"}};f.Call(share,id:id,args:args);int resolved=f.Resolves;f.ResolveOverride=o=>{throw new InvalidOperationException("offline");};Assert(f.Call(share,id:id,args:args).State=="completed"&&f.Calls==1&&f.Resolves==resolved,"completed write resent");
        });
        check("global access never accepts another chat or source account identity",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share(),id=Guid.NewGuid().ToString("N");var args=new Dictionary<string,object>{{"resource","r"}};f.Call(share,id:id,args:args);var other=f.Session();other.Thread="another-chat";Refused(()=>f.Call(share,source:other,id:id,args:args));f.SourceValid=false;Refused(()=>f.Shares());Refused(()=>f.Call(share));Refused(()=>f.Tunnel.Describe(f.Session(),share,"*","*"));Assert(f.Calls==1,"identity bypass");
        });
        check("global share identity changes with owner account or home and stale ids fail closed",delegate{
            var f=new Fixture(root);f.Enable();string original=f.Share();f.Owners[1].Account="different-account";string next=f.Share();Assert(next!=original,"account missing from binding");Refused(()=>f.Call(original));f.Owners[1].Home=Path.Combine(f.Store.Root,"relocated");Assert(f.Share()!=next,"home missing from binding");Refused(()=>f.Call(next));Assert(f.Inventories==0&&f.Calls==0,"stale share contacted owner");
        });
        check("global owner identity digest is independent of desktop and worker JSON escaping",delegate{
            var f=new Fixture(root);var policy=f.Enable();var owner=f.Owners[1];owner.Account="compte-é<'&";owner.Home=Path.Combine(f.Store.Root,"répertoire-é");string home=Path.GetFullPath(owner.Home).TrimEnd('\\','/').ToUpperInvariant();string expected="global:"+owner.Id+":"+Webhooks.Hash("global-tool-owner\n"+owner.Account.Length+":"+owner.Account+home).Substring(0,32)+":"+policy.Revision;Assert(f.Share()==expected,"runtime-dependent identity hash");
        });
        check("global mode retains transport owner verification before and after native inventory",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share();f.ResolveOverride=o=>new TunnelOwner{Id=o.Id,Name=o.Name,Home=o.Home,Account="wrong-native-account"};Assert(f.Call(share).State=="blocked"&&f.Calls==0&&f.Inventories==0,"native account drift accepted");f.ResolveOverride=null;f.Connections[f.Owners[1].Id].OnInventory=()=>f.Owners[1].Account="drift-after-inventory";Assert(f.Call(share).State=="blocked"&&f.Calls==0,"late identity drift accepted");
        });
        check("global revocation during inventory blocks dispatch and does not mutate customized grants",delegate{
            var f=new Fixture(root);f.Explicit();byte[] grants=File.ReadAllBytes(Path.Combine(f.Store.Root,"tool-grants.dpapi"));var policy=f.Enable();string share=f.Share();f.Connections[f.Owners[1].Id].OnInventory=()=>ToolAccessPolicy.Save(f.Store,false,policy.Revision);Assert(f.Call(share).State=="blocked"&&f.Calls==0,"revoked mode sent a write");Assert(grants.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Store.Root,"tool-grants.dpapi"))),"revocation rewrote grants");
        });
        check("global calls use current provider schema and risk changes without requiring a stale selected contract",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share();var tool=f.Connections[f.Owners[1].Id].Tools[1];tool.ReadOnly=true;tool.Annotations=new{readOnlyHint=true};tool.Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"resource\":{\"type\":\"string\"},\"new_native_field\":{\"type\":\"string\"}},\"required\":[\"resource\",\"new_native_field\"]}");var call=f.Call(share,args:new Dictionary<string,object>{{"resource","r"},{"new_native_field","new"}});Assert(call.State=="completed"&&call.ContractStatus=="global","static contract restricted explicit global policy");
        });
        check("global missing native tools and invalid current arguments remain blocked before invocation",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share();Assert(f.Call(share,"not-native").State=="blocked","missing tool invented");Assert(f.Call(share,args:new Dictionary<string,object>{{"content","no required resource"}}).State=="blocked","required field omitted");Assert(f.Call(share,args:new Dictionary<string,object>{{"resource","r"},{"unexpected","bad"}}).State=="blocked"&&f.Calls==0,"native schema bypass");
        });
        check("global sharing preserves native upload and recursive relay transport exclusions",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share();var connection=f.Connections[f.Owners[1].Id];var tool=connection.Tools[1];tool.Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"resource\":{\"type\":\"string\"},\"file\":{\"type\":\"string\",\"description\":\"This parameter expects an absolute local file path.\"}}}");Assert(f.Call(share,args:new Dictionary<string,object>{{"resource","r"},{"file","C:/fake.zip"}}).State=="blocked","unsupported upload sent");foreach(string server in new[]{"creezio-relay","codex_app","cua_repl","node_repl","code-review"}){var excluded=Tool("excluded",false);excluded.Server=server;connection.Tools.Add(excluded);var c=f.Tunnel.Invoke(f.Session(),Guid.NewGuid().ToString("N"),share,server,"excluded",new Dictionary<string,object>{{"resource","r"}},CancellationToken.None).GetAwaiter().GetResult();Assert(c.State=="blocked","recursive control exported");}Assert(f.Calls==0,"unsupported transport invoked");
        });
        check("global native approval requirements do not auto approve or repeat a call",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share(),id=Guid.NewGuid().ToString("N");var args=new Dictionary<string,object>{{"resource","r"}};f.Connections[f.Owners[1].Id].OnInvoke=()=>{throw new TunnelInteractionException();};var call=f.Call(share,id:id,args:args);Assert(call.State=="uncertain"&&call.Error.Contains("interaction native"),"native approval requirement hidden");f.Call(share,id:id,args:args);Assert(f.Calls==1,"native interaction repeated");
        });
        check("global cancellation and an unavailable selected account never dispatch and do not affect metadata list",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share();var cancel=new CancellationTokenSource();f.Connections[f.Owners[1].Id].OnInventory=()=>cancel.Cancel();Assert(f.Call(share,token:cancel.Token).State=="blocked"&&f.Calls==0,"cancelled write sent");f.Connections[f.Owners[1].Id].Offline=true;Assert(f.Call(share).State=="blocked"&&f.Shares().Length==2&&f.Calls==0,"one unavailable owner broke global list");
        });
        check("corrupt global policy is reported and cannot silently disable or overwrite stored access policy",delegate{
            var f=new Fixture(root);f.Enable();string path=Path.Combine(f.Store.Root,ToolAccessPolicy.FileName);File.WriteAllBytes(path,new byte[]{1,2,3,4});byte[] broken=File.ReadAllBytes(path);bool readFailed=false,saveFailed=false;try{ToolAccessPolicy.Read(f.Store);}catch{readFailed=true;}try{ToolAccessPolicy.Save(f.Store,false,"initial");}catch{saveFailed=true;}Assert(readFailed&&saveFailed&&broken.SequenceEqual(File.ReadAllBytes(path)),"corrupt policy silently replaced");
        });
        check("global metadata enumeration from vault excludes archived and unsigned instances without launching them",delegate{
            var f=new Fixture(root);var vault=new Vault(Path.GetDirectoryName(f.Store.Root));var data=vault.Load();data.Instances.Add(new DesktopInstance{Id=f.Owners[1].Id,Name="Signed owner",AccountKey="signed"});data.Instances.Add(new DesktopInstance{Id=Guid.NewGuid().ToString("N"),Name="Archived owner",AccountKey="archived",Archived=true});data.Instances.Add(new DesktopInstance{Id=Guid.NewGuid().ToString("N"),Name="Unsigned owner"});vault.Save(data);f.Enable();int connects=0;var tunnel=new ToolTunnel(f.Store,id=>{throw new Exception("owner resolver contacted");},o=>{connects++;throw new Exception("connection started");},s=>{});var shares=RelayEngine.Rows(Json.Read<object>(Json.Write(tunnel.List(f.Session())))).ToArray();Assert(shares.Length==1&&Json.Str(Json.Get(shares[0],"instance"))==f.Owners[1].Id&&connects==0,"metadata policy violated");
        });
        check("global Pages read and edit work for a future Page without a static resource or tool grant",delegate{
            var f=new Fixture(root);f.Enable();DocumentTools(f);string share=f.Share();var viewer=new SharedPages(f.Store,f.Tunnel);var document=viewer.Read(f.Session(),share,"page-created-after-policy",CancellationToken.None).GetAwaiter().GetResult();Assert(document.CanEdit&&document.Blocks[0].Markdown=="Original","global Page unreadable");var save=viewer.Save(f.Session(),Guid.NewGuid().ToString("N"),share,document.Page,document.ReadId,"block","Updated",CancellationToken.None).GetAwaiter().GetResult();Assert(save.State=="saved"&&save.Document.Blocks[0].Markdown=="Updated"&&f.Calls==3&&f.Tunnel.Grants().Count==0,"global Page edit failed");
        });
        check("global Page read revocation and missing native editor remain enforced",delegate{
            var f=new Fixture(root);var policy=f.Enable();DocumentTools(f);string share=f.Share();var viewer=new SharedPages(f.Store,f.Tunnel);var doc=viewer.Read(f.Session(),share,"page",CancellationToken.None).GetAwaiter().GetResult();f.Connections[f.Owners[1].Id].Tools.RemoveAll(t=>t.Name==SharedPages.EditTool);var save=viewer.Save(f.Session(),Guid.NewGuid().ToString("N"),share,"page",doc.ReadId,"block","Updated",CancellationToken.None).GetAwaiter().GetResult();Assert(save.State=="blocked"&&f.Calls==1,"unavailable editor invented");f.Connections[f.Owners[1].Id].OnInvoke=()=>ToolAccessPolicy.Save(f.Store,false,policy.Revision);Refused(()=>viewer.Read(f.Session(),share,"page",CancellationToken.None).GetAwaiter().GetResult());
        });
        check("global resource links require explicit Page or Site kind and preserve legacy MCP requirements",delegate{
            var f=new Fixture(root);f.Enable();string share=f.Share();var links=new SharedResourceLinks(f.Store,f.Tunnel,(g,t,c)=>Task.FromResult<object>(new{requested=true}));using(var server=ResourceLinkServer.Start(f.Store,links)){Refused(()=>links.Create(f.Session(),share,"resource"));Refused(()=>links.Create(f.Session(),share,"resource","invented"));var page=links.Create(f.Session(),share,"resource","page");var site=links.Create(f.Session(),share,"resource","site");Assert(page.Kind=="Page"&&site.Kind=="Site"&&page.Url!=site.Url&&f.Inventories==0&&f.Calls==0,"link kind collision or eager provider access");}var schema=RelayMcp.Tools().Select(t=>Json.Read<object>(Json.Write(t))).Single(t=>Json.Str(Json.Get(t,"name"))=="get_shared_resource_link");var input=Json.Get(schema,"inputSchema");Assert(Json.Get(Json.Get(input,"properties"),"kind")!=null&&!RelayEngine.Rows(Json.Get(input,"required")).Cast<string>().Contains("kind"),"legacy link requires new field");var row=f.Shares().First();Assert(Json.Str(Json.Get(Json.Get(row,"resourceLinks"),"tool"))=="get_shared_resource_link","global link discovery metadata absent");
        });
        check("global Page links open native owner target without reading a body and are revoked on disable",delegate{
            var f=new Fixture(root);var policy=f.Enable();string share=f.Share();int opened=0;object target=null;var links=new SharedResourceLinks(f.Store,f.Tunnel,(g,t,c)=>{opened++;target=t;return Task.FromResult<object>(new{requested=true});});using(var server=ResourceLinkServer.Start(f.Store,links)){var link=links.Create(f.Session(),share,"unlisted-page","page");string id=new Uri(link.Url).AbsolutePath.Substring(3);links.Open(id,CancellationToken.None).GetAwaiter().GetResult();Assert(opened==1&&Json.Str(Json.Get(Json.Read<object>(Json.Write(target)),"pageId"))=="unlisted-page"&&f.Calls==0,"wrong Page target or eager body read");ToolAccessPolicy.Save(f.Store,false,policy.Revision);Refused(()=>links.Open(id,CancellationToken.None).GetAwaiter().GetResult());Assert(opened==1,"revoked link opened");}
        });
        check("global Site links verify the exact native Site before navigating and remain owner pinned",delegate{
            var f=new Fixture(root);f.Enable();DocumentTools(f);string share=f.Share();int opened=0;object target=null;var links=new SharedResourceLinks(f.Store,f.Tunnel,(g,t,c)=>{opened++;target=t;return Task.FromResult<object>(new{requested=true});});using(var server=ResourceLinkServer.Start(f.Store,links)){var link=links.Create(f.Session(),share,"site-created-after-policy","site");string id=new Uri(link.Url).AbsolutePath.Substring(3);links.Open(id,CancellationToken.None).GetAwaiter().GetResult();Assert(opened==1&&Json.Str(Json.Get(Json.Read<object>(Json.Write(target)),"pageId"))=="native-attached-page"&&f.Calls==1,"Site was not verified");f.Owners[1].Account="replacement";Refused(()=>links.Open(id,CancellationToken.None).GetAwaiter().GetResult());Assert(opened==1&&f.Calls==1,"old link acquired replacement identity");}
        });
    }
}
