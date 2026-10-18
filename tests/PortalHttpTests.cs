using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Creezio.Switcher;

internal static class PortalHttpTests
{
    private static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    private static void Reject(int status,Action action){try{action();}catch(WebhookException e){Assert(e.StatusCode==status,"Expected HTTP "+status+", got "+e.StatusCode);return;}throw new Exception("Missing HTTP rejection "+status);}
    private sealed class GuardedBody : MemoryStream
    {
        private readonly long begin;internal int BodyReads;
        internal GuardedBody(string headers,string body):base(Encoding.UTF8.GetBytes(headers+body)){begin=Encoding.UTF8.GetByteCount(headers);}
        public override int Read(byte[] buffer,int offset,int count){if(Position>=begin&&count>0){BodyReads++;throw new Exception("Rejected request read its body");}return base.Read(buffer,offset,count);}
    }
    private sealed class Fixture
    {
        internal RelayStore Store;internal WebhookRoute Route;internal PortalConfig Config;internal PortalInstanceGrant Grant;internal string Key;
        internal Fixture(string root,string name)
        {
            string folder=Path.Combine(root,"portal-http-tests",name+"-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(folder);
            var accounts=new AccountService(folder);string payload=Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string,object>{{"sub","portal-http-fixture"},{"email","fixture@example.test"},{"https://api.openai.com/auth",new{chatgpt_account_id="portal-http-account",chatgpt_plan_type="plus"}}}))).TrimEnd('=').Replace('+','-').Replace('/','_');
            string auth=Json.Write(new{auth_mode="chatgpt",tokens=new{access_token="fictional-never-used",id_token="e30."+payload+".fake",refresh_token="fictional-never-used",account_id="portal-http-account"}});var profile=accounts.Import(auth,"Portal HTTP fixture");
            var instance=new DesktopInstance{Id=new string('b',32),Name="Portal HTTP fixture",AccountKey=profile.Key,AccountLocked=true};accounts.Data.Instances.Add(instance);accounts.Save();string home=accounts.Instances.Home(instance);Directory.CreateDirectory(home);File.WriteAllText(Path.Combine(home,"auth.json"),auth);File.WriteAllText(Path.Combine(home,"config.toml"),"cli_auth_credentials_store = \"file\"\n");string workspace=Path.Combine(folder,"workspace");Directory.CreateDirectory(workspace);
            Store=new RelayStore(Path.Combine(folder,"relay"));Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true});Route=Webhooks.SaveRoute(Store,new WebhookRoute{Name="Private portal HTTP fixture",Slug="private-portal",InstanceId=instance.Id,AccountKey=profile.Key,Home=home,Workspace=workspace,Enabled=true});Key=Webhooks.RotateKey(Store,Route.Id);Route=Webhooks.Route(Store,Route.Id);
            Grant=new PortalInstanceGrant{InstanceId=instance.Id,AccountKey=profile.Key,Home=home,Workspace=workspace,Access="desktop",PermissionProfile="inherit",Model="gpt-6.1-sol",ReasoningEffort="ultra"};Config=Portal.SaveConfig(Store,new PortalConfig{RootRouteId=Route.Id,Enabled=true,IncludeExisting=true,Instances=new List<PortalInstanceGrant>{Grant}});
        }
        internal string PathFor(string suffix){return "/v1/hooks/"+Route.Slug+"/instances/"+Grant.InstanceId+suffix;}
        internal WebhookHttpRequest Request(string method,string path,string body="{}",string key=null){return new WebhookHttpRequest{Method=method,Path=path,Body=body,Headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"Authorization","Bearer "+(key??Key)}}};}
        internal void HeaderReject(int code,string path,string key){string body="{\"prompt\":\"Not read\"}";string head="POST "+path+" HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nAuthorization: Bearer "+key+"\r\n\r\n";using(var stream=new GuardedBody(head,body)){Reject(code,()=>WebhookHttp.ReadRequest(stream,r=>WebhookHttp.ValidateHeaders(Store,r)));Assert(stream.BodyReads==0,"Unauthorized portal request consumed body");}}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("portal HTTP authenticates all new entry points before body",()=>{
            var f=new Fixture(root,"body-auth");foreach(string path in new[]{"/v1/hooks/private-portal/instances",f.PathFor("/chats"),f.PathFor("/attachments"),"/v1/hooks/private-portal/attachments",f.PathFor("/capabilities"),f.PathFor("/tools/"+new string('a',64)+"/widget"),f.PathFor("/tools/"+new string('a',64)+"/call")})f.HeaderReject(401,path,"wrong-secret");Assert(Webhooks.Jobs(f.Store).Length==0,"Invalid token admitted work");
        });
        check("portal HTTP revoked root key rejects before body and never falls back to legacy",()=>{
            var f=new Fixture(root,"revoked-key");string old=f.Key;Webhooks.RotateKey(f.Store,f.Route.Id);f.HeaderReject(401,f.PathFor("/chats"),old);Assert(WebhookHttp.Handle(f.Store,f.Request("POST",f.PathFor("/chats"),"{}",old)).Status==401,"Rotated root accepted");
        });
        check("portal HTTP unshared or revoked instance fails before reading body",()=>{
            var f=new Fixture(root,"grant-revoke");f.HeaderReject(404,"/v1/hooks/private-portal/instances/"+new string('c',32)+"/chats",f.Key);f.Config.Instances.Clear();Portal.SaveConfig(f.Store,f.Config);f.HeaderReject(404,f.PathFor("/chats"),f.Key);Assert(Webhooks.Jobs(f.Store).Length==0,"Unshared profile created a job");
        });
        check("portal HTTP disabled configuration does not expose any instance",()=>{
            var f=new Fixture(root,"disabled");f.Config.Enabled=false;Portal.SaveConfig(f.Store,f.Config);f.HeaderReject(403,f.PathFor("/capabilities"),f.Key);Assert(WebhookHttp.Handle(f.Store,f.Request("GET","/v1/hooks/private-portal/instances")).Status==403,"Disabled portal disclosed list");
        });
        check("portal HTTP methods remain strict after valid account authorization",()=>{
            var f=new Fixture(root,"methods");foreach(var pair in new[]{new{Method="POST",Suffix="/capabilities"},new{Method="POST",Suffix="/tools/"+new string('a',64)+"/widget"},new{Method="GET",Suffix="/tools/"+new string('a',64)+"/call"},new{Method="POST",Suffix="/chats/01900000-0000-7000-8000-000000000001/events"}})Assert(WebhookHttp.Handle(f.Store,f.Request(pair.Method,f.PathFor(pair.Suffix))).Status==405,"Unexpected method reached native operation");Assert(Webhooks.Jobs(f.Store).Length==0,"Unsupported method created work");
        });
        check("portal HTTP MCP route requires full opaque 64 hex identifier",()=>{
            var f=new Fixture(root,"tool-id");foreach(string id in new[]{new string('a',32),new string('G',64),"../auth.json",new string('a',65)}){var response=WebhookHttp.Handle(f.Store,f.Request("GET",f.PathFor("/tools/"+id+"/widget")));Assert(response.Status==404,"Invalid MCP ID accepted");}Assert(WebhookHttp.Handle(f.Store,f.Request("POST",f.PathFor("/tools/"+new string('a',64)+"/widget"))).Status==405,"Valid 64-hex ID not recognized");
        });
        check("portal HTTP MCP callback body has exact safe shape before tool RPC",()=>{
            var f=new Fixture(root,"strict-mcp");foreach(string body in new[]{"{}","null","{","[]","{\"toolName\":\"read\",\"args\":{},\"server\":\"foreign\"}","{\"toolName\":2,\"args\":{}}","{\"toolName\":\"\",\"args\":{}}","{\"toolName\":\"read\",\"args\":null}","{\"toolName\":\"read\",\"args\":[]}","{\"toolName\":\"read\",\"args\":\"not-an-object\"}","{\"toolName\":\""+new string('x',501)+"\",\"args\":{}}"})Assert(WebhookHttp.Handle(f.Store,f.Request("POST",f.PathFor("/tools/"+new string('a',64)+"/call"),body)).Status==400,"Malformed callback reached tool RPC");Assert(Webhooks.Jobs(f.Store).Length==0,"Invalid callback admitted model work");
        });
        check("portal HTTP selected live path requires WebSocket rather than normal GET",()=>{
            var f=new Fixture(root,"events-http");var response=WebhookHttp.Handle(f.Store,f.Request("GET",f.PathFor("/chats/01900000-0000-7000-8000-000000000001/events")));Assert(response.Status==426,"Events normal GET did not require upgrade");
        });
        check("portal WebSocket distinguishes native GUID and durable chat IDs",()=>{
            string slug,instance,chat;string native="01900000-0000-7000-8000-000000000001",durable=new string('a',32);Assert(WebhookWebSocket.IsPortalEventsPath("/v1/hooks/private-portal/instances/local/chats/"+native+"/events",out slug,out instance,out chat)&&slug=="private-portal"&&instance=="local"&&chat==native,"Native main route lost");Assert(WebhookWebSocket.IsPortalEventsPath("/v1/hooks/private-portal/instances/"+new string('b',32)+"/chats/"+durable+"/events",out slug,out instance,out chat)&&chat==durable,"Durable route lost");Assert(!WebhookWebSocket.IsPortalEventsPath("/v1/hooks/private-portal/instances/foreign/chats/"+native+"/events",out slug,out instance,out chat),"Arbitrary profile accepted");
        });
        check("portal WebSocket native handshake preserves legacy chat routing",()=>{
            string slug,chat;var request=new WebhookHttpRequest{Method="GET",Path="/v1/hooks/private-portal/instances/local/chats/01900000-0000-7000-8000-000000000001/events",Headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"Upgrade","websocket"},{"Connection","Upgrade"},{"Sec-WebSocket-Version","13"},{"Sec-WebSocket-Key","dGhlIHNhbXBsZSBub25jZQ=="}}};Assert(WebhookWebSocket.AcceptKey(request)=="s3pPLMBiTxaQ9kYGzzhZRbK+xOo=","Native WebSocket handshake failed");Assert(WebhookWebSocket.IsEventsPath("/v1/hooks/private-portal/chats/"+new string('a',32)+"/events",out slug,out chat)&&chat==new string('a',32),"Legacy route changed");
        });
        check("portal HTTP encoded query and path override never reach credentials",()=>{
            var f=new Fixture(root,"path-override");foreach(string suffix in new[]{"/chats?key=secret","/chats%2fsecret","/chats#secret","/chats\\secret"})f.HeaderReject(400,f.PathFor(suffix),f.Key);
        });
        check("portal HTTP stale permanent account is rejected without agent work",()=>{
            var f=new Fixture(root,"account-change");File.WriteAllText(Path.Combine(f.Grant.Home,"auth.json"),"{}");Assert(WebhookHttp.Handle(f.Store,f.Request("POST",f.PathFor("/capabilities"))).Status==409&&Webhooks.Jobs(f.Store).Length==0,"Changed profile was accepted");
        });
        check("portal HTTP upload admission rejects unauthorized instance and malformed payload without a model turn",()=>{
            var f=new Fixture(root,"upload-shape");f.HeaderReject(404,"/v1/hooks/private-portal/instances/"+new string('c',32)+"/attachments",f.Key);
            Assert(WebhookHttp.Handle(f.Store,f.Request("GET",f.PathFor("/attachments"))).Status==405,"Upload accepted GET");
            foreach(string body in new[]{"{}","{\"name\":\"brief.txt\",\"mimeType\":\"text/plain\",\"contentBase64\":\"YQ==\",\"path\":\"C:/secret\"}"}){
                var request=f.Request("POST",f.PathFor("/attachments"),body);request.Headers["Idempotency-Key"]="upload-shape";Assert(WebhookHttp.Handle(f.Store,request).Status==400,"Malformed upload was accepted");
            }
            Assert(Webhooks.Jobs(f.Store).Length==0,"Upload created a model turn");
        });
        check("portal HTTP keeps prompt bodies small while admitting only bounded scoped upload bodies",()=>{
            var f=new Fixture(root,"upload-body-limits");string body="{\"name\":\"brief.txt\",\"mimeType\":\"text/plain\",\"contentBase64\":\""+new string('A',70000)+"\"}";
            Func<string,string> wire=path=>"POST "+path+" HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nAuthorization: Bearer "+f.Key+"\r\n\r\n"+body;
            using(var input=new MemoryStream(Encoding.UTF8.GetBytes(wire(f.PathFor("/attachments"))))){var request=WebhookHttp.ReadRequest(input,r=>WebhookHttp.ValidateHeaders(f.Store,r));Assert(request.Body.Length==body.Length,"Bounded upload was truncated");}
            using(var input=new MemoryStream(Encoding.UTF8.GetBytes(wire(f.PathFor("/chats")))))Reject(413,()=>WebhookHttp.ReadRequest(input,r=>WebhookHttp.ValidateHeaders(f.Store,r)));
        });
    }
}
