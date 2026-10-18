using System;
using System.Linq;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        internal static void InstanceResourcePublishAcceptance(Action<bool,string> assert)
        {
            var site=new InstanceResource{Kind="Site",Field="project_id",CanModify=true};
            var page=new InstanceResource{Kind="Page",Field="page_id",CanModify=true};
            var readSite=new InstanceResource{Kind="Site",Field="project_id",CanModify=false};
            assert(InstanceResourcesView.PermissionChoices(site).SequenceEqual(new[]{"Lecture","Lecture et modification","Lecture, modification et publication"}),"Site : droit de publication explicite distinct de modification");
            assert(InstanceResourcesView.PermissionChoices(page).Length==2&&InstanceResourcesView.PermissionChoices(readSite).Length==1,"Publication absente des Pages et Sites en lecture seule");
            assert(InstanceResourcesView.PermissionIndex(site,"edit")==1&&InstanceResourcesView.PermissionMode(site,1)=="edit"&&InstanceResourcesView.PermissionMode(site,0)=="read","Droits lecture et modification conservés sans élévation");
            assert(InstanceResourcesView.PermissionIndex(site,"publish")==2&&InstanceResourcesView.PermissionMode(site,2)=="publish"&&InstanceResourcesView.PermissionMode(page,2)=="read","Publication restaurée uniquement pour le Site modifiable");
            assert(InstanceResourcesView.SitePermissionDescription(2).Contains("Sauvegarder et publier les versions de ce Site")&&InstanceResourcesView.SitePermissionDescription(1).Contains("métadonnées uniquement"),"Notice distingue publication et simple modification des métadonnées");
        }
    }
}
