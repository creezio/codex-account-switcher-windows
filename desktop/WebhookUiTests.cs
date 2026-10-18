using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    // Runs on the UI test runner's STA thread. No real vault, process, network,
    // tunnel, conversation or model is used by these checks.
    internal static class WebhookUiTests
    {
        internal static void RunAll(Action<string, Action> check)
        {
            Action<bool, string> assert = (value, message) => { if (!value) throw new InvalidOperationException(message); };
            var choice = new WebhookInstanceChoice { Id = new string('a', 32), AccountKey = new string('b', 64), Home = Path.GetTempPath(), Label = "Atelier de recette · agent@example.invalid" };
            Func<WebhookRouteWizard> create = () => new WebhookRouteWizard(null, new[] { choice }, null, value => Task.FromResult(value)) { ConfirmDiscard = () => true };
            check("webhook assistant suggère une adresse lisible sans accents", () => {
                assert(WebhookRouteWizard.SuggestSlug("Validation des factures été 2026") == "validation-des-factures-ete-2026", "Adresse mal normalisée.");
                string longName = WebhookRouteWizard.SuggestSlug(new string('a', 63) + " bla"); assert(longName.Length <= 64 && !longName.EndsWith("-"), "Adresse tronquée avec tiret final.");
            });
            check("webhook assistant commence par le nom et conserve les champs au retour", () => {
                var wizard = create(); try {
                    assert(wizard.Step == 0 && !wizard.BackButton.IsEnabled, "L’assistant commence au mauvais endroit.");
                    wizard.RouteName.Text = "Validation factures"; assert(wizard.Slug.Text == "validation-factures", "Adresse non proposée."); wizard.Next().GetAwaiter().GetResult();
                    assert(wizard.Step == 1, "La destination doit suivre le nom."); wizard.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    assert(wizard.Step == 0 && wizard.RouteName.Text == "Validation factures", "Le retour perd le nom.");
                } finally { wizard.Close(); }
            });
            check("webhook assistant refuse les adresses ambiguës et n’enregistre rien", () => {
                int saves = 0; var wizard = new WebhookRouteWizard(null, new[] { choice }, null, value => { saves++; return Task.FromResult(value); }) { ConfirmDiscard = () => true };
                try { wizard.RouteName.Text = "Recette"; wizard.Slug.Text = "../admin"; ExpectRejected(wizard.Next); assert(wizard.Step == 0 && saves == 0, "Une mauvaise adresse a progressé."); } finally { wizard.Close(); }
            });
            check("webhook assistant exige une instance et un dossier existant", () => {
                var wizard = create(); try {
                    wizard.RouteName.Text = "Mission recette"; wizard.Next().GetAwaiter().GetResult(); ExpectRejected(wizard.Next);
                    wizard.Instance.SelectedItem = choice; wizard.Workspace.Text = "dossier-relatif"; ExpectRejected(wizard.Next);
                    wizard.Workspace.Text = Path.GetTempPath(); wizard.Next().GetAwaiter().GetResult(); assert(wizard.Step == 2, "Une destination valide ne progresse pas.");
                } finally { wizard.Close(); }
            });
            check("webhook assistant normalise le lien de conversation ciblée", () => {
                const string thread = "01a11111-2222-7333-8444-555555555555";
                assert(WebhookRouteWizard.ThreadId("codex://threads/" + thread + "?view=chat") == thread, "Le lien garde ses paramètres.");
                var wizard = create(); try {
                    ToDestination(wizard, choice); wizard.Conversation.SelectedIndex = 1; wizard.Thread.Text = "autre-compte"; ExpectRejected(wizard.Next);
                    wizard.Thread.Text = "codex://threads/" + thread; wizard.Next().GetAwaiter().GetResult(); assert(wizard.Step == 3, "Un lien valide est refusé.");
                } finally { wizard.Close(); }
            });
            check("webhook création épingle le compte le dossier et les permissions sans réseau", () => {
                int saves = 0; WebhookRoute submitted = null;
                var wizard = new WebhookRouteWizard(null, new[] { choice }, null, value => { saves++; submitted = value; return Task.FromResult(value); }) { ConfirmDiscard = () => true };
                try {
                    ToDestination(wizard, choice); wizard.Next().GetAwaiter().GetResult(); wizard.Instructions.Text = "Rester dans ce dossier et retourner les vérifications.";
                    wizard.Execution.SelectedIndex = 1; wizard.Permission.SelectedIndex = 1; wizard.Next().GetAwaiter().GetResult();
                    assert(saves == 1 && wizard.SavedRoute == submitted, "Enregistrement répété ou absent.");
                    assert(submitted.InstanceId == choice.Id && submitted.AccountKey == choice.AccountKey && submitted.Home == choice.Home && submitted.Workspace == Path.GetFullPath(Path.GetTempPath()), "Les destinations ne sont pas épinglées.");
                    assert(submitted.Access == "headless" && submitted.PermissionProfile == "full-access" && submitted.Model == "gpt-6.1-sol" && submitted.ReasoningEffort == "ultra", "Le mode choisi n’est pas enregistré.");
                    assert(submitted.ThreadId == null && String.IsNullOrEmpty(submitted.SecretHash), "La création ne doit ni choisir un ancien chat ni inventer un secret.");
                } finally { if (wizard.SavedRoute == null) wizard.Close(); }
            });
            check("webhook réglages par défaut respectent les permissions natives", () => {
                var wizard = create(); try { assert(wizard.Permission.SelectedIndex == 0 && wizard.Execution.SelectedIndex == 0 && !wizard.Thread.IsEnabled, "Des permissions ou un mode sans fenêtre sont imposés automatiquement."); } finally { wizard.Close(); }
            });
            check("webhook édition conserve identité révision et empreinte privée", () => {
                var original = new WebhookRoute { Id = new string('c', 32), Revision = 7, Name = "Recette", Slug = "recette", InstanceId = choice.Id, AccountKey = choice.AccountKey, Home = choice.Home, Workspace = Path.GetTempPath(), SecretHash = "empreinte-fictive-ne-pas-afficher", Access = "desktop", PermissionProfile = "inherit", Enabled = false, Model = "gpt-6.1-sol", ReasoningEffort = "ultra" };
                WebhookRoute submitted = null; var wizard = new WebhookRouteWizard(null, new[] { choice }, original, value => { submitted = value; return Task.FromResult(value); }) { ConfirmDiscard = () => true };
                try {
                    wizard.RouteName.Text = "Recette renommée"; wizard.Next().GetAwaiter().GetResult(); wizard.Next().GetAwaiter().GetResult(); wizard.Next().GetAwaiter().GetResult(); wizard.Next().GetAwaiter().GetResult();
                    assert(submitted.Id == original.Id && submitted.Revision == 7 && submitted.SecretHash == original.SecretHash && !submitted.Enabled, "L’édition remplace l’identité ou active la route.");
                    assert(original.Name == "Recette", "L’édition modifie l’objet fourni avant l’enregistrement.");
                } finally { if (wizard.SavedRoute == null) wizard.Close(); }
            });
            check("webhook états distinguent file attente suivi et envoi incertain", () => {
                assert(WebhooksPage.StateLabel("waiting").Contains("attente") && WebhooksPage.StateLabel("monitoring").Contains("suivi"), "Les états réels ne sont pas traduits.");
                assert(WebhooksPage.StateLabel("uncertain").Contains("ne pas réessayer") && WebhooksPage.StateLabel("completed") == "Terminée", "Un envoi incertain ressemble à une réussite.");
            });
            check("webhook exemples utilisent clé privée variable et idempotence", () => {
                const string url = "https://hooks.example.invalid/v1/hooks/recette/jobs";
                string curl = WebhooksPage.CurlExample(url), powershell = WebhooksPage.PowerShellExample(url);
                assert(curl.Contains("Idempotency-Key: commande-123") && curl.Contains("Bearer VOTRE_CLE") && !curl.Contains("\n+"), "L’exemple cURL est invalide.");
                assert(powershell.Contains("$env:SWITCHER_WEBHOOK_KEY") && powershell.Contains("Idempotency-Key") && powershell.Contains(url), "L’exemple PowerShell conserve un secret ou perd l’adresse.");
                assert(!powershell.Contains("accountKey") && !curl.Contains("instanceId"), "Le payload permet de choisir une autre destination.");
            });
            check("webhook lecture des projets manquants reste locale et vide", () => {
                assert(WebhookRouteWizard.ReadProjects(Path.Combine(Path.GetTempPath(), "switcher-webhook-ui-never-created-" + Guid.NewGuid().ToString("N"))).Length == 0, "Un dossier absent provoque une lecture distante.");
            });
        }
        private static void ToDestination(WebhookRouteWizard wizard, WebhookInstanceChoice choice)
        { wizard.RouteName.Text = "Mission recette"; wizard.Next().GetAwaiter().GetResult(); wizard.Instance.SelectedItem = choice; wizard.Workspace.Text = Path.GetTempPath(); wizard.Next().GetAwaiter().GetResult(); }
        private static void ExpectRejected(Func<Task> action)
        { try { action().GetAwaiter().GetResult(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("La saisie invalide aurait dû être refusée."); }
    }
}
