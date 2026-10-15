using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudGitHubTests
{
    private const string Secret="PRIVATE_GITHUB_TOKEN_AND_URL_MUST_NOT_ESCAPE";
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected GitHub catalogue refusal");}
    private static string Encode(object value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static AuthIdentity Identity(string account="account-a",string revision="old")
    {return new AuthIdentity{Key=account+":user-a",AccountId=account,AccessToken="e30."+Encode(new Dictionary<string,object>{{"exp",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600},{"https://api.openai.com/auth",new{chatgpt_account_id=account,chatgpt_user_id="user-a"}},{"revision",revision}})+".fixture"};}
    private static object Repository(string id="repo_fixture",bool permissions=true)
    {
        var value=new Dictionary<string,object>{{"id",id},{"repository_full_name","fixture/lab"},{"default_branch","main"},{"archived",false},{"clone_url","https://"+Secret+"@github.com/fixture/lab.git"},{"access_token",Secret}};
        if(permissions)value["permissions"]=new{push=true};return value;
    }
    private sealed class Request
    {internal string Method,Uri,Authorization,Account,Cookie;}
    private sealed class Handler:HttpMessageHandler
    {
        internal readonly List<Request> Requests=new List<Request>();
        internal Func<Request,Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken token)
        {
            var request=new Request{Method=message.Method.Method,Uri=message.RequestUri.AbsoluteUri,Authorization=message.Headers.Authorization==null?null:message.Headers.Authorization.ToString(),Account=Header(message,"ChatGPT-Account-Id"),Cookie=Header(message,"Cookie")};Requests.Add(request);
            return Reply(request);
        }
        private static string Header(HttpRequestMessage message,string key){IEnumerable<string> values;return message.Headers.TryGetValues(key,out values)?String.Join(",",values):null;}
    }
    private sealed class Fixture:IDisposable
    {
        internal AuthIdentity Current=Identity();
        internal readonly Handler Transport=new Handler();
        internal readonly CloudGitHubBackend Backend;
        private readonly HttpClient client;
        internal Fixture(){client=new HttpClient(Transport);Backend=new CloudGitHubBackend(client,()=>Current,CloudBackend.Capture(Current));Reply(new{repositories=new object[0]});}
        internal void Reply(object value,HttpStatusCode status=HttpStatusCode.OK){Transport.Reply=r=>Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(Json.Write(value),Encoding.UTF8,"application/json")});}
        internal CloudGitHubRepositoryPage List(int page=1){return Backend.ListRepositories(page,CancellationToken.None).GetAwaiter().GetResult();}
        public void Dispose(){Backend.Dispose();client.Dispose();}
    }
    public static void RunAll(Action<string,Action> check)
    {
        check("cloud GitHub catalogue uses its bound account, HTTPS and GET without browser cookies",()=>{
            using(var f=new Fixture()){
                f.List();var request=f.Transport.Requests.Single();Assert(request.Method=="GET"&&request.Uri.StartsWith("https://chatgpt.com/backend-api/wham/github/list-repositories?",StringComparison.Ordinal),"catalogue destination changed");
                Assert(request.Account==f.Current.AccountId&&request.Authorization=="Bearer "+f.Current.AccessToken&&request.Cookie==null&&!request.Uri.Contains(f.Current.AccessToken),"GitHub catalogue account boundary lost");
            }
        });
        check("cloud GitHub repository metadata drops clone URLs, tokens and unknown backend fields",()=>{
            using(var f=new Fixture()){
                f.Reply(Repository());var repository=f.Backend.GetRepository("repo_fixture",CancellationToken.None).GetAwaiter().GetResult();
                Assert(repository.Id=="repo_fixture"&&repository.FullName=="fixture/lab"&&repository.DefaultBranch=="main"&&repository.CanWrite==true&&repository.Archived==false,"repository metadata lost");
                Assert(!Json.Write(repository).Contains(Secret)&&!Json.Write(repository).Contains("clone_url"),"repository secret or credential URL retained");
            }
        });
        check("cloud GitHub does not invent push access when permissions are unavailable",()=>{
            using(var f=new Fixture()){
                f.Reply(Repository(permissions:false));Assert(f.Backend.GetRepository("repo_fixture",CancellationToken.None).GetAwaiter().GetResult().CanWrite==null,"unknown push permission treated as granted");
                var denied=Json.Obj(Repository());denied["permissions"]=new{push=false};f.Reply(denied);Assert(f.Backend.GetRepository("repo_fixture",CancellationToken.None).GetAwaiter().GetResult().CanWrite==false,"read-only repository treated as writable");
            }
        });
        check("cloud GitHub pagination distinguishes a short page from an unknown full page",()=>{
            using(var f=new Fixture()){
                f.Reply(new{repositories=Enumerable.Range(0,20).Select(i=>Repository("repo_"+i)).ToArray()});var full=f.List(2);
                Assert(full.Data.Count==20&&full.Page==2&&full.HasNextPage==null,"full page invented authoritative next-page state");
                f.Reply(new{repositories=new[]{Repository()}});Assert(f.List().HasNextPage==false,"short page should end catalogue");
            }
        });
        check("cloud GitHub rejects unsafe connection selectors and pagination before network",()=>{
            using(var f=new Fixture()){
                Reject(()=>f.List(0));Reject(()=>f.List(501));
                Reject(()=>f.Backend.ListRepositories(1,"id&link_id=other",null,CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>f.Backend.ListRepositories(1,CloudGitHubBackend.DefaultConnectorId,"../other",CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>f.Backend.GetRepository("repo/other",CancellationToken.None).GetAwaiter().GetResult());Assert(f.Transport.Requests.Count==0,"unsafe catalogue selector reached backend");
            }
        });
        check("cloud GitHub exact repository reads reject substituted IDs and invalid names",()=>{
            using(var f=new Fixture()){
                f.Reply(Repository("repo_other"));Reject(()=>f.Backend.GetRepository("repo_fixture",CancellationToken.None).GetAwaiter().GetResult());
                var invalid=Json.Obj(Repository());invalid["repository_full_name"]="https://evil.example/lab";f.Reply(invalid);Reject(()=>f.Backend.GetRepository("repo_fixture",CancellationToken.None).GetAwaiter().GetResult());
            }
        });
        check("cloud GitHub installation metadata preserves memberships without exposing installation URLs",()=>{
            using(var f=new Fixture()){
                f.Reply(new{memberships=new[]{new{id=123,account_login="fixture",account_type="User"}},other=new[]{new{id="456",account_login="fixture-org",account_type="Organization"}},likely_on_old_access_token=true,app_installation_url="https://"+Secret+".example/install"});
                var result=f.Backend.ListInstallations(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.ConnectionVerified&&result.LikelyOnOldAccessToken==true&&result.Data.Count==2&&result.Data[0].Id=="123"&&result.Data[0].IsMembership&&!result.Data[1].IsMembership,"installation membership metadata changed");
                Assert(!Json.Write(result).Contains(Secret),"installation link retained");
            }
        });
        check("cloud GitHub refuses redirect responses without using a browser or another destination",()=>{
            using(var f=new Fixture()){
                f.Transport.Reply=r=>{var response=new HttpResponseMessage(HttpStatusCode.Redirect){Content=new StringContent("{}")} ;response.Headers.Location=new Uri("https://evil.example/");return Task.FromResult(response);};
                var error=Reject(()=>f.List()) as CloudBackendException;Assert(error!=null&&error.Code=="redirect"&&f.Transport.Requests.Count==1,"catalogue redirect followed");
            }
        });
        check("cloud GitHub distinguishes an HTML edge challenge without copying its private page",()=>{
            using(var f=new Fixture()){
                f.Transport.Reply=r=>{var response=new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("<html>"+Secret+"</html>",Encoding.UTF8,"text/html")};response.Headers.TryAddWithoutValidation("cf-mitigated","challenge");return Task.FromResult(response);};
                var error=Reject(()=>f.List()) as CloudBackendException;
                Assert(error!=null&&error.Code=="unexpected-html"&&error.HttpStatus==403&&error.ResponseMediaType=="text/html"&&error.EdgeChallenge&&!error.ToString().Contains(Secret),"edge challenge misclassified or raw HTML leaked");
            }
        });
        check("cloud GitHub HTTP errors expose status while redacting server credentials and messages",()=>{
            foreach(var status in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,(HttpStatusCode)429})using(var f=new Fixture()){
                f.Reply(new{error=new{message=Secret,token=f.Current.AccessToken}},status);var error=Reject(()=>f.List()) as CloudBackendException;
                Assert(error!=null&&error.HttpStatus==(int)status&&!error.ToString().Contains(Secret)&&!error.ToString().Contains(f.Current.AccessToken)&&f.Transport.Requests.Count==1,"HTTP diagnostic leaks server body or silently retries");
            }
        });
        check("cloud GitHub validates the selected account before and after reading metadata",()=>{
            using(var f=new Fixture()){
                f.Current=Identity("account-b");Reject(()=>f.List());Assert(f.Transport.Requests.Count==0,"different account sent a request");
            }
            using(var f=new Fixture()){
                f.Transport.Reply=r=>{f.Current=Identity("account-b");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Json.Write(new{repositories=new[]{Repository()}}))});};
                var error=Reject(()=>f.List()) as CloudBackendException;Assert(error!=null&&error.Code=="authentication"&&f.Transport.Requests.Count==1,"post-read account change returned metadata");
            }
        });
        check("cloud GitHub renews a rejected read once using the same account and never retries other failures",()=>{
            using(var f=new Fixture()){
                int refreshes=0;string old=f.Current.AccessToken;
                f.Backend.RefreshReadAuthentication=token=>{refreshes++;f.Current=Identity(revision:"renewed");};
                f.Transport.Reply=r=>Task.FromResult(new HttpResponseMessage(f.Transport.Requests.Count==1?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=new StringContent("{\"repositories\":[]}")});
                f.List();Assert(refreshes==1&&f.Transport.Requests.Count==2&&f.Transport.Requests.All(r=>r.Method=="GET"&&r.Account==f.Current.AccountId)&&f.Transport.Requests[0].Authorization=="Bearer "+old&&f.Transport.Requests[1].Authorization=="Bearer "+f.Current.AccessToken,"renewed catalogue read lost account pinning or repeated a mutation");
            }
            using(var f=new Fixture()){
                int refreshes=0;f.Backend.RefreshReadAuthentication=token=>refreshes++;f.Reply(new{},HttpStatusCode.Unauthorized);
                Reject(()=>f.List());Assert(refreshes==1&&f.Transport.Requests.Count==2,"catalogue authentication retried indefinitely");
            }
            using(var f=new Fixture()){
                int refreshes=0;f.Backend.RefreshReadAuthentication=token=>refreshes++;f.Reply(new{},HttpStatusCode.Forbidden);
                Reject(()=>f.List());Assert(refreshes==0&&f.Transport.Requests.Count==1,"catalogue retried a non-authentication refusal");
            }
        });
        check("cloud GitHub refuses a different account during rejected-read renewal before retrying",()=>{
            using(var f=new Fixture()){
                f.Backend.RefreshReadAuthentication=token=>f.Current=Identity("account-b");f.Reply(new{},HttpStatusCode.Unauthorized);
                var error=Reject(()=>f.List()) as CloudBackendException;Assert(error!=null&&error.Code=="authentication"&&f.Transport.Requests.Count==1,"catalogue renewal sent a request under another account");
            }
        });
        check("cloud GitHub cancellation and timeouts remain read-only without retries",()=>{
            using(var f=new Fixture())using(var cancel=new CancellationTokenSource()){
                cancel.Cancel();Reject(()=>f.Backend.ListRepositories(1,cancel.Token).GetAwaiter().GetResult());Assert(f.Transport.Requests.Count==0,"pre-cancelled read sent");
                f.Transport.Reply=r=>{throw new TaskCanceledException(Secret);};var error=Reject(()=>f.List()) as CloudBackendException;
                Assert(error!=null&&error.Code=="timeout"&&!(error is CloudBackendUncertainException)&&f.Transport.Requests.Count==1&&!error.ToString().Contains(Secret),"read timeout wrongly replayed or marked as a mutation");
            }
        });
        check("cloud GitHub refuses malformed metadata and responses larger than its bounded catalogue",()=>{
            using(var f=new Fixture()){
                var invalid=Json.Obj(Repository());invalid["permissions"]=new{push="true"};f.Reply(new{repositories=new[]{invalid}});Reject(()=>f.List());
                f.Reply(new{repositories=Enumerable.Range(0,21).Select(i=>Repository("repo_"+i)).ToArray()});Reject(()=>f.List());
                f.Transport.Reply=r=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new string('x',2097153))});Reject(()=>f.List());
            }
        });
    }
}
