using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task StorageAcceptance(DesktopContext context,ShellWindow shell,string root,Action<bool,string> assert)
        {
            var page=(StoragePage)shell.Pages["Stockage"];
            assert(page.Scopes.ItemTemplate!=null&&String.IsNullOrEmpty(page.Scopes.DisplayMemberPath)&&page.CompactScopes.ItemTemplate==null&&page.CompactScopes.DisplayMemberPath=="Name","sélecteurs Stockage utilisent exclusivement un template ou DisplayMemberPath selon leur disposition WPF");
            var scopes=new[]{new StorageScope{Id="instance-demo",Name="Instance de recette",Path=Path.Combine(root,"storage-fixture","codex-home"),Kind="instance",Protected=true},new StorageScope{Id="project-demo",Name="Projet de recette",Path=Path.Combine(root,"storage-fixture","project"),Kind="project",Protected=true},new StorageScope{Id="runtime-demo",Name="Temporaires du Switcher",Path=Path.Combine(root,"storage-fixture","runtime"),Kind="switcher"},new StorageScope{Id="system-demo",Name="Mémoire virtuelle Windows",Path=Path.Combine(root,"storage-fixture","pagefile.sys"),Kind="system",Protected=true}};
            var history=new StorageEntry{Id="history",ScopeId=scopes[0].Id,Name="Historique des conversations",Path=Path.Combine(scopes[0].Path,"sessions"),Kind="history",Bytes=3L*1024*1024*1024,Files=20,CanClean=false,Reason="Conversations et données conservées"};
            var dependencies=new StorageEntry{Id="dependencies",ScopeId=scopes[1].Id,Name="node_modules",Path=Path.Combine(scopes[1].Path,"node_modules"),Kind="dependencies",Bytes=4L*1024*1024*1024,Files=200,CanClean=false,Reason="Potentiellement régénérable ; aucune suppression automatique de dépendances"};
            var scratch=new StorageEntry{Id="runtime-one",ScopeId=scopes[2].Id,Name="Temporaire vérifié A",Path=Path.Combine(scopes[2].Path,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),Kind="runtime",Bytes=100L*1024*1024,Files=3,CanClean=true,Reason="Marqueur reconnu et processus terminé"};
            var locked=new StorageEntry{Id="runtime-two",ScopeId=scopes[2].Id,Name="Temporaire vérifié B",Path=Path.Combine(scopes[2].Path,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),Kind="runtime",Bytes=80L*1024*1024,Files=2,CanClean=true,Reason="Marqueur reconnu et processus terminé"};
            var system=new StorageEntry{Id="pagefile",ScopeId=scopes[3].Id,Name="pagefile.sys",Path=scopes[3].Path,Kind="data",Bytes=8L*1024*1024*1024,Files=1,CanClean=false,Reason="Fichier système Windows protégé"};
            var measured=new StorageSnapshot{Entries=new[]{history,dependencies,scratch,locked,system},FreeBytes=12L*1024*1024*1024,TotalBytes=200L*1024*1024*1024,CapturedUtc=DateTime.UtcNow.ToString("o")};
            int scans=0,cleans=0;context.StorageScopesFixture=()=>scopes;
            page.ScanFixture=(inventory,token)=>{scans++;assert(inventory.Select(scope=>scope.Id).SequenceEqual(scopes.Select(scope=>scope.Id)),"analyse reçoit uniquement l’inventaire de chemins déclarés");return Task.FromResult(measured);};
            shell.Navigate("Stockage");await page.Refresh();shell.UpdateLayout();
            assert(shell.CurrentPage=="Stockage"&&shell.Navigation.Items.Cast<string>().Contains("Stockage"),"Stockage accessible dans la navigation principale");
            assert(scans==0&&page.Snapshot==null&&!page.PreviewButton.IsEnabled,"ouvrir Stockage ne lance aucune analyse et aucun nettoyage");
            await page.Analyze();shell.UpdateLayout();
            assert(scans==1&&page.Scopes.Items.Count==5&&page.Entries.Items.Count==5,"analyse manuelle restitue instances, projets, Switcher et système");
            assert(Descendants(page).OfType<TextBlock>().Any(text=>text.Text.Contains("20 Gio"))&&Descendants(page).OfType<TextBlock>().Any(text=>text.Text.Contains("logiques")),"seuil disque et distinction des tailles logiques sont explicites");
            StorageEntryRow Row(string id)=>page.Entries.Items.Cast<StorageEntryRow>().Single(row=>row.Id==id);
            Row("history").Chosen=true;Row("dependencies").Chosen=true;Row("pagefile").Chosen=true;
            assert(!Row("history").Chosen&&!Row("dependencies").Chosen&&!Row("pagefile").Chosen&&!page.PreviewButton.IsEnabled,"historiques, dépendances et fichiers système restent impossibles à sélectionner pour nettoyage");
            Row("runtime-one").Chosen=true;Row("runtime-two").Chosen=true;
            assert(page.PreviewButton.IsEnabled&&Row("dependencies").Safety=="À examiner"&&Row("history").Safety=="Protégé","potentiellement régénérable reste distinct de nettoyable vérifié");
            string opened=null;page.OpenFolderFixture=path=>opened=path;page.Entries.SelectedItem=Row("history");page.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));assert(opened==history.Path,"ouvrir un dossier cible uniquement la ligne choisie sans supprimer son contenu");
            var project=page.Scopes.Items.Cast<StorageScopeRow>().Single(row=>row.Id==scopes[1].Id);page.Scopes.SelectedItem=project;page.Search.Text="dépendances";
            assert(page.Entries.Items.Count==1&&((StorageEntryRow)page.Entries.Items[0]).Id==dependencies.Id&&scans==1,"changement d’emplacement et recherche utilisent la mesure existante sans rescanner");
            page.Search.Clear();page.Scopes.SelectedItem=page.Scopes.Items.Cast<StorageScopeRow>().Single(row=>row.Id=="all");
            for(int repeat=0;repeat<8;repeat++)await page.Refresh();assert(scans==1,"actualisation périodique de la page ne relance aucune analyse lourde");
            context.Store.WriteRecord("storage-maintenance.dpapi",new StorageMaintenanceReport{CheckedUtc=DateTime.UtcNow.ToString("o"),FreeBytes=measured.FreeBytes,Results=new[]{new StorageCleanupResult{Path=locked.Path,Code="win32-32",Message="Fichier verrouillé",Removed=false}}});
            var automatic=page.LoadAutomaticReport();assert(automatic.Results.Single().Code=="win32-32"&&page.AutomaticReportSummary.Text.Contains("1 conservé(s)")&&scans==1&&cleans==0,"rapport automatique affiche les résultats enregistrés sans analyse ni nettoyage live");
            var oldWidth=shell.Width;var oldHeight=shell.Height;
            try
            {
                foreach(var size in new[]{new Size(1280,860),new Size(980,620)})
                {
                    shell.Width=size.Width;shell.Height=size.Height;shell.UpdateLayout();await Task.Delay(20);shell.UpdateLayout();
                    bool Inside(FrameworkElement element){var point=element.TranslatePoint(new Point(),shell);return point.X>=0&&point.Y>=0&&point.X+element.ActualWidth<=shell.ActualWidth+1&&point.Y+element.ActualHeight<=shell.ActualHeight+1;}
                    assert(page.Entries.ActualHeight>=65&&Inside(page.PreviewButton)&&Inside(page.AnalyzeButton),"grille et actions Stockage restent visibles à "+size.Width+" × "+size.Height);
                    assert(size.Width==1280?page.Scopes.IsVisible:page.CompactScopes.IsVisible,"sélection des emplacements adaptée à la largeur "+size.Width);Capture(shell,Path.Combine(root,"Stockage-"+size.Width+"x"+size.Height+".png"));
                }
            }
            finally{shell.Width=oldWidth;shell.Height=oldHeight;shell.UpdateLayout();}
            StorageEntry[] previewed=null;page.ConfirmCleanup=entries=>{previewed=entries;return false;};page.CleanFixture=(paths,token)=>{cleans++;return Task.FromResult(new StorageCleanupResult[0]);};await page.CleanSelected();
            assert(previewed.Select(entry=>entry.Id).OrderBy(id=>id).SequenceEqual(new[]{scratch.Id,locked.Id}.OrderBy(id=>id))&&cleans==0,"prévisualisation contient seulement les temporaires cochés et annuler ne lance pas le moteur");
            var preview=new StorageCleanupPreview(shell,previewed);
            _=shell.Dispatcher.BeginInvoke(new Action(()=>{preview.UpdateLayout();assert(Descendants(preview).OfType<ListBox>().Single().Items.Count==2,"confirmation concrète liste chaque chemin approuvé");Capture(preview,Path.Combine(root,"Stockage-preview.png"));Descendants(preview).OfType<Button>().Single(button=>Object.Equals(button.Content,"Annuler")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}),DispatcherPriority.ContextIdle);
            assert(preview.ShowDialog()!=true,"fermer la confirmation de nettoyage ne supprime aucun fichier");
            var held=new TaskCompletionSource<StorageCleanupResult[]>();page.ConfirmCleanup=entries=>true;page.CleanFixture=(paths,token)=>{cleans++;assert(paths.OrderBy(path=>path).SequenceEqual(new[]{scratch.Path,locked.Path}.OrderBy(path=>path)),"moteur reçoit uniquement les chemins effectivement prévisualisés");return held.Task;};
            var cleaning=page.CleanSelected();await Until(()=>cleans==1);assert(!page.CanLeave()&&!page.AnalyzeButton.IsEnabled,"nettoyage confirmé empêche navigation et analyse concurrente");
            held.SetResult(new[]{new StorageCleanupResult{Path=scratch.Path,Removed=true,ReclaimedBytes=scratch.Bytes,Code="removed",Message="Temporaire supprimé"},new StorageCleanupResult{Path=locked.Path,Removed=false,Code="win32-32",Message="Le fichier est utilisé par un autre processus"}});await cleaning;
            assert(Row("runtime-one").Safety=="Supprimé"&&!Row("runtime-one").CanClean&&Row("runtime-two").Reason.Contains("win32-32")&&Row("runtime-two").Reason.Contains("autre processus"),"résultats distinguent suppression et refus avec code Win32 et message réel");
            assert(!page.PreviewButton.IsEnabled&&scans==1&&cleans==1,"résultats ne relancent ni scan lourd ni seconde suppression automatique");
            var previous=page.Snapshot;page.ScanFixture=async(inventory,token)=>{scans++;await Task.Delay(Timeout.Infinite,token);return measured;};var scanning=page.Analyze();await Until(()=>scans==2);page.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await scanning;
            assert(ReferenceEquals(previous,page.Snapshot)&&!page.Busy&&page.CanLeave(),"annuler une analyse conserve la dernière mesure et libère la navigation");
            page.AutoClean.IsChecked=false;page.AutoClean.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));assert(!context.Store.ReadRecord<StorageSettings>("storage-settings.dpapi").AutoCleanOwnedTemps,"réglage de nettoyage automatique désactivable et enregistré");page.AutoClean.IsChecked=true;page.AutoClean.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            context.StorageScopesFixture=null;page.ScanFixture=null;page.CleanFixture=null;
        }
    }
}
