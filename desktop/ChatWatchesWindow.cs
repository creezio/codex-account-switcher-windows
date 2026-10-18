using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    // This window only reads local monitoring receipts. The native worker polls
    // Codex; opening or refreshing this view never sends an agent prompt.
    internal sealed class ChatWatchesWindow : Window
    {
        private readonly DesktopContext context;
        private readonly ListBox list = new ListBox();
        private readonly StackPanel details = new StackPanel();
        private readonly TextBlock notice = Ui.Text("", 13, true), error = Ui.Text("", 13);
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private string selectedId, fingerprint;
        private bool refreshing, closed;
        internal Func<ChatWatchRecord, Task> OpenFixture { get; set; }
        internal Func<bool> ConfirmCompleteFixture { get; set; }
        internal bool RefreshTimerRunning => timer.IsEnabled;
        internal int PendingAlertCount { get; private set; }
        internal ListBox WatchList => list;
        internal string MonitoringNotice => notice.Text;
        private sealed class Row
        {
            internal ChatWatchRecord Watch;
            public string Label { get; set; }
            public override string ToString() { return Label; }
        }
        internal ChatWatchesWindow(DesktopContext context, Window owner)
        {
            this.context = context;
            Owner = owner;
            Title = "Surveillances des conversations";
            Width = Math.Min(1000, SystemParameters.WorkArea.Width);
            Height = Math.Min(700, SystemParameters.WorkArea.Height);
            MinWidth = 720; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "CanvasBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
            var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            header.Children.Add(Ui.Text("Surveillances", 26));
            header.Children.Add(Ui.Text("Le Switcher vérifie l’état natif toutes les 60 secondes, sans solliciter de modèle. Un arrêt ne signifie pas que la mission est terminée.", 13, true));
            var commands = Ui.Actions(header);
            commands.Children.Add(Ui.AsyncButton("Surveiller une conversation…", AddWatch, ShowError, true));
            commands.Children.Add(Ui.AsyncButton("Actualiser la vue", RefreshNow, ShowError));
            header.Children.Add(notice); header.Children.Add(error);
            var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            footer.Children.Add(Ui.Button("Fermer cette vue", Close));
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(grid);
            System.Windows.Automation.AutomationProperties.SetName(list, "Conversations surveillées");
            list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            var template = new DataTemplate(typeof(Row)); var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(10)); template.VisualTree = label; list.ItemTemplate = template;
            grid.Children.Add(list);
            var scroll = new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 2); grid.Children.Add(scroll);
            list.SelectionChanged += delegate { var row = list.SelectedItem as Row; selectedId = row?.Watch.Id; Show(row?.Watch); };
            timer.Tick += async delegate { try { await RefreshNow(); } catch (Exception e) { ShowError(e); } };
            Loaded += async delegate { try { await RefreshNow(); if (!closed) timer.Start(); } catch (Exception e) { ShowError(e); } };
            Closed += delegate { closed = true; timer.Stop(); };
        }
        internal static bool NeedsAttention(ChatWatchRecord watch)
        { return watch != null && !String.IsNullOrEmpty(watch.AlertUtc) && String.IsNullOrEmpty(watch.AcknowledgedUtc); }
        internal static string StateLabel(string state)
        {
            switch (state)
            {
                case "running": return "En cours";
                case "waiting-approval": return "Autorisation attendue";
                case "waiting-input": return "Réponse attendue";
                case "failed": return "Erreur d’exécution";
                case "stalled": return "Activité à vérifier";
                case "stopped": return "Arrêtée · fin non confirmée";
                case "unavailable": return "Suivi indisponible";
                case "completed": return "Mission terminée et confirmée";
                case "paused": return "Surveillance en pause";
                case "configured": return "Surveillance configurée";
                case "alert": return "Alerte détectée";
                case "notified": return "Notification transmise à Windows";
                case "acknowledged": return "Alerte acquittée";
                default: return "Première vérification en attente";
            }
        }
        internal static bool IsStale(ChatWatchRecord watch)
        {
            DateTimeOffset checkedAt;
            return watch.Enabled && (!DateTimeOffset.TryParse(watch.LastCheckedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out checkedAt) || DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromSeconds(150));
        }
        private static string ObservedLabel(ChatWatchRecord watch)
        { return IsStale(watch) && !String.IsNullOrEmpty(watch.LastCheckedUtc) ? "Contrôle en retard · suivi à vérifier" : StateLabel(watch.State); }
        internal static string Age(string value)
        {
            DateTimeOffset date;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)) return "jamais";
            var elapsed = DateTimeOffset.UtcNow - date;
            if (elapsed.TotalSeconds < 60) return "il y a " + Math.Max(0, (int)elapsed.TotalSeconds) + " s";
            if (elapsed.TotalMinutes < 60) return "il y a " + (int)elapsed.TotalMinutes + " min";
            if (elapsed.TotalHours < 24) return "il y a " + (int)elapsed.TotalHours + " h";
            return "il y a " + (int)elapsed.TotalDays + " j";
        }
        private static string AbsoluteTime(string value)
        { DateTimeOffset date; return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ? date.ToLocalTime().ToString("dd/MM · HH:mm:ss") : "Date inconnue"; }
        internal async Task RefreshNow()
        {
            if (refreshing || closed) return; refreshing = true;
            try
            {
                var snapshot = await Task.Run(() => new { Watches = new ChatWatches(context.Store).List(), Running = !context.Fixture && ChatWatchWorker.Running(context.Store) });
                if (closed) return;
                PendingAlertCount = snapshot.Watches.Count(NeedsAttention);
                notice.Text = PendingAlertCount + " alerte(s) à consulter · " + (context.Fixture ? "Mode de recette" : !snapshot.Watches.Any(w => w.Enabled) ? "Aucune surveillance active" : !snapshot.Running ? "Moteur arrêté : la surveillance n’est pas assurée" : snapshot.Watches.Any(w => IsStale(w) && !String.IsNullOrEmpty(w.LastCheckedUtc)) ? "Contrôle en retard : le suivi doit être vérifié" : "Moteur actif");
                string stamp = Json.Write(snapshot.Watches) + DateTime.UtcNow.ToString("yyyyMMddHHmm") + String.Join("|", snapshot.Watches.Select(w => IsStale(w)));
                if (fingerprint == stamp) return; fingerprint = stamp;
                list.ItemsSource = snapshot.Watches.OrderByDescending(NeedsAttention).ThenBy(w => w.Name).Select(w => new Row { Watch = w, Label = w.Name + "\n" + ObservedLabel(w) + "\nVérifiée " + Age(w.LastCheckedUtc) + (NeedsAttention(w) ? "\n● À consulter" : "") }).ToArray();
                list.SelectedItem = list.Items.Cast<Row>().FirstOrDefault(r => r.Watch.Id == selectedId) ?? list.Items.Cast<Row>().FirstOrDefault();
                if (snapshot.Watches.Length == 0) Show(null);
            }
            finally { refreshing = false; }
        }
        private void Show(ChatWatchRecord watch)
        {
            details.Children.Clear();
            if (watch == null) { details.Children.Add(Ui.Text("Aucune conversation surveillée", 20)); details.Children.Add(Ui.Text("Ajoutez une conversation pour être prévenu si elle attend une intervention, échoue ou s’arrête avant la fin de sa mission.", 14, true)); return; }
            details.Children.Add(Ui.Text(watch.Name, 23));
            details.Children.Add(Ui.Text(ObservedLabel(watch), 17));
            details.Children.Add(Ui.Text(watch.Detail ?? "La première vérification n’a pas encore eu lieu.", 14));
            details.Children.Add(Ui.Text("Dernière vérification : " + Age(watch.LastCheckedUtc) + " · " + AbsoluteTime(watch.LastCheckedUtc), 12, true));
            var accounts = context.Accounts();
            var instance = accounts.Data.Instances.FirstOrDefault(i => i.Id == watch.InstanceId);
            var profile = accounts.Data.Profiles.FirstOrDefault(p => p.Key == watch.AccountKey);
            details.Children.Add(Ui.Text((instance?.Name ?? watch.InstanceId) + " · " + (profile?.Email ?? "Compte permanent lié"), 13, true));
            var actions = Ui.Actions(details);
            actions.Children.Add(Ui.AsyncButton("Ouvrir la conversation", () => Open(watch), ShowError, true));
            if (NeedsAttention(watch)) actions.Children.Add(Ui.AsyncButton("Acquitter l’alerte", async delegate { await Task.Run(() => new ChatWatches(context.Store).Acknowledge(watch.Id)); await Updated(); }, ShowError));
            if (watch.State != "completed")
            {
                actions.Children.Add(Ui.AsyncButton(watch.Enabled ? "Mettre en pause" : "Reprendre le suivi", async delegate {
                    await Task.Run(() => { new ChatWatches(context.Store).SetEnabled(watch.Id, !watch.Enabled); if (!context.Fixture && !watch.Enabled) ChatWatchWorker.Ensure(context.Store); });
                    await Updated();
                }, ShowError));
                actions.Children.Add(Ui.AsyncButton("Confirmer la mission terminée…", async delegate {
                    bool confirmed = ConfirmCompleteFixture != null ? ConfirmCompleteFixture() : Ui.Confirm(this, "Confirmez-vous que toute la mission est terminée et vérifiée ? Cette action arrête sa surveillance. Une publication partielle ne suffit pas.", "Confirmer la fin de mission");
                    if (!confirmed) return;
                    await Task.Run(() => new ChatWatches(context.Store).ConfirmComplete(watch.Id)); await Updated();
                }, ShowError));
            }
            if (NeedsAttention(watch)) details.Children.Add(Ui.Text("L’alerte reste visible jusqu’à son acquittement ou à la reprise constatée du travail. Le Switcher ne relance pas l’agent et ne donne aucune autorisation à sa place.", 12, true));
            details.Children.Add(Ui.Text("Chronologie", 18));
            var history = watch.Events;
            if (history != null) foreach (var entry in history.AsEnumerable().Reverse().Take(30))
            {
                var line = new StackPanel();
                line.Children.Add(Ui.Text(StateLabel(entry.State) + " · " + Age(entry.At) + " · " + AbsoluteTime(entry.At), 12, true));
                line.Children.Add(Ui.Text(entry.Detail ?? "", 13)); details.Children.Add(Ui.Card(line));
            }
            var technical = new StackPanel();
            technical.Children.Add(Ui.Text("Conversation : " + watch.ThreadId + "\nInstance : " + watch.InstanceId + "\nDossier : " + watch.Workspace + (String.IsNullOrEmpty(watch.CompletionFile) ? "" : "\nPreuve de fin : " + watch.CompletionFile), 12, true));
            if (!String.IsNullOrEmpty(watch.NotifiedUtc)) technical.Children.Add(Ui.Text("Notification Windows demandée " + Age(watch.NotifiedUtc) + ". Son affichage dépend des réglages de Windows.", 12, true));
            if (!String.IsNullOrEmpty(watch.AcknowledgedUtc)) technical.Children.Add(Ui.Text("Alerte acquittée " + Age(watch.AcknowledgedUtc), 12, true));
            details.Children.Add(new Expander { Header = "Conversation et preuve de suivi", Content = technical });
        }
        private async Task Open(ChatWatchRecord watch)
        {
            if (context.Fixture) { if (OpenFixture != null) await OpenFixture(watch); return; }
            var target = await context.Read(a => new { Accounts = a, Instance = a.Data.Instances.Single(i => i.Id == watch.InstanceId && !i.Archived) });
            if (target.Instance.AccountKey != watch.AccountKey) throw new InvalidOperationException("Le compte de cette instance a changé. La conversation ne sera pas ouverte sur un autre compte.");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                var channel = await NamedInstances.Connect(context.Store, target.Accounts, target.Instance, watch.Workspace, timeout.Token, watch.ThreadId, false);
                await new DesktopRelayTransport().Call(channel, "navigate_to_codex_page", new { threadId = watch.ThreadId }, timeout.Token);
            }
        }
        private async Task AddWatch()
        {
            var accounts = await context.Read(a => a);
            var instances = accounts.Data.Instances.Where(i => !i.Archived && !String.IsNullOrEmpty(i.AccountKey)).ToArray();
            if (instances.Length == 0) throw new InvalidOperationException("Associez d’abord un compte à une instance.");
            var dialog = new EditWindow(this, "Surveiller une conversation", "Activer la surveillance");
            dialog.Fields.Children.Add(Ui.Text("Choisissez l’instance qui possède le chat. Copiez son lien depuis Codex. Le suivi ne transmet aucun message à l’agent.", 13, true));
            var labels = instances.Select(i => i.Name + " · " + (accounts.Data.Profiles.FirstOrDefault(p => p.Key == i.AccountKey)?.Email ?? "Compte associé")).ToArray();
            var instance = Ui.Select("Instance et compte", labels, labels[0], dialog.Fields);
            var name = Ui.Input("Nom de la mission", "", dialog.Fields);
            var thread = Ui.Input("Lien Codex ou identifiant de conversation", "", dialog.Fields);
            var folder = Ui.Input("Dossier de travail", Environment.CurrentDirectory, dialog.Fields);
            dialog.Fields.Children.Add(Ui.Text("Une conversation arrêtée déclenche une alerte tant que sa mission n’est pas confirmée terminée.", 13, true));
            var advanced = new StackPanel();
            var noProgress = Ui.Input("Prévenir après X minutes sans activité visible (0 = désactivé)", "15", advanced);
            advanced.Children.Add(Ui.Text("Une commande longue peut expliquer l’absence de nouveaux messages. Cette alerte invite à vérifier le chat ; elle ne prouve pas un blocage.", 12, true));
            var completion = Ui.Input("Fichier JSON de preuve de fin (facultatif)", "", advanced);
            var property = Ui.Input("Propriété de fin (ex. completion.entire18LotPlan)", "", advanced);
            var expected = Ui.Input("Valeur exacte attendue", "", advanced);
            dialog.Fields.Children.Add(new Expander { Header = "Alertes et critère de fin · avancé", Content = advanced });
            dialog.Save = async delegate {
                string value = thread.Text.Trim();
                const string prefix = "codex://threads/";
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value.Substring(prefix.Length).Split('?', '#')[0].TrimEnd('/');
                Guid id; if (!Guid.TryParse(value, out id)) throw new InvalidOperationException("Saisissez un lien codex://threads/… ou un identifiant de conversation valide.");
                if (String.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Renseignez le nom de la mission.");
                int quietMinutes; if (!Int32.TryParse(noProgress.Text, out quietMinutes) || (quietMinutes != 0 && (quietMinutes < 5 || quietMinutes > 240))) throw new InvalidOperationException("Le délai sans activité doit être 0 (désactivé) ou un nombre entier entre 5 et 240 minutes.");
                var target = instances[instance.SelectedIndex];
                // Snapshot UI values before the background write: WPF fields
                // belong to this dispatcher and must never be read in Task.Run.
                var record = new ChatWatchRecord { Name = name.Text.Trim(), InstanceId = target.Id, AccountKey = target.AccountKey, ThreadId = id.ToString(), Workspace = folder.Text.Trim(), NoProgressMinutes = quietMinutes, CompletionFile = completion.Text.Trim(), CompletionProperty = property.Text.Trim(), CompletionExpectedValue = expected.Text.Trim(), Enabled = true };
                var saved = await Task.Run(() => new ChatWatches(context.Store).Save(record));
                selectedId = saved.Id;
                if (!context.Fixture) await Task.Run(() => ChatWatchWorker.Ensure(context.Store));
            };
            dialog.ShowDialog();
            if (dialog.Saved) await Updated();
        }
        private async Task Updated() { fingerprint = null; error.Text = ""; await RefreshNow(); }
        private void ShowError(Exception e) { error.Text = Program.SafeError(e); }
    }
}
