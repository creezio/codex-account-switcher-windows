using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class PluginAccessEditor
    {
        internal static EditWindow Create(DesktopContext context, ShellWindow shell, DesktopInstance owner, DesktopInstance client, InstancePlugin plugin, PluginAccess service = null)
        {
            if(client == null) throw new InvalidOperationException("Choisissez d’abord l’instance destinataire dans « Partager avec ».");
            service = service ?? new PluginAccess(context.Store);
            var accounts = context.Accounts();
            var dialog = new EditWindow(shell, plugin.Name, "Enregistrer les actions");
            dialog.Width = Math.Min(760, SystemParameters.WorkArea.Width);
            var identity = new StackPanel();
            identity.Children.Add(Ui.Text("Depuis " + owner.Name + "  →  Pour " + client.Name, 16));
            identity.Children.Add(Ui.Text("Compte du plugin : " + (accounts.Data.Profiles.FirstOrDefault(p=>p.Key==owner.AccountKey)?.Email ?? owner.Name), 13, true));
            dialog.Fields.Children.Add(Ui.Card(identity));
            dialog.Fields.Children.Add(Ui.Text("Autorisez les actions que cette instance pourra utiliser.", 16));
            dialog.Fields.Children.Add(Ui.Text("Les actions cochées utilisent les accès de " + owner.Name + ". Le switcher ne sait pas encore limiter ce plugin à des éléments précis.", 13, true));
            var status = Ui.Text("Chargement des actions de " + plugin.Name + "…", 13, true);
            dialog.Fields.Children.Add(status);
            var search = new TextBox();
            dialog.Fields.Children.Add(Ui.SearchField(search, "Rechercher une action"));
            var actions = Ui.Actions(dialog.Fields);
            var list = new StackPanel();dialog.Fields.Children.Add(list);
            var selection = new HashSet<string>();
            PluginAccessState snapshot = null;
            var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            bool closed = false, loading = false;
            dialog.SaveButton.IsEnabled = false;
            Action summarize = () => {
                if(snapshot==null) return;
                int reads = snapshot.Tools.Count(t=>selection.Contains(PluginAccess.Key(t))&&t.ReadOnly);
                int changes = selection.Count-reads;
                status.Text = selection.Count + " action(s) cochée(s) · " + reads + " lecture(s) · " + changes + " autre(s) action(s)";
                dialog.SaveButton.IsEnabled = !loading && !selection.SetEquals(snapshot.Selected);
            };
            Action render = () => {
                list.Children.Clear();if(snapshot==null)return;
                var missing=selection.Where(k=>!snapshot.Tools.Any(t=>PluginAccess.Key(t)==k)).ToArray();
                foreach(string key in missing){var absent=Ui.Check("Action devenue indisponible : "+key,true,list);absent.Checked+=delegate{selection.Add(key);summarize();};absent.Unchecked+=delegate{selection.Remove(key);summarize();};}
                foreach(bool read in new[]{true,false}){
                    var matching=snapshot.Tools.Where(t=>t.ReadOnly==read&&(PluginAccess.Label(t)+" "+t.Description+" "+t.Name).IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();
                    if(matching.Length==0)continue;
                    list.Children.Add(Ui.Text(read?"LECTURE":"AUTRES ACTIONS · peuvent modifier des données",12,true));
                    foreach(var tool in matching){
                        string key=PluginAccess.Key(tool), limitation=ToolTunnel.Limitation(tool);
                        var card=new StackPanel();var check=Ui.Check(PluginAccess.Label(tool),selection.Contains(key),card);check.IsEnabled=limitation.Length==0;
                        check.ToolTip=tool.Description;
                        if(limitation.Length>0)card.Children.Add(Ui.Text(limitation,12,true));
                        else if(Object.Equals(Json.Get(tool.Annotations,"destructiveHint"),true))card.Children.Add(Ui.Text("Action signalée comme destructive par le plugin.",12,true));
                        var details=new StackPanel();details.Children.Add(Ui.Text(tool.Description??"Description non fournie par le plugin.",12,true));details.Children.Add(Ui.Text(tool.Name,12,true));
                        card.Children.Add(new Expander{Header="Détails de l’action",Content=details});
                        check.Checked+=delegate{selection.Add(key);summarize();};check.Unchecked+=delegate{selection.Remove(key);summarize();};
                        var border=Ui.Card(card);border.Padding=new Thickness(12);border.Margin=new Thickness(0,0,0,8);list.Children.Add(border);
                    }
                }
                if(list.Children.Count==0)list.Children.Add(Ui.Text("Aucune action correspondant à cette recherche.",14,true));
                summarize();
            };
            actions.Children.Add(Ui.Button("Cocher les lectures affichées",()=>{if(snapshot==null||loading)return;foreach(var t in snapshot.Tools.Where(t=>t.ReadOnly&&ToolTunnel.Limitation(t).Length==0&&(PluginAccess.Label(t)+" "+t.Description+" "+t.Name).IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0))selection.Add(PluginAccess.Key(t));render();}));
            actions.Children.Add(Ui.Button("Tout décocher",()=>{if(loading)return;selection.Clear();render();}));
            search.TextChanged+=delegate{render();};
            Func<Task> load = async () => {
                if(loading||closed)return;loading=true;dialog.SaveButton.IsEnabled=false;
                try{
                    var loaded=await service.Load(owner.Id,owner.AccountKey,accounts.Instances.Home(owner),client.Id,accounts.Instances.Home(client),client.AccountKey,plugin.Key,deadline.Token);
                    if(closed)return;snapshot=loaded;selection=new HashSet<string>(snapshot.Selected);
                    if(snapshot.ExistingRules.Length>0){
                        var existing=new StackPanel();existing.Children.Add(Ui.Text("Des accès existent aussi dans d’autres règles. Ils sont conservés et ne seront pas révoqués par ce formulaire.",13,true));
                        foreach(var rule in snapshot.ExistingRules)existing.Children.Add(Ui.Text(rule.Name,12,true));
                        dialog.Fields.Children.Insert(3,Ui.Card(existing));
                    }
                    render();
                }catch(Exception e){if(!closed)status.Text=Program.SafeError(e);}
                finally{loading=false;if(!closed)summarize();}
            };
            dialog.Loaded+=async delegate{await load();};
            dialog.Closed+=delegate{closed=true;deadline.Cancel();deadline.Dispose();};
            dialog.Dirty=()=>snapshot!=null&&!selection.SetEquals(snapshot.Selected);
            dialog.Save=async()=>{
                if(snapshot==null||loading)throw new InvalidOperationException("Attendez le chargement des actions.");
                dialog.Fields.IsEnabled=false;
                try{await service.Save(snapshot,selection,CancellationToken.None);}finally{dialog.Fields.IsEnabled=true;}
            };
            return dialog;
        }
    }
}
