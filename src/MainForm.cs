using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class MainForm : Form
    {
        internal static readonly Color Ink=Color.FromArgb(23,40,39), Muted=Color.FromArgb(101,117,114), Green=Color.FromArgb(18,114,91), Pale=Color.FromArgb(236,244,239), Line=Color.FromArgb(225,232,228);
        private readonly AccountService service;
        private readonly bool demo;
        private readonly FlowLayoutPanel cards=new FlowLayoutPanel();
        private readonly Label subtitle=new Label(), status=new Label();
        private readonly Button cancel=new Button();
        private readonly List<Control> actions=new List<Control>();
        private readonly HashSet<string> notified=new HashSet<string>();
        private NotifyIcon tray;
        private System.Windows.Forms.Timer timer;
        private readonly CheckBox resetToggle=new CheckBox();
        private DateTime lastFullRefreshUtc=DateTime.MinValue;
        private readonly HashSet<string> resetNotified=new HashSet<string>();
        private bool busy, exiting;
        private CancellationTokenSource operation;
        private List<Profile> demoProfiles;
        private List<DesktopInstance> demoInstances;
        private bool instancesView=true, showArchived;
        private readonly Label heading=new Label(), eyebrow=new Label();
        private FlowLayoutPanel toolbar;
        private System.Windows.Forms.Timer instanceTimer;
        private string instanceFingerprint;
        private Button instanceNav,accountNav;
        private RelayStore relayStore;
        private System.Windows.Forms.Timer relayTimer;
        private readonly CancellationTokenSource relayLifetime=new CancellationTokenSource();
        private bool relayBusy;
        public MainForm(AccountService accounts,bool isDemo)
        {
            service=accounts; demo=isDemo;
            Text="Codex Account Switcher · Creezio"; ClientSize=new Size(1240,870); MinimumSize=new Size(1080,720);
            StartPosition=FormStartPosition.CenterScreen; BackColor=Color.FromArgb(248,250,247); ForeColor=Ink;
            Font=new Font("Segoe UI",10); AutoScaleMode=AutoScaleMode.Dpi; Icon=SystemIcons.Application;
            var sidebar=new Panel {Dock=DockStyle.Left,Width=230,BackColor=Color.FromArgb(23,48,42),Padding=new Padding(22)};
            Controls.Add(sidebar);
            var brand=LabelAt("creezio",26,32,180,45,27,FontStyle.Bold,Color.White); sidebar.Controls.Add(brand);
            sidebar.Controls.Add(LabelAt("CODEX\nPOUR WINDOWS",27,88,182,42,8,FontStyle.Bold,Color.FromArgb(171,201,183)));
            var intro=LabelAt("Vos comptes.\nVotre espace.",27,148,179,63,10,FontStyle.Regular,Color.FromArgb(216,231,220)); intro.AutoEllipsis=false; sidebar.Controls.Add(intro);
            instanceNav=SideButton("Instances",229,async delegate {instancesView=true;BuildToolbar();await Task.FromResult(0);});sidebar.Controls.Add(instanceNav);
            accountNav=SideButton("Comptes",277,async delegate {instancesView=false;BuildToolbar();await Task.FromResult(0);});sidebar.Controls.Add(accountNav);
            sidebar.Controls.Add(SideButton("Relais",325,delegate {using(var f=new RelayForm(relayStore))f.ShowDialog(this);return Task.FromResult(0);}));
            var prefs=SideButton("Paramètres",383,async delegate { ShowSettings(); await Task.FromResult(0); }); sidebar.Controls.Add(prefs);
            var help=SideButton("Guide d'utilisation",431,async delegate { Process.Start(new ProcessStartInfo("https://github.com/creezio/codex-account-switcher-windows#readme") {UseShellExecute=true}); await Task.FromResult(0); }); sidebar.Controls.Add(help);
            resetToggle.SetBounds(26,485,178,68);resetToggle.Text="Reset des limites\nà 1 % restant";resetToggle.ForeColor=Color.White;resetToggle.Checked=demo || service.Settings.AutoResetCredits;
            resetToggle.CheckedChanged+=delegate {
                if(demo || resetToggle.Checked==service.Settings.AutoResetCredits) return;
                bool previous=service.Settings.AutoResetCredits;service.Settings.AutoResetCredits=resetToggle.Checked;
                try {service.Vault.SaveSettings(service.Settings);status.Text=resetToggle.Checked?"Reset activé : chaque compte utilisé est vérifié une fois par minute, même s'il est partagé entre plusieurs instances.":"Reset automatique désactivé. Aucune réinitialisation ne sera consommée automatiquement.";}
                catch(Exception error) {service.Settings.AutoResetCredits=previous;resetToggle.Checked=previous;MessageBox.Show(this,Program.SafeError(error),"Creezio");}
            };
            sidebar.Controls.Add(resetToggle);actions.Add(resetToggle);
            var footer=new Label {Text="PROFILS INDÉPENDANTS\nMessages entre instances.\n\nCreezio · v0.4.0",Dock=DockStyle.Bottom,Height=105,ForeColor=Color.FromArgb(171,201,183),Font=new Font("Segoe UI",9)}; sidebar.Controls.Add(footer);
            var content=new Panel {Dock=DockStyle.Fill,Padding=new Padding(30,0,30,0)}; Controls.Add(content); content.BringToFront();
            var header=new Panel {Dock=DockStyle.Top,Height=181}; content.Controls.Add(header);
            eyebrow.SetBounds(0,25,850,22);eyebrow.Font=new Font("Segoe UI",9,FontStyle.Bold);eyebrow.ForeColor=Green;header.Controls.Add(eyebrow);
            heading.SetBounds(0,52,880,47);heading.Font=new Font("Segoe UI",27,FontStyle.Bold);header.Controls.Add(heading);
            subtitle.SetBounds(1,105,900,30); subtitle.ForeColor=Muted; subtitle.Font=new Font("Segoe UI",10); header.Controls.Add(subtitle);
            toolbar=new FlowLayoutPanel {Left=0,Top=139,Width=930,Height=39,WrapContents=false,Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top}; header.Controls.Add(toolbar);
            cancel.Text="Annuler"; cancel.AutoSize=true; cancel.Height=34; cancel.FlatStyle=FlatStyle.Flat; cancel.Visible=false; cancel.Click+=delegate {if(operation!=null) operation.Cancel();};
            var bottom=new Panel {Dock=DockStyle.Bottom,Height=69,Padding=new Padding(0,14,0,0)}; content.Controls.Add(bottom);
            status.Dock=DockStyle.Fill; status.ForeColor=Muted; status.Font=new Font("Segoe UI",9); status.Text="Chaque instance garde ses conversations et ses réglages. Un compte partagé conserve les mêmes limites d'utilisation."; bottom.Controls.Add(status);
            cards.Dock=DockStyle.Fill; cards.FlowDirection=FlowDirection.TopDown; cards.WrapContents=false; cards.AutoScroll=true; cards.Padding=new Padding(0,7,0,10); content.Controls.Add(cards); cards.BringToFront();
            cards.Resize+=delegate { foreach(Control card in cards.Controls) card.Width=Math.Max(520,cards.ClientSize.Width-23); };
            if(demo) {
                demoProfiles=DemoData();
                demoInstances=new List<DesktopInstance>{new DesktopInstance{Id="local",Name="Session habituelle",AccountKey=demoProfiles[0].Key},new DesktopInstance{Id=new string('a',32),Name="Développement",AccountKey=demoProfiles[1].Key},new DesktopInstance{Id=new string('b',32),Name="Projets personnels",AccountKey=demoProfiles[0].Key}};
                demoProfiles[1].AllInstances=false;demoProfiles[1].InstanceIds.Add(new string('a',32));
            }
            else
            {
                relayStore=new RelayStore(RelayStore.DefaultRoot);var relayEngine=new RelayEngine(relayStore);
                relayTimer=new System.Windows.Forms.Timer{Interval=4000};
                relayTimer.Tick+=async delegate {if(relayBusy)return;relayBusy=true;try{await relayEngine.Pump(relayLifetime.Token);}catch(OperationCanceledException){}catch(Exception e){status.Text="Relais : "+Program.SafeError(e);}finally{relayBusy=false;if(exiting)Close();}};relayTimer.Start();
                var menu=new ContextMenuStrip();
                menu.Items.Add("Ouvrir",null,delegate { Show(); WindowState=FormWindowState.Normal; Activate(); });
                menu.Items.Add("Actualiser les quotas",null,async delegate { await RunOperation(RefreshAll); });
                menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Quitter",null,delegate { exiting=true; Close(); });
                tray=new NotifyIcon {Icon=SystemIcons.Application,Text="Creezio · Codex Account Switcher",Visible=true,ContextMenuStrip=menu};
                foreach(var profile in service.Data.Profiles) if(profile.ResetAttempt!=null && profile.ResetAttempt.State=="recovered") resetNotified.Add(profile.ResetAttempt.IdempotencyKey);
                tray.DoubleClick+=delegate { Show(); WindowState=FormWindowState.Normal; Activate(); };
                timer=new System.Windows.Forms.Timer {Interval=60000};
                timer.Tick+=async delegate {if(!busy) await Poll();}; timer.Start();
                instanceTimer=new System.Windows.Forms.Timer {Interval=3000};
                instanceTimer.Tick+=delegate {if(!busy && instancesView && !cards.Controls.Cast<Control>().SelectMany(c=>c.Controls.Cast<Control>()).OfType<ComboBox>().Any(c=>c.Focused || c.DroppedDown)) {
                    string current=String.Join("|",service.Data.Instances.Where(i=>!i.Archived).Select(i=>{var s=service.Instances.Runtime.Probe(i);return i.Id+":"+s.Phase+":"+s.NetworkWarning+":"+service.Instances.ActiveKey(i);}));
                    if(current!=instanceFingerprint) {instanceFingerprint=current;Redraw();}
                }};instanceTimer.Start();
                Shown+=async delegate {await Poll();};
            }
            FormClosing+=OnClosing;
            BuildToolbar();
            Redraw();
        }
        private void BuildToolbar()
        {
            instanceNav.BackColor=instancesView?Color.FromArgb(50,83,68):Color.FromArgb(23,48,42);
            accountNav.BackColor=!instancesView?Color.FromArgb(50,83,68):Color.FromArgb(23,48,42);
            toolbar.Controls.Remove(cancel);
            foreach(Control control in toolbar.Controls.Cast<Control>().ToArray()) control.Dispose();toolbar.Controls.Clear();
            if(instancesView) {
                eyebrow.Text="VOS ESPACES CODEX";heading.Text="Plusieurs comptes. En parallèle.";
                toolbar.Controls.Add(MakeButton("+  Créer une instance",async delegate {string name=TextPrompt.Ask(this,"Nouvelle instance","Nouvel espace");if(name!=null){service.Instances.Create(name);status.Text="Instance créée. Choisissez un compte autorisé ou ouvrez-la pour vous connecter.";}await Task.FromResult(0);},true));
                toolbar.Controls.Add(MakeButton("Gérer les comptes",async delegate {instancesView=false;BuildToolbar();await Task.FromResult(0);},false));
                toolbar.Controls.Add(MakeButton(showArchived?"Masquer les archives":"Voir les archives",async delegate {showArchived=!showArchived;BuildToolbar();await Task.FromResult(0);},false));
            } else {
                eyebrow.Text="VOS COMPTES CODEX";heading.Text="La bonne marge, au bon endroit.";
                toolbar.Controls.Add(MakeButton("+  Ajouter",AddAccount,true));
                toolbar.Controls.Add(MakeButton("Importer le compte local",async delegate {service.ImportCurrent();status.Text="Compte local enregistré.";await Task.FromResult(0);},false));
                toolbar.Controls.Add(MakeButton("Importer un fichier",async delegate {using(var picker=new OpenFileDialog{Filter="Connexion Codex (auth.json)|auth.json",CheckFileExists=true}) if(picker.ShowDialog(this)==DialogResult.OK){service.Import(SafeFiles.ReadText(picker.FileName),null);status.Text="Compte importé dans le coffre chiffré.";}await Task.FromResult(0);},false));
                toolbar.Controls.Add(MakeButton("Actualiser",RefreshAll,false));
            }
            toolbar.Controls.Add(cancel);actions.RemoveAll(c=>c.IsDisposed);
        }
        private Button SideButton(string text,int y,Func<Task> click)
        {
            var button=MakeButton(text,click,false); button.AutoSize=false; button.SetBounds(22,y,183,37); button.BackColor=Color.FromArgb(23,48,42); button.ForeColor=Color.FromArgb(223,235,226); button.FlatAppearance.BorderSize=0; button.TextAlign=ContentAlignment.MiddleLeft; return button;
        }
        private Button MakeButton(string text,Func<Task> action,bool primary)
        {
            var button=new Button {Text=text,Height=34,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(12,4,12,4),FlatStyle=FlatStyle.Flat,BackColor=primary?Green:Color.White,ForeColor=primary?Color.White:Ink,Cursor=Cursors.Hand,Margin=new Padding(0,0,9,0),Font=new Font("Segoe UI",9,FontStyle.Bold)};
            button.FlatAppearance.BorderColor=primary?Green:Line; button.FlatAppearance.BorderSize=1;
            if(action!=null) {button.Click+=async delegate {await RunOperation(action);}; actions.Add(button);}
            return button;
        }
        internal static Label LabelAt(string text,int x,int y,int w,int h,float size,FontStyle style,Color color)
        { return new Label {Text=text,Left=x,Top=y,Width=w,Height=h,Font=new Font("Segoe UI",size,style),ForeColor=color,BackColor=Color.Transparent,AutoEllipsis=true}; }
        private async Task RunOperation(Func<Task> action,bool quiet=false)
        {
            if(busy || demo) return;
            busy=true; operation=new CancellationTokenSource(); cancel.Visible=true;
            foreach(var control in actions) if(!control.IsDisposed) control.Enabled=false;
            try { await action(); }
            catch(Exception error) { status.Text=Program.SafeError(error); if(!exiting && !quiet) MessageBox.Show(this,Program.SafeError(error),"Creezio",MessageBoxButtons.OK,MessageBoxIcon.Information); }
            finally
            {
                operation.Dispose(); operation=null; busy=false; cancel.Visible=false;
                foreach(var control in actions) if(!control.IsDisposed) control.Enabled=true;
                Redraw(); if(exiting) Close();
            }
        }
        private async Task Poll()
        {
            if(service.Settings.AutoRefresh && DateTime.UtcNow-lastFullRefreshUtc>=TimeSpan.FromMinutes(5)) await RunOperation(RefreshAll,true);
            else if(service.Settings.AutoResetCredits) await RunOperation(async delegate {
                var active=service.Instances.ActiveProfiles();
                if(active.Count==0) return;
                foreach(var profile in active) {await service.Refresh(profile,operation.Token,true);NotifyReset(profile);}
                status.Text=active.Count+" compte(s) utilisé(s) vérifié(s) à "+DateTime.Now.ToString("HH:mm")+" · une seule vérification par compte partagé.";
            },true);
        }
        private void NotifyReset(Profile profile)
        {
            var attempt=profile.ResetAttempt;
            if(service.Settings.Notifications && attempt!=null && attempt.State=="recovered" && resetNotified.Add(attempt.IdempotencyKey))
                tray.ShowBalloonTip(6000,"Reset Codex",profile.Label+" : "+profile.ResetMessage,ToolTipIcon.Info);
        }
        private async Task AddAccount()
        {
            status.Text="Terminez la connexion dans votre navigateur. Choisissez le compte que vous souhaitez ajouter. Délai : 5 minutes.";
            var profile=await service.Login(url=>Process.Start(new ProcessStartInfo(url) {UseShellExecute=true}),operation.Token);
            status.Text="Compte enregistré. Lecture de ses quotas…";
            await service.Refresh(profile,operation.Token);
            status.Text=profile.Error ?? "Compte enregistré et quotas actualisés.";
        }
        private async Task RefreshAll()
        {
            foreach(var profile in service.Data.Profiles.ToArray())
            {
                operation.Token.ThrowIfCancellationRequested(); status.Text="Actualisation de « "+profile.Label+" »…";
                await service.Refresh(profile,operation.Token,true);
                NotifyReset(profile);
                if(service.Settings.Notifications && profile.IsFresh && profile.Score.HasValue && profile.Score<=10 && notified.Add(profile.Key))
                    tray.ShowBalloonTip(6000,"Quota Codex faible",profile.Label+" : moins de 10 % sur une fenêtre de quota.",ToolTipIcon.Info);
                if(profile.Score>10) notified.Remove(profile.Key);
            }
            int failed=service.Data.Profiles.Count(p=>!String.IsNullOrEmpty(p.Error));
            lastFullRefreshUtc=DateTime.UtcNow;
            status.Text=failed>0?failed+" compte(s) non actualisé(s). Les dernières valeurs sont conservées et signalées comme anciennes.":"Quotas actualisés à "+DateTime.Now.ToString("HH:mm")+". Aucune connexion active n'a été modifiée.";
        }
        private void Redraw()
        {
            if(instancesView) {RedrawInstances();return;}
            var profiles=demo?demoProfiles:service.Data.Profiles;
            string active=demo?profiles[0].Key:service.ActiveKey();
            var best=profiles.Where(p=>p.Key!=active && p.Score.HasValue && p.Score>0).OrderByDescending(p=>p.Score).FirstOrDefault();
            subtitle.Text=profiles.Count==0?"Ajoutez votre premier compte pour retrouver vos quotas ici.":profiles.Count+" comptes enregistrés  ·  "+profiles.Count(p=>p.IsFresh)+" quotas à jour"+(demo?"  ·  DÉMONSTRATION":"");
            cards.SuspendLayout();
            foreach(Control control in cards.Controls.Cast<Control>().ToArray()) control.Dispose(); cards.Controls.Clear();
            actions.RemoveAll(c=>c.IsDisposed);
            if(profiles.Count==0)
            {
                var empty=new Panel {Width=Math.Max(520,cards.ClientSize.Width-23),Height=273,BackColor=Color.White,Margin=new Padding(0,0,0,14)};
                empty.Controls.Add(LabelAt("Tous vos comptes, au même endroit.",28,31,640,40,18,FontStyle.Bold,Ink));
                empty.Controls.Add(LabelAt("1   Importez le compte déjà connecté à Codex.\n\n2   Ajoutez vos autres comptes depuis le navigateur.\n\n3   Consultez les quotas, puis choisissez votre compte.",29,89,650,121,11,FontStyle.Regular,Muted));
                empty.Controls.Add(LabelAt("Vos connexions restent sur ce PC, chiffrées pour votre utilisateur Windows.",29,227,650,31,9,FontStyle.Regular,Green)); cards.Controls.Add(empty);
            }
            foreach(var profile in profiles)
            {
                var card=new AccountCard(profile,profile.Key==active,best!=null&&profile.Key==best.Key);
                card.Width=Math.Max(520,cards.ClientSize.Width-23);
                var commandBar=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=45,Padding=new Padding(20,3,0,0),WrapContents=false}; card.Controls.Add(commandBar);
                var selected=profile;
                var associate=MakeButton("Associer aux instances",async delegate {using(var dialog=new AccountScopeForm(selected,service.Data.Instances)) if(dialog.ShowDialog(this)==DialogResult.OK){service.Instances.SetScope(selected,dialog.AllInstances,dialog.InstanceIds);status.Text="Associations enregistrées. Les connexions ouvertes restent inchangées.";}await Task.FromResult(0);},true);
                commandBar.Controls.Add(associate);
                commandBar.Controls.Add(MakeButton("Renommer",async delegate {string name=TextPrompt.Ask(this,"Nom du compte",selected.Label); if(name!=null) {selected.Label=name;service.Save();} await Task.FromResult(0);},false));
                commandBar.Controls.Add(MakeButton("Retirer",async delegate {
                    if(MessageBox.Show(this,"Retirer ce compte du coffre ? Cela ne déconnecte pas Codex.","Retirer un compte",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)
                    {
                        service.Instances.Forget(selected);status.Text="Compte retiré du coffre local. Les profils Codex restent conservés.";
                    }
                    await Task.FromResult(0);
                },false));
                cards.Controls.Add(card);
            }
            cards.ResumeLayout();
        }
        private void ShowSettings()
        {
            using(var dialog=new SettingsForm(service.Settings))
            {
                if(dialog.ShowDialog(this)==DialogResult.OK) {service.Settings=dialog.Result; service.Vault.SaveSettings(service.Settings);resetToggle.Checked=service.Settings.AutoResetCredits; status.Text="Paramètres enregistrés. Reset des limites : une vérification par compte utilisé et par minute.";}
                if(dialog.RestoreRequested) {service.RestorePrevious();status.Text="Connexion précédente restaurée. Vous pouvez rouvrir Codex.";}
            }
        }
        private void RedrawInstances()
        {
            var instances=demo?demoInstances:service.Data.Instances;
            var profiles=demo?demoProfiles:service.Data.Profiles;
            subtitle.Text=instances.Count(i=>!i.IsLocal && !i.Archived)+" instance(s) gérée(s) · Comptes autorisés par espace · Session habituelle préservée"+(demo?" · DÉMO":"");
            cards.SuspendLayout();
            foreach(Control control in cards.Controls.Cast<Control>().ToArray()) control.Dispose();cards.Controls.Clear();actions.RemoveAll(c=>c.IsDisposed);
            foreach(var instance in instances.Where(i=>!i.Archived || showArchived)) {
                var selected=instance;
                var state=demo?new InstanceState{Running=instance.Id!=new string('b',32),Phase=instance.IsLocal?"external":instance.Id==new string('a',32)?"running":"stopped",WindowReady=true}:service.Instances.Runtime.Probe(instance);
                string key=demo?instance.AccountKey:service.Instances.ActiveKey(instance);
                var account=profiles.FirstOrDefault(p=>p.Key==key);
                bool compact=instance.IsLocal && state.Running;
                var panel=new Panel {Width=Math.Max(520,cards.ClientSize.Width-23),Height=compact?138:212,BackColor=Color.White,Margin=new Padding(0,0,0,14),Padding=new Padding(20)};
                panel.Paint+=delegate(object sender,PaintEventArgs e) {using(var pen=new Pen(Line)) e.Graphics.DrawRectangle(pen,0,0,panel.Width-1,panel.Height-1);};
                panel.Controls.Add(LabelAt(instance.Name,21,15,510,31,16,FontStyle.Bold,Ink));
                var badge=LabelAt(instance.Archived?"ARCHIVÉE":instance.IsLocal?"SESSION HABITUELLE":state.Phase=="unknown"?"ÉTAT À VÉRIFIER":state.Phase=="error"?"ÉCHEC DU LANCEMENT":state.Running?(state.Phase=="starting"?"DÉMARRAGE…":"OUVERTE") :"FERMÉE",panel.Width-233,20,210,25,9,FontStyle.Bold,state.Running?Green:Muted);badge.Anchor=AnchorStyles.Right|AnchorStyles.Top;panel.Controls.Add(badge);
                string accountText=account!=null?account.Label+(account.Score.HasValue?" · "+Math.Round(account.Score.Value)+" % de marge":""):key!=null?"Compte connecté hors du coffre":"Aucun compte configuré";
                panel.Controls.Add(LabelAt(accountText,22,51,850,24,10,FontStyle.Bold,Green));
                string hint=instance.IsLocal?"La fermeture de cette session se fait dans Codex.":account!=null && !account.Allows(instance.Id)?"Ce compte n'est plus autorisé ici. Associez-le à cette instance ou choisissez-en un autre.":state.NetworkWarning?"Codex signale un problème réseau dans cette instance. Consultez sa fenêtre avant de travailler.":state.Phase=="error" || state.Phase=="unknown"?state.Message:instance.Archived?"Profil et conversations conservés. Restaurez l'instance pour la rouvrir.":"Profil indépendant · "+profiles.Count(p=>p.Allows(instance.Id))+" compte(s) autorisé(s)";
                if(!compact) panel.Controls.Add(LabelAt(hint,22,80,850,25,9,FontStyle.Regular,Muted));
                if(!compact && !instance.Archived) {
                    var choices=new ComboBox {Left=22,Top=112,Width=315,DropDownStyle=ComboBoxStyle.DropDownList,Enabled=!state.Running};
                    choices.Items.Add(new AccountChoice(null,"Choisir un compte autorisé…"));
                    foreach(var profile in profiles.Where(p=>p.Allows(instance.Id))) choices.Items.Add(new AccountChoice(profile,profile.Label));
                    choices.SelectedIndex=0;
                    for(int n=1;n<choices.Items.Count;n++) if(((AccountChoice)choices.Items[n]).Profile.Key==key) choices.SelectedIndex=n;
                    panel.Controls.Add(choices);
                    var configure=MakeButton("Configurer ce compte",async delegate {
                        var choice=choices.SelectedItem as AccountChoice;
                        if(choice==null || choice.Profile==null) {status.Text="Choisissez un compte dans la liste.";return;}
                        status.Text="Vérification du compte pour « "+selected.Name+" »…";
                        await service.Instances.SelectAccount(selected,choice.Profile,operation.Token);
                        status.Text="Compte configuré uniquement pour « "+selected.Name+" ». Vous pouvez ouvrir cette instance.";
                    },false);configure.SetBounds(350,109,200,34);configure.Enabled=!state.Running;panel.Controls.Add(configure);
                    var best=profiles.Where(p=>p.Allows(instance.Id)&&p.Score>0).OrderByDescending(p=>p.Score).FirstOrDefault();
                    if(best!=null) panel.Controls.Add(LabelAt("Meilleure marge : "+best.Label,580,116,285,27,9,FontStyle.Regular,Muted));
                }
                var bar=new FlowLayoutPanel {Left=22,Top=compact?87:161,Width=880,Height=39,WrapContents=false};panel.Controls.Add(bar);
                if(instance.Archived) {
                    bar.Controls.Add(MakeButton("Restaurer l'instance",async delegate {service.Instances.Archive(selected,false);status.Text="Instance restaurée, avec son profil conservé.";await Task.FromResult(0);},true));
                } else {
                    if(!instance.IsLocal) {
                        var launch=MakeButton(state.Running?"Instance ouverte":"Ouvrir",async delegate {status.Text="Ouverture de « "+selected.Name+" »…";await service.Instances.Start(selected,operation.Token);status.Text="Instance ouverte. Vous pouvez travailler en parallèle dans ses fenêtres.";},true);launch.Enabled=!state.Running;bar.Controls.Add(launch);
                        var stop=MakeButton("Fermer",async delegate {
                            if(MessageBox.Show(this,"Fermer uniquement « "+selected.Name+" » et ses processus ? Les tâches en cours dans cette instance seront interrompues.","Fermer cette instance",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
                            await service.Instances.Runtime.Stop(selected,operation.Token);status.Text="Instance fermée. Les autres sessions sont restées ouvertes.";
                        },false);stop.Enabled=state.Running;bar.Controls.Add(stop);
                    }
                    bar.Controls.Add(MakeButton("Importer sa connexion",async delegate {var imported=service.Instances.Capture(selected);status.Text="Compte importé et associé à « "+selected.Name+" ».";await service.Refresh(imported,operation.Token);},false));
                    bar.Controls.Add(MakeButton("Renommer",async delegate {string name=TextPrompt.Ask(this,"Nom de l'instance",selected.Name);if(name!=null) service.Instances.Rename(selected,name);await Task.FromResult(0);},false));
                    if(!instance.IsLocal) {var archive=MakeButton("Archiver",async delegate {service.Instances.Archive(selected,true);status.Text="Instance archivée. Son profil et ses conversations sont conservés.";await Task.FromResult(0);},false);archive.Enabled=!state.Running;bar.Controls.Add(archive);}
                }
                cards.Controls.Add(panel);
            }
            if(instances.Count(i=>!i.IsLocal&&!i.Archived)==0) {
                var empty=new Panel{Width=Math.Max(520,cards.ClientSize.Width-23),Height=190,BackColor=Pale,Margin=new Padding(0,5,0,0)};
                empty.Controls.Add(LabelAt("Créez un espace pour chaque usage.",24,25,800,40,19,FontStyle.Bold,Ink));
                empty.Controls.Add(LabelAt("Développement, travail, projets personnels…\n\nAssociez un compte à toutes les instances, ou réservez-le à certains espaces.",26,83,810,85,11,FontStyle.Regular,Muted));cards.Controls.Add(empty);
            }
            cards.ResumeLayout();
        }
        internal void ShowAccountsDemo() {instancesView=false;BuildToolbar();Redraw();}
        private void OnClosing(object sender,FormClosingEventArgs e)
        {
            if(demo) return;
            if(!exiting && e.CloseReason==CloseReason.UserClosing) {e.Cancel=true;Hide();tray.ShowBalloonTip(3000,"Creezio reste accessible","Cliquez sur l'icône près de l'horloge pour rouvrir l'application. Menu Quitter pour l'arrêter.",ToolTipIcon.Info);return;}
            if(relayBusy){exiting=true;e.Cancel=true;relayLifetime.Cancel();return;}
            if(busy) {exiting=true;e.Cancel=true;operation.Cancel();return;}
            if(timer!=null) timer.Dispose();
            if(instanceTimer!=null) instanceTimer.Dispose();
            if(relayTimer!=null)relayTimer.Dispose();relayLifetime.Dispose();
            if(tray!=null) {tray.Visible=false;tray.Dispose();}
        }
        public void RenderDemo(string path)
        {
            // Render only this application's sample controls, never capture the user's screen.
            ShowInTaskbar=false; Opacity=0; Show(); Application.DoEvents();
            using(var bitmap=new Bitmap(Width,Height)) {DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height)); bitmap.Save(path,ImageFormat.Png);}
            Close();
        }
        private static List<Profile> DemoData()
        {
            long reset=(long)(DateTimeOffset.UtcNow-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
            return new List<Profile> {
                new Profile {Key="demo-one",Label="Compte principal",Email="principal@example.com",Plan="plus",QuotaTimeUtc=DateTime.UtcNow.ToString("o"),ResetCredits=new ResetCredits{AvailableCount=2,Credits=new List<ResetCredit>{new ResetCredit{Id="demo-credit",ResetType="codexRateLimits",Status="available",ExpiresAt=reset+86400}}},ResetMessage="Reset automatique activé · déclenchement à 1 % restant.",Quotas=new List<QuotaBucket>{new QuotaBucket {Name="codex",Primary=new QuotaWindow{Remaining=24,Minutes=300,ResetsAt=reset+8200},Secondary=new QuotaWindow{Remaining=61,Minutes=10080,ResetsAt=reset+150000}}}},
                new Profile {Key="demo-two",Label="Compte de travail",Email="travail@example.com",Plan="pro",QuotaTimeUtc=DateTime.UtcNow.ToString("o"),ResetCredits=new ResetCredits{AvailableCount=0,Credits=new List<ResetCredit>()},Quotas=new List<QuotaBucket>{new QuotaBucket {Name="codex",Primary=new QuotaWindow{Remaining=92,Minutes=300,ResetsAt=reset+17000},Secondary=new QuotaWindow{Remaining=84,Minutes=10080,ResetsAt=reset+450000}}}}
            };
        }
    }
    internal sealed class AccountChoice
    {
        public readonly Profile Profile;private readonly string label;
        public AccountChoice(Profile profile,string text){Profile=profile;label=text;}
        public override string ToString(){return label;}
    }
    internal sealed class AccountScopeForm : Form
    {
        public bool AllInstances;public List<string> InstanceIds;
        public AccountScopeForm(Profile profile,List<DesktopInstance> instances)
        {
            Text="Associations · "+profile.Label;ClientSize=new Size(560,430);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            Controls.Add(MainForm.LabelAt("Où ce compte peut-il être utilisé ?",22,20,515,32,16,FontStyle.Bold,MainForm.Ink));
            var all=new CheckBox{Text="Toutes les instances, y compris les prochaines",Left=25,Top=72,Width=505,Checked=profile.AllInstances};Controls.Add(all);
            var choices=new CheckedListBox{Left=25,Top=116,Width=510,Height=192,CheckOnClick=true,Enabled=!all.Checked};Controls.Add(choices);
            foreach(var instance in instances) choices.Items.Add(instance.Name+(instance.Archived?" (archivée)":""),profile.InstanceIds.Contains(instance.Id));
            all.CheckedChanged+=delegate{choices.Enabled=!all.Checked;};
            Controls.Add(MainForm.LabelAt("L'association rend le compte disponible dans les espaces choisis.\nSon quota reste partagé : ouvrir plusieurs instances ne le multiplie pas.",25,323,510,49,9,FontStyle.Regular,MainForm.Muted));
            var save=new Button{Text="Enregistrer",Left=385,Top=383,Width=150,Height=32};Controls.Add(save);AcceptButton=save;
            save.Click+=delegate{AllInstances=all.Checked;InstanceIds=choices.CheckedIndices.Cast<int>().Select(i=>instances[i].Id).ToList();DialogResult=DialogResult.OK;Close();};
        }
    }
    internal sealed class AccountCard : Panel
    {
        private readonly Profile profile; private readonly bool active,best;
        public AccountCard(Profile value,bool isActive,bool isBest)
        {
            profile=value; active=isActive; best=isBest;
            Height=332+Math.Max(0,profile.Quotas.Count-1)*86; BackColor=Color.White; Margin=new Padding(0,0,0,16);
            DoubleBuffered=true; ResizeRedraw=true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var pen=new Pen(active?Color.FromArgb(166,198,177):MainForm.Line)) g.DrawRectangle(pen,0,0,Width-1,Height-1);
            using(var brush=new SolidBrush(MainForm.Pale)) g.FillEllipse(brush,22,21,43,43);
            using(var font=new Font("Segoe UI",16,FontStyle.Bold)) TextRenderer.DrawText(g,profile.Label.Substring(0,1).ToUpperInvariant(),font,new Rectangle(22,22,43,40),MainForm.Green,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            Draw(g,profile.Label,76,21,Width-280,27,13,FontStyle.Bold,MainForm.Ink);
            Draw(g,profile.Email+"  ·  "+(profile.Plan??"ChatGPT").ToUpperInvariant(),77,49,Width-290,22,9,FontStyle.Regular,MainForm.Muted);
            if(active||best) Draw(g,active?"COMPTE LOCAL":"MEILLEURE MARGE",Width-179,25,160,30,8,FontStyle.Bold,MainForm.Green);
            bool fresh=profile.IsFresh;
            Draw(g,profile.Error!=null?"ANCIENNES VALEURS · Reconnexion ou actualisation nécessaire":fresh?"QUOTAS DISPONIBLES":"QUOTAS NON ACTUALISÉS",22,83,Width-43,23,8,FontStyle.Bold,profile.Error!=null?Color.FromArgb(161,96,39):MainForm.Muted);
            if(profile.Quotas.Count==0) Draw(g,"Actualisez pour afficher les limites de ce compte.",22,124,Width-44,45,11,FontStyle.Regular,MainForm.Muted);
            int y=113;
            foreach(var bucket in profile.Quotas)
            {
                if(profile.Quotas.Count>1) Draw(g,bucket.Name,22,y-3,Width-44,18,8,FontStyle.Bold,MainForm.Green);
                int offset=profile.Quotas.Count>1?15:0;
                DrawQuota(g,bucket.Primary,22,y+offset,(Width-64)/2,fresh);
                DrawQuota(g,bucket.Secondary,Width/2+5,y+offset,(Width-64)/2,fresh);
                y+=86;
            }
            int resetY=193+Math.Max(0,profile.Quotas.Count-1)*86;
            string resetText="RÉINITIALISATIONS DES LIMITES · "+(profile.ResetCredits!=null&&profile.ResetCredits.AvailableCount.HasValue?profile.ResetCredits.AvailableCount.Value+" disponible(s)":"non renseignées");
            var next=profile.ResetCredits==null?null:profile.ResetCredits.Next(DateTime.UtcNow);
            if(next!=null && next.ExpiresAt.HasValue) resetText+=" · prochain expirant le "+new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(next.ExpiresAt.Value).ToLocalTime().ToString("dd/MM à HH:mm");
            Draw(g,resetText,22,resetY,Width-44,22,9,FontStyle.Bold,MainForm.Green);
            if(!String.IsNullOrEmpty(profile.ResetMessage)) Draw(g,profile.ResetMessage,22,resetY+26,Width-44,27,9,FontStyle.Regular,MainForm.Muted);
            Draw(g,profile.AllInstances?"DISPONIBLE POUR TOUTES LES INSTANCES · présentes et futures":"ASSOCIÉ À "+profile.InstanceIds.Count+" INSTANCE(S)",22,resetY+59,Width-44,24,8,FontStyle.Bold,MainForm.Green);
        }
        private static void Draw(Graphics g,string text,int x,int y,int w,int h,float size,FontStyle weight,Color color)
        {using(var font=new Font("Segoe UI",size,weight)) TextRenderer.DrawText(g,text,font,new Rectangle(x,y,w,h),color,TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);}
        private static void DrawQuota(Graphics g,QuotaWindow window,int x,int y,int width,bool fresh)
        {
            Draw(g,window==null?"Fenêtre non fournie":window.Caption,x,y,width-75,23,10,FontStyle.Bold,MainForm.Ink);
            string remaining=window!=null&&window.Remaining.HasValue?Math.Round(window.Remaining.Value)+" %":"—";
            Draw(g,remaining,x+width-75,y,75,24,12,FontStyle.Bold,fresh?MainForm.Green:MainForm.Muted);
            using(var brush=new SolidBrush(MainForm.Line)) g.FillRectangle(brush,x,y+32,width,7);
            if(window!=null&&window.Remaining.HasValue) using(var brush=new SolidBrush(fresh?MainForm.Green:Color.FromArgb(148,158,151))) g.FillRectangle(brush,x,y+32,(float)(width*window.Remaining.Value/100),7);
            Draw(g,window==null?"Donnée non disponible":window.ResetCaption,x,y+49,width,24,8,FontStyle.Regular,MainForm.Muted);
        }
    }
    internal sealed class SettingsForm : Form
    {
        public Settings Result; public bool RestoreRequested;
        public SettingsForm(Settings current)
        {
            Text="Paramètres · Creezio";ClientSize=new Size(640,480);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            Controls.Add(MainForm.LabelAt("Exécutable Codex CLI (codex.exe)",22,22,580,25,10,FontStyle.Bold,MainForm.Ink));
            var exe=new TextBox {Left=25,Top=54,Width=490,Text=current.CodexExecutable};Controls.Add(exe);
            var browse=new Button {Text="Choisir…",Left=525,Top=52,Width=90};Controls.Add(browse);
            browse.Click+=delegate {using(var picker=new OpenFileDialog {Filter="Codex CLI (*.exe)|*.exe",CheckFileExists=true}) if(picker.ShowDialog(this)==DialogResult.OK) exe.Text=picker.FileName;};
            Controls.Add(MainForm.LabelAt("Dossier Codex contenant auth.json et config.toml",22,101,590,25,10,FontStyle.Bold,MainForm.Ink));
            var home=new TextBox {Left=25,Top=135,Width=490,Text=current.CodexHome};Controls.Add(home);
            var folder=new Button {Text="Choisir…",Left=525,Top=133,Width=90};Controls.Add(folder);
            folder.Click+=delegate {using(var picker=new FolderBrowserDialog {SelectedPath=home.Text}) if(picker.ShowDialog(this)==DialogResult.OK) home.Text=picker.SelectedPath;};
            var automatic=new CheckBox {Text="Actualiser les quotas toutes les 5 minutes",Left=25,Top=188,Width=580,Checked=current.AutoRefresh};Controls.Add(automatic);
            var notify=new CheckBox {Text="Notifier quand un quota passe sous 10 %",Left=25,Top=225,Width=580,Checked=current.Notifications};Controls.Add(notify);
            var autoReset=new CheckBox {Text="Réinitialiser les limites à 1 % si une réinitialisation est disponible",Left=25,Top=263,Width=590,Checked=current.AutoResetCredits};Controls.Add(autoReset);
            Controls.Add(MainForm.LabelAt("Comptes utilisés : session habituelle et instances ouvertes.\nUne seule demande par compte partagé. Le switcher doit rester ouvert.",25,301,590,50,9,FontStyle.Regular,MainForm.Muted));
            var restore=new Button {Text="Restaurer la connexion précédente",Left=25,Top=361,Width=285,Height=32};Controls.Add(restore);
            restore.Click+=delegate {if(MessageBox.Show(this,"Restaurer la sauvegarde précédente ? Codex doit être fermé.","Restauration",MessageBoxButtons.YesNo)==DialogResult.Yes) {RestoreRequested=true;DialogResult=DialogResult.Cancel;Close();}};
            var save=new Button {Text="Enregistrer",Left=475,Top=425,Width=140,Height=34};Controls.Add(save);AcceptButton=save;
            save.Click+=delegate {
                if(!Path.IsPathRooted(home.Text) || !Directory.Exists(home.Text) || !File.Exists(exe.Text) || !String.Equals(Path.GetExtension(exe.Text),".exe",StringComparison.OrdinalIgnoreCase)) {MessageBox.Show(this,"Choisissez un dossier Codex existant et un exécutable .exe valide.");return;}
                Result=new Settings {CodexExecutable=Path.GetFullPath(exe.Text),CodexHome=Path.GetFullPath(home.Text),AutoRefresh=automatic.Checked,Notifications=notify.Checked,AutoResetCredits=autoReset.Checked};DialogResult=DialogResult.OK;Close();
            };
        }
    }
    internal static class TextPrompt
    {
        public static string Ask(IWin32Window parent,string title,string value)
        {
            using(var form=new Form {Text=title,ClientSize=new Size(430,127),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false})
            {
                var field=new TextBox {Left=20,Top=22,Width=390,Text=value,MaxLength=70};form.Controls.Add(field);
                var ok=new Button {Left=300,Top=75,Width=110,Text="Enregistrer"};form.Controls.Add(ok);form.AcceptButton=ok;
                ok.Click+=delegate {if(!String.IsNullOrWhiteSpace(field.Text)) {form.DialogResult=DialogResult.OK;form.Close();}};
                return form.ShowDialog(parent)==DialogResult.OK?field.Text.Trim():null;
            }
        }
    }
}
