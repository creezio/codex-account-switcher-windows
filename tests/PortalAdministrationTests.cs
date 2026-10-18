using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Creezio.Switcher;

internal static class PortalAdministrationTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(int status,Action action){try{action();}catch(WebhookException error){Assert(error.StatusCode==status,"Expected "+status+", got "+error.StatusCode);return;}throw new Exception("Missing HTTP rejection "+status);}
    internal sealed class Fixture
    {
        internal readonly RelayStore Store;internal readonly WebhookRoute Route;internal PortalAdministrationPolicy Policy;
        internal Fixture(string sandbox)
        {
            string root=Path.Combine(sandbox,"portal-admin-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var accounts=new AccountService(root);
            string encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(new{email="fixture@example.test",sub="fictional-admin",aud="fictional",@auth="fictional"}))).TrimEnd('=').Replace('+','-').Replace('/','_');
            string auth=Json.Write(new{auth_mode="chatgpt",tokens=new{access_token="fictional-never-used",id_token="e30."+encoded+".fake",refresh_token="fictional-never-used",account_id="fictional-admin-account"}});
            var profile=accounts.Import(auth,"Fictional admin");var instance=new DesktopInstance{Id=Guid.NewGuid().ToString("N"),Name="Fictional",AccountKey=profile.Key,AccountLocked=true};accounts.Data.Instances.Add(instance);accounts.Save();string home=accounts.Instances.Home(instance),workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);File.WriteAllText(Path.Combine(home,"auth.json"),auth);File.WriteAllText(Path.Combine(home,"config.toml"),"cli_auth_credentials_store = \"file\"\n");
            Store=new RelayStore(Path.Combine(root,"relay"));Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true});Route=Webhooks.SaveRoute(Store,new WebhookRoute{Name="Admin fixture",Slug="admin-fixture",InstanceId=instance.Id,AccountKey=profile.Key,Home=home,Workspace=workspace,Enabled=true});
            Portal.SaveConfig(Store,new PortalConfig{RootRouteId=Route.Id,Enabled=true});Policy=PortalAdministration.Save(Store,new PortalAdministrationPolicy{Enabled=true,RootRouteId=Route.Id,SiteOrigin="https://portal.example.com",AllowedEmails=new List<string>{"owner@example.com"}});
        }
        internal WebhookHttpRequest Request(string suffix="capabilities",string method="GET",string body="")
        {
            var value=new WebhookHttpRequest{Method=method,Path="/v1/admin/"+Route.Id+"/"+suffix,Body=body};
            value.Headers["Authorization"]="Switcher-Admin v1";value.Headers["X-Switcher-Admin-Site"]="https://portal.example.com";value.Headers["X-Switcher-Admin-Route"]=Route.Id;value.Headers["X-Switcher-Admin-User"]="user_owner";value.Headers["X-Switcher-Admin-Email"]="owner@example.com";value.Headers["X-Switcher-Admin-Time"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);value.Headers["X-Switcher-Admin-Nonce"]=Guid.NewGuid().ToString("N");value.Headers["X-Switcher-Admin-Body"]=Webhooks.Hash(body);
            if(method=="POST")value.Headers["Idempotency-Key"]="one-operation";Sign(value);return value;
        }
        internal void Sign(WebhookHttpRequest value){value.Headers["X-Switcher-Admin-Signature"]=PortalAdministration.Sign(value,Policy.Secret);}
    }
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("remote admin ordinary webhook bearer cannot authorize account creation",()=>{var f=new Fixture(sandbox);var request=f.Request("instance-enrollments","POST","{}");request.Headers.Clear();request.Headers["Authorization"]="Bearer "+Webhooks.RotateKey(f.Store,f.Route.Id);Reject(403,()=>PortalAdministration.ValidateHeaders(f.Store,request));});
        check("remote admin envelope pins identity origin route method body and idempotency",()=>{
            foreach(string name in new[]{"X-Switcher-Admin-Site","X-Switcher-Admin-Route","X-Switcher-Admin-User","X-Switcher-Admin-Email","X-Switcher-Admin-Body","Idempotency-Key"}){var f=new Fixture(sandbox);var request=f.Request("instance-enrollments","POST","{}");request.Headers[name]="foreign";Reject(403,()=>PortalAdministration.ValidateHeaders(f.Store,request));}
            var fixture=new Fixture(sandbox);var value=fixture.Request();value.Method="POST";Reject(403,()=>PortalAdministration.ValidateHeaders(fixture.Store,value));
        });
        check("remote admin timestamp has bounded past and future acceptance",()=>{var f=new Fixture(sandbox);foreach(long delta in new[]{-121L,121L}){var request=f.Request();request.Headers["X-Switcher-Admin-Time"]=(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+delta).ToString();f.Sign(request);Reject(403,()=>PortalAdministration.ValidateHeaders(f.Store,request));}});
        check("remote admin body substitution is rejected after header authentication",()=>{var f=new Fixture(sandbox);var request=f.Request("instance-enrollments","POST","{}");PortalAdministration.ValidateHeaders(f.Store,request);request.Body="{\"force\":true}";Reject(403,()=>PortalAdministration.Authorize(f.Store,request));});
        check("remote admin signed nonce is consumed once and survives another store reader",()=>{var f=new Fixture(sandbox);var request=f.Request();PortalAdministration.Authorize(f.Store,request);Reject(409,()=>PortalAdministration.Authorize(new RelayStore(f.Store.Root),request));});
        check("remote admin identities are bound to ID plus email and immediately revocable",()=>{
            var f=new Fixture(sandbox);var first=PortalAdministration.Authorize(f.Store,f.Request());var second=f.Request();second.Headers["X-Switcher-Admin-User"]="user_other";f.Sign(second);var other=PortalAdministration.Authorize(f.Store,second);Assert(first.Actor!=other.Actor,"Same email hid different native user");PortalAdministration.RequireActor(f.Store,first.Route,first.Actor);
            var policy=PortalAdministration.Policy(f.Store);policy.AllowedEmails=new List<string>{"replacement@example.com"};PortalAdministration.Save(f.Store,policy);Reject(403,()=>PortalAdministration.RequireActor(f.Store,first.Route,first.Actor));
        });
        check("remote admin disabled policy root or portal revokes pending actions",()=>{
            var f=new Fixture(sandbox);var identity=PortalAdministration.Authorize(f.Store,f.Request());var policy=PortalAdministration.Policy(f.Store);policy.Enabled=false;PortalAdministration.Save(f.Store,policy);Reject(403,()=>PortalAdministration.RequireActor(f.Store,identity.Route,identity.Actor));
            f=new Fixture(sandbox);identity=PortalAdministration.Authorize(f.Store,f.Request());var portal=Portal.Config(f.Store,f.Route.Id);portal.Enabled=false;Portal.SaveConfig(f.Store,portal);Reject(403,()=>PortalAdministration.RequireActor(f.Store,identity.Route,identity.Actor));
        });
        check("remote admin valid actor is registered only after full body and signature verification",()=>{var f=new Fixture(sandbox);var value=f.Request();value.Body="not empty";Reject(403,()=>PortalAdministration.Authorize(f.Store,value));string actor=Webhooks.Hash("user_owner\nowner@example.com");Reject(403,()=>PortalAdministration.RequireActor(f.Store,f.Route.Id,actor));});
        check("remote admin keys remain server managed and rotation invalidates old envelopes",()=>{var f=new Fixture(sandbox);string original=f.Policy.Secret;var request=f.Request();var policy=PortalAdministration.Policy(f.Store);policy.Secret="attacker";f.Policy=PortalAdministration.Save(f.Store,policy);Assert(f.Policy.Secret==original,"Client replaced server key");f.Policy=PortalAdministration.Save(f.Store,f.Policy,true);Reject(403,()=>PortalAdministration.ValidateHeaders(f.Store,request));Assert(f.Policy.Secret!=original,"Rotation did not change key");});
        check("remote admin JSON rejects decoded duplicates unknown keys nested values and malformed literals",()=>{
            foreach(string value in new[]{"{\"name\":\"one\",\"na\\u006de\":\"two\"}","{\"authJson\":\"secret\"}","{\"name\":{}}","{\"name\":[]}","{\"exposeInPortal\":truefalse}","{\"name\":\"one\",}","null","{} trailing"})Reject(400,()=>PortalAdministrationHttp.FlatObject.Parse(value,new[]{"name","exposeInPortal"}));
            var result=PortalAdministrationHttp.FlatObject.Parse("{\"name\":\"Équipe\",\"exposeInPortal\":true}",new[]{"name","exposeInPortal"});Assert((string)result["name"]=="Équipe"&&(bool)result["exposeInPortal"],"Valid Unicode request rejected");
        });
        check("remote admin HTTP authenticates before reading an unauthorized body",()=>{
            var f=new Fixture(sandbox);var request=f.Request("instance-enrollments","POST","{}");request.Headers["X-Switcher-Admin-Signature"]=new string('0',64);string head="POST "+request.Path+" HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 20\r\n"+String.Join("\r\n",request.Headers.Select(pair=>pair.Key+": "+pair.Value))+"\r\n\r\n";
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(head)))Reject(403,()=>WebhookHttp.ReadRequest(stream,headers=>WebhookHttp.ValidateHeaders(f.Store,headers)));
        });
        check("remote admin HTTP caps request allocation at four KiB",()=>{var f=new Fixture(sandbox);string head="POST /v1/admin/"+f.Route.Id+"/instance-enrollments HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 4097\r\n\r\n";using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(head)))Reject(413,()=>WebhookHttp.ReadRequest(stream,headers=>WebhookHttp.ValidateHeaders(f.Store,headers)));});
    }
}
