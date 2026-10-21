using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class GlobalAccessPage : PageView
    {
        internal readonly CheckBox FullAccess;
        private readonly Func<ToolAccessPolicyState> read;
        private readonly Func<bool,string,Task<ToolAccessPolicyState>> save;
        private ToolAccessPolicyState current;
        private bool saving;
        internal GlobalAccessPage(DesktopContext context,ShellWindow shell)
            :this(context,shell,()=>ToolAccessPolicy.Read(context.Store),(enabled,revision)=>Task.Run(()=>ToolAccessPolicy.Save(context.Store,enabled,revision))){}
        internal GlobalAccessPage(DesktopContext context,ShellWindow shell,Func<ToolAccessPolicyState> read,Func<bool,string,Task<ToolAccessPolicyState>> save)
            :base(context,shell,"Accès global","Un seul réglage pour toute l’application, tous les comptes et toutes les instances.")
        {
            this.read=read;this.save=save;var fields=new StackPanel();
            FullAccess=Ui.Check("Accès complet global",false,fields);
            fields.Children.Add(Ui.Text("Quand cette case est cochée, les partages et leurs restrictions sont ignorés partout dans Switcher. Toutes les instances ont accès à toutes les ressources des autres instances : lecture, écriture, publication et suppression.",15));
            fields.Children.Add(Ui.Text("Cela inclut automatiquement les comptes, instances, plugins et ressources ajoutés ensuite. Aucune sélection par destinataire, ressource ou action n’est nécessaire.",14));
            fields.Children.Add(Ui.Text("Décochez pour rétablir vos anciens partages. Ils restent conservés, mais ne limitent aucun accès pendant le mode global. Les connexions et autorisations propres aux services restent nécessaires.",13,true));
            Body.Children.Add(Ui.Card(fields));
            FullAccess.Click+=async delegate{await Change();};
        }
        public override Task Refresh()
        {
            if(saving)return Task.CompletedTask;
            try{current=read();FullAccess.IsChecked=current.Enabled;FullAccess.IsEnabled=true;Notice.Text=current.Enabled?"Accès complet global actif · aucune restriction de partage Switcher appliquée.":"Mode personnalisé actif · cochez pour ignorer toutes les restrictions de partage.";}
            catch(Exception error){FullAccess.IsEnabled=false;Notice.Text=Program.SafeError(error);}
            return Task.CompletedTask;
        }
        private async Task Change()
        {
            if(saving||current==null)return;
            bool enabled=FullAccess.IsChecked==true;saving=true;FullAccess.IsEnabled=false;Notice.Text="Application du réglage global…";
            try{
                var result=await save(enabled,current.Revision);
                if(result==null||result.Enabled!=enabled)throw new InvalidOperationException("Le réglage global n’a pas été confirmé.");
                current=result;saving=false;await Refresh();
            }catch(Exception error){saving=false;await Refresh();Notice.Text=Program.SafeError(error);}
            finally{saving=false;FullAccess.IsEnabled=current!=null;}
        }
        public override bool CanLeave()=>!saving;
    }
}
