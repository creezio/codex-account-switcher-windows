using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        internal static async Task InstanceResourcePublishAcceptance(DesktopContext context,ShellWindow shell,DesktopInstance owner,DesktopInstance client,Action<bool,string> assert)
        {
            var site=new InstanceResource{Kind="Site",Field="project_id",CanModify=true};
            var page=new InstanceResource{Kind="Page",Field="page_id",CanModify=true};
            var readSite=new InstanceResource{Kind="Site",Field="project_id",CanModify=false};
            assert(InstanceResourcesView.PermissionChoices(site).SequenceEqual(new[]{"Lecture","Lecture et modification","Lecture, modification et publication"}),"Site : droit de publication explicite distinct de modification");
            assert(InstanceResourcesView.PermissionChoices(page).Length==2&&InstanceResourcesView.PermissionChoices(readSite).Length==1,"Publication absente des Pages et Sites en lecture seule");
            assert(InstanceResourcesView.PermissionIndex(site,"edit")==1&&InstanceResourcesView.PermissionMode(site,1)=="edit"&&InstanceResourcesView.PermissionMode(site,0)=="read","Droits lecture et modification conservés sans élévation");
            assert(InstanceResourcesView.PermissionIndex(site,"publish")==2&&InstanceResourcesView.PermissionMode(site,2)=="publish"&&InstanceResourcesView.PermissionMode(page,2)=="read","Publication restaurée uniquement pour le Site modifiable");
            assert(InstanceResourcesView.SitePermissionDescription(2).Contains("Sauvegarder et publier les versions de ce Site")&&InstanceResourcesView.SitePermissionDescription(1).Contains("métadonnées uniquement"),"Notice distingue publication et simple modification des métadonnées");
            await InstanceResourceRefreshAcceptance(context,shell,owner,client,assert);
        }
        private static async Task InstanceResourceRefreshAcceptance(DesktopContext context,ShellWindow shell,DesktopInstance owner,DesktopInstance client,Action<bool,string> assert)
        {
            var accounts=context.Accounts();var service=new InstanceResources(context.Store);
            var baseline=service.Cached(owner.Id,owner.AccountKey,accounts.Instances.Home(owner));
            var originalGrants=new ToolTunnel(context.Store).Grants();
            var view=new InstanceResourcesView(context,shell,owner);
            var window=new Window{Owner=shell,Content=view,Width=1000,Height=700,ShowInTaskbar=false,Opacity=0};
            string Name(DependencyObject element)=>System.Windows.Automation.AutomationProperties.GetName(element);
            CheckBox SelectedSite()=>Descendants(view).OfType<CheckBox>().Single(c=>Name(c)=="Partager Portail client");
            try{
                window.Show();window.UpdateLayout();
                var recipient=Descendants(view).OfType<ComboBox>().Single(c=>Name(c)=="Configurer les accès pour");recipient.SelectedValue=client.Id;window.UpdateLayout();
                SelectedSite().IsChecked=true;window.UpdateLayout();
                Descendants(view).OfType<ComboBox>().Single(c=>Name(c)=="Droits pour Portail client").SelectedIndex=2;
                view.ConfirmDiscard=()=>throw new Exception("An inventory refresh must not discard an access draft.");
                var revisionField=typeof(InstanceResourcesView).GetField("revision",BindingFlags.Instance|BindingFlags.NonPublic);
                string revision=(string)revisionField.GetValue(view);
                var changedGrants=Json.Read<List<TunnelGrant>>(Json.Write(originalGrants));
                changedGrants.Add(new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Instance=owner.Id,CatalogClient=client.Id,Enabled=false,Name="Fixture concurrent edit",Revision="concurrent-fixture"});
                context.Store.WriteRecord("tool-grants.dpapi",changedGrants);
                string grantsBefore=Json.Write(new ToolTunnel(context.Store).Grants());
                var fresh=Json.Read<InstanceInventory>(Json.Write(baseline));fresh.Updated=DateTime.UtcNow.ToString("o");
                fresh.Resources.Add(new InstanceResource{Key="new-site-refresh",Plugin="sites",Kind="Site",Field="project_id",Value="site-new-refresh",Title="Nouveau Site après actualisation",Access="Propriétaire",CanModify=true});
                var pending=new TaskCompletionSource<InstanceInventory>(TaskCreationOptions.RunContinuationsAsynchronously);int refreshes=0;
                view.DiscoveryFixture=token=>{refreshes++;return pending.Task;};
                var refresh=view.Discover(true);window.UpdateLayout();
                assert(refreshes==1&&!recipient.IsEnabled&&!SelectedSite().IsEnabled,"Actualiser démarre avec un brouillon et fige le destinataire pendant la détection");
                view.ConfirmDiscard=()=>false;
                assert(!view.CanLeave()&&view.Dirty,"Navigation pendant une actualisation protège encore le brouillon d’accès");
                view.ConfirmDiscard=()=>throw new Exception("An inventory refresh must not discard an access draft.");
                pending.SetResult(fresh);await refresh;window.UpdateLayout();
                assert(view.Dirty&&SelectedSite().IsChecked==true&&Descendants(view).OfType<ComboBox>().Single(c=>Name(c)=="Droits pour Portail client").SelectedIndex==2,"Actualisation conserve la sélection et le droit de publication non enregistrés");
                assert(Descendants(view).OfType<TextBlock>().Any(t=>t.Text=="Nouveau Site après actualisation")&&recipient.IsEnabled,"Nouveaux Sites visibles après actualisation et destinataire réactivé");
                assert((string)revisionField.GetValue(view)==revision&&Json.Write(new ToolTunnel(context.Store).Grants())==grantsBefore,"Actualisation conserve la révision initiale de concurrence sans écrire les accès");

                view.DiscoveryFixture=token=>Task.FromException<InstanceInventory>(new InvalidOperationException("Erreur fournisseur de recette"));
                await view.Discover(true);window.UpdateLayout();
                assert(view.Dirty&&SelectedSite().IsChecked==true&&Descendants(view).OfType<TextBlock>().Any(t=>t.Text=="Nouveau Site après actualisation"),"Échec d’actualisation conserve catalogue affiché et brouillon sans confirmation d’abandon");

                var incomplete=Json.Read<InstanceInventory>(Json.Write(fresh));
                incomplete.Plugins.Single(p=>p.Key=="sites").State="error";
                incomplete.Plugins.Single(p=>p.Key=="sites").Message="Dernier inventaire conservé : 2 ressources non revérifiées.";
                foreach(var resource in incomplete.Resources.Where(r=>r.Plugin=="sites"))resource.CacheRetained=true;
                view.DiscoveryFixture=token=>Task.FromResult(incomplete);
                await view.Discover(true);window.UpdateLayout();
                assert(view.Dirty&&SelectedSite().IsChecked==true&&!SelectedSite().IsEnabled&&Descendants(view).OfType<TextBlock>().Any(t=>t.Text.StartsWith("Dernier inventaire conservé ·")),"Sites conservés restent visibles avec sélection intacte mais droits non modifiables tant que non revérifiés");
                view.Search.Text="aucun résultat de recette";window.UpdateLayout();
                assert(Descendants(view).OfType<TextBlock>().Any(t=>t.Text.StartsWith("Catalogue incomplet :"))&&!Descendants(view).OfType<TextBlock>().Any(t=>t.Text=="Aucune ressource correspondant à cette recherche."),"Erreur de catalogue n’est pas présentée comme une liste vide confirmée");
                assert(Json.Write(new ToolTunnel(context.Store).Grants())==grantsBefore,"Succès, échec et catalogue conservé ne modifient aucun accès enregistré");
            }finally{view.Dispose();window.Close();context.Store.WriteRecord("tool-grants.dpapi",originalGrants);}
        }
    }
}
