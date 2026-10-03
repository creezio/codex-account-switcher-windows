using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class TemplateForm : Form
    {
        public TemplateForm(RelayStore store,RelayPolicy policy)
        {
            Text="Modèle facultatif";ClientSize=new Size(720,430);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var body=new TableLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,ColumnCount=2,Padding=new Padding(20)};body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(body);
            Func<string,string[],ComboBox> choose=(label,items)=>{body.Controls.Add(new Label{Text=label,AutoSize=true});var c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top};c.Items.AddRange(items);if(c.Items.Count>0)c.SelectedIndex=0;body.Controls.Add(c);return c;};
            var model=choose("Point de départ",new[]{"Relecture et analyse","Développement délégué","Accès à une ressource privée"});
            var source=choose("Agent source",store.Channels().Select(c=>c.Id).ToArray());var target=choose("Agent destinataire",store.Channels().Select(c=>c.Id).ToArray());if(target.Items.Count>1)target.SelectedIndex=1;
            body.Controls.Add(new Label{Text="Identifiant du projet",AutoSize=true});var project=new TextBox{Dock=DockStyle.Top,Text="mon-projet"};body.Controls.Add(project);
            var info=new Label{Text="Le modèle utilise les canaux que vous choisissez. Il conserve les agents existants et crée un projet en délégation explicite. Sa règle est désactivée jusqu'à votre configuration. Aucun droit ni plugin n'est transféré.",AutoSize=true,MaximumSize=new Size(660,0)};body.Controls.Add(info);body.SetColumnSpan(info,2);
            var apply=new Button{Text="Ajouter au brouillon",AutoSize=true};body.Controls.Add(apply);apply.Click+=delegate{try{Apply(store,policy,project.Text.Trim(),(string)source.SelectedItem,(string)target.SelectedItem,model.SelectedIndex);DialogResult=DialogResult.OK;Close();}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}};ProductUx.Accessible(this);
        }
        internal static void Apply(RelayStore store,RelayPolicy p,string id,string source,string target,int model)
        {
            RelayStore.ChannelId(id);if(p.Projects.Any(x=>x.Id==id)||p.Rules.Any(x=>x.Id==id+"-route")||p.Resources.Any(x=>x.Id==id+"-resource"))throw new InvalidOperationException("Cet identifiant existe déjà.");
            var a=store.Channel(source);var b=store.Channel(target);if(a.Id==b.Id)throw new InvalidOperationException("Choisissez deux agents distincts.");if(!RelayStore.SamePath(a.Workspace,b.Workspace))throw new InvalidOperationException("Ce modèle simple exige le même dossier sur les deux canaux. Configurez les espaces isolés dans Projets pour un autre usage.");
            string kind=model==0?"review":model==1?"development":"specialist";
            if(!p.Agents.Any(x=>x.Channel==target))p.Agents.Add(new RelayAgent{Channel=target,Description=model==0?"Relecture et analyse":model==1?"Développement":"Spécialiste",AutoRoute=false,Instructions=model==0?"Analyser les fichiers dans le périmètre demandé. Ne rien modifier pour une demande de lecture.":"Respecter le périmètre demandé et vérifier l'accès réel aux outils nécessaires."});
            p.Projects.Add(new RelayProject{Id=id,Name=id,Workspace=a.Workspace,SourceChannels=source,TargetChannels=target,Delegation="explicit"});
            p.Rules.Add(new RelayRule{Id=id+"-route",Project=id,Task=kind,Sources=source,Targets=target,Enabled=false,When="À compléter par l'utilisateur selon son propre workflow."});
            if(model==2)p.Resources.Add(new RelayResource{Id=id+"-resource",Project=id,Channels=target,Instructions="Renseigner la ressource et vérifier son accès depuis le compte destinataire."});
        }
    }
}
