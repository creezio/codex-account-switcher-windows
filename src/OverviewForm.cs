using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class OverviewForm : Form
    {
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=5000};
        public OverviewForm(AccountService accounts,RelayStore store,Action<string> open)
        {
            Text="Vue d'ensemble";Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(248,250,247);ClientSize=new Size(950,780);
            var layout=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(24)};Controls.Add(layout);
            layout.Controls.Add(new Label{Text="Votre espace de travail",Font=new Font("Segoe UI",25,FontStyle.Bold),AutoSize=true,ForeColor=MainForm.Ink,Margin=new Padding(0,0,0,15)});
            var summary=new Label{AutoSize=true,MaximumSize=new Size(850,0),Margin=new Padding(0,0,0,18)};layout.Controls.Add(summary);
            var components=new Label{AutoSize=true,MaximumSize=new Size(850,0),Margin=new Padding(0,0,0,20)};layout.Controls.Add(components);
            var actions=new FlowLayoutPanel{AutoSize=true,MaximumSize=new Size(850,0)};layout.Controls.Add(actions);
            foreach(string section in new[]{"Assistant","Instances","Comptes","Agents","Projets","Travaux","Limites","Diagnostics"}){string s=section;var button=new Button{Text=s=="Assistant"?"Configurer mon usage":s,AutoSize=true,Height=36,Margin=new Padding(0,0,10,10)};button.Click+=delegate{open(s);};actions.Controls.Add(button);}
            layout.Controls.Add(new Label{Text="À traiter",Font=new Font("Segoe UI",15,FontStyle.Bold),AutoSize=true,Margin=new Padding(0,20,0,8)});
            var attention=new TextBox{Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.FixedSingle,BackColor=Color.White,Width=820,Height=220,ScrollBars=ScrollBars.Vertical};layout.Controls.Add(attention);
            var guide=new Label{AutoSize=true,MaximumSize=new Size(830,0),Margin=new Padding(0,18,0,0),Text="Un compte fournit une identité et des limites. Une instance possède ses conversations et ses réglages. Un agent ajoute un rôle à une connexion de projet. Les règles définissent quand et à qui déléguer.\n\nFermer cette fenêtre la masque. Les composants de fond activés continuent ; les fenêtres Codex restent indépendantes."};layout.Controls.Add(guide);
            Action refresh=delegate{try{var jobs=store.Messages();summary.Text=accounts.Data.Profiles.Count+" comptes · "+accounts.Data.Instances.Count(i=>!i.IsLocal&&!i.Archived)+" instances gérées · "+RelayPolicies.Load(store).Agents.Count+" agents configurés\n"+jobs.Count(RelayRouter.Active)+" travaux actifs · "+jobs.Count(m=>!RelayRouter.Active(m))+" travaux terminés";components.Text="Relais : "+(RelayWorker.Running(store)?"actif":"arrêté")+" · "+RelayDispatch.Caption(store)+"\nQuotas et resets : "+(UsageCoordinator.Running(store)?"supervision indépendante active":UsageCoordinator.Policies(store).Background?"supervision demandée, démarrage à vérifier":"suivis tant que le switcher reste ouvert")+"\nRéinitialisation globale : "+(accounts.Settings.AutoResetCredits?"activée (politiques par compte applicables)":"désactivée (politiques par compte applicables)");var blocked=RelayForm.Filter(jobs,"",1).Take(12).ToList();attention.Text=blocked.Count==0?"Aucune intervention demandée.":String.Join("\r\n\r\n",blocked.Select(m=>m.Title+" · "+ProductUx.JobState(m)+"\r\n"+ProductUx.Resolution(m)));}catch(Exception e){attention.Text=Program.SafeError(e);}};
            timer.Tick+=delegate{refresh();};timer.Start();FormClosed+=delegate{timer.Dispose();};refresh();ProductUx.Accessible(this);
        }
    }
    internal sealed class SetupForm : Form
    {
        public SetupForm(AccountService accounts,RelayStore store,Action<string> open)
        {
            Text="Premiers pas";Font=new Font("Segoe UI",10);ClientSize=new Size(950,780);BackColor=Color.White;
            var body=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24)};Controls.Add(body);
            body.Controls.Add(new Label{Text="Configurez votre usage",Font=new Font("Segoe UI",24,FontStyle.Bold),AutoSize=true});
            var choice=new ComboBox{Width=700,DropDownStyle=ComboBoxStyle.DropDownList};choice.Items.AddRange(new[]{"Gérer mes comptes et leurs limites","Utiliser plusieurs instances","Faire collaborer des agents"});choice.SelectedIndex=0;body.Controls.Add(choice);
            var steps=new FlowLayoutPanel{AutoSize=true,MaximumSize=new Size(840,0),FlowDirection=FlowDirection.TopDown,WrapContents=false};body.Controls.Add(steps);
            Action build=delegate{
                foreach(Control c in steps.Controls.Cast<Control>().ToArray())c.Dispose();steps.Controls.Clear();
                AddStep(steps,"1 · Enregistrer vos comptes",accounts.Data.Profiles.Count+" compte(s) enregistré(s). Importez votre connexion actuelle ou ajoutez un compte avec la connexion officielle.","Gérer les comptes",()=>open("Comptes"));
                AddStep(steps,"2 · Choisir le suivi des limites","Choisissez les comptes et le seuil. Activez la supervision indépendante pour continuer quand le switcher est fermé.","Configurer les limites",()=>open("Limites"));
                if(choice.SelectedIndex<1)return;
                AddStep(steps,"3 · Préparer les instances","Créez une instance, associez un compte autorisé puis ouvrez-la. Chaque instance conserve son propre profil.","Gérer les instances",()=>open("Instances"));
                if(choice.SelectedIndex<2)return;
                AddStep(steps,"4 · Installer l'intégration","Sélectionnez chaque profil participant. Utilisez ensuite un nouveau chat dans Codex.","Installer les skills",()=>{using(var f=new RelayIntegrationForm(store,accounts))f.ShowDialog(this);});
                AddStep(steps,"5 · Connecter le projet","Choisissez le dossier partagé. Copiez l'invitation dans un chat de chaque instance ; Codex vérifie son identité et ses permissions.","Préparer une invitation",()=>{using(var f=new RelayConnectForm())f.ShowDialog(this);});
                AddStep(steps,"6 · Définir vos rôles et règles","Créez votre propre configuration ou partez d'un modèle facultatif. Les modèles n'imposent aucun compte ni fournisseur.","Configurer les agents",()=>open("Agents"));
                AddStep(steps,"7 · Vérifier puis échanger","Simulez le routage sans exécution. Pour le premier échange, envoyez une demande simple de réponse sans outil.","Simuler",()=>{using(var f=new RoutingPreviewForm(store,RelayPolicies.Load(store)))f.ShowDialog(this);});
                AddStep(steps,"8 · Envoyer le premier travail","La demande reste modifiable avant envoi. Vérifiez son destinataire et consultez ensuite son résultat dans Travaux.","Nouvelle demande",()=>{using(var f=new RelayComposeForm(store,null,true))if(f.ShowDialog(this)==DialogResult.OK)RelayWorker.Ensure(store);});
            };
            choice.SelectedIndexChanged+=delegate{build();};build();ProductUx.Accessible(this);
        }
        private static void AddStep(FlowLayoutPanel parent,string title,string text,string button,Action action)
        {
            var panel=new FlowLayoutPanel{AutoSize=true,MaximumSize=new Size(800,0),FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(0,18,0,0)};parent.Controls.Add(panel);
            panel.Controls.Add(new Label{Text=title,Font=new Font("Segoe UI",12,FontStyle.Bold),AutoSize=true});panel.Controls.Add(new Label{Text=text,AutoSize=true,MaximumSize=new Size(780,0)});var b=new Button{Text=button,AutoSize=true};b.Click+=delegate{action();};panel.Controls.Add(b);
        }
    }
}
