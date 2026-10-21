using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task ToolAccessModeAcceptance(DesktopContext context,ShellWindow shell,DesktopInstance owner,Action<bool,string> assert)
        {
            // The UI policy writer is injected. No real policy, tool call,
            // account, connector, instance or model is changed by this recipe.
            var current=new ToolAccessPolicyState{Enabled=false,Revision="fixture-initial"};
            int reads=0,saves=0;bool fail=false;TaskCompletionSource<ToolAccessPolicyState> pending=null;
            Func<ToolAccessPolicyState> read=()=>{reads++;return new ToolAccessPolicyState{Enabled=current.Enabled,Revision=current.Revision,UpdatedUtc=current.UpdatedUtc};};
            Func<bool,string,Task<ToolAccessPolicyState>> write=async(enabled,revision)=>{
                assert(revision==current.Revision,"Mode global transmet la révision affichée pour vérifier les changements concurrents");saves++;
                if(fail)throw new InvalidOperationException("Le réglage global a changé ailleurs. Actualisez avant d’enregistrer.");
                if(pending!=null)await pending.Task;
                current=new ToolAccessPolicyState{Enabled=enabled,Revision="fixture-"+saves,UpdatedUtc=DateTime.UtcNow.ToString("o")};return read();
            };
            string grantsPath=Path.Combine(context.Store.Root,"tool-grants.dpapi");byte[] grantsBefore=File.ReadAllBytes(grantsPath);
            var panel=new ToolAccessModeView(shell,read,write);
            var window=new Window{Owner=shell,Content=panel,Width=700,Height=420,ShowInTaskbar=false,Opacity=0};
            EditWindow dialog=null;
            CheckBox Toggle(EditWindow form)=>Descendants(form).OfType<CheckBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)==ToolAccessModeView.ToggleLabel);
            try{
                window.Show();window.UpdateLayout();
                assert(!panel.FullAccessEnabled&&Descendants(panel).OfType<TextBlock>().Any(t=>t.Text==ToolAccessModeView.StatusLabel(false))&&saves==0,"Mode personnalisé affiché par défaut sans activation implicite");
                dialog=ToolAccessModeView.CreateDialog(shell,read,write);dialog.Show();dialog.UpdateLayout();
                assert(Toggle(dialog).IsChecked==false&&!dialog.Dirty()&&!dialog.SaveButton.IsEnabled&&saves==0,"Ouvrir le réglage ne modifie aucun accès et attend un changement explicite");
                assert(Descendants(dialog).OfType<TextBlock>().Any(t=>t.Text.Contains("actuelles et futures")&&t.Text.Contains("suppression")),"Dialogue explique les instances futures et toutes les opérations incluses");
                Toggle(dialog).IsChecked=true;pending=new TaskCompletionSource<ToolAccessPolicyState>(TaskCreationOptions.RunContinuationsAsynchronously);
                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(saves==1&&!Toggle(dialog).IsEnabled&&!dialog.Saved&&!current.Enabled,"Activation attend la confirmation d’enregistrement sans modifier son brouillon pendant l’attente");
                pending.SetResult(null);await Until(()=>dialog.Saved);pending=null;dialog=null;
                panel.Refresh();window.UpdateLayout();
                assert(panel.FullAccessEnabled&&Descendants(panel).OfType<TextBlock>().Any(t=>t.Text==ToolAccessModeView.StatusLabel(true)),"État global actif visible après confirmation de l’activation");
                assert(grantsBefore.SequenceEqual(File.ReadAllBytes(grantsPath)),"Activation globale ne réécrit pas les partages personnalisés");

                dialog=ToolAccessModeView.CreateDialog(shell,read,write);dialog.Show();dialog.UpdateLayout();
                assert(Toggle(dialog).IsChecked==true&&!dialog.Dirty(),"Nouvelle ouverture reflète le réglage actif enregistré");
                Toggle(dialog).IsChecked=false;dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>dialog.Saved);dialog=null;
                panel.Refresh();window.UpdateLayout();
                assert(!panel.FullAccessEnabled&&saves==2&&grantsBefore.SequenceEqual(File.ReadAllBytes(grantsPath)),"Désactivation restaure le mode personnalisé sans effacer ses accès");

                dialog=ToolAccessModeView.CreateDialog(shell,read,write);dialog.Show();dialog.UpdateLayout();Toggle(dialog).IsChecked=true;
                dialog.ConfirmDiscard=()=>true;dialog.Close();dialog=null;
                assert(!current.Enabled&&saves==2,"Annuler un réglage non enregistré n’active pas l’accès global");

                fail=true;dialog=ToolAccessModeView.CreateDialog(shell,read,write);dialog.Show();dialog.UpdateLayout();Toggle(dialog).IsChecked=true;
                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>Toggle(dialog).IsEnabled&&dialog.SaveButton.IsEnabled);
                assert(!dialog.Saved&&dialog.Dirty()&&!current.Enabled&&Descendants(dialog).OfType<TextBlock>().Any(t=>t.Text.Contains("changé ailleurs")),"Conflit de révision conserve le brouillon et laisse le mode enregistré inchangé");
                dialog.ConfirmDiscard=()=>true;dialog.Close();dialog=null;

                var resources=new InstanceResourcesView(context,shell,owner);
                try{
                    window.Content=resources;window.Width=1000;window.Height=700;window.UpdateLayout();
                    assert(Descendants(resources).OfType<Button>().Any(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Accès global entre instances…"),"Réglage global accessible directement depuis Ressources et accès");
                    assert(Descendants(resources).OfType<TextBlock>().Any(t=>t.Text==ToolAccessModeView.StatusLabel(ToolAccessPolicy.Read(context.Store).Enabled)),"Ressources et accès affiche l’état réel du mode global");
                }finally{window.Content=null;resources.Dispose();}
                assert(reads>0&&grantsBefore.SequenceEqual(File.ReadAllBytes(grantsPath)),"Toutes les lectures et modifications de recette conservent les partages précédents");
            }finally{
                if(dialog!=null&&!dialog.Saved){dialog.ConfirmDiscard=()=>true;dialog.Close();}
                window.Close();
            }
        }
    }
}
