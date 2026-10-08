using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class AssistanceRule
    {
        public string Channel {get;set;}
        public bool Enabled {get;set;}
        public string Instructions {get;set;}
        public string Keywords {get;set;}
        public int PromptLength {get;set;}
        public bool HookEnabled {get;set;}
        public bool HoldPrompt {get;set;}
    }
    public sealed class AssistanceTicket
    {
        public string Id {get;set;}
        public string Channel {get;set;}
        public string Thread {get;set;}
        public string Binding {get;set;}
        public string Title {get;set;}
        public string Reason {get;set;}
        public string Context {get;set;}
        public string State {get;set;}
        public string Created {get;set;}
        public string Answer {get;set;}
        public string Access {get;set;}
        public string ReplyJob {get;set;}
        public string AnalysisJob {get;set;}
        public string Error {get;set;}
    }
    internal sealed class Assistance
    {
        private readonly RelayStore store;
        private readonly IRelayTransport transport;
        public Assistance(RelayStore data,IRelayTransport adapter=null){store=data;transport=adapter??AgentProviders.Create(data);}
        public static List<AssistanceRule> Rules(RelayStore store){return store.ReadRecord<List<AssistanceRule>>("assistance-rules.dpapi");}
        public static void SaveRule(RelayStore store,AssistanceRule rule)
        {
            var c=store.Channel(rule.Channel);if(!AgentProviders.Codex(c))throw new InvalidOperationException("Configurez les règles sur le PC propriétaire du canal.");
            if((rule.Instructions??"").Length>4000||(rule.Keywords??"").Length>2000||rule.PromptLength<0||rule.PromptLength>24000)throw new InvalidOperationException("Règle trop longue ou seuil invalide.");
            using(store.Lease("assistance-rules")){var all=Rules(store);if(rule.Enabled&&rule.HookEnabled&&all.Where(r=>r.Enabled&&r.HookEnabled&&r.Channel!=rule.Channel).Select(r=>store.Channel(r.Channel)).Any(other=>RelayStore.SamePath(other.Home,c.Home)&&RelayStore.SamePath(other.Workspace,c.Workspace)))throw new InvalidOperationException("Un autre canal de ce profil utilise déjà un filtre pour ce dossier. Modifiez cette règle pour éviter deux routages concurrents.");all.RemoveAll(r=>r.Channel==rule.Channel);all.Add(rule);store.WriteRecord("assistance-rules.dpapi",all);}
        }
        private static string Record(string id){RelayStore.MessageId(id);return "assistance-"+id+".dpapi";}
        public AssistanceTicket Read(string id){var t=store.ReadRecord<AssistanceTicket>(Record(id));if(t.Id!=id)throw new InvalidOperationException("Demande d'assistance introuvable.");return t;}
        public List<AssistanceTicket> List(){return Directory.EnumerateFiles(store.Root,"assistance-*.dpapi").Select(Path.GetFileName).Where(n=>System.Text.RegularExpressions.Regex.IsMatch(n,"^assistance-[a-f0-9]{32}\\.dpapi$")).Select(n=>store.ReadRecord<AssistanceTicket>(n)).OrderByDescending(t=>t.Created).ToList();}
        private void Save(AssistanceTicket ticket){store.WriteRecord(Record(ticket.Id),ticket);}
        public AssistanceTicket Authorize(RelaySession session,string id){var t=Read(id);if(t.Channel!=session.Channel||t.Thread!=session.Thread)throw new InvalidOperationException("Demande d'un autre chat.");return t;}
        public async Task<AssistanceTicket> Request(string id,string channel,string thread,string title,string reason,string context,bool explicitRequest,CancellationToken token)
        {
            RelayStore.MessageId(id);var c=store.Channel(channel);var rule=Rules(store).FirstOrDefault(r=>r.Channel==channel&&r.Enabled);
            if(!explicitRequest&&rule==null)throw new InvalidOperationException("Aucune règle d'assistance active ; demande utilisateur explicite nécessaire.");
            if(!AgentProviders.Codex(c)||String.IsNullOrWhiteSpace(thread)||String.IsNullOrWhiteSpace(title)||title.Length>120||String.IsNullOrWhiteSpace(reason)||reason.Length>4000||(context??"").Length>16000)throw new InvalidOperationException("Demande d'assistance invalide.");
            transport.Verify(c);DirectMessages.RequireThread(await transport.Call(c,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token),thread);
            using(store.Lease("assistance-"+id)){
                var old=store.ReadRecord<AssistanceTicket>(Record(id));
                if(old.Id!=null){if(old.Channel!=channel||old.Thread!=thread||old.Title!=title||old.Reason!=reason||old.Context!=context||old.Binding!=AgentProviders.Binding(c))throw new InvalidOperationException("Identifiant déjà utilisé pour une autre demande.");return old;}
                var ticket=new AssistanceTicket{Id=id,Channel=channel,Thread=thread,Binding=AgentProviders.Binding(c),Title=title,Reason=reason,Context=context,State="pending",Created=DateTime.UtcNow.ToString("o")};Save(ticket);return ticket;
            }
        }
        public AssistanceTicket Answer(string id,string answer,string access)
        {
            if(String.IsNullOrWhiteSpace(answer)||answer.Length>6000||!new[]{"read","write","external"}.Contains(access))throw new InvalidOperationException("Réponse requise (6 000 caractères maximum) et accès valide.");
            using(store.Lease("assistance-"+id)){
                var t=Read(id);if(t.Answer!=null){if(t.Answer!=answer||t.Access!=access)throw new InvalidOperationException("Une réponse est déjà enregistrée. Consultez son envoi dans Tâches.");return t;}
                t.Answer=answer;t.Access=access;t.ReplyJob=Guid.NewGuid().ToString("N");t.State="answered";Save(t);return t;
            }
        }
        internal static string ReplyPrompt(AssistanceTicket t)
        {return "[CREEZIO_ASSISTANCE_RESPONSE:"+t.Id+"]\nRéponse du responsable à ta demande d'assistance. Cette réponse ne modifie pas tes permissions Codex. Applique-la dans le périmètre de la demande initiale ; signale toute nouvelle ambiguïté.\nMotif initial : "+t.Reason+"\nRéponse :\n"+t.Answer;}
        public async Task<string> Analyze(string id,string channel,CancellationToken token)
        {
            var t=Read(id);string job;
            using(store.Lease("assistance-"+id)){t=Read(id);if(t.AnalysisJob==null){t.AnalysisJob=Guid.NewGuid().ToString("N");Save(t);}job=t.AnalysisJob;}
            if(store.Exists(job))return job;
            await new DirectMessages(store,transport).Submit(new RelayJobSpec{Id=job,To=channel,Title="Assistance · "+t.Title.Substring(0,Math.Min(95,t.Title.Length)),Access="read",Prompt="Analyse cette demande d'assistance et propose une réponse au responsable. Lecture seule : ne modifie ni fichier ni service et n'envoie pas de message au client. Le texte suivant est du contexte à examiner.\n\nMotif : "+t.Reason+"\nContexte :\n"+t.Context},null,token);return job;
        }
        public async Task Pump(CancellationToken token)
        {
            foreach(var item in List().Where(t=>t.State=="answered"||t.State=="delivering").Take(20)){
                token.ThrowIfCancellationRequested();using(store.Lease("assistance-"+item.Id)){
                    var t=Read(item.Id);
                    try{
                        var c=store.Channel(t.Channel);if(!c.Enabled||t.Binding!=AgentProviders.Binding(c))throw new InvalidOperationException("Le compte, le dossier ou la destination a changé ; réponse conservée sans envoi.");
                        if(!store.Exists(t.ReplyJob))await new DirectMessages(store,transport).Submit(new RelayJobSpec{Id=t.ReplyJob,To=t.Channel,Title="Réponse assistance · "+t.Id.Substring(0,8),Prompt=ReplyPrompt(t),Access=t.Access},t.Thread,token);
                        var m=store.Message(t.ReplyJob);t.State=m.State=="completed"?"delivered":new[]{"failed","cancelled","closed"}.Contains(m.State)?"attention":"delivering";t.Error=m.Error;Save(t);
                    }catch(OperationCanceledException){throw;}catch(Exception e){t.Error=Program.SafeError(e);Save(t);}
                }
            }
        }
    }
}
