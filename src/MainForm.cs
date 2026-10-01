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
        private bool busy, exiting;
        private CancellationTokenSource operation;
        private List<Profile> demoProfiles;
        public MainForm(AccountService accounts,bool isDemo)
        {
            service=accounts; demo=isDemo;
            Text="Codex Account Switcher · Creezio"; ClientSize=new Size(1100,790); MinimumSize=new Size(920,670);
            StartPosition=FormStartPosition.CenterScreen; BackColor=Color.FromArgb(248,250,247); ForeColor=Ink;
            Font=new Font("Segoe UI",10); AutoScaleMode=AutoScaleMode.Dpi; Icon=SystemIcons.Application;
            var sidebar=new Panel {Dock=DockStyle.Left,Width=230,BackColor=Color.FromArgb(23,48,42),Padding=new Padding(22)};
            Controls.Add(sidebar);
            var brand=LabelAt("creezio",26,32,180,45,27,FontStyle.Bold,Color.White); sidebar.Controls.Add(brand);
            sidebar.Controls.Add(LabelAt("CODEX\nPOUR WINDOWS",27,88,182,42,8,FontStyle.Bold,Color.FromArgb(171,201,183)));
            var intro=LabelAt("Vos comptes.\nVotre espace.",27,148,179,63,10,FontStyle.Regular,Color.FromArgb(216,231,220)); intro.AutoEllipsis=false; sidebar.Controls.Add(intro);
            var nav=MakeButton("Comptes",null,true); nav.AutoSize=false; nav.SetBounds(20,229,190,44); nav.BackColor=Color.FromArgb(50,83,68); nav.ForeColor=Color.White; sidebar.Controls.Add(nav);
            var prefs=SideButton("Paramètres",296,async delegate { ShowSettings(); await Task.FromResult(0); }); sidebar.Controls.Add(prefs);
            var help=SideButton("Guide d'utilisation",345,async delegate { Process.Start(new ProcessStartInfo("https://github.com/creezio/codex-account-switcher-windows#readme") {UseShellExecute=true}); await Task.FromResult(0); }); sidebar.Controls.Add(help);
            var footer=new Label {Text="PROTECTION LOCALE\n\nConnexions chiffrées pour\nvotre utilisateur Windows.\n\nCreezio · v0.1.0",Dock=DockStyle.Bottom,Height=144,ForeColor=Color.FromArgb(171,201,183),Font=new Font("Segoe UI",9)}; sidebar.Controls.Add(footer);
            var content=new Panel {Dock=DockStyle.Fill,Padding=new Padding(30,0,30,0)}; Controls.Add(content); content.BringToFront();
            var header=new Panel {Dock=DockStyle.Top,Height=181}; content.Controls.Add(header);
            header.Controls.Add(LabelAt("VOS COMPTES CODEX",0,25,410,22,9,FontStyle.Bold,Green));
            header.Controls.Add(LabelAt("Gardez de la marge.",0,52,700,47,27,FontStyle.Bold,Ink));
            subtitle.SetBounds(1,105,700,30); subtitle.ForeColor=Muted; subtitle.Font=new Font("Segoe UI",10); header.Controls.Add(subtitle);
            var buttons=new FlowLayoutPanel {Left=0,Top=139,Width=740,Height=39,WrapContents=false}; header.Controls.Add(buttons);
            buttons.Controls.Add(MakeButton("+  Ajouter un compte", async delegate { await AddAccount(); },true));
            buttons.Controls.Add(MakeButton("Importer le compte local",async delegate { service.ImportCurrent(); status.Text="Connexion locale enregistrée dans le coffre."; await Task.FromResult(0); },false));
            buttons.Controls.Add(MakeButton("Actualiser",async delegate { await RefreshAll(); },false));
            cancel.Text="Annuler"; cancel.AutoSize=true; cancel.Height=34; cancel.FlatStyle=FlatStyle.Flat; cancel.Visible=false; cancel.Click+=delegate {if(operation!=null) operation.Cancel();}; buttons.Controls.Add(cancel);
            var bottom=new Panel {Dock=DockStyle.Bottom,Height=89,Padding=new Padding(0,14,0,0)}; content.Controls.Add(bottom);
            status.Dock=DockStyle.Fill; status.ForeColor=Muted; status.Font=new Font("Segoe UI",9); status.Text="Prêt. Les quotas sont lus uniquement sur demande, sauf actualisation automatique activée."; bottom.Controls.Add(status);
            cards.Dock=DockStyle.Fill; cards.FlowDirection=FlowDirection.TopDown; cards.WrapContents=false; cards.AutoScroll=true; cards.Padding=new Padding(0,7,0,10); content.Controls.Add(cards); cards.BringToFront();
            cards.Resize+=delegate { foreach(Control card in cards.Controls) card.Width=Math.Max(520,cards.ClientSize.Width-23); };
            if(demo) demoProfiles=DemoData();
            else
            {
                var menu=new ContextMenuStrip();
                menu.Items.Add("Ouvrir",null,delegate { Show(); WindowState=FormWindowState.Normal; Activate(); });
                menu.Items.Add("Actualiser les quotas",null,async delegate { await RunOperation(RefreshAll); });
                menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Quitter",null,delegate { exiting=true; Close(); });
                tray=new NotifyIcon {Icon=SystemIcons.Application,Text="Creezio · Codex Account Switcher",Visible=true,ContextMenuStrip=menu};
                tray.DoubleClick+=delegate { Show(); WindowState=FormWindowState.Normal; Activate(); };
                timer=new System.Windows.Forms.Timer {Interval=300000};
                timer.Tick+=async delegate {if(service.Settings.AutoRefresh && !busy) await RunOperation(RefreshAll);}; timer.Start();
            }
            FormClosing+=OnClosing;
            Redraw();
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
        private async Task RunOperation(Func<Task> action)
        {
            if(busy || demo) return;
            busy=true; operation=new CancellationTokenSource(); cancel.Visible=true;
            foreach(var control in actions) if(!control.IsDisposed) control.Enabled=false;
            try { await action(); }
            catch(Exception error) { status.Text=Program.SafeError(error); if(!exiting) MessageBox.Show(this,Program.SafeError(error),"Creezio",MessageBoxButtons.OK,MessageBoxIcon.Information); }
            finally
            {
                operation.Dispose(); operation=null; busy=false; cancel.Visible=false;
                foreach(var control in actions) if(!control.IsDisposed) control.Enabled=true;
                Redraw(); if(exiting) Close();
            }
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
                await service.Refresh(profile,operation.Token);
                if(service.Settings.Notifications && profile.IsFresh && profile.Score.HasValue && profile.Score<=10 && notified.Add(profile.Key))
                    tray.ShowBalloonTip(6000,"Quota Codex faible",profile.Label+" : moins de 10 % sur une fenêtre de quota.",ToolTipIcon.Info);
                if(profile.Score>10) notified.Remove(profile.Key);
            }
            int failed=service.Data.Profiles.Count(p=>!String.IsNullOrEmpty(p.Error));
            status.Text=failed>0?failed+" compte(s) non actualisé(s). Les dernières valeurs sont conservées et signalées comme anciennes.":"Quotas actualisés à "+DateTime.Now.ToString("HH:mm")+". Aucune connexion active n'a été modifiée.";
        }
        private void Redraw()
        {
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
                var activate=MakeButton(profile.Key==active?"Compte configuré":"Utiliser ce compte",async delegate {
                    status.Text="Vérification du compte et préparation de la bascule…";
                    await service.Switch(selected,operation.Token);
                    status.Text="Compte configuré et fichier vérifié. Vous pouvez rouvrir Codex ; vérifiez le compte affiché dans son profil.";
                },profile.Key!=active);
                activate.Enabled=profile.Key!=active && !demo; commandBar.Controls.Add(activate);
                commandBar.Controls.Add(MakeButton("Renommer",async delegate {string name=TextPrompt.Ask(this,"Nom du compte",selected.Label); if(name!=null) {selected.Label=name;service.Save();} await Task.FromResult(0);},false));
                commandBar.Controls.Add(MakeButton("Retirer",async delegate {
                    if(MessageBox.Show(this,"Retirer ce compte du coffre ? Cela ne déconnecte pas Codex.","Retirer un compte",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)
                    {
                        service.Data.Profiles.Remove(selected);
                        if(!String.IsNullOrEmpty(service.Data.PreviousAuthJson)) { try {if(AuthIdentity.Parse(service.Data.PreviousAuthJson).Key==selected.Key) service.Data.PreviousAuthJson=null;} catch {} }
                        service.Save(); status.Text="Compte retiré du coffre local.";
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
                if(dialog.ShowDialog(this)==DialogResult.OK) {service.Settings=dialog.Result; service.Vault.SaveSettings(service.Settings); status.Text="Paramètres enregistrés. L'actualisation automatique, si activée, se fait toutes les 5 minutes.";}
                if(dialog.RestoreRequested) {service.RestorePrevious();status.Text="Connexion précédente restaurée. Vous pouvez rouvrir Codex.";}
            }
        }
        private void OnClosing(object sender,FormClosingEventArgs e)
        {
            if(demo) return;
            if(!exiting && e.CloseReason==CloseReason.UserClosing) {e.Cancel=true;Hide();tray.ShowBalloonTip(3000,"Creezio reste accessible","Cliquez sur l'icône près de l'horloge pour rouvrir l'application. Menu Quitter pour l'arrêter.",ToolTipIcon.Info);return;}
            if(busy) {exiting=true;e.Cancel=true;operation.Cancel();return;}
            if(timer!=null) timer.Dispose();
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
                new Profile {Key="demo-one",Label="Compte principal",Email="principal@example.com",Plan="plus",QuotaTimeUtc=DateTime.UtcNow.ToString("o"),Quotas=new List<QuotaBucket>{new QuotaBucket {Name="codex",Primary=new QuotaWindow{Remaining=24,Minutes=300,ResetsAt=reset+8200},Secondary=new QuotaWindow{Remaining=61,Minutes=10080,ResetsAt=reset+150000}}}},
                new Profile {Key="demo-two",Label="Compte de travail",Email="travail@example.com",Plan="pro",QuotaTimeUtc=DateTime.UtcNow.ToString("o"),Quotas=new List<QuotaBucket>{new QuotaBucket {Name="codex",Primary=new QuotaWindow{Remaining=92,Minutes=300,ResetsAt=reset+17000},Secondary=new QuotaWindow{Remaining=84,Minutes=10080,ResetsAt=reset+450000}}}}
            };
        }
    }
    internal sealed class AccountCard : Panel
    {
        private readonly Profile profile; private readonly bool active,best;
        public AccountCard(Profile value,bool isActive,bool isBest)
        {
            profile=value; active=isActive; best=isBest;
            Height=247+Math.Max(0,profile.Quotas.Count-1)*86; BackColor=Color.White; Margin=new Padding(0,0,0,16);
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
            Text="Paramètres · Creezio";ClientSize=new Size(640,397);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
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
            var restore=new Button {Text="Restaurer la connexion précédente",Left=25,Top=275,Width=285,Height=32};Controls.Add(restore);
            restore.Click+=delegate {if(MessageBox.Show(this,"Restaurer la sauvegarde précédente ? Codex doit être fermé.","Restauration",MessageBoxButtons.YesNo)==DialogResult.Yes) {RestoreRequested=true;DialogResult=DialogResult.Cancel;Close();}};
            var save=new Button {Text="Enregistrer",Left=475,Top=341,Width=140,Height=34};Controls.Add(save);AcceptButton=save;
            save.Click+=delegate {
                if(!Path.IsPathRooted(home.Text) || !Directory.Exists(home.Text) || !File.Exists(exe.Text) || !String.Equals(Path.GetExtension(exe.Text),".exe",StringComparison.OrdinalIgnoreCase)) {MessageBox.Show(this,"Choisissez un dossier Codex existant et un exécutable .exe valide.");return;}
                Result=new Settings {CodexExecutable=Path.GetFullPath(exe.Text),CodexHome=Path.GetFullPath(home.Text),AutoRefresh=automatic.Checked,Notifications=notify.Checked};DialogResult=DialogResult.OK;Close();
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
