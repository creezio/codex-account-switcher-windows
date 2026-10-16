using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class GuidedEditor : Panel
    {
        private readonly RelayStore store;
        private readonly RelayPolicy policy;
        private readonly ToolTip help=new ToolTip();
        private bool advanced;
        private static readonly Dictionary<string,string> Labels=new Dictionary<string,string>{
            {"Id","Identifiant"},{"Name","Nom"},{"Channel","Instance connectée / agent"},{"Description","Rôle de l'agent"},{"Capabilities","Capacités déclarées"},{"Tasks","Types de tâches acceptés"},{"Projects","Projets autorisés"},{"Enabled","Activé"},{"AutoRoute","Disponible pour le choix automatique"},{"ReuseConversation","Réutiliser une conversation terminée"},{"MaxConcurrent","Travaux simultanés"},{"MinRemaining","Réserve minimale de quota (%)"},{"AllowUnknownQuota","Accepter un quota inconnu"},{"Permission","Permissions attendues dans Codex"},{"Model","Modèle (vide : choix de Codex)"},{"Instructions","Instructions"},{"Workspace","Dossier du projet"},{"Delegation","Quand autoriser la délégation"},{"SourceChannels","Agents pouvant déléguer"},{"TargetChannels","Agents destinataires autorisés"},{"MaxJobs","Nombre maximal de sous-tâches"},{"MaxDepth","Profondeur maximale"},{"MaxMinutes","Délai maximal avant démarrage (minutes)"},{"Project","Projet"},{"Channels","Agents autorisés"},{"ExternalId","Identifiant externe de la ressource"},{"Task","Type de tâche"},{"Sources","Agents sources"},{"Targets","Agents destinataires"},{"When","Quand utiliser cette règle"},{"Priority","Priorité de la règle"},{"RoutingStrategy","Stratégie de choix"},{"PreferredAgent","Agent préféré"},{"ReassignQueued","Réaffecter les demandes non démarrées"},{"ReturnMode","Remise des résultats"},{"ReturnDelaySeconds","Regrouper pendant (secondes)"},{"WorkspaceMode","Organisation des fichiers"},{"AllowedWorkspaces","Espaces autorisés (un chemin par ligne)"}};
        public GuidedEditor(RelayStore data,RelayPolicy configuration){store=data;policy=configuration;AutoScroll=true;BackColor=Color.White;Padding=new Padding(16);}
        protected override void Dispose(bool disposing){if(disposing)help.Dispose();base.Dispose(disposing);}
        internal static string Label(string key){string text;return Labels.TryGetValue(key,out text)?text:key;}
        private string[] Choices(string key,Type type)
        {
            switch(key){
                case "Permission":return new[]{"inherit","full-access"};
                case "Delegation":return new[]{"explicit","rules"};
                case "RoutingStrategy":return new[]{"available","balanced","quota","preferred"};
                case "ReturnMode":return new[]{"immediate","batch","manual"};
                case "WorkspaceMode":return new[]{"shared","isolated"};
                case "Channel":case "PreferredAgent":return new[]{""}.Concat(store.Channels().Select(c=>c.Id)).ToArray();
                case "Project":return (type==typeof(RelayRule)?new[]{"*"}:new[]{""}).Concat(policy.Projects.Select(p=>p.Id)).ToArray();
            }
            return null;
        }
        internal static string Friendly(string value)
        {
            switch(value){case "inherit":return "Respecter le mode de Codex";case "full-access":return "Exiger Accès complet vérifié";case "explicit":return "Sur demande explicite";case "rules":return "Selon mes règles";case "available":return "Disponibilité puis charge";case "balanced":return "Répartir la charge";case "quota":return "Préserver la marge de quota";case "preferred":return "Privilégier un agent";case "immediate":return "Retour immédiat";case "batch":return "Regrouper les résultats";case "manual":return "Consulter sans relancer la source";case "shared":return "Dossier partagé";case "isolated":return "Espaces autorisés (un chemin par ligne)";case "*":return "Tous";case "":return "Choisir… / aucun";default:return value;}
        }
        public void Edit(object item)
        {
            SuspendLayout();foreach(Control c in Controls.Cast<Control>().ToArray())c.Dispose();Controls.Clear();
            if(item==null){Controls.Add(new Label{Text="Ajoutez ou sélectionnez un élément.",AutoSize=true});ResumeLayout();return;}
            var table=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(4)};table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,42));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,58));Controls.Add(table);
            var toggle=new CheckBox{Text="Afficher les réglages avancés",AutoSize=true,Checked=advanced,Margin=new Padding(0,0,0,18)};table.Controls.Add(toggle,0,0);table.SetColumnSpan(toggle,2);toggle.CheckedChanged+=delegate{advanced=toggle.Checked;Edit(item);};
            int row=1;
            foreach(var property in item.GetType().GetProperties().Where(p=>p.CanRead&&p.CanWrite)){
                var p=property;string key=p.Name;
                if(!advanced&&new[]{"MaxJobs","MaxDepth","MaxMinutes","Model","ReuseConversation","ReturnDelaySeconds","AllowedWorkspaces","Priority"}.Contains(key))continue;
                var label=new Label{Text=Label(key),AutoSize=true,MaximumSize=new Size(280,0),Margin=new Padding(0,8,12,12)};table.Controls.Add(label,0,row);
                Control input;object value=p.GetValue(item,null);var choices=Choices(key,item.GetType());
                if(p.PropertyType==typeof(bool)){
                    var check=new CheckBox{Checked=(bool)value,Text="Oui",AutoSize=true};check.CheckedChanged+=delegate{p.SetValue(item,check.Checked,null);};input=check;
                }else if(p.PropertyType==typeof(int)||p.PropertyType==typeof(double)){
                    bool fraction=p.PropertyType==typeof(double);var number=new NumericUpDown{Minimum=key=="Priority"?Int32.MinValue:key=="ReturnDelaySeconds"?5:new[]{"MaxJobs","MaxMinutes","MaxConcurrent"}.Contains(key)?1:0,Maximum=key=="Priority"?Int32.MaxValue:key=="MinRemaining"?100:key=="MaxDepth"?8:key=="MaxJobs"?256:key=="MaxConcurrent"?(item is RelayAgent?8:16):key=="ReturnDelaySeconds"?3600:10080,DecimalPlaces=fraction?1:0,Value=Convert.ToDecimal(value)};
                    number.ValueChanged+=delegate{p.SetValue(item,fraction?(object)(double)number.Value:(int)number.Value,null);};input=number;
                }else if(choices!=null){
                    var combo=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,FormattingEnabled=true};combo.Items.AddRange(choices);if(!choices.Contains((string)value??""))combo.Items.Add((string)value??"");combo.SelectedItem=(string)value??"";
                    combo.Format+=delegate(object sender,ListControlConvertEventArgs e){e.Value=Friendly((string)e.ListItem);};combo.SelectedIndexChanged+=delegate{p.SetValue(item,(string)combo.SelectedItem,null);};input=combo;
                }else if(new[]{"SourceChannels","TargetChannels","Channels","Sources","Targets","Projects"}.Contains(key)){
                    var checkedList=new CheckedListBox{CheckOnClick=true,Height=86};string[] known=(key=="Projects"?policy.Projects.Select(x=>x.Id):store.Channels().Select(x=>x.Id)).ToArray();var selected=RelayPolicies.Tags((string)value);var options=new[]{"*"}.Concat(known).Concat(selected).Distinct().ToArray();
                    foreach(string option in options)checkedList.Items.Add(option,selected.Contains(option));
                    checkedList.ItemCheck+=delegate(object sender,ItemCheckEventArgs e){var entries=checkedList.CheckedItems.Cast<string>().ToList();string changed=(string)checkedList.Items[e.Index];if(e.NewValue==CheckState.Checked){if(!entries.Contains(changed))entries.Add(changed);}else entries.Remove(changed);p.SetValue(item,String.Join(",",entries),null);};input=checkedList;
                }else{
                    bool multi=new[]{"Instructions","When","AllowedWorkspaces"}.Contains(key);var field=new TextBox{Text=(string)value??"",Multiline=multi,Height=multi?82:28,ScrollBars=multi?ScrollBars.Vertical:ScrollBars.None};field.TextChanged+=delegate{p.SetValue(item,field.Text.Trim(),null);};
                    if(key=="Workspace"){var panel=new TableLayoutPanel{ColumnCount=2,Height=34};panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));field.Dock=DockStyle.Fill;panel.Controls.Add(field);var browse=new Button{Text="Parcourir",Dock=DockStyle.Fill};panel.Controls.Add(browse);browse.Click+=delegate{using(var folder=new FolderBrowserDialog())if(folder.ShowDialog(this)==DialogResult.OK)field.Text=folder.SelectedPath;};input=panel;}else input=field;
                }
                input.Dock=DockStyle.Top;input.Margin=new Padding(0,4,0,10);input.AccessibleName=label.Text;table.Controls.Add(input,1,row++);
                var description=(DescriptionAttribute)p.GetCustomAttributes(typeof(DescriptionAttribute),true).FirstOrDefault();if(description!=null)help.SetToolTip(input,description.Description);
            }
            var note=new Label{AutoSize=true,MaximumSize=new Size(680,0),ForeColor=MainForm.Muted,Text="* signifie tous. Les capacités sont des étiquettes déclarées. L'installation d'un plugin et l'accès réel à une ressource sont vérifiés séparément.",Margin=new Padding(0,12,0,16)};table.Controls.Add(note,0,row);table.SetColumnSpan(note,2);ProductUx.Accessible(this);ResumeLayout(true);
        }
    }
}
