using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class InstanceWizard
    {
        internal static async Task Open(DesktopContext context,ShellWindow shell,string instanceId=null,Func<CancellationToken,Task<string>> loginFixture=null)
        {
            var snapshot=await context.Read(a=>new {Profiles=a.Data.Profiles.ToArray(),Instance=a.Data.Instances.FirstOrDefault(i=>i.Id==instanceId),a.Settings});
            var browsers=await AccountLoginPanel.Catalog(context);
            var form=new EditWindow(shell,"Votre instance Codex","Préparer et ouvrir");
            form.Fields.Children.Add(Ui.Text("Un nom, un compte. Le switcher prépare le plugin et les skills automatiquement.",16,true));
            var name=Ui.Input("Nom à utiliser pour déléguer (ex. Léa, Relecture, Studio)",snapshot.Instance?.Name??"",form.Fields);
            name.MaxLength=70;name.IsReadOnly=instanceId!=null;
            var choices=new ComboBox {ItemsSource=new[]{"Connecter un nouveau compte…"}.Concat(snapshot.Profiles.Select(p=>p.Label+" · "+p.Email)).ToArray(),SelectedIndex=0};
            form.Fields.Children.Add(Ui.Text("Compte permanent",13,true));form.Fields.Children.Add(choices);
            System.Windows.Automation.AutomationProperties.SetName(choices,"Compte permanent");
            form.Fields.Children.Add(Ui.Text("Ce compte restera associé à cette instance. Pour utiliser un autre compte, créez une autre instance.",14,true));
            var connection=new AccountLoginPanel(context,snapshot.Settings,browsers);form.Fields.Children.Add(connection);
            choices.SelectionChanged+=delegate{connection.Visibility=choices.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;};
            var status=Ui.Text("",14,true);form.Fields.Children.Add(status);
            string prepared=instanceId;string connectedKey=null;
            bool bound=snapshot.Instance?.AccountLocked==true;
            form.Save=async delegate {
                // Read WPF controls on their dispatcher before any background query
                // or login await. Keep this submission stable until it completes.
                string instanceName=name.Text.Trim();int selectedAccount=choices.SelectedIndex;
                name.IsReadOnly=true;choices.IsEnabled=false;
                try {
                if(String.IsNullOrWhiteSpace(instanceName))throw new InvalidOperationException("Donnez un nom à votre instance.");
                if(selectedAccount<0||selectedAccount>snapshot.Profiles.Length)throw new InvalidOperationException("Choisissez un compte ou connectez un nouveau compte.");
                if(prepared==null&&await context.Read(a=>a.Data.Instances.Any(i=>!i.Archived&&String.Equals(i.Name,instanceName,StringComparison.CurrentCultureIgnoreCase))))throw new InvalidOperationException("Ce nom existe déjà. Choisissez un autre nom.");
                if(!bound){
                    if(connectedKey==null){
                        if(selectedAccount==0){
                            string auth=await connection.Connect(loginFixture);
                            await context.Mutate(a=>{connectedKey=a.Import(auth,null).Key;return Task.CompletedTask;});
                        }else connectedKey=snapshot.Profiles[selectedAccount-1].Key;
                    }
                    status.Text="Association du compte permanent…";
                    await context.Mutate(a=>{
                        var instance=prepared==null?a.Instances.Create(instanceName):a.Data.Instances.Single(i=>i.Id==prepared);prepared=instance.Id;
                        a.Instances.BindAccount(instance,a.Data.Profiles.Single(p=>p.Key==connectedKey));return Task.CompletedTask;
                    });
                    bound=true;
                    connection.Visibility=Visibility.Collapsed;
                    name.IsReadOnly=true;choices.IsEnabled=false;
                }
                status.Text="Installation et vérification du plugin et des skills…";
                await context.MaintainIntegrations(true,prepared,true);
                if(!context.Fixture){var state=await context.Read(a=>RelayIntegration.Status(context.Store,a.Instances.Home(a.Data.Instances.Single(i=>i.Id==prepared))));if(!state.Healthy)throw new InvalidOperationException("L'instance est enregistrée. "+state.Status+" Réessayez ici ou utilisez Vérifier et réparer dans sa fiche.");}
                status.Text="Ouverture de votre instance…";
                if(!context.Fixture)await context.Mutate(async a=>{var instance=a.Data.Instances.Single(i=>i.Id==prepared);if(!instance.IsLocal&&!a.Instances.Runtime.Probe(instance).Running)await a.Instances.Start(instance,CancellationToken.None);});
                }finally{name.IsReadOnly=prepared!=null;choices.IsEnabled=!bound&&connectedKey==null;}
            };
            form.ShowDialog();
        }
    }
}
