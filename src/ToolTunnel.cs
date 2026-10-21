using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class TunnelTool
    {
        public string Server {get;set;}
        public string Name {get;set;}
        public string Title {get;set;}
        public string Group {get;set;}
        public string ConnectorId {get;set;}
        public object Annotations {get;set;}
        public string Description {get;set;}
        public object Schema {get;set;}
        public bool ReadOnly {get;set;}
        public string Signature {get;set;}
    }
    public sealed class TunnelGrant
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Instance {get;set;}
        public string InstanceName {get;set;}
        public string Account {get;set;}
        public string Home {get;set;}
        public string Revision {get;set;}
        public bool Enabled {get;set;}
        public Dictionary<string,string> Sources {get;set;}
        public List<TunnelTool> Tools {get;set;}
        public string ResourceField {get;set;}
        public string ResourceValue {get;set;}
        public List<string> ResourceValues {get;set;}
        public Dictionary<string,string> ResourceLabels {get;set;}
        public string CatalogClient {get;set;}
        public string CatalogPlugin {get;set;}
        public TunnelGrant(){Sources=new Dictionary<string,string>();Tools=new List<TunnelTool>();}
    }
    public sealed class TunnelCall
    {
        public string Id {get;set;}
        public string Grant {get;set;}
        public string GrantName {get;set;}
        public string Instance {get;set;}
        public string Revision {get;set;}
        public string SourceThread {get;set;}
        public string SourceHome {get;set;}
        public string SourceAccount {get;set;}
        public string Fingerprint {get;set;}
        public string Tool {get;set;}
        public string State {get;set;}
        public string Error {get;set;}
        public string Created {get;set;}
        public string Updated {get;set;}
        public object Result {get;set;}
        public bool HasResult {get;set;}
        public bool SensitiveResult {get;set;}
        public string ArchiveHash {get;set;}
        public long ArchiveBytes {get;set;}
        public string ContractStatus {get;set;}
        public string ContractReason {get;set;}
    }
    internal sealed class TunnelOwner
    {
        public string Id,Name,Home,Account,Executable;
    }
    internal sealed class ToolTunnel
    {
        private readonly RelayStore store;
        private readonly Func<string,TunnelOwner> resolveOwner;
        private readonly Func<TunnelOwner,IToolTunnelConnection> connect;
        private readonly Action<RelaySession> verifySource;
        private readonly Func<TunnelOwner[]> listOwners;
        public ToolTunnel(RelayStore data,Func<string,TunnelOwner> resolver=null,Func<TunnelOwner,IToolTunnelConnection> connector=null,Action<RelaySession> verifier=null,Func<TunnelOwner[]> owners=null)
        {store=data;resolveOwner=resolver??ResolveOwner;connect=connector??(o=>new ToolTunnelRpc(o.Executable,o.Home,o.Account,store));verifySource=verifier??VerifySource;listOwners=owners??ListOwners;}
        internal static bool AllowedServer(string name)
        {return !String.IsNullOrWhiteSpace(name)&&!new[]{"creezio-relay","codex_app","cua_repl","node_repl","code-review"}.Contains(name);}
        private TunnelOwner ResolveOwner(string id)
        {
            var a=new AccountService(Path.GetDirectoryName(store.Root));var i=NamedInstances.Resolve(a,id);NamedInstances.VerifyAccount(a,i);
            if(i.HeadlessEnabled){HeadlessAccounts.Read(a,i,false,CancellationToken.None);return new TunnelOwner{Id=i.Id,Name=i.Name,Home=a.Instances.Home(i),Account=i.AccountKey,Executable=a.Settings.CodexExecutable};}
            string home=a.Instances.Home(i),pipe=null;int pid=0;long ticks=0;
            if(!i.IsLocal){var runtime=a.Instances.Runtime.Probe(i);if(runtime.Running){pipe=runtime.AppToolsPipe;pid=runtime.DesktopPid;ticks=runtime.DesktopStartTicks;}}
            else{
                var c=store.Channels().FirstOrDefault(x=>x.Enabled&&RelayStore.SamePath(x.Home,home)&&x.AccountKey==i.AccountKey&&DesktopRuntime.SameProcess(x.ServerPid,x.ServerStartTicks));
                if(c!=null){pipe=c.PipePath;pid=c.ServerPid;ticks=c.ServerStartTicks;}
                else if(RelayStore.SamePath(home,CodexEnvironment.DefaultHome()))pipe=Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");
            }
            if(String.IsNullOrEmpty(pipe))throw new InvalidOperationException("Ouvrez l'instance « "+i.Name+" ». Pour la session habituelle, connectez une fois le plugin depuis son chat.");
            using(var client=new AppToolsClient(pipe,pid,ticks)){}
            return new TunnelOwner{Id=i.Id,Name=i.Name,Home=home,Account=i.AccountKey,Executable=a.Settings.CodexExecutable};
        }
        private void VerifySource(RelaySession session)
        {
            var c=store.Channel(session.Channel);if(!c.Enabled||c.AccountKey!=session.Account||!RelayStore.SamePath(c.Home,session.Home))throw new InvalidOperationException("La connexion du chat source a changé.");
            var a=new AccountService(Path.GetDirectoryName(store.Root));var instance=a.Data.Instances.SingleOrDefault(i=>!i.Archived&&RelayStore.SamePath(a.Instances.Home(i),session.Home));
            if(instance==null||instance.AccountKey!=session.Account)throw new InvalidOperationException("Compte source non reconnu.");
            NamedInstances.VerifyAccount(a,instance);AgentProviders.Create(store).Verify(c);
        }
        private TunnelOwner[] ListOwners()
        {
            // Metadata only: listing shares must not start a GUI, app-server or
            // one connection per account. Native inventory is fetched on demand.
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));
            return accounts.Data.Instances.Where(i=>!i.Archived&&!String.IsNullOrWhiteSpace(i.AccountKey))
                .Select(i=>new TunnelOwner{Id=i.Id,Name=i.Name,Home=accounts.Instances.Home(i),Account=i.AccountKey}).ToArray();
        }
        internal static bool GlobalGrantId(string id){return id!=null&&id.StartsWith("global:",StringComparison.Ordinal);}
        private static string GlobalId(TunnelOwner owner,ToolAccessPolicyState policy)
        {
            string home=Path.GetFullPath(owner.Home).TrimEnd('\\','/').ToUpperInvariant();
            // The desktop (.NET) and worker (Framework) serialize Unicode
            // differently. Hash raw framed identity text so both see one ID.
            return "global:"+owner.Id+":"+Webhooks.Hash("global-tool-owner\n"+owner.Account.Length+":"+owner.Account+home).Substring(0,32)+":"+policy.Revision;
        }
        private TunnelGrant[] GlobalGrants(RelaySession session,ToolAccessPolicyState policy)
        {
            if(!policy.Enabled)return new TunnelGrant[0];
            return listOwners().Where(o=>o!=null&&!String.IsNullOrEmpty(o.Id)&&!String.IsNullOrEmpty(o.Account)&&!String.IsNullOrEmpty(o.Home)&&!RelayStore.SamePath(o.Home,session.Home))
                .Select(o=>new TunnelGrant{Id=GlobalId(o,policy),Name="Accès complet des ressources · "+o.Name,Instance=o.Id,InstanceName=o.Name,Home=o.Home,Account=o.Account,Enabled=true,Revision=policy.Revision,
                    Sources=new Dictionary<string,string>{{session.Home,session.Account}}}).ToArray();
        }
        internal static string Canonical(object value)
        {
            var obj=value as IDictionary<string,object>;
            if(obj!=null)return "{"+String.Join(",",obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>Json.Write(p.Key)+":"+Canonical(p.Value)))+"}";
            var rows=value as IEnumerable;if(rows!=null&&!(value is string))return "["+String.Join(",",rows.Cast<object>().Select(Canonical))+"]";
            return Json.Write(value);
        }
        internal static string Hash(object value)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(value)))).Replace("-","").ToLowerInvariant();}
        internal static string Signature(TunnelTool tool)
        {return Hash(Json.Read<object>(Json.Write(new{tool.Server,tool.Name,tool.Description,tool.Schema,tool.ReadOnly,tool.ConnectorId,tool.Annotations})));}
        internal static bool SameContract(TunnelTool saved,TunnelTool live)
        {
            // The immutable DPAPI snapshot is the authorization contract. .NET 10 and
            // Framework escape JSON strings differently, so a saved serializer hash
            // cannot be compared to a live hash produced by the other runtime.
            return ContractDifference(saved,live,null)==null;
        }
        private static bool Presentation(string key){return new[]{"description","title","$comment","examples"}.Contains(key);}
        private static string ContractPath(string value){return WebhookChats.Safe(value,180);}
        // Documentation is not a permission. Provider identity, risk hints and
        // executable constraints stay fixed. New optional inputs without defaults
        // may coexist, but the immutable old grant does not authorize their use.
        internal static string ContractDifference(TunnelTool saved,TunnelTool live,object arguments)
        {
            if(saved==null||live==null)return "outil absent du catalogue du compte proprietaire";
            if(saved.Server!=live.Server||saved.Name!=live.Name||String.IsNullOrEmpty(saved.Name))return "identite de l'outil ou serveur modifie";
            if(saved.ConnectorId!=live.ConnectorId)return "connecteur proprietaire modifie";
            if(saved.Server=="codex_apps"&&String.IsNullOrWhiteSpace(saved.ConnectorId)&&Signature(saved)!=Signature(live))return "identite du connecteur absente : compatibilite automatique non verifiable";
            if(saved.ReadOnly!=live.ReadOnly)return "niveau lecture/ecriture modifie";
            var oldAnnotations=Json.Obj(Json.Read<object>(Json.Write(saved.Annotations))).Where(p=>p.Key!="title"&&p.Key!="description").ToDictionary(p=>p.Key,p=>p.Value);
            var newAnnotations=Json.Obj(Json.Read<object>(Json.Write(live.Annotations))).Where(p=>p.Key!="title"&&p.Key!="description").ToDictionary(p=>p.Key,p=>p.Value);
            if(Canonical(oldAnnotations)!=Canonical(newAnnotations))return "annotations de risque modifiees";
            return SchemaDifference(Json.Read<object>(Json.Write(saved.Schema)),Json.Read<object>(Json.Write(live.Schema)),"arguments",arguments==null?null:Json.Read<object>(Json.Write(arguments)),0);
        }
        private static string[] SchemaNames(object value)
        {var rows=value as IEnumerable;if(rows==null||value is string||value is IDictionary)return value==null?new string[0]:null;var names=rows.Cast<object>().Select(v=>v as string).ToArray();return names.Any(n=>n==null)||names.Distinct(StringComparer.Ordinal).Count()!=names.Length?null:names.OrderBy(n=>n,StringComparer.Ordinal).ToArray();}
        private static bool HasDefault(object value,int depth)
        {if(depth>32)return true;var obj=value as IDictionary<string,object>;if(obj!=null)return obj.ContainsKey("default")||obj.Values.Any(v=>HasDefault(v,depth+1));var rows=value as IEnumerable;return rows!=null&&!(value is string)&&rows.Cast<object>().Any(v=>HasDefault(v,depth+1));}
        private static string SchemaDifference(object saved,object live,string path,object arguments,int depth)
        {
            if(depth>32)return "schema trop imbrique pour une compatibilite automatique";
            if(Canonical(saved)==Canonical(live))return null;
            var before=saved as IDictionary<string,object>;var after=live as IDictionary<string,object>;
            if(before!=null&&after!=null){
                foreach(string key in before.Keys.Union(after.Keys).OrderBy(k=>k,StringComparer.Ordinal)){
                    if(Presentation(key))continue;object a,b;before.TryGetValue(key,out a);after.TryGetValue(key,out b);
                    if(key=="required"){var left=SchemaNames(a);var right=SchemaNames(b);if(left==null||right==null||!left.SequenceEqual(right))return "parametres obligatoires modifies a "+ContractPath(path);continue;}
                    if(key=="enum"){var left=a as IEnumerable;var right=b as IEnumerable;if(left==null||right==null||a is string||b is string||a is IDictionary||b is IDictionary||!left.Cast<object>().Select(Canonical).OrderBy(v=>v,StringComparer.Ordinal).SequenceEqual(right.Cast<object>().Select(Canonical).OrderBy(v=>v,StringComparer.Ordinal)))return "valeurs enumerees modifiees a "+ContractPath(path);continue;}
                    if(key=="properties"){
                        if(before.ContainsKey(key)&&!(a is IDictionary<string,object>)||after.ContainsKey(key)&&!(b is IDictionary<string,object>))return "proprietes du schema invalides a "+ContractPath(path);
                        var oldProperties=Json.Obj(a);var newProperties=Json.Obj(b);var required=SchemaNames(Json.Get(live,"required"));if(required==null)return "liste de parametres obligatoires invalide";
                        foreach(string name in oldProperties.Keys.Union(newProperties.Keys).OrderBy(k=>k,StringComparer.Ordinal)){
                            object oldProperty,newProperty;if(!newProperties.TryGetValue(name,out newProperty))return "parametre supprime : "+ContractPath(path+"."+name);
                            if(!oldProperties.TryGetValue(name,out oldProperty)){
                                if(!(newProperty is IDictionary<string,object>)&&!(newProperty is bool))return "definition de parametre invalide : "+ContractPath(path+"."+name);
                                if(path.IndexOf("/$defs/",StringComparison.Ordinal)>=0||path.IndexOf("/definitions/",StringComparison.Ordinal)>=0)return "definition referencee modifiee : "+ContractPath(path+"."+name);
                                if(required.Contains(name)||HasDefault(newProperty,depth+1))return "nouveau parametre obligatoire ou avec valeur par defaut : "+ContractPath(path+"."+name);
                                if(Json.Obj(arguments).ContainsKey(name))return "nouveau parametre non autorise par le partage existant : "+ContractPath(path+"."+name);
                                continue;
                            }
                            string changed=SchemaDifference(oldProperty,newProperty,path+"."+name,Json.Get(arguments,name),depth+1);if(changed!=null)return changed;
                        }continue;
                    }
                    // Definition keys are user-defined names, not documentation.
                    if(key=="$defs"||key=="definitions"){
                        if(before.ContainsKey(key)&&!(a is IDictionary<string,object>)||after.ContainsKey(key)&&!(b is IDictionary<string,object>))return "definitions du schema invalides";
                        var oldDefinitions=Json.Obj(a);var newDefinitions=Json.Obj(b);if(!oldDefinitions.Keys.OrderBy(k=>k,StringComparer.Ordinal).SequenceEqual(newDefinitions.Keys.OrderBy(k=>k,StringComparer.Ordinal)))return "definitions de schema modifiees";
                        foreach(string name in oldDefinitions.Keys){string changed=SchemaDifference(oldDefinitions[name],newDefinitions[name],path+"/"+key+"/"+name,null,depth+1);if(changed!=null)return changed;}continue;
                    }
                    if(key=="items"){
                        string changed=SchemaDifference(a,b,path+"[]",null,depth+1);if(changed!=null)return changed;
                        var values=arguments as IEnumerable;if(values!=null&&!(arguments is string)&&!(arguments is IDictionary))foreach(var value in values){changed=SchemaDifference(a,b,path+"[]",value,depth+1);if(changed!=null)return changed;}continue;
                    }
                    var oldRows=a as IEnumerable;var newRows=b as IEnumerable;
                    if(new[]{"anyOf","oneOf","allOf","prefixItems"}.Contains(key)&&oldRows!=null&&newRows!=null&&!(a is string)&&!(b is string)&&!(a is IDictionary)&&!(b is IDictionary)){
                        var left=oldRows.Cast<object>().ToArray();var right=newRows.Cast<object>().ToArray();if(left.Length!=right.Length)return "structure de schema modifiee a "+ContractPath(path+"."+key);
                        for(int i=0;i<left.Length;i++){string changed=SchemaDifference(left[i],right[i],path+"."+key+"["+i+"]",arguments,depth+1);if(changed!=null)return changed;}continue;
                    }
                    // Only known schema positions contain schema annotations.
                    // const/default/unknown keywords are literal data: a property
                    // named description inside such a value is executable data.
                    if(new[]{"additionalProperties","additionalItems","contains","propertyNames","not","if","then","else","unevaluatedProperties","unevaluatedItems"}.Contains(key)){
                        string difference=SchemaDifference(a,b,path+"."+key,arguments,depth+1);if(difference!=null)return difference;
                    }else if(Canonical(a)!=Canonical(b))return "contrainte de schema modifiee a "+ContractPath(path+"."+key);
                }return null;
            }
            return "contrainte de schema modifiee a "+ContractPath(path);
        }
        private static void CheckArgumentSurface(object schema,object arguments,string path)
        {
            if(Json.Str(Json.Get(schema,"type"))=="object"&&arguments is IDictionary<string,object>){
                var values=Json.Obj(arguments);var properties=Json.Obj(Json.Get(schema,"properties"));var required=SchemaNames(Json.Get(schema,"required"));if(required==null)throw new InvalidOperationException("Schema de parametres obligatoires invalide.");
                foreach(string name in required)if(!values.ContainsKey(name))throw new InvalidOperationException("Parametre autorise manquant : "+ContractPath(path+"."+name));
                foreach(var pair in values){object property;if(properties.TryGetValue(pair.Key,out property))CheckArgumentSurface(property,pair.Value,path+"."+pair.Key);else if(Object.Equals(Json.Get(schema,"additionalProperties"),false))throw new InvalidOperationException("Parametre absent du contrat autorise : "+ContractPath(path+"."+pair.Key));}
            }
            var rows=arguments as IEnumerable;if(rows!=null&&!(arguments is string)&&!(arguments is IDictionary))foreach(var row in rows)CheckArgumentSurface(Json.Get(schema,"items"),row,path+"[]");
        }
        internal static string Limitation(TunnelTool tool)
        {
            if(IsSitesSave(tool))return SupportsSitesRemoteBuild(tool)?"Archive Sites .tar.gz transférée avec le compte propriétaire ; Accès complet du chat appelant requis. Le repli de construction distante accepte seulement project_id et commit_sha, sans champ archive, lorsque le packaging local ne peut réellement pas aboutir. Préservez la preuve de cet échec et le SHA poussé exact ; ne simulez pas un échec pour omettre l'archive.":"Cette définition Sites ne confirme pas le contrat de publication pris en charge ; aucun fichier local n'est transféré.";
            return "";
        }
        private static bool IsSitesSave(TunnelTool tool)
        {return tool!=null&&(tool.Name=="sites.save_site_version"||tool.Name=="sites.save_version_and_deploy_private");}
        internal static bool SupportsSitesRemoteBuild(TunnelTool tool)
        {
            if(tool.Server!="codex_apps"||tool.ReadOnly||Object.Equals(Json.Get(tool.Annotations,"readOnlyHint"),true)||Json.Str(Json.Get(tool.Schema,"type"))!="object")return false;
            var properties=Json.Obj(Json.Get(tool.Schema,"properties"));object project,commit,archive;
            if(!properties.TryGetValue("project_id",out project)||Json.Str(Json.Get(project,"type"))!="string"||!properties.TryGetValue("commit_sha",out commit)||Json.Str(Json.Get(commit,"type"))!="string"||!properties.TryGetValue("archive",out archive)||Json.Str(Json.Get(archive,"type"))!="string")return false;
            var requiredValue=Json.Get(tool.Schema,"required");var required=requiredValue as IEnumerable;
            if(required==null||requiredValue is string||requiredValue is IDictionary)return false;
            var names=required.Cast<object>().Select(value=>value as string).ToArray();
            if(names.Length!=2||names.Any(String.IsNullOrEmpty)||names.Distinct(StringComparer.Ordinal).Count()!=2||!names.Contains("project_id")||!names.Contains("commit_sha"))return false;
            // These native contract words describe the supported capability,
            // not permission to invent a packaging failure or widen a grant.
            string description=(tool.Description??"")+"\n"+Json.Str(Json.Get(archive,"description"));
            return description.IndexOf("omit it only when local packaging cannot complete",StringComparison.OrdinalIgnoreCase)>=0&&description.IndexOf("remote build fallback",StringComparison.OrdinalIgnoreCase)>=0;
        }
        private static void CheckSitesTransport(TunnelTool tool,object args)
        {
            if(!IsSitesSave(tool))return;
            if(!SupportsSitesRemoteBuild(tool))throw new InvalidOperationException("Le contrat autorisé de cet outil ne confirme pas un repli Sites sans archive. Aucun appel envoyé.");
            var values=args as IDictionary<string,object>;
            // Null, an empty string, a remote URL or a made-up file reference
            // must not turn an explicitly supplied archive into an omission.
            if(values==null||values.ContainsKey("archive"))throw new InvalidOperationException("Le tunnel ne transfère aucune archive locale. Le repli documenté exige l'absence complète du champ archive, jamais une valeur nulle ou un chemin.");
            object project,commit;
            if(values.Count!=2||!values.TryGetValue("project_id",out project)||!(project is string)||String.IsNullOrWhiteSpace((string)project)||!values.TryGetValue("commit_sha",out commit)||!(commit is string)||!System.Text.RegularExpressions.Regex.IsMatch((string)commit,"^[0-9a-f]{40}$"))throw new InvalidOperationException("Le repli Sites exige seulement le project_id autorisé et le SHA complet exact du commit poussé. Aucun appel envoyé.");
        }
        internal static void CheckTransport(object schema,object args)
        {
            if(args==null)return;
            string description=Json.Str(Json.Get(schema,"description"));
            if(description.IndexOf("absolute local file path",StringComparison.OrdinalIgnoreCase)>=0||Json.Str(Json.Get(schema,"format"))=="binary")throw new InvalidOperationException("Cet argument nécessite un transfert de fichier natif, indisponible dans le tunnel. Effectuez cette opération dans Codex.");
            foreach(var pair in Json.Obj(args)){var property=Json.Get(Json.Get(schema,"properties"),pair.Key);if(property!=null)CheckTransport(property,pair.Value);}
            if(args is IEnumerable&&!(args is string)&&!(args is IDictionary<string,object>))foreach(var item in (IEnumerable)args)CheckTransport(Json.Get(schema,"items"),item);
            foreach(string choice in new[]{"anyOf","oneOf","allOf"})foreach(var variant in RelayEngine.Rows(Json.Get(schema,choice)))CheckTransport(variant,args);
        }
        internal static void Bound(string value,int max,string name)
        {if(String.IsNullOrWhiteSpace(value)||value.Length>max)throw new InvalidOperationException(name+" absent ou trop long.");}
        public List<TunnelGrant> Grants(){return store.ReadRecord<List<TunnelGrant>>("tool-grants.dpapi");}
        public async Task<TunnelTool[]> Discover(string instance,CancellationToken token)
        {
            var owner=resolveOwner(instance);
            using(var connection=connect(owner)){
                var tools=await connection.Inventory(token).ConfigureAwait(false);CheckOwner(owner,resolveOwner(owner.Id));
                foreach(var t in tools)t.Signature=Signature(t);return tools;
            }
        }
        internal TunnelOwner Owner(string instance){return resolveOwner(instance);}
        internal IToolTunnelConnection Connection(TunnelOwner owner){return connect(owner);}
        internal static void CheckOwner(TunnelOwner expected,TunnelOwner actual)
        {if(expected.Id!=actual.Id||expected.Account!=actual.Account||!RelayStore.SamePath(expected.Home,actual.Home))throw new InvalidOperationException("L'identité de l'instance propriétaire a changé.");}
        public void SaveGrant(TunnelGrant grant)
        {
            PrepareGrant(grant);
            using(store.Lease("tool-grants")){var grants=Grants();grants.RemoveAll(g=>g.Id==grant.Id);grants.Add(grant);WriteGrants(grants);}
        }
        internal void PrepareGrant(TunnelGrant grant)
        {
            RelayStore.MessageId(grant.Id);Bound(grant.Name,120,"Nom du partage");
            var owner=resolveOwner(grant.Instance);grant.Instance=owner.Id;grant.InstanceName=owner.Name;grant.Account=owner.Account;grant.Home=owner.Home;
            if(grant.Sources==null||grant.Sources.Count==0||grant.Sources.Count>100)throw new InvalidOperationException("Choisissez au moins une instance cliente.");
            if(grant.Tools==null||grant.Tools.Count==0||grant.Tools.Count>300)throw new InvalidOperationException("Choisissez entre 1 et 300 outils.");
            if(grant.Tools.GroupBy(t=>t.Server+"/"+t.Name).Any(g=>g.Count()>1))throw new InvalidOperationException("Outil sélectionné en double.");
            foreach(var t in grant.Tools){Bound(t.Name,200,"Outil");if(!AllowedServer(t.Server)||t.Schema==null)throw new InvalidOperationException("Outil non exportable.");t.Signature=Signature(t);}
            grant.ResourceField=(grant.ResourceField??"").Trim();grant.ResourceValue=(grant.ResourceValue??"").Trim();
            if(grant.ResourceField.Length>0){
                Bound(grant.ResourceField,100,"Champ de ressource");
                if(grant.ResourceValues!=null&&grant.ResourceValues.Count>0){if(grant.ResourceValues.Count>10000)throw new InvalidOperationException("Trop de ressources dans ce partage.");foreach(string value in grant.ResourceValues)Bound(value,512,"Identifiant de ressource");grant.ResourceValues=grant.ResourceValues.Distinct(StringComparer.Ordinal).OrderBy(v=>v,StringComparer.Ordinal).ToList();}
                else Bound(grant.ResourceValue,512,"Identifiant de ressource");
                if(grant.Tools.Any(t=>!Json.Obj(Json.Get(t.Schema,"properties")).ContainsKey(grant.ResourceField)))throw new InvalidOperationException("Certains outils ne prennent pas le champ de ressource choisi. Retirez-les pour conserver cette restriction.");
            }else if(grant.ResourceValue.Length>0||(grant.ResourceValues!=null&&grant.ResourceValues.Count>0))throw new InvalidOperationException("Indiquez le champ de ressource.");
            foreach(var source in grant.Sources){Bound(source.Key,1024,"Profil client");Bound(source.Value,300,"Compte client");if(RelayStore.SamePath(source.Key,owner.Home))throw new InvalidOperationException("Choisissez une instance cliente différente du propriétaire.");}
            if(grant.ResourceLabels!=null)foreach(var label in grant.ResourceLabels){if(grant.ResourceValues==null||!grant.ResourceValues.Contains(label.Key))throw new InvalidOperationException("Libellé sans ressource autorisée.");Bound(label.Value,240,"Nom de ressource");}
            grant.Revision=Guid.NewGuid().ToString("N");
            if(Encoding.UTF8.GetByteCount(Json.Write(grant))>3000000)throw new InvalidOperationException("Partage trop volumineux. Sélectionnez moins d'outils.");
        }
        internal void WriteGrants(List<TunnelGrant> grants)
        {if(grants.Count>100||Encoding.UTF8.GetByteCount(Json.Write(grants))>3500000)throw new InvalidOperationException("La configuration des partages est trop volumineuse. Réduisez les outils sélectionnés.");store.WriteRecord("tool-grants.dpapi",grants);}
        public void Disable(string id)
        {using(store.Lease("tool-grants")){var all=Grants();var g=all.Single(x=>x.Id==id);g.Enabled=false;g.Revision=Guid.NewGuid().ToString("N");store.WriteRecord("tool-grants.dpapi",all);}}
        internal static bool CanUse(TunnelGrant g,RelaySession s)
        {return g.Enabled&&g.Sources!=null&&g.Sources.Any(p=>RelayStore.SamePath(p.Key,s.Home)&&p.Value==s.Account);}
        internal TunnelGrant Authorize(RelaySession session,string id)
        {
            verifySource(session);
            var policy=ToolAccessPolicy.Read(store);
            var g=GlobalGrantId(id)?GlobalGrants(session,policy).SingleOrDefault(x=>x.Id==id):Grants().SingleOrDefault(x=>x.Id==id);
            // Global mode replaces authorization by individual shares. An old
            // client may still send a disabled/read-only/resource-scoped ID:
            // use it only to identify the owner, never as an access restriction.
            if(policy.Enabled&&g!=null&&!GlobalGrantId(id))g=GlobalGrants(session,policy).SingleOrDefault(x=>x.Instance==g.Instance);
            if(g==null||!CanUse(g,session))throw new InvalidOperationException("Ce partage d'outils est absent, désactivé ou interdit à cette instance.");return g;
        }
        internal TunnelGrant[] EffectiveGrants(RelaySession session)
        {verifySource(session);var policy=ToolAccessPolicy.Read(store);return policy.Enabled?GlobalGrants(session,policy):Grants().Where(g=>CanUse(g,session)).ToArray();}
        internal string CurrentName(TunnelGrant grant)
        {
            var instance=new Vault(Path.GetDirectoryName(store.Root)).Load().Instances.FirstOrDefault(i=>i.Id==grant.Instance);
            return instance==null?grant.InstanceName:instance.Name;
        }
        public object List(RelaySession session)
        {
            return EffectiveGrants(session).Select(g=>new{id=g.Id,name=g.Name,instance=g.Instance,instanceName=GlobalGrantId(g.Id)?g.InstanceName:CurrentName(g),resourceField=g.ResourceField,resourceValue=g.ResourceValue,resourceValues=g.ResourceValues,resourceLabels=g.ResourceLabels,
                dynamic=GlobalGrantId(g.Id),allResources=GlobalGrantId(g.Id),access=GlobalGrantId(g.Id)?"read-write":null,
                discovery=GlobalGrantId(g.Id)?new{server="*",tool="*",instruction="Use describe_shared_tool with this share and server/tool * to fetch only this owner's current native tools. All current and future resources and read/write actions are shared."}:null,
                resourceLinks=GlobalGrantId(g.Id)?new{tool="get_shared_resource_link",instruction="Supply kind=page or kind=site with the exact resource ID from a verified native result."}:null,
                tools=g.Tools.Select(t=>new{server=t.Server,name=t.Name,group=t.Group,readOnly=t.ReadOnly,limitation=Limitation(t)}).ToArray()}).ToArray();
        }
        public object Describe(RelaySession session,string grantId,string server,string name)
        {return DescribeAsync(session,grantId,server,name,CancellationToken.None).GetAwaiter().GetResult();}
        public async Task<object> DescribeAsync(RelaySession session,string grantId,string server,string name,CancellationToken token)
        {
            var g=Authorize(session,grantId);bool global=GlobalGrantId(g.Id);TunnelTool[] tools=g.Tools.ToArray();
            if(global){
                var owner=resolveOwner(g.Instance);CheckOwner(new TunnelOwner{Id=g.Instance,Account=g.Account,Home=g.Home},owner);
                using(var connection=connect(owner))tools=(await connection.Inventory(token).ConfigureAwait(false)).Where(t=>AllowedServer(t.Server)).ToArray();
                CheckOwner(owner,resolveOwner(g.Instance));
                if(Authorize(session,grantId).Revision!=g.Revision)throw new InvalidOperationException("Le mode d'accès complet a changé pendant l'inventaire.");
                foreach(var t in tools)t.Signature=Signature(t);
                if(server=="*"&&name=="*")return new{instance=g.Instance,instanceName=g.InstanceName,allResources=true,access="read-write",tools=tools};
            }
            var tool=tools.SingleOrDefault(t=>t.Server==server&&t.Name==name);if(tool==null)throw new InvalidOperationException("Cet outil n'est pas disponible dans ce partage.");
            return new{instance=g.Instance,instanceName=global?g.InstanceName:CurrentName(g),resourceField=g.ResourceField,resourceValue=g.ResourceValue,resourceValues=g.ResourceValues,resourceLabels=g.ResourceLabels,allResources=global,limitation=Limitation(tool),fileTransfer="Declared Sites .tar.gz archives use the owner's authenticated upload, with full access required in the source chat. Other native file uploads are unavailable; credentials are returned once and redacted from storage.",tool=tool};
        }
        private string Record(string id){RelayStore.MessageId(id);return "tool-call-"+id+".dpapi";}
        private void Persist(TunnelCall call)
        {
            call.Updated=DateTime.UtcNow.ToString("o");var result=call.Result;
            if(result!=null){bool sensitive=false;var redacted=Redact(result,ref sensitive);call.SensitiveResult=sensitive;store.WriteRecord("tool-result-"+call.Id+".dpapi",new TunnelCall{Id=call.Id,Result=redacted});call.HasResult=true;}
            // The UI reads only small metadata records, never all private connector responses.
            call.Result=null;try{store.WriteRecord(Record(call.Id),call);}finally{call.Result=result;}
        }
        internal static object Redact(object value,ref bool sensitive)
        {
            var obj=value as IDictionary<string,object>;
            if(obj!=null){var copy=new Dictionary<string,object>();foreach(var p in obj){
                string key=p.Key.ToLowerInvariant();
                if(new[]{"token","access_token","refresh_token","id_token","api_key","password","secret","client_secret","authorization","siwc_bypass_bearer_token"}.Contains(key)&&p.Value!=null){copy[p.Key]="[not retained]";sensitive=true;}
                else if(p.Key=="text"&&p.Value is string){try{copy[p.Key]=Json.Write(Redact(Json.Read<object>((string)p.Value),ref sensitive));}catch{copy[p.Key]=p.Value;}}
                else copy[p.Key]=Redact(p.Value,ref sensitive);
            }return copy;}
            var array=value as IEnumerable;if(array!=null&&!(value is string)){var items=new List<object>();foreach(var item in array)items.Add(Redact(item,ref sensitive));return items.ToArray();}return value;
        }
        internal static void CheckResource(TunnelGrant g,object args)
        {if(!(args is IDictionary<string,object>))throw new InvalidOperationException("Les arguments doivent être un objet JSON.");if(!String.IsNullOrEmpty(g.ResourceField)){string value=Json.Get(args,g.ResourceField) as string;if(value==null||((g.ResourceValues!=null&&g.ResourceValues.Count>0)?!g.ResourceValues.Contains(value,StringComparer.Ordinal):value!=g.ResourceValue))throw new InvalidOperationException("Cette ressource n'est pas autorisée par le partage.");}}
        internal static void SameCaller(TunnelCall c,RelaySession s)
        {if(c.Id==null||c.SourceThread!=s.Thread||c.SourceAccount!=s.Account||!RelayStore.SamePath(c.SourceHome,s.Home))throw new InvalidOperationException("Cet appel appartient à un autre chat.");}
        public TunnelCall Read(RelaySession session,string id)
        {verifySource(session);var c=store.ReadRecord<TunnelCall>(Record(id));SameCaller(c,session);Authorize(session,c.Grant);if(c.Result==null&&c.HasResult)c.Result=store.ReadRecord<TunnelCall>("tool-result-"+id+".dpapi").Result;return c;}
        public TunnelCall[] Recent()
        {return Directory.EnumerateFiles(store.Root,"tool-call-*.dpapi").OrderByDescending(File.GetLastWriteTimeUtc).Take(100).Select(p=>store.ReadRecord<TunnelCall>(Path.GetFileName(p))).ToArray();}
        public async Task<TunnelCall> Invoke(RelaySession session,string id,string grantId,string server,string name,object args,CancellationToken token)
        {
            RelayStore.MessageId(id);if(Encoding.UTF8.GetByteCount(Json.Write(args))>200000)throw new InvalidOperationException("Arguments limités à 200 Ko.");
            var grant=Authorize(session,grantId);CheckResource(grant,args);
            bool global=GlobalGrantId(grant.Id);
            var chosen=grant.Tools.SingleOrDefault(t=>t.Server==server&&t.Name==name);if(!global&&chosen==null)throw new InvalidOperationException("Cet outil n'est pas partagé.");
            string hash=Hash(Json.Read<object>(Json.Write(new{grantId,server,name,args})));
            using(store.Lease("tool-call-"+id)){
                var existing=store.ReadRecord<TunnelCall>(Record(id));
                if(existing.Id!=null){SameCaller(existing,session);if(existing.Fingerprint!=hash)throw new InvalidOperationException("Cet identifiant a déjà été utilisé avec d'autres arguments.");if(global&&(existing.Grant!=grant.Id||existing.Revision!=grant.Revision))throw new InvalidOperationException("Le mode global ou le compte propriétaire a changé depuis cet appel. Aucun renvoi effectué.");return existing;}
                var call=new TunnelCall{Id=id,Grant=grant.Id,GrantName=grant.Name,Instance=grant.Instance,Revision=grant.Revision,SourceThread=session.Thread,SourceHome=session.Home,SourceAccount=session.Account,Fingerprint=hash,Tool=server+"/"+name,State="preparing",Created=DateTime.UtcNow.ToString("o")};Persist(call);
                bool sent=false;SitesArchive archive=null;
                try{
                    if(!global){
                        CheckArgumentSurface(chosen.Schema,args,"arguments");
                        if(SitesArchive.Supplied(chosen,args))archive=SitesArchive.Open(chosen,args,session);
                        else {CheckSitesTransport(chosen,args);CheckTransport(chosen.Schema,args);}
                    }
                    var pinned=new TunnelOwner{Id=grant.Instance,Home=grant.Home,Account=grant.Account};var owner=resolveOwner(grant.Instance);CheckOwner(pinned,owner);
                    // Serializes direct calls to one account, including calls from different chats/processes.
                    using(store.Lease("tool-owner-"+Hash(owner.Account).Substring(0,32)))
                    using(var connection=connect(owner)){
                        var inventory=await connection.Inventory(token).ConfigureAwait(false);var live=inventory.SingleOrDefault(t=>t.Server==server&&t.Name==name);
                        if(global){
                            if(live==null||!AllowedServer(live.Server)||live.Schema==null)throw new InvalidOperationException("Cet outil n'est pas disponible dans l'inventaire natif du propriétaire.");
                            // An explicit global policy authorizes the current
                            // native contract, including future write tools and
                            // resources, instead of a static selected snapshot.
                            chosen=live;
                            if(SitesArchive.Supplied(live,args))archive=SitesArchive.Open(live,args,session);
                        }
                        string difference=ContractDifference(chosen,live,args);call.ContractStatus=global?"global":difference!=null?"breaking":Signature(chosen)==Signature(live)?"unchanged":"compatible";call.ContractReason=difference??(global?"Accès complet explicite : contrat natif courant, toutes ressources et actions lecture/écriture.":call.ContractStatus=="compatible"?"Documentation ou parametres optionnels compatibles ; droits et schema autorise conserves.":null);
                        if(difference!=null)throw new InvalidOperationException("Le contrat de cet outil necessite une nouvelle autorisation : "+difference+". Aucun appel envoye. Actualisez uniquement ce partage si vous acceptez cette modification.");
                        // Customized grants do not implicitly accept newly
                        // introduced parameters. Global policy instead uses the
                        // selected owner's current native contract above.
                        CheckArgumentSurface(live.Schema,args,"arguments");
                        if(archive==null){CheckSitesTransport(live,args);CheckTransport(live.Schema,args);}
                        var current=Authorize(session,grantId);if(current.Revision!=grant.Revision)throw new InvalidOperationException("Le partage a été modifié ou révoqué avant l'envoi.");
                        var fileConnection=connection as IToolTunnelArchiveConnection;
                        if(archive!=null&&fileConnection==null)throw new InvalidOperationException("Ce moteur ne prend pas en charge le transfert natif d’archive Sites. Aucun appel envoyé.");
                        if(archive!=null){call.ArchiveHash=archive.Hash;call.ArchiveBytes=archive.Bytes;}
                        CheckOwner(pinned,resolveOwner(owner.Id));token.ThrowIfCancellationRequested();call.State="executing";Persist(call);sent=true;
                        Action validateBeforeSave=delegate{
                            var latest=Authorize(session,grantId);if(latest.Revision!=grant.Revision)throw new InvalidOperationException("Le partage a été modifié ou révoqué pendant le transfert. Aucune sauvegarde envoyée.");
                            CheckOwner(pinned,resolveOwner(owner.Id));
                            if(RelayPermissions.Read(session.Home,session.Thread)!="full-access")throw new RelayPermissionException("L’Accès complet du chat source n’est plus confirmé. Aucune sauvegarde envoyée.");
                        };
                        var result=archive==null?await connection.Invoke(server,name,args,token).ConfigureAwait(false):await fileConnection.InvokeArchive(server,name,args,validateBeforeSave,token).ConfigureAwait(false);CheckOwner(pinned,resolveOwner(owner.Id));
                        if(Encoding.UTF8.GetByteCount(Json.Write(result))>3000000)throw new IOException("Résultat trop volumineux ; vérifier l'opération chez le propriétaire.");
                        call.Result=result;call.State=Object.Equals(Json.Get(result,"isError"),true)||Json.Get(Json.Get(result,"structuredContent"),"error")!=null?"tool_error":"completed";Persist(call);
                    }
                }catch(Exception e){call.State=sent?"uncertain":"blocked";call.Error=e is TunnelInteractionException?e.Message:sent?"La réponse n'a pas pu être confirmée. Vérifiez la ressource avant toute nouvelle opération. Aucun nouvel envoi automatique. "+(e is InvalidOperationException||e is IOException?e.Message:e.GetType().Name):Program.SafeError(e);Persist(call);}
                finally{if(archive!=null)archive.Dispose();}
                return call;
            }
        }
        public static object Summary(TunnelCall c)
        {return new{id=c.Id,grant=c.Grant,name=c.GrantName,tool=c.Tool,state=c.State,error=c.Error,created=c.Created,updated=c.Updated,resultAvailable=c.HasResult||c.Result!=null,sensitiveResult=c.SensitiveResult,archiveHash=c.ArchiveHash,archiveBytes=c.ArchiveBytes,contractStatus=c.ContractStatus,contractReason=c.ContractReason,note=c.State=="executing"||c.State=="uncertain"?"Do not repeat this operation. Inspect the provider resource; an interrupted process may have completed it.":c.SensitiveResult?"Sensitive values are returned only with the first response and are redacted from storage. Never display or persist credentials.":null};}
        public static object Reply(TunnelCall c)
        {return c.SensitiveResult&&c.Result!=null?(object)new{call=Summary(c),ephemeralResult=c.Result}:Summary(c);}
        public object Result(RelaySession session,string id,int offset,int length)
        {
            var c=Read(session,id);string text=c.Result==null?"":Json.Write(c.Result);
            if(offset<0||offset>text.Length||length<1||length>24000)throw new InvalidOperationException("Page de résultat invalide.");
            string part=text.Substring(offset,Math.Min(length,text.Length-offset));return new{call=Summary(c),json=part,total=text.Length,nextOffset=offset+part.Length,hasMore=offset+part.Length<text.Length};
        }
    }
}
