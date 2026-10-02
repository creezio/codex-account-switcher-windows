using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

namespace Creezio.Switcher
{
    internal static class RelayQuota
    {
        internal static string Name(string account){using(var sha=SHA256.Create())return "quota-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(account))).Replace("-","").ToLowerInvariant()+".dpapi";}
        public static double? Read(RelayStore store,string account)
        {
            var cached=store.ReadRecord<Profile>(Name(account));if(cached.IsFresh)return cached.Score;
            try{string root=Path.GetDirectoryName(store.Root);if(!File.Exists(Path.Combine(root,"accounts.dpapi")))return null;var profile=new Vault(root).Load().Profiles.FirstOrDefault(p=>p.Key==account);return profile==null?null:profile.Score;}catch{return null;}
        }
        public static string WaitReason(RelayStore store,RelayAgent agent,string account)
        {
            if(agent==null)return null;double? remaining=Read(store,account);
            if(!remaining.HasValue&&!agent.AllowUnknownQuota)return "Limites d'utilisation inconnues ; attente d'une actualisation.";
            if(remaining.HasValue&&remaining.Value<=agent.MinRemaining)return "Limites d'utilisation insuffisantes pour cet agent ; tâche conservée en attente.";
            return null;
        }
        public static async Task RefreshWaiting(RelayStore store,CancellationToken token)
        {
            var waiting=store.Messages().Where(m=>m.State=="queued").Select(m=>m.TargetChannelId).Distinct().ToArray();
            if(waiting.Length==0)return;
            var service=new AccountService(Path.GetDirectoryName(store.Root));
            foreach(var channel in store.Channels().Where(c=>waiting.Contains(c.Id)&&c.Enabled).GroupBy(c=>c.AccountKey).Select(g=>g.First())){
                var old=store.ReadRecord<Profile>(Name(channel.AccountKey));DateTime last;if(DateTime.TryParse(old.QuotaTimeUtc,out last)&&DateTime.UtcNow-last.ToUniversalTime()<TimeSpan.FromMinutes(1))continue;
                try{
                    new DesktopRelayTransport().Verify(channel);string auth=SafeFiles.ReadText(Path.Combine(channel.Home,"auth.json"));
                    if(AuthIdentity.Parse(auth).Key!=channel.AccountKey)continue;
                    var snapshot=await service.FetchUsage(auth,token);var p=new Profile{Key=channel.AccountKey};snapshot.Apply(p,DateTime.UtcNow);
                    // Store usage only. Never rewrite the shared account vault or any live auth file.
                    p.AuthJson=null;store.WriteRecord(Name(channel.AccountKey),p);
                }catch(OperationCanceledException){throw;}catch(Exception){store.WriteRecord(Name(channel.AccountKey),new Profile{Key=channel.AccountKey,QuotaTimeUtc=DateTime.UtcNow.ToString("o"),Error="Usage unavailable"});}
            }
        }
    }
}
