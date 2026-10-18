using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class RemoteAdministrationWindow
    {
        internal static async Task Open(DesktopContext context,Window owner)
        {
            var state=await context.Read(accounts=>new{Policy=PortalAdministration.Policy(context.Store),Routes=Webhooks.Routes(context.Store).Where(route=>Portal.Config(context.Store,route.Id).Enabled).ToArray()});
            var form=new EditWindow(owner,"Administration depuis votre Site","Enregistrer les accès");
            form.Fields.Children.Add(Ui.Text("Autorisez les administrateurs de votre Site à connecter des comptes et préparer des instances sur ce PC. Les clés de chat ne donnent pas ce droit.",15,true));
            var enabled=Ui.Check("Autoriser la création d’instances depuis le Site",state.Policy.Enabled,form.Fields);
            var origin=Ui.Input("Adresse du Site (origine HTTPS)",state.Policy.SiteOrigin??"",form.Fields);
            form.Fields.Children.Add(Ui.Text("Portail concerné",13,true));
            var routes=new ComboBox{ItemsSource=state.Routes,DisplayMemberPath="Name",SelectedItem=state.Routes.FirstOrDefault(route=>route.Id==state.Policy.RootRouteId)??state.Routes.FirstOrDefault()};
            System.Windows.Automation.AutomationProperties.SetName(routes,"Portail concerné par l’administration");form.Fields.Children.Add(routes);
            var emails=Ui.Input("Comptes ChatGPT administrateurs (séparés par une virgule)",String.Join(", ",state.Policy.AllowedEmails),form.Fields);
            form.Fields.Children.Add(Ui.Text("Le Site doit aussi autoriser ces comptes. Le compte que vous ajoutez à une instance ne devient pas administrateur du Site. Aucun partage de ressources existant n’est ajouté automatiquement.",13,true));
            var status=Ui.Text("La clé d’administration reste distincte de celle des messages. Elle doit être placée uniquement dans les secrets serveur du Site.",13,true);form.Fields.Children.Add(status);
            var key=Ui.Button("Copier la clé serveur existante",()=>{
                try{var current=PortalAdministration.Policy(context.Store);if(String.IsNullOrEmpty(current.Secret)){status.Text="Enregistrez une première configuration pour générer sa clé.";return;}Clipboard.SetText(current.Secret);status.Text="Clé copiée. Secret serveur : SWITCHER_ADMIN_KEY. Ne la mettez pas dans un prompt ni dans du code navigateur.";}catch{status.Text="Impossible de copier cette clé. Réessayez depuis cette fenêtre.";}
            });form.Fields.Children.Add(key);
            form.Fields.Children.Add(Ui.Text("Configuration serveur du Site : PORTAL_ADMIN_ALLOWED_EMAILS, SWITCHER_ADMIN_ROUTE_ID et SWITCHER_ADMIN_KEY. Le PC et son tunnel doivent rester disponibles.",13,true));
            form.Save=async()=>{
                var selected=routes.SelectedItem as WebhookRoute;if(selected==null)throw new InvalidOperationException("Configurez d’abord un portail dans Webhooks.");
                var value=new PortalAdministrationPolicy{Enabled=enabled.IsChecked==true,Revision=state.Policy.Revision,SiteOrigin=origin.Text.Trim(),RootRouteId=selected.Id,AllowedEmails=emails.Text.Split(new[]{',',';','\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(email=>email.Trim()).ToList()};
                await context.Mutate(accounts=>{PortalAdministration.Save(context.Store,value);return Task.CompletedTask;});
            };
            form.ShowDialog();
        }
    }
}
