using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudBackendTests
{
    private const string ConfigurationId="11111111-2222-3333-4444-555555555555~asenvcfg_fixture";
    private const string VersionId="11111111-2222-3333-4444-555555555555~cecfgver_fixture";
    private const string DraftId="11111111-2222-3333-4444-555555555555~draft_fixture";
    private const string Secret="BACKEND_PRIVATE_SECRET_MUST_NOT_ESCAPE";
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action)
    {
        try{action();}catch(Exception error){return error;}
        throw new Exception("Expected request to be rejected");
    }
    private static string Encode(object value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static AuthIdentity Identity(string account="workspace-a",string user="user-a",long? expires=null)
    {
        long expiry=expires??DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600;
        string access="e30."+Encode(new Dictionary<string,object>{{"exp",expiry},{"https://api.openai.com/auth",new{chatgpt_account_id=account,chatgpt_user_id=user}}})+".fixture";
        return new AuthIdentity{Key=account+":"+user,AccountId=account,UserId="different-id-token-subject",Email=user+"@example.test",AccessToken=access};
    }
    private static HttpResponseMessage Response(HttpStatusCode status,string json)
    {return new HttpResponseMessage(status){Content=new StringContent(json,Encoding.UTF8,"application/json")};}
    private static string Configuration(string name="Lab")
    {
        return Json.Write(new{id=ConfigurationId,name=name,is_owner=true,share_settings="private",status="ready",version_id=VersionId,version_revision=2,latest_ready_version_id=VersionId,repositories=new[]{new{repository_id="github/123",@ref="refs/heads/main",depth=1}},network_policy=new{type="disabled"},unknown_secret=Secret});
    }
    private static string DraftConfiguration()
    {
        var configuration=Json.Obj(Json.Read<object>(Configuration()));
        configuration["draft"]=new{id=DraftId,base_version_id=VersionId,revision=3,repositories=new[]{new{repository_id="github/123",@ref="refs/heads/main",depth=1}},network_policy=new{type="disabled"},install_script="python -V"};
        return Json.Write(configuration);
    }
    private sealed class Request
    {
        internal string Method,Uri,Body,Authorization,Account,Product;
    }
    private sealed class FakeHandler:HttpMessageHandler
    {
        internal readonly List<Request> Requests=new List<Request>();
        internal Func<Request,CancellationToken,Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken token)
        {
            var request=new Request{Method=message.Method.Method,Uri=message.RequestUri.AbsoluteUri,Body=message.Content==null?null:message.Content.ReadAsStringAsync().GetAwaiter().GetResult(),Authorization=message.Headers.Authorization==null?null:message.Headers.Authorization.ToString(),Account=Header(message,"ChatGPT-Account-Id"),Product=Header(message,"X-OpenAI-Product-Sku")};
            Requests.Add(request);
            return Reply==null?Task.FromResult(Response(HttpStatusCode.OK,"{\"data\":[],\"next_cursor\":null}")):Reply(request,token);
        }
        private static string Header(HttpRequestMessage message,string name){IEnumerable<string> values;return message.Headers.TryGetValues(name,out values)?String.Join(",",values):null;}
    }
    private sealed class Fixture:IDisposable
    {
        internal AuthIdentity Current=Identity();
        internal readonly FakeHandler Handler=new FakeHandler();
        private readonly HttpClient client;
        internal readonly CloudBackend Backend;
        internal Fixture(){client=new HttpClient(Handler);Backend=new CloudBackend(client,()=>Current,CloudBackend.Capture(Current));}
        public void Dispose(){Backend.Dispose();client.Dispose();}
        internal void Reply(string json,HttpStatusCode status=HttpStatusCode.OK){Handler.Reply=(r,t)=>Task.FromResult(Response(status,json));}
        internal CloudConfiguration Get(){return Backend.Get(ConfigurationId,CancellationToken.None).GetAwaiter().GetResult();}
    }
    private static CloudConfigurationCreate NewConfiguration()
    {
        return new CloudConfigurationCreate{Name="Fixture privée",ShareSettings="private",StartOnboarding=false,Repositories=new List<CloudRepository>{new CloudRepository{RepositoryId="github/123",Ref="refs/heads/main",Depth=1}},NetworkPolicy=new CloudNetworkPolicy{Type="disabled",Presets=new List<string>(),EgressHosts=new List<string>()}};
    }
    public static void RunAll(Action<string,Action> check)
    {
        check("cloud binding captures access-token workspace and user without credentials",()=>{
            var identity=Identity();var binding=CloudBackend.Capture(identity);
            Assert(binding.AccountKey==identity.Key&&binding.AccountId=="workspace-a"&&binding.UserId=="user-a","wrong backend principal");
            Assert(!Json.Write(binding).Contains(identity.AccessToken)&&!Json.Write(binding).Contains("fixture"),"binding serializes bearer credential");
        });
        check("cloud rejects expired, absent expiry and inconsistent access-token accounts",()=>{
            Reject(()=>CloudBackend.Capture(Identity(expires:DateTimeOffset.UtcNow.ToUnixTimeSeconds()-1)));
            var missing=Identity();missing.AccessToken="e30."+Encode(new Dictionary<string,object>{{"https://api.openai.com/auth",new{chatgpt_account_id="workspace-a",chatgpt_user_id="user-a"}}})+".fixture";Reject(()=>CloudBackend.Capture(missing));
            var wrong=Identity();wrong.AccountId="another-workspace";Reject(()=>CloudBackend.Capture(wrong));
        });
        check("cloud request fixes backend host and binds bearer, workspace and product",()=>{
            using(var f=new Fixture()){
                f.Backend.List("user",null,CancellationToken.None).GetAwaiter().GetResult();var request=f.Handler.Requests.Single();
                Assert(request.Method=="GET"&&request.Uri.StartsWith("https://codex-cloud-backend.chatgpt.com/v1/",StringComparison.Ordinal),"untrusted destination");
                Assert(request.Authorization=="Bearer "+f.Current.AccessToken&&request.Account==f.Current.AccountId&&request.Product=="codex","missing account-bound headers");
                Assert(request.Body==null&&!request.Uri.Contains(f.Current.AccessToken),"credential serialized as input");
            }
        });
        check("cloud refuses changed workspace or user before sending HTTP",()=>{
            using(var f=new Fixture()){f.Current=Identity("workspace-b");Reject(()=>f.Get());Assert(f.Handler.Requests.Count==0,"wrong workspace contacted");}
            using(var f=new Fixture()){f.Current=Identity("workspace-a","user-b");Reject(()=>f.Get());Assert(f.Handler.Requests.Count==0,"different user contacted");}
        });
        check("cloud permits fresh credentials for the same bound principal",()=>{
            using(var f=new Fixture()){
                f.Current=Identity(expires:DateTimeOffset.UtcNow.ToUnixTimeSeconds()+7200);f.Reply(Configuration());
                Assert(f.Get().Id==ConfigurationId&&f.Handler.Requests.Single().Authorization=="Bearer "+f.Current.AccessToken,"same-account token refresh rejected");
            }
        });
        check("cloud validates list scope before network and escapes opaque cursors",()=>{
            using(var f=new Fixture()){
                Reject(()=>f.Backend.List("user&scope=workspace",null,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Handler.Requests.Count==0,"scope injection sent");
                f.Backend.List("user","next /+&?=cursor",CancellationToken.None).GetAwaiter().GetResult();var uri=f.Handler.Requests.Single().Uri;
                Assert(uri.Contains("scope=user")&&!uri.Contains("next /+")&&uri.Contains("%26")&&uri.Contains("%3F"),"cursor alters query");
            }
        });
        check("cloud accepts native composite IDs and rejects path or query injection",()=>{
            using(var f=new Fixture()){
                f.Reply(Configuration());Assert(f.Get().Id==ConfigurationId,"composite id rejected");
                foreach(var id in new[]{"../environment-configs","id?scope=workspace","id/versions","https://evil.example/x","id%2fother","id\r\nX-Injected: true"}){
                    int count=f.Handler.Requests.Count;Reject(()=>f.Backend.Get(id,CancellationToken.None).GetAwaiter().GetResult());Assert(f.Handler.Requests.Count==count,"unsafe id sent");
                }
            }
        });
        check("cloud parses configuration metadata and discards unknown response fields",()=>{
            using(var f=new Fixture()){
                f.Reply(Configuration());var configuration=f.Get();
                Assert(configuration.Name=="Lab"&&configuration.VersionId==VersionId&&configuration.VersionRevision==2&&configuration.IsOwner==true,"configuration metadata missing");
                Assert(!Json.Write(configuration).Contains(Secret),"unknown backend secret retained");
            }
        });
        check("cloud lists server configuration IDs and cursor without inventing ownership",()=>{
            using(var f=new Fixture()){
                var configuration=Json.Obj(Json.Read<object>(Configuration("Shared")));configuration.Remove("is_owner");
                f.Reply(Json.Write(new{data=new[]{configuration},next_cursor="cursor-2"}));
                var page=f.Backend.List("workspace",null,CancellationToken.None).GetAwaiter().GetResult();
                Assert(page.Data.Count==1&&page.Data[0].Id==ConfigurationId&&page.Data[0].IsOwner==null&&page.NextCursor=="cursor-2","list metadata fabricated or lost");
            }
        });
        check("cloud refuses redirects without replaying bearer to a second host",()=>{
            using(var f=new Fixture()){
                f.Handler.Reply=(r,t)=>{var response=Response(HttpStatusCode.TemporaryRedirect,"{}");response.Headers.Location=new Uri("https://evil.example/steal");return Task.FromResult(response);};
                var error=Reject(()=>f.Get()) as CloudBackendException;Assert(error!=null&&error.Code=="redirect"&&f.Handler.Requests.Count==1&&!error.ToString().Contains(f.Current.AccessToken),"redirect followed or bearer leaked");
            }
        });
        check("cloud HTTP errors preserve status and redact backend message and bearer",()=>{
            foreach(var status in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,(HttpStatusCode)429,HttpStatusCode.InternalServerError})using(var f=new Fixture()){
                f.Reply(Json.Write(new{error=new{code="private_backend_code",message=Secret+f.Current.AccessToken}}),status);
                var error=Reject(()=>f.Get()) as CloudBackendException;
                Assert(error!=null&&error.HttpStatus==(int)status,"HTTP status lost");
                Assert(!error.ToString().Contains(Secret)&&!error.ToString().Contains(f.Current.AccessToken),"server error leaked credentials");
                Assert(f.Handler.Requests.Count==1,"error automatically retried");
            }
        });
        check("cloud rejects a response after the account changed during a read",()=>{
            using(var f=new Fixture()){
                f.Handler.Reply=(r,t)=>{f.Current=Identity("workspace-b");return Task.FromResult(Response(HttpStatusCode.OK,Configuration()));};
                Reject(()=>f.Get());Assert(f.Handler.Requests.Count==1,"identity change caused extra request");
            }
        });
        check("cloud create sends an explicitly private repository-scoped payload",()=>{
            using(var f=new Fixture()){
                f.Reply(Configuration("Fixture privée"));var created=f.Backend.Create(NewConfiguration(),CancellationToken.None).GetAwaiter().GetResult();var request=f.Handler.Requests.Single();
                Assert(request.Method=="POST"&&created.Id==ConfigurationId,"create not confirmed");
                var body=Json.Read<object>(request.Body);Assert(Json.Str(Json.Get(body,"share_settings"))=="private","create broadens sharing");
                Assert(!request.Body.Contains(f.Current.AccessToken)&&request.Body.Contains("github/123")&&request.Body.Contains("refs/heads/main"),"repository scope or token boundary missing");
            }
        });
        check("cloud rejects unsafe create scope and missing repository identity before sending",()=>{
            using(var f=new Fixture()){
                var broad=NewConfiguration();broad.ShareSettings="public";Reject(()=>f.Backend.Create(broad,CancellationToken.None).GetAwaiter().GetResult());
                var noRepository=NewConfiguration();noRepository.Repositories[0].RepositoryId="";Reject(()=>f.Backend.Create(noRepository,CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Handler.Requests.Count==0,"invalid create reached backend");
            }
        });
        check("cloud uncertain create timeout is surfaced once without resubmission",()=>{
            using(var f=new Fixture()){
                f.Handler.Reply=(r,t)=>{throw new TaskCanceledException("transport "+Secret);};
                var error=Reject(()=>f.Backend.Create(NewConfiguration(),CancellationToken.None).GetAwaiter().GetResult());
                Assert(error is CloudBackendUncertainException&&f.Handler.Requests.Count==1,"uncertain create not recognized or replayed");
                Assert(!error.ToString().Contains(Secret),"transport exception leaks backend data");
            }
        });
        check("cloud successful mutation with malformed result stays uncertain",()=>{
            using(var f=new Fixture()){
                f.Reply("{\"access_token\":\""+Secret+"\"}");
                var error=Reject(()=>f.Backend.Create(NewConfiguration(),CancellationToken.None).GetAwaiter().GetResult());
                Assert(error is CloudBackendUncertainException&&f.Handler.Requests.Count==1&&!error.ToString().Contains(Secret),"ambiguous mutation treated as safe failure");
            }
        });
        check("cloud account change after a mutation is uncertain and never automatically replayed",()=>{
            using(var f=new Fixture()){
                f.Handler.Reply=(r,t)=>{f.Current=Identity("workspace-b");return Task.FromResult(Response(HttpStatusCode.OK,Configuration()));};
                var error=Reject(()=>f.Backend.Create(NewConfiguration(),CancellationToken.None).GetAwaiter().GetResult());
                Assert(error is CloudBackendUncertainException&&f.Handler.Requests.Count==1,"post-write account race misclassified");
            }
        });
        check("cloud rename sends only a name and cannot change sharing or repository access",()=>{
            using(var f=new Fixture()){
                f.Reply(Configuration("Nom changé"));var result=f.Backend.Rename(ConfigurationId,"Nom changé",CancellationToken.None).GetAwaiter().GetResult();
                var request=f.Handler.Requests.Single();var body=Json.Obj(Json.Read<object>(request.Body));
                Assert(request.Method=="PATCH"&&result.Name=="Nom changé"&&body.Count==1&&Json.Str(Json.Get(body,"name"))=="Nom changé","rename changed more than display name");
            }
        });
        check("cloud invalid draft IDs are refused before mutation",()=>{
            using(var f=new Fixture()){
                Reject(()=>f.Backend.PatchDraft(ConfigurationId,"draft/other",new CloudDraftPatch{BaseVersionId=VersionId,ExpectedRevision=2,InstallScript="python -V"},CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Handler.Requests.Count==0,"unsafe draft path sent");
            }
        });
        check("cloud operation state and kind are read without backend failure text",()=>{
            using(var f=new Fixture()){
                f.Reply(Json.Write(new{id="operation_fixture",kind="APPROVE_ENVIRONMENT_CONFIG_DRAFT",environment_id="environment_fixture",state="FAILED",failure=new{code="BOOT_FAILED",message=Secret}}));
                var operation=f.Backend.ReadOperation("operation_fixture",CancellationToken.None).GetAwaiter().GetResult();
                Assert(operation.Kind=="APPROVE_ENVIRONMENT_CONFIG_DRAFT"&&operation.State=="FAILED","operation progress lost");
                Assert(!Json.Write(operation).Contains(Secret),"backend failure text retained");
            }
        });
        check("cloud draft changes carry the exact base version and optimistic revision",()=>{
            using(var f=new Fixture()){
                f.Reply(DraftConfiguration());var result=f.Backend.PatchDraft(ConfigurationId,DraftId,new CloudDraftPatch{BaseVersionId=VersionId,ExpectedRevision=2,InstallScript="python -V"},CancellationToken.None).GetAwaiter().GetResult();
                var request=f.Handler.Requests.Single();var body=Json.Obj(Json.Read<object>(request.Body));
                Assert(request.Method=="PATCH"&&request.Uri.EndsWith("/drafts/"+DraftId,StringComparison.Ordinal)&&result.Draft.Id==DraftId&&result.Draft.Revision==3,"wrong draft patched");
                Assert(Json.Str(Json.Get(body,"base_version_id"))==VersionId&&Json.Number(Json.Get(body,"expected_revision"))==2&&body.Count==3,"draft concurrency contract changed");
                Assert(!body.ContainsKey("share_settings")&&!body.ContainsKey("name"),"draft unexpectedly changed display or sharing");
            }
        });
        check("cloud rejects a different configuration or draft returned for the request",()=>{
            using(var f=new Fixture()){
                var configuration=Json.Obj(Json.Read<object>(Configuration()));configuration["id"]="other_configuration";f.Reply(Json.Write(configuration));Reject(()=>f.Get());
                f.Reply(Configuration());Reject(()=>f.Backend.Get(ConfigurationId,DraftId,CancellationToken.None).GetAwaiter().GetResult());
            }
        });
        check("cloud creates a draft without starting a chat unless requested",()=>{
            using(var f=new Fixture()){
                f.Reply(Json.Write(new{draft_id=DraftId,environment_id="environment_fixture",thread_id=(string)null}));
                var result=f.Backend.CreateDraft(ConfigurationId,false,CancellationToken.None).GetAwaiter().GetResult();
                var request=f.Handler.Requests.Single();var body=Json.Obj(Json.Read<object>(request.Body));
                Assert(result.DraftId==DraftId&&result.ThreadId==null&&body.Count==1&&Json.Get(body,"start_thread") is bool&&!(bool)Json.Get(body,"start_thread"),"draft unexpectedly started a conversation");
            }
        });
        check("cloud approval preserves idempotency key and expected draft revision",()=>{
            using(var f=new Fixture()){
                f.Reply(Json.Write(new{id="operation_fixture",kind="APPROVE_ENVIRONMENT_CONFIG_DRAFT",environment_id="environment_fixture",state="PENDING"}));
                var operation=f.Backend.BeginApproval(ConfigurationId,DraftId,3,"fixture-approval-request",CancellationToken.None).GetAwaiter().GetResult();
                var request=f.Handler.Requests.Single();var body=Json.Obj(Json.Read<object>(request.Body));
                Assert(operation.State=="PENDING"&&request.Method=="POST"&&request.Uri.EndsWith("/approve/begin",StringComparison.Ordinal),"approval did not return pending operation");
                Assert(body.Count==2&&Json.Number(Json.Get(body,"expected_revision"))==3&&Json.Str(Json.Get(body,"idempotency_key"))=="fixture-approval-request","approval concurrency or deduplication key missing");
            }
        });
        check("cloud rejects unknown operation states, kinds and substituted operation IDs",()=>{
            foreach(var value in new[]{new{id="operation_fixture",kind="DELETE_CONFIGURATION",environment_id="environment_fixture",state="SUCCEEDED"},new{id="operation_fixture",kind="APPROVE_ENVIRONMENT_CONFIG_DRAFT",environment_id="environment_fixture",state="UNKNOWN"},new{id="other_operation",kind="APPROVE_ENVIRONMENT_CONFIG_DRAFT",environment_id="environment_fixture",state="SUCCEEDED"}})using(var f=new Fixture()){
                f.Reply(Json.Write(value));Reject(()=>f.Backend.ReadOperation("operation_fixture",CancellationToken.None).GetAwaiter().GetResult());Assert(f.Handler.Requests.Count==1,"invalid operation retried");
            }
        });
    }
}
