using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class RelayForm : Form
    {
        private readonly RelayStore store;
        private readonly RelayEngine engine;
        private readonly ListView messages=new ListView(),channels=new ListView();
        private readonly TextBox detail=new TextBox();
        private readonly Label status=new Label();
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=3000};
        public RelayForm(RelayStore data)
        {
            store=data;engine=new RelayEngine(store);Text="Relais entre comptes · Creezio";ClientSize=new Size(1080,760);MinimumSize=new Size(900,650);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(248,250,247);
            var header=new Panel{Dock=DockStyle.Top,Height=100,Padding=new Padding(22)};Controls.Add(header);
            header.Controls.Add(MainForm.LabelAt("Un compte prépare. Un autre exécute.",22,15,960,38,23,FontStyle.Bold,MainForm.Ink));
            header.Controls.Add(MainForm.LabelAt("Messages et réponses entre vos instances · Compte et dossier vérifiés avant chaque envoi",24,60,970,26,10,FontStyle.Regular,MainForm.Muted));
            var tabs=new TabControl{Dock=DockStyle.Fill,Padding=new Point(18,8)};Controls.Add(tabs);tabs.BringToFront();
            var inbox=new TabPage("Demandes et réponses");var destinations=new TabPage("Canaux connectés");tabs.TabPages.Add(inbox);tabs.TabPages.Add(destinations);
            var commands=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,MinimumSize=new Size(0,48),Padding=new Padding(10,7,0,7)};inbox.Controls.Add(commands);
            Button(commands,"Nouvelle demande",async delegate {using(var dialog=new RelayComposeForm(store,null))if(dialog.ShowDialog(this)==DialogResult.OK){await engine.Process(dialog.MessageId,CancellationToken.None);Reload();}},true);
            Button(commands,"Continuer l'échange",async delegate {var m=Selected();if(m==null)return;using(var dialog=new RelayComposeForm(store,m))if(dialog.ShowDialog(this)==DialogResult.OK){await engine.Process(dialog.MessageId,CancellationToken.None);Reload();}},false);
            Button(commands,"Relire le résultat",async delegate {var m=Selected();if(m==null)return;store.Recheck(m.Id);await engine.Process(m.Id,CancellationToken.None);Reload();},false);
            Button(commands,"Annuler l'attente",delegate {var m=Selected();if(m!=null)store.Cancel(m.Id);Reload();return System.Threading.Tasks.Task.FromResult(0);},false);
            Button(commands,"Classer après vérification",delegate {var m=Selected();if(m!=null && MessageBox.Show(this,"Vous avez vérifié dans Codex ce qui a réellement été exécuté ? Classer libère le canal sans renvoyer cette demande.","Résultat vérifié",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)store.CloseReviewed(m.Id);Reload();return System.Threading.Tasks.Task.FromResult(0);},false);
            var split=new SplitContainer{Size=new Size(1000,500),Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=250};inbox.Controls.Add(split);split.BringToFront();
            messages.Dock=DockStyle.Fill;messages.View=View.Details;messages.FullRowSelect=true;messages.MultiSelect=false;messages.HideSelection=false;
            messages.Columns.Add("Demande",340);messages.Columns.Add("Destinataire",170);messages.Columns.Add("État",150);messages.Columns.Add("Retour",130);messages.Columns.Add("Créée",150);split.Panel1.Controls.Add(messages);
            detail.Dock=DockStyle.Fill;detail.Multiline=true;detail.ReadOnly=true;detail.ScrollBars=ScrollBars.Vertical;detail.BackColor=Color.White;detail.BorderStyle=BorderStyle.FixedSingle;split.Panel2.Controls.Add(detail);
            messages.SelectedIndexChanged+=delegate{ShowMessage();};
            var channelCommands=new FlowLayoutPanel{Dock=DockStyle.Top,Height=48,Padding=new Padding(10,7,0,0)};destinations.Controls.Add(channelCommands);
            Button(channelCommands,"Connecter / reconnecter un canal",delegate{using(var f=new RelayConnectForm())f.ShowDialog(this);Reload();return System.Threading.Tasks.Task.FromResult(0);},true);
            Button(channelCommands,"Activer / désactiver",delegate{if(channels.SelectedItems.Count>0){var c=store.Channel((string)channels.SelectedItems[0].Tag);store.SetEnabled(c.Id,!c.Enabled);Reload();}return System.Threading.Tasks.Task.FromResult(0);},false);
            channels.Dock=DockStyle.Fill;channels.View=View.Details;channels.FullRowSelect=true;channels.MultiSelect=false;channels.Columns.Add("Canal",180);channels.Columns.Add("Compte",210);channels.Columns.Add("État",130);channels.Columns.Add("Permissions observées",205);channels.Columns.Add("Dossier partagé",400);destinations.Controls.Add(channels);channels.BringToFront();
            status.Text="Une seule demande active par canal. Les résultats incertains ne sont jamais renvoyés automatiquement.";status.Dock=DockStyle.Bottom;status.Height=43;status.Padding=new Padding(12,10,0,0);status.ForeColor=MainForm.Muted;Controls.Add(status);
            timer.Tick+=delegate{Reload();};timer.Start();FormClosed+=delegate{timer.Dispose();};Reload();
        }
        private void Button(Control parent,string text,Func<System.Threading.Tasks.Task> action,bool primary)
        {
            var b=new Button{Text=text,AutoSize=true,Height=33,FlatStyle=FlatStyle.Flat,BackColor=primary?MainForm.Green:Color.White,ForeColor=primary?Color.White:MainForm.Ink,Padding=new Padding(7,3,7,3)};
            b.Click+=async delegate {b.Enabled=false;try{await action();}catch(Exception e){MessageBox.Show(this,Program.SafeError(e),"Relais");}finally{b.Enabled=true;}};parent.Controls.Add(b);
        }
        private RelayMessage Selected(){return messages.SelectedItems.Count==0?null:store.Message((string)messages.SelectedItems[0].Tag);}
        internal static string State(string value)
        {
            switch(value){case "queued":return "En attente";case "sending":return "Envoi…";case "waiting":return "En cours";case "completed":return "Réponse reçue";case "failed":return "Échec de la tâche";case "cancelled":return "Annulée";case "closed":return "Classée";case "uncertain":return "À vérifier";case "attention":return "À vérifier";case "pending":return "À remettre";case "delivered":return "Remis à Codex";case "none":return "Consultation ici";default:return value;}
        }
        private void Reload()
        {
            try {
                string selected=messages.SelectedItems.Count==0?null:(string)messages.SelectedItems[0].Tag;
                messages.BeginUpdate();messages.Items.Clear();
                foreach(var m in store.Messages().Take(200)){var row=new ListViewItem(new[]{m.Title,m.TargetChannelId,State(m.State),State(m.ReturnState),DateTime.Parse(m.CreatedUtc).ToLocalTime().ToString("dd/MM HH:mm")}){Tag=m.Id};messages.Items.Add(row);row.Selected=m.Id==selected;}
                if(selected==null && messages.Items.Count>0)messages.Items[0].Selected=true;
                messages.EndUpdate();ShowMessage();
                selected=channels.SelectedItems.Count==0?null:(string)channels.SelectedItems[0].Tag;channels.BeginUpdate();channels.Items.Clear();
                foreach(var c in store.Channels()){string health="Connecté";try{new DesktopRelayTransport().Verify(c);}catch{health=c.Enabled?"À reconnecter":"Désactivé";}string permissions=RelayPermissions.Read(c.Home,c.AnchorThreadId);if(c.RequireFullAccess && permissions!="full-access")health="Permissions à vérifier";var row=new ListViewItem(new[]{c.Id,c.Email,health,permissions=="full-access"?"Accès complet":permissions,c.Workspace}){Tag=c.Id};channels.Items.Add(row);row.Selected=c.Id==selected;}
                channels.EndUpdate();
            }catch(Exception e){status.Text=Program.SafeError(e);}
        }
        private void ShowMessage()
        {
            var m=Selected();if(m==null)return;
            string text="DEMANDE : "+m.Title+"\r\n"+m.SourceChannelId+" → "+m.TargetChannelId+"\r\nDossier : "+m.Workspace+"\r\nVersion : "+m.Revision+"\r\nIdentifiant : "+m.Id+"\r\nConversation : "+(m.TargetThreadId??"en attente")+"\r\n"+(m.Error==null?"":"État : "+m.Error+"\r\n")+"\r\n"+m.Prompt+"\r\n\r\nRÉPONSE\r\n"+(m.Result??"En attente de la réponse finale…");
            if(detail.Text!=text)detail.Text=text;
        }
        internal void RenderDemo(string path)
        {
            ShowInTaskbar=false;Opacity=0;Show();Application.DoEvents();if(messages.Items.Count>0){messages.Items[0].Selected=true;ShowMessage();}
            using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);}Close();
        }
    }
    internal sealed class RelayComposeForm : Form
    {
        public string MessageId;
        public RelayComposeForm(RelayStore store,RelayMessage previous)
        {
            Text=previous==null?"Nouvelle demande":"Continuer l'échange";ClientSize=new Size(720,565);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            var from=new ComboBox{Left=22,Top=43,Width=325,DropDownStyle=ComboBoxStyle.DropDownList};var to=new ComboBox{Left=370,Top=43,Width=325,DropDownStyle=ComboBoxStyle.DropDownList};
            Controls.Add(MainForm.LabelAt("Conversation source",22,15,320,25,10,FontStyle.Bold,MainForm.Ink));Controls.Add(MainForm.LabelAt("Canal destinataire",370,15,320,25,10,FontStyle.Bold,MainForm.Ink));Controls.Add(from);Controls.Add(to);
            foreach(var c in store.Channels().Where(c=>c.Enabled)){from.Items.Add(c.Id);to.Items.Add(c.Id);}
            if(previous!=null){from.SelectedItem=previous.SourceChannelId;to.SelectedItem=previous.TargetChannelId;from.Enabled=false;to.Enabled=false;}else{if(from.Items.Count>0)from.SelectedIndex=0;if(to.Items.Count>1)to.SelectedIndex=1;}
            Controls.Add(MainForm.LabelAt("Objet",22,86,640,24,10,FontStyle.Bold,MainForm.Ink));var title=new TextBox{Left=22,Top=112,Width=673,MaxLength=120,Text=previous==null?"":previous.Title};Controls.Add(title);
            Controls.Add(MainForm.LabelAt("Version prête (commit Git ou empreinte, si applicable)",22,152,670,24,10,FontStyle.Bold,MainForm.Ink));var revision=new TextBox{Left=22,Top=178,Width=673,MaxLength=160};Controls.Add(revision);
            Controls.Add(MainForm.LabelAt("Instructions pour le compte destinataire",22,218,670,24,10,FontStyle.Bold,MainForm.Ink));var prompt=new TextBox{Left=22,Top=246,Width=673,Height=214,Multiline=true,ScrollBars=ScrollBars.Vertical,MaxLength=24000};Controls.Add(prompt);
            var back=new CheckBox{Left=22,Top=478,Width=670,Text="Renvoyer la réponse dans la conversation source (relance l'agent)",Checked=true};Controls.Add(back);
            var send=new Button{Left=515,Top=520,Width=180,Height=32,Text="Envoyer la demande",BackColor=MainForm.Green,ForeColor=Color.White};Controls.Add(send);
            send.Click+=delegate{try{if(from.SelectedItem==null || to.SelectedItem==null)throw new InvalidOperationException("Connectez deux canaux avant l'envoi.");var source=store.Channel((string)from.SelectedItem);var m=store.Enqueue(source.Id,previous==null?source.AnchorThreadId:previous.SourceThreadId,(string)to.SelectedItem,title.Text,prompt.Text,revision.Text,back.Checked,previous==null?null:previous.Id);MessageId=m.Id;DialogResult=DialogResult.OK;Close();}catch(Exception e){MessageBox.Show(this,Program.SafeError(e),"Relais");}};
        }
    }
    internal sealed class RelayConnectForm : Form
    {
        public RelayConnectForm()
        {
            Text="Connecter une instance au relais";ClientSize=new Size(730,530);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            Controls.Add(MainForm.LabelAt("Une connexion par compte et par projet",22,20,680,38,19,FontStyle.Bold,MainForm.Ink));
            Controls.Add(MainForm.LabelAt("Copiez la consigne dans une conversation de l'instance choisie.\nElle identifiera le compte connecté et activera son canal local.",23,67,680,52,10,FontStyle.Regular,MainForm.Muted));
            Controls.Add(MainForm.LabelAt("Nom du canal (ex. publication-creezio)",23,130,650,25,10,FontStyle.Bold,MainForm.Ink));var name=new TextBox{Left=23,Top=157,Width=675,Text="publication-creezio",MaxLength=48};Controls.Add(name);
            Controls.Add(MainForm.LabelAt("Dossier partagé du projet",23,199,650,25,10,FontStyle.Bold,MainForm.Ink));var path=new TextBox{Left=23,Top=226,Width=540};Controls.Add(path);var pick=new Button{Left=571,Top=222,Width=127,Height=33,Text="Parcourir…"};Controls.Add(pick);
            pick.Click+=delegate{using(var dialog=new FolderBrowserDialog())if(dialog.ShowDialog(this)==DialogResult.OK)path.Text=dialog.SelectedPath;};
            var preview=new TextBox{Left=23,Top=275,Width=675,Height=134,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};Controls.Add(preview);
            var full=new CheckBox{Left=23,Top=420,Width=675,Height=30,Checked=true,Text="Exiger Accès complet confirmé dans la conversation de connexion"};Controls.Add(full);
            var copy=new Button{Left=457,Top=475,Width=241,Height=34,Text="Copier la consigne de connexion"};Controls.Add(copy);
            copy.Click+=delegate{try{RelayStore.ChannelId(name.Text);string folder=RelayStore.WorkspacePath(path.Text);string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreezioRelay.exe");if(!File.Exists(exe))throw new InvalidOperationException("Gardez CreezioRelay.exe à côté du switcher.");string command="@{operation='register';channel="+DesktopRuntime.QuotePS(name.Text)+";workspace="+DesktopRuntime.QuotePS(folder)+";requireFullAccess="+(full.Checked?"$true":"$false")+"} | ConvertTo-Json -Compress | & "+DesktopRuntime.QuotePS(exe);preview.Text="Je veux connecter cette conversation au relais local Creezio pour ce projet et autoriser les demandes entre mes canaux connectés. "+(full.Checked?"J'ai choisi Accès complet dans cette conversation. ":"")+"Exécute cette commande PowerShell, puis confirme le compte, le dossier et les permissions retournés. Ne change pas de compte ni de permissions.\r\n\r\n"+command;Clipboard.SetText(preview.Text);}catch(Exception e){MessageBox.Show(this,Program.SafeError(e),"Relais");}};
        }
    }
}
