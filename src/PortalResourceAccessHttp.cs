using System;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    internal static class PortalResourceAccessHttp
    {
        private static readonly Regex Endpoint=new Regex(@"\A/v1/admin/([a-f0-9]{32})/resource-access\z");
        internal static Match Path(string path){return Endpoint.Match(path??"");}
        internal static WebhookHttpResponse Handle(RelayStore store,WebhookHttpRequest request,PortalAdministrationIdentity identity,PortalResourceAccess fixture=null)
        {
            if(!Path(request.Path).Success)throw new WebhookException(404,"Adresse de gestion des accès introuvable.");
            var service=fixture??new PortalResourceAccess(store);
            if(request.Method=="GET")return Result(service.Read(identity.Route,identity.Actor));
            if(request.Method!="POST")throw new WebhookException(405,"Méthode non autorisée.");
            var fields=PortalManagementHttp.Fields.Parse(request.Body,PortalAdministration.MaximumBody,"enabled","expectedRevision");
            object rawEnabled,rawRevision;
            if(fields.Count!=2||!fields.TryGetValue("enabled",out rawEnabled)||!(rawEnabled is bool)||!fields.TryGetValue("expectedRevision",out rawRevision)||!(rawRevision is string))throw new WebhookException(400,"Indiquez enabled et expectedRevision pour modifier le mode global.");
            string key;request.Headers.TryGetValue("Idempotency-Key",out key);
            return Result(service.Save(identity.Route,identity.Actor,key,(bool)rawEnabled,(string)rawRevision));
        }
        private static WebhookHttpResponse Result(ToolAccessPolicyState value)
        {return new WebhookHttpResponse{Status=200,Body=Json.Write(new{enabled=value.Enabled,revision=value.Revision,updatedUtc=value.UpdatedUtc,scope="all-instances",access="read-write",includesFutureInstances=true,includesFutureResources=true})};}
    }
}
