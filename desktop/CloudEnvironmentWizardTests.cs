using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task CloudEnvironmentWizardAcceptance(ShellWindow shell, string root, Action<bool, string> assert)
        {
            var accounts = new[] { new CloudEnvironmentAccountChoice { Id = "owner", AccountKey = "account-owner", Label = "Compte propriétaire · owner@example.com" }, new CloudEnvironmentAccountChoice { Id = "worker", AccountKey = "account-worker", Label = "Équipe Cloud · worker@example.com" } };
            var catalog = Enumerable.Range(0, 35).Select(index => new CloudGitHubRepository { Id = "repo-" + index, FullName = "laboratoire/depot-" + index, DefaultBranch = "main" }).ToArray();
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            async Task Snapshot(CloudEnvironmentWizard wizard, string name)
            {
                wizard.UpdateLayout();
                await wizard.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                wizard.UpdateLayout(); Capture(wizard, Path.Combine(root, name));
            }
            bool InsideContent(CloudEnvironmentWizard wizard, FrameworkElement target)
            {
                var content = (FrameworkElement)wizard.Content;
                return target.ActualHeight > 0 && new Rect(new Point(), content.RenderSize).Contains(target.TransformToAncestor(content).TransformBounds(new Rect(new Point(), target.RenderSize)));
            }
            bool FooterFits(CloudEnvironmentWizard wizard)
            {
                var content = (FrameworkElement)wizard.Content; var bounds = new Rect(new Point(), content.RenderSize);
                return Descendants(content).OfType<Button>().Where(button => button.IsVisible).All(button => bounds.Contains(button.TransformToAncestor(content).TransformBounds(new Rect(new Point(), button.RenderSize))));
            }
            var late = new TaskCompletionSource<CloudGitHubRepository[]>(); bool cancelled = false; int loads = 0, sends = 0;
            CloudEnvironmentWizardIntent created = null; var release = new TaskCompletionSource<bool>();
            var create = new CloudEnvironmentWizard(shell, accounts, (account, token) => { loads++; if (account.Id == "owner") { token.Register(() => cancelled = true); return late.Task; } return Task.FromResult(catalog); }, async (intent, token) => { sends++; created = intent; await release.Task.WaitAsync(token); }, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false, Width = 680, Height = 580 };
            try
            {
                create.Show(); assert(create.Step == 0 && !create.NextButton.IsEnabled, "assistant environnement demande d’abord le compte sans dépôt implicite");
                create.Account.SelectedIndex = 0; await Until(() => loads == 1); create.Account.SelectedIndex = 1; await Until(() => loads == 2 && !create.Loading);
                assert(cancelled && create.Account.IsEnabled && create.RepositoryRows.Count == 35 && create.RepositoryRows.All(row => !row.Selected) && !create.NextButton.IsEnabled, "sélection du compte charge automatiquement ses dépôts et annule le compte précédent sans choisir de dépôt");
                late.SetException(new InvalidOperationException("Ancienne erreur à ignorer")); await Task.Delay(30);
                assert(!create.Status.Text.Contains("Ancienne erreur") && create.RepositoryRows.Count == 35, "erreur tardive d’un ancien compte ne remplace pas le catalogue courant");
                create.RepositoryRows.Single(row => row.Id == "repo-2").Selected = true; create.RepositoryRows.Single(row => row.Id == "repo-2").Ref = "feature/preparation";
                create.Search.Text = "depot-1"; create.RepositoryRows.Single(row => row.Id == "repo-12").Selected = true; create.Search.Text = "";
                assert(create.RepositoryRows.Count(row => row.Selected) == 2 && create.Repositories.Items.Count == 35, "recherche locale conserve les dépôts cochés hors du filtre");
                var toggleRow = create.RepositoryRows.Single(row => row.Id == "repo-0"); create.Repositories.ScrollIntoView(toggleRow); create.UpdateLayout();
                var rowContainer = (DataGridRow)create.Repositories.ItemContainerGenerator.ContainerFromItem(toggleRow);
                var check = Descendants(rowContainer).OfType<CheckBox>().Single(); var toggle = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle);
                toggle.Toggle(); assert(toggleRow.Selected && AutomationProperties.GetName(check) == toggleRow.Name, "une activation de la case nommée coche immédiatement le dépôt sans premier clic de focus"); toggle.Toggle();
                assert(Descendants(rowContainer).OfType<TextBlock>().Any(text => text.Text == toggleRow.Name && Object.Equals(text.ToolTip, toggleRow.Name) && text.TextTrimming == TextTrimming.CharacterEllipsis) && FooterFits(create), "dépôts 680×580 garde le footer visible et le nom complet accessible en infobulle"); await Snapshot(create, "Cloud-environment-wizard-repositories.png");
                Click(create.BackButton); Click(create.NextButton); assert(loads == 2 && create.RepositoryRows.Count(row => row.Selected) == 2, "retour compte puis dépôts conserve les choix sans recharger le compte inchangé");
                Click(create.NextButton); assert(create.Step == 2, "choix de plusieurs dépôts mène aux réglages");
                Click(create.NextButton); assert(create.Step == 2 && !create.IntentLocked && create.ErrorText.Text.Contains("nom"), "nom vide expliqué avant tout gel d’intention");
                create.EnvironmentName.Text = "Environnement partagé de recette"; create.Directory.Text = "relatif"; Click(create.NextButton);
                assert(create.Step == 2 && !create.IntentLocked && create.ErrorText.Text.Contains("absolu"), "dossier relatif refusé avant mutation");
                create.Directory.Text = "/workspace/laboratoire"; create.Network.SelectedIndex = 1; create.UpdateLayout(); assert(FooterFits(create), "réglages 680×580 garde toute la navigation dans le contenu réel"); await Snapshot(create, "Cloud-environment-wizard-settings.png"); Click(create.NextButton);
                create.Install.Text = "python -m unittest discover -v"; create.Start.Text = "Inspecter les deux dépôts, puis rendre les résultats de tests."; create.UpdateLayout(); assert(FooterFits(create) && InsideContent(create, create.Install) && InsideContent(create, create.Start), "démarrage 680×580 garde les deux zones de saisie et le footer dans le contenu réel"); await Snapshot(create, "Cloud-environment-wizard-startup.png"); Click(create.NextButton);
                assert(create.Step == 4 && create.Review.Text.Contains("worker@example.com") && create.Review.Text.Contains("feature/preparation") && create.Review.Text.Contains("Gestionnaires de paquets") && create.Review.Text.Contains(create.Install.Text), "récapitulatif complet montre compte, dépôts, références, réseau et instructions");
                Click(create.NextButton); assert(sends == 0 && !create.NextButton.IsEnabled, "double clic ne traverse pas la confirmation pour créer un environnement");
                create.UpdateLayout(); assert(FooterFits(create) && create.Review.VerticalContentAlignment == VerticalAlignment.Top, "assistant environnement 680×580 garde les actions visibles et le résumé aligné en haut"); await Snapshot(create, "Cloud-environment-wizard-review.png");
                Click(create.BackButton); Click(create.BackButton); assert(create.Step == 2 && create.Install.Text.Contains("unittest") && create.Directory.Text == "/workspace/laboratoire", "retour aux réglages préserve commandes, instructions et dossier");
                Click(create.NextButton); Click(create.NextButton); await Until(() => create.NextButton.IsEnabled); Click(create.NextButton); await Until(() => sends == 1); Click(create.NextButton);
                assert(sends == 1 && create.IntentLocked && create.Submitting && !create.BackButton.IsEnabled && created.Account.Id == "worker" && created.Account.AccountKey == "account-worker" && created.Create.ShareSettings == "private" && !created.Create.StartOnboarding && created.Create.Repositories.Count == 2 && created.Create.Cwd == "/workspace/laboratoire", "envoi unique conserve compte, périmètre multi dépôts, mode privé et préparation explicite");
                create.Close(); assert(create.IsVisible, "fermeture bloquée pendant un enregistrement en cours"); release.SetResult(true); await Until(() => create.Saved);
            }
            finally { release.TrySetResult(true); create.ConfirmDiscard = () => true; if (create.IsVisible) create.Close(); late.TrySetResult(new CloudGitHubRepository[0]); }

            int attempts = 0;
            var retry = new CloudEnvironmentWizard(shell, accounts, (account, token) => { if (++attempts == 1) throw new InvalidOperationException("GitHub fictif indisponible · " + new string('E', 5000)); return Task.FromResult(attempts == 2 ? new CloudGitHubRepository[0] : catalog); }, (intent, token) => Task.CompletedTask, CancellationToken.None, "worker") { Opacity = 0, ShowInTaskbar = false, Width = 680, Height = 580 };
            try
            {
                retry.Show(); await Until(() => !retry.Loading); assert(retry.Status.Text.Contains("GitHub fictif") && retry.RetryButton.IsEnabled && !retry.NextButton.IsEnabled, "échec GitHub affiche une reprise sans faux catalogue vide");
                retry.UpdateLayout(); assert(retry.Status.ActualHeight <= 58 && ((retry.Status.ToolTip as string)?.Length ?? 0) > 5000 && FooterFits(retry), "erreur GitHub longue est bornée avec diagnostic complet en infobulle et footer visible à680×580"); await Snapshot(retry, "Cloud-environment-wizard-error.png");
                Click(retry.RetryButton); await Until(() => attempts == 2 && !retry.Loading); assert(retry.Status.Text.Contains("Aucun dépôt") && !retry.NextButton.IsEnabled, "compte sans dépôt a une explication distincte de l’erreur réseau");
                Click(retry.RetryButton); await Until(() => attempts == 3 && !retry.Loading); retry.RepositoryRows.First().Selected = true; retry.RepositoryRows.First().Ref = "bad ref"; Click(retry.NextButton);
                assert(retry.Step == 1 && !retry.IntentLocked && retry.ErrorText.Text.Contains("référence"), "référence Git malformée refusée avant la mutation");
                retry.RepositoryRows.First().Ref = "refs/heads/valid"; Click(retry.NextButton); retry.EnvironmentName.Text = "Brouillon conservé";
                retry.ConfirmDiscard = () => false; retry.Close(); assert(retry.IsVisible, "abandon d’un brouillon demande confirmation sans perdre la saisie");
            }
            finally { retry.ConfirmDiscard = () => true; retry.Close(); }

            var ownerSuccess = new TaskCompletionSource<CloudGitHubRepository[]>();
            var differentBranches = new CloudEnvironmentWizard(shell, accounts, (account, token) => account.Id == "owner" ? ownerSuccess.Task : Task.FromResult(new[] { new CloudGitHubRepository { Id = "same-repository", FullName = "worker/application", DefaultBranch = "worker-branch" } }), (intent, token) => Task.CompletedTask, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false };
            try
            {
                differentBranches.Show(); differentBranches.Account.SelectedIndex = 0; differentBranches.Search.Text = "ancienne recherche"; differentBranches.Account.SelectedIndex = 1; await Until(() => !differentBranches.Loading);
                ownerSuccess.SetResult(new[] { new CloudGitHubRepository { Id = "same-repository", FullName = "owner/application", DefaultBranch = "owner-branch" } }); await Task.Delay(25);
                assert(differentBranches.RepositoryRows.Single().Ref == "worker-branch" && differentBranches.RepositoryRows.Single().Name == "worker/application" && !differentBranches.RepositoryRows.Single().Selected && differentBranches.Search.Text == "" && differentBranches.Repositories.Items.Count == 1, "nouveau compte réinitialise le filtre et ne réutilise ni ancienne branche ni ancien nom d’un dépôt semblable");
            }
            finally { differentBranches.Close(); ownerSuccess.TrySetResult(new CloudGitHubRepository[0]); }

            var original = new CloudConfiguration { Id = "configuration-existing", Name = "Environnement existant", VersionId = "version-exact", VersionRevision = 4, IsOwner = true, Repositories = new List<CloudRepository> { new CloudRepository { RepositoryId = "repo-2", Ref = "refs/tags/v1.7", Depth = 7 }, new CloudRepository { RepositoryId = "hidden-repo", Ref = "aabbcc001122334455", Depth = 19 } }, NetworkPolicy = new CloudNetworkPolicy { Type = "restricted", Presets = new List<string>(), EgressHosts = new List<string> { "github.com", "private.example.com" } }, Cwd = null, InstallScript = "ancienne installation", StartSkill = "anciennes instructions" };
            CloudEnvironmentWizardIntent changed = null; int modifications = 0;
            var edit = new CloudEnvironmentWizard(shell, new[] { accounts[0] }, (account, token) => Task.FromResult(catalog), (intent, token) => { modifications++; changed = intent; throw new CloudBackendException("forbidden", "Refus fictif du service", 403); }, CancellationToken.None, "owner", original) { Opacity = 0, ShowInTaskbar = false, Width = 680, Height = 580 };
            try
            {
                edit.Show(); await Until(() => !edit.Loading); assert(edit.Step == 1 && !edit.Account.IsEnabled && edit.RepositoryRows.Count(row => row.Selected) == 2 && edit.RepositoryRows.Single(row => row.Id == "hidden-repo").Missing && edit.Status.Text.Contains("conservés"), "édition verrouille le propriétaire et conserve un dépôt absent du catalogue");
                assert(edit.RepositoryRows.Single(row => row.Id == "repo-2").Ref == "refs/tags/v1.7" && edit.RepositoryRows.Single(row => row.Id == "repo-2").Depth == 7, "catalogue GitHub ne remplace pas une référence exacte ni sa profondeur par la branche par défaut");
                Click(edit.NextButton); assert(edit.EnvironmentName.IsReadOnly && edit.Network.SelectedIndex == 0, "édition conserve le nom et la règle réseau par défaut"); Click(edit.NextButton); assert(edit.Step == 3, "édition sans changement préalable laisse accéder aux scripts de démarrage");
                edit.Install.Text = "nouvelle installation"; Click(edit.NextButton); assert(edit.Review.Text.Contains("private.example.com") && edit.Review.Text.Contains("Profondeur : 19") && edit.Review.Text.Contains("règle actuelle conservée"), "résumé édition décrit les destinations réseau personnalisées et les dépôts conservés");
                Click(edit.BackButton); Click(edit.BackButton); Click(edit.BackButton); edit.RepositoryRows.Single(row => row.Id == "repo-2").Ref = "refs/heads/new-branch"; Click(edit.NextButton); Click(edit.NextButton); Click(edit.NextButton);
                await Until(() => edit.NextButton.IsEnabled); Click(edit.NextButton); await Until(() => modifications == 1 && !edit.Submitting); Click(edit.NextButton);
                assert(!edit.Saved && edit.IntentLocked && !edit.NextButton.IsEnabled && !edit.BackButton.IsEnabled && modifications == 1 && edit.ErrorText.Text.Contains("Refus fictif"), "erreur de mutation connue fige la demande et interdit tout renvoi depuis l’assistant");
                assert(changed.Id == edit.IntentId && changed.ConfigurationId == original.Id && changed.Patch.BaseVersionId == "version-exact" && changed.Patch.ExpectedRevision == 0 && changed.Patch.NetworkPolicy == null && changed.Patch.Cwd == null && changed.Patch.Repositories.Count == 2 && changed.Patch.Repositories.Single(repo => repo.RepositoryId == "repo-2").Depth == 7 && changed.Patch.Repositories.Single(repo => repo.RepositoryId == "hidden-repo").Ref == "aabbcc001122334455", "patch modifie seulement le périmètre demandé et préserve compte, version, réseau, dossier null et profondeur");
                assert(edit.Review.Text.Contains("private.example.com") && FooterFits(edit), "refus de mutation conserve un résumé lisible et le footer compact");
            }
            finally { edit.ConfirmDiscard = () => true; edit.Close(); }

            var pending = new TaskCompletionSource<CloudGitHubRepository[]>(); bool closeCancelled = false;
            var closing = new CloudEnvironmentWizard(shell, accounts, (account, token) => { token.Register(() => closeCancelled = true); return pending.Task; }, (intent, token) => Task.CompletedTask, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false };
            closing.Show(); closing.Account.SelectedIndex = 0; closing.Close(); pending.SetResult(catalog); await Task.Delay(25);
            assert(closeCancelled && closing.RepositoryRows.Count == 0 && !closing.Saved, "fermeture annule la lecture GitHub et ignore sa réponse tardive");

            var emptyEdit = new CloudEnvironmentWizard(shell, new[] { accounts[0] }, (account, token) => Task.FromResult(new CloudGitHubRepository[0]), (intent, token) => Task.CompletedTask, CancellationToken.None, "owner", original) { Opacity = 0, ShowInTaskbar = false, Width = 680, Height = 580 };
            try
            {
                emptyEdit.Show(); await Until(() => !emptyEdit.Loading); assert(emptyEdit.RepositoryRows.Count == 2 && emptyEdit.RepositoryRows.All(row => row.Selected && row.Missing) && emptyEdit.NextButton.IsEnabled, "catalogue vide à l’édition ne supprime silencieusement aucun dépôt existant");
                Click(emptyEdit.NextButton); Click(emptyEdit.NextButton); Click(emptyEdit.NextButton); assert(emptyEdit.Step == 3 && !emptyEdit.IntentLocked && emptyEdit.ErrorText.Text.Contains("au moins"), "modification vide est refusée avant gel sans empêcher l’accès aux instructions");
                emptyEdit.Install.Text = new string('x', 3000); emptyEdit.Start.Text = new string('y', 3000); Click(emptyEdit.NextButton); emptyEdit.UpdateLayout();
                assert(FooterFits(emptyEdit) && emptyEdit.Review.Text.Contains(new string('x', 3000)), "contenus de démarrage longs restent dans un unique résumé défilant sans repousser les actions"); await Snapshot(emptyEdit, "Cloud-environment-wizard-edit.png");
            }
            finally { emptyEdit.ConfirmDiscard = () => true; emptyEdit.Close(); }

            foreach (var failure in new Exception[] { new CloudBackendUncertainException("connexion fictive interrompue"), new IOException("Échec local après réponse serveur") })
            {
                int uncertainSends = 0;
                var uncertain = new CloudEnvironmentWizard(shell, accounts, (account, token) => Task.FromResult(catalog), (intent, token) => { uncertainSends++; throw failure; }, CancellationToken.None, "worker") { Opacity = 0, ShowInTaskbar = false };
                try
                {
                    uncertain.Show(); await Until(() => !uncertain.Loading); uncertain.RepositoryRows.First().Selected = true; Click(uncertain.NextButton); uncertain.EnvironmentName.Text = "Résultat incertain"; Click(uncertain.NextButton); Click(uncertain.NextButton); await Until(() => uncertain.NextButton.IsEnabled); Click(uncertain.NextButton); await Until(() => uncertainSends == 1 && !uncertain.Submitting); Click(uncertain.NextButton);
                    assert(uncertain.IntentLocked && !uncertain.Saved && !uncertain.NextButton.IsEnabled && uncertainSends == 1 && Descendants(uncertain).OfType<TextBlock>().Any(text => text.Text.Contains("Résultat à vérifier") && text.Text.Contains(uncertain.IntentId)), "mutation " + failure.GetType().Name + " affiche son résultat incertain et sa référence sans répétition possible");
                }
                finally { uncertain.ConfirmDiscard = () => true; uncertain.Close(); }
            }
        }
    }
}
