using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class RoutingPreviewForm : Form
    {
        public RoutingPreviewForm(RelayStore store,RelayPolicy policy)
        {
            Text="Simulation · aucune tâche ne sera envoyée";ClientSize=new Size(960,640);MinimumSize=new Size(720,480);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var inputs=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(18)};inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,230));inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(inputs);
            Func<string,string[],ComboBox> select=(label,items)=>{inputs.Controls.Add(new Label{Text=label,AutoSize=true,Margin=new Padding(0,8,8,8)});var c=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};c.Items.AddRange(items);if(c.Items.Count>0)c.SelectedIndex=0;inputs.Controls.Add(c);return c;};
            var source=select("Agent source",store.Channels().Select(c=>c.Id).ToArray());var project=select("Projet",policy.Projects.Select(p=>p.Id).ToArray());
            inputs.Controls.Add(new Label{Text="Type de tâche",AutoSize=true});var kind=new TextBox{Text="review",Dock=DockStyle.Top};inputs.Controls.Add(kind);
            inputs.Controls.Add(new Label{Text="Capacités requises",AutoSize=true});var capabilities=new TextBox{Dock=DockStyle.Top};inputs.Controls.Add(capabilities);
            var resource=select("Ressource",new[]{""}.Concat(policy.Resources.Select(r=>r.Id)).ToArray());
            var explicitRequest=new CheckBox{Text="Demande explicite de l'utilisateur",Checked=true,AutoSize=true};inputs.Controls.Add(explicitRequest);var run=new Button{Text="Simuler",AutoSize=true};inputs.Controls.Add(run);
            var rows=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true};rows.Columns.Add("Agent",150);rows.Columns.Add("Décision",140);rows.Columns.Add("Quota",90);rows.Columns.Add("Explication",550);Controls.Add(rows);rows.BringToFront();
            run.Click+=delegate{try{rows.Items.Clear();var options=new RelayRouter(store).Preview(new RelayJobSpec{From=(string)source.SelectedItem,Project=(string)project.SelectedItem,Kind=kind.Text,Capabilities=capabilities.Text,Resource=String.IsNullOrWhiteSpace((string)resource.SelectedItem)?null:(string)resource.SelectedItem,ExplicitDelegation=explicitRequest.Checked,Access="read"},policy);bool chosen=false;foreach(var o in options){string decision=!o.Allowed?"Exclu":!chosen?"Choisi":"Alternative";if(o.Allowed)chosen=true;rows.Items.Add(new ListViewItem(new[]{o.Channel,decision,ProductUx.Percent(o.Remaining),o.Reason}));}}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}};ProductUx.Accessible(this);
        }
    }
    internal sealed class ConfigPreviewForm : Form
    {
        public ConfigPreviewForm(RelayPolicy before,RelayPolicy after)
        {
            Text="Changements de configuration";ClientSize=new Size(820,580);MinimumSize=new Size(640,420);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var report=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false};Controls.Add(report);
            report.Text="Les demandes non démarrées seront vérifiées avec cette configuration. Les conversations en cours conservent leur mandat.\r\n\r\n"+Difference(before,after);
            var apply=new Button{Text="Appliquer ces changements",Dock=DockStyle.Bottom,Height=44,DialogResult=DialogResult.OK};Controls.Add(apply);var cancel=new Button{Text="Revenir à l'édition",Dock=DockStyle.Bottom,Height=36,DialogResult=DialogResult.Cancel};Controls.Add(cancel);CancelButton=cancel;
        }
        internal static string Difference(RelayPolicy before,RelayPolicy after)
        {
            var lines=new System.Collections.Generic.List<string>();
            foreach(var property in typeof(RelayPolicy).GetProperties()){
                if(property.Name=="Revision")continue;
                object left=property.GetValue(before,null),right=property.GetValue(after,null);if(Json.Write(left)==Json.Write(right))continue;
                var collection=right as System.Collections.IEnumerable;
                if(collection!=null&&!(right is string)){
                    var oldRows=((System.Collections.IEnumerable)left).Cast<object>().ToDictionary(Identity);var newRows=collection.Cast<object>().ToDictionary(Identity);
                    foreach(string id in oldRows.Keys.Except(newRows.Keys))lines.Add("Retiré · "+property.Name+" · "+id);
                    foreach(var row in newRows){if(!oldRows.ContainsKey(row.Key)){lines.Add("Ajouté · "+property.Name+" · "+row.Key);continue;}foreach(var field in row.Value.GetType().GetProperties()){object a=field.GetValue(oldRows[row.Key],null),b=field.GetValue(row.Value,null);if(Json.Write(a)!=Json.Write(b))lines.Add(row.Key+" · "+GuidedEditor.Label(field.Name)+" : "+Display(a)+" → "+Display(b));}}
                }else lines.Add(GuidedEditor.Label(property.Name)+" : "+Display(left)+" → "+Display(right));
            }
            return lines.Count==0?"Aucun changement de valeur.":String.Join("\r\n",lines);
        }
        private static string Identity(object value){var p=value.GetType().GetProperty("Id")??value.GetType().GetProperty("Channel");return Convert.ToString(p.GetValue(value,null));}
        private static string Display(object value){if(value==null)return "aucun";if(value is bool)return (bool)value?"oui":"non";return GuidedEditor.Friendly(Convert.ToString(value));}
    }
}
