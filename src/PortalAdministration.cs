using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    public sealed class PortalAdministrationPolicy
    {
        public bool Enabled {get;set;}
        public long Revision {get;set;}
        public string SiteOrigin {get;set;}
        public string RootRouteId {get;set;}
        public string Secret {get;set;}
        public List<string> AllowedEmails {get;set;}
        public PortalAdministrationPolicy(){AllowedEmails=new List<string>();}
    }
    internal sealed class PortalAdministrationActor
    {
        public string Id {get;set;}
        public string Email {get;set;}
        public string SiteOrigin {get;set;}
        public string RootRouteId {get;set;}
        public long PolicyRevision {get;set;}
    }
    internal sealed class PortalAdministrationNonce
    {
        public string Hash {get;set;}
        public long At {get;set;}
    }
    internal sealed class PortalAdministrationReplay
    {
        public List<PortalAdministrationNonce> Entries {get;set;}
        public PortalAdministrationReplay(){Entries=new List<PortalAdministrationNonce>();}
    }
    internal sealed class PortalAdministrationIdentity
    {
        internal string Route,Actor;
        internal string SiteOrigin;
        internal long PolicyRevision;
    }
    // Separate from webhook/chat keys. Only the authenticated Site server signs
    // this envelope; the browser never receives an administration credential.
    internal static class PortalAdministration
    {
        internal const string Record="portal-administration-v1.dpapi";
        internal const int MaximumBody=4096;
        private static readonly Regex Endpoint=new Regex(@"\A/v1/admin/([a-f0-9]{32})/(?:(capabilities|instance-enrollments)(?:/([a-f0-9]{32})(?:/(confirm|cancel|browser-callback))?)?|instances/([a-f0-9]{32})/(plugins|plugin-installs)(?:/([a-f0-9]{32}))?)\z");
        internal static Match Path(string path){var match=Endpoint.Match(path??"");return match.Success?match:PortalManagementHttp.Path(path);}
        internal static int MaximumBodyFor(string path){return PortalManagementHttp.MaximumBody(path);}
        internal static PortalAdministrationPolicy Policy(RelayStore store){return store.ReadRecord<PortalAdministrationPolicy>(Record);}
        internal static string NewSecret(){var bytes=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);return Hex(bytes);}
        internal static PortalAdministrationPolicy Save(RelayStore store,PortalAdministrationPolicy input,bool rotate=false)
        {
            if(input==null)throw new ArgumentNullException("input");
            var value=Json.Read<PortalAdministrationPolicy>(Json.Write(input));
            value.SiteOrigin=Origin(value.SiteOrigin);
            RelayStore.MessageId(value.RootRouteId);Webhooks.Route(store,value.RootRouteId);
            value.AllowedEmails=(value.AllowedEmails??new List<string>()).Select(Email).Distinct(StringComparer.Ordinal).ToList();
            if(value.AllowedEmails.Count==0||value.AllowedEmails.Count>50)throw new InvalidOperationException("Choisissez de 1 a 50 adresses administratrices pour ce portail.");
            using(store.Lease("portal-administration-policy")){
                var previous=Policy(store);if(previous.Revision!=value.Revision)throw new InvalidOperationException("Les droits d'administration ont change. Actualisez avant d'enregistrer.");
                value.Secret=rotate||!Regex.IsMatch(previous.Secret??"",@"\A[a-f0-9]{64}\z")?NewSecret():previous.Secret;
                value.Revision=previous.Revision+1;store.WriteRecord(Record,value);return value;
            }
        }
        internal static string Origin(string input)
        {
            Uri uri;if(!Uri.TryCreate(input,UriKind.Absolute,out uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.AbsolutePath!="/"||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.HostNameType!=UriHostNameType.Dns||!uri.Host.Contains(".")||uri.Host.EndsWith(".localhost",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Indiquez l'origine HTTPS exacte du Site, sans chemin ni parametre.");
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }
        private static string Email(string input)
        {string value=(input??"").Trim().ToLowerInvariant();if(!Regex.IsMatch(value,@"\A[A-Za-z0-9.!#$&'*+/=?^_`{|}~-]{1,200}@[A-Za-z0-9.-]{1,120}\z")||value.Length>254)throw new InvalidOperationException("Adresse administratrice invalide.");return value;}
        private static string Header(WebhookHttpRequest request,string name){string value;return request.Headers.TryGetValue(name,out value)?value:"";}
        private static WebhookException Forbidden(){return new WebhookException(403,"Cette action demande une autorisation d'administration du Switcher.");}
        private static bool Equal(string left,string right)
        {if(left==null||right==null||left.Length!=right.Length)return false;int difference=0;for(int index=0;index<left.Length;index++)difference|=left[index]^right[index];return difference==0;}
        private static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        internal static string Canonical(WebhookHttpRequest request)
        {return String.Join("\n",new[]{"switcher-admin-v1",request.Method,request.Path,Header(request,"X-Switcher-Admin-Site"),Header(request,"X-Switcher-Admin-Route"),Header(request,"X-Switcher-Admin-User"),Header(request,"X-Switcher-Admin-Email"),Header(request,"X-Switcher-Admin-Time"),Header(request,"X-Switcher-Admin-Nonce"),Header(request,"X-Switcher-Admin-Body"),Header(request,"Idempotency-Key")});}
        internal static string Sign(WebhookHttpRequest request,string secret)
        {using(var mac=new HMACSHA256(Encoding.UTF8.GetBytes(secret)))return Hex(mac.ComputeHash(Encoding.UTF8.GetBytes(Canonical(request))));}
        internal static PortalAdministrationIdentity ValidateHeaders(RelayStore store,WebhookHttpRequest request)
        {return ValidateHeaders(store,request,DateTimeOffset.UtcNow.ToUnixTimeSeconds());}
        internal static PortalAdministrationIdentity ValidateHeaders(RelayStore store,WebhookHttpRequest request,long now)
        {
            var path=Path(request.Path);if(!path.Success)throw new WebhookException(404,"Adresse introuvable.");
            var policy=Policy(store);string route=path.Groups[1].Value;
            if(!policy.Enabled||policy.RootRouteId!=route||!Webhooks.Config(store).Enabled||Header(request,"Authorization")!="Switcher-Admin v1"||!Regex.IsMatch(policy.Secret??"",@"\A[a-f0-9]{64}\z"))throw Forbidden();
            var root=Webhooks.Route(store,route);var portal=Portal.Config(store,route);
            if(!root.Enabled||!portal.Enabled||portal.RootRouteId!=route)throw Forbidden();
            string user=Header(request,"X-Switcher-Admin-User"),email=Header(request,"X-Switcher-Admin-Email"),at=Header(request,"X-Switcher-Admin-Time"),nonce=Header(request,"X-Switcher-Admin-Nonce"),body=Header(request,"X-Switcher-Admin-Body"),signature=Header(request,"X-Switcher-Admin-Signature");long time;
            if(Header(request,"X-Switcher-Admin-Site")!=policy.SiteOrigin||Header(request,"X-Switcher-Admin-Route")!=route||!Regex.IsMatch(user,@"\A[A-Za-z0-9_-]{1,256}\z")||!policy.AllowedEmails.Contains(email,StringComparer.Ordinal)||!Regex.IsMatch(at,@"\A[0-9]{10,12}\z")||!Int64.TryParse(at,NumberStyles.None,CultureInfo.InvariantCulture,out time)||Math.Abs(now-time)>120||!Regex.IsMatch(nonce,@"\A[a-f0-9]{32}\z")||!Regex.IsMatch(body,@"\A[a-f0-9]{64}\z")||!Regex.IsMatch(signature,@"\A[a-f0-9]{64}\z")||!Equal(signature,Sign(request,policy.Secret)))throw Forbidden();
            if(request.Method=="POST"){
                string key=Header(request,"Idempotency-Key");if(key.Length==0||key.Length>128||key.Any(character=>character<33||character>126))throw new WebhookException(400,"Idempotency-Key est obligatoire pour cette action.");
            }
            return new PortalAdministrationIdentity{Route=route,Actor=Webhooks.Hash(user+"\n"+email),SiteOrigin=policy.SiteOrigin,PolicyRevision=policy.Revision};
        }
        internal static PortalAdministrationIdentity Authorize(RelayStore store,WebhookHttpRequest request)
        {
            var identity=ValidateHeaders(store,request);
            if(Encoding.UTF8.GetByteCount(request.Body??"")>MaximumBodyFor(request.Path))throw new WebhookException(413,"La demande d'administration depasse la limite de cette operation.");
            if(!Equal(Header(request,"X-Switcher-Admin-Body"),Webhooks.Hash(request.Body??"")))throw Forbidden();
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string replayKey=Webhooks.Hash(Canonical(request));
            using(store.Lease("portal-administration-replay")){
                var replay=store.ReadRecord<PortalAdministrationReplay>("portal-administration-replay-v1.dpapi");
                replay.Entries=replay.Entries.Where(entry=>entry.At>=now-240&&entry.At<=now+120).ToList();
                if(replay.Entries.Any(entry=>entry.Hash==replayKey))throw new WebhookException(409,"Cette requete d'administration a deja ete recue. Reessayez avec une nouvelle signature et la meme cle d'idempotence.");
                if(replay.Entries.Count>=2048)throw new WebhookException(429,"Trop de requetes d'administration. Patientez avant de reessayer.");
                replay.Entries.Add(new PortalAdministrationNonce{Hash=replayKey,At=now});store.WriteRecord("portal-administration-replay-v1.dpapi",replay);
            }
            // A key/policy rotation during body admission must not promote an
            // envelope signed under the previous policy into the new revision.
            var current=ValidateHeaders(store,request);if(current.PolicyRevision!=identity.PolicyRevision||current.SiteOrigin!=identity.SiteOrigin)throw Forbidden();
            store.WriteRecord("portal-admin-actor-"+identity.Actor+".dpapi",new PortalAdministrationActor{Id=Header(request,"X-Switcher-Admin-User"),Email=Header(request,"X-Switcher-Admin-Email"),RootRouteId=identity.Route,SiteOrigin=identity.SiteOrigin,PolicyRevision=identity.PolicyRevision});
            RequireActor(store,identity.Route,identity.Actor);return identity;
        }
        internal static void RequireActor(RelayStore store,string route,string actor)
        {
            if(!Regex.IsMatch(actor??"",@"\A[a-f0-9]{64}\z"))throw Forbidden();var policy=Policy(store);var value=store.ReadRecord<PortalAdministrationActor>("portal-admin-actor-"+actor+".dpapi");
            if(!policy.Enabled||policy.RootRouteId!=route||value.RootRouteId!=route||value.SiteOrigin!=policy.SiteOrigin||value.PolicyRevision!=policy.Revision||!policy.AllowedEmails.Contains(value.Email??"",StringComparer.Ordinal)||Webhooks.Hash(value.Id+"\n"+value.Email)!=actor||!Webhooks.Config(store).Enabled)throw Forbidden();
            var root=Webhooks.Route(store,route);var portal=Portal.Config(store,route);if(!root.Enabled||!portal.Enabled||portal.RootRouteId!=route)throw Forbidden();
        }
    }
}
