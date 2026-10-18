using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // Portal authorization is checked before creating any profile process. The
    // short-lived app-server is used only for discovery and explicitly selected
    // read-only widget tools; it never starts a model turn or resumes a chat.
    internal static class PortalMcpCapabilities
    {
        private sealed class LiveTool
        {
            internal string Id,Server,Name,Title,Description,PluginId,PluginName,Uri,Protocol;
            internal object Schema,Raw;
            internal bool ReadOnly,AppAccessible,Connected;
        }
        private sealed class Scope : IDisposable
        {
            internal RelayStore Store;internal AccountService Accounts;internal DesktopInstance Instance;
            internal string Slug,Bearer,ExpectedAccount,ExpectedHome;
            internal IHeadlessRpc Rpc;internal HeadlessProcesses Tracking;internal string Thread;
            internal Action VerifyScope;
            internal void Verify(){if(VerifyScope!=null){VerifyScope();return;}var grant=Portal.RequireGrant(Store,Slug,Bearer,Instance.Id);if(grant.AccountKey!=ExpectedAccount||!RelayStore.SamePath(grant.Home,ExpectedHome))throw new InvalidOperationException("L'autorisation du portail a change pendant cette operation.");}
            public void Dispose(){try{if(Rpc!=null)Rpc.Dispose();}finally{if(Tracking!=null)Tracking.Dispose();}}
        }
        private static Scope Open(RelayStore store,string slug,string bearer,string instanceId)
        {
            var grant=Portal.RequireGrant(store,slug,bearer,instanceId);
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,grant.InstanceId);
            NamedInstances.VerifyAccount(accounts,instance);
            if(instance.AccountKey!=grant.AccountKey||!RelayStore.SamePath(accounts.Instances.Home(instance),grant.Home))throw new InvalidOperationException("Le compte autorise pour ce portail a change.");
            var scope=new Scope{Store=store,Accounts=accounts,Instance=instance,Slug=slug,Bearer=bearer,ExpectedAccount=grant.AccountKey,ExpectedHome=grant.Home};
            try{scope.Rpc=new HeadlessRpc(accounts.Settings.CodexExecutable,grant.Home,grant.AccountKey);scope.Tracking=new HeadlessProcesses(store,scope.Rpc,grant.Home,grant.AccountKey);scope.Verify();return scope;}catch{scope.Dispose();throw;}
        }
        internal static async Task<object> Get(RelayStore store,string slug,string bearer,string instanceId,CancellationToken token)
        {
            using(var scope=Open(store,slug,bearer,instanceId)){
                var warnings=new List<string>();object rawSkills=null,rawPlugins=null;
                var grant=Portal.RequireGrant(store,slug,bearer,instanceId);
                try{rawSkills=await scope.Rpc.Call("skills/list",new{cwds=new[]{grant.Workspace},forceReload=false},token).ConfigureAwait(false);}catch(HeadlessRpcException){warnings.Add("Le catalogue des skills n'est pas disponible sur cette version de Codex.");}
                try{rawPlugins=await scope.Rpc.Call("plugin/list",new{forceRefetch=false},token).ConfigureAwait(false);}catch(HeadlessRpcException){warnings.Add("Le catalogue des plugins n'est pas disponible sur cette version de Codex.");}
                var tools=await Inventory(scope,token).ConfigureAwait(false);scope.Verify();
                var plugins=PluginRows(rawPlugins).Select(p=>Json.Read<object>(Json.Write(p))).ToList();
                foreach(var group in tools.Where(t=>t.PluginId.Length>0).GroupBy(t=>t.PluginId))if(!plugins.Any(p=>Json.Str(Json.Get(p,"id"))==group.Key))plugins.Add(new{id=group.Key,name=group.First().PluginName,description="Connecteur de ce compte",enabled=group.Any(t=>t.Connected),installed=true});
                var modelCatalog=await ModelCatalog(scope.Rpc,token,warnings).ConfigureAwait(false);scope.Verify();
                return new{instanceId=scope.Instance.Id,skills=SkillRows(rawSkills),plugins=plugins.Select(p=>PublicPlugin(p)).ToArray(),tools=tools.Select(PublicTool).ToArray(),models=modelCatalog.Models,collaborationModes=modelCatalog.Modes,supportsPlanMode=modelCatalog.SupportsPlanMode,permissionProfiles=new[]{new{id="inherit",label="Conserver les permissions du chat"},new{id="read-only",label="Lecture seule"},new{id="workspace-write",label="Lecture et ecriture dans le dossier"},new{id="full-access",label="Acces complet"}},composer=new{nativeDesktopStructured=true,nativeHeadlessStructured=true,pluginMentionFormat="native-mention",skillMentionFormat="native-skill",desktopPlanMode=modelCatalog.SupportsPlanMode,headlessPlanMode=modelCatalog.SupportsPlanMode,desktopGuard="unique-native-thread-and-pinned-account"},warnings=warnings.ToArray(),widgetPolicy="read-only",generatedUtc=DateTime.UtcNow.ToString("o")};
            }
        }
        private static object PublicPlugin(object value)
        {
            var p=Json.Read<object>(Json.Write(value));string id=Json.Str(Json.Get(p,"id"));bool enabled=Object.Equals(Json.Get(p,"enabled"),true);
            return new{id=id,name=Json.Str(Json.Get(p,"name")),description=Json.Str(Json.Get(p,"description")),enabled=enabled,installed=Object.Equals(Json.Get(p,"installed"),true),invokeSupported=enabled,invocationKind=id.StartsWith("connector_",StringComparison.Ordinal)?"app-mention":"plugin-mention"};
        }
        internal static async Task<PortalComposerInventory> ComposerInventory(RelayStore store,string rootRouteId,PortalInstanceGrant grant,string workspace,CancellationToken token,PortalMessageOptions options=null)
        {
            PortalComposer.VerifyGrant(store,rootRouteId,grant);var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,grant.InstanceId);
            using(var scope=new Scope{Store=store,Accounts=accounts,Instance=instance,ExpectedAccount=grant.AccountKey,ExpectedHome=grant.Home,VerifyScope=()=>PortalComposer.VerifyGrant(store,rootRouteId,grant)}){
                scope.Rpc=new HeadlessRpc(accounts.Settings.CodexExecutable,grant.Home,grant.AccountKey);scope.Tracking=new HeadlessProcesses(store,scope.Rpc,grant.Home,grant.AccountKey);scope.Verify();
                object rawSkills=null,rawPlugins=null;var tools=new List<LiveTool>();
                if(options==null||options.Skills.Count>0)rawSkills=await scope.Rpc.Call("skills/list",new{cwds=new[]{workspace},forceReload=false},token).ConfigureAwait(false);
                if(options==null||options.Plugins.Count>0){rawPlugins=await scope.Rpc.Call("plugin/list",new{forceRefetch=false},token).ConfigureAwait(false);tools=await Inventory(scope,token).ConfigureAwait(false);}
                var warnings=new List<string>();var catalog=await ModelCatalog(scope.Rpc,token,warnings).ConfigureAwait(false);catalog.Warnings=warnings.ToArray();
                catalog.Skills=RelayEngine.Rows(Json.Get(rawSkills,"data")).SelectMany(e=>RelayEngine.Rows(Json.Get(e,"skills"))).Take(4000).Select(s=>new PortalSkillReference{Id=ToolTunnel.Hash(new{kind="skill",name=Json.Str(Json.Get(s,"name")),path=Json.Str(Json.Get(s,"path"))}),Name=Json.Str(Json.Get(s,"name")),Path=Json.Str(Json.Get(s,"path")),Enabled=Object.Equals(Json.Get(s,"enabled"),true)}).ToArray();
                var plugins=PluginRows(rawPlugins).Select(p=>Json.Read<object>(Json.Write(p))).Select(p=>new PortalPluginReference{Id=Json.Str(Json.Get(p,"id")),Name=Json.Str(Json.Get(p,"name")),Enabled=Object.Equals(Json.Get(p,"enabled"),true),InvokeSupported=Object.Equals(Json.Get(p,"enabled"),true)}).ToList();
                foreach(var group in tools.Where(t=>t.PluginId.Length>0&&t.Server=="codex_apps").GroupBy(t=>t.PluginId)){var plugin=plugins.FirstOrDefault(p=>p.Id==group.Key);bool connected=group.Any(t=>t.Connected);if(plugin==null)plugins.Add(new PortalPluginReference{Id=group.Key,Name=group.First().PluginName,Enabled=connected,InvokeSupported=connected});else plugin.InvokeSupported&=connected;}
                catalog.Plugins=plugins.ToArray();scope.Verify();return catalog;
            }
        }
        private static async Task<PortalComposerInventory> ModelCatalog(IHeadlessRpc rpc,CancellationToken token,List<string> warnings)
        {
            var models=new List<object>();var seen=new HashSet<string>(StringComparer.Ordinal);string cursor=null;
            try{do{var result=await rpc.Call("model/list",new{cursor=cursor,limit=100,includeHidden=false},token).ConfigureAwait(false);models.AddRange(RelayEngine.Rows(Json.Get(result,"data")).Select(PublicModel));cursor=Json.Str(Json.Get(result,"nextCursor"));if(cursor.Length>0&&(!seen.Add(cursor)||seen.Count>=10))throw new InvalidOperationException("Catalogue des modeles trop volumineux.");}while(cursor.Length>0);}catch(HeadlessRpcException){warnings.Add("Le catalogue des modeles n'est pas disponible ; aucun modele inconnu ne sera accepte.");}
            object[] modes=new object[0];try{var raw=await rpc.Call("collaborationMode/list",new{},token).ConfigureAwait(false);modes=RelayEngine.Rows(Json.Get(raw,"data")).Take(20).Select(m=>new{mode=Clip(Json.Get(m,"mode"),40),name=Clip(Json.Get(m,"name"),120),model=Clip(Json.Get(m,"model"),120),reasoningEffort=Clip(Json.Get(m,"reasoning_effort"),30)}).Cast<object>().ToArray();}catch(HeadlessRpcException){warnings.Add("Les modes de collaboration ne sont pas exposes par cette version.");}
            var normalizedModes=modes.Select(m=>Json.Read<object>(Json.Write(m))).ToArray();return new PortalComposerInventory{Models=models.Take(500).ToArray(),Modes=normalizedModes,SupportsPlanMode=normalizedModes.Any(m=>Json.Str(Json.Get(m,"mode"))=="plan")};
        }
        internal static object PublicModel(object model)
        {return Json.Read<object>(Json.Write(new{id=Clip(Json.Get(model,"model"),120),catalogId=Clip(Json.Get(model,"id"),120),name=Clip(Json.Get(model,"displayName"),160),description=Clip(Json.Get(model,"description"),1000),isDefault=Object.Equals(Json.Get(model,"isDefault"),true),defaultReasoningEffort=Clip(Json.Get(model,"defaultReasoningEffort"),30),reasoningEfforts=RelayEngine.Rows(Json.Get(model,"supportedReasoningEfforts")).Select(e=>Clip(Json.Get(e,"reasoningEffort"),30)).Where(e=>e.Length>0).Distinct().ToArray(),inputModalities=RelayEngine.Rows(Json.Get(model,"inputModalities")).Select(e=>Clip(e,30)).ToArray()}));}
        internal static object[] SkillRows(object raw)
        {
            return RelayEngine.Rows(Json.Get(raw,"data")).SelectMany(e=>RelayEngine.Rows(Json.Get(e,"skills"))).Take(4000).Select(s=>new{id=ToolTunnel.Hash(new{kind="skill",name=Json.Str(Json.Get(s,"name")),path=Json.Str(Json.Get(s,"path"))}),name=Clip(Json.Get(s,"name"),240),displayName=Clip(Json.Get(Json.Get(s,"interface"),"displayName"),240),description=Clip(Json.Get(s,"description"),6000),enabled=Object.Equals(Json.Get(s,"enabled"),true),scope=Clip(Json.Get(s,"scope"),40),pluginId=Clip(Json.Get(s,"pluginId"),300)}).Cast<object>().ToArray();
        }
        internal static object[] PluginRows(object raw)
        {
            return RelayEngine.Rows(Json.Get(raw,"marketplaces")).SelectMany(m=>RelayEngine.Rows(Json.Get(m,"plugins"))).Where(p=>Object.Equals(Json.Get(p,"installed"),true)).Take(2000).GroupBy(p=>Json.Str(Json.Get(p,"id"))).Select(g=>g.First()).Select(p=>new{id=Clip(Json.Get(p,"id"),300),name=Clip(Json.Get(Json.Get(p,"interface"),"displayName")??Json.Get(p,"name"),240),description=Clip(Json.Get(Json.Get(p,"interface"),"shortDescription"),6000),enabled=Object.Equals(Json.Get(p,"enabled"),true),installed=true}).Cast<object>().ToArray();
        }
        private static async Task<List<LiveTool>> Inventory(Scope scope,CancellationToken token)
        {
            if(scope.Thread==null){var context=await scope.Rpc.Call("thread/start",new{cwd=scope.Accounts.Instances.Home(scope.Instance),ephemeral=true},token).ConfigureAwait(false);scope.Thread=Json.Str(Json.Get(Json.Get(context,"thread"),"id"));if(scope.Thread.Length==0)throw new IOException("Contexte de decouverte Codex absent.");}
            var apps=await scope.Rpc.Call("app/installed",new{threadId=scope.Thread,forceRefresh=false},token).ConfigureAwait(false);
            var callable=new HashSet<string>(RelayEngine.Rows(Json.Get(apps,"apps")).Where(a=>Object.Equals(Json.Get(a,"enabled"),true)&&Object.Equals(Json.Get(a,"callable"),true)).Select(a=>Json.Str(Json.Get(a,"id"))),StringComparer.Ordinal);
            var tools=new List<LiveTool>();var cursors=new HashSet<string>();string cursor=null;
            do{var result=await scope.Rpc.Call("mcpServerStatus/list",new{threadId=scope.Thread,detail="toolsAndAuthOnly",cursor=cursor},token).ConfigureAwait(false);
                foreach(var server in RelayEngine.Rows(Json.Get(result,"data"))){var name=Json.Str(Json.Get(server,"name"));if(!ToolTunnel.AllowedServer(name))continue;foreach(var pair in Json.Obj(Json.Get(server,"tools"))){var tool=ParseTool(name,pair.Value);tool.Connected=name!="codex_apps"||callable.Contains(tool.PluginId);tools.Add(tool);}}
                cursor=Json.Str(Json.Get(result,"nextCursor"));if(cursor.Length>0&&!cursors.Add(cursor))throw new IOException("Pagination des outils repetee.");if(tools.Count>20000||cursors.Count>100)throw new IOException("Catalogue des outils trop volumineux.");
            }while(cursor.Length>0);
            return tools.GroupBy(t=>t.Id,StringComparer.Ordinal).Select(g=>g.First()).OrderBy(t=>t.PluginName).ThenBy(t=>t.Name).ToList();
        }
        private static LiveTool ParseTool(string server,object raw)
        {
            var meta=Json.Get(raw,"_meta");var ui=Json.Get(meta,"ui");var annotations=Json.Get(raw,"annotations");
            string uri=Json.Str(Json.Get(ui,"resourceUri"));if(uri.Length==0)uri=Json.Str(Json.Get(meta,"ui/resourceUri"));if(uri.Length==0)uri=Json.Str(Json.Get(meta,"openai/outputTemplate"));
            string name=Json.Str(Json.Get(raw,"name")),connector=Json.Str(Json.Get(meta,"connector_id"));
            bool appAccessible=Object.Equals(Json.Get(meta,"openai/widgetAccessible"),true)||RelayEngine.Rows(Json.Get(ui,"visibility")).Any(v=>Json.Str(v)=="app");
            return new LiveTool{Id=ToolTunnel.Hash(new{server=server,name=name,connector=connector}),Server=server,Name=name,Title=Clip(Json.Get(raw,"title"),240),Description=Clip(Json.Get(raw,"description"),6000),PluginId=connector.Length>0?connector:server,PluginName=Clip(Json.Get(meta,"connector_name")??server,240),Uri=uri,Protocol=uri.StartsWith("ui://",StringComparison.Ordinal)?(Json.Get(meta,"openai/outputTemplate")!=null?"openai":"mcp-app"):null,ReadOnly=Object.Equals(Json.Get(annotations,"readOnlyHint"),true)&&!Object.Equals(Json.Get(annotations,"destructiveHint"),true),AppAccessible=appAccessible,Schema=Json.Get(raw,"inputSchema"),Raw=raw};
        }
        private static object PublicTool(LiveTool t)
        {return new{id=t.Id,pluginId=t.PluginId,pluginName=t.PluginName,name=t.Name,title=t.Title,description=t.Description,readOnly=t.ReadOnly,connected=t.Connected,inputSchema=PublicSchema(t.Schema),widget=new{available=t.Protocol!=null,protocol=t.Protocol,resourceUri=t.Protocol!=null?t.Uri:null,reason=t.Uri.StartsWith("internal://",StringComparison.Ordinal)?"Interface interne reservee a Codex.":t.Protocol==null?"Cet outil ne declare pas d'interface MCP exportable.":null,appAccessible=t.AppAccessible}};}
        internal static object DescribeTool(string server,object raw){return PublicTool(ParseTool(server,raw));}
        internal static object PublicSchema(object schema)
        {
            var output=new Dictionary<string,object>();foreach(var key in new[]{"type","description","title","required","enum","minimum","maximum","minLength","maxLength"}){var value=Json.Get(schema,key);if(value!=null)output[key]=value;}
            var properties=Json.Obj(Json.Get(schema,"properties"));if(properties.Count>0)output["properties"]=properties.ToDictionary(p=>p.Key,p=>PublicSchema(p.Value));
            var items=Json.Get(schema,"items");if(items!=null)output["items"]=PublicSchema(items);return output;
        }
        internal static async Task<object> Widget(RelayStore store,string slug,string bearer,string instanceId,string toolId,CancellationToken token)
        {
            using(var scope=Open(store,slug,bearer,instanceId)){
                var tool=Find(await Inventory(scope,token).ConfigureAwait(false),toolId);if(tool.Protocol==null)throw new InvalidOperationException("Cet outil ne declare aucune interface MCP exportable.");
                var connector=Json.Str(Json.Get(Json.Get(tool.Raw,"_meta"),"connector_id"));scope.Verify();
                object result=await scope.Rpc.Call("mcpServer/resource/read",new{threadId=scope.Thread,server=tool.Server,uri=tool.Uri,connectorId=connector.Length>0?connector:null},token).ConfigureAwait(false);scope.Verify();
                return WidgetResponse(toolId,tool.Protocol,tool.Uri,result);
            }
        }
        internal static object WidgetResponse(string id,string protocol,string uri,object result)
        {
            var content=RelayEngine.Rows(Json.Get(result,"contents")).FirstOrDefault(c=>Json.Str(Json.Get(c,"uri"))==uri&&(Json.Str(Json.Get(c,"mimeType")).StartsWith("text/html",StringComparison.OrdinalIgnoreCase)));
            if(content==null)throw new InvalidOperationException("Le connecteur n'a pas retourne d'interface HTML pour cette ressource.");
            var html=Json.Str(Json.Get(content,"text"));if(html.Length==0){var blob=Json.Str(Json.Get(content,"blob"));if(blob.Length>2*1024*1024)throw new InvalidOperationException("Interface MCP trop volumineuse.");try{html=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(blob));}catch(Exception e){if(e is FormatException||e is System.Text.DecoderFallbackException)throw new InvalidOperationException("Interface MCP HTML illisible.");throw;}}
            if(Encoding.UTF8.GetByteCount(html)>1024*1024)throw new InvalidOperationException("Interface MCP trop volumineuse.");
            var meta=Json.Get(content,"_meta");var csp=Json.Get(Json.Get(meta,"ui"),"csp");var legacy=Json.Get(meta,"openai/widgetCSP");
            return new{toolId=id,protocol=protocol,html=html,mimeType=Json.Str(Json.Get(content,"mimeType")),csp=new{connectDomains=Domains(Json.Get(csp,"connectDomains")??Json.Get(legacy,"connect_domains")),resourceDomains=Domains(Json.Get(csp,"resourceDomains")??Json.Get(legacy,"resource_domains")),frameDomains=Domains(Json.Get(csp,"frameDomains")??Json.Get(legacy,"frame_domains"))},readOnly=true};
        }
        internal static string[] Domains(object values)
        {
            var result=new List<string>();foreach(var value in RelayEngine.Rows(values)){var text=Json.Str(value);Uri uri;if(text.Length<=2048&&Uri.TryCreate(text,UriKind.Absolute,out uri)&&(uri.Scheme=="https"||uri.Scheme=="wss")&&String.IsNullOrEmpty(uri.UserInfo)&&String.IsNullOrEmpty(uri.Query)&&String.IsNullOrEmpty(uri.Fragment)&&uri.AbsolutePath=="/"&&Uri.CheckHostName(uri.Host)==UriHostNameType.Dns)result.Add(uri.GetLeftPart(UriPartial.Authority));}return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
        }
        internal static async Task<object> CallReadOnly(RelayStore store,string slug,string bearer,string instanceId,string widgetToolId,string requestedTool,object arguments,CancellationToken token)
        {
            if(Json.Write(arguments).Length>65536)throw new InvalidOperationException("Arguments MCP trop volumineux.");
            using(var scope=Open(store,slug,bearer,instanceId)){
                var tools=await Inventory(scope,token).ConfigureAwait(false);var origin=Find(tools,widgetToolId);if(origin.Protocol==null)throw new InvalidOperationException("Cette interface n'est pas exportable.");
                var selected=tools.Where(t=>t.Server==origin.Server&&t.PluginId==origin.PluginId&&(t.Name==requestedTool||t.Name==origin.PluginId+"."+requestedTool||t.Name.EndsWith("."+requestedTool,StringComparison.Ordinal))).ToArray();
                if(selected.Length!=1)throw new InvalidOperationException("Outil absent du connecteur de cette interface.");var tool=selected[0];
                if(!tool.Connected)throw new InvalidOperationException("Ce connecteur n'est pas connecte ou autorise sur le compte de cette instance.");RequireReadOnly(tool.Name,tool.ReadOnly,tool.Id==origin.Id||tool.AppAccessible);ToolTunnel.CheckTransport(tool.Schema,arguments);scope.Verify();
                object result;try{result=await scope.Rpc.Call("mcpServer/tool/call",new{threadId=scope.Thread,server=tool.Server,tool=tool.Name,arguments=arguments},token).ConfigureAwait(false);}catch(HeadlessInteractionException){throw new TunnelInteractionException();}
                scope.Verify();return PublicResult(result);
            }
        }
        internal static void RequireReadOnly(string name,bool readOnly,bool accessible)
        {
            string baseName=(name??"").Split('.').Last();if(!readOnly||!accessible||Regex.IsMatch(baseName,"token|secret|credential|password|cookie|environment_variable|auth",RegexOptions.IgnoreCase))throw new InvalidOperationException("Cette action demande une modification, une autorisation ou un acces sensible. Effectuez-la dans Codex ; le portail n'accorde aucune approbation native.");
        }
        internal static object PublicResult(object raw)
        {
            // Authentication, account links and session internals are never sent
            // to an iframe. Structured business data belongs to the authorized
            // portal user; connector-specific hidden metadata stays in Codex.
            object value=Json.Get(raw,"result")??raw;
            return new{content=Json.Get(value,"content")??new object[0],structuredContent=Json.Get(value,"structuredContent"),isError=Object.Equals(Json.Get(value,"isError"),true)};
        }
        private static LiveTool Find(IEnumerable<LiveTool> tools,string id)
        {if(!Regex.IsMatch(id??"","^[a-f0-9]{64}$"))throw new InvalidOperationException("Interface MCP inconnue.");var tool=tools.SingleOrDefault(t=>t.Id==id);if(tool==null)throw new InvalidOperationException("Cet outil n'est plus disponible sur cette instance.");return tool;}
        private static string Clip(object value,int maximum){string text=Json.Str(value);return text.Length<=maximum?text:text.Substring(0,maximum);}
    }
}
