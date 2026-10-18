using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task ApplicationUpdatesAcceptance(string root,Action<bool,string> assert)
        {
            var context=new DesktopContext(Path.Combine(root,"application-updates-data"),true);
            var accounts=new AccountService(context.Root);accounts.Vault.Save(new VaultData());accounts=new AccountService(context.Root);
            accounts.Settings.CodexExecutable=Environment.ProcessPath;accounts.Settings.CodexHome=Path.Combine(context.Root,"home");Directory.CreateDirectory(accounts.Settings.CodexHome);accounts.Vault.SaveSettings(accounts.Settings);
            var value=new ApplicationUpdateStatus{InstalledVersion=RelayWorker.Version,Phase="idle",Message="Recette locale : aucune vérification effectuée."};
            ApplicationUpdateStatus Copy()=>Json.Read<ApplicationUpdateStatus>(Json.Write(value));
            int checks=0,downloads=0,installs=0,saves=0,applies=0;bool denyPreferences=false;
            context.ApplicationUpdateStatusFixture=Copy;
            var checkReply=new TaskCompletionSource<ApplicationUpdateStatus>();
            context.CheckApplicationUpdateFixture=()=>{checks++;return checkReply.Task;};
            context.InstallApplicationFixture=()=>{installs++;value.StableInstalled=true;return Task.FromResult(Copy());};
            context.SaveApplicationUpdatePreferencesFixture=(automatic,startup)=>{saves++;if(denyPreferences)throw new InvalidOperationException("Recette : démarrage Windows refusé.");value.AutoUpdate=automatic;value.StartWithWindows=startup;};
            var downloadReply=new TaskCompletionSource<ApplicationUpdateStatus>();
            context.DownloadApplicationUpdateFixture=()=>{downloads++;return downloadReply.Task;};
            context.ApplicationUpdateApplyFixture=()=>{applies++;value.BusyReason="Recette : une mission vient de démarrer.";value.CanApply=false;return Task.FromResult(false);};
            var shell=new ShellWindow(context){ShowInTaskbar=false,Opacity=0};
            shell.Show();
            var preferences=(PreferencesPage)shell.Pages["Paramètres"];
            try{
                shell.Navigate("Paramètres");await preferences.Refresh();
                await Until(()=>Descendants(preferences).OfType<TextBox>().Any(field=>AutomationProperties.GetName(field)=="Modèle des missions Cloud"));shell.UpdateLayout();
                var panel=Descendants(preferences).OfType<ApplicationUpdatesView>().Single();
                Button Action(string name)=>Descendants(panel).OfType<Button>().Single(button=>AutomationProperties.GetName(button)==name);
                CheckBox Option(string name)=>Descendants(panel).OfType<CheckBox>().Single(box=>AutomationProperties.GetName(box)==name);
                TextBlock State()=>Descendants(panel).OfType<TextBlock>().Single(text=>AutomationProperties.GetName(text)=="État des mises à jour du Switcher");
                CheckBox automatic=Option("Mettre le Switcher à jour automatiquement"),startup=Option("Lancer le Switcher au démarrage de Windows");
                Button check=Action("Vérifier les mises à jour"),download=Action("Télécharger la mise à jour"),apply=Action("Installer et redémarrer"),install=Action("Installer le Switcher sur ce PC"),save=Action("Enregistrer ces options");
                var model=Descendants(preferences).OfType<TextBox>().Single(field=>AutomationProperties.GetName(field)=="Modèle des missions Cloud");string originalModel=model.Text;
                await preferences.Refresh();
                assert(checks==0&&downloads==0&&installs==0&&applies==0,"application : actualisation des Paramètres lit uniquement l’état local, sans réseau ni installation");
                assert(!automatic.IsEnabled&&!startup.IsEnabled&&install.IsVisible&&!download.IsEnabled&&!apply.IsEnabled,"application : portable propose l’entrée permanente avant les options automatiques");

                model.Text="brouillon-local";preferences.ConfirmDiscard=()=>false;
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(checks==1&&!check.IsEnabled&&!download.IsEnabled,"application : double clic de vérification n’admet qu’une opération et montre son attente");
                await preferences.Refresh();
                assert(model.Text=="brouillon-local"&&State().Text.Contains("Vérification")&&!preferences.CanLeave(),"application : vérification et Refresh préservent les autres réglages non enregistrés");
                value.AvailableVersion="0.29.0-beta.1";value.Phase="available";value.Message="Recette : mise à jour disponible.";value.CanDownload=true;checkReply.SetResult(Copy());
                await Until(()=>check.IsEnabled&&download.IsEnabled);
                assert(Descendants(panel).OfType<TextBlock>().Any(text=>text.Text=="Version disponible : 0.29.0-beta.1")&&model.Text=="brouillon-local","application : version disponible affichée sans reconstruire les autres champs");

                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>automatic.IsEnabled);
                assert(installs==1&&startup.IsEnabled&&install.Visibility==Visibility.Collapsed,"application : installation du raccourci stable active les options sans redémarrage fictif");
                automatic.IsChecked=true;startup.IsChecked=true;await preferences.Refresh();
                assert(automatic.IsChecked==true&&startup.IsChecked==true&&panel.Dirty,"application : lecture locale ne remplace pas un brouillon des options Windows");
                denyPreferences=true;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>save.IsEnabled&&State().Text.Contains("refusé"));
                assert(panel.Dirty&&automatic.IsChecked==true&&startup.IsChecked==true&&!value.AutoUpdate&&!value.StartWithWindows,"application : refus d’enregistrement garde les choix et ne promet pas un démarrage Windows actif");
                denyPreferences=false;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>!panel.Dirty&&check.IsEnabled);
                assert(saves==2&&value.AutoUpdate&&value.StartWithWindows&&model.Text=="brouillon-local"&&!preferences.CanLeave(),"application : nouvelle tentative enregistre ces options et conserve les autres brouillons");

                model.Text=originalModel;automatic.IsChecked=false;startup.IsChecked=false;
                var globalSave=Descendants(preferences).OfType<Button>().Single(button=>AutomationProperties.GetName(button)=="Enregistrer les réglages");
                globalSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>globalSave.IsEnabled&&!panel.Dirty);
                assert(saves==3&&!value.AutoUpdate&&!value.StartWithWindows&&preferences.CanLeave(),"application : enregistrement global prend aussi les options de mise à jour et Windows");

                using(var readStarted=new ManualResetEventSlim())using(var releaseRead=new ManualResetEventSlim()){
                    var obsolete=Copy();context.ApplicationUpdateStatusFixture=()=>{readStarted.Set();if(!releaseRead.Wait(TimeSpan.FromSeconds(5)))throw new InvalidOperationException("Lecture de recette non libérée.");return obsolete;};
                    var lateRefresh=preferences.Refresh();
                    assert(await Task.Run(()=>readStarted.Wait(TimeSpan.FromSeconds(5))),"application : lecture locale ancienne retenue pour la régression de concurrence");
                    context.CheckApplicationUpdateFixture=()=>{checks++;value.AvailableVersion="0.29.0-beta.2";return Task.FromResult(Copy());};
                    check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));releaseRead.Set();await lateRefresh;
                    assert(Descendants(panel).OfType<TextBlock>().Any(text=>text.Text=="Version disponible : 0.29.0-beta.2"),"application : ancien état local reçu après une vérification ne remplace pas sa version plus récente");
                    context.ApplicationUpdateStatusFixture=Copy;
                }

                context.CheckApplicationUpdateFixture=()=>{checks++;return Task.FromException<ApplicationUpdateStatus>(new InvalidOperationException("Recette : vérification indisponible."));};
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>check.IsEnabled&&State().Text.Contains("indisponible"));
                assert(download.IsEnabled&&Descendants(panel).OfType<TextBlock>().Any(text=>text.Text=="Version installée : "+RelayWorker.Version),"application : échec de vérification conserve la version installée et permet une nouvelle action explicite");
                download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(downloads==1&&!check.IsEnabled&&!download.IsEnabled&&!apply.IsEnabled,"application : téléchargement admis une seule fois sans application anticipée");
                value.Phase="ready";value.Message="Recette : paquet vérifié prêt.";value.CanDownload=false;value.CanApply=false;value.BusyReason="Recette : mission de relais en cours.";downloadReply.SetResult(Copy());
                await Until(()=>check.IsEnabled&&State().Text.Contains("paquet vérifié"));
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(!apply.IsEnabled&&applies==0&&Descendants(panel).OfType<TextBlock>().Any(text=>text.Text==value.BusyReason),"application : travail du relais bloque le redémarrage et affiche sa raison");
                value.BusyReason=null;value.CanApply=true;model.Text="brouillon-avant-mise-a-jour";await preferences.Refresh();apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(()=>apply.IsEnabled&&State().Text.Contains("paquet vérifié"));
                assert(applies==0&&shell.IsVisible&&model.Text=="brouillon-avant-mise-a-jour","application : refus d’abandon d’un autre réglage interrompt Apply avant le backend et conserve le brouillon");
                model.Text=originalModel;await preferences.Refresh();apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(()=>applies==1&&!apply.IsEnabled&&State().Text.Contains("paquet vérifié"));
                assert(shell.IsVisible&&Descendants(panel).OfType<TextBlock>().Any(text=>text.Text=="Recette : une mission vient de démarrer."),"application : refus backend tardif garde la fenêtre et explique la nouvelle activité");

                value.BusyReason=null;value.CanApply=true;context.ApplicationUpdateApplyFixture=()=>{applies++;return Task.FromResult(true);};await preferences.Refresh();
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>applies==2&&State().Text.Contains("redémarre"));
                assert(shell.IsVisible&&!apply.IsEnabled&&!check.IsEnabled,"application : reçu accepté passe par Shell et attend le redémarrage sans fermer le harness ni rejouer l’application");
            }finally{preferences.ConfirmDiscard=()=>true;shell.ExitForTest();}
        }
    }
}
