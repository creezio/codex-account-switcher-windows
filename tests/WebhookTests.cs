using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class WebhookTests
{
    private const string Payload="{\"title\":\"Fixture mission\",\"prompt\":\"Produce a fictional receipt only.\",\"context\":\"No native tools.\"}";
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void Reject(int status,Action action)
    {try{action();}catch(WebhookException ex){Assert(ex.StatusCode==status,"Unexpected HTTP status "+ex.StatusCode);return;}throw new Exception("Expected HTTP rejection "+status);}
    private static void Invalid(Action action)
    {try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected local configuration rejection");}
    private sealed class Fixture
    {
        internal readonly RelayStore Store;
        internal WebhookRoute Route;
        internal string Key;
        internal Fixture(string root,string name)
        {
            string folder=Path.Combine(root,"webhook-tests",name),home=Path.Combine(folder,"home"),workspace=Path.Combine(folder,"workspace");
            Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);Store=new RelayStore(Path.Combine(folder,"relay"));
            Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true,Port=47831});
            Route=Webhooks.SaveRoute(Store,new WebhookRoute{Name="Fictional hook",Slug="fixture-hook",InstanceId=new string('a',32),AccountKey="fixture-account",Home=home,Workspace=workspace,Access="desktop",Enabled=true});
            Key=Webhooks.RotateKey(Store,Route.Id);Route=Webhooks.Route(Store,Route.Id);
        }
        internal WebhookJob Submit(string id="fixture-one",string body=Payload){return Webhooks.Submit(Store,Route.Slug,Key,id,body);}
    }
    private static WebhookHttpRequest Parse(string text)
    {using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(text)))return WebhookHttp.ReadRequest(stream);}
    private static string Post(string headers,string body=Payload)
    {return "POST /v1/hooks/fixture-hook/jobs HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\n"+headers+"\r\n"+body;}
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("webhook accepts pinned route and stores no plaintext key",()=>{
            var f=new Fixture(root,"accept");var job=f.Submit();var saved=Webhooks.Route(f.Store,f.Route.Id);
            Assert(job.State=="queued"&&job.RouteSnapshot.InstanceId==f.Route.InstanceId&&job.RouteSnapshot.AccountKey==f.Route.AccountKey,"job lost its local binding");
            Assert(job.RouteSnapshot.Workspace==f.Route.Workspace&&job.RouteSnapshot.SecretHash==null,"snapshot exposed authentication data");
            Assert(saved.SecretHash==Webhooks.Hash(f.Key)&&!Json.Write(saved).Contains(f.Key),"plaintext token persisted");
            byte[] disk=File.ReadAllBytes(Path.Combine(f.Store.Root,"webhook-routes-v1.dpapi"));Assert(!Encoding.UTF8.GetString(disk).Contains(f.Key),"token exposed on disk");
        });
        check("webhook idempotence canonicalizes property order and JSON whitespace",()=>{
            var f=new Fixture(root,"canonical");var first=f.Submit();var again=f.Submit("fixture-one","{ \"context\":\"No native tools.\", \"prompt\":\"Produce a fictional receipt only.\",\"title\":\"Fixture mission\" }");
            Assert(first.Id==again.Id&&Webhooks.Jobs(f.Store).Length==1,"retry created a second mission");
        });
        check("webhook idempotence rejects reused key with different content",()=>{
            var f=new Fixture(root,"conflict");f.Submit();Reject(409,()=>f.Submit("fixture-one","{\"prompt\":\"Different request\"}"));Assert(Webhooks.Jobs(f.Store).Length==1,"conflict mutated queue");
        });
        check("webhook concurrent retries persist exactly one job",()=>{
            var f=new Fixture(root,"concurrent");var ids=new string[8];Parallel.For(0,8,i=>ids[i]=f.Submit().Id);
            Assert(ids.Distinct().Count()==1&&Webhooks.Jobs(f.Store).Length==1,"concurrent delivery duplicated work");
        });
        check("webhook key rotation revokes old token and preserves new one",()=>{
            var f=new Fixture(root,"rotate");string previous=f.Key;f.Key=Webhooks.RotateKey(f.Store,f.Route.Id);
            Reject(401,()=>Webhooks.Submit(f.Store,f.Route.Slug,previous,"old",Payload));f.Submit("new");Assert(f.Key!=previous,"rotation reused token");
        });
        check("webhook disabled broker rejects admission and reads",()=>{
            var f=new Fixture(root,"disabled-broker");var job=f.Submit();Webhooks.SaveConfig(f.Store,new WebhookConfig{Enabled=false});Reject(503,()=>f.Submit("second"));Reject(503,()=>Webhooks.ReadAuthorized(f.Store,f.Route.Slug,f.Key,job.Id));
        });
        check("webhook disabled route rejects admission",()=>{
            var f=new Fixture(root,"disabled-route");f.Route.Enabled=false;f.Route=Webhooks.SaveRoute(f.Store,f.Route);Reject(503,()=>f.Submit());
        });
        check("webhook authentication does not reveal missing routes",()=>{
            var f=new Fixture(root,"auth");Reject(401,()=>Webhooks.Submit(f.Store,f.Route.Slug,"bad","id",Payload));Reject(401,()=>Webhooks.Submit(f.Store,"not-found","bad","id",Payload));Assert(Webhooks.Jobs(f.Store).Length==0,"unauthorized request queued work");
        });
        check("webhook route token cannot read another route's job",()=>{
            var f=new Fixture(root,"cross-route");var job=f.Submit();var second=Json.Read<WebhookRoute>(Json.Write(f.Route));second.Id=null;second.Slug="second-route";second.Revision=0;second=Webhooks.SaveRoute(f.Store,second);string key=Webhooks.RotateKey(f.Store,second.Id);
            Reject(404,()=>Webhooks.ReadAuthorized(f.Store,second.Slug,key,job.Id));
        });
        check("webhook remote payload cannot override local routing or permissions",()=>{
            var f=new Fixture(root,"overrides");foreach(string key in new[]{"instanceId","accountKey","home","workspace","threadId","access","model","permissionProfile","callbackUrl","id"})Reject(400,()=>f.Submit(key,"{\"prompt\":\"Fiction\",\""+key+"\":\"attacker\"}"));
            Assert(Webhooks.Jobs(f.Store).Length==0,"override payload was admitted");
        });
        check("webhook rejects duplicate and escaped duplicate JSON properties",()=>{
            var f=new Fixture(root,"duplicate-json");Reject(400,()=>f.Submit("one","{\"prompt\":\"A\",\"prompt\":\"B\"}"));Reject(400,()=>f.Submit("two","{\"prompt\":\"A\",\"pro\\u006dpt\":\"B\"}"));
        });
        check("webhook rejects malformed JSON and nontext values",()=>{
            foreach(string body in new[]{"[]","{\"prompt\":null}","{\"prompt\":{}}","{\"prompt\":12}","{\"prompt\":\"A\",}","{\"prompt\":\"A\"} trailing","{\"prompt\":\"A\nB\"}"})Reject(400,()=>Webhooks.ParseBody(body));
        });
        check("webhook enforces body prompt and title bounds",()=>{
            Reject(413,()=>Webhooks.ParseBody(new string(' ',65537)));Reject(400,()=>Webhooks.ParseBody(Json.Write(new{prompt=new string('a',20001)})));Reject(400,()=>Webhooks.ParseBody(Json.Write(new{prompt=" ",title="title"})));Reject(400,()=>Webhooks.ParseBody(Json.Write(new{prompt="valid",title=new string('a',121)})));
        });
        check("webhook validates complete relay envelope before accepting work",()=>{
            var f=new Fixture(root,"envelope");f.Route.PromptPrefix=new string('i',2000);f.Route=Webhooks.SaveRoute(f.Store,f.Route);
            var valid=f.Submit("within",Json.Write(new{prompt=new string('p',20000),context=new string('c',976),title=new string('t',120)}));Assert(valid.State=="queued","valid boundary mission was rejected");
            Reject(413,()=>f.Submit("outside",Json.Write(new{prompt=new string('p',20000),context=new string('c',977)})));Assert(Webhooks.Jobs(f.Store).Length==1,"oversized relay envelope was queued");
        });
        check("webhook requires a bounded visible ASCII idempotency key",()=>{
            var f=new Fixture(root,"idempotency-format");foreach(string key in new[]{null,"",new string('a',129),"a b","a\nb","cle-é"})Reject(400,()=>Webhooks.Submit(f.Store,f.Route.Slug,f.Key,key,Payload));
        });
        check("webhook admission rate is bounded while retries remain safe",()=>{
            var f=new Fixture(root,"rate");var first=f.Submit();f.Store.WriteRecord("webhook-rate-"+f.Route.Id+".dpapi",new WebhookRate{Window=DateTime.UtcNow.ToString("yyyyMMddHHmm",System.Globalization.CultureInfo.InvariantCulture),Submissions=60});Reject(429,()=>f.Submit("new"));Assert(f.Submit().Id==first.Id,"safe retry was converted to a new send");
        });
        check("webhook route queue capacity rejects excess work",()=>{
            var f=new Fixture(root,"capacity");var first=f.Submit("capacity-0");for(int i=1;i<100;i++){var job=Json.Read<WebhookJob>(Json.Write(first));job.Id=Guid.NewGuid().ToString("N");job.ExternalId="capacity-"+i;Webhooks.SaveJob(f.Store,job);}
            Reject(429,()=>f.Submit("over-limit"));Assert(Webhooks.Jobs(f.Store).Length==100,"capacity overflow persisted");
        });
        check("webhook route snapshots and job inputs are immutable",()=>{
            var f=new Fixture(root,"immutable");var job=f.Submit();job.Prompt="changed";Invalid(()=>Webhooks.SaveJob(f.Store,job));job=Webhooks.Job(f.Store,job.Id);job.RouteSnapshot.AccountKey="changed";Invalid(()=>Webhooks.SaveJob(f.Store,job));
        });
        check("webhook list index excludes private bodies while detail remains intact",()=>{
            var f=new Fixture(root,"index-private");var job=f.Submit();job.Result=new string('r',10000);Webhooks.SaveJob(f.Store,job);
            var row=Webhooks.Jobs(f.Store).Single();Assert(row.Id==job.Id&&row.Title==job.Title&&row.Prompt==null&&row.Context==null&&row.Result==null&&row.RouteSnapshot==null&&row.Fingerprint==null,"list index retained private bodies");
            string index=Json.Write(f.Store.ReadRecord<WebhookIndexShard>("webhook-index-shard-00-v1.dpapi"));Assert(!index.Contains("\"Prompt\"")&&!index.Contains("\"Context\"")&&!index.Contains("\"Result\"")&&!index.Contains("\"RouteSnapshot\""),"index schema includes private-body fields");
            var detail=Webhooks.Job(f.Store,job.Id);Assert(detail.Prompt==job.Prompt&&detail.Context==job.Context&&detail.Result==job.Result&&detail.RouteSnapshot.AccountKey==f.Route.AccountKey,"index discarded canonical detail");
        });
        check("webhook cached metadata cannot be changed by callers and refreshes on writes",()=>{
            var f=new Fixture(root,"index-cache");var job=f.Submit();var row=Webhooks.Jobs(f.Store).Single();row.State="forged";Assert(Webhooks.Jobs(f.Store).Single().State=="queued","caller contaminated cached metadata");job.State="completed";Webhooks.SaveJob(f.Store,job);Assert(Webhooks.Jobs(f.Store).Single().State=="completed","cached state ignored durable write");
        });
        check("webhook dirty index recovers a canonical job written before interrupted indexing",()=>{
            var f=new Fixture(root,"index-dirty-record");var first=f.Submit();Webhooks.Jobs(f.Store);var later=Json.Read<WebhookJob>(Json.Write(first));later.Id=Guid.NewGuid().ToString("N");later.ExternalId="after-crash";later.State="waiting";
            f.Store.WriteRecord("webhook-index-dirty-v1.dpapi",new{stage="before-record"});f.Store.WriteRecord("webhook-job-"+later.Id+".dpapi",later);
            var rows=Webhooks.Jobs(f.Store);Assert(rows.Length==2&&rows.Single(j=>j.Id==later.Id).State=="waiting"&&rows.All(j=>j.Prompt==null),"dirty rebuild lost an admitted job or loaded bodies into index");Assert(!File.Exists(Path.Combine(f.Store.Root,"webhook-index-dirty-v1.dpapi")),"successful recovery retained dirty marker");
        });
        check("webhook interrupted index marker alone does not invent or duplicate jobs",()=>{
            var f=new Fixture(root,"index-dirty-only");var first=f.Submit();f.Store.WriteRecord("webhook-index-dirty-v1.dpapi",new{stage="before-job"});var rows=Webhooks.Jobs(f.Store);Assert(rows.Length==1&&rows[0].Id==first.Id,"dirty-only recovery changed admitted jobs");
        });
        check("webhook metadata shards stay bounded and recover every canonical record",()=>{
            var f=new Fixture(root,"index-shards");var first=f.Submit();for(int i=1;i<=500;i++){var job=Json.Read<WebhookJob>(Json.Write(first));job.Id=Guid.NewGuid().ToString("N");job.ExternalId="seed-"+i;f.Store.WriteRecord("webhook-job-"+job.Id+".dpapi",job);}f.Store.WriteRecord("webhook-index-dirty-v1.dpapi",new{stage="seed"});
            var rows=Webhooks.Jobs(f.Store);var index=f.Store.ReadRecord<WebhookIndex>("webhook-index-v1.dpapi");Assert(rows.Length==501&&index.Shards==2&&index.Map.Count==501,"sharding lost or duplicated jobs");
            Assert(f.Store.ReadRecord<WebhookIndexShard>("webhook-index-shard-00-v1.dpapi").Rows.Count==500&&f.Store.ReadRecord<WebhookIndexShard>("webhook-index-shard-01-v1.dpapi").Rows.Count==1,"metadata shard exceeded its bound");
            var detail=Webhooks.Job(f.Store,rows[0].Id);detail.State="completed";Webhooks.SaveJob(f.Store,detail);Assert(Webhooks.Jobs(f.Store).Single(j=>j.Id==detail.Id).State=="completed"&&Webhooks.Job(f.Store,detail.Id).Prompt==first.Prompt,"sharded update lost state or body");
        });
        check("webhook local edits retain existing secret and reject stale revision",()=>{
            var f=new Fixture(root,"revision");var stale=Json.Read<WebhookRoute>(Json.Write(f.Route));f.Route.Name="Renamed";f.Route.SecretHash=Webhooks.Hash("forged");var current=Webhooks.SaveRoute(f.Store,f.Route);Assert(current.SecretHash==Webhooks.Hash(f.Key),"local save replaced key without rotation");Invalid(()=>Webhooks.SaveRoute(f.Store,stale));
        });
        check("webhook route validation rejects unsafe targets",()=>{
            var f=new Fixture(root,"target-validation");var route=Json.Read<WebhookRoute>(Json.Write(f.Route));route.Workspace=Path.GetPathRoot(route.Workspace);Invalid(()=>Webhooks.SaveRoute(f.Store,route));route=Json.Read<WebhookRoute>(Json.Write(f.Route));route.ThreadId="bad";Invalid(()=>Webhooks.SaveRoute(f.Store,route));route=Json.Read<WebhookRoute>(Json.Write(f.Route));route.Access="remote-command";Invalid(()=>Webhooks.SaveRoute(f.Store,route));
        });
        check("webhook interrupted sending is uncertain and never dispatched again",()=>{
            var f=new Fixture(root,"recover");var job=f.Submit();job.State="sending";Webhooks.SaveJob(f.Store,job);WebhookWorker.Recover(f.Store);int calls=0;
            WebhookWorker.ProcessOne(f.Store,job.Id,(s,j,t)=>{calls++;return Task.FromResult(0);},CancellationToken.None).GetAwaiter().GetResult();Assert(calls==0&&Webhooks.Job(f.Store,job.Id).State=="uncertain","interrupted send was replayed");
        });
        check("webhook worker resumes queued waiting and monitoring journals",()=>{
            foreach(string state in new[]{"queued","waiting","monitoring","attention"}){var f=new Fixture(root,"resume-"+state);var job=f.Submit();job.State=state;Webhooks.SaveJob(f.Store,job);int calls=0;WebhookWorker.ProcessOne(f.Store,job.Id,(s,j,t)=>{calls++;j.State="completed";j.Result="fixture complete";Webhooks.SaveJob(s,j);return Task.FromResult(0);},CancellationToken.None).GetAwaiter().GetResult();Assert(calls==1&&Webhooks.Job(f.Store,job.Id).State=="completed","durable job was not resumed");}
        });
        check("webhook worker exception cannot silently replay an uncertain enqueue",()=>{
            var f=new Fixture(root,"process-error");var job=f.Submit();int calls=0;Func<RelayStore,WebhookJob,CancellationToken,Task> fail=(s,j,t)=>{calls++;throw new IOException("PRIVATE_DATA");};WebhookWorker.ProcessOne(f.Store,job.Id,fail,CancellationToken.None).GetAwaiter().GetResult();WebhookWorker.ProcessOne(f.Store,job.Id,fail,CancellationToken.None).GetAwaiter().GetResult();job=Webhooks.Job(f.Store,job.Id);Assert(calls==1&&job.State=="uncertain"&&!job.Error.Contains("PRIVATE_DATA"),"uncertain job replayed or leaked exception");
        });
        check("webhook uncertain jobs with durable receipt continue observation without new admission",()=>{
            var f=new Fixture(root,"uncertain-observation");var job=f.Submit();job.State="uncertain";job.RelayJobId=job.Id;Webhooks.SaveJob(f.Store,job);int reads=0;
            WebhookWorker.ProcessOne(f.Store,job.Id,(s,j,t)=>{reads++;j.State="completed";j.Result="Observed existing receipt";Webhooks.SaveJob(s,j);return Task.FromResult(0);},CancellationToken.None).GetAwaiter().GetResult();Assert(reads==1&&Webhooks.Job(f.Store,job.Id).State=="completed","reconciled uncertain receipt stayed stale");
        });
        check("webhook preparation deadline leaves an unsent request waiting",()=>{
            var f=new Fixture(root,"deadline");var job=f.Submit();WebhookWorker.ProcessBounded(f.Store,job.Id,async(s,j,t)=>{await Task.Delay(Timeout.Infinite,t);},30).GetAwaiter().GetResult();job=Webhooks.Job(f.Store,job.Id);Assert(job.State=="waiting"&&String.IsNullOrEmpty(job.RelayJobId),"expired preparation was lost or falsely sent");
        });
        check("webhook worker honors exclusive job ownership",()=>{
            var f=new Fixture(root,"lease");var job=f.Submit();int calls=0;using(var lease=Webhooks.TryJobLease(f.Store,job.Id)){WebhookWorker.ProcessOne(f.Store,job.Id,(s,j,t)=>{calls++;return Task.FromResult(0);},CancellationToken.None).GetAwaiter().GetResult();}Assert(calls==0,"job lease was ignored");
        });
        check("webhook HTTP parser accepts one bounded JSON request",()=>{
            var request=Parse(Post("Authorization: Bearer fixture\r\nIdempotency-Key: one\r\n"));Assert(request.Method=="POST"&&request.Body==Payload&&request.Headers["Idempotency-Key"]=="one","HTTP parser corrupted payload");
        });
        check("webhook HTTP rejects duplicate headers and transfer encoding",()=>{
            Reject(400,()=>Parse(Post("Content-Length: 1\r\n")));Reject(400,()=>Parse(Post("Transfer-Encoding: chunked\r\n")));Reject(400,()=>Parse(Post("Authorization: A\r\nauthorization: B\r\n")));Reject(400,()=>Parse(Post("Expect: 100-continue\r\n")));
        });
        check("webhook HTTP rejects query tokens and ambiguous request lines",()=>{
            Reject(400,()=>Parse("GET /health?token=secret HTTP/1.1\r\nHost: localhost\r\n\r\n"));Reject(400,()=>Parse("GET /%68ealth HTTP/1.1\r\nHost: localhost\r\n\r\n"));Reject(400,()=>Parse("GET http://localhost/health HTTP/1.1\r\nHost: localhost\r\n\r\n"));Reject(400,()=>Parse("GET /health HTTP/1.0\r\n\r\n"));
        });
        check("webhook HTTP rejects missing length invalid content type and truncation",()=>{
            Reject(411,()=>Parse("POST /v1/hooks/fixture-hook/jobs HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n\r\n"));Reject(415,()=>Parse(Post("").Replace("application/json","text/plain")));Reject(400,()=>Parse(Post("").Substring(0,Post("").Length-1)));Reject(400,()=>Parse("GET /health HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1\r\n\r\nx"));
        });
        check("webhook HTTP rejects non-UTF8 input and excessive headers",()=>{
            string prefix="POST /v1/hooks/fixture-hook/jobs HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 1\r\n\r\n";byte[] valid=Encoding.ASCII.GetBytes(prefix);byte[] bad=new byte[valid.Length+1];Buffer.BlockCopy(valid,0,bad,0,valid.Length);bad[bad.Length-1]=255;
            Reject(400,()=>{using(var stream=new MemoryStream(bad))WebhookHttp.ReadRequest(stream);});Reject(431,()=>Parse("GET /health HTTP/1.1\r\nHost: localhost\r\nX-Filler: "+new string('a',16400)+"\r\n\r\n"));
        });
        check("webhook HTTP health is generic and successful jobs disclose no binding",()=>{
            var f=new Fixture(root,"http-contract");var health=WebhookHttp.Handle(f.Store,Parse("GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"));Assert(health.Status==200&&health.Body=="{\"status\":\"ok\"}","health exposed private metadata");
            var response=WebhookHttp.Handle(f.Store,Parse(Post("Authorization: Bearer "+f.Key+"\r\nIdempotency-Key: http-one\r\n")));Assert(response.Status==202&&response.Location.StartsWith("/v1/hooks/fixture-hook/jobs/",StringComparison.Ordinal),"accepted request lacks durable result URL");Assert(!response.Body.Contains(f.Key)&&!response.Body.Contains(f.Route.AccountKey)&&!response.Body.Contains(f.Route.Home)&&!response.Body.Contains("Produce a fictional"),"response exposed secret input or binding");
        });
        check("webhook real TCP listener binds loopback and returns accepted then status",()=>{
            var f=new Fixture(root,"tcp");using(var server=new WebhookHttp(f.Store,0)){
                server.Start();string response=Exchange(server.Port,Post("Authorization: Bearer "+f.Key+"\r\nIdempotency-Key: tcp-one\r\n"));Assert(response.StartsWith("HTTP/1.1 202",StringComparison.Ordinal),"TCP POST was not accepted");var job=Webhooks.Jobs(f.Store).Single();string result=Exchange(server.Port,"GET /v1/hooks/fixture-hook/jobs/"+job.Id+" HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer "+f.Key+"\r\n\r\n");Assert(result.StartsWith("HTTP/1.1 200",StringComparison.Ordinal)&&result.Contains("queued"),"TCP polling failed");Assert(!result.Contains("Access-Control-Allow-Origin"),"implicit cross-origin access enabled");
            }
        });
    }
    private static string Exchange(int port,string request)
    {
        using(var client=new TcpClient()){client.Connect(IPAddress.Loopback,port);client.ReceiveTimeout=5000;using(var stream=client.GetStream()){byte[] bytes=Encoding.UTF8.GetBytes(request);stream.Write(bytes,0,bytes.Length);using(var reader=new StreamReader(stream,Encoding.UTF8))return reader.ReadToEnd();}}
    }
}
