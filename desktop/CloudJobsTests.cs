using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
                    CloudMissionWizard form = null;
                    try
                    {
                        await Until(() => Application.Current.Windows.OfType<CloudMissionWizard>().Any());
                        form = Application.Current.Windows.OfType<CloudMissionWizard>().Single(); form.Opacity = 0;
                        assert(form.Owner == window && !Button("Nouvelle mission Cloud").IsEnabled, "assistant Cloud possède sa fenêtre et bloque une seconde création simultanée");
                        var account = form.Account; var environment = form.Environment;
                        CloudMissionAccountChoice Choice(string id) => account.Items.Cast<CloudMissionAccountChoice>().Single(value => value.Id == id);
                        void Next() => form.NextButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        void Back() => form.BackButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        assert(form.Step == 0 && !form.NextButton.IsEnabled && !form.MissionTitle.IsVisible, "assistant commence par le compte et masque les questions suivantes");
                        account.SelectedItem = Choice(owner.Id);
                        await Until(() => loads == 1);
                        assert(form.Step == 1 && form.Loading && account.IsEnabled && !environment.IsEnabled && !form.NextButton.IsEnabled, "choix du compte charge automatiquement les environnements et laisse changer de compte pendant l’attente");
                        account.SelectedItem = Choice(client.Id);
                        await Until(() => loads == 2 && environment.Items.Count == 1 && !form.Loading);
                        assert(environment.SelectedItem == null && !form.NextButton.IsEnabled, "environnement unique attend un choix explicite et ne fait aucun envoi implicite");
                        oldLoad.SetResult(new[] { new CloudConfiguration { Id = "environment-owner", Name = "Réponse tardive du premier compte" } });
                        await Task.Delay(40);
                        assert(environment.Items.Cast<CloudConfiguration>().Single().Id == "environment-client" && form.LoadedAccountKey == client.Id + "/" + client.AccountKey, "réponse tardive d’un compte annulé ne remplace jamais les environnements du compte courant");
                        environment.SelectedIndex = 0; Next();
                        assert(form.Step == 2 && form.MissionTitle.IsVisible && !account.IsVisible, "choix d’environnement conduit à la question de mission sans formulaire géant");
                        Next(); assert(form.Step == 2 && form.ErrorText.Text.Contains("titre"), "titre vide est expliqué à l’étape de mission avant tout envoi");
                        var title = form.MissionTitle; title.Text = "Mission depuis l’assistant";
                        Next(); assert(form.Step == 2 && form.ErrorText.Text.Contains("travail"), "mandat vide bloque le récapitulatif sans perdre le titre");
                        var prompt = form.Mandate; prompt.Text = "Recette fictive sans réseau ni dépôt réel.";
                        Back(); Back(); assert(form.Step == 0 && title.Text == "Mission depuis l’assistant" && prompt.Text == "Recette fictive sans réseau ni dépôt réel.", "retour jusqu’au compte conserve le titre et le mandat");
                        Next(); assert(form.Step == 1 && loads == 2 && environment.SelectedItem != null, "retour à un compte inchangé conserve le catalogue et son choix sans nouvelle requête");
                        Next(); Next();
                        assert(form.Step == 3 && Descendants(form).OfType<TextBlock>().Any(text => text.IsVisible && text.Text.Contains("compte-1@example.com")) && Descendants(form).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "Environnement du second compte"), "récapitulatif montre le compte et l’environnement exacts avant l’envoi");
                        Next();
                        assert(form.Step == 3 && submits == 0 && !form.NextButton.IsEnabled, "double clic sur Continuer ne traverse pas le récapitulatif pour envoyer une mission");
                        Back();
                        await Task.Delay(Math.Max(350, System.Windows.Forms.SystemInformation.DoubleClickTime + 80));
                        assert(form.Step == 2 && submits == 0, "retour pendant la transition annule l’armement du récapitulatif abandonné");
                        Next();
                        assert(!form.NextButton.IsEnabled && submits == 0, "nouvelle entrée dans le récapitulatif exige sa propre transition avant envoi");
                        await Until(() => form.NextButton.IsEnabled);
                        form.Width = 680; form.Height = 640; form.UpdateLayout();
                        assert(CloudWizardFooterFits(form) && !Descendants(form).OfType<ScrollViewer>().Any(scroll => scroll.IsVisible && !(scroll.TemplatedParent is TextBox)), "assistant compact garde l’envoi dans les limites du contenu sans défilement du formulaire");
                        Capture(form, Path.Combine(root, "Cloud-mission-review.png"));
                        Next(); await Until(() => submits == 1 && !form.Submitting);
                        assert(!form.Saved && form.IntentLocked && title.IsReadOnly && prompt.IsReadOnly && !account.IsEnabled && !environment.IsEnabled && !form.BackButton.IsEnabled, "échec d’enregistrement fige l’intention affichée pour une reprise identifiée");
                        Next(); await Until(() => submits == 2); Next();
                        assert(submits == 2 && ids.Distinct().Count() == 1 && ids[0].Length == 32 && !form.NextButton.IsEnabled, "reprise et double clic conservent un seul identifiant et aucun troisième envoi");
                        submitted.SetResult(true); await Until(() => form.Saved); composed.SetResult(true);
                    }
                    catch (Exception exception) { composed.SetException(exception); }
                    finally { submitted.TrySetResult(true); if (form != null && !form.Saved) { form.ConfirmDiscard = () => true; form.Close(); } }
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
            await CloudWizardStates(shell, owner, client, root, assert);
            await CloudWizardAccountChange(context, shell, owner, client, assert);
        }

        private static async Task CloudWizardStates(ShellWindow shell, DesktopInstance owner, DesktopInstance client, string root, Action<bool, string> assert)
        {
            var choices = new[] { new CloudMissionAccountChoice { Id = owner.Id, AccountKey = owner.AccountKey, Label = "Compte propriétaire · owner@example.com" }, new CloudMissionAccountChoice { Id = client.Id, AccountKey = client.AccountKey, Label = "Compte exécutant · worker@example.com" } };
            var old = new TaskCompletionSource<CloudConfiguration[]>(); bool oldCancelled = false; int clientLoads = 0, ownerLoads = 0, unexpectedSubmits = 0;
            Task<CloudConfiguration[]> Load(CloudMissionAccountChoice choice, CancellationToken token)
            {
                if (choice.Id == owner.Id)
                {
                    ownerLoads++; if (ownerLoads == 1) { token.Register(() => oldCancelled = true); return old.Task; }
                    return Task.FromResult(new[] { new CloudConfiguration { Id = "environment-owner", Name = "Autre compte" } });
                }
                if (++clientLoads == 1) throw new InvalidOperationException("Service fictif momentanément indisponible");
                return Task.FromResult(clientLoads == 2 ? new CloudConfiguration[0] : new[] { new CloudConfiguration { Id = "environment-client", Name = "Environnement de recette" } });
            }
            var form = new CloudMissionWizard(shell, choices, Load, (spec, token) => { unexpectedSubmits++; return Task.CompletedTask; }, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false };
            try
            {
                form.Show(); form.Account.SelectedIndex = 0; form.Account.SelectedIndex = 1;
                await Until(() => !form.Loading && clientLoads == 1);
                assert(oldCancelled && form.Account.IsEnabled && form.LoadStatus.Text.Contains("Service fictif") && form.RetryButton.IsVisible && !form.NextButton.IsEnabled, "erreur de catalogue distingue l’échec réseau et permet une reprise sans bloquer le compte");
                old.SetException(new InvalidOperationException("Erreur tardive de l’ancien compte")); await Task.Delay(30);
                assert(!form.LoadStatus.Text.Contains("Erreur tardive"), "erreur tardive d’un compte abandonné ne remplace pas le diagnostic courant");
                form.RetryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => clientLoads == 2 && !form.Loading);
                assert(form.LoadStatus.Text.Contains("Aucun environnement") && form.Environment.Items.Count == 0 && !form.NextButton.IsEnabled && form.RetryButton.IsVisible, "catalogue vide affiche une explication distincte et aucun faux choix");
                form.RetryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => clientLoads == 3 && !form.Loading);
                assert(form.Environment.Items.Count == 1 && form.Environment.SelectedItem == null, "réessai charge automatiquement le catalogue sans choisir arbitrairement un environnement");
                form.Environment.SelectedIndex = 0; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                form.MissionTitle.Text = "Conserver ce titre"; form.Mandate.Text = "Conserver ce mandat";
                form.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); form.Account.SelectedIndex = 0;
                await Until(() => ownerLoads == 2 && !form.Loading);
                assert(form.Environment.SelectedItem == null && form.Environment.Items.Cast<CloudConfiguration>().Single().Id == "environment-owner" && !form.NextButton.IsEnabled && form.MissionTitle.Text == "Conserver ce titre" && form.Mandate.Text == "Conserver ce mandat", "changer de compte invalide l’environnement et conserve le travail de rédaction");
                form.Width = 680; form.Height = 580; form.UpdateLayout();
                Capture(form, Path.Combine(root, "Cloud-mission-environment.png"));
                assert(CloudWizardFooterFits(form) && form.Account.IsVisible, "étape environnement 680×580 conserve compte et footer dans les limites du contenu réel");
                form.ConfirmDiscard = () => false; form.Close(); assert(form.IsVisible, "fermeture conserve un mandat rédigé tant que son abandon n’est pas confirmé");
                form.ConfirmDiscard = () => true; form.Close();
            }
            finally { form.ConfirmDiscard = () => true; if (form.IsVisible) form.Close(); old.TrySetResult(new CloudConfiguration[0]); }

            var late = new TaskCompletionSource<CloudConfiguration[]>(); bool cancelled = false;
            var closing = new CloudMissionWizard(shell, choices, (choice, token) => { token.Register(() => cancelled = true); return late.Task; }, (spec, token) => { unexpectedSubmits++; return Task.CompletedTask; }, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false };
            closing.Show(); closing.Account.SelectedIndex = 0; string statusBeforeClose = closing.LoadStatus.Text; closing.Close();
            late.SetResult(new[] { new CloudConfiguration { Id = "late", Name = "Après fermeture" } }); await Task.Delay(30);
            assert(cancelled && closing.Environment.Items.Count == 0 && closing.LoadStatus.Text == statusBeforeClose && !closing.Saved, "fermeture de l’assistant annule le catalogue et ignore sa réponse tardive");

            int seededLoads = 0;
            var seeded = new CloudMissionWizard(shell, choices, (choice, token) => { seededLoads++; return Task.FromResult(new[] { new CloudConfiguration { Id = "requested-environment", Name = "Environnement explicitement demandé" }, new CloudConfiguration { Id = "other-environment", Name = "Un autre environnement" } }); }, (spec, token) => { unexpectedSubmits++; return Task.CompletedTask; }, CancellationToken.None, client.Id, "requested-environment") { Opacity = 0, ShowInTaskbar = false };
            try
            {
                seeded.Show(); await Until(() => seededLoads == 1 && !seeded.Loading);
                assert(seeded.Step == 1 && ((CloudMissionAccountChoice)seeded.Account.SelectedItem).Id == client.Id && ((CloudConfiguration)seeded.Environment.SelectedItem).Id == "requested-environment" && seeded.NextButton.IsEnabled && unexpectedSubmits == 0, "lancement depuis un environnement explicite préremplit seulement ce compte et cet environnement sans déléguer");
            }
            finally { seeded.Close(); }

            var longReview = new CloudMissionWizard(shell, choices, (choice, token) => Task.FromResult(new[] { new CloudConfiguration { Id = "long-environment", Name = String.Concat(Enumerable.Repeat("Environnement du laboratoire avec un nom très long ", 10)) } }), (spec, token) => { unexpectedSubmits++; return Task.CompletedTask; }, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false, Width = 680, Height = 580 };
            try
            {
                longReview.Show(); longReview.Account.SelectedIndex = 1; await Until(() => !longReview.Loading && longReview.Environment.Items.Count == 1);
                longReview.Environment.SelectedIndex = 0; longReview.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                longReview.MissionTitle.Text = new string('M', 200); longReview.Mandate.Text = String.Join("\n", Enumerable.Repeat("Travail détaillé à vérifier dans le dépôt de recette.", 80));
                longReview.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); longReview.UpdateLayout();
                var preview = Descendants(longReview).OfType<TextBox>().Single(text => AutomationProperties.GetName(text) == "Mandat à envoyer");
                assert(longReview.Step == 3 && CloudWizardFooterFits(longReview) && preview.ActualHeight > 0 && preview.VerticalContentAlignment == VerticalAlignment.Top && longReview.Mandate.VerticalContentAlignment == VerticalAlignment.Top, "récapitulatif 680×580 aligne les consignes en haut et garde le footer dans les limites du contenu réel");
                assert(Descendants(longReview).OfType<TextBlock>().Any(text => text.IsVisible && text.MaxHeight == 44 && ((text.ToolTip as string)?.Length ?? 0) > 200) && unexpectedSubmits == 0, "nom d’environnement complet reste accessible en infobulle sans agrandir le résumé ni envoyer la mission");
                Capture(longReview, Path.Combine(root, "Cloud-mission-review-compact.png"));
            }
            finally { longReview.ConfirmDiscard = () => true; longReview.Close(); }
        }

        private static async Task CloudWizardAccountChange(DesktopContext context, ShellWindow shell, DesktopInstance owner, DesktopInstance client, Action<bool, string> assert)
        {
            int catalogReads = 0; string originalKey = client.AccountKey; var done = new TaskCompletionSource<bool>();
            var window = new CloudJobsWindow(context, shell) { Opacity = 0, ShowInTaskbar = false, JobsFixture = () => Task.FromResult(new CloudJob[0]), EnvironmentsFixture = (id, token) => { catalogReads++; return Task.FromResult(new CloudConfiguration[0]); } };
            _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
            {
                CloudMissionWizard form = null;
                try
                {
                    await Until(() => Application.Current.Windows.OfType<CloudMissionWizard>().Any()); form = Application.Current.Windows.OfType<CloudMissionWizard>().Single(); form.Opacity = 0;
                    var service = new AccountService(context.Root); service.Data.Instances.Single(instance => instance.Id == client.Id).AccountKey = owner.AccountKey; service.Vault.Save(service.Data);
                    form.Account.SelectedItem = form.Account.Items.Cast<CloudMissionAccountChoice>().Single(choice => choice.Id == client.Id);
                    await Until(() => !form.Loading && form.LoadStatus.Text.Contains("compte associé"));
                    assert(catalogReads == 0 && form.Environment.Items.Count == 0 && !form.NextButton.IsEnabled, "compte permanent modifié après ouverture est refusé avant toute lecture de son catalogue sous un ancien libellé");
                    done.SetResult(true);
                }
                catch (Exception exception) { done.SetException(exception); }
                finally
                {
                    var service = new AccountService(context.Root); service.Data.Instances.Single(instance => instance.Id == client.Id).AccountKey = originalKey; service.Vault.Save(service.Data);
                    if (form != null) { form.ConfirmDiscard = () => true; form.Close(); }
                }
            }), DispatcherPriority.ContextIdle);
            try { window.Show(); await window.StartNewMission(); await done.Task; }
            finally { if (window.IsVisible) window.Close(); }
        }

        private static bool CloudWizardFooterFits(CloudMissionWizard form)
        {
            var content = (FrameworkElement)form.Content;
            var available = new Rect(new Point(0, 0), content.RenderSize);
            return new[] { form.BackButton, form.CancelButton, form.NextButton }.Where(button => button.IsVisible).All(button =>
                available.Contains(button.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), button.RenderSize))));
        }
    }
}
