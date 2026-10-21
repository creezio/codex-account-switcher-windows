using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    internal sealed class PortalResourceAccessOperation
    {
        public string Id {get;set;} public string Fingerprint {get;set;}
        public string State {get;set;} public string UpdatedUtc {get;set;}
        public ToolAccessPolicyState Result {get;set;}
    }

    // This is a machine-wide setting, available only to the separately signed
    // administration identity. Ordinary chat and webhook bearer keys do not
    // acquire permission to change it.
    internal sealed class PortalResourceAccess
    {
        private readonly RelayStore store;
        private readonly Action<string,string> authorize;
        internal PortalResourceAccess(RelayStore data,Action<string,string> verifier=null)
        {store=data;authorize=verifier??((route,actor)=>PortalAdministration.RequireActor(store,route,actor));}
        internal ToolAccessPolicyState Read(string route,string actor)
        {authorize(route,actor);return ToolAccessPolicy.Read(store);}
        internal ToolAccessPolicyState Save(string route,string actor,string key,bool enabled,string expectedRevision)
        {
            authorize(route,actor);
            if(String.IsNullOrWhiteSpace(key)||key.Length>128||!Regex.IsMatch(key,@"\A[\x21-\x7e]+\z"))throw new WebhookException(400,"Idempotency-Key est obligatoire pour modifier les accès globaux.");
            if(String.IsNullOrEmpty(expectedRevision)||expectedRevision.Length>128)throw new WebhookException(400,"Actualisez la révision du mode d'accès avant d'enregistrer.");
            string id=Webhooks.Hash("portal-resource-access\n"+route+"\n"+actor+"\n"+key).Substring(0,32);
            string recordName="portal-resource-access-"+id+".dpapi";
            string fingerprint=ToolTunnel.Hash(new{enabled,expectedRevision});
            using(store.Lease("portal-resource-access-"+id)){
                authorize(route,actor);
                if(File.Exists(Path.Combine(store.Root,recordName))){
                    var prior=store.ReadRecord<PortalResourceAccessOperation>(recordName);
                    if(prior.Id!=id||prior.Fingerprint!=fingerprint)throw new WebhookException(409,"Cette clé d'idempotence correspond à un autre changement d'accès.");
                    if(prior.State=="completed"&&prior.Result!=null)return prior.Result;
                    throw new WebhookException(409,"Le résultat de ce changement doit être vérifié. Relisez le mode actuel avant une nouvelle demande ; aucun changement n'est rejoué.");
                }
                var operation=new PortalResourceAccessOperation{Id=id,Fingerprint=fingerprint,State="uncertain",UpdatedUtc=DateTime.UtcNow.ToString("o")};
                // Persist the intention before crossing the policy write. A
                // crash between these records must never replay an old choice.
                store.WriteRecord(recordName,operation);
                authorize(route,actor);
                operation.Result=ToolAccessPolicy.Save(store,enabled,expectedRevision);
                operation.State="completed";operation.UpdatedUtc=DateTime.UtcNow.ToString("o");
                store.WriteRecord(recordName,operation);return operation.Result;
            }
        }
    }
}
