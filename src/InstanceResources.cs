using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class InstanceResource
    {
        public string Key {get;set;}
        public string Plugin {get;set;}
        public string Title {get;set;}
        public string Kind {get;set;}
        public string Field {get;set;}
        public string Value {get;set;}
        public string Access {get;set;}
        public bool CanModify {get;set;}
        public bool NativeEditor {get;set;}
    }
    public sealed class InstancePlugin
    {
        public string Key {get;set;}
        public string Name {get;set;}
        public string Server {get;set;}
        public int Tools {get;set;}
        public string State {get;set;}
        public string Message {get;set;}
    }
    public sealed class InstanceInventory
    {
        public string Instance {get;set;}
        public string Account {get;set;}
        public string Home {get;set;}
        public string Updated {get;set;}
        public List<InstancePlugin> Plugins {get;set;}
        public List<InstanceResource> Resources {get;set;}
        public InstanceInventory(){Plugins=new List<InstancePlugin>();Resources=new List<InstanceResource>();}
    }
    // Metadata-only catalog. Never runs a model, reads document bodies or grants access during discovery.
    internal sealed class InstanceResources
    {
        private readonly RelayStore store;
        private readonly ToolTunnel tunnel;
        internal InstanceResources(RelayStore store,ToolTunnel transport=null){this.store=store;tunnel=transport??new ToolTunnel(store);}
        internal static string PluginKey(TunnelTool tool){return ToolTunnel.Hash(tool.Server+"/"+(String.IsNullOrEmpty(tool.ConnectorId)?tool.Group:tool.ConnectorId));}
        internal static string ResourceKey(string plugin,string field,string value){return ToolTunnel.Hash(plugin+"/"+field+"/"+value);}
        internal static string CacheName(string instance){return "resource-catalog-"+ToolTunnel.Hash(instance).Substring(0,32)+".dpapi";}
        internal InstanceInventory Cached(string instance,string account,string home)
        {
            var data=store.ReadRecord<InstanceInventory>(CacheName(instance));
            return data.Instance==instance&&data.Account==account&&RelayStore.SamePath(data.Home,home)?data:new InstanceInventory{Instance=instance,Account=account,Home=home};
        }
        internal static bool Fresh(InstanceInventory inventory)
        {DateTime time;return DateTime.TryParse(inventory.Updated,out time)&&DateTime.UtcNow-time.ToUniversalTime()<TimeSpan.FromMinutes(10);}
        internal static object Value(object response)
        {
            if(Object.Equals(Json.Get(response,"isError"),true))throw new InvalidOperationException("Le fournisseur a refusé l'inventaire. Vérifiez sa connexion dans Codex.");
            object data=Json.Get(response,"structuredContent");
            if(data==null)foreach(var part in RelayEngine.Rows(Json.Get(response,"content")))if(Json.Str(Json.Get(part,"type"))=="text")try{data=Json.Read<object>(Json.Str(Json.Get(part,"text")));break;}catch(ArgumentException){}
            if(data==null||Json.Get(data,"error")!=null)throw new InvalidOperationException("Le fournisseur n'a pas renvoyé d'inventaire exploitable.");return data;
        }
        internal async Task<InstanceInventory> Discover(string instance,CancellationToken token)
        {
            var owner=tunnel.Owner(instance);var data=new InstanceInventory{Instance=owner.Id,Account=owner.Account,Home=owner.Home,Updated=DateTime.UtcNow.ToString("o")};
            using(var connection=tunnel.Connection(owner)){
                var tools=await connection.Inventory(token).ConfigureAwait(false);
                foreach(var group in tools.GroupBy(PluginKey).OrderBy(g=>g.First().Group)){
                    var first=group.First();var plugin=new InstancePlugin{Key=group.Key,Name=first.Group,Server=first.Server,Tools=group.Count(),State="unsupported",Message="Ce plugin expose des outils, mais aucun catalogue de ressources pris en charge. Configurez ses opérations dans les réglages avancés."};data.Plugins.Add(plugin);
                    var list=group.FirstOrDefault(t=>t.Server=="codex_apps"&&t.ReadOnly&&(t.Name=="sites.list_sites"||t.Name=="chatgpt_space.list_pages"));
                    if(list==null)continue;
                    plugin.State="ready";plugin.Message="Inventaire récupéré auprès du compte.";
                    try{
                        bool sites=list.Name=="sites.list_sites";
                        foreach(string role in sites?new[]{"owner","editor"}:new[]{""}){
                            var seen=new HashSet<string>();string cursor=null;
                            for(int page=0;page<50;page++){
                                token.ThrowIfCancellationRequested();var args=new Dictionary<string,object>{{"limit",sites?50:100}};if(sites)args["role"]=role;if(cursor!=null)args["cursor"]=cursor;
                                var response=Value(await connection.Invoke(list.Server,list.Name,args,token).ConfigureAwait(false));
                                if(!(Json.Get(response,"items") is System.Collections.IEnumerable))throw new InvalidOperationException("La structure du catalogue a changé. Aucun partage n'a été modifié.");
                                foreach(var item in RelayEngine.Rows(Json.Get(response,"items"))){
                                    string id=Json.Str(Json.Get(item,sites?"id":"page_id"));if(String.IsNullOrWhiteSpace(id)||id.Length>512)continue;
                                    if(!sites&&Object.Equals(Json.Get(Json.Get(item,"access"),"can_read"),false))continue;
                                    string field=sites?"project_id":"page_id",key=ResourceKey(plugin.Key,field,id);
                                    if(data.Resources.Any(r=>r.Key==key))continue;
                                    string title=Json.Str(Json.Get(item,"title")),type=Json.Str(Json.Get(item,"document_type"));
                                    data.Resources.Add(new InstanceResource{Key=key,Plugin=plugin.Key,Title=String.IsNullOrWhiteSpace(title)?(sites?"Site sans titre":"Page sans titre"):title.Substring(0,Math.Min(title.Length,240)),Kind=sites?"Site":String.IsNullOrEmpty(type)?"Page":type=="granola_workbook"?"Tableur":type=="granola_presentation"?"Présentation":type=="granola_document"?"Document":"Document natif",Field=field,Value=id,Access=sites?(role=="owner"?"Propriétaire":"Éditeur"):Object.Equals(Json.Get(Json.Get(item,"access"),"can_write"),true)?"Modification autorisée":"Lecture",CanModify=sites||Object.Equals(Json.Get(Json.Get(item,"access"),"can_write"),true),NativeEditor=!String.IsNullOrEmpty(type)||Json.Get(item,"site_project_id")!=null});
                                    if(data.Resources.Count>=5000){plugin.State="partial";plugin.Message="Inventaire limité à 5 000 ressources. Affinez depuis le fournisseur.";break;}
                                }
                                if(Object.Equals(Json.Get(response,"partial_results"),true)){plugin.State="partial";plugin.Message="Le fournisseur signale un inventaire partiel.";}
                                cursor=Json.Str(Json.Get(response,sites?"cursor":"next_cursor"));if(cursor.Length==0||data.Resources.Count>=5000)break;
                                if(!seen.Add(cursor)||page==49){plugin.State="partial";plugin.Message="Pagination interrompue : inventaire partiel.";break;}
                            }
                            if(data.Resources.Count>=5000)break;
                        }
                    }catch(OperationCanceledException){throw;}catch(Exception e){plugin.State="error";plugin.Message=Program.SafeError(e);}
                }
            }
            ToolTunnel.CheckOwner(owner,tunnel.Owner(instance));
            if(Encoding.UTF8.GetByteCount(Json.Write(data))>3000000)throw new InvalidOperationException("Inventaire trop volumineux. Le dernier catalogue est conservé.");
            store.WriteRecord(CacheName(instance),data);return data;
        }
        internal static bool Managed(TunnelGrant g,string instance,string client)
        {return g.Instance==instance&&g.CatalogClient==client;}
        internal string Revision(string instance,string client)
        {return ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants().Where(g=>Managed(g,instance,client)).OrderBy(g=>g.Id).ToArray())));}
        internal static string Mode(TunnelGrant g){return g.Tools.Any(t=>!t.ReadOnly)?"edit":"read";}
        internal Dictionary<string,string> Selections(InstanceInventory inventory,string client)
        {
            var selected=new Dictionary<string,string>();
            foreach(var g in tunnel.Grants().Where(g=>Managed(g,inventory.Instance,client)&&g.Enabled&&g.Account==inventory.Account&&RelayStore.SamePath(g.Home,inventory.Home)))
                foreach(var t in g.Tools.Take(1))foreach(string value in g.ResourceValues??new List<string>{g.ResourceValue})selected[ResourceKey(PluginKey(t),g.ResourceField,value)]=Mode(g);
            return selected;
        }
        internal static bool Compatible(TunnelTool tool,InstanceResource resource,string mode)
        {
            if(PluginKey(tool)!=resource.Plugin||ToolTunnel.Limitation(tool).Length>0||!Json.Obj(Json.Get(tool.Schema,"properties")).ContainsKey(resource.Field))return false;
            if(tool.ReadOnly)return true;
            // Basic "modify" never includes deleting, changing access, credentials or deployment.
            return mode=="edit"&&resource.CanModify&&new[]{"chatgpt_space.edit_page","sites.update_site_metadata"}.Contains(tool.Name)&&tool.Server=="codex_apps";
        }
        internal async Task Save(InstanceInventory inventory,string client,string clientHome,string clientAccount,Dictionary<string,string> selected,string expected,CancellationToken token)
        {
            var replacements=new List<TunnelGrant>();
            if(selected.Count>0){
                if(!Fresh(inventory))throw new InvalidOperationException("Actualisez les ressources avant d'enregistrer des accès.");
                var owner=tunnel.Owner(inventory.Instance);if(owner.Account!=inventory.Account||!RelayStore.SamePath(owner.Home,inventory.Home))throw new InvalidOperationException("Le compte du catalogue a changé.");
                using(var connection=tunnel.Connection(owner)){
                    var tools=await connection.Inventory(token).ConfigureAwait(false);
                    var resources=selected.Select(p=>new{Resource=inventory.Resources.SingleOrDefault(r=>r.Key==p.Key),Mode=p.Value}).ToArray();
                    if(resources.Any(x=>x.Resource==null||x.Resource.NativeEditor||!new[]{"read","edit"}.Contains(x.Mode)||(x.Mode=="edit"&&!x.Resource.CanModify)))throw new InvalidOperationException("Une ressource ou un droit sélectionné n'est plus disponible. Actualisez le catalogue.");
                    foreach(var group in resources.GroupBy(x=>x.Resource.Plugin+"/"+x.Resource.Field+"/"+x.Mode)){
                        var item=group.First();var allowed=tools.Where(t=>Compatible(t,item.Resource,item.Mode)).ToList();
                        if(allowed.Count==0||(item.Mode=="edit"&&!allowed.Any(t=>!t.ReadOnly)))throw new InvalidOperationException("Les outils correspondant à ces accès ne sont plus disponibles.");
                        var grant=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Name=(item.Resource.Kind+" · "+(item.Mode=="edit"?"lecture et modification":"lecture")+" via "+owner.Name),Instance=owner.Id,CatalogClient=client,Enabled=true,Sources=new Dictionary<string,string>{{clientHome,clientAccount}},Tools=allowed,ResourceField=item.Resource.Field,ResourceValues=group.Select(x=>x.Resource.Value).ToList()};
                        grant.ResourceLabels=group.ToDictionary(x=>x.Resource.Value,x=>x.Resource.Title);
                        tunnel.PrepareGrant(grant);replacements.Add(grant);
                    }
                }
                ToolTunnel.CheckOwner(owner,tunnel.Owner(inventory.Instance));
            }
            token.ThrowIfCancellationRequested();
            using(store.Lease("tool-grants")){
                if(Revision(inventory.Instance,client)!=expected)throw new InvalidOperationException("Les accès ont été modifiés ailleurs. Rechargez avant d'enregistrer.");
                var all=tunnel.Grants();all.RemoveAll(g=>Managed(g,inventory.Instance,client));all.AddRange(replacements);tunnel.WriteGrants(all);
            }
        }
    }
}
