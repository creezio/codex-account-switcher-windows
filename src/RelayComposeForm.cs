using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class RelayComposeForm : Form
    {
        public string MessageId;
        private static ComboBox Select(TableLayoutPanel body,string label,IEnumerable<string> items){var c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};c.Items.AddRange(items.ToArray());if(c.Items.Count>0)c.SelectedIndex=0;Field(body,label,c);return c;}
        private static void Field(TableLayoutPanel body,string label,Control input){body.Controls.Add(new Label{Text=label,AutoSize=true,Margin=new Padding(0,8,12,12),MaximumSize=new Size(190,0)});input.Dock=DockStyle.Top;input.Margin=new Padding(0,5,0,10);input.AccessibleName=label;body.Controls.Add(input);}
        public RelayComposeForm(RelayStore store,RelayMessage previous,bool firstTest=false)
        {
            Text=previous==null?"Nouvelle demande":"Continuer l'échange";ClientSize=new Size(830,750);MinimumSize=new Size(650,480);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(20)};Controls.Add(scroll);
            var body=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2};body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,195));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));scroll.Controls.Add(body);
            var channels=store.Channels().Where(c=>c.Enabled).Select(c=>c.Id).ToArray();var policy=RelayPolicies.Load(store);
            var from=Select(body,"Conversation source",channels);var to=Select(body,"Destinataire",new[]{"Automatique selon mes règles"}.Concat(channels));
            var project=Select(body,"Projet",new[]{""}.Concat(policy.Projects.Select(p=>p.Id)));
            var access=Select(body,"Accès demandé",new[]{"Lecture et analyse","Modifier les fichiers","Action externe (publication, service…)"});
            var title=new TextBox{MaxLength=120,Text=previous!=null?previous.Title:firstTest?"Premier échange de test":""};Field(body,"Objet",title);
            var prompt=new TextBox{Multiline=true,Height=190,ScrollBars=ScrollBars.Vertical,MaxLength=24000,Text=firstTest?"Réponds simplement : Relais opérationnel. N'utilise aucun outil et ne modifie aucun fichier.":""};Field(body,"Travail à effectuer",prompt);
            var back=new CheckBox{Text="Renvoyer la réponse dans le chat source",AutoSize=true,Checked=true};Field(body,"Réponse",back);
            var note=new Label{Text="L'envoi part du chat de connexion du canal source. Pour déléguer depuis un autre chat, utilisez le skill intégré. Le mode de retour du projet s'applique.",AutoSize=true,MaximumSize=new Size(550,0)};Field(body,"",note);
            var advanced=new CheckBox{Text="Afficher les options avancées",AutoSize=true};Field(body,"Options",advanced);
            var options=new TableLayoutPanel{AutoSize=true,ColumnCount=2,Visible=false};options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,195));options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));body.Controls.Add(options);body.SetColumnSpan(options,2);options.Dock=DockStyle.Top;
            var kind=new TextBox{Text=previous!=null&&previous.Job!=null?previous.Job.Kind:"general"};Field(options,"Type de tâche",kind);
            var caps=new TextBox();Field(options,"Capacités (virgules)",caps);var resource=Select(options,"Ressource",new[]{""}.Concat(policy.Resources.Select(r=>r.Id)));
            var revision=new TextBox{MaxLength=160};Field(options,"Version prête",revision);
            var files=new TextBox{ReadOnly=true};Field(options,"Fichiers vérifiés",files);var hashes=new Dictionary<string,string>();
            var attach=new Button{Text="Choisir les fichiers…",AutoSize=true};Field(options,"Empreintes SHA256",attach);
            attach.Click+=delegate{try{if(from.SelectedItem==null)throw new InvalidOperationException("Choisissez la source.");string folder=store.Channel((string)from.SelectedItem).Workspace;using(var picker=new OpenFileDialog{Multiselect=true,InitialDirectory=folder})if(picker.ShowDialog(this)==DialogResult.OK){hashes=WorkspaceService.Manifest(folder,picker.FileNames);files.Text=hashes.Count+" fichier(s) · vérifiés avant exécution";}}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}};
            advanced.CheckedChanged+=delegate{options.Visible=advanced.Checked;};
            if(previous!=null){from.SelectedItem=previous.SourceChannelId;to.SelectedItem=previous.TargetChannelId;from.Enabled=false;to.Enabled=false;if(previous.Job!=null){project.SelectedItem=previous.Job.Project??"";caps.Text=previous.Job.Capabilities;resource.SelectedItem=previous.Job.Resource??"";access.SelectedIndex=Array.IndexOf(new[]{"read","write","external"},previous.Job.Access);}}
            var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(15),FlowDirection=FlowDirection.RightToLeft};Controls.Add(footer);
            var send=new Button{Text="Envoyer la demande",AutoSize=true,BackColor=MainForm.Green,ForeColor=Color.White};footer.Controls.Add(send);var cancel=new Button{Text="Annuler",AutoSize=true,DialogResult=DialogResult.Cancel};footer.Controls.Add(cancel);CancelButton=cancel;
            var id=Guid.NewGuid().ToString("N");
            send.Click+=async delegate{send.Enabled=false;try{
                if(from.SelectedItem==null||to.SelectedItem==null)throw new InvalidOperationException("Connectez les canaux avant l'envoi.");
                var source=store.Channel((string)from.SelectedItem);var spec=new RelayJobSpec{Id=id,From=source.Id,To=to.SelectedIndex==0?null:(string)to.SelectedItem,Project=(string)project.SelectedItem,Kind=kind.Text,Capabilities=caps.Text,Resource=(string)resource.SelectedItem,Access=new[]{"read","write","external"}[Math.Max(0,access.SelectedIndex)],Title=title.Text,Prompt=prompt.Text,Revision=revision.Text,Files=hashes,ReturnToSource=back.Checked,ExplicitDelegation=true,ReplyTo=previous==null?null:previous.Id};
                var m=await new RelayRouter(store).Submit(spec,previous==null?source.AnchorThreadId:previous.SourceThreadId,CancellationToken.None);MessageId=m.Id;DialogResult=DialogResult.OK;Close();
            }catch(Exception e){MessageBox.Show(this,Program.SafeError(e),"Relais");}finally{if(!IsDisposed)send.Enabled=true;}};ProductUx.Accessible(this);
        }
    }
}
