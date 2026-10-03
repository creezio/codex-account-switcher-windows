using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Creezio.Switcher
{
    public sealed class RemoteConfig
    {
        public bool Enabled {get;set;}
        public string BindAddress {get;set;}
        public int Port {get;set;}
        public string Certificate {get;set;}
        public string Pin {get;set;}
        public string DeviceId {get;set;}
        public RemoteConfig(){BindAddress="127.0.0.1";Port=17381;}
    }
    public sealed class RemoteGrant
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string TokenHash {get;set;}
        public string Expires {get;set;}
        public bool Revoked {get;set;}
        public bool CanSend {get;set;}
        public bool CanCreate {get;set;}
        public bool CanAssist {get;set;}
        public List<string> Channels {get;set;}
        public Dictionary<string,string> Threads {get;set;}
        public Dictionary<string,string> Accounts {get;set;}
        public Dictionary<string,string> Workspaces {get;set;}
        public RemoteGrant(){Channels=new List<string>();Threads=new Dictionary<string,string>();Accounts=new Dictionary<string,string>();Workspaces=new Dictionary<string,string>();}
    }
    public sealed class RemotePeer
    {
        public int Version {get;set;}
        public string Id {get;set;}
        public string Name {get;set;}
        public string DeviceId {get;set;}
        public string Host {get;set;}
        public int Port {get;set;}
        public string Pin {get;set;}
        public string Token {get;set;}
        public string Expires {get;set;}
        public bool Enabled {get;set;}
        public RemotePeer(){Version=1;Enabled=true;}
    }
    public sealed class SharedChannel
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Account {get;set;}
        public string Workspace {get;set;}
        public string Anchor {get;set;}
        public bool CanSend {get;set;}
        public bool CanCreate {get;set;}
        public bool RequireFullAccess {get;set;}
    }
    internal static class RemotePeers
    {
        internal static string Hash(string value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value??""))).Replace("-","").ToLowerInvariant();}
        internal static string CertificatePin(X509Certificate certificate){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-","").ToLowerInvariant();}
        internal static string Secret(){byte[] value=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(value);return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();}
        internal static bool Equal(string a,string b){if(a==null||b==null||a.Length!=b.Length)return false;int diff=0;for(int i=0;i<a.Length;i++)diff|=a[i]^b[i];return diff==0;}
        internal static bool Future(string value){DateTime expiry;return DateTime.TryParse(value,out expiry)&&expiry.ToUniversalTime()>DateTime.UtcNow;}
        public static RemoteConfig Config(RelayStore store){return store.ReadRecord<RemoteConfig>("remote-config.dpapi");}
        public static List<RemotePeer> Peers(RelayStore store){return store.ReadRecord<List<RemotePeer>>("remote-peers.dpapi");}
        public static List<RemoteGrant> Grants(RelayStore store){return store.ReadRecord<List<RemoteGrant>>("remote-grants.dpapi");}
        public static void SaveConfig(RelayStore store,RemoteConfig config)
        {
            IPAddress address;if(!IPAddress.TryParse(config.BindAddress,out address)||config.Port<1024||config.Port>65535)throw new InvalidOperationException("Choisissez une adresse IP locale et un port de 1024 à 65535.");
            using(store.Lease("remote-settings")){
                var old=Config(store);config.Certificate=old.Certificate;config.Pin=old.Pin;config.DeviceId=old.DeviceId;
                if(String.IsNullOrEmpty(config.Certificate)){
                    using(var rsa=RSA.Create()){rsa.KeySize=2048;var request=new CertificateRequest("CN=Account Switcher",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
                        using(var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2))){config.Certificate=Convert.ToBase64String(cert.Export(X509ContentType.Pfx));config.Pin=CertificatePin(cert);}}
                    config.DeviceId=Guid.NewGuid().ToString("N");
                }
                store.WriteRecord("remote-config.dpapi",config);
            }
        }
        internal static void ValidatePeer(RemotePeer peer)
        {
            if(peer==null||String.IsNullOrWhiteSpace(peer.Name)||peer.Name.Length>100)throw new InvalidOperationException("Invitation invalide.");
            RelayStore.MessageId(peer.Id);RelayStore.MessageId(peer.DeviceId);
            if(peer.Version!=1||!System.Text.RegularExpressions.Regex.IsMatch(peer.Pin??"","^[a-f0-9]{64}$")||!System.Text.RegularExpressions.Regex.IsMatch(peer.Token??"","^[a-f0-9]{64}$")||Uri.CheckHostName(peer.Host??"")==UriHostNameType.Unknown||peer.Port<1024||peer.Port>65535||!Future(peer.Expires))throw new InvalidOperationException("Invitation invalide ou expirée.");
        }
        public static RemotePeer Import(RelayStore store,string json)
        {
            if(json==null||json.Length>12000)throw new InvalidOperationException("Invitation trop longue.");var peer=Json.Read<RemotePeer>(json);if(peer==null)throw new InvalidOperationException("Invitation absente.");ValidatePeer(peer);
            using(store.Lease("remote-settings")){var list=Peers(store);var old=list.FirstOrDefault(p=>p.Id==peer.Id);if(old!=null&&(old.DeviceId!=peer.DeviceId||old.Pin!=peer.Pin))throw new InvalidOperationException("L'identité de ce PC a changé ; retirez explicitement l'ancienne association.");list.RemoveAll(p=>p.Id==peer.Id);list.Add(peer);store.WriteRecord("remote-peers.dpapi",list);}return peer;
        }
        public static string Invite(RelayStore store,string name,string host,string[] channels,bool send,bool create,bool assist,int days,Dictionary<string,string> additionalThreads=null)
        {
            if(days<1||days>30||channels==null||channels.Length==0||String.IsNullOrWhiteSpace(name)||name.Length>100||Uri.CheckHostName(host??"")==UriHostNameType.Unknown)throw new InvalidOperationException("Renseignez un nom, une adresse, au moins un canal et une durée de 1 à 30 jours.");
            if(create&&!send)throw new InvalidOperationException("Créer un chat exige le droit d'envoyer.");
            var config=Config(store);if(String.IsNullOrEmpty(config.Certificate))throw new InvalidOperationException("Configurez d'abord le partage de ce PC.");
            var grant=new RemoteGrant{Id=Guid.NewGuid().ToString("N"),Name=name,Expires=DateTime.UtcNow.AddDays(days).ToString("o"),CanSend=send,CanCreate=create,CanAssist=assist};string secret=Secret();grant.TokenHash=Hash(secret);
            foreach(string id in channels.Distinct()){
                var c=store.Channel(id);if(!c.Enabled||!AgentProviders.Codex(c))throw new InvalidOperationException("Seuls les canaux Codex locaux activés peuvent être partagés.");
                grant.Channels.Add(id);grant.Threads[c.AnchorThreadId]=id;grant.Accounts[id]=c.AccountKey;grant.Workspaces[id]=c.Workspace;
            }
            foreach(var entry in additionalThreads??new Dictionary<string,string>()){
                if(!grant.Channels.Contains(entry.Value)||String.IsNullOrWhiteSpace(entry.Key)||entry.Key.Length>200)throw new InvalidOperationException("Conversation partagée invalide.");grant.Threads[entry.Key]=entry.Value;
            }
            using(store.Lease("remote-settings")){var all=Grants(store);all.Add(grant);store.WriteRecord("remote-grants.dpapi",all);}
            return Json.Write(new RemotePeer{Id=grant.Id,DeviceId=config.DeviceId,Name=name,Host=host,Port=config.Port,Pin=config.Pin,Token=secret,Expires=grant.Expires});
        }
        public static void Revoke(RelayStore store,string id)
        {using(store.Lease("remote-settings")){var all=Grants(store);var grant=all.Single(g=>g.Id==id);grant.Revoked=true;store.WriteRecord("remote-grants.dpapi",all);}}
        public static void DisablePeer(RelayStore store,string id)
        {using(store.Lease("remote-settings")){var all=Peers(store);all.Single(p=>p.Id==id).Enabled=false;store.WriteRecord("remote-peers.dpapi",all);}}
        public static RelayChannel Connect(RelayStore store,RemotePeer peer,SharedChannel remote,string localId,string localWorkspace)
        {
            ValidatePeer(peer);RelayStore.ChannelId(localId);RelayStore.ChannelId(remote.Id);localWorkspace=RelayStore.WorkspacePath(localWorkspace);
            string home=Path.Combine(store.Root,"remote-profiles",peer.Id);SafeFiles.PrivateDirectory(home);
            var channel=new RelayChannel{Id=localId,Name=remote.Name+" · "+peer.Name,Provider="remote",PeerId=peer.Id,RemoteChannel=remote.Id,RemoteWorkspace=remote.Workspace,RemoteAccount=remote.Account,RemoteDevice=peer.DeviceId,Home=home,Workspace=localWorkspace,AccountKey="remote:"+peer.DeviceId+":"+remote.Account,AnchorThreadId=remote.Anchor,RequireFullAccess=remote.RequireFullAccess,Enabled=true,ConnectedUtc=DateTime.UtcNow.ToString("o")};
            store.Register(channel);return channel;
        }
    }
}
