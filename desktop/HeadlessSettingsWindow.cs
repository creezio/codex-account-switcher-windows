using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class HeadlessSettingsWindow
    {
        internal static string Caption(DesktopInstance instance)
        {return instance.HeadlessEnabled?"Outils et agents disponibles sans fenêtre":"Outils et agents : fenêtre Codex requise";}
        internal static int PermissionIndex(DesktopInstance instance)
        {return instance.HeadlessSandbox=="danger-full-access"&&instance.HeadlessApprovalPolicy=="never"?2:instance.HeadlessSandbox=="read-only"?0:1;}
        internal static async Task Open(DesktopContext context,Window owner,string id)
        {
            var instance=await context.Read(a=>a.Data.Instances.Single(i=>i.Id==id&&!i.Archived));
            var form=new EditWindow(owner,"Fonctionnement sans fenêtre"){Width=650,Height=570,MinHeight=470};
            form.Fields.Children.Add(Ui.Text(instance.Name,20));
            form.Fields.Children.Add(Ui.Text("Le Cloud utilise directement le compte associé. Les outils et agents locaux démarrent un moteur temporaire, sans ouvrir Codex. Les fenêtres déjà ouvertes restent utilisables.",14,true));
            var enabled=Ui.Check("Autoriser les outils et agents locaux sans fenêtre",instance.HeadlessEnabled,form.Fields);
            var permissions=Ui.Select("Permissions des nouvelles missions locales",new[]{"Lecture seule","Modifier les fichiers du dossier de travail","Accès complet au PC, sans confirmation de commande"},new[]{"Lecture seule","Modifier les fichiers du dossier de travail","Accès complet au PC, sans confirmation de commande"}[PermissionIndex(instance)],form.Fields);
            var explanation=Ui.Text("",13,true);form.Fields.Children.Add(explanation);
            Action update=()=>{permissions.IsEnabled=enabled.IsChecked==true;explanation.Text=permissions.SelectedIndex==2?"Les nouvelles missions pourront exécuter des commandes et modifier des fichiers en dehors du dossier de travail. Les règles de délégation et les droits des comptes restent applicables.":"Les confirmations et questions du moteur apparaissent dans Tâches → Interventions. Une demande en attente n’est jamais approuvée automatiquement.";};
            enabled.Checked+=delegate{update();};enabled.Unchecked+=delegate{update();};permissions.SelectionChanged+=delegate{update();};update();
            form.Fields.Children.Add(Ui.Text("La connexion est renouvelée à la demande lorsque cela est possible. Une session révoquée ou une autorisation de plugin expirée demandera une reconnexion.",13,true));
            form.Fields.Children.Add(Ui.Text("Attendez la fin des missions locales avant de modifier ces réglages. Un seul moteur utilise le profil à la fois ; ses ressources se libèrent à la fin du travail.",13,true));
            var status=Ui.Text("",13,true);form.Fields.Children.Add(status);
            form.Fields.Children.Add(Ui.AsyncButton("Vérifier la connexion Cloud",async()=>{status.Text="Vérification auprès du serveur…";status.Text=await context.VerifyHeadlessAccount(id);},e=>status.Text=Program.SafeError(e)));
            int initial=PermissionIndex(instance);bool active=instance.HeadlessEnabled;
            form.Dirty=()=>enabled.IsChecked!=active||permissions.SelectedIndex!=initial;
            form.Save=async()=>{
                int selected=permissions.SelectedIndex;bool allow=enabled.IsChecked==true;
                if(selected<0||selected>2)throw new InvalidOperationException("Choisissez les permissions de travail.");
                await context.Mutate(a=>{
                var current=a.Data.Instances.Single(i=>i.Id==id&&!i.Archived);
                if(current.AccountKey!=instance.AccountKey)throw new InvalidOperationException("Le compte de cette instance a changé. Rouvrez ses réglages.");
                if(!context.Fixture&&HeadlessProcesses.Roots(context.Store,a.Instances.Home(current),current.AccountKey).Length>0)throw new InvalidOperationException("Un moteur utilise cette instance. Attendez sa fin ou arrêtez sa mission depuis Tâches avant de modifier ses réglages.");
                current.HeadlessEnabled=allow;
                current.HeadlessSandbox=new[]{"read-only","workspace-write","danger-full-access"}[selected];
                current.HeadlessApprovalPolicy=selected==2?"never":"on-request";
                a.Vault.Save(a.Data);return Task.CompletedTask;
                });
            };
            form.ShowDialog();
        }
    }
}
