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
        private static async Task PerformanceAcceptance(DesktopContext context,ShellWindow shell,string target,string root,Action<bool,string> assert)
        {
            var page=(PerformancePage)shell.Pages["Performances"];int samples=0,activities=0,closed=0;string activity="busy";bool running=true;long identity=DateTime.UtcNow.AddHours(-2).Ticks;
            ResourceProcess Process(int pid,string name,int parent,long memory)=>new ResourceProcess{Pid=pid,Parent=parent,Started=identity,Name=name,Role=ProcessResources.Role(name,null),Resident=memory,PrivateBytes=memory+1024*1024,CpuPercent=1.5,Association="Descendant vérifié"};
            context.ResourceSnapshotFixture=()=>{
                samples++;var main=new ResourceScope{Id="local",Name="Mon compte principal",Account="principal@example.com",Primary=true,State=new InstanceState{Running=true,DesktopPid=100,DesktopStartTicks=identity,Phase="running"}};
                main.State.WindowReady=true;var other=new ResourceScope{Id=target,Name="Atelier",Account="atelier@example.com",Managed=true,State=new InstanceState{Running=running,WindowReady=running,DesktopPid=200,DesktopStartTicks=identity,Phase=running?"running":"stopped"}};
                var processes=running?new[]{Process(200,"ChatGPT.exe",10,600L*1024*1024),Process(201,"node.exe",200,200L*1024*1024)}:new ResourceProcess[0];
                return new ResourceSnapshot{Captured=DateTime.UtcNow,TotalPhysical=16L*1024*1024*1024,AvailablePhysical=1024L*1024*1024,CollectionMilliseconds=8,Groups=new[]{new ResourceGroup{Scope=main,Processes=new[]{Process(100,"ChatGPT.exe",0,1024L*1024*1024)},Resident=1024L*1024*1024,PrivateBytes=1100L*1024*1024,Cpu=3},new ResourceGroup{Scope=other,Processes=processes,Resident=processes.Sum(p=>p.Resident??0),PrivateBytes=processes.Sum(p=>p.PrivateBytes??0),Cpu=running?(double?)2:null}},Unassigned=new[]{Process(999,"codex.exe",444,20L*1024*1024)}};
            };
            context.ResourceActivityFixture=id=>{activities++;return Task.FromResult(new ResourceActivity{State=activity,Message=activity=="busy"?"1 chat actif":activity=="visible_idle"?"Chats visibles au repos":"Activité inconnue",Titles=activity=="busy"?new[]{"Mission de test"}:new string[0],Checked=DateTime.UtcNow});};
            var lifecycle=context.InstanceLifecycleFixture;context.InstanceLifecycleFixture=(id,restart,expected)=>{closed++;assert(id==target&&expected.DesktopPid==200,"tableau de bord ferme uniquement l’identité sélectionnée");running=false;return Task.FromResult("Instance fermée : Atelier");};
            Button Action(string text)=>Descendants(page).OfType<Button>().Single(b=>Equals(b.Content,text));
            async Task Check(){int before=activities;Action("Vérifier l’activité").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>activities>before&&Action("Vérifier l’activité").IsEnabled);}
            try{
                shell.Navigate("Performances");await page.RefreshNow();shell.UpdateLayout();
                assert(shell.Navigation.Items.Contains("Performances")&&page.Instances.Items.Count==3,"performances accessible sans réglages avancés, origines inconnues séparées");
                page.Instances.SelectedItem=page.Instances.Items.Cast<ResourceRow>().Single(r=>r.Id=="local");assert(!Action("Fermer l’instance").IsEnabled&&!Action("Redémarrer").IsEnabled,"instance principale protégée dans le tableau de bord");
                page.Instances.SelectedItem=page.Instances.Items.Cast<ResourceRow>().Single(r=>r.Id=="unassigned");assert(!Action("Fermer l’instance").IsEnabled&&!Action("Afficher la fenêtre").IsEnabled,"processus non attribués jamais proposés à l’arrêt");
                page.Instances.SelectedItem=page.Instances.Items.Cast<ResourceRow>().Single(r=>r.Id==target);await Check();
                assert(page.Processes.Items.Count==2&&!Action("Fermer l’instance").IsEnabled&&!Action("Redémarrer").IsEnabled&&Descendants(page).OfType<TextBlock>().Any(t=>t.Text.Contains("Mission de test")),"activité réelle affichée, fermeture bloquée pendant un chat");
                Capture(shell,Path.Combine(root,"Performances.png"));var selected=page.Instances.SelectedItem;page.Processes.SelectedIndex=1;var process=page.Processes.SelectedItem;await page.RefreshNow();
                assert(ReferenceEquals(selected,page.Instances.SelectedItem)&&ReferenceEquals(process,page.Processes.SelectedItem),"actualisation conserve l’instance et le processus sélectionnés");
                page.Pause.IsChecked=true;int paused=samples;await page.Refresh();assert(samples==paused,"pause arrête les mesures automatiques");await page.RefreshNow();assert(samples==paused+1,"actualisation manuelle disponible pendant la pause");
                page.Interval.SelectedIndex=2;assert(context.Store.ReadRecord<PerformanceSettings>("performance-settings.dpapi").Interval==30,"fréquence de mesure persistante");
                activity="unknown";identity+=TimeSpan.TicksPerMinute;await page.RefreshNow();assert(!page.Processes.Items.Contains(process)&&ReferenceEquals(selected,page.Instances.SelectedItem),"PID réutilisé reçoit une nouvelle ligne et conserve la sélection d’instance");
                assert(page.Instances.Items.Cast<ResourceRow>().Single(r=>r.Id==target).Activity=="Activité inconnue","une nouvelle identité ne réutilise pas l’activité de l’ancien lancement");
                activity="unknown";await Check();page.ConfirmAction=message=>true;Action("Fermer l’instance").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>Action("Fermer l’instance").IsEnabled);assert(closed==0&&Descendants(page).OfType<TextBlock>().Any(t=>t.Text.Contains("pas vérifiable")),"contrôle complet refuse une activité inconnue sans fermer");
                activity="visible_idle";await Check();page.ConfirmAction=message=>false;Action("Fermer l’instance").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));assert(closed==0,"annuler la fermeture conserve l’instance");
                shell.Theme("Sombre");shell.UpdateLayout();Capture(shell,Path.Combine(root,"Performances-dark.png"));shell.Theme("Clair");double width=shell.Width,height=shell.Height;shell.Width=800;shell.Height=560;shell.UpdateLayout();Capture(shell,Path.Combine(root,"Performances-compact.png"));assert(Descendants(page).OfType<ScrollViewer>().Any(s=>s.ScrollableHeight>0),"tableau de bord accessible par défilement en petite fenêtre");shell.Width=width;shell.Height=height;
                page.ConfirmAction=message=>true;Action("Fermer l’instance").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>closed==1&&!Action("Fermer l’instance").IsEnabled);await page.RefreshNow();assert(page.Instances.Items.Cast<ResourceRow>().Single(r=>r.Id==target).Count==0&&Action("Afficher la fenêtre").IsEnabled,"fermeture contrôlée met les compteurs à jour et permet la réouverture");
            }finally{context.InstanceLifecycleFixture=lifecycle;context.ResourceSnapshotFixture=()=>new ResourceSnapshot{Captured=DateTime.UtcNow,Groups=new ResourceGroup[0],Unassigned=new ResourceProcess[0]};context.ResourceActivityFixture=null;page.Pause.IsChecked=false;page.Interval.SelectedIndex=0;}
        }
    }
}
