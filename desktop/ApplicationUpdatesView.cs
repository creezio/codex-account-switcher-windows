using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class ApplicationUpdatesView : StackPanel
    {
        private readonly DesktopContext context;
        private readonly ShellWindow shell;
        private readonly TextBlock installed=Ui.Text("Version installée : lecture en cours…",14),available=Ui.Text("Version disponible : non vérifiée",13,true),status=Ui.Text("",13),blocked=Ui.Text("",13),entry=Ui.Text("",13,true);
        private readonly CheckBox automatic,startWindows;
        private readonly Button check,download,apply,install,save;
        private ApplicationUpdateStatus current;
        private bool busy,refreshing,initialized,savedAutomatic,savedStartup,handoff;
        private int generation;
        internal bool Dirty=>initialized&&((automatic.IsChecked==true)!=savedAutomatic||(startWindows.IsChecked==true)!=savedStartup);
        internal ApplicationUpdatesView(DesktopContext context,ShellWindow shell)
        {
            this.context=context;this.shell=shell;
            foreach(var item in new[]{installed,available,entry,status,blocked})Children.Add(item);
            AutomationProperties.SetName(installed,"Version installée du Switcher");AutomationProperties.SetName(available,"Version disponible du Switcher");
            AutomationProperties.SetName(status,"État des mises à jour du Switcher");AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);AutomationProperties.SetName(blocked,"Installation du Switcher en attente");
            var actions=Ui.Actions(this);
            check=ActionButton("Vérifier les mises à jour",()=>Run(context.CheckApplicationUpdate,"Vérification des publications…"));actions.Children.Add(check);
            download=ActionButton("Télécharger la mise à jour",()=>Run(context.DownloadApplicationUpdate,"Téléchargement et vérification…"));actions.Children.Add(download);
            apply=ActionButton("Installer et redémarrer",Apply,true);actions.Children.Add(apply);
            install=ActionButton("Installer le Switcher sur ce PC",()=>Run(context.InstallApplication,"Installation de l’entrée permanente…"));Children.Add(install);
            automatic=Ui.Check("Mettre le Switcher à jour automatiquement",false,this);
            startWindows=Ui.Check("Lancer le Switcher au démarrage de Windows",false,this);
            Children.Add(Ui.Text("Les mises à jour automatiques attendent la fin des tâches du relais avant de redémarrer le Switcher.",12,true));
            save=ActionButton("Enregistrer ces options",SaveOptions);Children.Add(save);
            automatic.Checked+=delegate{UpdateButtons();};automatic.Unchecked+=delegate{UpdateButtons();};startWindows.Checked+=delegate{UpdateButtons();};startWindows.Unchecked+=delegate{UpdateButtons();};
            UpdateButtons();
        }
        private Button ActionButton(string caption,Func<Task> action,bool primary=false)
        {
            Button button=null;button=Ui.Button(caption,async()=>{if(!button.IsEnabled)return;try{await action();}catch(Exception error){ShowError(error);}finally{UpdateButtons();}},primary);return button;
        }
        private void ShowError(Exception error){status.Text=Program.SafeError(error);}
        private void UpdateButtons()
        {
            bool active=busy||handoff||current?.Phase=="checking"||current?.Phase=="downloading"||current?.Phase=="applying";
            check.IsEnabled=!active;download.IsEnabled=initialized&&!active&&current.CanDownload;apply.IsEnabled=initialized&&!active&&current.CanApply;
            install.IsEnabled=initialized&&!active;install.Visibility=current?.StableInstalled==true?Visibility.Collapsed:Visibility.Visible;
            automatic.IsEnabled=initialized&&!active&&current.StableInstalled;startWindows.IsEnabled=automatic.IsEnabled;save.IsEnabled=initialized&&!active&&Dirty;
        }
        private void Show(ApplicationUpdateStatus value,bool savedOptions=false)
        {
            if(value==null)throw new InvalidOperationException("L’état des mises à jour est indisponible. Réessayez sa lecture.");
            bool preserveOptions=Dirty&&!savedOptions;
            current=value;
            installed.Text="Version installée : "+(String.IsNullOrWhiteSpace(value.InstalledVersion)?"non communiquée":value.InstalledVersion);
            available.Text="Version disponible : "+(String.IsNullOrWhiteSpace(value.AvailableVersion)?"non annoncée":value.AvailableVersion);
            entry.Text=value.StableInstalled?"L’entrée permanente du Switcher est installée. Votre raccourci ouvrira la version courante.":"Installez l’entrée permanente pour ouvrir le Switcher sans rechercher son exécutable et activer les options ci-dessous.";
            status.Text=value.Message??"";blocked.Text=value.BusyReason??"";blocked.Visibility=String.IsNullOrWhiteSpace(blocked.Text)?Visibility.Collapsed:Visibility.Visible;
            if(!preserveOptions){savedAutomatic=value.AutoUpdate;savedStartup=value.StartWithWindows;automatic.IsChecked=value.AutoUpdate;startWindows.IsChecked=value.StartWithWindows;}
            initialized=true;UpdateButtons();
        }
        internal async Task Refresh()
        {
            if(busy||handoff||refreshing)return;
            refreshing=true;int turn=generation;
            try{var value=await Task.Run(context.ApplicationUpdateStatus);if(!busy&&turn==generation)Show(value);}
            catch(Exception error){if(!busy&&turn==generation)ShowError(error);}
            finally{refreshing=false;UpdateButtons();}
        }
        private async Task Run(Func<Task<ApplicationUpdateStatus>> operation,string message,bool savedOptions=false)
        {
            if(busy)return;
            busy=true;generation++;status.Text=message;UpdateButtons();
            try{Show(await operation(),savedOptions);}
            finally{busy=false;UpdateButtons();}
        }
        internal async Task SaveOptions()
        {
            if(!Dirty)return;
            if(busy)throw new InvalidOperationException("Attendez la fin de l’opération de mise à jour avant d’enregistrer ces options.");
            bool automaticChoice=automatic.IsChecked==true,startupChoice=startWindows.IsChecked==true;
            await Run(async()=>{await Task.Run(()=>context.SaveApplicationUpdatePreferences(automaticChoice,startupChoice));return await Task.Run(context.ApplicationUpdateStatus);},"Enregistrement des options de l’application…",true);
        }
        private async Task Apply()
        {
            if(busy||current?.CanApply!=true)return;
            busy=true;generation++;status.Text="Préparation de l’installation…";UpdateButtons();
            try{if(await shell.ApplyApplicationUpdate()){handoff=true;status.Text="Le Switcher redémarre pour terminer la mise à jour.";}else Show(await Task.Run(context.ApplicationUpdateStatus));}
            finally{busy=false;UpdateButtons();}
        }
    }
}
