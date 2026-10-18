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
        private static async Task DelegationStartupAcceptance(ShellWindow shell, string root, Action<bool, string> assert)
        {
            var isolated = new DesktopContext(Path.Combine(root, "cloud-monitor-startup"), true);
            int cloudStarts = 0, localStarts = 0;
            isolated.EnsureCloudWorkerFixture = () => cloudStarts++;
            isolated.EnsureLocalReturnWorkerFixture = () => localStarts++;
            await isolated.RestoreCloudMonitoring();
            assert(cloudStarts == 0 && localStarts == 0, "démarrage sans mission Cloud ne lance aucun moteur de suivi");

            string id = "f9e2a1d4c6784310b5e8f12345678901";
            var job = new CloudJob { SchemaVersion = 1, Id = id, Title = "Mission Cloud seule après redémarrage", State = "running", ThreadId = "cloud-native-fixture", AccountKey = "worker-fixture", ReturnState = "waiting", ReturnToSource = true, SourceChannel = "fixture-source", SourceThread = "fixture-source-chat" };
            isolated.Store.WriteRecord("cloud-job-" + id + ".dpapi", job);
            await isolated.RestoreCloudMonitoring();
            assert(cloudStarts == 1 && localStarts == 0, "reprise avec seulement une mission Cloud durable restaure son suivi sans instance ouverte");
            job.State = "completed"; job.ReturnState = "pending";
            isolated.Store.WriteRecord("cloud-job-" + id + ".dpapi", job);
            await isolated.RestoreCloudMonitoring();
            assert(cloudStarts == 2 && localStarts == 0, "résultat Cloud en attente suffit à rétablir la notification au démarrage");
            job.ReturnState = "delivered";
            isolated.Store.WriteRecord("cloud-job-" + id + ".dpapi", job);
            await isolated.RestoreCloudMonitoring();
            assert(cloudStarts == 2, "notification déjà remise ne réveille pas le moteur après redémarrage");

            assert(shell.Navigation.Items.Cast<string>().Contains("Délégations"), "dashboard des délégations visible dans la navigation courante sans réglages avancés");
            shell.Navigate("Délégations");
            var page = (CloudDelegationsPage)shell.Pages["Délégations"];
            await Until(() => page.Dashboard.IsLoaded && shell.CurrentPage == "Délégations");
            await page.Refresh();
            assert(shell.CurrentPage == "Délégations" && Object.Equals(shell.Navigation.SelectedItem, "Délégations") && page.Dashboard.IsLoaded, "navigation Délégations affiche directement le dashboard et sélectionne la bonne entrée");
            shell.Navigate("Tâches");
            var launch = Descendants(shell.Pages["Tâches"]).OfType<Button>().Single(button => Object.Equals(button.Content, "Missions Cloud"));
            launch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => shell.CurrentPage == "Délégations");
            assert(!Application.Current.Windows.OfType<CloudJobsWindow>().Any(), "accès depuis Tâches réutilise le dashboard dans la fenêtre principale sans dialogue supplémentaire");
            shell.Navigate("Comptes");
        }
    }
}
