using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class InstanceWizard
    {
        internal static async Task Open(DesktopContext context,ShellWindow shell,string instanceId=null)
        {
            var snapshot=await context.Read(a=>new {Profiles=a.Data.Profiles.ToArray(),Instance=a.Data.Instances.FirstOrDefault(i=>i.Id==instanceId)});
            var form=new EditWindow(shell,"Votre instance Codex","Préparer et ouvrir");
            form.Fields.Children.Add(Ui.Text("Un nom, un compte. Le switcher prépare le plugin et les skills automatiquement.",16,true));
            var name=Ui.Input("Nom à utiliser pour déléguer (ex. Léa, Relecture, Studio)",snapshot.Instance?.Name??"",form.Fields);
            name.MaxLength=70;name.IsReadOnly=instanceId!=null;
            var choices=new ComboBox {ItemsSource=new[]{"Connecter un nouveau compte…"}.Concat(snapshot.Profiles.Select(p=>p.Label+" · "+p.Email)).ToArray(),SelectedIndex=0};
            form.Fields.Children.Add(Ui.Text("Compte permanent",13,true));form.Fields.Children.Add(choices);
            System.Windows.Automation.AutomationProperties.SetName(choices,"Compte permanent");
            form.Fields.Children.Add(Ui.Text("Ce compte restera associé à cette instance. Pour utiliser un autre compte, créez une autre instance.",14,true));
            var status=Ui.Text("",14,true);form.Fields.Children.Add(status);
            CancellationTokenSource login=null;
            var cancel=Ui.Button("Annuler la connexion",()=>login?.Cancel());cancel.Visibility=Visibility.Collapsed;form.Fields.Children.Add(cancel);
            string prepared=instanceId;string connectedKey=null;
            bool bound=snapshot.Instance?.AccountLocked==true;
            form.Save=async delegate {
                if(String.IsNullOrWhiteSpace(name.Text))throw new InvalidOperationException("Donnez un nom à votre instance.");
                if(prepared==null&&await context.Read(a=>a.Data.Instances.Any(i=>!i.Archived&&String.Equals(i.Name,name.Text.Trim(),StringComparison.OrdinalIgnoreCase))))throw new InvalidOperationException("Ce nom existe déjà. Choisissez un autre nom.");
                if(!bound){
                    if(connectedKey==null){
                        if(choices.SelectedIndex==0){
                            login=new CancellationTokenSource(TimeSpan.FromMinutes(5));cancel.Visibility=Visibility.Visible;status.Text="Terminez la connexion dans votre navigateur…";
                            try {
                                var service=await context.Read(a=>a);
                                string auth=await service.LoginAuth(url=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true}),login.Token);
                                await context.Mutate(a=>{connectedKey=a.Import(auth,null).Key;return Task.CompletedTask;});
                            }finally {login.Dispose();login=null;cancel.Visibility=Visibility.Collapsed;}
                        }else connectedKey=snapshot.Profiles[choices.SelectedIndex-1].Key;
                    }
                    status.Text="Association du compte permanent…";
                    await context.Mutate(a=>{
                        var instance=prepared==null?a.Instances.Create(name.Text):a.Data.Instances.Single(i=>i.Id==prepared);prepared=instance.Id;
                        a.Instances.BindAccount(instance,a.Data.Profiles.Single(p=>p.Key==connectedKey));return Task.CompletedTask;
                    });
                    bound=true;
                    name.IsReadOnly=true;choices.IsEnabled=false;
                }
                status.Text="Installation et vérification du plugin et des skills…";
                await context.MaintainIntegrations(true,prepared,true);
                if(!context.Fixture){var state=await context.Read(a=>RelayIntegration.Status(context.Store,a.Instances.Home(a.Data.Instances.Single(i=>i.Id==prepared))));if(!state.Healthy)throw new InvalidOperationException("L'instance est enregistrée. "+state.Status+" Réessayez ici ou utilisez Vérifier et réparer dans sa fiche.");}
                status.Text="Ouverture de votre instance…";
                if(!context.Fixture)await context.Mutate(async a=>{var instance=a.Data.Instances.Single(i=>i.Id==prepared);if(!instance.IsLocal&&!a.Instances.Runtime.Probe(instance).Running)await a.Instances.Start(instance,CancellationToken.None);});
            };
            form.ShowDialog();
        }
    }
}
