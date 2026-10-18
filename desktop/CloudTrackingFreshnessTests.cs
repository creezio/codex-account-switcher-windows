using System;
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
        private static void CloudMonitoringFreshnessAcceptance(string root, Action<bool, string> assert)
        {
            var store = new RelayStore(Path.Combine(root, "monitor-freshness")); string id = new string('f', 32); store.WriteRecord("cloud-monitor-" + id + ".dpapi", new CloudJobMonitorRecord());
            var job = new CloudJob { Id = id, State = "running", Fingerprint = "freshness-fixture", InstanceId = "fixture-instance", AccountKey = "fixture-account", ConfigurationId = "fixture-environment", ThreadId = "fixture-thread", TurnId = "fixture-turn", ReturnToSource = false, ReturnState = "none" };
            var clock = DateTime.Parse("2026-10-16T23:21:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind); var monitor = new CloudJobMonitoring(store, () => clock);
            monitor.Observe(job); monitor.BeginCheck(job); var began = monitor.Read(job);
            assert(began.LastSuccessfulCheckUtc == null && began.LastKnownState == null, "démarrer une tentative ou consigner un état local ne crée aucune lecture Cloud réussie");
            clock = clock.AddSeconds(3); monitor.CheckFinished(job, null); var confirmed = monitor.Read(job); string success = clock.ToUniversalTime().ToString("o");
            assert(confirmed.LastSuccessfulCheckUtc == success && confirmed.LastKnownState == "running", "lecture native réussie fige séparément sa date et l’état confirmé");
            clock = clock.AddMinutes(1); monitor.BeginCheck(job); clock = clock.AddSeconds(4); monitor.CheckFinished(job, "Lecture native indisponible"); var failed = monitor.Read(job);
            assert(failed.LastCheckedUtc != confirmed.LastCheckedUtc && failed.LastSuccessfulCheckUtc == success && failed.LastKnownState == "running" && failed.LastCheckError != null && failed.NextCheckUtc != null, "échec ultérieur avance seulement la tentative et conserve le dernier état certain avec reprise planifiée");
            job.State = "uncertain"; monitor.Observe(job); monitor.BeginSourceCheck(job); monitor.SourceCheckError(job, "Source indisponible"); var local = monitor.Read(job);
            assert(local.LastSuccessfulCheckUtc == success && local.LastKnownState == "running", "transition locale et vérification du retour ne rafraîchissent pas une preuve d’activité Cloud");
            job.State = "completed"; clock = clock.AddMinutes(1); monitor.BeginCheck(job); monitor.CheckFinished(job, null); var recovered = monitor.Read(job);
            assert(recovered.LastKnownState == "completed" && recovered.LastSuccessfulCheckUtc == clock.ToUniversalTime().ToString("o") && recovered.LastCheckError == null && recovered.Events.Count(item => item.Kind == "check-recovered") == 1, "récupération native remplace la preuve par le nouvel état et efface seulement l’erreur de lecture");
            var legacy = store.ReadRecord<CloudJobMonitorRecord>("cloud-monitor-" + id + ".dpapi"); legacy.LastKnownState = null; legacy.LastSuccessfulCheckUtc = null; store.WriteRecord("cloud-monitor-" + id + ".dpapi", legacy); string path = Path.Combine(store.Root, "cloud-monitor-" + id + ".dpapi"); DateTime saved = File.GetLastWriteTimeUtc(path); var old = monitor.Read(job);
            assert(old.LastSuccessfulCheckUtc == null && old.LastKnownState == null && File.GetLastWriteTimeUtc(path) == saved, "ancien journal conserve ses dates inconnues sans inventer une réussite ni réécrire le sidecar à la lecture");
        }

        private static async Task CloudDashboardReadFreshness(DesktopContext context, ShellWindow shell, DesktopInstance owner, string root, Action<bool, string> assert)
        {
            var clock = DateTimeOffset.Parse("2026-10-16T23:45:00Z"); string known = "2026-10-16T23:21:00Z";
            var stale = new CloudJob { Id = new string('a', 32), Title = "Recette · lecture Cloud indisponible", InstanceId = owner.Id, AccountKey = owner.AccountKey, State = "running", ThreadId = "native-stale", TurnId = "turn-stale", CreatedUtc = "2026-10-16T23:00:00Z", UpdatedUtc = "2026-10-16T23:44:59Z", ReturnState = "waiting" };
            var uncertain = new CloudJob { Id = new string('b', 32), Title = "Recette · envoi non confirmé", InstanceId = owner.Id, AccountKey = owner.AccountKey, State = "uncertain", ThreadId = "native-idle", CreatedUtc = "2026-10-16T23:10:00Z", ReturnState = "none" };
            var delivered = new CloudJob { Id = new string('c', 32), Title = "Recette · résultat notifié", InstanceId = owner.Id, AccountKey = owner.AccountKey, State = "completed", ThreadId = "native-complete", TurnId = "turn-complete", CreatedUtc = "2026-10-16T23:15:00Z", ReturnState = "delivered" };
            var read = new CloudJobMonitorSnapshot { LastCheckedUtc = "2026-10-16T23:44:00Z", LastSuccessfulCheckUtc = known, LastKnownState = "running", LastCheckError = "Lecture native indisponible [phase=read-thread]", NextCheckUtc = "2026-10-16T23:45:00Z", NotificationState = "waiting", Events = new[] { new CloudJobMonitorEvent { TimeUtc = known, Kind = "state", Message = "État de la mission : running." } } };
            var window = new CloudJobsWindow(context, shell) { Opacity = 0, ShowInTaskbar = false, Width = 1100, Height = 760, JobsFixture = () => Task.FromResult(new[] { stale, uncertain, delivered }), MonitoringFixture = job => job.Id == stale.Id ? read : new CloudJobMonitorSnapshot { LastKnownState = job.State, LastSuccessfulCheckUtc = known, NotificationState = job.Id == delivered.Id ? "delivered" : "none", NotificationSentUtc = job.Id == delivered.Id ? "2026-10-16T23:40:00Z" : null, Events = new CloudJobMonitorEvent[0] } };
            window.Dashboard.ClockFixture = () => clock;
            TextBlock Label(string name) => Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetName(text) == name);
            try
            {
                window.Show(); await Until(() => window.JobsList.Items.Count == 3); window.Dashboard.Search.Text = stale.Title; window.UpdateLayout();
                assert(Label("En cours").Text == "0" && Label("À traiter").Text == "2" && Label("Missions notifiées").Text == "1", "erreur de suivi et envoi incertain sortent du compteur En cours tandis qu’une notification reste une notification");
                var tracking = Label("État réel du suivi Cloud"); assert(Label("État observé de la mission Cloud").Text.StartsWith("Erreur de suivi") && tracking.Text.Contains(CloudMissionPresentation.AbsoluteTime(known)) && tracking.Text.Contains("Il y a 24 min") && tracking.Text.Contains("60 secondes") && !tracking.Text.Contains("23:44:59"), "fiche montre le dernier état confirmé avec sa date et son âge sans utiliser la dernière sauvegarde comme activité");
                clock = clock.AddMinutes(2); await window.RefreshNow(); assert(tracking.Text.Contains("Il y a 26 min") && window.SelectedJobId == stale.Id, "âge de la dernière preuve avance sans masquer l’erreur ni changer la mission sélectionnée");
                window.Dashboard.Search.Text = ""; window.Dashboard.SetView(1); assert(window.JobsList.Items.Count == 0, "filtre En cours ne présente aucun travail dont l’activité actuelle est non confirmée"); window.Dashboard.SetView(2); assert(window.JobsList.Items.Count == 2, "erreurs de lecture et rapprochements incertains restent dans À traiter"); window.Dashboard.SetView(0); window.Dashboard.Search.Text = uncertain.Title; window.UpdateLayout();
                assert(Label("État observé de la mission Cloud").Text == "Envoi à réconcilier" && Label("État réel du suivi Cloud").Text.Contains("Un chat ouvert ou au repos") && window.ResultText.Text.Contains("Aucun résultat"), "chat au repos sans tour ne devient ni mission en fonctionnement ni résultat annoncé");
                read.LastCheckError = null; read.LastSuccessfulCheckUtc = clock.ToString("o"); read.LastCheckedUtc = clock.ToString("o"); read.NextCheckUtc = clock.AddSeconds(60).ToString("o"); window.Dashboard.Search.Text = stale.Title; await window.RefreshNow(); assert(Label("En cours").Text == "1" && Label("État observé de la mission Cloud").Text == "En cours", "seule une lecture native redevenue certaine rétablit le statut En cours");
                clock = clock.AddSeconds(121); await window.RefreshNow(); assert(Label("En cours").Text == "0" && Label("État observé de la mission Cloud").Text == "État à actualiser", "preuve devenue ancienne sans nouvelle erreur ne reste pas présentée comme activité certaine");
                Capture(window, Path.Combine(root, "Cloud-suivi-perime.png"));
            }
            finally { window.Close(); }
        }
    }
}
