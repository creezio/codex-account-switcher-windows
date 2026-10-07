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
        public ToolTunnel(RelayStore data,Func<string,TunnelOwner> resolver=null,Func<TunnelOwner,IToolTunnelConnection> connector=null,Action<RelaySession> verifier=null)
        {store=data;resolveOwner=resolver??ResolveOwner;connect=connector??(o=>new ToolTunnelRpc(o.Executable,o.Home));verifySource=verifier??VerifySource;}
        internal static bool AllowedServer(string name)
        {return !String.IsNullOrWhiteSpace(name)&&!new[]{"creezio-relay","codex_app","cua_repl","node_repl","code-review"}.Contains(name);}
        private TunnelOwner ResolveOwner(string id)
        {
            var a=new AccountService(Path.GetDirectoryName(store.Root));var i=NamedInstances.Resolve(a,id);NamedInstances.VerifyAccount(a,i);
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
            return saved!=null&&live!=null&&Signature(saved)==Signature(live);
        }
        internal static string Limitation(TunnelTool tool)
        {
            if(tool.Server=="codex_apps"&&(tool.Name=="sites.save_site_version"||tool.Name=="sites.save_version_and_deploy_private"))return "Publication d'une nouvelle version Sites indisponible : le transport direct ne transfère pas l'archive. Utilisez la publication native dans Codex.";
            return "";
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
        {verifySource(session);var g=Grants().SingleOrDefault(x=>x.Id==id);if(g==null||!CanUse(g,session))throw new InvalidOperationException("Ce partage d'outils est absent, désactivé ou interdit à cette instance.");return g;}
        internal string CurrentName(TunnelGrant grant)
        {
            var instance=new Vault(Path.GetDirectoryName(store.Root)).Load().Instances.FirstOrDefault(i=>i.Id==grant.Instance);
            return instance==null?grant.InstanceName:instance.Name;
        }
        public object List(RelaySession session)
        {
            verifySource(session);
            return Grants().Where(g=>CanUse(g,session)).Select(g=>new{id=g.Id,name=g.Name,instance=g.Instance,instanceName=CurrentName(g),resourceField=g.ResourceField,resourceValue=g.ResourceValue,resourceValues=g.ResourceValues,resourceLabels=g.ResourceLabels,tools=g.Tools.Select(t=>new{server=t.Server,name=t.Name,group=t.Group,readOnly=t.ReadOnly,limitation=Limitation(t)}).ToArray()}).ToArray();
        }
        public object Describe(RelaySession session,string grantId,string server,string name)
        {var g=Authorize(session,grantId);var tool=g.Tools.Single(t=>t.Server==server&&t.Name==name);return new{instance=g.Instance,instanceName=CurrentName(g),resourceField=g.ResourceField,resourceValue=g.ResourceValue,resourceValues=g.ResourceValues,resourceLabels=g.ResourceLabels,limitation=Limitation(tool),fileTransfer="Native local-file uploads are unavailable; credentials are returned once and redacted from storage.",tool=tool};}
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
                if(new[]{"token","access_token","refresh_token","id_token","api_key","password","secret","client_secret","authorization"}.Contains(key)&&p.Value!=null){copy[p.Key]="[not retained]";sensitive=true;}
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
            var chosen=grant.Tools.SingleOrDefault(t=>t.Server==server&&t.Name==name);if(chosen==null)throw new InvalidOperationException("Cet outil n'est pas partagé.");
            string hash=Hash(Json.Read<object>(Json.Write(new{grantId,server,name,args})));
            using(store.Lease("tool-call-"+id)){
                var existing=store.ReadRecord<TunnelCall>(Record(id));
                if(existing.Id!=null){SameCaller(existing,session);if(existing.Fingerprint!=hash)throw new InvalidOperationException("Cet identifiant a déjà été utilisé avec d'autres arguments.");return existing;}
                var call=new TunnelCall{Id=id,Grant=grant.Id,GrantName=grant.Name,Instance=grant.Instance,Revision=grant.Revision,SourceThread=session.Thread,SourceHome=session.Home,SourceAccount=session.Account,Fingerprint=hash,Tool=server+"/"+name,State="preparing",Created=DateTime.UtcNow.ToString("o")};Persist(call);
                bool sent=false;
                try{
                    string limitation=Limitation(chosen);if(limitation.Length>0)throw new InvalidOperationException(limitation);CheckTransport(chosen.Schema,args);
                    var pinned=new TunnelOwner{Id=grant.Instance,Home=grant.Home,Account=grant.Account};var owner=resolveOwner(grant.Instance);CheckOwner(pinned,owner);
                    // Serializes direct calls to one account, including calls from different chats/processes.
                    using(store.Lease("tool-owner-"+Hash(owner.Account).Substring(0,32)))
                    using(var connection=connect(owner)){
                        var inventory=await connection.Inventory(token).ConfigureAwait(false);var live=inventory.SingleOrDefault(t=>t.Server==server&&t.Name==name);
                        if(!SameContract(chosen,live))throw new InvalidOperationException("La définition de cet outil a changé depuis l'autorisation. Rouvrez ses accès dans le switcher et enregistrez-les à nouveau.");
                        var current=Authorize(session,grantId);if(current.Revision!=grant.Revision)throw new InvalidOperationException("Le partage a été modifié ou révoqué avant l'envoi.");
                        CheckOwner(pinned,resolveOwner(owner.Id));token.ThrowIfCancellationRequested();call.State="executing";Persist(call);sent=true;
                        var result=await connection.Invoke(server,name,args,token).ConfigureAwait(false);CheckOwner(pinned,resolveOwner(owner.Id));
                        if(Encoding.UTF8.GetByteCount(Json.Write(result))>3000000)throw new IOException("Résultat trop volumineux ; vérifier l'opération chez le propriétaire.");
                        call.Result=result;call.State=Object.Equals(Json.Get(result,"isError"),true)||Json.Get(Json.Get(result,"structuredContent"),"error")!=null?"tool_error":"completed";Persist(call);
                    }
                }catch(Exception e){call.State=sent?"uncertain":"blocked";call.Error=e is TunnelInteractionException?e.Message:sent?"La réponse n'a pas pu être confirmée. Vérifiez la ressource avant toute nouvelle opération. Aucun nouvel envoi automatique. "+(e is InvalidOperationException||e is IOException?e.Message:e.GetType().Name):Program.SafeError(e);Persist(call);}
                return call;
            }
        }
        public static object Summary(TunnelCall c)
        {return new{id=c.Id,grant=c.Grant,name=c.GrantName,tool=c.Tool,state=c.State,error=c.Error,created=c.Created,updated=c.Updated,resultAvailable=c.HasResult||c.Result!=null,sensitiveResult=c.SensitiveResult,note=c.State=="executing"||c.State=="uncertain"?"Do not repeat this operation. Inspect the provider resource; an interrupted process may have completed it.":c.SensitiveResult?"Sensitive values are returned only with the first response and are redacted from storage. Never display or persist credentials.":null};}
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
