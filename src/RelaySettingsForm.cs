using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class RelaySettingsForm : Form
    {
        private readonly RelayStore store;
        private readonly RelayPolicy policy;
        private readonly System.Collections.Generic.List<Action> refreshEditors=new System.Collections.Generic.List<Action>();
        public RelaySettingsForm(RelayStore data,string selectedTab=null)
        {
            store=data;policy=RelayPolicies.Load(store);Text="Configurer la délégation";ClientSize=new Size(1000,720);MinimumSize=new Size(850,600);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var intro=new Label{Dock=DockStyle.Top,Height=80,Padding=new Padding(18),Text="Vos usages, vos règles. Définissez les rôles et les projets qui peuvent déléguer.\nLes capacités déclarées ne donnent aucun accès à un plugin ou à une ressource privée."};Controls.Add(intro);
            var tabs=new TabControl{Dock=DockStyle.Fill};Controls.Add(tabs);tabs.BringToFront();
            var general=new TabPage("Général");tabs.TabPages.Add(general);
            var autoInstall=new CheckBox{Left=22,Top=25,Width=820,Text="Installer l'intégration sur les instances gérées au lancement du switcher",Checked=policy.AutoInstallManaged};general.Controls.Add(autoInstall);
            var persistent=new CheckBox{Left=22,Top=66,Width=820,Text="Garder le moteur du relais actif, même sans travail en attente",Checked=policy.KeepWorkerRunning};general.Controls.Add(persistent);
            general.Controls.Add(new Label{Left=22,Top=114,Width=470,Text="Nombre maximal de travaux simultanés"});var max=new NumericUpDown{Left=510,Top=110,Minimum=1,Maximum=16,Value=policy.MaxParallel};general.Controls.Add(max);
            general.AutoScroll=true;
            var template=new Button{Left=22,Top=375,Text="Partir d'un modèle…",AutoSize=true};general.Controls.Add(template);template.Click+=delegate{using(var f=new TemplateForm(store,policy))if(f.ShowDialog(this)==DialogResult.OK)foreach(var refresh in refreshEditors)refresh();};
            general.Controls.Add(new Label{Left=22,Top=330,Width=470,Text="Travaux simultanés par compte (toutes ses instances)"});var perAccount=new NumericUpDown{Left=510,Top=325,Minimum=1,Maximum=16,Value=policy.MaxPerAccount};general.Controls.Add(perAccount);
            general.Controls.Add(new Label{Left=22,Top=160,Width=880,Height=150,Text="La délégation reste explicite par défaut. Dans un projet, choisissez « Selon mes règles » pour permettre les règles automatiques que vous définissez.\n\nUne règle choisit les destinataires selon vos types de tâches et vos capacités. Les instructions libres précisent quand l'agent doit l'utiliser.\n\nLe moteur continue les travaux acceptés après fermeture de l'interface. Il s'arrête lorsqu'il devient inactif, sauf si l'option ci-dessus est activée."});
            AddEditor(tabs,"Agents",policy.Agents,()=>new RelayAgent{Channel=store.Channels().Select(c=>c.Id).FirstOrDefault(id=>!policy.Agents.Any(a=>a.Channel==id))??"nouvel-agent"});
            AddEditor(tabs,"Projets",policy.Projects,()=>new RelayProject{Id=Unique("projet",policy.Projects.Select(p=>p.Id)),Name="Nouveau projet",Workspace=store.Channels().Select(c=>c.Workspace).FirstOrDefault()});
            AddEditor(tabs,"Ressources",policy.Resources,()=>new RelayResource{Id=Unique("ressource",policy.Resources.Select(p=>p.Id)),Project=policy.Projects.Select(p=>p.Id).FirstOrDefault()});
            AddEditor(tabs,"Règles",policy.Rules,()=>new RelayRule{Id=Unique("regle",policy.Rules.Select(p=>p.Id))});
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=58,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(12)};Controls.Add(bottom);
            var save=new Button{Text="Enregistrer",AutoSize=true,BackColor=MainForm.Green,ForeColor=Color.White};bottom.Controls.Add(save);
            var cancel=new Button{Text="Annuler",AutoSize=true};bottom.Controls.Add(cancel);cancel.Click+=delegate{Close();};
            var simulate=new Button{Text="Tester le routage",AutoSize=true};bottom.Controls.Add(simulate);simulate.Click+=delegate{using(var f=new RoutingPreviewForm(store,policy))f.ShowDialog(this);};
            var export=new Button{Text="Exporter…",AutoSize=true};bottom.Controls.Add(export);export.Click+=delegate{using(var f=new SaveFileDialog{Filter="Configuration (*.json)|*.json",FileName="delegation.json"})if(f.ShowDialog(this)==DialogResult.OK){try{RelayPolicies.Validate(policy,false);SafeFiles.AtomicWrite(f.FileName,System.Text.Encoding.UTF8.GetBytes(RelayPolicies.Export(policy)));}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}}};
            var import=new Button{Text="Importer…",AutoSize=true};bottom.Controls.Add(import);import.Click+=delegate{using(var f=new OpenFileDialog{Filter="Configuration (*.json)|*.json"})if(f.ShowDialog(this)==DialogResult.OK){try{var next=RelayPolicies.Import(SafeFiles.ReadText(f.FileName));RelayPolicies.ValidateReferences(next,store);using(var preview=new ConfigPreviewForm(policy,next))if(preview.ShowDialog(this)==DialogResult.OK){RelayPolicies.Save(store,next);DialogResult=DialogResult.OK;Close();}}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}}};
            var undo=new Button{Text="Restaurer…",AutoSize=true};bottom.Controls.Add(undo);undo.Click+=delegate{try{var old=store.ReadRecord<RelayPolicy>("policy-previous.dpapi");using(var f=new ConfigPreviewForm(policy,old))if(f.ShowDialog(this)==DialogResult.OK){RelayPolicies.Restore(store);DialogResult=DialogResult.OK;Close();}}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}};
            save.Click+=delegate{try{policy.AutoInstallManaged=autoInstall.Checked;policy.KeepWorkerRunning=persistent.Checked;policy.MaxParallel=(int)max.Value;policy.MaxPerAccount=(int)perAccount.Value;RelayPolicies.ValidateReferences(policy,store);using(var preview=new ConfigPreviewForm(RelayPolicies.Load(store),policy))if(preview.ShowDialog(this)!=DialogResult.OK)return;RelayPolicies.Save(store,policy);if(policy.KeepWorkerRunning)RelayWorker.Ensure(store);DialogResult=DialogResult.OK;Close();}catch(Exception e){MessageBox.Show(this,Program.SafeError(e),"Configuration");}};
            if(selectedTab!=null)foreach(TabPage tab in tabs.TabPages)if(tab.Text==selectedTab)tabs.SelectedTab=tab;
            string initial=Json.Write(policy);
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(DialogResult!=DialogResult.OK&&(Json.Write(policy)!=initial||autoInstall.Checked!=policy.AutoInstallManaged||persistent.Checked!=policy.KeepWorkerRunning||max.Value!=policy.MaxParallel||perAccount.Value!=policy.MaxPerAccount)&&MessageBox.Show(this,"Abandonner les changements non enregistrés ?","Brouillon de configuration",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)e.Cancel=true;};
            ProductUx.Accessible(this);
        }
        private static string Unique(string prefix,IEnumerable<string> existing){int n=1;while(existing.Contains(prefix+"-"+n))n++;return prefix+"-"+n;}
        private void AddEditor<T>(TabControl tabs,string title,List<T> data,Func<T> create)
        {
            var page=new TabPage(title);tabs.TabPages.Add(page);
            var grid=new GuidedEditor(store,policy){Dock=DockStyle.Fill};page.Controls.Add(grid);
            var side=new Panel{Dock=DockStyle.Left,Width=260};page.Controls.Add(side);
            var list=new ListBox{Dock=DockStyle.Fill};side.Controls.Add(list);
            Action reload=delegate{object current=list.SelectedItem;list.DataSource=null;list.DataSource=data.ToArray();if(current!=null&&data.Contains((T)current))list.SelectedItem=current;};
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=90,Padding=new Padding(6)};side.Controls.Add(buttons);
            var add=new Button{Text="Ajouter",AutoSize=true};var remove=new Button{Text="Retirer",AutoSize=true};buttons.Controls.Add(add);buttons.Controls.Add(remove);
            add.Click+=delegate{var item=create();data.Add(item);reload();list.SelectedItem=item;};remove.Click+=delegate{if(list.SelectedItem!=null){data.Remove((T)list.SelectedItem);reload();}};
            list.SelectedIndexChanged+=delegate{grid.Edit(list.SelectedItem);};refreshEditors.Add(reload);reload();
        }
    }
    internal sealed class RelayIntegrationForm : Form
    {
        private readonly RelayStore store;
        private readonly ComboBox profiles=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
        private readonly string executable;
        private readonly TextBox report=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
        private sealed class Choice{public string Name,Home;public override string ToString(){return Name;}}
        public RelayIntegrationForm(RelayStore data,AccountService service)
        {
            store=data;executable=service==null?CodexEnvironment.FindExecutable():service.Settings.CodexExecutable;Text="Intégration Codex · skills et outils";ClientSize=new Size(950,620);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
            var top=new FlowLayoutPanel{Dock=DockStyle.Top,Height=98,Padding=new Padding(16)};Controls.Add(top);profiles.Width=420;top.Controls.Add(profiles);
            if(service!=null)foreach(var i in service.Data.Instances.Where(i=>!i.Archived))profiles.Items.Add(new Choice{Name=i.Name+(i.IsLocal?" · session habituelle":""),Home=service.Instances.Home(i)});
            else foreach(var c in store.Channels().GroupBy(x=>x.Home).Select(g=>g.First()))profiles.Items.Add(new Choice{Name=c.Name,Home=c.Home});
            var install=new Button{Text="Installer / mettre à jour",AutoSize=true};var inventory=new Button{Text="Inventaire des plugins",AutoSize=true};top.Controls.Add(install);top.Controls.Add(inventory);Controls.Add(report);report.BringToFront();
            if(profiles.Items.Count>0)profiles.SelectedIndex=0;
            profiles.SelectedIndexChanged+=delegate{ShowStatus();};ShowStatus();
            install.Click+=async delegate{if(profiles.SelectedItem==null)return;install.Enabled=false;try{var item=(Choice)profiles.SelectedItem;var state=await RelayIntegration.Install(store,item.Home,executable,true,CancellationToken.None);report.Text=state.Status+"\r\n\r\nUtilisez une nouvelle conversation pour vérifier le chargement des skills et des outils. La conversation actuelle est conservée.";}catch(Exception e){report.Text=Program.SafeError(e);}finally{install.Enabled=true;}};
            inventory.Click+=async delegate{if(profiles.SelectedItem==null)return;inventory.Enabled=false;try{var result=await RelayIntegration.Inventory(executable,((Choice)profiles.SelectedItem).Home,CancellationToken.None);report.Text="Métadonnées installées : l'accès aux ressources privées doit être vérifié dans le chat destinataire.\r\n\r\n"+Json.Write(result);}catch(Exception e){report.Text=Program.SafeError(e);}finally{inventory.Enabled=true;}};
        }
        private void ShowStatus(){if(profiles.SelectedItem==null){report.Text="Créez une instance ou connectez un canal pour installer l'intégration.";return;}var item=(Choice)profiles.SelectedItem;var state=RelayIntegration.Status(store,item.Home);report.Text=(state.Status??"Intégration non installée")+"\r\nProfil : "+item.Home+"\r\n\r\nL'intégration fournit deux skills et des outils pour les travaux configurés par l'utilisateur. Elle n'installe pas les plugins privés d'autres comptes.\r\n\r\nConnectez ensuite un canal pour le dossier voulu. Chaque conversation dispose de sa propre session de relais.";}
    }
}
