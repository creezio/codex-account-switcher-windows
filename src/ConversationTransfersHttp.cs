using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Creezio.Switcher
{
    internal static class ConversationTransfersHttp
    {
        private static readonly Regex Endpoint=new Regex(@"\A/v1/admin/([a-f0-9]{32})/conversation-transfers(?:/(?<action>list)|/(?<id>[a-f0-9]{32})(?:/(?<action>resume))?)?\z");
        internal static Match Path(string path){return Endpoint.Match(path??"");}
        private static WebhookHttpResponse Result(int status,object value){return new WebhookHttpResponse{Status=status,Body=Json.Write(value)};}
        internal static WebhookHttpResponse Handle(RelayStore store,WebhookHttpRequest request,PortalAdministrationIdentity identity,ConversationTransfers fixture=null)
        {
            var path=Path(request.Path);if(!path.Success||identity.Route!=path.Groups[1].Value)throw new WebhookException(404,"Adresse de transfert introuvable.");
            var service=fixture??new ConversationTransfers(store);string id=path.Groups["id"].Value,action=path.Groups["action"].Value;
            if(request.Method=="GET"&&id.Length>0&&action.Length==0)return Result(200,service.Read(identity.Route,identity.Actor,null,id));
            if(request.Method!="POST")throw new WebhookException(405,"Méthode non autorisée.");
            string key;request.Headers.TryGetValue("Idempotency-Key",out key);
            if(action=="resume"){
                PortalManagementHttp.Fields.Parse(request.Body,4096,new string[0]);return Result(202,service.Resume(identity.Route,identity.Actor,null,id));
            }
            if(action=="list"){
                var fields=PortalManagementHttp.Fields.Parse(request.Body,4096,"instance","query","cursor");
                using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)))return Result(200,service.List(identity.Route,identity.Actor,null,Text(fields,"instance",true),Text(fields,"query",false),Text(fields,"cursor",false),timeout.Token).GetAwaiter().GetResult());
            }
            if(id.Length>0)throw new WebhookException(405,"Méthode non autorisée.");
            var body=PortalManagementHttp.Fields.Parse(request.Body,4096,"kind","source","destination","threadId","title","explicitRequest");object consent;
            if(!body.TryGetValue("explicitRequest",out consent)||!(consent is bool))throw new WebhookException(400,"Indiquez la demande explicite de l'utilisateur.");
            return Result(202,service.Start(identity.Route,identity.Actor,null,key,Text(body,"kind",true),Text(body,"source",true),Text(body,"destination",true),Text(body,"threadId",true),Text(body,"title",true),(bool)consent));
        }
        private static string Text(Dictionary<string,object> fields,string name,bool required)
        {object value;if(!fields.TryGetValue(name,out value)){if(!required)return null;throw new WebhookException(400,"Champ obligatoire : "+name);}if(!(value is string))throw new WebhookException(400,"Champ texte attendu : "+name);return (string)value;}
    }
}
