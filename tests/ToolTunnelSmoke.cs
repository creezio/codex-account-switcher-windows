using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Creezio.Switcher;

internal static class ToolTunnelSmoke
{
    public sealed class Acceptance {public string CreateId {get;set;} public string Grant {get;set;} public string Page {get;set;} public string Url {get;set;} public string EditId {get;set;} public object EditArguments {get;set;} public string SiteCreateId {get;set;} public string SiteGrant {get;set;} public string Site {get;set;}}
    private static object Value(object result)
    {
        if(Json.Get(result,"structuredContent")!=null)return Json.Get(result,"structuredContent");
        foreach(var item in RelayEngine.Rows(Json.Get(result,"content")))if(Json.Str(Json.Get(item,"type"))=="text")return Json.Read<object>(Json.Str(Json.Get(item,"text")));
        throw new Exception("No structured result");
    }
    private static object Call(ToolTunnel tunnel,RelaySession session,TunnelGrant grant,string name,object args,string id=null)
    {
        var call=tunnel.Invoke(session,id??Guid.NewGuid().ToString("N"),grant.Id,"codex_apps",name,Json.Read<object>(Json.Write(args)),CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(Json.Write(ToolTunnel.Summary(call)));
        if(call.State!="completed")throw new Exception("Provider call not completed: "+call.Error);
        return Value(call.Result??tunnel.Read(session,call.Id).Result);
    }
    public static int Main(string[] args)
    {
        try{
            var store=new RelayStore(RelayStore.DefaultRoot);var tunnel=new ToolTunnel(store);
            if(args.Length>2&&args[2]=="resources"){
                var before=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants())));
                var data=new InstanceResources(store).Discover(args[0],CancellationToken.None).GetAwaiter().GetResult();
                if(before!=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants()))))throw new Exception("Discovery changed grants");
                Console.WriteLine(Json.Write(new{plugins=data.Plugins.Count,resources=data.Resources.Count,types=data.Resources.GroupBy(r=>r.Kind).Select(g=>new{kind=g.Key,count=g.Count()}),states=data.Plugins.Select(p=>new{plugin=p.Name,state=p.State,message=p.State=="error"?p.Message:null})}));
                Console.WriteLine("PASS metadata discovery; no model turn; existing permissions preserved");return 0;
            }
            var catalog=tunnel.Discover(args[0],CancellationToken.None).GetAwaiter().GetResult();
            if(args.Length>2&&args[2]=="plugin-context"){
                var accounts=new AccountService(Path.GetDirectoryName(store.Root));var owner=NamedInstances.Resolve(accounts,args[0]);var client=accounts.Data.Instances.First(i=>!i.Archived&&i.Id!=owner.Id&&!String.IsNullOrEmpty(i.AccountKey));
                if(args.Length<4||String.IsNullOrWhiteSpace(args[3]))throw new Exception("Provide the exact plugin name with -PluginName");
                var matching=catalog.Where(t=>String.Equals(t.Group,args[3],StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matching.Length==0)throw new Exception("Target plugin not present on this account");
                if(matching.Select(InstanceResources.PluginKey).Distinct().Count()!=1)throw new Exception("Ambiguous plugin name");
                var key=InstanceResources.PluginKey(matching[0]);var before=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants())));
                var pluginState=new PluginAccess(store).Load(owner.Id,owner.AccountKey,accounts.Instances.Home(owner),client.Id,accounts.Instances.Home(client),client.AccountKey,key,CancellationToken.None).GetAwaiter().GetResult();
                if(pluginState.Tools.Any(t=>InstanceResources.PluginKey(t)!=key))throw new Exception("Mixed plugin context");
                if(before!=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants()))))throw new Exception("Loading changed permissions");
                Console.WriteLine(Json.Write(new{plugin=matching[0].Group,actions=pluginState.Tools.Length,namedActions=pluginState.Tools.Count(t=>PluginAccess.Label(t)!=t.Name),readActions=pluginState.Tools.Count(t=>t.ReadOnly),selected=pluginState.Selected.Count,existingRules=pluginState.ExistingRules.Length}));
                Console.WriteLine("PASS exact live plugin context; no action invoked and no permission changed");return 0;
            }
            var wanted=new[]{"chatgpt_space.list_pages","chatgpt_space.create_page","chatgpt_space.read_page","chatgpt_space.edit_page","sites.create_site","sites.save_site_version","sites.save_version_and_deploy_private","sites.deploy_site_version","sites.get_site","sites.create_source_repository_write_credential","sites.get_deployment_status","sites.list_site_versions"};
            var tools=catalog.Where(t=>wanted.Contains(t.Name)).ToArray();
            File.WriteAllText(args[1],Json.Write(tools));Console.WriteLine("PASS official app-server inventory: "+catalog.Length+" tools; saved "+tools.Length+" selected schemas (no credentials)");
            if(args.Length<3||String.IsNullOrWhiteSpace(args[2]))return 0;
            if(args[2]=="resources-read"){
                var sessionTest=RelaySessions.Bind(store,null,CancellationToken.None).GetAwaiter().GetResult();
                var service=new InstanceResources(store);var owner=tunnel.Owner(args[0]);
                var inventory=service.Cached(owner.Id,owner.Account,owner.Home);
                if(!InstanceResources.Fresh(inventory))inventory=service.Discover(owner.Id,CancellationToken.None).GetAwaiter().GetResult();
                var page=inventory.Resources.Single(r=>r.Kind=="Page"&&r.Title=="Validation du tunnel Account Switcher");
                string client="acceptance-"+Guid.NewGuid().ToString("N"),initial=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants())));
                try{
                    service.Save(inventory,client,sessionTest.Home,sessionTest.Account,new Dictionary<string,string>{{page.Key,"read"}},service.Revision(owner.Id,client),CancellationToken.None).GetAwaiter().GetResult();
                    var selected=tunnel.Grants().Single(g=>g.CatalogClient==client);
                    if(selected.ResourceValues.Count!=1||selected.ResourceLabels[page.Value]!=page.Title)throw new Exception("Incorrect named allowlist");
                    Call(tunnel,sessionTest,selected,"chatgpt_space.read_page",new{page_id=page.Value,include_content=false});
                    Console.WriteLine("PASS catalog selection -> named scoped grant -> real cross-account metadata read");
                }finally{service.Save(inventory,client,sessionTest.Home,sessionTest.Account,new Dictionary<string,string>(),service.Revision(owner.Id,client),CancellationToken.None).GetAwaiter().GetResult();}
                if(initial!=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants()))))throw new Exception("Existing permissions changed");
                Console.WriteLine("PASS test access revoked; previous permissions preserved");return 0;
            }
            if(args[2]=="integration-status"){
                var accounts=new AccountService(Path.GetDirectoryName(store.Root));
                foreach(var instance in accounts.Data.Instances.Where(i=>!i.Archived)){
                    var integration=RelayIntegration.Status(store,accounts.Instances.Home(instance));
                    Console.WriteLine(Json.Write(new{instance=instance.Name,healthy=integration.Healthy,version=integration.InstalledVersion,status=integration.Status}));
                    if(!integration.Healthy||!integration.InstalledVersion.StartsWith(RelayWorker.Version+"+"))throw new Exception("Integration update not confirmed");
                }
                return 0;
            }
            var session=RelaySessions.Bind(store,null,CancellationToken.None).GetAwaiter().GetResult();
            var state=store.ReadRecord<Acceptance>("tool-tunnel-acceptance.dpapi");
            if(state.CreateId==null){state.CreateId=Guid.NewGuid().ToString("N");state.Grant=Guid.NewGuid().ToString("N");store.WriteRecord("tool-tunnel-acceptance.dpapi",state);}
            var grant=new TunnelGrant{Id=state.Grant,Name="Validation du tunnel Pages",Enabled=true,Instance=args[0],Sources=new Dictionary<string,string>{{session.Home,session.Account}}};
            if(args[2]=="pages-create"){
                grant.Tools=tools.Where(t=>t.Name=="chatgpt_space.create_page").ToList();tunnel.SaveGrant(grant);
                var page=Call(tunnel,session,grant,"chatgpt_space.create_page",new{title="Validation du tunnel Account Switcher",initial_blocks=new[]{"Cette page privée sert à vérifier les appels directs entre deux instances Codex.\n\nÉtat initial : en attente de modification."},idempotency_key="switcher-tunnel-"+state.CreateId},state.CreateId);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1]),"tunnel-page-create.json"),Json.Write(page));
                Console.WriteLine(Json.Write(page));
            }
            if(args[2]=="pages-list"){
                grant.Tools=catalog.Where(t=>t.Name=="chatgpt_space.list_pages").ToList();tunnel.SaveGrant(grant);
                var data=Call(tunnel,session,grant,"chatgpt_space.list_pages",new{limit=100});Console.WriteLine(Json.Write(RelayEngine.Rows(Json.Get(data,"items")).Where(p=>Json.Str(Json.Get(p,"title"))=="Validation du tunnel Account Switcher").ToArray()));
            }
            if(args[2]=="pages-reconcile"){
                // Manual recovery of this test creation only: same provider idempotency key,
                // after a read-only inventory check found no matching Page. Never generic retry.
                grant.Tools=tools.Where(t=>t.Name=="chatgpt_space.create_page").ToList();tunnel.SaveGrant(grant);
                var page=Call(tunnel,session,grant,"chatgpt_space.create_page",new{title="Validation du tunnel Account Switcher",initial_blocks=new[]{"Cette page privée sert à vérifier les appels directs entre deux instances Codex.\n\nÉtat initial : en attente de modification."},idempotency_key="switcher-tunnel-"+state.CreateId});
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1]),"tunnel-page-create.json"),Json.Write(page));Console.WriteLine(Json.Write(page));
            }
            if(args[2]=="pages-edit"){
                var created=Json.Read<object>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(args[1]),"tunnel-page-create.json")));
                state.Page=Json.Str(Json.Get(Json.Get(created,"metadata"),"page_id"));state.Url=Json.Str(Json.Get(Json.Get(created,"metadata"),"url"));
                if(state.Page.Length==0)throw new Exception("Created Page ID missing");
                grant.ResourceField="page_id";grant.ResourceValue=state.Page;grant.Tools=tools.Where(t=>t.Name=="chatgpt_space.read_page"||t.Name=="chatgpt_space.edit_page").ToList();tunnel.SaveGrant(grant);
                var before=Call(tunnel,session,grant,"chatgpt_space.read_page",new{page_id=state.Page,include_content=true});
                var content=Json.Get(before,"content");var metadata=Json.Get(before,"metadata");
                const string final="État final : modification reçue via le tunnel Account Switcher, sans prompt envoyé à une autre conversation.";
                if(state.EditId==null){
                    var block=RelayEngine.Rows(Json.Get(content,"blocks")).Single(b=>Json.Str(Json.Get(b,"markdown")).Contains("État initial"));
                    state.EditId=Guid.NewGuid().ToString("N");
                    state.EditArguments=new{page_id=state.Page,stream_kind=Json.Str(Json.Get(metadata,"stream_kind"))=="scratch"?"scratch":"content",base_sequence=Json.Get(metadata,"current_sequence"),operations=new[]{new{op="replace_block_markdown",block_id=Json.Str(Json.Get(block,"id")),expected_hash=Json.Str(Json.Get(block,"hash")),markdown=final}}};
                    store.WriteRecord("tool-tunnel-acceptance.dpapi",state);
                }
                var receipt=Call(tunnel,session,grant,"chatgpt_space.edit_page",state.EditArguments,state.EditId);Console.WriteLine("EDIT_RECEIPT "+Json.Write(receipt));
                Call(tunnel,session,grant,"chatgpt_space.edit_page",state.EditArguments,state.EditId);
                var after=Call(tunnel,session,grant,"chatgpt_space.read_page",new{page_id=state.Page,include_content=true});
                if(!RelayEngine.Rows(Json.Get(Json.Get(after,"content"),"blocks")).Any(b=>Json.Str(Json.Get(b,"markdown"))==final))throw new Exception("Page readback mismatch (including Unicode)");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1]),"tunnel-page-evidence.json"),Json.Write(new{page_id=state.Page,url=state.Url,edit_call=state.EditId,readback=true,unicode=true,no_model_turn=true}));
                tunnel.Disable(grant.Id);Console.WriteLine("PASS cross-account Page edit + Unicode readback + idempotent replay; test share disabled: "+state.Url);
            }
            if(args[2]=="site-create"){
                if(state.SiteCreateId==null){state.SiteCreateId=Guid.NewGuid().ToString("N");state.SiteGrant=Guid.NewGuid().ToString("N");store.WriteRecord("tool-tunnel-acceptance.dpapi",state);}
                grant.Id=state.SiteGrant;grant.Name="Validation du tunnel Sites";grant.Tools=tools.Where(t=>t.Name=="sites.create_site").ToList();tunnel.SaveGrant(grant);
                var result=Call(tunnel,session,grant,"sites.create_site",new{title="Validation du tunnel Account Switcher",slug="switcher-tunnel-"+state.SiteCreateId.Substring(0,8),description="Site privé de validation des appels directs entre instances Codex."},state.SiteCreateId);
                state.Site=Json.Str(Json.Get(result,"id"));if(state.Site.Length==0)throw new Exception("Site ID missing; inspect encrypted call result before another create");store.WriteRecord("tool-tunnel-acceptance.dpapi",state);
                Console.WriteLine("SITE "+state.Site+" keys="+String.Join(",",Json.Obj(result).Keys));
            }
            if(args[2]=="site-read"){
                if(String.IsNullOrEmpty(state.Site))throw new Exception("Create the test Site first");
                grant.Id=state.SiteGrant;grant.Name="Validation du tunnel Sites";grant.ResourceField="project_id";grant.ResourceValue=state.Site;
                grant.Tools=tools.Where(t=>t.Name=="sites.get_site").ToList();tunnel.SaveGrant(grant);
                var site=Call(tunnel,session,grant,"sites.get_site",new{project_id=state.Site});
                if(Json.Str(Json.Get(site,"id"))!=state.Site)throw new Exception("Unexpected Site identity");
                Console.WriteLine("PASS cross-account Site read; native archive upload is unavailable in this transport.");
                tunnel.Disable(grant.Id);
            }
            if(args[2]=="finish"){
                // Only records created by this acceptance test are sanitized. Preserve receipts and resource IDs.
                int count=0;
                foreach(string path in Directory.EnumerateFiles(store.Root,"tool-call-*.dpapi")){
                    var call=store.ReadRecord<TunnelCall>(Path.GetFileName(path));
                    if(call.Grant!=state.Grant&&call.Grant!=state.SiteGrant)continue;
                    if(call.Result==null&&call.HasResult)call.Result=store.ReadRecord<TunnelCall>("tool-result-"+call.Id+".dpapi").Result;
                    if(call.Result==null)continue;
                    bool sensitive=false;var result=ToolTunnel.Redact(call.Result,ref sensitive);
                    store.WriteRecord("tool-result-"+call.Id+".dpapi",new TunnelCall{Id=call.Id,Result=result});
                    call.SensitiveResult=call.SensitiveResult||sensitive;call.HasResult=true;call.Result=null;store.WriteRecord(Path.GetFileName(path),call);count++;
                }
                foreach(var g in tunnel.Grants().Where(g=>g.Id==state.Grant||g.Id==state.SiteGrant))tunnel.Disable(g.Id);
                Console.WriteLine("PASS test shares disabled; "+count+" test responses sanitized, metadata retained.");
            }
            return 0;
        }catch(Exception e){Console.WriteLine("FAIL "+e.Message);return 1;}
    }
}
