using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class RelaySession
    {
        public string Token {get;set;}
        public string Channel {get;set;}
        public string Thread {get;set;}
        public string Home {get;set;}
        public string Account {get;set;}
        public string Expires {get;set;}
    }
    internal static class RelaySessions
    {
        private static string Record(string token){if(token==null||!System.Text.RegularExpressions.Regex.IsMatch(token,"^[a-f0-9]{64}$"))throw new InvalidOperationException("Session du relais absente ou invalide. Utilisez le script de connexion dans ce chat.");return "session-"+token+".dpapi";}
        public static async Task<RelaySession> Bind(RelayStore store,string channelId,CancellationToken token)
        {
            string thread=Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            if(String.IsNullOrEmpty(thread))throw new InvalidOperationException("La connexion doit être exécutée comme commande dans le chat appelant.");
            string home=CodexEnvironment.DefaultHome();
            var candidates=store.Channels().Where(c=>c.Enabled&&RelayStore.SamePath(c.Home,home)&&(String.IsNullOrEmpty(channelId)||c.Id==channelId)).ToList();
            if(candidates.Count!=1)throw new InvalidOperationException("Indiquez le canal à utiliser dans ce profil : "+String.Join(", ",candidates.Select(c=>c.Id)));
            var channel=candidates[0];RelayCommand.RequireSource(channel);
            var transport=new DesktopRelayTransport();var snapshot=await transport.Call(channel,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token);
            if(Json.Str(Json.Get(Json.Get(snapshot,"thread"),"id"))!=thread)throw new InvalidOperationException("Chat appelant non reconnu.");
            byte[] bytes=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);
            var session=new RelaySession{Token=BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant(),Channel=channel.Id,Thread=thread,Home=home,Account=channel.AccountKey,Expires=DateTime.UtcNow.AddDays(7).ToString("o")};
            store.WriteRecord(Record(session.Token),session);return session;
        }
        public static RelaySession Resolve(RelayStore store,string token,string expectedHome,bool verifyLive=true)
        {
            var session=store.ReadRecord<RelaySession>(Record(token));DateTime expiry;
            if(session.Token!=token||!DateTime.TryParse(session.Expires,out expiry)||expiry.ToUniversalTime()<DateTime.UtcNow||!RelayStore.SamePath(session.Home,expectedHome))throw new InvalidOperationException("Session expirée ou appartenant à une autre instance.");
            var channel=store.Channel(session.Channel);
            if(channel.AccountKey!=session.Account||!RelayStore.SamePath(channel.Home,session.Home)||!channel.Enabled)throw new InvalidOperationException("Le compte ou le canal de cette session a changé.");
            if(verifyLive)new DesktopRelayTransport().Verify(channel);return session;
        }
        public static RelayMessage Authorize(RelayStore store,RelaySession session,string id,bool originOnly)
        {
            var m=store.Message(id);bool source=m.SourceChannelId==session.Channel&&m.SourceThreadId==session.Thread;
            bool target=m.TargetChannelId==session.Channel&&m.TargetThreadId==session.Thread;
            if(!source&&(originOnly||!target))throw new InvalidOperationException("Cette tâche n'appartient pas à la conversation connectée.");return m;
        }
    }
}
