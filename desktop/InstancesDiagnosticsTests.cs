using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task InstanceDiagnosticsAcceptance(DesktopContext context,ShellWindow shell,InstancesPage instances,DesktopInstance owner,DesktopInstance client,Action<bool,string> assert)
        {
            // This account and profile exist only in the already isolated UI fixture.
            DesktopInstance added=null;
            await context.Mutate(accounts=>{
                var profile=accounts.Import(FakeAuth(99),"Nouveau compte OAuth fictif");
                added=accounts.Instances.Create("Instance OAuth ajoutée depuis le portail");
                accounts.Instances.BindAccount(added,profile);return Task.CompletedTask;
            });
            await instances.Open(owner.Id,true);shell.UpdateLayout();
            var resources=Descendants(instances).OfType<InstanceResourcesView>().Single();
            var choice=Descendants(resources).OfType<CheckBox>().Single(c=>AutomationProperties.GetName(c)=="Partager Feuille de route produit");
            choice.IsChecked=true;
            assert(resources.Dirty,"diagnostic : brouillon d’accès préparé avant l’indisponibilité Windows");
            string unavailable=client.Id;bool recoveredInspection=false;
            const string refusedMessage="État du relais à vérifier : Windows n'autorise pas la lecture des métadonnées d'un processus. L'installation sur disque est conservée ; aucun moteur actif n'est confirmé.";
            context.InstanceIntegrationFixture=instance=>{
                if(instance.Id==unavailable)throw new Win32Exception(5,"Inspection fictive refusée");
                if(recoveredInspection&&instance.Id==client.Id)return new RelayIntegrationState{Healthy=true,InstalledVersion=RelayWorker.Version,RuntimeStatus="unknown",RuntimeMessage=refusedMessage};
                return new RelayIntegrationState{Healthy=true,RuntimeStatus="pending"};
            };
            TextBlock Warning()=>Descendants(instances).OfType<TextBlock>().Single(text=>AutomationProperties.GetName(text)=="Diagnostic de l’instance");
            var vaultBefore=File.ReadAllBytes(Path.Combine(context.Root,"accounts.dpapi"));
            try{
                await instances.Refresh();shell.UpdateLayout();
                var rows=instances.List.Items.Cast<ItemRow>().ToArray();
                assert(rows.Length>=4&&new[]{"local",owner.Id,client.Id,added.Id}.All(id=>rows.Any(row=>row.Id==id)),"diagnostic : refus Win32 au milieu de l’inventaire conserve toutes les instances, y compris la nouvelle instance OAuth");
                assert(rows.Single(row=>row.Id==client.Id).State.Contains("Diagnostic à vérifier")&&!rows.Single(row=>row.Id==owner.Id).State.Contains("Diagnostic à vérifier"),"diagnostic : état à vérifier limité à l’instance dont l’inspection a été refusée");
                assert(((ItemRow)instances.List.SelectedItem).Id==owner.Id&&resources.Dirty&&ReferenceEquals(resources,Descendants(instances).OfType<InstanceResourcesView>().Single()),"diagnostic : actualisation partielle conserve sélection, vue des ressources et brouillon");
                assert(Warning().Visibility==Visibility.Collapsed,"diagnostic : avertissement d’une autre instance absent de la fiche sélectionnée");

                unavailable=owner.Id;await instances.Refresh();shell.UpdateLayout();
                assert(Warning().Visibility==Visibility.Visible&&Warning().Text.Contains("Windows a refusé")&&Warning().Text.Contains("n’est pas confirmé"),"diagnostic : refus de la fiche sélectionnée expliqué sans faux chargement confirmé");
                assert(resources.Dirty&&ReferenceEquals(resources,Descendants(instances).OfType<InstanceResourcesView>().Single()),"diagnostic : avertissement de la fiche active ne remplace pas le brouillon");

                unavailable=client.Id;resources.ConfirmDiscard=()=>false;
                await instances.Open(client.Id,false);shell.UpdateLayout();
                assert(((ItemRow)instances.List.SelectedItem).Id==owner.Id&&resources.Dirty,"diagnostic : instance à vérifier ne contourne pas la protection d’un brouillon");
                resources.ConfirmDiscard=()=>true;
                await instances.Open(client.Id,false);instances.ShowTab(0);shell.UpdateLayout();
                assert(((ItemRow)instances.List.SelectedItem).Id==client.Id&&Warning().Visibility==Visibility.Visible,"diagnostic : instance à vérifier reste sélectionnable après abandon explicite du brouillon");
                assert(!Descendants(instances).OfType<TextBlock>().Any(text=>text.Text=="Relais actif confirmé pour les connexions gérées"),"diagnostic : inspection refusée ne produit aucune confirmation du relais");

                await instances.Open(added.Id,false);instances.ShowTab(0);shell.UpdateLayout();
                assert(((ItemRow)instances.List.SelectedItem).Id==added.Id&&Warning().Visibility==Visibility.Collapsed&&Descendants(instances).OfType<TextBlock>().Any(text=>text.Text=="Nouveau compte OAuth fictif"),"diagnostic : nouvelle instance OAuth et son compte restent accessibles malgré le refus sur une autre ligne");
                unavailable=null;recoveredInspection=true;await instances.Open(client.Id,false);instances.ShowTab(0);shell.UpdateLayout();
                assert(Descendants(instances).OfType<TextBlock>().Any(text=>text.Text==refusedMessage)&&Descendants(instances).OfType<TextBlock>().Any(text=>text.Text=="Installation du relais vérifiée sur disque"),"diagnostic : résultat core unknown conserve la preuve disque et affiche le refus Windows, même sans exception UI");
                assert(!Descendants(instances).OfType<TextBlock>().Any(text=>text.Text.Contains("version active")||text.Text=="Relais actif confirmé pour les connexions gérées"),"diagnostic : preuve disque conservée sans inventer de version active confirmée");
                assert(IntegrationEditor.RuntimeDetail(new RelayIntegrationState{Healthy=true,RuntimeStatus="unknown"}).Contains("ancienne version")&&IntegrationEditor.RuntimeDetail(new RelayIntegrationState{Healthy=true,RuntimeStatus="reload-required",RuntimeMessage="Message de recette"}).Contains("ancienne version"),"diagnostic : repli des anciens reçus sans message et premier rechargement conservés");
                recoveredInspection=false;await instances.Refresh();shell.UpdateLayout();
                assert(((ItemRow)instances.List.SelectedItem).Id==client.Id&&Warning().Visibility==Visibility.Collapsed&&!instances.List.Items.Cast<ItemRow>().Single(row=>row.Id==client.Id).State.Contains("Diagnostic à vérifier"),"diagnostic : reprise de l’inspection retire l’avertissement sans changer la sélection");
                assert(vaultBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(context.Root,"accounts.dpapi"))),"diagnostic : refus, sélections et reprise ne réécrivent aucun compte ni association");
            }finally{
                context.InstanceIntegrationFixture=null;
                await context.Mutate(accounts=>{
                    if(accounts.Data.Instances.Any(instance=>instance.Id!=added.Id&&instance.AccountKey==added.AccountKey))throw new InvalidOperationException("Le compte fictif du diagnostic est lié à une autre instance de recette ; nettoyage interrompu.");
                    accounts.Data.Instances.RemoveAll(instance=>instance.Id==added.Id);
                    accounts.Data.Profiles.RemoveAll(profile=>profile.Key==added.AccountKey);
                    accounts.Save();return Task.CompletedTask;
                });
                await instances.Open(owner.Id,true);
            }
        }
    }
}
