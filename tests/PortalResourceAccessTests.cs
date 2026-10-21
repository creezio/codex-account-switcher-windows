using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Creezio.Switcher;

internal static class PortalResourceAccessTests
{
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static object Value(WebhookHttpResponse response){return Json.Read<object>(response.Body);}
    private static string Revision(WebhookHttpResponse response){return Json.Str(Json.Get(Value(response),"revision"));}
    private static WebhookHttpResponse Send(PortalAdministrationTests.Fixture f,string method,string body="",string key="change")
    {
        var request=f.Request("resource-access",method,body);
        if(method=="POST")request.Headers["Idempotency-Key"]=key;
        f.Sign(request);return PortalAdministrationHttp.Handle(f.Store,request);
    }
    private static string Body(bool enabled,string revision){return Json.Write(new{enabled,expectedRevision=revision});}
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("global resources admin API defaults off and changes only its separate policy",()=>{
            var f=new PortalAdministrationTests.Fixture(sandbox);
            f.Store.WriteRecord("tool-grants.dpapi",new List<TunnelGrant>{new TunnelGrant{Id=new string('a',32),Name="Preserved custom grant"}});
            string grants=Path.Combine(f.Store.Root,"tool-grants.dpapi"),vault=Path.Combine(Path.GetDirectoryName(f.Store.Root),"accounts.dpapi");
            byte[] beforeGrants=File.ReadAllBytes(grants),beforeVault=File.ReadAllBytes(vault);
            var initial=Send(f,"GET");Assert(initial.Status==200&&Object.Equals(Json.Get(Value(initial),"enabled"),false)&&!File.Exists(Path.Combine(f.Store.Root,ToolAccessPolicy.FileName)),"Reading enabled or persisted the mode");
            var enabled=Send(f,"POST",Body(true,Revision(initial)),"enable");Assert(enabled.Status==200&&ToolAccessPolicy.Read(f.Store).Enabled,"Admin could not enable the full resource mode");
            var disabled=Send(f,"POST",Body(false,Revision(enabled)),"disable");Assert(disabled.Status==200&&!ToolAccessPolicy.Read(f.Store).Enabled,"Admin could not disable the mode");
            Assert(beforeGrants.SequenceEqual(File.ReadAllBytes(grants))&&beforeVault.SequenceEqual(File.ReadAllBytes(vault)),"Policy changed custom grants or account data");
            Assert(Object.Equals(Json.Get(Value(enabled),"includesFutureInstances"),true)&&Object.Equals(Json.Get(Value(enabled),"includesFutureResources"),true),"API omitted future resource scope");
        });
        check("global resources ordinary webhook keys cannot change machine access",()=>{
            var f=new PortalAdministrationTests.Fixture(sandbox);var request=f.Request("resource-access","POST",Body(true,"initial"));request.Headers.Clear();request.Headers["Authorization"]="Bearer "+Webhooks.RotateKey(f.Store,f.Route.Id);
            Assert(PortalAdministrationHttp.Handle(f.Store,request).Status==403&&!ToolAccessPolicy.Read(f.Store).Enabled,"Ordinary chat bearer changed global access");
        });
        check("global resources API rejects omitted duplicate unknown and mistyped fields",()=>{
            var f=new PortalAdministrationTests.Fixture(sandbox);
            foreach(string body in new[]{"{}","{\"enabled\":true}","{\"expectedRevision\":\"initial\"}","{\"enabled\":\"true\",\"expectedRevision\":\"initial\"}","{\"enabled\":true,\"enabled\":false,\"expectedRevision\":\"initial\"}","{\"enabled\":true,\"en\\u0061bled\":false,\"expectedRevision\":\"initial\"}","{\"enabled\":true,\"expectedRevision\":\"initial\",\"force\":true}"}){
                Assert(Send(f,"POST",body,Guid.NewGuid().ToString("N")).Status==400,"Malformed global access body accepted");
            }
            Assert(!ToolAccessPolicy.Read(f.Store).Enabled,"Bad bodies activated global access");
        });
        check("global resources API stale revisions cannot overwrite a newer choice",()=>{
            var f=new PortalAdministrationTests.Fixture(sandbox);var enabled=Send(f,"POST",Body(true,"initial"),"enable");
            Assert(enabled.Status==200&&Send(f,"POST",Body(false,"initial"),"stale-disable").Status==409,"Stale admin request overwrote current policy");
            Assert(ToolAccessPolicy.Read(f.Store).Enabled&&ToolAccessPolicy.Read(f.Store).Revision==Revision(enabled),"Conflict changed global state");
        });
        check("global resources API idempotent retry never reactivates a later disabled mode",()=>{
            var f=new PortalAdministrationTests.Fixture(sandbox);string firstBody=Body(true,"initial");var enabled=Send(f,"POST",firstBody,"one");
            var disabled=Send(f,"POST",Body(false,Revision(enabled)),"two");var retried=Send(f,"POST",firstBody,"one");
            Assert(retried.Status==200&&Revision(retried)==Revision(enabled)&&!ToolAccessPolicy.Read(f.Store).Enabled&&ToolAccessPolicy.Read(f.Store).Revision==Revision(disabled),"Retry replayed an old enable choice");
            Assert(Send(f,"POST",Body(false,"initial"),"one").Status==409,"Reused key accepted different content");
        });
        check("global resources API uncertain journal never retries a policy write",()=>{
            var f=new PortalAdministrationTests.Fixture(sandbox);string actor=Webhooks.Hash("user_owner\nowner@example.com"),key="interrupted";
            string id=Webhooks.Hash("portal-resource-access\n"+f.Route.Id+"\n"+actor+"\n"+key).Substring(0,32);
            f.Store.WriteRecord("portal-resource-access-"+id+".dpapi",new PortalResourceAccessOperation{Id=id,Fingerprint=ToolTunnel.Hash(new{enabled=true,expectedRevision="initial"}),State="uncertain"});
            Assert(Send(f,"POST",Body(true,"initial"),key).Status==409&&!ToolAccessPolicy.Read(f.Store).Enabled,"Uncertain write was replayed");
        });
        check("global resources API revoked administrator and frozen updater cannot change access",()=>{
            var f=new PortalAdministrationTests.Fixture(sandbox);var request=f.Request("resource-access","POST",Body(true,"initial"));
            var policy=PortalAdministration.Policy(f.Store);policy.Enabled=false;PortalAdministration.Save(f.Store,policy);
            Assert(PortalAdministrationHttp.Handle(f.Store,request).Status==403&&!ToolAccessPolicy.Read(f.Store).Enabled,"Revoked actor enabled the mode");
            f=new PortalAdministrationTests.Fixture(sandbox);f.Store.WriteRecord(ApplicationUpdates.TransactionName,new ApplicationUpdateTransaction{Frozen=true});
            Assert(Send(f,"POST",Body(true,"initial")).Status==503&&!ToolAccessPolicy.Read(f.Store).Enabled,"Update freeze admitted a policy mutation");
            Assert(Send(f,"GET").Status==200,"Update freeze blocked read-only status");
        });
        check("global resources API rechecks administrator immediately before saving",()=>{
            var store=new RelayStore(Path.Combine(sandbox,"resource-access-revoked-"+Guid.NewGuid().ToString("N")));int checks=0;
            var service=new PortalResourceAccess(store,(route,actor)=>{if(++checks==3)throw new WebhookException(403,"Fictional authorization revoked");});
            bool rejected=false;try{service.Save(new string('a',32),new string('b',64),"test",true,"initial");}catch(WebhookException error){rejected=error.StatusCode==403;}
            Assert(rejected&&!ToolAccessPolicy.Read(store).Enabled,"Authorization changed before save but mode was enabled");
        });
    }
}
