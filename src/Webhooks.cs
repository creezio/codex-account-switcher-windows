using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Creezio.Switcher
{
    public sealed class WebhookConfig
    {
        public bool Enabled {get;set;}
        public int Port {get;set;}
        public WebhookConfig(){Port=47831;}
    }
    public sealed class WebhookRoute
    {
        public string Id {get;set;}
        public long Revision {get;set;}
        public string Slug {get;set;}
        public string Name {get;set;}
        public string InstanceId {get;set;}
        public string AccountKey {get;set;}
        public string Home {get;set;}
        public string Workspace {get;set;}
        public string ThreadId {get;set;}
        public string PromptPrefix {get;set;}
        public string Access {get;set;}
        public string PermissionProfile {get;set;}
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string SecretHash {get;set;}
        public bool Enabled {get;set;}
    }
    public sealed class WebhookJob
    {
        public long PortalRevision {get;set;}
        public string PortalInstanceId {get;set;}
        public string PortalChatId {get;set;}
        public string Id {get;set;}
        public string RouteId {get;set;}
        public string ExternalId {get;set;}
        public string Title {get;set;}
        public string Prompt {get;set;}
        public string Context {get;set;}
        public PortalMessageOptions Options {get;set;}
        public string ConversationId {get;set;}
        public string Fingerprint {get;set;}
        public string State {get;set;}
        public string Error {get;set;}
        public string RelayJobId {get;set;}
        public string ThreadId {get;set;}
        public string Result {get;set;}
        public string CreatedUtc {get;set;}
        public string UpdatedUtc {get;set;}
        public WebhookRoute RouteSnapshot {get;set;}
    }
    public sealed class WebhookWorkerState
    {
        public int Pid {get;set;}
        public long Started {get;set;}
        public string Updated {get;set;}
        public string Error {get;set;}
        public int Port {get;set;}
        public bool Listening {get;set;}
    }
    internal sealed class WebhookException : InvalidOperationException
    {
        internal int StatusCode {get;private set;}
        internal WebhookException(int status,string message):base(message){StatusCode=status;}
    }
    internal sealed class WebhookRate
    {
        public string Window {get;set;}
        public int Submissions {get;set;}
        public int Reads {get;set;}
    }
    internal sealed class WebhookIndex
    {
        public int Version {get;set;}
        public int Shards {get;set;}
        public Dictionary<string,int> Map {get;set;}
    }
    internal sealed class WebhookIndexShard
    {
        public List<WebhookIndexRow> Rows {get;set;}
    }
    internal sealed class WebhookIndexRow
    {
        public string Id {get;set;}
        public string RouteId {get;set;}
        public string ExternalId {get;set;}
        public string ConversationId {get;set;}
        public string Title {get;set;}
        public string State {get;set;}
        public string Error {get;set;}
        public string RelayJobId {get;set;}
        public string ThreadId {get;set;}
        public string CreatedUtc {get;set;}
        public string UpdatedUtc {get;set;}
    }
    internal static class Webhooks
    {
        internal const int MaximumBodyBytes=65536;
        internal const int MaximumPrompt=20000;
        internal const int MaximumRoutes=100;
        internal const int MaximumPending=500;
        internal const int MaximumPendingPerRoute=100;
        internal const int MaximumHistory=10000;
        private static readonly string[] States={"queued","waiting","sending","monitoring","attention","uncertain","completed","failed","blocked","cancelled"};
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        private const string IndexFile="webhook-index-v1.dpapi",DirtyFile="webhook-index-dirty-v1.dpapi";
        private const int ShardSize=500;
        private sealed class IndexCache
        {
            internal long Stamp,Length;
            internal WebhookJob[] Rows;
            internal DateTime Accessed;
        }
        private static readonly object CacheGate=new object();
        private static readonly Dictionary<string,IndexCache> Cache=new Dictionary<string,IndexCache>(StringComparer.OrdinalIgnoreCase);
        private static T Copy<T>(T value){return Json.Read<T>(Json.Write(value));}
        internal static string Hash(string value)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(value??""))).Replace("-","").ToLowerInvariant();}
        private static string JobFile(string id){RelayStore.MessageId(id);return "webhook-job-"+id+".dpapi";}
        internal static FileStream Admission(RelayStore store)
        {
            DateTime deadline=DateTime.UtcNow.AddSeconds(3);
            while(true)try{return store.Lease("webhook-admission");}catch(IOException){if(DateTime.UtcNow>=deadline)throw new WebhookException(429,"Le service est occupe. Reessayez avec la meme cle d'idempotence.");Thread.Sleep(10);}
        }
        private static FileStream IndexLease(RelayStore store)
        {
            DateTime deadline=DateTime.UtcNow.AddSeconds(10);
            while(true)try{return store.Lease("webhook-index-v1");}catch(IOException){if(DateTime.UtcNow>=deadline)throw new InvalidOperationException("Le registre des webhooks est occupe. Reessayez dans quelques secondes.");Thread.Sleep(10);}
        }
        private static string ShardFile(int shard){return "webhook-index-shard-"+shard.ToString("00",CultureInfo.InvariantCulture)+"-v1.dpapi";}
        private static string Short(string value,int maximum){return value==null||value.Length<=maximum?value:value.Substring(0,maximum);}
        private static WebhookJob Metadata(WebhookJob value)
        {
            return new WebhookJob{Id=value.Id,RouteId=value.RouteId,ExternalId=Short(value.ExternalId,128),ConversationId=value.ConversationId,Title=Short(value.Title,120),State=value.State,Error=Short(value.Error,160),RelayJobId=Short(value.RelayJobId,200),ThreadId=Short(value.ThreadId,200),CreatedUtc=value.CreatedUtc,UpdatedUtc=value.UpdatedUtc};
        }
        private static WebhookIndexRow IndexRow(WebhookJob value)
        {var row=Metadata(value);return new WebhookIndexRow{Id=row.Id,RouteId=row.RouteId,ExternalId=row.ExternalId,ConversationId=row.ConversationId,Title=row.Title,State=row.State,Error=row.Error,RelayJobId=row.RelayJobId,ThreadId=row.ThreadId,CreatedUtc=row.CreatedUtc,UpdatedUtc=row.UpdatedUtc};}
        private static WebhookJob Metadata(WebhookIndexRow row)
        {return new WebhookJob{Id=row.Id,RouteId=row.RouteId,ExternalId=row.ExternalId,ConversationId=row.ConversationId,Title=row.Title,State=row.State,Error=row.Error,RelayJobId=row.RelayJobId,ThreadId=row.ThreadId,CreatedUtc=row.CreatedUtc,UpdatedUtc=row.UpdatedUtc};}
        private static void InvalidateIndex(RelayStore store){lock(CacheGate)Cache.Remove(store.Root);}
        private static void ClearDirty(RelayStore store)
        {string path=Path.Combine(store.Root,DirtyFile);SafeFiles.RejectLinks(path);File.Delete(path);InvalidateIndex(store);}
        private static void MarkDirty(RelayStore store)
        {store.WriteRecord(DirtyFile,new{at=DateTime.UtcNow.ToString("o")});InvalidateIndex(store);}
        private static WebhookIndex RebuildIndex(RelayStore store)
        {
            MarkDirty(store);var index=new WebhookIndex{Version=1,Map=new Dictionary<string,int>(StringComparer.Ordinal)};var shard=new List<WebhookIndexRow>();int number=0;
            foreach(string path in Directory.EnumerateFiles(store.Root,"webhook-job-*.dpapi",SearchOption.TopDirectoryOnly)){
                string name=Path.GetFileName(path);if(!Regex.IsMatch(name,@"\Awebhook-job-[a-f0-9]{32}\.dpapi\z"))continue;
                if(index.Map.Count>=MaximumHistory)throw new InvalidOperationException("Le registre depasse le maximum de missions webhook. Les historiques sont conserves.");
                var row=IndexRow(Job(store,name.Substring(12,32)));index.Map.Add(row.Id,number);shard.Add(row);
                if(shard.Count==ShardSize){store.WriteRecord(ShardFile(number),new WebhookIndexShard{Rows=shard});number++;shard=new List<WebhookIndexRow>();}
            }
            if(shard.Count>0){store.WriteRecord(ShardFile(number),new WebhookIndexShard{Rows=shard});number++;}
            index.Shards=number;store.WriteRecord(IndexFile,index);ClearDirty(store);return index;
        }
        private static WebhookIndex ReadIndex(RelayStore store)
        {
            if(File.Exists(Path.Combine(store.Root,DirtyFile))||!File.Exists(Path.Combine(store.Root,IndexFile)))return RebuildIndex(store);
            var index=store.ReadRecord<WebhookIndex>(IndexFile);
            if(index.Version!=1||index.Map==null||index.Map.Count>MaximumHistory||index.Shards<0||index.Shards>MaximumHistory/ShardSize||index.Map.Any(pair=>!Regex.IsMatch(pair.Key,@"\A[a-f0-9]{32}\z")||pair.Value<0||pair.Value>=index.Shards))throw new InvalidOperationException("L'index des webhooks est invalide. Les missions sont conservees.");
            return index;
        }
        internal static WebhookConfig Config(RelayStore store){return store.ReadRecord<WebhookConfig>("webhook-config-v1.dpapi");}
        internal static void SaveConfig(RelayStore store,WebhookConfig value)
        {
            if(value==null||value.Port<1024||value.Port>65535)throw new InvalidOperationException("Le port local doit etre compris entre 1024 et 65535.");
            using(store.Lease("webhook-config"))store.WriteRecord("webhook-config-v1.dpapi",value);
        }
        internal static WebhookRoute[] Routes(RelayStore store){return store.ReadRecord<List<WebhookRoute>>("webhook-routes-v1.dpapi").ToArray();}
        internal static WebhookRoute Route(RelayStore store,string id)
        {RelayStore.MessageId(id);var value=Routes(store).SingleOrDefault(r=>r.Id==id);if(value==null)throw new WebhookException(404,"Route introuvable.");return value;}
        private static void ValidateRoute(WebhookRoute route)
        {
            RelayStore.MessageId(route.Id);
            if(!Regex.IsMatch(route.Slug??"",@"\A[a-z0-9][a-z0-9-]{1,62}\z"))throw new InvalidOperationException("L'adresse de la route doit contenir 2 a 63 lettres minuscules, chiffres ou tirets.");
            if(String.IsNullOrWhiteSpace(route.Name)||route.Name.Length>160||route.Name.Any(Char.IsControl))throw new InvalidOperationException("Donnez un nom de route entre 1 et 160 caracteres.");
            if(route.InstanceId!="local")RelayStore.MessageId(route.InstanceId);
            if(String.IsNullOrWhiteSpace(route.AccountKey)||route.AccountKey.Length>256||route.AccountKey.Any(Char.IsControl))throw new InvalidOperationException("Compte permanent de la route invalide.");
            if(String.IsNullOrWhiteSpace(route.Home)||!Path.IsPathRooted(route.Home)||route.Home.StartsWith("\\\\",StringComparison.Ordinal)||!Directory.Exists(route.Home))throw new InvalidOperationException("Profil local de la route introuvable.");
            route.Home=Path.GetFullPath(route.Home);SafeFiles.RejectLinks(route.Home);
            route.Workspace=RelayStore.WorkspacePath(route.Workspace);
            Guid thread;if(!String.IsNullOrEmpty(route.ThreadId)&&!Guid.TryParse(route.ThreadId,out thread))throw new InvalidOperationException("Identifiant du chat cible invalide.");
            if((route.PromptPrefix??"").Length>MaximumPrompt)throw new InvalidOperationException("Les instructions permanentes sont trop longues.");
            route.Access=String.IsNullOrEmpty(route.Access)?"desktop":route.Access;
            if(!new[]{"desktop","headless"}.Contains(route.Access))throw new InvalidOperationException("Mode d'execution de la route invalide.");
            route.PermissionProfile=String.IsNullOrEmpty(route.PermissionProfile)?"inherit":route.PermissionProfile;
            if(!new[]{"inherit","full-access"}.Contains(route.PermissionProfile))throw new InvalidOperationException("Profil de permissions invalide.");
            if(!String.IsNullOrEmpty(route.Model)&&!Regex.IsMatch(route.Model,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,119}\z"))throw new InvalidOperationException("Modele invalide.");
            if(!String.IsNullOrEmpty(route.ReasoningEffort)&&!new[]{"none","minimal","low","medium","high","xhigh","max","ultra"}.Contains(route.ReasoningEffort))throw new InvalidOperationException("Niveau de raisonnement invalide.");
        }
        internal static WebhookRoute SaveRoute(RelayStore store,WebhookRoute input)
        {
            if(input==null)throw new ArgumentNullException("input");var value=Copy(input);value.Id=String.IsNullOrEmpty(value.Id)?Guid.NewGuid().ToString("N"):value.Id;ValidateRoute(value);
            using(store.Lease("webhook-routes")){
                var routes=Routes(store).ToList();var old=routes.SingleOrDefault(r=>r.Id==value.Id);
                if(routes.Any(r=>r.Id!=value.Id&&r.Slug==value.Slug))throw new InvalidOperationException("Cette adresse est deja utilisee par une route.");
                if(old==null&&routes.Count>=MaximumRoutes)throw new InvalidOperationException("Le maximum de routes est atteint.");
                if(old!=null&&value.Revision!=old.Revision)throw new InvalidOperationException("La route a change. Actualisez avant d'enregistrer.");
                value.SecretHash=old==null?null:old.SecretHash;value.Revision=old==null?1:old.Revision+1;
                routes.RemoveAll(r=>r.Id==value.Id);routes.Add(value);store.WriteRecord("webhook-routes-v1.dpapi",routes);return value;
            }
        }
        internal static string RotateKey(RelayStore store,string id)
        {
            RelayStore.MessageId(id);using(store.Lease("webhook-routes")){
                var routes=Routes(store).ToList();var route=routes.SingleOrDefault(r=>r.Id==id);if(route==null)throw new WebhookException(404,"Route introuvable.");
                var random=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(random);
                string secret="whk_"+Convert.ToBase64String(random).TrimEnd('=').Replace('+','-').Replace('/','_');Array.Clear(random,0,random.Length);
                route.SecretHash=Hash(secret);route.Revision++;store.WriteRecord("webhook-routes-v1.dpapi",routes);return secret;
            }
        }
        private static bool EqualSecret(string expected,string actual)
        {if(expected==null||actual==null||expected.Length!=64||actual.Length!=64)return false;int difference=0;for(int i=0;i<64;i++)difference|=expected[i]^actual[i];return difference==0;}
        internal static WebhookRoute Authorize(RelayStore store,string slug,string bearer)
        {
            // Route existence and disabled state do not leak through failed authentication.
            var route=Routes(store).SingleOrDefault(r=>r.Slug==slug);
            bool valid=bearer!=null&&Regex.IsMatch(bearer,@"\Awhk_[A-Za-z0-9_-]{43}\z");
            string digest=Hash(valid?bearer:"");bool equal=EqualSecret(route==null?new string('0',64):route.SecretHash,digest);
            if(route==null||!valid||!equal)throw new WebhookException(401,"Authentification requise.");
            if(!Config(store).Enabled||!route.Enabled)throw new WebhookException(503,"Cette route est desactivee.");
            return route;
        }
        internal static void ValidateAuthorization(RelayStore store,string slug,string bearer){Authorize(store,slug,bearer);}
        internal static void Limit(RelayStore store,WebhookRoute route,bool submission)
        {
            string name="webhook-rate-"+route.Id+".dpapi";var rate=store.ReadRecord<WebhookRate>(name);string window=DateTime.UtcNow.ToString("yyyyMMddHHmm",CultureInfo.InvariantCulture);
            if(rate.Window!=window)rate=new WebhookRate{Window=window};
            if(submission){if(rate.Submissions>=60)throw new WebhookException(429,"Trop de demandes. Reessayez plus tard.");rate.Submissions++;}
            else{if(rate.Reads>=180)throw new WebhookException(429,"Trop de lectures. Reessayez plus tard.");rate.Reads++;}
            store.WriteRecord(name,rate);
        }
        internal static bool IsPending(WebhookJob job){return new[]{"queued","waiting","sending","monitoring","attention","uncertain"}.Contains(job.State);}
        internal static WebhookJob[] Jobs(RelayStore store)
        {
            using(IndexLease(store)){
                bool dirty=File.Exists(Path.Combine(store.Root,DirtyFile));var file=new FileInfo(Path.Combine(store.Root,IndexFile));IndexCache cached;
                lock(CacheGate)if(!dirty&&file.Exists&&Cache.TryGetValue(store.Root,out cached)&&cached.Stamp==file.LastWriteTimeUtc.Ticks&&cached.Length==file.Length){cached.Accessed=DateTime.UtcNow;return cached.Rows.Select(Metadata).ToArray();}
                var index=ReadIndex(store);var rows=new List<WebhookJob>();
                for(int i=0;i<index.Shards;i++){
                    var shard=store.ReadRecord<WebhookIndexShard>(ShardFile(i));
                    if(shard.Rows==null||shard.Rows.Count>ShardSize||shard.Rows.Any(row=>row==null||!index.Map.ContainsKey(row.Id)||index.Map[row.Id]!=i))throw new InvalidOperationException("Un segment du registre des webhooks est invalide.");
                    rows.AddRange(shard.Rows.Select(Metadata));
                }
                if(rows.Count!=index.Map.Count||rows.Select(row=>row.Id).Distinct(StringComparer.Ordinal).Count()!=rows.Count)throw new InvalidOperationException("Le registre des webhooks est incomplet. Les missions sont conservees.");
                var sorted=rows.OrderByDescending(row=>row.CreatedUtc,StringComparer.Ordinal).ToArray();file.Refresh();
                lock(CacheGate){if(Cache.Count>=16&&!Cache.ContainsKey(store.Root))Cache.Remove(Cache.OrderBy(pair=>pair.Value.Accessed).First().Key);Cache[store.Root]=new IndexCache{Stamp=file.LastWriteTimeUtc.Ticks,Length=file.Length,Rows=sorted,Accessed=DateTime.UtcNow};}
                return sorted.Select(Metadata).ToArray();
            }
        }
        internal static WebhookJob Job(RelayStore store,string id)
        {var value=store.ReadRecord<WebhookJob>(JobFile(id));if(value.Id!=id)throw new WebhookException(404,"Mission introuvable.");return value;}
        internal static void SaveJob(RelayStore store,WebhookJob job)
        {
            if(job==null||!States.Contains(job.State))throw new InvalidOperationException("Etat de mission webhook invalide.");
            RelayStore.MessageId(job.Id);RelayStore.MessageId(job.RouteId);
            using(IndexLease(store)){
                var index=ReadIndex(store);
                if(File.Exists(Path.Combine(store.Root,JobFile(job.Id)))){
                    var old=Job(store,job.Id);
                    if(old.PortalRevision!=job.PortalRevision||old.PortalInstanceId!=job.PortalInstanceId||old.PortalChatId!=job.PortalChatId||old.RouteId!=job.RouteId||old.ExternalId!=job.ExternalId||old.ConversationId!=job.ConversationId||old.Fingerprint!=job.Fingerprint||old.Title!=job.Title||old.Prompt!=job.Prompt||old.Context!=job.Context||old.CreatedUtc!=job.CreatedUtc||Json.Write(old.Options)!=Json.Write(job.Options)||Json.Write(old.RouteSnapshot)!=Json.Write(job.RouteSnapshot))throw new InvalidOperationException("L'identite d'une mission acceptee est immuable.");
                }
                int number;
                if(!index.Map.TryGetValue(job.Id,out number)){
                    if(index.Map.Count>=MaximumHistory)throw new WebhookException(507,"Le registre des missions est plein. Consultez le Switcher.");
                    number=index.Map.Count/ShardSize;index.Map.Add(job.Id,number);if(number>=index.Shards)index.Shards=number+1;
                }
                var shard=store.ReadRecord<WebhookIndexShard>(ShardFile(number));shard.Rows=shard.Rows??new List<WebhookIndexRow>();shard.Rows.RemoveAll(row=>row.Id==job.Id);shard.Rows.Add(IndexRow(job));
                if(shard.Rows.Count>ShardSize)throw new InvalidOperationException("Le segment du registre des webhooks est plein.");
                job.UpdatedUtc=DateTime.UtcNow.ToString("o");shard.Rows.RemoveAll(row=>row.Id==job.Id);shard.Rows.Add(IndexRow(job));
                MarkDirty(store);store.WriteRecord(JobFile(job.Id),job);store.WriteRecord(ShardFile(number),shard);store.WriteRecord(IndexFile,index);ClearDirty(store);
            }
        }
        internal static FileStream TryJobLease(RelayStore store,string id)
        {RelayStore.MessageId(id);try{return store.Lease("webhook-job-"+id);}catch(IOException){return null;}}
        internal static WebhookJob Submit(RelayStore store,string slug,string bearer,string idempotencyKey,string body)
        {return SubmitConversation(store,slug,bearer,null,idempotencyKey,body);}
        internal static WebhookJob SubmitConversation(RelayStore store,string slug,string bearer,string conversationId,string idempotencyKey,string body)
        {
            if(String.IsNullOrEmpty(idempotencyKey)||idempotencyKey.Length>128||idempotencyKey.Any(c=>c<33||c>126))throw new WebhookException(400,"Idempotency-Key est obligatoire (1 a 128 caracteres ASCII visibles).");
            var payload=ParseBody(body);var options=PortalMessageOptions.FromPayload(payload);using(Admission(store)){
                var route=Authorize(store,slug,bearer);
                WebhookJob root=conversationId==null?null:WebhookChats.RequireRoot(store,route,conversationId,false);
                if((route.PromptPrefix??"").Length+payload["prompt"].Length+payload["context"].Length+1024>24000)throw new WebhookException(413,"Les instructions permanentes, le prompt et le contexte depassent ensemble la capacite d'une mission (24000 caracteres).");
                string id=Hash(route.Id+"\n"+(root==null?"":"chat:"+root.Id+"\n")+idempotencyKey).Substring(0,32);
                string fingerprint=PayloadFingerprint(payload);
                if(File.Exists(Path.Combine(store.Root,JobFile(id)))){var existing=Job(store,id);if(existing.Fingerprint!=fingerprint)throw new WebhookException(409,"Cette cle d'idempotence designe deja un autre contenu.");return existing;}
                if(root!=null)WebhookChats.RequireRoot(store,route,conversationId,true);
                Limit(store,route,true);var jobs=Jobs(store);
                if(root!=null&&jobs.Any(j=>(j.Id==root.Id||j.ConversationId==root.Id)&&IsPending(j)))throw new WebhookException(409,"Cette conversation attend deja une reponse ou une intervention. Attendez sa fin avant d'envoyer un nouveau message.");
                if(jobs.Length>=MaximumHistory)throw new WebhookException(507,"Le registre des missions est plein. Consultez le Switcher.");
                if(jobs.Count(IsPending)>=MaximumPending||jobs.Count(j=>j.RouteId==route.Id&&IsPending(j))>=MaximumPendingPerRoute)throw new WebhookException(429,"La file de missions est pleine.");
                var snapshot=Copy(route);snapshot.SecretHash=null;
                snapshot.Id=ExecutionRouteId(route.Id,null,options);
                if(root!=null)snapshot.ThreadId=root.ThreadId;
                var grant=WebhookHttp.RouteGrant(route);
                if(options!=null){PortalAttachments.Require(store,route.Id,grant,options.Attachments);PortalAttachments.Claim(store,route.Id,grant,options.Attachments);}
                var job=new WebhookJob{Id=id,RouteId=route.Id,ConversationId=root==null?null:root.Id,ExternalId=idempotencyKey,Title=payload["title"],Prompt=payload["prompt"],Context=payload["context"],Options=options,Fingerprint=fingerprint,State="queued",CreatedUtc=DateTime.UtcNow.ToString("o"),RouteSnapshot=snapshot};
                SaveJob(store,job);return job;
            }
        }
        internal static WebhookJob ReadAuthorized(RelayStore store,string slug,string bearer,string id)
        {
            using(Admission(store)){var route=Authorize(store,slug,bearer);Limit(store,route,false);if(!Regex.IsMatch(id??"",@"\A[a-f0-9]{32}\z"))throw new WebhookException(404,"Mission introuvable.");var job=Job(store,id);if(job.RouteId!=route.Id)throw new WebhookException(404,"Mission introuvable.");if(!String.IsNullOrEmpty(job.PortalInstanceId))Portal.ValidateJobRead(store,job);return job;}
        }
        internal static Dictionary<string,string> ParseBody(string body)
        {
            try{if(body==null||Utf8.GetByteCount(body)>MaximumBodyBytes)throw new WebhookException(413,"Le corps JSON depasse 64 Kio.");}catch(EncoderFallbackException){throw new WebhookException(400,"Le corps doit etre encode en UTF-8 valide.");}
            var parser=new Payload(body);var values=parser.Read();string prompt;
            if(!values.TryGetValue("prompt",out prompt)||String.IsNullOrWhiteSpace(prompt)||prompt.Length>MaximumPrompt)throw new WebhookException(400,"Le prompt est obligatoire et limite a 20000 caracteres.");
            if(!values.ContainsKey("title"))values["title"]="Mission externe";
            if(String.IsNullOrWhiteSpace(values["title"])||values["title"].Length>120||values["title"].Any(Char.IsControl))throw new WebhookException(400,"Le titre doit contenir 1 a 120 caracteres.");
            if(!values.ContainsKey("context"))values["context"]="";
            if(values["context"].Length>MaximumPrompt)throw new WebhookException(400,"Le contexte est limite a 20000 caracteres.");
            return values;
        }
        internal static string PayloadFingerprint(IDictionary<string,string> payload)
        {
            var options=PortalMessageOptions.FromPayload(payload);
            // Preserve all old retry keys. New options form part of the exact
            // durable request, so changing an attachment or model cannot resend.
            return options==null?Hash(Json.Write(new[]{payload["title"],payload["prompt"],payload["context"]})):Hash(Json.Write(new[]{payload["title"],payload["prompt"],payload["context"],options.Canonical()}));
        }
        internal static string ExecutionRouteId(string root,string instance,PortalMessageOptions options)
        {
            string basis=String.IsNullOrEmpty(instance)?root:Portal.FrozenRouteId(root,instance);
            // Previous executables do not understand composer selections. Their
            // route checks must fail closed rather than silently dropping files.
            return options==null?basis:Hash(basis+"\ncomposer-v1\n"+options.Canonical()).Substring(0,32);
        }
        internal static object PublicOptions(RelayStore store,WebhookJob job,PortalInstanceGrant grant)
        {
            if(job.Options==null)return null;var value=Json.Obj(Json.Read<object>(job.Options.Canonical()));
            try{value["attachments"]=PortalAttachments.PublicMetadata(store,job.RouteId,grant,job.Options.Attachments);}
            catch(InvalidOperationException){value["attachments"]=(job.Options.Attachments??new List<string>()).Select(id=>new{id=id,name="Piece jointe indisponible",relativePath=(string)null,mimeType="application/octet-stream",size=0}).ToArray();}
            return value;
        }
        // A deliberately small JSON object grammar prevents duplicate or escaped
        // property names from bypassing the payload allow-list on either runtime.
        private sealed class Payload
        {
            private readonly string text;private int at;
            internal Payload(string text){this.text=text;}
            private void Space(){while(at<text.Length&&" \t\r\n".IndexOf(text[at])>=0)at++;}
            private WebhookException Bad(){return new WebhookException(400,"Corps JSON invalide : utilisez title, prompt, context (textes) et options (objet) uniquement.");}
            private void Take(char value){Space();if(at>=text.Length||text[at++]!=value)throw Bad();}
            private string String()
            {
                Space();int start=at;Take('"');bool end=false;
                while(at<text.Length){char c=text[at++];if(c=='"'){end=true;break;}if(c<32)throw Bad();if(c=='\\'){
                    if(at>=text.Length)throw Bad();char escape=text[at++];if(escape=='u'){for(int i=0;i<4;i++){if(at>=text.Length||!Uri.IsHexDigit(text[at++]))throw Bad();}}
                    else if("\"\\/bfnrt".IndexOf(escape)<0)throw Bad();
                }}
                if(!end)throw Bad();try{return Json.Read<string>(text.Substring(start,at-start));}catch(Exception){throw Bad();}
            }
            private string Options()
            {
                Space();int start=at;Take('{');int depth=1;bool quoted=false,escaped=false;
                while(at<text.Length&&depth>0){char c=text[at++];if(quoted){if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='"')quoted=false;continue;}if(c=='"')quoted=true;else if(c=='{'||c=='['){if(++depth>4)throw Bad();}else if(c=='}'||c==']')depth--;}
                if(depth!=0||quoted)throw Bad();return PortalMessageOptions.Parse(text.Substring(start,at-start)).Canonical();
            }
            internal Dictionary<string,string> Read()
            {
                var values=new Dictionary<string,string>(StringComparer.Ordinal);Take('{');Space();if(at<text.Length&&text[at]=='}'){at++;}else while(true){
                    string key=String();if(!new[]{"title","prompt","context","options"}.Contains(key)||values.ContainsKey(key))throw Bad();Take(':');values[key]=key=="options"?Options():String();Space();if(at>=text.Length)throw Bad();char next=text[at++];if(next=='}')break;if(next!=',')throw Bad();
                }
                Space();if(at!=text.Length)throw Bad();return values;
            }
        }
    }
}
