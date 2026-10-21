using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    // A single global policy is separate from the user's per-resource grants.
    // Rendering or opening this control never changes either policy or grants.
    internal sealed class ToolAccessModeView : StackPanel
    {
        internal const string ToggleLabel="Accès complet entre toutes les instances";
        internal const string ScopeDescription="Toutes les instances actuelles et futures peuvent utiliser les ressources et outils de toutes les autres : lecture, écriture, publication et suppression. Les nouvelles ressources sont incluses automatiquement.";
        private readonly Window owner;
        private readonly Func<ToolAccessPolicyState> read;
        private readonly Func<bool,string,Task<ToolAccessPolicyState>> save;
        private readonly Func<Task> changed;
        private readonly TextBlock status=Ui.Text("",14),detail=Ui.Text("",12,true);
        internal readonly Button Configure;
        internal bool FullAccessEnabled {get;private set;}
        internal ToolAccessModeView(DesktopContext context,Window owner,Func<Task> changed=null)
            :this(owner,()=>ToolAccessPolicy.Read(context.Store),(enabled,revision)=>Task.Run(()=>ToolAccessPolicy.Save(context.Store,enabled,revision)),changed){}
        internal ToolAccessModeView(Window owner,Func<ToolAccessPolicyState> read,Func<bool,string,Task<ToolAccessPolicyState>> save,Func<Task> changed=null)
        {
            this.owner=owner;this.read=read;this.save=save;this.changed=changed;
            Children.Add(status);Children.Add(detail);
            Configure=Ui.AsyncButton("Accès global entre instances…",async()=>{
                var dialog=CreateDialog(this.owner,this.read,this.save);dialog.ShowDialog();Refresh();
                if(dialog.Saved&&this.changed!=null)await this.changed();
            },e=>status.Text=Program.SafeError(e));
            Configure.HorizontalAlignment=HorizontalAlignment.Left;
            Configure.Margin=new Thickness(0,6,0,8);Children.Add(Configure);
            Refresh();
        }
        internal static string StatusLabel(bool enabled)=>enabled?"Accès complet global actif":"Accès personnalisés entre instances";
        internal void Refresh()
        {
            try{
                var value=read();if(value==null)throw new InvalidOperationException("Le réglage global n’a pas pu être lu.");
                FullAccessEnabled=value.Enabled;status.Text=StatusLabel(value.Enabled);
                detail.Text=value.Enabled?"Toutes les instances et toutes leurs ressources sont incluses. Les sélections ci-dessous restent conservées pour le mode personnalisé.":"Chaque instance utilise ses partages enregistrés. Le mode global peut inclure toutes les instances et leurs ressources.";
            }catch(Exception error){FullAccessEnabled=false;status.Text="Accès global à vérifier";detail.Text=Program.SafeError(error);}
        }
        internal static EditWindow CreateDialog(Window owner,Func<ToolAccessPolicyState> read,Func<bool,string,Task<ToolAccessPolicyState>> save)
        {
            var original=read();if(original==null)throw new InvalidOperationException("Le réglage global n’a pas pu être lu.");
            var dialog=new EditWindow(owner,"Accès global entre instances"){Height=530};
            dialog.Fields.Children.Add(Ui.Text(ScopeDescription,14));
            var enabled=Ui.Check(ToggleLabel,original.Enabled,dialog.Fields);
            dialog.Fields.Children.Add(Ui.Text("Ce réglage s’applique dans les deux sens, sans sélection par compte, instance, plugin ou ressource.",13,true));
            dialog.Fields.Children.Add(Ui.Text("Les partages personnalisés restent enregistrés. Désactiver ce mode les remet en application sans les recréer.",13,true));
            dialog.Fields.Children.Add(Ui.Text("Les autorisations natives des comptes et des services restent applicables. Changer ce réglage ne lance aucune tâche et n’envoie aucun message.",13,true));
            dialog.Dirty=()=>(enabled.IsChecked==true)!=original.Enabled;
            dialog.SaveButton.IsEnabled=false;
            enabled.Checked+=delegate{dialog.SaveButton.IsEnabled=dialog.Dirty();};
            enabled.Unchecked+=delegate{dialog.SaveButton.IsEnabled=dialog.Dirty();};
            dialog.Save=async()=>{
                if(!dialog.Dirty())return;
                bool requested=enabled.IsChecked==true;enabled.IsEnabled=false;
                try{
                    var saved=await save(requested,original.Revision);
                    if(saved==null||saved.Enabled!=requested)throw new InvalidOperationException("Le changement d’accès global n’a pas été confirmé. Actualisez son état avant de réessayer.");
                }finally{enabled.IsEnabled=true;}
            };
            return dialog;
        }
    }
}
