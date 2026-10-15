using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
        private sealed class CloudUiHandler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
            internal int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++;
                return Reply(request, token);
            }
        }

        private static async Task CloudAcceptance(DesktopContext context, ShellWindow shell, InstancesPage instances, DesktopInstance owner, string root, Action<bool, string> assert)
        {
            const string first = "11111111-2222-3333-4444-555555555555~asenvcfg_first";
            const string second = "11111111-2222-3333-4444-555555555555~asenvcfg_second";
            const string added = "11111111-2222-3333-4444-555555555555~asenvcfg_added";
            const string secret = "PRIVATE_CLOUD_ERROR_MUST_NOT_BE_DISPLAYED";
            var names = new Dictionary<string, string> { { first, "Brouillon cloud" }, { second, "Autre environnement" } };
            object Configuration(string id) => new { id, name = names[id], is_owner = true, share_settings = "private", status = "pending", version_id = "11111111-2222-3333-4444-555555555555~cecfgver_fixture", version_revision = 1, repositories = new[] { new { repository_id = "github/123", @ref = "refs/heads/main", depth = 1 } }, network_policy = new { type = "disabled" } };
            HttpResponseMessage Response(object value, HttpStatusCode code = HttpStatusCode.OK) => new HttpResponseMessage(code) { Content = new StringContent(Json.Write(value), Encoding.UTF8, "application/json") };
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> catalog = (request, token) =>
            {
                string query = request.RequestUri.Query;
                if (query.Contains("scope=user")) return Task.FromResult(Response(new { data = names.Keys.Select(Configuration).Take(1).ToArray(), next_cursor = "page-2" }));
                if (query.Contains("scope=workspace")) return Task.FromResult(Response(new { data = names.Keys.Select(Configuration).ToArray(), next_cursor = (string)null }));
                return Task.FromResult(Response(Configuration(Uri.UnescapeDataString(request.RequestUri.Segments.Last()))));
            };
            // Page two must terminate; workspace intentionally repeats the first entry.
            var handler = new CloudUiHandler { Reply = (request, token) => request.RequestUri.Query.Contains("cursor=")
                ? Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null })) : catalog(request, token) };
            var identity = new AuthIdentity { Key = "cloud-fixture:user", AccountId = "cloud-fixture", AccessToken = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { { "exp", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 }, { "https://api.openai.com/auth", new { chatgpt_account_id = "cloud-fixture", chatgpt_user_id = "user" } } }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture" };
            using (var http = new HttpClient(handler))
            {
                instances.ShowTab(3);
                shell.UpdateLayout();
                await Task.Delay(30);
                var cloud = Descendants(instances).OfType<CloudEnvironmentPanel>().Single();
                TextBlock Status() => Descendants(cloud).OfType<TextBlock>().Single(text => AutomationProperties.GetName(text) == "État de la connexion Codex Cloud");
                Button Button(string label) => Descendants(cloud).OfType<Button>().Single(button => Object.Equals(button.Content, label));
                assert(Status().Text.Contains("Aucun appel Cloud") && handler.Requests == 0, "onglet Cloud de recette ne contacte jamais les comptes réels");
                cloud.ClientFactory = () => new CloudBackend(http, () => identity, CloudBackend.Capture(identity));
                cloud.GitHubFixture = token => Task.FromResult(new CloudGitHubRepositoryPage { Data = new List<CloudGitHubRepository> { new CloudGitHubRepository { Id = "github/123", FullName = "fixture/project", DefaultBranch = "main" } }, Page = 1, HasNextPage = false });
                await cloud.Reload(first);
                shell.UpdateLayout();
                assert(cloud.EnvironmentChoice.Items.Count == 2 && handler.Requests == 4, "Cloud charge les pages des deux périmètres et déduplique leurs environnements");
                assert(Descendants(cloud).OfType<TextBlock>().Any(text => text.Text.Contains("Instance : " + owner.Name) && text.Text.Contains("@example.com")), "Cloud conserve le compte et le nom de l’instance visibles");
                assert(Descendants(cloud).OfType<TextBlock>().Any(text => text.Text == "Brouillon enregistré · préparation à terminer") && !Descendants(cloud).OfType<TextBlock>().Any(text => text.Text.Contains("publié avec succès")), "configuration initiale affichée comme brouillon sans promesse de publication");
                assert(Button("Créer un brouillon Cloud").IsEnabled && Button("Renommer l’environnement").IsEnabled, "actions Cloud accessibles après relecture réussie");
                assert(Descendants(cloud).OfType<TextBlock>().Any(text => AutomationProperties.GetName(text) == "État des dépôts GitHub de cette instance" && text.Text.Contains("1 dépôt(s)")), "catalogue GitHub appartient au compte de l’instance affichée");
                Capture(shell, Path.Combine(root, "Instance-cloud.png"));

                int before = handler.Requests;
                handler.Reply = (request, token) => Task.FromResult(Response(new { error = new { code = "denied", message = secret } }, HttpStatusCode.Forbidden));
                Button("Actualiser le Cloud").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => Status().Text.Contains("403") && Button("Actualiser le Cloud").IsEnabled);
                assert(handler.Requests == before + 1 && cloud.EnvironmentChoice.Items.Count == 2 && !Status().Text.Contains(secret), "erreur Cloud expliquée sans secret ni relance et sans effacer la liste précédente");

                var waiting = new TaskCompletionSource<bool>();
                handler.Reply = async (request, token) => { waiting.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return Response(new { data = new object[0] }); };
                var reading = cloud.Reload();
                await waiting.Task;
                assert(!Button("Actualiser le Cloud").IsEnabled && !cloud.EnvironmentChoice.IsEnabled && cloud.CanLeave(), "lecture Cloud désactive les doubles actions et reste annulable");
                try { await reading; } catch (OperationCanceledException) { }
                handler.Reply = (request, token) => request.RequestUri.Query.Contains("cursor=") ? Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null })) : catalog(request, token);
                await cloud.Reload(first);

                handler.Reply = (request, token) => Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null }));
                await cloud.Reload();
                assert(cloud.EnvironmentChoice.Items.Count == 0 && Button("Créer un brouillon Cloud").IsEnabled, "premier environnement peut être créé depuis GitHub sans configuration Cloud préexistante");
                handler.Reply = (request, token) => request.RequestUri.Query.Contains("cursor=") ? Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null })) : catalog(request, token);
                await cloud.Reload(first);

                int created = 0;
                var operationIds = new List<string>();
                cloud.CreateFixture = (request, id, token) =>
                {
                    created++;
                    operationIds.Add(id);
                    assert(request.Name == "Création contrôlée" && request.ShareSettings == "private" && !request.StartOnboarding && request.NetworkPolicy.Type == "disabled" && request.Repositories.Single().RepositoryId == "github/123", "formulaire Cloud n’élargit ni partage ni réseau et ne lance aucun agent");
                    names[added] = request.Name;
                    return Task.FromResult(new CloudConfiguration { Id = added, Name = request.Name });
                };
                async Task Dialog(string title, string button, Func<EditWindow, Task> inspect)
                {
                    var done = new TaskCompletionSource<bool>();
                    _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        EditWindow form = null;
                        try { form = await WaitDialog(title); await inspect(form); done.SetResult(true); }
                        catch (Exception error) { done.SetException(error); }
                        finally { if (form != null && !form.Saved) { form.ConfirmDiscard = () => true; form.Close(); } }
                    }), DispatcherPriority.ContextIdle);
                    Button(button).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await done.Task;
                    await Until(() => Button("Actualiser le Cloud").IsEnabled);
                }
                await Dialog("Créer un brouillon Codex Cloud", "Créer un brouillon Cloud", async form =>
                {
                    var input = Descendants(form).OfType<TextBox>().Single();
                    input.Text = "Création contrôlée";
                    assert(Descendants(form).OfType<ComboBox>().Single(choice => AutomationProperties.GetName(choice) == "Accès réseau initial").SelectedIndex == 0, "création permet un choix réseau explicite et garde le réseau désactivé par défaut");
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => form.Saved);
                });
                await Until(() => cloud.EnvironmentChoice.Items.Count == 3 && Status().Text.Contains("créé et relu"));
                assert(created == 1 && operationIds.Single().Length == 32, "validation du formulaire envoie une seule intention identifiée avant relecture");

                int renamed = 0;
                cloud.RenameFixture = (id, name, operationId, token) =>
                {
                    renamed++;
                    assert(id == added && name == "Nom confirmé", "renommage Cloud cible uniquement l’environnement sélectionné");
                    names[id] = name;
                    return Task.FromResult(new CloudConfiguration { Id = id, Name = name });
                };
                await Dialog("Renommer l’environnement Cloud", "Renommer l’environnement", async form =>
                {
                    Descendants(form).OfType<TextBox>().Single().Text = "Nom confirmé";
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => form.Saved);
                });
                await Until(() => Status().Text.Contains("Nom enregistré et relu"));
                assert(renamed == 1 && Descendants(cloud).OfType<TextBlock>().Any(text => text.Text == "Nom confirmé"), "renommage confirmé seulement après relecture du catalogue et du détail");

                int refused = 0;
                cloud.CreateFixture = (request, id, token) => { refused++; throw new CloudBackendException("fixture", "Refus de recette", 400); };
                await Dialog("Créer un brouillon Codex Cloud", "Créer un brouillon Cloud", async form =>
                {
                    var input = Descendants(form).OfType<TextBox>().Single();
                    input.Text = "Création refusée";
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => form.SaveButton.Visibility == Visibility.Collapsed);
                    assert(!form.Saved && input.IsReadOnly && refused == 1 && Descendants(form).OfType<TextBlock>().Any(text => text.Text.Contains("nouvelle demande corrigée")), "échec connu ferme la possibilité de renvoi et explique comment corriger une nouvelle intention");
                });

                int uncertain = 0;
                cloud.CreateFixture = (request, id, token) => { uncertain++; throw new CloudBackendUncertainException("réponse perdue"); };
                await Dialog("Créer un brouillon Codex Cloud", "Créer un brouillon Cloud", async form =>
                {
                    Descendants(form).OfType<TextBox>().Single().Text = "Résultat incertain";
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => form.SaveButton.Visibility == Visibility.Collapsed);
                    assert(!form.Saved && uncertain == 1 && Descendants(form).OfType<TextBlock>().Any(text => text.Text.Contains("Référence de suivi")), "création incertaine bloque tout nouvel envoi et expose un rapprochement explicite");
                });

                int preparations = 0, follows = 0, publications = 0;
                string setupId = null;
                cloud.SetupStartFixture = (id, repositories, operationId, token) =>
                {
                    preparations++; setupId = operationId;
                    assert(id == added && repositories.Count == 1 && repositories["github/123"] == "fixture/project" && operationId.Length == 32, "préparation conserve le compte sélectionné et le nom GitHub explicitement confirmé");
                    return Task.FromResult(new CloudEnvironmentSetupResult { Id = operationId, ConfigurationId = id, ThreadId = "setup-thread", TurnId = "setup-turn", State = "running", Stage = "setup-running" });
                };
                cloud.SetupFollowFixture = (id, token) =>
                {
                    follows++;
                    assert(id == setupId, "suivi réutilise uniquement l’identifiant de préparation existant");
                    return Task.FromResult(new CloudEnvironmentSetupResult { Id = id, ConfigurationId = added, ThreadId = "setup-thread", TurnId = "setup-turn", State = publications == 0 ? "ready-to-publish" : "completed", Stage = publications == 0 ? "setup-completed" : "completed" });
                };
                cloud.SetupPublishFixture = (id, token) =>
                {
                    publications++;
                    assert(id == setupId, "publication cible la préparation existante sans recréer de chat");
                    return Task.FromResult(new CloudEnvironmentSetupResult { Id = id, ConfigurationId = added, ThreadId = "setup-thread", TurnId = "setup-turn", State = "publishing", Stage = "approval-pending" });
                };
                assert(!Button("Modifier et publier…").IsEnabled && Button("Préparer l’environnement…").IsEnabled, "révision initiale propose préparation et protège la modification d’une version non publiée");
                await Dialog("Préparer l’environnement Codex Cloud", "Préparer l’environnement…", async form =>
                {
                    assert(!Descendants(form).OfType<TextBox>().Any() && Descendants(form).OfType<TextBlock>().Any(text => text.Text.Contains("fixture/project")), "préparation utilise le nom GitHub fourni par le catalogue sans saisie d’identifiant technique");
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => form.Saved);
                });
                await Until(() => Status().Text.Contains("Préparation en cours"));
                assert(preparations == 1 && publications == 0 && !Descendants(cloud).OfType<Button>().Any(button => Object.Equals(button.Content, "Publier l’environnement")), "démarrer une préparation ne publie pas automatiquement l’environnement");
                Button("Suivre la préparation").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => Descendants(cloud).OfType<Button>().Any(button => Object.Equals(button.Content, "Publier l’environnement")));
                assert(follows == 1 && publications == 0, "fin de préparation propose une publication explicitement séparée");
                Button("Publier l’environnement").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => Status().Text.Contains("Publication de l’environnement en cours"));
                Button("Suivre la préparation").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => Status().Text.Contains("publication vérifiée"));
                assert(preparations == 1 && publications == 1 && follows == 2, "suivi terminé ne rejoue ni chat ni prompt ni publication");
                instances.ShowTab(1);
                shell.UpdateLayout();
            }
        }
    }
}
