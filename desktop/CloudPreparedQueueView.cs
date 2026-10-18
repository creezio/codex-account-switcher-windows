using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudQueueSourceChoice
    {
        public string Label { get; set; }
        internal RelaySession Source { get; set; }
    }

    // A passive local view. Only the worker admits work; displaying or refreshing
    // this page never polls Cloud or manufactures a new mission.
    internal sealed class CloudPreparedQueueView : UserControl
    {
        private readonly DesktopContext context;
        private readonly ShellWindow shell;
        internal readonly ComboBox Teams = new ComboBox { DisplayMemberPath = "Name", MinWidth = 180, MaxWidth = 360 };
        internal readonly ListBox Missions = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0) };
        internal readonly TextBox Search = new TextBox { MinHeight = 36 };
        internal readonly TextBlock Notice = Ui.Text("", 12, true), Error = Ui.Text("", 12);
        internal readonly TextBlock AlertText = Ui.Text("", 13), DetailsTitle = Ui.Text("", 20);
        internal readonly Button PauseButton, CancelButton, AcceptButton, RejectButton, ResumeMissionButton, AlertButton, ConfigureButton, NewButton;
        internal readonly ScrollViewer DetailScroll;
        internal Func<Task<CloudQueueSnapshot>> SnapshotFixture;
        internal Func<CloudQueueTeam, RelaySession, Task> ConfigureFixture;
        internal Func<CloudPreparedMission, RelaySession, Task> PrepareFixture;
        internal Func<string, bool, RelaySession, Task> PauseFixture;
        internal Func<string, RelaySession, Task> CancelFixture;
        internal Func<string, RelaySession, Task> ResumeMissionFixture;
        internal Func<CloudPreparedMission, RelaySession, Task> AcceptFixture;
        internal Func<CloudPreparedMission, RelaySession, Task> RejectFixture;
        internal Func<CloudQueueIdleAlert, RelaySession, Task> AcknowledgeFixture;
        internal Func<Task<CloudQueueSourceChoice[]>> SourcesFixture;
        internal Func<Task<CloudMissionAccountChoice[]>> AccountsFixture;
        internal Func<CloudQueueWorkerState> WorkerFixture;
        internal Func<CloudMissionAccountChoice, CancellationToken, Task<CloudConfiguration[]>> EnvironmentLoader;
        private readonly Grid split = new Grid(), header = new Grid();
        private readonly StackPanel detail = new StackPanel();
        private readonly Border alert;
        private readonly TextBlock[] counts = new TextBlock[4];
        private readonly Button[] filters = new Button[5];
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly TextBlock pool = Ui.Text("", 12, true), source = Ui.Text("", 12, true), empty = Ui.Text("", 14, true);
        private CloudQueueSnapshot snapshot;
        private CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool closed, reading, changing, acting;
        private int filter, generation;
        internal CloudQueueSnapshot Snapshot => snapshot;
        internal bool TimerRunning => timer.IsEnabled;
        internal string SelectedMissionId => (Missions.SelectedItem as MissionRow)?.Mission.Id;
        private sealed class MissionRow
        {
            public string Title { get; set; }
            public string Meta { get; set; }
            public string State { get; set; }
            public string Blocker { get; set; }
            internal CloudPreparedMission Mission;
        }
        internal CloudPreparedQueueView(DesktopContext context, ShellWindow shell)
        {
            this.context = context; this.shell = shell;
            var root = new Grid(); Content = root;
            for (int n = 0; n < 5; n++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel(); heading.Children.Add(Ui.Text("File préparée", 25)); heading.Children.Add(Ui.Text("Des mandats complets, prêts à partir quand leur compte et leurs dépendances le permettent.", 12, true)); header.Children.Add(heading);
            var commands = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            ConfigureButton = Ui.Button("Équipes…", () => ConfigureButton.ContextMenu.IsOpen = true);
            ConfigureButton.ContextMenu = new ContextMenu();
            var createTeam = new MenuItem { Header = "Créer une équipe" }; createTeam.Click += async delegate { try { await ConfigureTeam(true); } catch (Exception exception) { ShowError(exception); } }; ConfigureButton.ContextMenu.Items.Add(createTeam);
            var editTeam = new MenuItem { Header = "Configurer l’équipe sélectionnée" }; editTeam.Click += async delegate { try { await ConfigureTeam(); } catch (Exception exception) { ShowError(exception); } }; ConfigureButton.ContextMenu.Items.Add(editTeam);
            NewButton = Ui.AsyncButton("Préparer une mission", PrepareMission, ShowError, true);
            commands.Children.Add(ConfigureButton); commands.Children.Add(NewButton); Grid.SetColumn(commands, 1); header.Children.Add(commands); root.Children.Add(header);
            var teamBar = new Grid { Margin = new Thickness(0, 3, 0, 10) }; teamBar.ColumnDefinitions.Add(new ColumnDefinition()); teamBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var teamIdentity = new StackPanel { Orientation = Orientation.Horizontal }; teamIdentity.Children.Add(Teams);
            PauseButton = Ui.AsyncButton("Mettre en pause", ToggleTeam, ShowError); teamIdentity.Children.Add(PauseButton); teamBar.Children.Add(teamIdentity);
            var refresh = Ui.AsyncButton("Actualiser", RefreshNow, ShowError); Grid.SetColumn(refresh, 1); teamBar.Children.Add(refresh); Grid.SetRow(teamBar, 1); root.Children.Add(teamBar);
            AutomationProperties.SetName(Teams, "Équipe de la file préparée");
            var metrics = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 8) }; string[] captions = { "Prêtes", "En cours", "Bloquées", "À valider" };
            for (int n = 0; n < counts.Length; n++)
            {
                var body = new StackPanel(); counts[n] = Ui.Text("0", 23); counts[n].Margin = new Thickness(0); body.Children.Add(counts[n]); var caption = Ui.Text(captions[n], 12, true); caption.Margin = new Thickness(0); body.Children.Add(caption);
                var card = Ui.Card(body); card.Padding = new Thickness(12, 8, 12, 8); card.Margin = new Thickness(0, 0, n == 3 ? 0 : 8, 0); metrics.Children.Add(card); AutomationProperties.SetName(counts[n], "Missions préparées " + captions[n].ToLowerInvariant());
            }
            Grid.SetRow(metrics, 2); root.Children.Add(metrics);
            var alertBody = new Grid(); alertBody.ColumnDefinitions.Add(new ColumnDefinition()); alertBody.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AlertText.Margin = new Thickness(0); alertBody.Children.Add(AlertText); AlertButton = Ui.Button("Vu", async () => { try { await AcknowledgeAlert(); } catch (Exception exception) { ShowError(exception); } }); Grid.SetColumn(AlertButton, 1); alertBody.Children.Add(AlertButton);
            alert = Ui.Card(alertBody); alert.Padding = new Thickness(12, 9, 12, 9); alert.Margin = new Thickness(0, 0, 0, 8); alert.Visibility = Visibility.Collapsed; alert.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); Grid.SetRow(alert, 3); root.Children.Add(alert);
            AutomationProperties.SetName(AlertText, "Alerte durable de l’équipe Cloud"); AutomationProperties.SetLiveSetting(AlertText, AutomationLiveSetting.Polite);
            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 8) }; toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.Children.Add(Ui.SearchField(Search, "Chercher une mission, un lot ou une dépendance"));
            var filterBar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) }; var views = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(views, Dock.Left); filterBar.Children.Add(views);
            string[] viewNames = { "Toutes", "Prêtes", "En cours", "Bloquées", "À valider" };
            for (int n = 0; n < filters.Length; n++) { int selected = n; filters[n] = Ui.Button(viewNames[n], () => { filter = selected; Paint(); }); filters[n].Padding = new Thickness(9, 5, 9, 5); filters[n].MinHeight = 28; views.Children.Add(filters[n]); }
            var status = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; Notice.Margin = Error.Margin = new Thickness(0); Error.MaxWidth = 320; Error.MaxHeight = 34; Error.TextTrimming = TextTrimming.CharacterEllipsis; status.Children.Add(Notice); status.Children.Add(Error); filterBar.Children.Add(status); Grid.SetRow(filterBar, 1); toolbar.Children.Add(filterBar); Grid.SetRow(toolbar, 4); root.Children.Add(toolbar);
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 220 }); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); split.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(split, 5); root.Children.Add(split);
            var listHost = new Grid(); listHost.Children.Add(Missions); empty.IsHitTestVisible = false; empty.Margin = new Thickness(22); empty.VerticalAlignment = VerticalAlignment.Center; listHost.Children.Add(empty); split.Children.Add(listHost);
            Missions.SetResourceReference(BackgroundProperty, "CanvasBrush"); ScrollViewer.SetHorizontalScrollBarVisibility(Missions, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(Missions, ScrollBarVisibility.Auto); VirtualizingPanel.SetIsVirtualizing(Missions, true); VirtualizingPanel.SetVirtualizationMode(Missions, VirtualizationMode.Recycling); Missions.ItemTemplate = RowTemplate(); AutomationProperties.SetName(Missions, "Liste des missions préparées");
            var detailRoot = new Grid(); detailRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); detailRoot.RowDefinitions.Add(new RowDefinition()); detailRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); detailRoot.Children.Add(DetailsTitle);
            DetailScroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(DetailScroll, 1); detailRoot.Children.Add(DetailScroll);
            var actions = new WrapPanel(); CancelButton = Ui.AsyncButton("Annuler la mission", CancelSelected, ShowError); AcceptButton = Ui.AsyncButton("Valider après revue…", AcceptSelected, ShowError, true); RejectButton = Ui.AsyncButton("Écarter après vérification…", RejectSelected, ShowError); ResumeMissionButton = Ui.AsyncButton("Revérifier et reprendre", ResumeSelected, ShowError, true); actions.Children.Add(CancelButton); actions.Children.Add(AcceptButton); actions.Children.Add(RejectButton); actions.Children.Add(ResumeMissionButton); Grid.SetRow(actions, 2); detailRoot.Children.Add(actions);
            var detailCard = Ui.Card(detailRoot); detailCard.Margin = new Thickness(0); detailCard.Padding = new Thickness(16); Grid.SetColumn(detailCard, 2); split.Children.Add(detailCard);
            Teams.SelectionChanged += delegate { if (!changing) { filter = 0; Paint(); } }; Search.TextChanged += delegate { if (!changing) Paint(); }; Missions.SelectionChanged += delegate { if (!changing) ShowSelected(); };
            SizeChanged += delegate { split.ColumnDefinitions[0].Width = new GridLength(ActualWidth < 1050 ? 280 : 350); Grid.SetColumn(commands, ActualWidth < 760 ? 0 : 1); Grid.SetRow(commands, ActualWidth < 760 ? 1 : 0); };
            timer.Tick += async delegate { try { await RefreshNow(); } catch (Exception exception) { ShowError(exception); } };
            Loaded += async delegate { closed = false; generation++; if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new CancellationTokenSource(); } try { await RefreshNow(); } catch (Exception exception) { ShowError(exception); } if (!closed) timer.Start(); };
            Unloaded += delegate { Suspend(); }; ShowSelected();
        }
        internal void Suspend() { closed = true; generation++; timer.Stop(); lifetime.Cancel(); }
        internal async Task RefreshNow()
        {
            if (reading || closed) return; reading = true; int version = generation;
            try
            {
                var next = context.Fixture ? SnapshotFixture == null ? Empty() : await SnapshotFixture() : await context.Read(accounts => new CloudMissionQueue(context.Store).List());
                if (closed || version != generation) return;
                await ReadAccounts(); if (closed || version != generation) return;
                var worker = context.Fixture ? WorkerFixture?.Invoke() : context.Store.ReadRecord<CloudQueueWorkerState>(CloudQueueWorker.Record);
                string selected = (Teams.SelectedItem as CloudQueueTeam)?.Id; snapshot = next ?? Empty(); changing = true;
                try { Teams.ItemsSource = snapshot.Teams; Teams.SelectedItem = snapshot.Teams.FirstOrDefault(team => team.Id == selected) ?? snapshot.Teams.FirstOrDefault(); } finally { changing = false; }
                Notice.Text = "Lecture locale · " + DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture); Error.Text = String.IsNullOrEmpty(worker?.Error) ? "" : "Moteur de la file · " + worker.Error; Error.ToolTip = Error.Text; Paint();
            }
            finally { reading = false; }
        }
        private static CloudQueueSnapshot Empty() => new CloudQueueSnapshot { Teams = new List<CloudQueueTeam>(), Missions = new List<CloudPreparedMission>(), Alerts = new List<CloudQueueIdleAlert>() };
        internal static string Status(CloudPreparedMission mission) => mission.State == "queued" ? String.IsNullOrEmpty(mission.BlockReason) ? "Prête" : "En attente" : mission.State == "paused" ? "En pause" : mission.State == "claimed" ? "Attribution en cours" : mission.State == "submitted" ? "En cours" : mission.State == "attention" ? "Intervention nécessaire" : mission.State == "completed" ? "À valider" : mission.State == "accepted" ? "Validée après revue" : mission.State == "rejected" ? "Écartée après vérification" : mission.State == "uncertain" ? "Envoi à vérifier" : mission.State == "failed" ? "Échec à examiner" : mission.State == "cancelled" ? "Annulée" : mission.State;
        internal static string Reason(string code)
        {
            if (String.IsNullOrEmpty(code)) return "Toutes les conditions locales sont réunies ; le moteur revérifie les accès avant l’envoi.";
            switch (code)
            {
                case "global-paused": return "Les nouvelles attributions sont suspendues dans les réglages du Switcher.";
                case "team-paused": return "L’équipe est en pause.";
                case "team-disabled": return "L’équipe est désactivée.";
                case "mission-paused": return "Cette mission est en pause.";
                case "dependency": case "dependency-pending": case "dependencies": return "Une mission préalable doit être terminée ou validée après revue.";
                case "dependency-not-accepted": return "Une mission préalable attend la validation du chef après revue et intégration.";
                case "dependency-not-completed": return "Une mission préalable n’a pas encore terminé son travail.";
                case "dependency-failed": return "Une mission préalable a échoué ou a été écartée. Le chef doit réorganiser ce mandat.";
                case "capacity": case "global-capacity": case "capacity-full": return "La capacité de l’équipe ou la limite globale est occupée.";
                case "team-capacity": return "La capacité de cette équipe est occupée.";
                case "account-busy": case "target-busy": return "Les comptes autorisés sont déjà occupés.";
                case "files-busy": case "file-conflict": case "files-reserved": return "Un autre mandat réserve les mêmes fichiers jusqu’à sa revue ou son exclusion explicite.";
                case "legacy-files-unknown": return "Une mission extérieure à cette file utilise ce dépôt. Son périmètre doit être vérifié avant une attribution concurrente.";
                case "no-target": case "no-eligible-target": return "Aucun compte éligible n’est configuré pour cette mission.";
                case "source-binding": return "Le chat ou le compte orchestrateur doit être reconnecté.";
                case "native-attention": return "Codex Cloud demande une intervention. Ouvrez la mission dans l’onglet Missions pour traiter l’approbation, la question ou le blocage natif ; aucune réponse n’est donnée automatiquement.";
                case "intervention-required": return "Une mission attend une intervention dans Codex Cloud. Consultez les missions bloquées.";
                case "queue-repository-mismatch": return "Le dépôt réel de l’environnement diffère du dépôt du mandat. Aucun nouveau prompt métier n’est envoyé.";
                case "follow-up-conflict": return "Une correction nécessite des fichiers ou des préalables déjà repris. Le chef doit réorganiser les missions avant de la lancer.";
                case "configuration-changed": return "L’environnement a changé ; le mandat doit être vérifié.";
                case "target-unavailable": return "Les comptes ou environnements autorisés ne sont pas disponibles. Vérifiez la configuration de l’équipe.";
                case "awaiting-review": return "Le travail est terminé. Le chef doit revoir et intégrer son résultat avant de le valider.";
                case "submission-uncertain": return "L’envoi a pu partir mais sa confirmation est incertaine. Ne recréez pas ce mandat avant vérification.";
                case "observed-job-mismatch": return "La mission attribuée ne correspond plus au reçu enregistré. Une vérification est nécessaire.";
                case "team-binding-changed": return "Le chat orchestrateur ou un compte de l’équipe a changé depuis la préparation.";
                case "no-ready-mission": case "queue-empty": return "Aucune mission prête ne peut être attribuée. Préparez un mandat ou examinez ses dépendances.";
                case "empty-backlog": return "La file de cette équipe est vide. Le chef doit préparer les prochains mandats.";
                case "backlog-blocked": return "Les mandats préparés attendent une condition. Consultez les missions bloquées et leurs préalables.";
                case "review-required": return "Des livraisons attendent une revue ou une décision du chef.";
                case "execution-unconfirmed": return "Une attribution ou un envoi reste à confirmer. Vérifiez ce mandat avant d’en créer un autre.";
                default: return "Condition à examiner : " + code + ".";
            }
        }
        private static bool Running(CloudPreparedMission mission) => mission.State == "claimed" || mission.State == "submitted";
        private static bool Blocked(CloudPreparedMission mission) => mission.State == "attention" || mission.State == "paused" || mission.State == "failed" || mission.State == "uncertain" || mission.State == "queued" && !String.IsNullOrEmpty(mission.BlockReason);
        private void Paint()
        {
            var team = Teams.SelectedItem as CloudQueueTeam; var all = (snapshot?.Missions ?? new List<CloudPreparedMission>()).Where(mission => mission.TeamId == team?.Id).ToArray();
            int[] totals = { all.Count(m => m.State == "queued" && String.IsNullOrEmpty(m.BlockReason)), all.Count(Running), all.Count(Blocked), all.Count(m => m.State == "completed") }; for (int n = 0; n < counts.Length; n++) counts[n].Text = totals[n].ToString(CultureInfo.CurrentCulture);
            var listScroll = Ui.Descendants(Missions).OfType<ScrollViewer>().FirstOrDefault(); double listOffset = listScroll?.VerticalOffset ?? 0, detailOffset = DetailScroll.VerticalOffset;
            string selected = SelectedMissionId; string query = Search.Text.Trim(); var filtered = all.Where(mission => filter == 0 || filter == 1 && mission.State == "queued" && String.IsNullOrEmpty(mission.BlockReason) || filter == 2 && Running(mission) || filter == 3 && Blocked(mission) || filter == 4 && mission.State == "completed").Where(mission => query.Length == 0 || (mission.Title + " " + mission.Feature + " " + mission.Lot + " " + String.Join(" ", (mission.Dependencies ?? new List<CloudQueueDependency>()).Select(value => value.MissionId))).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).OrderByDescending(mission => mission.Priority).ThenBy(mission => mission.Sequence).ThenBy(mission => mission.CreatedUtc, StringComparer.Ordinal).Select(mission => new MissionRow { Mission = mission, Title = mission.Title, Meta = "Priorité " + mission.Priority + " · " + mission.Lot + " · " + Assignment(mission, team), State = Status(mission), Blocker = String.IsNullOrEmpty(mission.BlockReason) ? "" : Reason(mission.BlockReason) }).ToArray();
            changing = true; try { Missions.ItemsSource = filtered; Missions.SelectedItem = filtered.FirstOrDefault(row => row.Mission.Id == selected) ?? filtered.FirstOrDefault(); } finally { changing = false; }
            for (int n = 0; n < filters.Length; n++) filters[n].FontWeight = n == filter ? FontWeights.SemiBold : FontWeights.Normal;
            empty.Text = team == null ? "Créez une équipe, puis préparez sa première mission." : all.Length == 0 ? "Aucun mandat préparé pour cette équipe." : "Aucune mission dans cette vue."; empty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PauseButton.IsEnabled = team != null && !acting; PauseButton.Content = team?.Paused == true ? "Reprendre l’équipe" : "Mettre en pause"; NewButton.IsEnabled = team != null && !acting;
            var currentAlert = snapshot?.Alerts?.Where(value => value.TeamId == team?.Id && value.Active).OrderByDescending(value => value.RaisedUtc, StringComparer.Ordinal).FirstOrDefault();
            alert.Visibility = currentAlert == null ? Visibility.Collapsed : Visibility.Visible;
            if (currentAlert != null) { AlertText.Text = "Équipe sans activité confirmée · " + Reason(currentAlert.Reason) + (String.IsNullOrEmpty(currentAlert.AcknowledgedUtc) ? "" : " · Signalement vu"); alert.ToolTip = "Depuis " + CloudMissionPresentation.AbsoluteTime(currentAlert.SinceUtc); AlertButton.IsEnabled = !acting && String.IsNullOrEmpty(currentAlert.AcknowledgedUtc); }
            ShowSelected();
            if (selected != null && selected == SelectedMissionId) { Missions.UpdateLayout(); DetailScroll.UpdateLayout(); listScroll?.ScrollToVerticalOffset(listOffset); DetailScroll.ScrollToVerticalOffset(detailOffset); }
        }
        private string Assignment(CloudPreparedMission mission, CloudQueueTeam team)
        {
            if (mission.Target != null) return Label(mission.Target.InstanceId);
            int eligible = (mission.EligibleInstanceIds ?? new List<string>()).Count;
            return eligible == 0 ? "Aucun compte autorisé" : eligible + " compte(s) éligible(s)";
        }
        private string Label(string id) => accountLabels.TryGetValue(id ?? "", out string label) ? label : "Instance " + (id == null ? "absente" : id.Substring(0, Math.Min(8, id.Length)));
        private readonly Dictionary<string, string> accountLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        private void AddSection(string label, UIElement body) { var heading = Ui.Text(label, 10, true); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 12, 0, 5); detail.Children.Add(heading); detail.Children.Add(body); }
        private void ShowSelected()
        {
            detail.Children.Clear(); var team = Teams.SelectedItem as CloudQueueTeam; var mission = (Missions.SelectedItem as MissionRow)?.Mission;
            DetailsTitle.Text = mission?.Title ?? (team?.Name ?? "Votre équipe Cloud");
            CancelButton.Visibility = mission != null && new[] { "queued", "paused" }.Contains(mission.State) ? Visibility.Visible : Visibility.Collapsed; AcceptButton.Visibility = mission?.State == "completed" ? Visibility.Visible : Visibility.Collapsed; RejectButton.Visibility = (mission?.State == "failed" || mission?.State == "attention" && String.IsNullOrEmpty(mission.ObservedThreadId) && String.IsNullOrEmpty(mission.ObservedTurnId)) && !String.IsNullOrEmpty(mission.ObservedJobId) ? Visibility.Visible : Visibility.Collapsed; ResumeMissionButton.Visibility = CanResumeBinding(mission) ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = AcceptButton.IsEnabled = RejectButton.IsEnabled = ResumeMissionButton.IsEnabled = !acting;
            if (team == null) { detail.Children.Add(Ui.Text("Une équipe relie un chat orchestrateur, un dépôt et un ensemble de comptes autorisés. Le Switcher distribue uniquement les mandats que vous avez préparés.", 14, true)); return; }
            source.Text = team.Source == null ? "Orchestrateur absent" : "Chat " + team.Source.Thread + " · " + team.Source.Channel; pool.Text = String.Join("\n", (team.Targets ?? new List<CloudQueueTarget>()).Select(target => Label(target.InstanceId) + " · " + (target.ConfigurationName ?? "Environnement " + Short(target.ConfigurationId))));
            if (mission == null) { AddSection("DÉPÔT", Ui.Text(team.Repository, 14)); AddSection("ORCHESTRATEUR", source); AddSection("COMPTES ET ENVIRONNEMENTS", pool); AddSection("ORGANISATION", Ui.Text("Au plus " + team.MaximumParallel + " missions de cette équipe à la fois, dans la limite globale. Les dépendances attendent une validation après revue par défaut.", 13, true)); return; }
            AddSection("SITUATION", Ui.Text(Status(mission) + " · " + Assignment(mission, team), 14)); if (!String.IsNullOrEmpty(mission.BlockReason) || !String.IsNullOrEmpty(mission.ErrorCode)) AddSection("POURQUOI CETTE MISSION ATTEND", Ui.Text(Reason(mission.BlockReason ?? mission.ErrorCode) + (mission.ErrorHttpStatus.HasValue ? " (HTTP " + mission.ErrorHttpStatus.Value + ")" : ""), 13));
            AddSection("LIVRAISON", Ui.Text(mission.Repository + "\n" + mission.Feature + " · " + mission.Lot + (String.IsNullOrEmpty(mission.Specification) ? "" : "\n" + mission.Specification), 13));
            AddSection("MANDAT", Ui.Text(mission.Prompt ?? "", 13));
            var links = new StackPanel(); foreach (var dependency in mission.Dependencies ?? new List<CloudQueueDependency>()) { var parent = snapshot.Missions.FirstOrDefault(value => value.Id == dependency.MissionId); var link = Ui.Button((parent?.Title ?? dependency.MissionId) + (dependency.Mode == "completed" ? " · terminée" : " · validée après revue"), () => SelectMission(dependency.MissionId)); link.ToolTip = dependency.MissionId; links.Children.Add(link); } if (links.Children.Count == 0) links.Children.Add(Ui.Text("Aucune dépendance.", 13, true)); AddSection("PRÉALABLES", links);
            AddSection("COMPTES AUTORISÉS", Ui.Text(String.Join("\n", (mission.EligibleInstanceIds ?? new List<string>()).Select(id => { var target = (mission.EligibleTargets ?? new List<CloudQueueTarget>()).FirstOrDefault(value => value.InstanceId == id) ?? team.Targets.FirstOrDefault(value => value.InstanceId == id); return Label(id) + (target == null ? "" : " · " + (target.ConfigurationName ?? "Environnement " + Short(target.ConfigurationId))); })), 13));
            AddSection("RÉGLAGES FIGÉS", Ui.Text((mission.Model ?? "Réglage natif") + " · " + (mission.ReasoningEffort ?? "effort natif") + " · " + (mission.PermissionProfile ?? "permissions natives"), 12, true));
            if ((mission.ReservedFiles?.Count ?? 0) > 0) AddSection("FICHIERS RÉSERVÉS", Ui.Text(String.Join("\n", mission.ReservedFiles), 12, true));
            if (!String.IsNullOrEmpty(mission.ObservedJobId)) AddSection("MISSION ATTRIBUÉE", Ui.Text("Job " + mission.ObservedJobId + "\nChat " + mission.ObservedThreadId + "\nTour " + mission.ObservedTurnId, 11, true));
            AddSection("ORCHESTRATEUR", source);
        }
        internal void SelectMission(string id)
        {
            var found = snapshot?.Missions?.FirstOrDefault(value => value.Id == id); if (found == null) return;
            var team = snapshot.Teams.FirstOrDefault(value => value.Id == found.TeamId); changing = true; try { Teams.SelectedItem = team; Search.Text = ""; filter = 0; } finally { changing = false; } Paint(); Missions.SelectedItem = Missions.Items.Cast<MissionRow>().FirstOrDefault(row => row.Mission.Id == id); if (Missions.SelectedItem != null) Missions.ScrollIntoView(Missions.SelectedItem);
        }
        private static string Short(string value) => String.IsNullOrEmpty(value) ? "absent" : value.Substring(0, Math.Min(18, value.Length));
        private static DataTemplate RowTemplate()
        {
            var body = new FrameworkElementFactory(typeof(StackPanel)); body.SetValue(FrameworkElement.MarginProperty, new Thickness(11, 10, 11, 10));
            foreach (var binding in new[] { "Title", "Meta", "State", "Blocker" }) { var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(binding)); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.FontSizeProperty, binding == "Title" ? 14.0 : 11.0); text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 4)); if (binding == "Title") text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); body.AppendChild(text); }
            return new DataTemplate { VisualTree = body };
        }
        private void ShowError(Exception exception) { Error.Text = Program.SafeError(exception); }
        private async Task RunMutation(Func<Task> mutation)
        {
            if (acting || closed) return; acting = true; Paint();
            try { await mutation(); if (!context.Fixture) CloudQueueWorker.Ensure(context.Store); await RefreshNow(); }
            finally { acting = false; if (!closed) Paint(); }
        }
        private Task ToggleTeam()
        {
            var team = Teams.SelectedItem as CloudQueueTeam; if (team == null) return Task.CompletedTask;
            return RunMutation(async () => { if (context.Fixture) { if (PauseFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await PauseFixture(team.Id, !team.Paused, team.Source); } else await context.Read(accounts => new CloudMissionQueue(context.Store).SetTeamPaused(team.Id, !team.Paused, team.Source)); });
        }
        private Task CancelSelected()
        {
            var mission = (Missions.SelectedItem as MissionRow)?.Mission; var team = Teams.SelectedItem as CloudQueueTeam; if (mission == null || team == null) return Task.CompletedTask;
            return RunMutation(async () => { if (context.Fixture) { if (CancelFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await CancelFixture(mission.Id, team.Source); } else await context.Read(accounts => new CloudMissionQueue(context.Store).Cancel(mission.Id, team.Source)); });
        }
        private static bool CanResumeBinding(CloudPreparedMission mission) => mission != null && (mission.State == "claimed" || mission.State == "uncertain") && mission.ErrorCode == "team-binding-changed" && String.IsNullOrEmpty(mission.ObservedJobId);
        private Task ResumeSelected()
        {
            var mission = (Missions.SelectedItem as MissionRow)?.Mission; var team = Teams.SelectedItem as CloudQueueTeam;
            if (!CanResumeBinding(mission) || team == null) return Task.CompletedTask;
            return RunMutation(async () => { if (context.Fixture) { if (ResumeMissionFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await ResumeMissionFixture(mission.Id, team.Source); } else await context.Read(accounts => new CloudMissionQueue(context.Store).SetMissionPaused(mission.Id, false, team.Source)); });
        }
        internal Func<bool> ConfirmAcceptanceFixture;
        internal Func<bool> ConfirmRejectionFixture;
        private Task AcceptSelected()
        {
            var mission = (Missions.SelectedItem as MissionRow)?.Mission; var team = Teams.SelectedItem as CloudQueueTeam; if (mission == null || team == null) return Task.CompletedTask;
            if (!(ConfirmAcceptanceFixture?.Invoke() ?? (!context.Fixture && Ui.Confirm(Window.GetWindow(this) ?? shell, "Confirmez uniquement après avoir revu et intégré le résultat attendu. Cette validation débloque les missions dépendantes ; elle ne fusionne aucune branche GitHub.", "Valider après revue")))) return Task.CompletedTask;
            return RunMutation(async () => { if (context.Fixture) { if (AcceptFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await AcceptFixture(mission, team.Source); } else await context.Read(accounts => new CloudMissionQueue(context.Store).Accept(mission.Id, mission.ObservedJobId, mission.ObservedThreadId, mission.ObservedTurnId, team.Source)); });
        }
        private Task AcknowledgeAlert()
        {
            var team = Teams.SelectedItem as CloudQueueTeam; var current = snapshot?.Alerts?.FirstOrDefault(value => value.TeamId == team?.Id && value.Active); if (current == null || team == null) return Task.CompletedTask;
            return RunMutation(async () => { if (context.Fixture) { if (AcknowledgeFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await AcknowledgeFixture(current, team.Source); } else await context.Read(accounts => new CloudMissionQueue(context.Store).AcknowledgeIdle(team.Id, current.EpisodeId, team.Source)); });
        }
        private Task RejectSelected()
        {
            var mission = (Missions.SelectedItem as MissionRow)?.Mission; var team = Teams.SelectedItem as CloudQueueTeam; if (mission == null || team == null) return Task.CompletedTask;
            if (!(ConfirmRejectionFixture?.Invoke() ?? (!context.Fixture && Ui.Confirm(Window.GetWindow(this) ?? shell, "Confirmez après avoir vérifié l’échec et ses éventuels fichiers modifiés. Les réservations de ce mandat seront libérées. Ses dépendants resteront bloqués : ce résultat ne sera pas considéré comme accepté.", "Écarter après vérification")))) return Task.CompletedTask;
            return RunMutation(async () => { if (context.Fixture) { if (RejectFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await RejectFixture(mission, team.Source); } else await context.Read(accounts => new CloudMissionQueue(context.Store).Reject(mission.Id, mission.ObservedJobId, mission.ObservedThreadId, mission.ObservedTurnId, team.Source)); });
        }
        private async Task<CloudMissionAccountChoice[]> ReadAccounts()
        {
            var choices = context.Fixture && AccountsFixture != null ? await AccountsFixture() : await context.Read(accounts => accounts.Data.Instances.Where(instance => !instance.Archived && !String.IsNullOrEmpty(instance.AccountKey)).Select(instance => new CloudMissionAccountChoice { Id = instance.Id, AccountKey = instance.AccountKey, Label = instance.Name + " · " + accounts.Data.Profiles.Where(profile => profile.Key == instance.AccountKey).Select(profile => profile.Email).FirstOrDefault() }).ToArray());
            foreach (var choice in choices) accountLabels[choice.Id] = choice.Label; return choices;
        }
        private async Task<CloudQueueSourceChoice[]> ReadSources()
        {
            if (context.Fixture) return SourcesFixture == null ? new CloudQueueSourceChoice[0] : await SourcesFixture();
            return await context.Read(accounts => CloudQueueSourceLabels.Choices(context.Store.Channels(), new CloudJobs(context.Store).List()));
        }
        private async Task ConfigureTeam(bool create = false)
        {
            var accounts = await ReadAccounts(); var sources = await ReadSources(); if (closed) return;
            if (sources.Length == 0) throw new InvalidOperationException("Connectez d’abord le chat orchestrateur au relais depuis Codex. Son canal et sa conversation apparaîtront ici sans saisir de jeton.");
            var selected = create ? null : Teams.SelectedItem as CloudQueueTeam;
            var form = new CloudQueueTeamWizard(Window.GetWindow(this) ?? shell, accounts, sources, EnvironmentLoader ?? ((choice, token) => Task.FromResult(new CloudConfiguration[0])), async (team, sourceChoice) => {
                if (context.Fixture) { if (ConfigureFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await ConfigureFixture(team, sourceChoice); } else await context.Read(service => new CloudMissionQueue(context.Store).ConfigureTeam(team, sourceChoice));
                if (!context.Fixture) CloudQueueWorker.Ensure(context.Store);
            }, lifetime.Token, selected);
            form.ShowDialog(); if (form.Saved && !closed) await RefreshNow();
        }
        private async Task PrepareMission()
        {
            var team = Teams.SelectedItem as CloudQueueTeam; if (team == null) return; var choices = await ReadAccounts(); if (closed) return;
            var defaults = CloudMissionPreferences.Read(context.Store);
            var form = new CloudPreparedMissionWizard(Window.GetWindow(this) ?? shell, team, snapshot.Missions, choices, defaults, async mission => {
                if (context.Fixture) { if (PrepareFixture == null) throw new InvalidOperationException("Aucune mutation réelle depuis la recette."); await PrepareFixture(mission, team.Source); } else await context.Read(service => new CloudMissionQueue(context.Store).Prepare(mission, team.Source));
                if (!context.Fixture) CloudQueueWorker.Ensure(context.Store);
            }, lifetime.Token);
            form.ShowDialog(); if (form.Saved && !closed) { await RefreshNow(); SelectMission(form.MissionId); }
        }
    }
}
