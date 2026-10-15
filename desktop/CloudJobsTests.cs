using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task CloudJobsAcceptance(DesktopContext context, ShellWindow shell, DesktopInstance owner, DesktopInstance client, string root, Action<bool, string> assert)
        {
            string savedId = null;
            var jobs = Enumerable.Range(0, 70).Select(index => new CloudJob { SchemaVersion = 1, Id = "d" + index.ToString("x31"), Title = "Mission Cloud de recette " + index, InstanceId = index % 2 == 0 ? owner.Id : client.Id, InstanceName = "Ancien nom", AccountKey = index % 2 == 0 ? owner.AccountKey : client.AccountKey, State = "running", Result = String.Join("\n", Enumerable.Range(0, 130).Select(line => "Résultat fictif et sélectionnable · ligne " + line)), Branch = "codex/fixture/" + index, ConfigurationId = "configuration-fixture", ThreadId = "thread-fixture", ReturnState = "none" }).ToList();
            int reads = 0, checks = 0, loads = 0, submits = 0, ensures = 0;
            var window = new CloudJobsWindow(context, shell) { Opacity = 0, ShowInTaskbar = false, Width = 880, Height = 580 };
            window.JobsFixture = () =>
            {
                reads++;
                return Task.FromResult(Json.Read<CloudJob[]>(Json.Write(jobs.Select(job => job.Id == savedId ? context.Store.ReadRecord<CloudJob>("cloud-job-" + savedId + ".dpapi") : job))));
            };
            Button Button(string label) => Descendants(window).OfType<Button>().Single(button => Object.Equals(button.Content, label));
            TextBlock Error() => Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetName(text) == "Erreur des missions Cloud");
            try
            {
                window.Show();
                await Until(() => window.JobsList.Items.Count == 70 && window.RefreshTimerRunning);
                window.UpdateLayout();
                assert(window.Owner == shell && Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("compte-0@example.com")) && Descendants(window).OfType<TextBlock>().Any(text => text.Text == "En cours"), "missions Cloud affichent le compte exécutant et un statut français dans la fenêtre possédée");
                window.JobsList.SelectedIndex = 30;
                window.JobsList.ScrollIntoView(window.JobsList.SelectedItem);
                var diagnostic = Descendants(window).OfType<Expander>().Single(); diagnostic.IsExpanded = true;
                window.UpdateLayout();
                var listScroll = Descendants(window.JobsList).OfType<ScrollViewer>().First(); listScroll.ScrollToVerticalOffset(20);
                window.ResultText.Select(18, 9); window.ResultText.ScrollToVerticalOffset(100);
                window.DetailsScroll.ScrollToBottom(); window.UpdateLayout();
                double listOffset = listScroll.VerticalOffset, outerOffset = window.DetailsScroll.VerticalOffset, innerOffset = window.ResultText.VerticalOffset;
                var resultControl = window.ResultText; string selected = window.SelectedJobId;
                await window.RefreshNow();
                assert(window.SelectedJobId == selected && ReferenceEquals(resultControl, window.ResultText) && diagnostic.IsExpanded && window.ResultText.SelectionStart == 18 && window.ResultText.SelectionLength == 9, "actualisation conserve la mission sélectionnée, la réponse sélectionnable et le diagnostic ouvert");
                assert(listOffset > 0 && outerOffset > 0 && innerOffset > 0 && Math.Abs(listScroll.VerticalOffset - listOffset) < 1 && Math.Abs(window.DetailsScroll.VerticalOffset - outerOffset) < 2 && Math.Abs(window.ResultText.VerticalOffset - innerOffset) < 2, "actualisation identique conserve les trois positions de lecture sans reconstruire les contrôles");
                jobs[30].State = "attention";
                await window.RefreshNow();
                assert(window.SelectedJobId == selected && diagnostic.IsExpanded && window.ResultText.SelectionStart == 18 && window.DetailsScroll.VerticalOffset >= outerOffset - 2 && Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("Intervention requise")), "changement de statut actualise le français sans perdre la lecture en cours");
                Capture(window, Path.Combine(root, "Cloud-jobs.png"));

                var checkedJob = new TaskCompletionSource<bool>();
                window.AdvanceFixture = async (id, token) => { checks++; assert(id == selected, "vérification de progression cible seulement la mission affichée"); await checkedJob.Task.WaitAsync(token); jobs.Single(job => job.Id == id).State = "completed"; return jobs.Single(job => job.Id == id); };
                await window.RefreshNow();
                // The injected action is new; change a visible field to render its fixture button.
                jobs[30].Error = "Progression de recette"; await window.RefreshNow();
                Button("Vérifier la progression").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => checks == 1);
                Button("Vérifier la progression").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                assert(checks == 1 && !Button("Vérifier la progression").IsEnabled, "double clic de progression ne démarre qu’une lecture en parallèle");
                checkedJob.SetResult(true);
                await Until(() => Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("Terminée")) && !Descendants(window).OfType<Button>().Any(button => Object.Equals(button.Content, "Vérifier la progression")));

                var oldLoad = new TaskCompletionSource<CloudConfiguration[]>();
                var submitted = new TaskCompletionSource<bool>();
                var ids = new List<string>();
                window.EnvironmentsFixture = (id, token) => { loads++; return id == owner.Id ? oldLoad.Task : Task.FromResult(new[] { new CloudConfiguration { Id = "environment-client", Name = "Environnement du second compte", VersionRevision = 2, VersionId = "version-client", LatestReadyVersionId = "version-client", Status = "ready" } }); };
                window.SubmitFixture = async (spec, token) =>
                {
                    submits++; ids.Add(spec.Id);
                    assert(spec.Instance == client.Id && spec.ExpectedAccountKey == client.AccountKey && spec.ConfigurationId == "environment-client" && spec.ExplicitDelegation && !spec.ReturnToSource, "envoi conserve le compte choisi et l’environnement chargé pour ce compte");
                    if (submits == 1) throw new InvalidOperationException("Enregistrement fictif temporairement indisponible");
                    await submitted.Task.WaitAsync(token);
                    savedId = spec.Id;
                    var job = new CloudJob { SchemaVersion = 1, Id = spec.Id, Title = spec.Title, Prompt = spec.Prompt, AccountKey = client.AccountKey, InstanceId = client.Id, InstanceName = client.Name, ConfigurationId = spec.ConfigurationId, State = "queued", ReturnState = "none", Branch = "codex/switcher/" + spec.Id };
                    context.Store.WriteRecord("cloud-job-" + savedId + ".dpapi", job); jobs.Insert(0, job); return job;
                };
                window.EnsureWorkerFixture = () => { ensures++; if (ensures == 1) throw new InvalidOperationException("Suivi fictif indisponible"); };
                var composed = new TaskCompletionSource<bool>();
                _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    EditWindow form = null;
                    try
                    {
                        form = await WaitDialog("Nouvelle mission Codex Cloud"); form.Opacity = 0;
                        assert(form.Owner == window && !Button("Nouvelle mission Cloud").IsEnabled, "formulaire de mission appartient à sa fenêtre et bloque une seconde création simultanée");
                        var account = Descendants(form).OfType<ComboBox>().Single(choice => AutomationProperties.GetName(choice) == "Compte exécutant la mission Cloud");
                        var environment = Descendants(form).OfType<ComboBox>().Single(choice => AutomationProperties.GetName(choice) == "Environnement publié de la mission Cloud");
                        object Choice(string id) => account.Items.Cast<object>().Single(value => Object.Equals(value.GetType().GetProperty("Id").GetValue(value), id));
                        Button Load() => Descendants(form).OfType<Button>().Single(button => Object.Equals(button.Content, "Charger les environnements"));
                        account.SelectedItem = Choice(owner.Id); Load().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Until(() => loads == 1);
                        assert(!account.IsEnabled, "chargement d’environnements indique son compte et empêche une sélection concurrente ordinaire");
                        account.SelectedItem = Choice(client.Id);
                        oldLoad.SetResult(new[] { new CloudConfiguration { Id = "environment-owner", Name = "Réponse tardive du premier compte" } });
                        await Until(() => Load().IsEnabled);
                        assert(environment.Items.Count == 0, "réponse tardive d’un autre compte ne remplit jamais le sélecteur courant");
                        Load().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Until(() => environment.Items.Count == 1 && Load().IsEnabled);
                        assert(environment.SelectedItem == null, "aucun environnement n’est choisi automatiquement à la place de l’utilisateur");
                        environment.SelectedIndex = 0;
                        var title = Descendants(form).OfType<TextBox>().Single(input => AutomationProperties.GetName(input) == "Titre de la mission"); title.Text = "Mission depuis le formulaire";
                        var prompt = Descendants(form).OfType<TextBox>().Single(input => AutomationProperties.GetName(input) == "Mandat, périmètre autorisé et résultat attendu"); prompt.Text = "Recette fictive sans réseau ni dépôt réel.";
                        form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Until(() => submits == 1 && form.SaveButton.IsEnabled);
                        assert(!form.Saved && title.IsReadOnly && prompt.IsReadOnly && !account.IsEnabled && !environment.IsEnabled, "échec d’enregistrement fige la même intention pour une reprise identifiée");
                        form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Until(() => submits == 2);
                        form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        assert(submits == 2 && ids.Distinct().Count() == 1 && ids[0].Length == 32, "reprise et double clic conservent un seul identifiant de mission et aucun troisième envoi");
                        submitted.SetResult(true);
                        await Until(() => form.Saved);
                        composed.SetResult(true);
                    }
                    catch (Exception exception) { composed.SetException(exception); }
                    finally { if (form != null && !form.Saved) { form.ConfirmDiscard = () => true; form.Close(); } }
                }), DispatcherPriority.ContextIdle);
                Button("Nouvelle mission Cloud").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await composed.Task;
                await Until(() => savedId != null && window.SelectedJobId == savedId && Error().Text.Contains("enregistrée une seule fois"));
                await window.RefreshNow();
                assert(submits == 2 && ensures == 1 && Error().Text.Contains("suivi indisponible"), "échec du suivi conserve la mission enregistrée et son explication après actualisation");
                Button("Démarrer le suivi").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => ensures == 2);
                assert(submits == 2 && Error().Text == "", "reprise du suivi démarre le superviseur sans resoumettre la mission");
                Button("Annuler cette mission").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("Annulée")) && !Descendants(window).OfType<Button>().Any(button => Object.Equals(button.Content, "Annuler cette mission")));
                assert(context.Store.ReadRecord<CloudJob>("cloud-job-" + savedId + ".dpapi").State == "cancelled", "annulation de la mission en attente est enregistrée sans envoyer de travail Cloud");
                int previous = reads;
                await Until(() => reads > previous);
                assert(window.RefreshTimerRunning, "fenêtre visible actualise automatiquement la liste des missions");
                window.Close();
                assert(!window.RefreshTimerRunning && loads == 2 && submits == 2, "fermeture arrête le suivi visuel et tous les appels de recette restent injectés");
            }
            finally { if (window.IsLoaded) window.Close(); }

            var pending = new TaskCompletionSource<CloudJob[]>(); int pendingReads = 0;
            var closing = new CloudJobsWindow(context, shell) { Opacity = 0, ShowInTaskbar = false, JobsFixture = () => { pendingReads++; return pending.Task; } };
            closing.Show(); await Until(() => pendingReads == 1); closing.Close(); pending.SetResult(new CloudJob[0]); await Until(() => !closing.Refreshing);
            assert(!closing.RefreshTimerRunning && pendingReads == 1, "fermeture pendant la première lecture ne redémarre jamais le timer après sa réponse tardive");
        }
    }
}
