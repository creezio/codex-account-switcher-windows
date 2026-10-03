using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class RemotePage : CollectionPage
    {
        public RemotePage(DesktopContext c,ShellWindow s):base(c,s,"PC distants","Partagez uniquement les agents et conversations choisis. Codex reste ouvert sur chaque PC.")
        {
            Command("Partager ce PC",Configure,true);
            Command("Créer une invitation",Invite);
            Command("Associer un PC",Import);
        }
        public override Task Refresh()
        {
            var peers=RemotePeers.Peers(Context.Store);var grants=RemotePeers.Grants(Context.Store);
            Rows(peers.Select(p=>new ItemRow{Id="peer-"+p.Id,Title=p.Name,Summary=p.Host+":"+p.Port,State=p.Enabled&&RemotePeers.Future(p.Expires)?"PC associé":"Désactivé ou expiré",Value=p})
                .Concat(grants.Select(g=>new ItemRow{Id="grant-"+g.Id,Title=g.Name,Summary="Accès à ce PC · "+String.Join(", ",g.Channels),State=g.Revoked?"Révoqué":!RemotePeers.Future(g.Expires)?"Expiré":g.CanSend?"Lecture et envoi":"Lecture seule",Value=g})).Where(r=>(r.Title+" "+r.Summary).IndexOf(Search.Text,StringComparison.OrdinalIgnoreCase)>=0));
            var config=RemotePeers.Config(Context.Store);var state=RemoteGateway.Status(Context.Store);
            Notice.Text=!config.Enabled?"Partage de ce PC désactivé":DesktopRuntime.SameProcess(state.Pid,state.Started)?"Partage actif · "+config.BindAddress+":"+config.Port:"Partage configuré, service arrêté. "+state.Error;
            return Task.CompletedTask;
        }
        protected override void ShowSelected()
        {
            Details.Children.Clear();var row=List.SelectedItem as ItemRow;if(row==null)return;
            Details.Children.Add(Ui.Text(row.Title,23));Details.Children.Add(Ui.Text(row.Summary,14,true));var actions=Ui.Actions(Details);
            if(row.Value is RemotePeer peer){
                Details.Children.Add(Ui.Text("Expire le "+peer.Expires+"\nEmpreinte du PC : "+peer.Pin,12,true));
                actions.Children.Add(Ui.AsyncButton("Connecter un agent partagé",()=>Connect(peer),Error,true));
                actions.Children.Add(Ui.AsyncButton("Consulter les conversations",()=>Browse(peer),Error));
                actions.Children.Add(Ui.AsyncButton("Vérifier la connexion",async delegate{var channels=await RemoteClient.Call(peer,"channels",null,null,null,new{},CancellationToken.None);Notice.Text=RelayEngine.Rows(channels).Count()+" agent(s) partagé(s) accessibles.";},Error));
                actions.Children.Add(Ui.AsyncButton("Désactiver",async delegate{RemotePeers.DisablePeer(Context.Store,peer.Id);await Refresh();},Error));
            }else if(row.Value is RemoteGrant grant){
                Details.Children.Add(Ui.Text("Conversations partagées : "+grant.Threads.Count+"\nCréer des chats : "+(grant.CanCreate?"Oui":"Non")+"\nAssistance : "+(grant.CanAssist?"Oui":"Non")+"\nExpire le "+grant.Expires,14,true));
                actions.Children.Add(Ui.AsyncButton("Révoquer cet accès",async delegate{if(Ui.Confirm(Shell,"Révoquer cet accès ? Les prochaines lectures et demandes seront refusées. Les tâches déjà lancées dans Codex continuent.","Révoquer")){RemotePeers.Revoke(Context.Store,grant.Id);await Refresh();}},Error));
            }
        }
        private Task Configure()
        {
            var config=RemotePeers.Config(Context.Store);var d=new EditWindow(Shell,"Partage de ce PC");
            var enabled=Ui.Check("Autoriser les PC associés à se connecter",config.Enabled,d.Fields);
            var address=Ui.Input("Adresse IP d'écoute",config.BindAddress,d.Fields);var port=Ui.Input("Port",config.Port.ToString(),d.Fields);
            d.Fields.Children.Add(Ui.Text("127.0.0.1 limite l'accès à ce PC ou à un tunnel SSH. Pour votre réseau privé/VPN, indiquez l'adresse IP de cette machine. Aucun port du pare-feu n'est ouvert automatiquement.",13,true));
            d.Fields.Children.Add(Ui.Text("Le service peut rester actif avec la fenêtre du switcher fermée. Désactivez ce partage pour l'arrêter. Codex doit rester ouvert.",13,true));
            d.Dirty=()=>enabled.IsChecked!=config.Enabled||address.Text!=config.BindAddress||port.Text!=config.Port.ToString();
            d.Save=async delegate{int p;if(!Int32.TryParse(port.Text,out p))throw new InvalidOperationException("Port invalide.");var old=RemoteGateway.Status(Context.Store);bool changed=config.BindAddress!=address.Text.Trim()||config.Port!=p;RemotePeers.SaveConfig(Context.Store,new RemoteConfig{Enabled=enabled.IsChecked==true,BindAddress=address.Text.Trim(),Port=p});if(enabled.IsChecked==true){if(changed&&DesktopRuntime.SameProcess(old.Pid,old.Started)){for(int n=0;n<40&&DesktopRuntime.SameProcess(old.Pid,old.Started);n++)await Task.Delay(100);if(DesktopRuntime.SameProcess(old.Pid,old.Started))throw new InvalidOperationException("L'ancien écouteur termine ses requêtes. Réouvrez ce réglage puis enregistrez pour démarrer sur la nouvelle adresse.");}RemoteGateway.Start(Context.Store);}await Refresh();};d.ShowDialog();return Task.CompletedTask;
        }
        private Task Invite()
        {
            var channels=Context.Store.Channels().Where(c=>c.Enabled&&AgentProviders.Codex(c)).ToArray();if(channels.Length==0)throw new InvalidOperationException("Connectez d'abord un canal Codex local depuis Agents.");
            var d=new EditWindow(Shell,"Créer une invitation");var name=Ui.Input("Nom de la personne autorisée","",d.Fields);var host=Ui.Input("Adresse de ce PC vue par cette personne",RemotePeers.Config(Context.Store).BindAddress,d.Fields);
            var channel=Ui.Select("Agent partagé",channels.Select(c=>c.Id),channels[0].Id,d.Fields);
            var send=Ui.Check("Autoriser l'envoi de messages",false,d.Fields);var create=Ui.Check("Autoriser de nouvelles conversations",false,d.Fields);var assist=Ui.Check("Autoriser les réponses aux demandes d'assistance",false,d.Fields);
            var threads=Ui.Input("Autres identifiants de conversations à partager (un par ligne)","",d.Fields,true);var days=Ui.Input("Durée en jours (1 à 30)","7",d.Fields);
            d.Fields.Children.Add(Ui.Text("Le chat de connexion de cet agent sera partagé. Les autres chats restent privés, sauf ceux ajoutés ici ou créés via cette invitation. Les fichiers et identifiants de comptes restent sur ce PC.",13,true));
            string invitation=null;d.Save=delegate{int duration;if(!Int32.TryParse(days.Text,out duration))throw new InvalidOperationException("Durée invalide.");var extra=RelayPolicies.Paths(threads.Text).ToDictionary(t=>t,t=>(string)channel.SelectedItem);invitation=RemotePeers.Invite(Context.Store,name.Text.Trim(),host.Text.Trim(),new[]{(string)channel.SelectedItem},send.IsChecked==true,create.IsChecked==true,assist.IsChecked==true,duration,extra);return Task.CompletedTask;};d.ShowDialog();
            if(invitation!=null){var show=new EditWindow(Shell,"Invitation créée");var text=Ui.Input("Secret d'association : transmettre seulement à la personne autorisée",invitation,show.Fields,true);text.IsReadOnly=true;show.Fields.Children.Add(Ui.Button("Copier l'invitation",()=>Clipboard.SetText(invitation)));show.ShowDialog();}return Refresh();
        }
        private Task Import()
        {
            var d=new EditWindow(Shell,"Associer un PC");var text=Ui.Input("Invitation reçue du propriétaire du PC","",d.Fields,true);
            d.Dirty=()=>text.Text.Length>0;
            d.Fields.Children.Add(Ui.Text("L'invitation contient l'adresse, l'empreinte du certificat et un secret d'accès. Vérifiez son origine. Elle sera conservée chiffrée sur ce PC.",13,true));
            d.Save=async delegate{var proposed=Json.Read<RemotePeer>(text.Text);RemotePeers.ValidatePeer(proposed);await RemoteClient.Call(proposed,"channels",null,null,null,new{},CancellationToken.None);RemotePeers.Import(Context.Store,text.Text);await Refresh();};d.ShowDialog();return Task.CompletedTask;
        }
        private async Task Connect(RemotePeer peer)
        {
            var result=await RemoteClient.Call(peer,"channels",null,null,null,new{},CancellationToken.None);var available=RelayEngine.Rows(result).Select(x=>Json.Read<SharedChannel>(Json.Write(x))).ToArray();if(available.Length==0)throw new InvalidOperationException("Aucun agent partagé disponible.");
            var d=new EditWindow(Shell,"Connecter un agent distant");var remote=Ui.Select("Agent distant",available.Select(c=>c.Id),available[0].Id,d.Fields);var id=Ui.Input("Nom local du canal","distant-"+Guid.NewGuid().ToString("N").Substring(0,6),d.Fields);
            var workspace=Ui.Input("Dossier local correspondant (facultatif)","",d.Fields);d.Fields.Children.Add(Ui.Text("Ce dossier sert aux règles locales et à la vérification des versions. Les fichiers ne sont pas copiés sur l'autre PC. Vide : crée un espace de correspondance vide.",13,true));
            d.Save=delegate{string path=workspace.Text.Trim();if(path.Length==0){RelayStore.ChannelId(id.Text);path=Path.Combine(Context.Root,"remote-workspaces",id.Text);SafeFiles.PrivateDirectory(path);}var c=RemotePeers.Connect(Context.Store,peer,available.Single(a=>a.Id==(string)remote.SelectedItem),id.Text,path);var policy=RelayPolicies.Load(Context.Store);if(!policy.Agents.Any(a=>a.Channel==c.Id)){policy.Agents.Add(new RelayAgent{Channel=c.Id,Description=c.Name});RelayPolicies.Save(Context.Store,policy);}return Task.CompletedTask;};d.ShowDialog();await Refresh();
        }
        private async Task Browse(RemotePeer peer)
        {
            var available=RelayEngine.Rows(await RemoteClient.Call(peer,"channels",null,null,null,new{},CancellationToken.None)).Select(x=>Json.Read<SharedChannel>(Json.Write(x))).ToArray();if(available.Length==0)throw new InvalidOperationException("Aucun agent partagé.");
            var d=new EditWindow(Shell,"Conversations partagées · "+peer.Name);var channel=Ui.Select("Agent",available.Select(c=>c.Id),available[0].Id,d.Fields);var chats=new ComboBox{DisplayMemberPath="Title",Margin=new Thickness(0,8,0,8)};d.Fields.Children.Add(chats);var transcript=Ui.Input("Messages utilisateur et réponses", "",d.Fields,true);transcript.IsReadOnly=true;transcript.MinHeight=260;var info=Ui.Text("Seules les conversations partagées sont consultables. Les sorties d'outils sont exclues.",13,true);d.Fields.Children.Add(info);
            Func<SharedChannel> selected=()=>available.Single(c=>c.Id==(string)channel.SelectedItem);
            d.Fields.Children.Add(Ui.AsyncButton("Charger les conversations",async delegate{var c=selected();var rows=await RemoteClient.Call(peer,"list_threads",c.Id,c.Account,c.Workspace,new{},CancellationToken.None);chats.ItemsSource=RelayEngine.Rows(Json.Get(rows,"threads")).Select(t=>new ConversationChoice{Id=Json.Str(Json.Get(t,"threadId")),Title=Json.Str(Json.Get(t,"title"))}).ToArray();chats.SelectedIndex=0;},e=>info.Text=Program.SafeError(e)));
            d.Fields.Children.Add(Ui.AsyncButton("Lire / actualiser le chat",async delegate{var choice=chats.SelectedItem as ConversationChoice;if(choice==null)throw new InvalidOperationException("Sélectionnez une conversation.");var c=selected();var snapshot=await RemoteClient.Call(peer,"read_thread",c.Id,c.Account,c.Workspace,new{threadId=choice.Id,turnLimit=8},CancellationToken.None);transcript.Text=String.Join("\n\n",RelayEngine.Rows(Json.Get(snapshot,"turns")).SelectMany(t=>RelayEngine.Rows(Json.Get(t,"items"))).Select(i=>(Json.Str(Json.Get(i,"type"))=="userMessage"?"Utilisateur : ":"Codex : ")+(Json.Get(i,"text")!=null?Json.Str(Json.Get(i,"text")):String.Join("\n",RelayEngine.Rows(Json.Get(i,"content")).Select(b=>Json.Str(Json.Get(b,"text")))))));info.Text="Derniers 8 tours · actualisé à "+DateTime.Now.ToString("HH:mm:ss");},e=>info.Text=Program.SafeError(e)));
            channel.SelectionChanged+=delegate{chats.ItemsSource=null;transcript.Text="";};d.ShowDialog();
        }
    }
}
