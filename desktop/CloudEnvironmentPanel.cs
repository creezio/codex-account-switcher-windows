using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudEnvironmentPanel : Grid, IDisposable
    {
        private readonly DesktopContext context;
        private readonly ShellWindow shell;
        private readonly DesktopInstance owner;
        private readonly TextBlock account = Ui.Text("", 13, true);
        private readonly TextBlock status = Ui.Text("", 13, true);
        private readonly TextBlock github = Ui.Text("", 12, true);
        private readonly StackPanel details = new StackPanel();
        private readonly Button refresh, create;
        internal readonly ComboBox EnvironmentChoice = new ComboBox { DisplayMemberPath = "Name" };
        private readonly List<CloudConfiguration> configurations = new List<CloudConfiguration>();
        private readonly List<CloudGitHubRepository> repositories = new List<CloudGitHubRepository>();
        private readonly Dictionary<string, CloudEnvironmentUpdateResult> updates = new Dictionary<string, CloudEnvironmentUpdateResult>(StringComparer.Ordinal);
        private readonly Dictionary<string, CloudEnvironmentSetupResult> setups = new Dictionary<string, CloudEnvironmentSetupResult>(StringComparer.Ordinal);
        private CancellationTokenSource operation;
        private bool busy, mutating, changing, disposed, loaded;
        private CloudConfiguration selected;
        private string email;
        internal Func<CloudBackend> ClientFactory = null;
        internal Func<CloudConfigurationCreate, string, CancellationToken, Task<CloudConfiguration>> CreateFixture = null;
        internal Func<string, string, string, CancellationToken, Task<CloudConfiguration>> RenameFixture = null;
        internal Func<string, IDictionary<string, string>, string, CancellationToken, Task<CloudEnvironmentSetupResult>> SetupStartFixture = null;
        internal Func<string, CancellationToken, Task<CloudEnvironmentSetupResult>> SetupFollowFixture = null;
        internal Func<string, CancellationToken, Task<CloudEnvironmentSetupResult>> SetupPublishFixture = null;
        internal Func<CancellationToken, Task<CloudGitHubRepositoryPage>> GitHubFixture = null;

        internal CloudEnvironmentPanel(DesktopContext context, ShellWindow shell, DesktopInstance owner, string email)
        {
            this.context = context;
            this.shell = shell;
            this.owner = owner;
            this.email = email;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            var top = new StackPanel();
            Children.Add(top);
            top.Children.Add(Ui.Text("Environnements Codex Cloud", 20));
            top.Children.Add(account);
            UpdateOwnerName(owner.Name, email);
            var actions = Ui.Actions(top);
            refresh = Ui.AsyncButton("Actualiser le Cloud", () => Reload(), ShowError);
            create = Ui.AsyncButton("Créer un brouillon Cloud", Create, ShowError, true);
            actions.Children.Add(refresh);
            actions.Children.Add(create);
            top.Children.Add(Ui.Text("Environnement", 13));
            AutomationProperties.SetName(EnvironmentChoice, "Environnement Cloud de cette instance");
            top.Children.Add(EnvironmentChoice);
            AutomationProperties.SetName(status, "État de la connexion Codex Cloud");
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            top.Children.Add(status);
            AutomationProperties.SetName(github, "État des dépôts GitHub de cette instance");
            top.Children.Add(github);
            var scroll = new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            SetRow(scroll, 1);
            Children.Add(scroll);
            EnvironmentChoice.SelectionChanged += async delegate
            {
                if (changing || busy || disposed) return;
                try { await ReadSelected(); } catch (Exception error) { ShowError(error); }
            };
            Loaded += async delegate
            {
                if (loaded || disposed) return;
                loaded = true;
                try { await Reload(); } catch (Exception error) { ShowError(error); }
            };
            UpdateButtons();
            Render();
        }

        internal void UpdateOwnerName(string name, string accountEmail)
        {
            owner.Name = name;
            email = accountEmail;
            account.Text = "Compte : " + (email ?? "À connecter") + " · Instance : " + owner.Name;
        }

        private DesktopInstance Current(AccountService accounts)
        {
            var instance = accounts.Data.Instances.SingleOrDefault(value => value.Id == owner.Id && !value.Archived);
            if (instance == null || instance.AccountKey != owner.AccountKey)
                throw new InvalidOperationException("Le compte de cette instance a changé. Revenez à la liste des instances avant de continuer.");
            return instance;
        }

        private async Task<CloudBackend> Open()
        {
            if (ClientFactory != null) return ClientFactory();
            if (context.Fixture) throw new InvalidOperationException("Connexion Cloud non configurée dans cette recette.");
            return await context.Read(accounts => CloudInstanceConnection.Open(accounts, Current(accounts)));
        }

        internal async Task Reload(string preferredId = null)
        {
            if (busy || disposed) return;
            if (context.Fixture && ClientFactory == null)
            {
                status.Text = "Aucun appel Cloud n’est effectué avec les comptes fictifs de cette recette.";
                return;
            }
            var keepId = preferredId ?? selected?.Id;
            Begin("Lecture des environnements du compte " + email + "…");
            try
            {
                var found = new List<CloudConfiguration>();
                using (var client = await Open())
                {
                    foreach (var scope in new[] { "user", "workspace" })
                    {
                        string cursor = null;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        do
                        {
                            var page = await client.List(scope, cursor, operation.Token);
                            if (page.Data != null) found.AddRange(page.Data);
                            cursor = page.NextCursor;
                            if (!String.IsNullOrEmpty(cursor) && !seen.Add(cursor))
                                throw new InvalidOperationException("Le Cloud a renvoyé une page déjà reçue. La liste précédente est conservée.");
                            if (seen.Count >= 50)
                                throw new InvalidOperationException("Le catalogue est trop volumineux pour être chargé complètement. La liste précédente est conservée.");
                        } while (!String.IsNullOrEmpty(cursor));
                    }
                    if (disposed) return;
                    configurations.Clear();
                    configurations.AddRange(found.Where(value => !String.IsNullOrWhiteSpace(value.Id)).GroupBy(value => value.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase));
                    selected = configurations.FirstOrDefault(value => value.Id == keepId) ?? configurations.FirstOrDefault();
                    changing = true;
                    EnvironmentChoice.ItemsSource = configurations.ToArray();
                    EnvironmentChoice.SelectedItem = selected;
                    changing = false;
                    if (selected != null) selected = await client.Get(selected.Id, operation.Token);
                }
                if (!context.Fixture)
                {
                    var recent = await context.Read(accounts => new { Updates = CloudEnvironmentUpdate.Recent(accounts, Current(accounts)), Setups = CloudEnvironmentSetup.Recent(accounts, Current(accounts)) });
                    updates.Clear();
                    foreach (var update in recent.Updates) if (!updates.ContainsKey(update.ConfigurationId)) updates.Add(update.ConfigurationId, update);
                    setups.Clear();
                    foreach (var setup in recent.Setups) if (!setups.ContainsKey(setup.ConfigurationId)) setups.Add(setup.ConfigurationId, setup);
                }
                await LoadRepositories();
                if (disposed) return;
                Render();
                status.Text = configurations.Count == 0
                    ? "Aucun environnement accessible à ce compte. Choisissez un dépôt GitHub pour créer le premier."
                    : configurations.Count + " environnement(s) lus sur le compte Cloud · " + DateTime.Now.ToString("HH:mm:ss");
            }
            finally { End(); }
        }

        private async Task LoadRepositories()
        {
            if (context.Fixture && GitHubFixture == null) return;
            try
            {
                var found = new List<CloudGitHubRepository>();
                if (context.Fixture) found.AddRange((await GitHubFixture(operation.Token)).Data);
                else
                {
                    using (var client = await context.Read(accounts => CloudInstanceConnection.OpenGitHub(accounts, Current(accounts))))
                    {
                        bool more = true;
                        for (int page = 1; page <= 50 && more; page++)
                        {
                            github.Text = "Lecture des dépôts GitHub du compte " + email + " · page " + page + "…";
                            var data = await client.ListRepositories(page, operation.Token);
                            found.AddRange(data.Data);
                            more = data.HasNextPage ?? data.Data.Count == 20;
                            if (page == 50 && more) throw new InvalidOperationException("Le catalogue dépasse 1 000 dépôts. La liste précédente est conservée.");
                        }
                    }
                }
                if (disposed) return;
                repositories.Clear();
                repositories.AddRange(found.GroupBy(value => value.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(value => value.FullName, StringComparer.CurrentCultureIgnoreCase));
                github.Text = repositories.Count == 0 ? "Aucun dépôt GitHub accessible à ce compte. Connectez GitHub dans Codex sur cette instance puis actualisez." : repositories.Count + " dépôt(s) GitHub accessibles au compte " + email + ". Les droits d’envoi de modifications seront vérifiés lors de chaque mission.";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                github.Text = "Lecture GitHub impossible : " + Program.SafeError(error) + " Actualisez après avoir vérifié la connexion GitHub de ce compte. La dernière liste complète est conservée.";
            }
        }

        private async Task ReadSelected()
        {
            if (!(EnvironmentChoice.SelectedItem is CloudConfiguration value)) return;
            Begin("Lecture de « " + value.Name + " »…");
            try
            {
                using (var client = await Open()) selected = await client.Get(value.Id, operation.Token);
                if (disposed) return;
                Render();
                status.Text = "Configuration relue sur le compte " + email + ".";
            }
            finally { End(); }
        }

        private void Render()
        {
            details.Children.Clear();
            if (selected == null)
            {
                details.Children.Add(Ui.Card(Ui.Text("Les environnements accessibles à cette instance apparaîtront ici après actualisation.", 14, true)));
                return;
            }
            var environment = selected;
            var summary = new StackPanel();
            summary.Children.Add(Ui.Text(environment.Name, 19));
            summary.Children.Add(Ui.Text(State(environment), 13, true));
            var actions = Ui.Actions(summary);
            var rename = Ui.AsyncButton("Renommer l’environnement", () => Rename(environment), ShowError);
            rename.IsEnabled = !busy && environment.IsOwner != false;
            if (environment.IsOwner == false)
                rename.ToolTip = "Ce prototype permet de renommer seulement les environnements dont ce compte est propriétaire.";
            actions.Children.Add(rename);
            var edit = Ui.AsyncButton("Modifier et publier…", () => Edit(environment), ShowError);
            updates.TryGetValue(environment.Id, out var update);
            bool initial = !environment.VersionRevision.HasValue || environment.VersionRevision.Value <= 1;
            edit.IsEnabled = !busy && !initial && environment.IsOwner == true && environment.Draft == null && (update == null || update.State == "completed" || update.State == "failed");
            if (!edit.IsEnabled) edit.ToolTip = initial ? "La préparation initiale de cet environnement doit être terminée avant de modifier une version publiée." : environment.Draft != null ? "Un brouillon existe déjà. Actualisez pour vérifier son état ; il ne sera pas remplacé." : "Le compte doit être propriétaire et aucune publication incertaine ne doit rester à rapprocher.";
            actions.Children.Add(edit);
            setups.TryGetValue(environment.Id, out var setup);
            if (initial)
            {
                var prepare = Ui.AsyncButton("Préparer l’environnement…", () => Setup(environment), ShowError, true);
                prepare.IsEnabled = !busy && environment.IsOwner == true && environment.Draft == null && String.IsNullOrEmpty(environment.ThreadId) && setup == null;
                if (!prepare.IsEnabled) prepare.ToolTip = setup != null ? "Suivez la préparation déjà créée pour cet environnement." : "Ce compte doit être propriétaire et aucune préparation ne doit exister.";
                actions.Children.Add(prepare);
            }
            if (setup != null)
            {
                summary.Children.Add(Ui.Text(SetupState(setup), 13, true));
                if (setup.State != "completed")
                {
                    var followSetup = Ui.AsyncButton("Suivre la préparation", () => FollowSetup(setup, false), ShowError);
                    followSetup.IsEnabled = !busy;
                    actions.Children.Add(followSetup);
                    if (setup.State == "ready-to-publish")
                    {
                        var publish = Ui.AsyncButton("Publier l’environnement", () => FollowSetup(setup, true), ShowError, true);
                        publish.IsEnabled = !busy;
                        actions.Children.Add(publish);
                    }
                }
            }
            if (update != null)
            {
                summary.Children.Add(Ui.Text(UpdateState(update), 13, true));
                if (update.State != "completed")
                {
                    var follow = Ui.AsyncButton("Suivre la publication", () => Follow(update), ShowError);
                    follow.IsEnabled = !busy;
                    actions.Children.Add(follow);
                }
            }
            summary.Children.Add(Ui.Text("Dépôts associés", 14));
            foreach (var repository in environment.Repositories ?? new List<CloudRepository>())
            {
                var known = repositories.FirstOrDefault(value => value.Id == repository.RepositoryId);
                summary.Children.Add(Ui.Text("GitHub · " + (known?.FullName ?? repository.RepositoryId) + " · " + (String.IsNullOrEmpty(repository.Ref) ? "référence du Cloud" : repository.Ref), 13, true));
            }
            if (environment.Repositories == null || environment.Repositories.Count == 0)
                summary.Children.Add(Ui.Text("Aucun dépôt renseigné dans cette configuration.", 13, true));
            details.Children.Add(Ui.Card(summary));
            var configuration = new StackPanel();
            configuration.Children.Add(Ui.Text("Préparation de l’environnement", 17));
            configuration.Children.Add(Ui.Text("Les modifications de préparation créent leur propre brouillon, puis demandent sa publication. Leur progression reste disponible dans cette instance après réouverture du Switcher.", 13, true));
            AddReadOnly(configuration, "Installation", environment.InstallScript);
            AddReadOnly(configuration, "Dossier de travail", environment.Cwd);
            AddReadOnly(configuration, "Instructions de démarrage", environment.StartSkill);
            details.Children.Add(Ui.Card(configuration));
            var diagnostic = new StackPanel();
            diagnostic.Children.Add(Ui.Text("Identifiant : " + environment.Id, 12, true));
            if (!String.IsNullOrEmpty(environment.VersionId)) diagnostic.Children.Add(Ui.Text("Version : " + environment.VersionId, 12, true));
            if (!String.IsNullOrEmpty(environment.EnvironmentId)) diagnostic.Children.Add(Ui.Text("Exécution : " + environment.EnvironmentId, 12, true));
            if (update != null) diagnostic.Children.Add(Ui.Text("Publication : " + update.Id + " · étape : " + update.Stage, 12, true));
            if (setup != null)
            {
                diagnostic.Children.Add(Ui.Text("Préparation : " + setup.Id + " · étape : " + setup.Stage, 12, true));
                if (!String.IsNullOrEmpty(setup.ThreadId)) diagnostic.Children.Add(Ui.Text("Conversation de préparation : " + setup.ThreadId, 12, true));
            }
            details.Children.Add(new Expander { Header = "Identifiants et diagnostic", Content = diagnostic });
        }

        private static void AddReadOnly(Panel panel, string label, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            panel.Children.Add(Ui.Text(label, 12, true));
            panel.Children.Add(new TextBox { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private static string State(CloudConfiguration environment)
        {
            if (environment.Draft != null) return "Brouillon de modification enregistré · publication non confirmée";
            if (environment.VersionRevision.HasValue && environment.VersionRevision.Value <= 1) return "Brouillon enregistré · préparation à terminer";
            if (!String.IsNullOrEmpty(environment.LatestReadyVersionId)) return "Une version prête est signalée par le Cloud";
            return "Configuration enregistrée" + (environment.VersionRevision.HasValue ? " · révision " + environment.VersionRevision.Value : "") + " · disponibilité d’exécution non vérifiée";
        }

        private static string UpdateState(CloudEnvironmentUpdateResult update)
        {
            if (update.State == "completed") return "Dernière publication vérifiée sur le compte Cloud.";
            if (update.State == "publishing") return "Publication en cours · actualisez son suivi pour connaître le résultat.";
            if (update.State == "verifying") return "Publication envoyée · disponibilité de la nouvelle version encore à vérifier.";
            if (update.State == "failed") return "Publication arrêtée · " + (update.FailureCode ?? "échec") + ". Actualisez avant une nouvelle modification.";
            return "Résultat de publication incertain · vérifiez son suivi avant toute nouvelle demande. Aucun nouvel envoi automatique.";
        }

        private static string SetupState(CloudEnvironmentSetupResult setup)
        {
            if (setup.State == "completed") return "Environnement préparé et publication vérifiée sur ce compte Cloud.";
            if (setup.State == "ready-to-publish") return "Le chat de préparation a terminé son tour. Vérifiez son résultat dans Codex avant de publier l’environnement.";
            if (setup.State == "awaiting-input") return "Le chat de préparation attend une réponse dans Codex. Reprenez-le sur ce compte avant de poursuivre.";
            if (setup.State == "awaiting-approval") return "Le chat de préparation attend une approbation dans Codex. Le Switcher ne peut pas la donner à votre place.";
            if (setup.State == "publishing") return "Publication de l’environnement en cours · consultez son suivi.";
            if (setup.State == "verifying") return "Publication envoyée · nouvelle version prête encore à vérifier.";
            if (setup.State == "running") return "Préparation en cours dans un chat Codex Cloud de ce compte · consultez son suivi.";
            if (setup.State == "failed") return "Préparation arrêtée · " + (setup.FailureCode ?? "échec") + ". Vérifiez le chat avant une nouvelle demande.";
            return "Résultat de préparation incertain · consultez le suivi. Aucun chat ni message ne sera recréé automatiquement.";
        }

        private async Task Setup(CloudConfiguration environment)
        {
            if (busy || disposed) return;
            var configured = environment.Repositories ?? new List<CloudRepository>();
            if (configured.Count == 0) throw new InvalidOperationException("Choisissez d’abord un dépôt pour cet environnement Cloud.");
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var repository in configured)
            {
                var known = repositories.SingleOrDefault(value => value.Id == repository.RepositoryId);
                if (known == null) throw new InvalidOperationException("Ce dépôt n’a pas été retrouvé dans le catalogue GitHub de ce compte. Actualisez avant de préparer l’environnement.");
                names.Add(repository.RepositoryId, known.FullName);
            }
            var form = new EditWindow(shell, "Préparer l’environnement Codex Cloud", "Démarrer la préparation");
            form.Fields.Children.Add(Ui.Text("Compte : " + email + " · Instance : " + owner.Name, 13, true));
            form.Fields.Children.Add(Ui.Text("Un nouveau chat Cloud préparera cet environnement. La publication restera une action séparée. Les dépôts ci-dessous ont été retrouvés dans le catalogue GitHub de ce compte.", 13, true));
            foreach (var repository in configured) form.Fields.Children.Add(Ui.Text(names[repository.RepositoryId] + " · " + repository.Ref, 14));
            string operationId = Guid.NewGuid().ToString("N");
            CloudEnvironmentSetupResult outcome = null;
            form.Save = async () =>
            {
                Begin("Création du chat de préparation Cloud…", true, TimeSpan.FromMinutes(3));
                try
                {
                    if (context.Fixture && SetupStartFixture != null) outcome = await SetupStartFixture(environment.Id, names, operationId, operation.Token);
                    else await context.Mutate(async accounts => { outcome = await CloudEnvironmentSetup.Start(accounts, Current(accounts), environment.Id, names, operationId, operation.Token); });
                    setups[environment.Id] = outcome;
                }
                catch
                {
                    setups[environment.Id] = new CloudEnvironmentSetupResult { Id = operationId, ConfigurationId = environment.Id, State = "uncertain", Stage = "À vérifier" };
                    ReconcileRequired(form, operationId);
                    throw;
                }
                finally { End(); }
            };
            form.ShowDialog();
            if (!form.Saved || outcome == null || disposed) return;
            await Reload(environment.Id);
            status.Text = SetupState(outcome);
        }

        private async Task FollowSetup(CloudEnvironmentSetupResult setup, bool publish)
        {
            if (busy || disposed) return;
            Begin(publish ? "Demande de publication de l’environnement Cloud…" : "Lecture du suivi de préparation Cloud…", true, TimeSpan.FromMinutes(15));
            CloudEnvironmentSetupResult outcome = null;
            try
            {
                if (context.Fixture && (publish ? SetupPublishFixture != null : SetupFollowFixture != null))
                    outcome = await (publish ? SetupPublishFixture(setup.Id, operation.Token) : SetupFollowFixture(setup.Id, operation.Token));
                else await context.Mutate(async accounts => { outcome = publish ? await CloudEnvironmentSetup.Publish(accounts, Current(accounts), setup.Id, operation.Token) : await CloudEnvironmentSetup.Follow(accounts, Current(accounts), setup.Id, operation.Token); });
                setups[setup.ConfigurationId] = outcome;
                if (outcome.Configuration != null && selected?.Id == outcome.ConfigurationId) selected = outcome.Configuration;
                status.Text = SetupState(outcome);
            }
            finally { End(); }
        }

        private async Task Edit(CloudConfiguration environment)
        {
            if (busy || disposed) return;
            var form = new EditWindow(shell, "Modifier et publier l’environnement Cloud", "Enregistrer et publier");
            form.Fields.Children.Add(Ui.Text("Compte : " + email + " · Instance : " + owner.Name, 13, true));
            form.Fields.Children.Add(Ui.Text("Ces réglages seront exécutés dans un environnement Cloud. La publication utilise un nouveau brouillon et s’arrête si la version affichée a changé.", 13, true));
            var install = Ui.Input("Commandes d’installation", environment.InstallScript ?? "", form.Fields, true);
            var cwd = Ui.Input("Dossier de travail Cloud (chemin absolu, par exemple /workspace)", environment.Cwd ?? "", form.Fields);
            var instructions = Ui.Input("Instructions de démarrage", environment.StartSkill ?? "", form.Fields, true);
            var network = Ui.Select("Accès réseau", new[] { "Conserver la règle actuelle", "Réseau désactivé", "Gestionnaires de paquets uniquement", "Accès réseau sans restriction" }, "Conserver la règle actuelle", form.Fields);
            form.Dirty = () => install.Text != (environment.InstallScript ?? "") || cwd.Text != (environment.Cwd ?? "") || instructions.Text != (environment.StartSkill ?? "") || network.SelectedIndex != 0;
            string operationId = Guid.NewGuid().ToString("N");
            CloudEnvironmentUpdateResult outcome = null;
            form.Save = async () =>
            {
                string installation = install.Text, directory = cwd.Text.Trim(), start = instructions.Text;
                var patch = new CloudDraftPatch { BaseVersionId = environment.VersionId, ExpectedRevision = 0 };
                if (installation != (environment.InstallScript ?? "")) patch.InstallScript = installation;
                if (directory != (environment.Cwd ?? ""))
                {
                    if (!directory.StartsWith("/", StringComparison.Ordinal)) throw new InvalidOperationException("Le dossier Cloud doit être un chemin absolu commençant par /.");
                    patch.Cwd = directory;
                }
                if (start != (environment.StartSkill ?? "")) patch.StartSkill = start;
                if (network.SelectedIndex == 1) patch.NetworkPolicy = new CloudNetworkPolicy { Type = "disabled" };
                if (network.SelectedIndex == 2) patch.NetworkPolicy = new CloudNetworkPolicy { Type = "restricted", Presets = new List<string> { "package_managers" }, EgressHosts = new List<string>() };
                if (network.SelectedIndex == 3) patch.NetworkPolicy = new CloudNetworkPolicy { Type = "unrestricted" };
                if (patch.InstallScript == null && patch.Cwd == null && patch.StartSkill == null && patch.NetworkPolicy == null) throw new InvalidOperationException("Modifiez au moins un réglage avant d’enregistrer.");
                install.IsReadOnly = cwd.IsReadOnly = instructions.IsReadOnly = true;
                network.IsEnabled = false;
                Begin("Enregistrement et préparation de la publication Cloud…", true, TimeSpan.FromMinutes(15));
                try
                {
                    await context.Mutate(async accounts => { outcome = await CloudEnvironmentUpdate.Update(accounts, Current(accounts), environment.Id, patch, operationId, operation.Token); });
                    updates[environment.Id] = outcome;
                }
                catch
                {
                    // The same intent can be followed, but it cannot silently become a new draft.
                    updates[environment.Id] = new CloudEnvironmentUpdateResult { Id = operationId, ConfigurationId = environment.Id, State = "uncertain", Stage = "À vérifier" };
                    ReconcileRequired(form, operationId);
                    throw;
                }
                finally { End(); }
            };
            form.ShowDialog();
            if (!form.Saved || outcome == null || disposed) return;
            await Reload(environment.Id);
            status.Text = UpdateState(outcome);
        }

        private async Task Follow(CloudEnvironmentUpdateResult update)
        {
            if (busy || disposed) return;
            Begin("Vérification de la publication Cloud…", true, TimeSpan.FromMinutes(15));
            CloudEnvironmentUpdateResult outcome = null;
            try
            {
                await context.Mutate(async accounts => { outcome = await CloudEnvironmentUpdate.Follow(accounts, Current(accounts), update.Id, operation.Token); });
                updates[update.ConfigurationId] = outcome;
                if (outcome.Configuration != null && selected?.Id == outcome.ConfigurationId) selected = outcome.Configuration;
                status.Text = UpdateState(outcome);
            }
            finally { End(); }
        }

        private sealed class RepositoryChoice
        {
            public string Label { get; set; }
            public CloudRepository Repository { get; set; }
        }

        private RepositoryChoice[] RepositoryChoices()
        {
            return repositories.Select(repository => new RepositoryChoice
            {
                Label = repository.FullName + " · " + repository.DefaultBranch,
                Repository = new CloudRepository { RepositoryId = repository.Id, Ref = repository.DefaultBranch }
            }).ToArray();
        }

        private async Task Create()
        {
            if (busy || disposed) return;
            var choices = RepositoryChoices();
            if (choices.Length == 0)
            {
                status.Text = "Aucun dépôt GitHub accessible. Vérifiez la connexion GitHub de cette instance puis actualisez.";
                return;
            }
            var form = new EditWindow(shell, "Créer un brouillon Codex Cloud", "Créer le brouillon");
            form.Fields.Children.Add(Ui.Text("Compte : " + email + " · Instance : " + owner.Name, 13, true));
            var name = Ui.Input("Nom de l’environnement", "", form.Fields);
            name.MaxLength = 256;
            form.Fields.Children.Add(Ui.Text("Dépôt GitHub accessible à ce compte", 13));
            var repository = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedIndex = 0 };
            AutomationProperties.SetName(repository, "Dépôt du nouveau brouillon Cloud");
            form.Fields.Children.Add(repository);
            var network = Ui.Select("Accès réseau initial", new[] { "Réseau désactivé", "Gestionnaires de paquets uniquement", "Accès réseau sans restriction" }, "Réseau désactivé", form.Fields);
            form.Fields.Children.Add(Ui.Text("Le brouillon sera privé. Le réseau est désactivé par défaut ; choisissez les gestionnaires de paquets si la préparation nécessite des dépendances. Vous pourrez ensuite préparer et publier sa première version.", 13, true));
            form.Dirty = () => !String.IsNullOrWhiteSpace(name.Text);
            CloudConfiguration created = null;
            string operationId = Guid.NewGuid().ToString("N");
            form.Save = async () =>
            {
                string label = name.Text.Trim();
                var choice = repository.SelectedItem as RepositoryChoice;
                if (String.IsNullOrEmpty(label) || choice == null) throw new InvalidOperationException("Saisissez un nom et choisissez un dépôt.");
                var policy = network.SelectedIndex == 1 ? new CloudNetworkPolicy { Type = "restricted", Presets = new List<string> { "package_managers" }, EgressHosts = new List<string>() } : new CloudNetworkPolicy { Type = network.SelectedIndex == 2 ? "unrestricted" : "disabled" };
                var request = new CloudConfigurationCreate
                {
                    Name = label, ShareSettings = "private", StartOnboarding = false,
                    NetworkPolicy = policy,
                    Repositories = new List<CloudRepository> { new CloudRepository { RepositoryId = choice.Repository.RepositoryId, Ref = choice.Repository.Ref, Depth = choice.Repository.Depth } }
                };
                name.IsReadOnly = true;
                repository.IsEnabled = false;
                network.IsEnabled = false;
                Begin("Création du brouillon Cloud…", true);
                try
                {
                    if (context.Fixture && CreateFixture != null) created = await CreateFixture(request, operationId, operation.Token);
                    else await context.Mutate(async accounts => { created = await CloudCommands.Create(accounts, Current(accounts), request, operationId, operation.Token); });
                }
                catch (CloudBackendUncertainException)
                {
                    ReconcileRequired(form, operationId);
                    throw;
                }
                catch
                {
                    RequestStopped(form, operationId);
                    throw;
                }
                finally { End(); }
            };
            form.ShowDialog();
            if (!form.Saved || created == null || disposed) return;
            await Reload(created.Id);
            status.Text = "Brouillon « " + created.Name + " » créé et relu sur le compte " + email + ". Aucun agent n’a été lancé.";
        }

        private async Task Rename(CloudConfiguration environment)
        {
            if (busy || disposed) return;
            var form = new EditWindow(shell, "Renommer l’environnement Cloud") { Height = 340, MinHeight = 340 };
            form.Fields.Children.Add(Ui.Text("Compte : " + email, 13, true));
            var name = Ui.Input("Nom de l’environnement", environment.Name, form.Fields);
            name.MaxLength = 256;
            form.Dirty = () => name.Text != environment.Name;
            string operationId = Guid.NewGuid().ToString("N");
            form.Save = async () =>
            {
                string label = name.Text.Trim();
                if (label.Length == 0) throw new InvalidOperationException("Saisissez un nom.");
                name.IsReadOnly = true;
                Begin("Enregistrement du nom dans Codex Cloud…", true);
                try
                {
                    if (context.Fixture && RenameFixture != null) await RenameFixture(environment.Id, label, operationId, operation.Token);
                    else await context.Mutate(async accounts => { await CloudCommands.Rename(accounts, Current(accounts), environment.Id, label, operationId, operation.Token); });
                }
                catch (CloudBackendUncertainException)
                {
                    ReconcileRequired(form, operationId);
                    throw;
                }
                catch
                {
                    RequestStopped(form, operationId);
                    throw;
                }
                finally { End(); }
            };
            form.ShowDialog();
            if (!form.Saved || disposed) return;
            await Reload(environment.Id);
            status.Text = "Nom enregistré et relu sur le compte Cloud " + email + ".";
        }

        private void ReconcileRequired(EditWindow form, string operationId)
        {
            form.SaveButton.Visibility = Visibility.Collapsed;
            form.Fields.Children.Add(Ui.Text("Le serveur n’a pas confirmé le résultat. Fermez cette fenêtre puis actualisez les environnements avant toute nouvelle demande. Référence de suivi : " + operationId, 13));
            status.Text = "Enregistrement incertain · actualisez et rapprochez le résultat avant de recommencer · " + operationId;
        }

        private void RequestStopped(EditWindow form, string operationId)
        {
            form.SaveButton.Visibility = Visibility.Collapsed;
            form.Fields.Children.Add(Ui.Text("L’enregistrement a été arrêté. Fermez cette fenêtre et actualisez le Cloud avant de préparer une nouvelle demande corrigée. Cette intention ne sera pas renvoyée. Référence de suivi : " + operationId, 13));
            status.Text = "Enregistrement arrêté · vérifiez le résultat et actualisez avant une nouvelle demande · " + operationId;
        }

        private void Begin(string message, bool mutation = false, TimeSpan? timeout = null)
        {
            busy = true;
            mutating = mutation;
            operation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
            status.Text = message;
            UpdateButtons();
        }

        private void End()
        {
            operation?.Dispose();
            operation = null;
            busy = false;
            mutating = false;
            if (!disposed) { UpdateButtons(); Render(); }
        }

        private void UpdateButtons()
        {
            refresh.IsEnabled = !busy;
            create.IsEnabled = !busy && RepositoryChoices().Length > 0;
            create.ToolTip = create.IsEnabled ? null : "Actualisez le catalogue GitHub. Un dépôt accessible à ce compte est nécessaire.";
            EnvironmentChoice.IsEnabled = !busy && configurations.Count > 0;
        }

        private void ShowError(Exception error)
        {
            if (disposed) return;
            var native = error as CloudBackendException;
            var uncertain = error is CloudBackendUncertainException;
            status.Text = uncertain
                ? "Résultat de l’enregistrement incertain. Actualisez avant de refaire cette action ; aucun nouvel envoi automatique n’est effectué."
                : "Codex Cloud : " + Program.SafeError(error);
            if (native != null)
            {
                status.Text += " · " + native.Code;
                if (native.HttpStatus.HasValue) status.Text += " · HTTP " + native.HttpStatus.Value;
                if (!String.IsNullOrEmpty(native.RequestId)) status.Text += " · diagnostic " + native.RequestId;
            }
        }

        internal bool CanLeave()
        {
            if (!busy) return true;
            if (!mutating) { operation?.Cancel(); return true; }
            status.Text = "Une opération Cloud est en cours. Attendez son résultat avant de changer d’instance ou d’onglet.";
            return false;
        }

        public void Dispose()
        {
            disposed = true;
            operation?.Cancel();
        }
    }
}
