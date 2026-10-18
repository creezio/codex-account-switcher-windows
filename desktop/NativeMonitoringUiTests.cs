using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        internal static int RunMonitoringUi(Application app, string root)
        {
            Directory.CreateDirectory(root);
            string marker = Path.Combine(root, ".switcher-native-monitoring-fixture"), dataRoot = Path.Combine(root, "data");
            if (Directory.Exists(dataRoot) && !File.Exists(marker)) throw new InvalidOperationException("Dossier de recette existant sans marqueur ; conservé.");
            File.WriteAllText(marker, "Native monitoring UI fixture; fictional account only; no network, model or real instance.");
            // Reuse this marked fixture without retaining watches whose account
            // definitions are about to be replaced by the fictional test vault.
            string oldRecords = Path.Combine(dataRoot, "relay");
            if (Directory.Exists(oldRecords))
            {
                SafeFiles.RejectLinks(oldRecords);
                foreach (string path in Directory.EnumerateFiles(oldRecords, "chat-watch-*.dpapi", SearchOption.TopDirectoryOnly))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\Achat-watch-[a-f0-9]{32}\.dpapi\z")) continue;
                    SafeFiles.RejectLinks(path); File.Delete(path);
                }
            }
            var report = new List<string>(); int count = 0;
            Action<bool, string> assert = (ok, message) => { report.Add((ok ? "PASS " : "FAIL ") + message); if (!ok) throw new Exception(message); count++; };
            new Vault(dataRoot).Save(new VaultData());
            var accounts = new AccountService(dataRoot);
            var instance = accounts.Instances.Create("Atelier de recette");
            var profile = accounts.Import(FakeAuth(81), "Compte fictif de recette"); accounts.Instances.BindAccount(instance, profile);
            var context = new DesktopContext(dataRoot, true);
            int ensureCalls = 0; context.EnsureChatWatchWorkerFixture = () => ensureCalls++;
            string thread = "01a11111-2222-7333-8444-555555555555";
            DateTime clock = DateTime.UtcNow; string observed = "idle", turn = "turn-fixture-1"; int nativeReads = 0;
            var monitor = new ChatWatches(context.Store, (watch, token) => {
                nativeReads++;
                return Task.FromResult(Json.Read<object>(Json.Write(new { thread = new { id = watch.ThreadId, status = new { type = observed } }, turns = new[] { new { id = turn, status = observed == "active" ? "inProgress" : "completed", items = new object[0] } } })));
            }, () => clock);
            var watchRecord = monitor.Save(new ChatWatchRecord { Name = "Mission complète de recette", InstanceId = instance.Id, AccountKey = instance.AccountKey, ThreadId = thread, Workspace = dataRoot, Enabled = true });
            var shell = new ShellWindow(context) { ShowInTaskbar = false, Opacity = 0 };
            app.MainWindow = shell;
            shell.Loaded += async delegate {
                ChatWatchesWindow watches = null; CodexCompatibilityWindow compatibility = null;
                try
                {
                    await context.RestoreChatMonitoring();
                    assert(ensureCalls == 1 && !ChatWatchWorker.Running(context.Store), "démarrage restaure la surveillance configurée via le callback de recette sans ouvrir de processus");
                    await monitor.Check(watchRecord.Id, CancellationToken.None);
                    int openCalls = 0; string openedThread = null;
                    watches = new ChatWatchesWindow(context, shell) { ShowInTaskbar = false, Width = 1000, Height = 700, OpenFixture = value => { openCalls++; openedThread = value.ThreadId; return Task.CompletedTask; }, ConfirmCompleteFixture = () => true };
                    watches.Show(); await Until(() => watches.WatchList.Items.Count == 1); watches.UpdateLayout();
                    Button Find(string title) => Descendants(watches).OfType<Button>().Single(b => Object.Equals(b.Content, title));
                    assert(watches.PendingAlertCount == 1 && watches.MonitoringNotice.StartsWith("1 alerte"), "arrêt sans fin confirmée produit une alerte visible dans Surveillances");
                    assert(Descendants(watches).OfType<TextBlock>().Any(t => t.Text == watchRecord.Name) && Descendants(watches).OfType<TextBlock>().Any(t => t.Text.Contains(instance.Name) && t.Text.Contains(profile.Email)), "fiche affiche la mission, l’instance et son compte permanent");
                    assert(Descendants(watches).OfType<TextBlock>().Any(t => t.Text == "Arrêtée · fin non confirmée") && Descendants(watches).OfType<TextBlock>().Any(t => t.Text.StartsWith("Dernière vérification :")), "dernier état et âge de sa vérification sont visibles sans confondre arrêt et réussite");
                    Capture(watches, Path.Combine(root, "Surveillances-1000x700.png"));
                    Find("Ouvrir la conversation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => openCalls == 1);
                    assert(openedThread == thread, "ouverture transmet uniquement l’identifiant exact du chat surveillé à la navigation fictive");
                    Find("Acquitter l’alerte").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !String.IsNullOrEmpty(monitor.Get(watchRecord.Id).AcknowledgedUtc)); await watches.RefreshNow(); await Until(() => watches.PendingAlertCount == 0);
                    assert(watches.PendingAlertCount == 0 && monitor.Get(watchRecord.Id).State == "stopped", "acquitter retire l’alerte sans déclarer la mission terminée ni relancer l’agent");
                    observed = "active"; turn = "turn-fixture-2"; clock = DateTime.UtcNow.AddMinutes(-4);
                    await monitor.Check(watchRecord.Id, CancellationToken.None); await watches.RefreshNow(); watches.UpdateLayout();
                    assert(Descendants(watches).OfType<TextBlock>().Any(t => t.Text == "Contrôle en retard · suivi à vérifier"), "un ancien état running ne prétend pas que l’agent travaille actuellement");
                    Find("Mettre en pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !monitor.Get(watchRecord.Id).Enabled); await watches.RefreshNow(); await Until(() => Descendants(watches).OfType<Button>().Any(b => Object.Equals(b.Content, "Reprendre le suivi")));
                    assert(monitor.Get(watchRecord.Id).State == "paused", "pause persiste la désactivation du suivi");
                    Find("Reprendre le suivi").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => monitor.Get(watchRecord.Id).Enabled); await watches.RefreshNow(); await Until(() => Descendants(watches).OfType<Button>().Any(b => Object.Equals(b.Content, "Mettre en pause")));
                    assert(monitor.Get(watchRecord.Id).State == "waiting" && !ChatWatchWorker.Running(context.Store), "reprise en fixture n’ouvre ni worker, ni Codex, ni moteur de modèle");
                    Find("Confirmer la mission terminée…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => monitor.Get(watchRecord.Id).State == "completed"); await watches.RefreshNow();
                    assert(!monitor.Get(watchRecord.Id).Enabled, "confirmation explicite de fin arrête la surveillance");
                    assert(openCalls == 1 && nativeReads == 2 && context.Store.Count() == 0 && context.Store.Channels().Count == 0, "actions de l’interface n’envoient aucun prompt et ne créent aucune tâche ou connexion");
                    watches.Close(); assert(!watches.RefreshTimerRunning, "fermer la vue arrête uniquement son rafraîchissement local"); watches = null;

                    var compatibilityReport = new CodexCompatibilityReport { InstalledVersion = "26.1013.4894.0", CliVersion = "codex-fixture 0.162", State = "verified", CheckedUtc = DateTime.UtcNow.ToString("o"), Detail = "Lecture native de recette vérifiée, opérations de modification non testées.", Checks = new List<CodexCompatibilityCheck> {
                        new CodexCompatibilityCheck { Name = instance.Name, InstanceId = instance.Id, State = "verified", RunningVersion = "26.1012.4000.0", CheckedUtc = DateTime.UtcNow.ToString("o"), Detail = "Catalogue et lecture fictive vérifiés ; aucun prompt." },
                        new CodexCompatibilityCheck { Name = "Studio fermé", InstanceId = new string('b',32), State = "not-tested", Detail = "Instance fermée : aucun lancement effectué." }
                    } };
                    context.Store.WriteRecord(CodexCompatibility.Record, compatibilityReport);
                    int checks = 0;
                    compatibility = new CodexCompatibilityWindow(context, shell) { ShowInTaskbar = false, CheckFixture = () => { checks++; return Task.FromResult(compatibilityReport); } };
                    compatibility.Show(); await Until(() => compatibility.Summary.Contains("Lecture native vérifiée")); compatibility.UpdateLayout();
                    var texts = Descendants(compatibility).OfType<TextBlock>().Select(t => t.Text).ToArray();
                    assert(texts.Any(t => t.Contains("Application installée : 26.1013.4894.0")) && texts.Any(t => t.Contains("Version active : 26.1012.4000.0")), "compatibilité distingue version installée et version encore active");
                    assert(texts.Any(t => t.StartsWith("Non testée · Aucune version active")) && texts.Any(t => t.Contains("Instance fermée : aucun lancement")), "instance fermée reste non testée et n’est pas présentée comme compatible");
                    assert(texts.Any(t => t.Contains("ne valide pas une publication")) && checks == 0, "consulter le rapport n’exécute aucune vérification réelle et précise les limites du contrôle");
                    Capture(compatibility, Path.Combine(root, "Compatibilite-Codex.png"));
                    Descendants(compatibility).OfType<Button>().Single(b => Object.Equals(b.Content, "Refaire les vérifications")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => checks == 1);
                    assert(checks == 1 && context.Store.Count() == 0, "contrôle demandé utilise seulement le callback de recette sans mission native");
                    compatibility.Close(); compatibility = null;
                    File.WriteAllText(Path.Combine(root, "native-monitoring-ui-result.txt"), String.Join("\n", report) + "\n" + count + " contrôles réussis\n");
                    shell.ExitForTest(); app.Shutdown(0);
                }
                catch (Exception e)
                {
                    File.WriteAllText(Path.Combine(root, "native-monitoring-ui-result.txt"), String.Join("\n", report) + "\nFAIL " + e + "\n");
                    if (watches != null) watches.Close(); if (compatibility != null) compatibility.Close(); shell.ExitForTest(); app.Shutdown(1);
                }
            };
            return app.Run(shell);
        }
    }
}
