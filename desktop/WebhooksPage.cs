using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    // This view reads receipts only. Refreshing it never opens Codex or sends a prompt.
    internal sealed class WebhooksPage : PageView
    {
        private readonly ListBox list = new ListBox();
        private readonly StackPanel details = new StackPanel();
        private readonly TextBlock endpoint = Ui.Text("", 13, true), service = Ui.Text("", 15);
        private readonly TextBox search = new TextBox();
        private readonly Button routesTab, jobsTab;
        private bool jobs, refreshing;
        private string selectedId, fingerprint;
        internal ListBox Entries => list;
        internal string ServiceSummary => service.Text;
        internal string EndpointSummary => endpoint.Text;
        private sealed class Row
        {
            internal object Value;
            internal string Id;
            public string Label { get; set; }
        }

        internal WebhooksPage(DesktopContext context, ShellWindow shell) : base(context, shell, "Webhooks", "Déclenchez vos agents depuis vos applications, avec une destination et des droits définis à l’avance.")
        {
            Command("Créer une entrée", () => Edit(null), true);
            Command("Connexion externe…", Configure);
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); Body.Children.Add(top);
            var status = new Grid(); status.ColumnDefinitions.Add(new ColumnDefinition()); status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var summary = new StackPanel(); summary.Children.Add(service); summary.Children.Add(endpoint); status.Children.Add(summary);
            var copySafe = Ui.AsyncButton("Copier l’adresse", () => { Clipboard.SetText(PublicBase(Context.Store)); Notice.Text = "Adresse copiée."; return Task.CompletedTask; }, Error);
            Grid.SetColumn(copySafe, 1); status.Children.Add(copySafe); top.Children.Add(Ui.Card(status));
            var tabs = Ui.Actions(top);
            routesTab = Ui.Button("Entrées", () => SelectTab(false)); jobsTab = Ui.Button("Journal des missions", () => SelectTab(true));
            tabs.Children.Add(routesTab); tabs.Children.Add(jobsTab);
            var filter = Ui.SearchField(search, "Rechercher une entrée ou une mission"); filter.Margin = new Thickness(0, 0, 0, 12); top.Children.Add(filter);
            search.TextChanged += async delegate { await Refresh(); };
            var split = new Grid(); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290), MinWidth = 210 }); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); split.ColumnDefinitions.Add(new ColumnDefinition()); Body.Children.Add(split);
            list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(list, "Entrées et missions webhook");
            var template = new DataTemplate(typeof(Row)); var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label")); label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); label.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 9, 10, 9)); template.VisualTree = label; list.ItemTemplate = template; split.Children.Add(list);
            var scroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumn(scroll, 2); split.Children.Add(scroll);
            list.SelectionChanged += delegate { var row = list.SelectedItem as Row; selectedId = row?.Id; Show(row?.Value); };
            StyleTabs();
        }

        private void StyleTabs()
        {
            routesTab.SetResourceReference(StyleProperty, !jobs ? "PrimaryButton" : typeof(Button));
            jobsTab.SetResourceReference(StyleProperty, jobs ? "PrimaryButton" : typeof(Button));
        }
        internal void SelectTab(bool journal) { jobs = journal; selectedId = null; StyleTabs(); _ = Refresh(); }
        internal static string PublicBase(RelayStore store)
        {
            var tunnel = CloudflareTunnel.Status(store);
            if (CloudflareTunnel.Config(store).Enabled && tunnel.State == "connected" && Uri.TryCreate(tunnel.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https") return uri.GetLeftPart(UriPartial.Authority);
            return "http://127.0.0.1:" + Webhooks.Config(store).Port;
        }
        internal static string StateLabel(string state)
        {
            switch (state)
            {
                case "queued": return "En file";
                case "waiting": return "En attente de disponibilité";
                case "sending": return "Envoi à Codex";
                case "monitoring": return "En cours · suivi du relais";
                case "attention": return "Autorisation attendue dans Codex";
                case "completed": return "Terminée";
                case "failed": return "Échec";
                case "blocked": return "Intervention nécessaire";
                case "uncertain": return "Envoi à vérifier · ne pas réessayer";
                case "cancelled": return "Annulée";
                default: return state ?? "État inconnu";
            }
        }
        internal static string RouteEndpoint(RelayStore store, WebhookRoute route) => PublicBase(store).TrimEnd('/') + "/v1/hooks/" + Uri.EscapeDataString(route.Slug) + "/jobs";
        internal static string CurlExample(string url) => "curl --request POST '" + url + "' \\\n  --header 'Authorization: Bearer VOTRE_CLE' \\\n  --header 'Idempotency-Key: commande-123' \\\n  --header 'Content-Type: application/json' \\\n  --data '{\"title\":\"Ma mission\",\"prompt\":\"Réalise cette mission dans le périmètre autorisé.\"}'";
        internal static string PowerShellExample(string url) => "$headers = @{ Authorization = \"Bearer $env:SWITCHER_WEBHOOK_KEY\"; 'Idempotency-Key' = 'commande-123' }\n$body = @{ title = 'Ma mission'; prompt = 'Réalise cette mission dans le périmètre autorisé.' } | ConvertTo-Json\nInvoke-RestMethod -Method Post -Uri '" + url + "' -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))";

        public override async Task Refresh()
        {
            if (refreshing) return; refreshing = true;
            try
            {
                var snapshot = await Task.Run(() => new { Config = Webhooks.Config(Context.Store), Tunnel = CloudflareTunnel.Status(Context.Store), Worker = WebhookWorker.Status(Context.Store), Routes = Webhooks.Routes(Context.Store), Jobs = Webhooks.Jobs(Context.Store) });
                DateTime checkedAt;
                bool listening = !Context.Fixture && snapshot.Worker.Listening && DesktopRuntime.SameProcess(snapshot.Worker.Pid, snapshot.Worker.Started) && String.IsNullOrEmpty(snapshot.Worker.Error) && DateTime.TryParse(snapshot.Worker.Updated, null, DateTimeStyles.RoundtripKind, out checkedAt) && DateTime.UtcNow - checkedAt.ToUniversalTime() < TimeSpan.FromSeconds(45);
                service.Text = !snapshot.Config.Enabled ? "Réception désactivée" : Context.Fixture ? "Configuration de recette · aucun service lancé" : listening ? "Réception active · port " + snapshot.Config.Port : "Réception indisponible · " + (snapshot.Worker.Error ?? "Service arrêté ou démarrage en cours");
                endpoint.Text = snapshot.Tunnel.State == "connected" ? "Tunnel Cloudflare connecté · " + PublicBase(Context.Store) : "Accès local · " + PublicBase(Context.Store) + (snapshot.Tunnel.State == "starting" ? "\nTunnel Cloudflare : connexion en cours" : snapshot.Tunnel.State == "retrying" ? "\nTunnel Cloudflare : reconnexion en cours" : "") + (String.IsNullOrEmpty(snapshot.Tunnel.Error) ? "" : "\nTunnel : " + snapshot.Tunnel.Error);
                string query = search.Text.Trim();
                string stamp = jobs + "|" + query + "|" + (jobs ? Json.Write(snapshot.Jobs) : Json.Write(snapshot.Routes));
                if (stamp == fingerprint) return; fingerprint = stamp;
                var rows = jobs ? snapshot.Jobs.OrderByDescending(j => j.CreatedUtc).Select(j => new Row { Id = j.Id, Value = j, Label = (String.IsNullOrEmpty(j.Title) ? "Mission webhook" : j.Title) + "\n" + StateLabel(j.State) + " · " + ChatWatchesWindow.Age(j.UpdatedUtc) }) : snapshot.Routes.OrderBy(r => r.Name).Select(r => new Row { Id = r.Id, Value = r, Label = r.Name + "\n/hooks/" + r.Slug + "\n" + (r.Enabled ? "Active" : "En pause") });
                list.ItemsSource = rows.Where(r => query.Length == 0 || r.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                list.SelectedItem = list.Items.Cast<Row>().FirstOrDefault(r => r.Id == selectedId) ?? list.Items.Cast<Row>().FirstOrDefault();
                if (list.Items.Count == 0) Show(null);
            }
            catch (Exception e) { Error(e); }
            finally { refreshing = false; }
        }
        private void Show(object value)
        {
            details.Children.Clear();
            if (value == null)
            {
                details.Children.Add(Ui.Text(jobs ? "Vos missions apparaîtront ici" : "Reliez une application à une instance", 22));
                details.Children.Add(Ui.Text(jobs ? "Chaque requête acceptée garde son identifiant et son état. Un nouvel envoi avec la même clé d’idempotence ne crée pas de doublon." : "Créez une entrée, choisissez son instance et son dossier. Votre application reçoit une adresse et une clé dédiée ; elle ne peut pas choisir un autre compte.", 14, true)); return;
            }
            if (value is WebhookRoute route) ShowRoute(route); else if (value is WebhookJob job) ShowJob(job);
        }
        private void ShowRoute(WebhookRoute route)
        {
            var heading = new DockPanel(); var more = Ui.Button("⋯", () => { }); DockPanel.SetDock(more, Dock.Right); heading.Children.Add(more); heading.Children.Add(Ui.Text(route.Name, 23)); details.Children.Add(heading);
            var menu = new ContextMenu();
            AddMenu(menu, "Modifier cette entrée…", () => Edit(route));
            AddMenu(menu, route.Enabled ? "Mettre en pause" : "Activer", async () => { var copy = Json.Read<WebhookRoute>(Json.Write(route)); copy.Enabled = !copy.Enabled; await Task.Run(() => Webhooks.SaveRoute(Context.Store, copy)); await Refresh(); });
            AddMenu(menu, "Remplacer la clé d’accès…", () => Rotate(route));
            more.ContextMenu = menu; more.Click += delegate { menu.PlacementTarget = more; menu.IsOpen = true; };
            var accounts = Context.Accounts(); var instance = accounts.Data.Instances.FirstOrDefault(i => i.Id == route.InstanceId); var profile = accounts.Data.Profiles.FirstOrDefault(p => p.Key == route.AccountKey);
            details.Children.Add(Ui.Text((route.Enabled ? "● Active" : "En pause") + " · " + (instance?.Name ?? "Instance indisponible"), 14));
            details.Children.Add(Ui.Text(profile?.Email ?? "Compte épinglé indisponible", 13, true));
            var target = new StackPanel(); target.Children.Add(Ui.Text("Destination autorisée", 12, true)); target.Children.Add(Ui.Text(route.Workspace, 14)); target.Children.Add(Ui.Text(String.IsNullOrEmpty(route.ThreadId) ? "Une nouvelle conversation par mission" : "Conversation ciblée : " + route.ThreadId, 13, true));
            target.Children.Add(Ui.Text((route.Access == "headless" ? "Agent sans fenêtre" : "Dans Codex ouvert") + " · " + (route.PermissionProfile == "full-access" ? "Accès complet exigé" : "Réglages de l’instance") + "\n" + route.Model + " · " + route.ReasoningEffort, 12, true)); details.Children.Add(Ui.Card(target));
            var address = Ui.Input("Adresse de cette entrée", RouteEndpoint(Context.Store, route), details); address.IsReadOnly = true;
            details.Children.Add(Ui.AsyncButton("Copier l’adresse de cette entrée", () => { Clipboard.SetText(address.Text); return Task.CompletedTask; }, Error));
            details.Children.Add(Ui.Text("La clé d’accès n’est jamais réaffichée. Si vous l’avez perdue, remplacez-la puis mettez à jour votre application.", 12, true));
            if (!String.IsNullOrEmpty(route.PromptPrefix)) { var instructions = Ui.Input("Instructions permanentes", route.PromptPrefix, details, true); instructions.IsReadOnly = true; instructions.Height = 100; }
            AddExamples(details, address.Text);
        }
        private void ShowJob(WebhookJob job)
        {
            job=Webhooks.Job(Context.Store,job.Id);
            details.Children.Add(Ui.Text(String.IsNullOrEmpty(job.Title) ? "Mission webhook" : job.Title, 23)); details.Children.Add(Ui.Text(StateLabel(job.State), 17));
            var route = Webhooks.Routes(Context.Store).FirstOrDefault(r => r.Id == job.RouteId);
            details.Children.Add(Ui.Text((route?.Name ?? "Entrée supprimée") + " · Reçue " + ChatWatchesWindow.Age(job.CreatedUtc) + " · Mise à jour " + ChatWatchesWindow.Age(job.UpdatedUtc), 12, true));
            if (!String.IsNullOrEmpty(job.Error)) details.Children.Add(Ui.Text(job.Error, 14));
            var prompt = Ui.Input("Demande reçue", job.Prompt, details, true); prompt.IsReadOnly = true; prompt.Height = 150;
            if (!String.IsNullOrEmpty(job.Result)) { var result = Ui.Input("Résultat", job.Result, details, true); result.IsReadOnly = true; result.Height = 180; }
            var trace = new StackPanel(); trace.Children.Add(Ui.Text("Mission : " + job.Id + "\nIdentifiant externe : " + (job.ExternalId ?? "—") + "\nConversation : " + (job.ThreadId ?? "Pas encore créée") + "\nRelais : " + (job.RelayJobId ?? "Pas encore transmis"), 12, true));
            details.Children.Add(new Expander { Header = "Identifiants de suivi", Content = trace });
            details.Children.Add(Ui.Text("Une requête HTTP acceptée confirme sa mise en file, pas la fin du travail. Aucun bouton de relance automatique n’est proposé pour un envoi incertain.", 12, true));
        }
        private void AddMenu(ContextMenu menu, string caption, Func<Task> action)
        { var item = new MenuItem { Header = caption }; item.Click += async delegate { try { await action(); } catch (Exception e) { Error(e); } }; menu.Items.Add(item); }
        private static void AddExamples(Panel parent, string url)
        {
            var examples = new StackPanel();
            var shell = Ui.Input("PowerShell", PowerShellExample(url), examples, true); shell.IsReadOnly = true; shell.Height = 170;
            var curl = Ui.Input("cURL", CurlExample(url), examples, true); curl.IsReadOnly = true; curl.Height = 150;
            examples.Children.Add(Ui.Text("Remplacez la clé et utilisez une clé d’idempotence différente pour chaque nouvelle mission. Gardez la même clé pour réessayer la même requête.", 12, true));
            parent.Children.Add(new Expander { Header = "Intégrer dans mon application", Content = examples, Margin = new Thickness(0, 12, 0, 0) });
        }
        private async Task Edit(WebhookRoute route)
        {
            var choices = await Context.Read(a => a.Data.Instances.Where(i => !i.Archived && !String.IsNullOrEmpty(i.AccountKey) && a.Data.Profiles.Any(p => p.Key == i.AccountKey)).Select(i => new WebhookInstanceChoice { Id = i.Id, Home = a.Instances.Home(i), AccountKey = i.AccountKey, Label = i.Name + " · " + a.Data.Profiles.First(p => p.Key == i.AccountKey).Email }).ToArray());
            if (choices.Length == 0) throw new InvalidOperationException("Associez d’abord un compte permanent à une instance.");
            var wizard = new WebhookRouteWizard(Shell, choices, route, async proposed => await Task.Run(() => Webhooks.SaveRoute(Context.Store, proposed)));
            wizard.ShowDialog();
            if (wizard.SavedRoute != null)
            {
                selectedId = wizard.SavedRoute.Id;
                if (route == null) ShowKey(wizard.SavedRoute, await Task.Run(() => Webhooks.RotateKey(Context.Store, wizard.SavedRoute.Id)));
                await Refresh();
            }
        }
        private async Task Rotate(WebhookRoute route)
        {
            if (!Ui.Confirm(Shell, "L’ancienne clé sera refusée dès ce remplacement. Les missions déjà acceptées continueront. Mettez ensuite à jour chaque application qui utilise cette entrée.", "Remplacer la clé d’accès")) return;
            var key = await Task.Run(() => Webhooks.RotateKey(Context.Store, route.Id)); ShowKey(route, key); await Refresh();
        }
        private void ShowKey(WebhookRoute route, string key)
        {
            var dialog = new EditWindow(Shell, "Votre entrée est prête", "J’ai conservé la clé");
            dialog.Fields.Children.Add(Ui.Text("Cette clé ne sera affichée qu’une seule fois. Transmettez-la uniquement à l’application autorisée.", 14));
            var field = Ui.Input("Clé d’accès privée", key, dialog.Fields); field.IsReadOnly = true;
            var feedback = Ui.Text("", 12, true);
            dialog.Fields.Children.Add(Ui.AsyncButton("Copier la clé", () => { Clipboard.SetText(key); feedback.Text = "Clé copiée. Conservez-la dans les secrets de votre application."; return Task.CompletedTask; }, e => feedback.Text = Program.SafeError(e), true)); dialog.Fields.Children.Add(feedback);
            var url = Ui.Input("Adresse de l’entrée", RouteEndpoint(Context.Store, route), dialog.Fields); url.IsReadOnly = true; AddExamples(dialog.Fields, url.Text);
            dialog.Save = () => Task.CompletedTask; dialog.ShowDialog(); field.Text = "";
        }
        private Task Configure()
        {
            var config = Webhooks.Config(Context.Store); var tunnel = CloudflareTunnel.Config(Context.Store);
            var dialog = new EditWindow(Shell, "Connexion externe");
            var enabled = Ui.Check("Recevoir les missions de mes applications", config.Enabled, dialog.Fields);
            var port = Ui.Input("Port local", config.Port.ToString(CultureInfo.InvariantCulture), dialog.Fields);
            var publicAccess = Ui.Check("Publier une adresse HTTPS avec Cloudflare Tunnel", tunnel.Enabled, dialog.Fields);
            var mode = Ui.Select("Type d’adresse", new[] { "Adresse temporaire de test", "Adresse permanente Cloudflare" }, tunnel.Mode == "named" ? "Adresse permanente Cloudflare" : "Adresse temporaire de test", dialog.Fields);
            var domain = Ui.Input("Nom d’hôte public de votre tunnel", tunnel.Hostname, dialog.Fields);
            dialog.Fields.Children.Add(Ui.Text("Jeton du tunnel Cloudflare · laissez vide pour conserver le jeton enregistré", 13));
            var token = new PasswordBox { MinHeight = 36 }; AutomationProperties.SetName(token, "Nouveau jeton Cloudflare Tunnel"); dialog.Fields.Children.Add(token);
            var executable = Ui.Input("Programme cloudflared.exe", tunnel.Executable, dialog.Fields);
            dialog.Fields.Children.Add(Ui.Button("Choisir cloudflared.exe…", () => { string path = Ui.File("Cloudflare Tunnel|cloudflared.exe|Exécutables|*.exe"); if (path != null) executable.Text = path; }));
            var help = Ui.Text("L’adresse temporaire change à chaque redémarrage. Pour une adresse stable, configurez un tunnel Cloudflare vers http://127.0.0.1:PORT et collez son jeton. Le Switcher ne lance aucune instance Codex lors de cette configuration.", 12, true); dialog.Fields.Children.Add(help);
            Action visibility = () => { bool named = mode.SelectedIndex == 1; domain.Visibility = named ? Visibility.Visible : Visibility.Collapsed; token.Visibility = named ? Visibility.Visible : Visibility.Collapsed; mode.IsEnabled = publicAccess.IsChecked == true; };
            mode.SelectionChanged += delegate { visibility(); }; publicAccess.Click += delegate { visibility(); }; visibility();
            dialog.Save = async delegate
            {
                if (!Int32.TryParse(port.Text, out var number) || number < 1024 || number > 65535) throw new InvalidOperationException("Choisissez un port entre 1024 et 65535.");
                var proposed = new CloudflareTunnelConfig { Enabled = publicAccess.IsChecked == true && enabled.IsChecked == true, Mode = mode.SelectedIndex == 1 ? "named" : "quick", Executable = executable.Text.Trim(), Hostname = domain.Text.Trim(), Token = token.Password.Length == 0 ? null : token.Password };
                bool receive = enabled.IsChecked == true;
                var previous=WebhookWorker.Status(Context.Store);
                await Task.Run(() => { CloudflareTunnel.SaveConfig(Context.Store, proposed); config.Port = number; config.Enabled = receive; Webhooks.SaveConfig(Context.Store, config); });
                if(!Context.Fixture){
                    if(previous.Port!=number||!receive){
                        for(int n=0;n<120&&DesktopRuntime.SameProcess(previous.Pid,previous.Started);n++)await Task.Delay(250);
                        if(DesktopRuntime.SameProcess(previous.Pid,previous.Started))throw new InvalidOperationException("Le service termine les verifications en cours. La configuration est enregistree ; reessayez apres son arret.");
                    }
                    if(receive)await Task.Run(()=>WebhookWorker.Ensure(Context.Store));
                }
                token.Clear(); await Refresh();
            };
            dialog.ShowDialog(); token.Clear(); return Task.CompletedTask;
        }
    }

    internal sealed class WebhookInstanceChoice
    {
        public string Label { get; set; }
        internal string Id, AccountKey, Home;
    }

    internal sealed class WebhookRouteWizard : Window
    {
        internal readonly TextBox RouteName = new TextBox { MaxLength = 100 }, Slug = new TextBox { MaxLength = 63 }, Workspace = new TextBox(), Thread = new TextBox(), Instructions = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 100, MaxLength = 12000 };
        internal readonly ComboBox Instance = new ComboBox { DisplayMemberPath = "Label" }, Projects = new ComboBox(), Conversation = new ComboBox(), Permission = new ComboBox(), Execution = new ComboBox();
        internal readonly Button NextButton, BackButton;
        internal readonly TextBlock ErrorText = Ui.Text("", 13);
        internal int Step { get; private set; }
        internal WebhookRoute SavedRoute { get; private set; }
        private readonly StackPanel[] panels = new StackPanel[4];
        private readonly Border[] markers = new Border[4];
        private readonly TextBlock review = Ui.Text("", 14), target = Ui.Text("", 12, true);
        private readonly WebhookRoute original;
        private readonly Func<WebhookRoute, Task<WebhookRoute>> save;
        private bool saving, closed, manualSlug;
        private int projectGeneration;
        internal Func<bool> ConfirmDiscard;

        internal WebhookRouteWizard(Window owner, WebhookInstanceChoice[] choices, WebhookRoute existing, Func<WebhookRoute, Task<WebhookRoute>> persist)
        {
            Owner = owner; original = existing == null ? null : Json.Read<WebhookRoute>(Json.Write(existing)); save = persist;
            Title = existing == null ? "Nouvelle entrée webhook" : "Modifier l’entrée webhook"; Width = Math.Min(740, SystemParameters.WorkArea.Width); Height = Math.Min(720, SystemParameters.WorkArea.Height); MinWidth = 560; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Segoe UI"); FontSize = 14; ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new DockPanel { Margin = new Thickness(26) }; Content = root;
            var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading); heading.Children.Add(Ui.Text(Title, 25));
            var steps = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 18) }; heading.Children.Add(steps);
            string[] names = { "Entrée", "Destination", "Conversation", "Vérifier" };
            for (int n = 0; n < 4; n++) { markers[n] = new Border { Child = Ui.Text((n + 1) + "  " + names[n], 12), Padding = new Thickness(8), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, n == 3 ? 0 : 6, 0) }; steps.Children.Add(markers[n]); }
            var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); ErrorText.Foreground = Brushes.Firebrick; footer.Children.Add(ErrorText);
            var buttons = new DockPanel(); footer.Children.Add(buttons); NextButton = Ui.AsyncButton("Continuer", Next, e => ErrorText.Text = Program.SafeError(e), true); DockPanel.SetDock(NextButton, Dock.Right); buttons.Children.Add(NextButton); BackButton = Ui.Button("Retour", () => ShowStep(Step - 1)); buttons.Children.Add(BackButton); buttons.Children.Add(Ui.Button("Annuler", Close));
            var body = new Grid(); root.Children.Add(body); for (int n = 0; n < 4; n++) { panels[n] = new StackPanel(); body.Children.Add(new ScrollViewer { Content = panels[n], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); }
            AddField(panels[0], "Comment s’appelle cette entrée ?", RouteName); AddField(panels[0], "Adresse courte · lettres, chiffres et tirets", Slug); panels[0].Children.Add(Ui.Text("Exemple : validation-facture. Une entrée possède une clé privée et une destination fixe. Votre application envoie uniquement le travail à effectuer.", 13, true));
            Instance.ItemsSource = choices; AddField(panels[1], "Quelle instance doit recevoir le travail ?", Instance); panels[1].Children.Add(target); Execution.ItemsSource = new[] { "Dans Codex ouvert", "Sans fenêtre si autorisé" }; Execution.SelectedIndex = 0; AddField(panels[1], "Exécution", Execution); AddField(panels[1], "Projet local connu · facultatif", Projects); AddField(panels[1], "Dossier de travail autorisé", Workspace);
            panels[1].Children.Add(Ui.Button("Choisir un dossier…", () => { string folder = Ui.Folder(); if (folder != null) Workspace.Text = folder; })); panels[1].Children.Add(Ui.Text("Les dossiers des projets sont lus localement. Choisir une instance ne l’ouvre pas et ne déclenche aucune mission.", 12, true));
            Conversation.ItemsSource = new[] { "Créer une nouvelle conversation par mission", "Utiliser toujours une conversation précise" }; Conversation.SelectedIndex = 0; AddField(panels[2], "Comment organiser les missions ?", Conversation); AddField(panels[2], "Identifiant du chat ciblé", Thread); panels[2].Children.Add(Ui.Text("Pour cibler un chat, copiez son lien codex://threads/… . Le compte et le dossier restent ceux de l’entrée.", 12, true));
            AddField(panels[3], "Instructions permanentes · périmètre et résultat attendu", Instructions); Permission.ItemsSource = new[] { "Réglages de l’instance", "Exiger Accès complet" }; Permission.SelectedIndex = 0; AddField(panels[3], "Permissions des missions reçues", Permission); panels[3].Children.Add(Ui.Text("Exiger Accès complet bloque le départ si le chat ne possède pas ces permissions. Le Switcher ne les élève pas et n’approuve aucune demande à votre place. Le modèle est GPT-6.1 Sol · Ultra.", 12, true)); panels[3].Children.Add(Ui.Card(review));
            RouteName.TextChanged += delegate { if (!manualSlug && original == null) Slug.Text = SuggestSlug(RouteName.Text); };
            Slug.PreviewTextInput += delegate { manualSlug = true; }; Slug.PreviewKeyDown += delegate { manualSlug = true; };
            Conversation.SelectionChanged += delegate { Thread.IsEnabled = Conversation.SelectedIndex == 1; };
            Instance.SelectionChanged += async delegate { if (Instance.SelectedItem is WebhookInstanceChoice choice) { target.Text = "Ce compte reste épinglé à l’entrée : " + choice.Label; await LoadProjects(choice); } };
            Projects.SelectionChanged += delegate { if (Projects.SelectedItem is string project) Workspace.Text = project; };
            Permission.SelectionChanged += delegate { UpdateReview(); }; Instructions.TextChanged += delegate { UpdateReview(); };
            ConfirmDiscard = () => Ui.Confirm(this, "Abandonner les modifications de cette entrée ?", "Configuration en cours");
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args) { if (saving && SavedRoute == null) args.Cancel = true; else if (SavedRoute == null && Dirty() && !ConfirmDiscard()) args.Cancel = true; };
            Closed += delegate { closed = true; ++projectGeneration; };
            if (existing != null) { RouteName.Text = existing.Name; Slug.Text = existing.Slug; manualSlug = true; Workspace.Text = existing.Workspace; Thread.Text = existing.ThreadId; Instructions.Text = existing.PromptPrefix; Conversation.SelectedIndex = String.IsNullOrEmpty(existing.ThreadId) ? 0 : 1; Permission.SelectedIndex = existing.PermissionProfile == "full-access" ? 1 : 0; Execution.SelectedIndex = existing.Access == "headless" ? 1 : 0; }
            Instance.SelectedItem = choices.FirstOrDefault(c => existing != null && c.Id == existing.InstanceId && c.AccountKey == existing.AccountKey);
            Thread.IsEnabled = Conversation.SelectedIndex == 1; ShowStep(0);
        }
        private static void AddField(Panel panel, string title, FrameworkElement control) { panel.Children.Add(Ui.Text(title, 13)); AutomationProperties.SetName(control, title); panel.Children.Add(control); }
        internal static string SuggestSlug(string value)
        { string normalized = (value ?? "").Normalize(System.Text.NormalizationForm.FormD); normalized = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant(); string slug = Regex.Replace(normalized, "[^a-z0-9]+", "-").Trim('-'); return slug.Substring(0, Math.Min(63, slug.Length)).TrimEnd('-'); }
        private async Task LoadProjects(WebhookInstanceChoice choice)
        {
            int generation = ++projectGeneration; Projects.ItemsSource = null;
            var paths = await Task.Run(() => ReadProjects(choice.Home));
            if (!closed && generation == projectGeneration) Projects.ItemsSource = paths;
        }
        internal static string[] ReadProjects(string home)
        {
            try { string path = Path.Combine(home, ".codex-global-state.json"); if (!File.Exists(path)) return new string[0]; var data = Json.Read<object>(SafeFiles.ReadText(path)); return Json.Obj(Json.Get(data, "local-projects")).SelectMany(p => RelayEngine.Rows(Json.Get(p.Value, "rootPaths"))).Select(Json.Str).Where(Path.IsPathRooted).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToArray(); }
            catch (IOException) { return new string[0]; } catch (UnauthorizedAccessException) { return new string[0]; } catch (ArgumentException) { return new string[0]; } catch (InvalidOperationException) { return new string[0]; } catch (FormatException) { return new string[0]; }
        }
        private bool Dirty() => original == null ? !String.IsNullOrEmpty(RouteName.Text) || !String.IsNullOrEmpty(Workspace.Text) || !String.IsNullOrEmpty(Instructions.Text) : RouteName.Text != original.Name || Slug.Text != original.Slug || Workspace.Text != original.Workspace || Instructions.Text != (original.PromptPrefix ?? "") || (Instance.SelectedItem as WebhookInstanceChoice)?.Id != original.InstanceId || Thread.Text != (original.ThreadId ?? "") || (Permission.SelectedIndex == 1) != (original.PermissionProfile == "full-access") || (Execution.SelectedIndex == 1) != (original.Access == "headless") || (Conversation.SelectedIndex == 1) != !String.IsNullOrEmpty(original.ThreadId);
        private void ShowStep(int step)
        {
            Step = Math.Max(0, Math.Min(3, step)); ErrorText.Text = "";
            for (int n = 0; n < 4; n++) { ((ScrollViewer)panels[n].Parent).Visibility = Step == n ? Visibility.Visible : Visibility.Collapsed; markers[n].SetResourceReference(Border.BorderBrushProperty, n <= Step ? "AccentBrush" : "LineBrush"); markers[n].SetResourceReference(Border.BackgroundProperty, Step == n ? "SurfaceBrush" : "CanvasBrush"); }
            BackButton.IsEnabled = Step > 0; NextButton.Content = Step == 3 ? original == null ? "Créer l’entrée et sa clé" : "Enregistrer" : "Continuer"; if (Step == 3) UpdateReview();
        }
        internal async Task Next()
        {
            if (saving) return;
            if (Step == 0) { if (String.IsNullOrWhiteSpace(RouteName.Text)) throw new InvalidOperationException("Donnez un nom à cette entrée."); if (!Regex.IsMatch(Slug.Text, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")) throw new InvalidOperationException("L’adresse courte doit compter 3 à 63 lettres minuscules, chiffres ou tirets, sans tiret aux extrémités."); ShowStep(1); return; }
            if (Step == 1) { if (!(Instance.SelectedItem is WebhookInstanceChoice)) throw new InvalidOperationException("Choisissez une instance et son compte permanent."); if (!Path.IsPathRooted(Workspace.Text) || !Directory.Exists(Workspace.Text)) throw new InvalidOperationException("Choisissez un dossier de travail existant avec un chemin absolu."); ShowStep(2); return; }
            if (Step == 2) { if (Conversation.SelectedIndex == 1 && !Guid.TryParse(ThreadId(Thread.Text), out _)) throw new InvalidOperationException("Collez le lien ou l’identifiant exact d’une conversation Codex."); ShowStep(3); return; }
            var choice = (WebhookInstanceChoice)Instance.SelectedItem; var route = original == null ? new WebhookRoute { Id = Guid.NewGuid().ToString("N"), Enabled = true } : Json.Read<WebhookRoute>(Json.Write(original));
            route.Name = RouteName.Text.Trim(); route.Slug = Slug.Text.Trim(); route.InstanceId = choice.Id; route.AccountKey = choice.AccountKey; route.Home = choice.Home; route.Workspace = Path.GetFullPath(Workspace.Text.Trim()); route.ThreadId = Conversation.SelectedIndex == 1 ? ThreadId(Thread.Text) : null; route.PromptPrefix = Instructions.Text.Trim(); route.PermissionProfile = Permission.SelectedIndex == 1 ? "full-access" : "inherit"; route.Access = Execution.SelectedIndex == 1 ? "headless" : "desktop"; route.Model = "gpt-6.1-sol"; route.ReasoningEffort = "ultra";
            saving = true; BackButton.IsEnabled = false;
            try { SavedRoute = await save(route); Close(); } finally { saving = false; BackButton.IsEnabled = true; }
        }
        internal static string ThreadId(string value) { value = (value ?? "").Trim(); const string prefix = "codex://threads/"; if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value.Substring(prefix.Length).Split('?', '#', '/')[0]; return value; }
        private void UpdateReview()
        { review.Text = (Instance.SelectedItem as WebhookInstanceChoice)?.Label + "\n" + Workspace.Text + "\n" + (Conversation.SelectedIndex == 1 ? "Chat ciblé : " + ThreadId(Thread.Text) : "Nouveau chat par mission") + "\n" + (Permission.SelectedIndex == 1 ? "Accès complet" : "Permissions de l’instance") + " · GPT-6.1 Sol · Ultra\nAdresse : /hooks/" + Slug.Text; }
    }
}
