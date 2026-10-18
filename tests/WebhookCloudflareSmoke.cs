using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

// Explicit real-HTTPS acceptance test. Its destination is a fictional native
// adapter: no user's conversation, account, quota or project is touched.
internal static class WebhookCloudflareSmoke
{
    private static int Main(string[] args)
    {
        try{Run(Path.GetFullPath(args[0])).GetAwaiter().GetResult();return 0;}
        catch(Exception error){Console.WriteLine("FAIL "+error.GetType().Name+": "+Program.SafeError(error));return 1;}
    }
    private static void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);Console.WriteLine("PASS "+text);}
    private static async Task<HttpResponseMessage> Request(HttpClient client,string method,string path,string key,string body,string id)
    {
        var request=new HttpRequestMessage(new HttpMethod(method),path);
        if(key!=null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        if(id!=null)request.Headers.Add("Idempotency-Key",id);
        if(body!=null)request.Content=new StringContent(body,Encoding.UTF8,"application/json");
        return await client.SendAsync(request);
    }
    private static async Task Run(string root)
    {
        if(Directory.Exists(root)&&Directory.GetFileSystemEntries(root).Length!=0)throw new InvalidOperationException("Le dossier de recette doit etre vide.");Directory.CreateDirectory(root);
        var fixture=new GeneralRelayTests.Fixture(root);fixture.Adapter.SourceReadHook=delegate{};var store=fixture.Store;
        using(var server=new WebhookHttp(store,0)){
            server.Start();Webhooks.SaveConfig(store,new WebhookConfig{Enabled=true,Port=server.Port});
            var route=Webhooks.SaveRoute(store,new WebhookRoute{Slug="fictional-acceptance",Name="Fictional HTTPS acceptance",InstanceId=Guid.NewGuid().ToString("N"),AccountKey="target",Home=fixture.Folder,Workspace=fixture.Folder,Enabled=true,Access="desktop",PermissionProfile="inherit"});
            string key=Webhooks.RotateKey(store,route.Id);
            CloudflareTunnel.SaveConfig(store,new CloudflareTunnelConfig{Enabled=true,Mode="quick",Executable=CloudflareTunnel.FindExecutable()});
            try{
                string url=null;DateTime end=DateTime.UtcNow.AddSeconds(100);
                while(DateTime.UtcNow<end){CloudflareTunnel.Ensure(store,server.Port);var state=CloudflareTunnel.Status(store);if(state.State=="connected"&&!String.IsNullOrEmpty(state.Url)){url=state.Url;break;}await Task.Delay(2000);}
                Check(url!=null,"Cloudflare quick tunnel establishes a real encrypted connection");
                using(var client=new HttpClient()){client.BaseAddress=new Uri(url);client.Timeout=TimeSpan.FromSeconds(25);
                    bool ready=false;for(int n=0;n<8&&!ready;n++){try{using(var health=await client.GetAsync("/health"))ready=health.IsSuccessStatusCode&&await health.Content.ReadAsStringAsync()=="{\"status\":\"ok\"}";}catch(HttpRequestException){}if(!ready)await Task.Delay(2000);}
                    Check(ready,"public HTTPS reaches only the fictional loopback receiver");
                    string path="/v1/hooks/"+route.Slug+"/jobs",body="{\"title\":\"Fictional acceptance\",\"prompt\":\"Return the fictional fixture result only.\"}",id;
                    using(var rejected=await Request(client,"POST",path,"incorrect",body,"fictional-once"))Check((int)rejected.StatusCode==401,"public unauthenticated request cannot create an agent job");
                    using(var accepted=await Request(client,"POST",path,key,body,"fictional-once")){Check((int)accepted.StatusCode==202,"public authenticated request is durably queued");id=Json.Str(Json.Get(Json.Read<object>(await accepted.Content.ReadAsStringAsync()),"id"));}
                    using(var duplicate=await Request(client,"POST",path,key,body,"fictional-once"))Check((int)duplicate.StatusCode==202&&Json.Str(Json.Get(Json.Read<object>(await duplicate.Content.ReadAsStringAsync()),"id"))==id,"public retry returns the exact same job");
                    using(var conflict=await Request(client,"POST",path,key,"{\"prompt\":\"Different fictional work\"}","fictional-once"))Check((int)conflict.StatusCode==409,"public conflicting retry is rejected");
                    var job=Webhooks.Job(store,id);
                    Func<WebhookRoute,CancellationToken,Task<RelayChannel>> connect=(r,ct)=>Task.FromResult(store.Channel("target"));
                    await WebhookDispatch.Process(store,job,CancellationToken.None,connect,r=>{},ct=>Task.FromResult(0),fixture.Adapter);
                    var engine=new RelayEngine(store,fixture.Adapter,(h,t)=>"full-access");for(int n=0;n<3;n++)await engine.Process(id,CancellationToken.None);
                    await WebhookDispatch.Process(store,Webhooks.Job(store,id),CancellationToken.None,connect,r=>{},ct=>Task.FromResult(0),fixture.Adapter);
                    using(var status=await Request(client,"GET",path+"/"+id,key,null,null)){var value=Json.Read<object>(await status.Content.ReadAsStringAsync());Check((int)status.StatusCode==200&&Json.Str(Json.Get(value,"state"))=="completed"&&Json.Str(Json.Get(value,"result")).Contains("Checked."),"public caller retrieves the final fictional native result");}
                    Check(store.Messages().Count==1&&fixture.Adapter.Created==1&&fixture.Adapter.WorkSent==1,"external retries execute the fictional native mission exactly once");
                    File.WriteAllText(Path.Combine(root,"acceptance.json"),Json.Write(new{completedUtc=DateTime.UtcNow.ToString("o"),https=true,realCodexPrompts=0,fictionalNativeMissions=1,duplicateNativeMissions=0,publicUrl=url,tunnelPid=CloudflareTunnel.Status(store).Pid}));
                }
            }finally{CloudflareTunnel.StopOwned(store);Webhooks.SaveConfig(store,new WebhookConfig{Enabled=false,Port=server.Port});}
        }
    }
}
