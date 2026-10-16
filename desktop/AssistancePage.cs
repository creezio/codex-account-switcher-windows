using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Creezio.Switcher.Desktop
{
    internal sealed class AssistanceEntry
    {
        internal AssistanceTicket Ticket;
        internal RelayChannel Remote;
    }
    internal sealed class AssistancePage : CollectionPage
    {
        private List<AssistanceEntry> remote = new List<AssistanceEntry>();
        private bool fetching;
        internal AssistancePage(DesktopContext c,ShellWindow s):base(c,s,"Assistance","Questions de vos agents et des PC associés. Une réponse reprend le chat visible dans Codex.")
        {Command("Configurer les règles",Configure,true);Command("Actualiser les PC distants",FetchRemote);}
        internal IEnumerable<AssistanceEntry> Entries(){return new Assistance(Context.Store).List().Select(t=>new AssistanceEntry{Ticket=t}).Concat(remote);}
        internal async Task FetchRemote()
        {
            if(fetching||Context.Fixture)return;fetching=true;
            try{
                var peers=RemotePeers.Peers(Context.Store).Where(p=>p.Enabled&&RemotePeers.Future(p.Expires)).ToArray();
                var channels=Context.Store.Channels().Where(c=>c.Enabled&&AgentProviders.Kind(c)=="remote"&&peers.Any(p=>p.Id==c.PeerId)).ToArray();
                var result=new List<AssistanceEntry>();var errors=new List<string>();
                using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12))){
                    foreach(var channel in channels){
                        try{var rows=await RemoteClient.Call(peers.First(p=>p.Id==channel.PeerId),"assistance_list",channel.RemoteChannel,channel.RemoteAccount,channel.RemoteWorkspace,new{},deadline.Token);result.AddRange(RelayEngine.Rows(rows).Select(t=>new AssistanceEntry{Remote=channel,Ticket=Json.Read<AssistanceTicket>(Json.Write(t))}));}
                        catch(Exception e){errors.Add(channel.Name+" : "+Program.SafeError(e));}
                    }
                }
                remote=result;Notice.Text=errors.Count==0?"Assistance distante actualisée. Consultation toutes les minutes tant que le switcher est ouvert.":String.Join("\n",errors);await Refresh();
            }finally{fetching=false;}
        }
        public override Task Refresh()
        {
            Rows(Entries().Select(e=>new ItemRow{Id=(e.Remote?.Id??"local")+":"+e.Ticket.Id,Title=e.Ticket.Title,Summary=(e.Remote?.Name??e.Ticket.Channel)+" · "+e.Ticket.Reason,State=State(e.Ticket.State),Revision=RemotePeers.Hash((e.Ticket.Answer??"")+(e.Ticket.Error??"")),Value=e}));return Task.CompletedTask;
        }
        private static string State(string state){switch(state){case "pending":return "Réponse attendue";case "answered":return "Réponse enregistrée";case "delivering":return "Envoi en cours · voir Tâches";case "delivered":return "Tour de réponse terminé";default:return "À vérifier";}}
        protected override void ShowSelected()
        {
            Details.Children.Clear();var e=(List.SelectedItem as ItemRow)?.Value as AssistanceEntry;if(e==null)return;var t=e.Ticket;
            Details.Children.Add(Ui.Text(t.Title,23));Details.Children.Add(Ui.Text(t.Reason,14));Details.Children.Add(Ui.Text("Chat : "+t.Thread+"\n"+t.Created,12,true));
            var body=Ui.Input("Contexte communiqué",t.Context??"",Details,true);body.IsReadOnly=true;
            if(t.Answer!=null){var answer=Ui.Input("Réponse enregistrée",t.Answer,Details,true);answer.IsReadOnly=true;}
            if(t.Error!=null)Details.Children.Add(Ui.Text(t.Error,13,true));
            var actions=Ui.Actions(Details);
            if(t.Answer==null)actions.Children.Add(Ui.AsyncButton("Répondre au chat",()=>Reply(e),Error,true));
            actions.Children.Add(Ui.AsyncButton("Demander une analyse à mon agent",()=>Analyze(e),Error));
            if(t.ReplyJob!=null&&e.Remote==null)actions.Children.Add(Ui.Button("Voir l'envoi",()=>Shell.Navigate("Tâches")));
            if(t.AnalysisJob!=null&&e.Remote==null&&Context.Store.Exists(t.AnalysisJob)){var m=Context.Store.Message(t.AnalysisJob);Details.Children.Add(Ui.Text("Analyse : "+m.State,13));if(m.Result!=null){var answer=Ui.Input("Proposition de l'agent · à relire",m.Result,Details,true);answer.IsReadOnly=true;}}
        }
        private Task Reply(AssistanceEntry entry)
        {
            var d=new EditWindow(Shell,"Répondre à la demande");var answer=Ui.Input("Réponse à transmettre","",d.Fields,true);var access=Ui.Select("Action autorisée",new[]{"Lecture et analyse","Modification de fichiers","Action externe"},"Lecture et analyse",d.Fields);
            d.Fields.Children.Add(Ui.Text("La réponse sera envoyée une fois dans le chat d'origine lorsqu'il sera disponible. Les permissions natives de Codex restent applicables.",13,true));
            d.Dirty=()=>answer.Text.Length>0||access.SelectedIndex!=0;
            d.Save=async delegate{string level=new[]{"read","write","external"}[access.SelectedIndex];if(entry.Remote==null){entry.Ticket=new Assistance(Context.Store).Answer(entry.Ticket.Id,answer.Text,level);RelayWorker.Ensure(Context.Store);}else{var c=entry.Remote;var peer=RemotePeers.Peers(Context.Store).Single(p=>p.Id==c.PeerId);entry.Ticket=Json.Read<AssistanceTicket>(Json.Write(await RemoteClient.Call(peer,"assistance_reply",c.RemoteChannel,c.RemoteAccount,c.RemoteWorkspace,new{id=entry.Ticket.Id,answer=answer.Text,access=level},CancellationToken.None)));}await Refresh();};d.ShowDialog();return Task.CompletedTask;
        }
        private Task Analyze(AssistanceEntry entry)
        {
            var channels=Context.Store.Channels().Where(c=>c.Enabled&&AgentProviders.Codex(c)).ToArray();if(channels.Length==0)throw new InvalidOperationException("Connectez un agent local pour analyser la demande.");
            var d=new EditWindow(Shell,"Faire analyser la demande");var target=Ui.Select("Agent d'analyse",channels.Select(c=>c.Id),channels[0].Id,d.Fields);d.Fields.Children.Add(Ui.Text("Une nouvelle conversation visible sera créée. L'agent proposera une réponse en lecture seule ; vous déciderez ensuite de la transmettre au client.",13,true));
            string id=Guid.NewGuid().ToString("N");d.Save=async delegate{if(entry.Remote==null)await new Assistance(Context.Store).Analyze(entry.Ticket.Id,(string)target.SelectedItem,CancellationToken.None);else await new DirectMessages(Context.Store).Submit(new RelayJobSpec{Id=id,To=(string)target.SelectedItem,Access="read",Title="Analyse assistance distante",Prompt="Analyse uniquement cette demande et propose une réponse. Ne modifie aucun fichier ni service. Contexte à examiner :\n"+entry.Ticket.Reason+"\n"+entry.Ticket.Context},null,CancellationToken.None);RelayWorker.Ensure(Context.Store);Shell.Navigate("Tâches");};d.ShowDialog();return Task.CompletedTask;
        }
        private Task Configure()
        {
            var channels=Context.Store.Channels().Where(c=>c.Enabled&&AgentProviders.Codex(c)).ToArray();if(channels.Length==0)throw new InvalidOperationException("Connectez d'abord un agent local.");
            var choose=new EditWindow(Shell,"Choisir le canal à configurer");var selection=Ui.Select("Agent",channels.Select(c=>c.Id),channels[0].Id,choose.Fields);string selected=null;choose.Save=delegate{selected=(string)selection.SelectedItem;return Task.CompletedTask;};choose.ShowDialog();if(selected==null)return Task.CompletedTask;
            var c=channels.Single(x=>x.Id==selected);var rule=Assistance.Rules(Context.Store).FirstOrDefault(r=>r.Channel==selected)??new AssistanceRule{Channel=selected};var d=new EditWindow(Shell,"Règles d'assistance · "+selected);
            var enabled=Ui.Check("Autoriser l'agent à demander assistance selon ces consignes",rule.Enabled,d.Fields);var instructions=Ui.Input("Quand l'agent doit-il demander de l'aide ?",rule.Instructions??"",d.Fields,true);
            d.Fields.Children.Add(Ui.Text("Ces consignes guident l'agent ; elles ne garantissent pas la détection de toute ambiguïté. Aucun critère métier n'est imposé par défaut.",13,true));
            var hook=Ui.Check("Activer le filtre de prompts (hook Codex à approuver)",rule.HookEnabled,d.Fields);var words=Ui.Input("Expressions à détecter (une par ligne, facultatif)",rule.Keywords??"",d.Fields,true);var length=Ui.Input("Seuil de caractères (0 = désactivé)",rule.PromptLength.ToString(),d.Fields);var hold=Ui.Check("Suspendre les prompts correspondant au filtre",rule.HoldPrompt,d.Fields);
            d.Fields.Children.Add(Ui.Text("Le filtre ne s'applique qu'au dossier exact du canal. Il exige une version Codex compatible et votre validation native du hook. L'installation seule ne prouve pas son activation.",13,true));
            d.Dirty=()=>enabled.IsChecked!=rule.Enabled||instructions.Text!=(rule.Instructions??"")||words.Text!=(rule.Keywords??"")||length.Text!=rule.PromptLength.ToString()||hook.IsChecked!=rule.HookEnabled||hold.IsChecked!=rule.HoldPrompt;
            d.Save=delegate{int n;if(!Int32.TryParse(length.Text,out n))throw new InvalidOperationException("Seuil invalide.");Assistance.SaveRule(Context.Store,new AssistanceRule{Channel=selected,Enabled=enabled.IsChecked==true,Instructions=instructions.Text,Keywords=words.Text,PromptLength=n,HookEnabled=hook.IsChecked==true,HoldPrompt=hold.IsChecked==true});if(hook.IsChecked==true){string file=AssistanceHooks.Install(Context.Store,c.Home,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe"));Notice.Text="Hook installé : "+file+". À examiner et approuver dans Codex ; aucune confiance accordée automatiquement.";}return Task.CompletedTask;};d.ShowDialog();return Refresh();
        }
    }
}
