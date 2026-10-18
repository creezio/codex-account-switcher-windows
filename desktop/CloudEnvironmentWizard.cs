using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    public sealed class CloudEnvironmentPreferences
    {
        public string NewEnvironmentNetwork { get; set; } = "disabled";
        internal const string Record = "cloud-environment-preferences.dpapi";
        internal static readonly string[] Policies = { "disabled", "restricted", "unrestricted" };
        internal static readonly string[] Labels = { "Internet désactivé", "Gestionnaires de paquets uniquement", "Internet : toutes les adresses" };
        internal static int Index(string policy) => Math.Max(0, Array.IndexOf(Policies, policy));
    }
    internal sealed class CloudEnvironmentAccountChoice
    {
        public string Id { get; set; }
        public string AccountKey { get; set; }
        public string Label { get; set; }
    }
    internal sealed class CloudEnvironmentWizardIntent
    {
        public string Id { get; set; }
        public CloudEnvironmentAccountChoice Account { get; set; }
        public string ConfigurationId { get; set; }
        public CloudConfigurationCreate Create { get; set; }
        public CloudDraftPatch Patch { get; set; }
    }
    internal sealed class CloudEnvironmentRepositoryRow : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Depth { get; set; }
        public bool Missing { get; set; }
        public string OriginalRef { get; set; }
        private bool selected; private string reference;
        public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); Changed?.Invoke(); } }
        public string Ref { get => reference; set { reference = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ref))); Changed?.Invoke(); } }
        internal Action Changed;
        public event PropertyChangedEventHandler PropertyChanged;
    }
    internal sealed class CloudEnvironmentWizard : Window
    {
        internal readonly ComboBox Account = new ComboBox { DisplayMemberPath = "Label" }, Network = new ComboBox();
        internal readonly TextBox Search = new TextBox(), EnvironmentName = new TextBox { MaxLength = 256 }, Directory = new TextBox { MaxLength = 4096 };
        internal readonly TextBox Install = Multiline(), Start = Multiline(), Review = Multiline(true);
        internal readonly DataGrid Repositories = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, RowHeaderWidth = 0, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, BorderThickness = new Thickness(1), MinHeight = 80 };
        internal readonly Button NextButton, BackButton, RetryButton;
        internal readonly TextBlock Status = Ui.Text("", 12, true), ErrorText = Ui.Text("", 12);
        internal Func<bool> ConfirmDiscard;
        internal string IntentId { get; } = Guid.NewGuid().ToString("N");
        internal bool Saved { get; private set; }
        internal int Step { get; private set; }
        internal bool Loading => loading;
        internal bool Submitting => submitting;
        internal bool IntentLocked => snapshot != null;
        internal IReadOnlyList<CloudEnvironmentRepositoryRow> RepositoryRows => rows;
        private readonly Func<CloudEnvironmentAccountChoice, CancellationToken, Task<CloudGitHubRepository[]>> load;
        private readonly Func<CloudEnvironmentWizardIntent, CancellationToken, Task> submit;
        private readonly CloudConfiguration original;
        private readonly int initialNetworkIndex;
        private readonly CancellationTokenSource lifetime;
        private CancellationTokenSource loadCancellation, reviewCancellation;
        private readonly List<CloudEnvironmentRepositoryRow> rows = new List<CloudEnvironmentRepositoryRow>();
        private readonly Grid[] pages = new Grid[5];
        private readonly Border[] markers = new Border[5];
        private readonly StackPanel accountPanel = new StackPanel();
        private readonly TextBlock subtitle = Ui.Text("", 13, true), count = Ui.Text("", 12, true), stopped = Ui.Text("", 12, true);
        private readonly ProgressBar progress = new ProgressBar { Height = 3, IsIndeterminate = true, Margin = new Thickness(0, 2, 0, 7) };
        private readonly Button cancel;
        private long loadGeneration, reviewGeneration;
        private bool closed, loaded, loading, submitting, reviewReady;
        private string loadedAccount;
        private CloudEnvironmentWizardIntent snapshot;

        internal CloudEnvironmentWizard(Window owner, CloudEnvironmentAccountChoice[] choices,
            Func<CloudEnvironmentAccountChoice, CancellationToken, Task<CloudGitHubRepository[]>> loadRepositories,
            Func<CloudEnvironmentWizardIntent, CancellationToken, Task> submit, CancellationToken token,
            string initialInstanceId = null, CloudConfiguration edit = null, string defaultNetwork = "disabled")
        {
            load = loadRepositories; this.submit = submit; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            original = edit == null ? null : Json.Read<CloudConfiguration>(Json.Write(edit));
            initialNetworkIndex = original == null ? CloudEnvironmentPreferences.Index(defaultNetwork) : 0;
            var accounts = choices.Select(value => new CloudEnvironmentAccountChoice { Id = value.Id, AccountKey = value.AccountKey, Label = value.Label }).ToArray();
            if (original != null && (accounts.Length != 1 || !String.IsNullOrEmpty(initialInstanceId) && accounts[0].Id != initialInstanceId)) throw new InvalidOperationException("La modification doit cibler uniquement le compte propriétaire de cet environnement.");
            Owner = owner; Title = original == null ? "Créer un environnement Cloud" : "Modifier l’environnement Cloud"; ShowInTaskbar = false;
            Width = Math.Min(780, SystemParameters.WorkArea.Width); Height = Math.Min(720, SystemParameters.WorkArea.Height); MinWidth = 620; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14; SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new Grid { Margin = new Thickness(22, 18, 22, 16) }; root.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush"); Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new StackPanel(); root.Children.Add(header); header.Children.Add(Ui.Text(Title, 23)); header.Children.Add(subtitle);
            var steps = new UniformGrid { Columns = 5, Margin = new Thickness(0, 5, 0, 12) }; header.Children.Add(steps);
            var names = new[] { "Compte", "Dépôts", "Réglages", "Démarrage", "Vérifier" };
            for (int i = 0; i < 5; i++)
            {
                var label = Ui.Text((i + 1) + "  " + names[i], 11); label.Margin = new Thickness();
                markers[i] = new Border { Child = label, Padding = new Thickness(8, 8, 4, 8), Margin = new Thickness(0, 0, i == 4 ? 0 : 5, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) }; steps.Children.Add(markers[i]);
            }
            accountPanel.Children.Add(Ui.Text("Quel compte possède cet environnement ?", 15)); accountPanel.Children.Add(Choice(Account, "Choisir un compte")); Account.ItemsSource = accounts; AutomationProperties.SetName(Account, "Compte propriétaire de l’environnement Cloud");
            if (original != null) accountPanel.Children.Add(Ui.Text("Compte propriétaire verrouillé. Modifier un environnement ne le transfère jamais vers un autre compte.", 12, true));
            Grid.SetRow(accountPanel, 1); root.Children.Add(accountPanel);
            var body = new Grid { Margin = new Thickness(0, 8, 0, 8) }; Grid.SetRow(body, 2); root.Children.Add(body);
            for (int i = 0; i < pages.Length; i++) { pages[i] = new Grid(); body.Children.Add(pages[i]); }
            pages[0].Children.Add(Ui.Text("Les dépôts GitHub accessibles à ce compte apparaîtront automatiquement. Aucun dépôt ne sera choisi à votre place.", 14, true));
            var repositoryPage = pages[1]; repositoryPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); repositoryPage.RowDefinitions.Add(new RowDefinition()); repositoryPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var repositoryHeader = new StackPanel(); repositoryPage.Children.Add(repositoryHeader); repositoryHeader.Children.Add(Ui.SearchField(Search, "Rechercher un dépôt GitHub")); repositoryHeader.Children.Add(progress); repositoryHeader.Children.Add(Status);
            Status.MaxHeight = 58; Status.TextTrimming = TextTrimming.CharacterEllipsis;
            var check = new FrameworkElementFactory(typeof(CheckBox)); check.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 3, 8, 3)); check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(CloudEnvironmentRepositoryRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(CloudEnvironmentRepositoryRow.Name)));
            Repositories.Columns.Add(new DataGridTemplateColumn { Header = "Inclure", CellTemplate = new DataTemplate { VisualTree = check }, Width = new DataGridLength(60) });
            var repositoryNameStyle = new Style(typeof(TextBlock)); repositoryNameStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); repositoryNameStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap)); repositoryNameStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(CloudEnvironmentRepositoryRow.Name))));
            Repositories.Columns.Add(new DataGridTextColumn { Header = "Dépôt GitHub", Binding = new Binding(nameof(CloudEnvironmentRepositoryRow.Name)), ElementStyle = repositoryNameStyle, IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            Repositories.Columns.Add(new DataGridTextColumn { Header = "Branche ou référence", Binding = new Binding(nameof(CloudEnvironmentRepositoryRow.Ref)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(180) });
            Repositories.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush"); Repositories.SetResourceReference(Control.ForegroundProperty, "TextBrush"); Repositories.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
            var repositoryRowStyle = new Style(typeof(DataGridRow)); repositoryRowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(CloudEnvironmentRepositoryRow.Id)))); Repositories.RowStyle = repositoryRowStyle;
            AutomationProperties.SetName(Repositories, "Dépôts et références Git de l’environnement"); Grid.SetRow(Repositories, 1); repositoryPage.Children.Add(Repositories);
            var repositoryFooter = new Grid(); repositoryFooter.ColumnDefinitions.Add(new ColumnDefinition()); repositoryFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); repositoryFooter.Children.Add(count);
            RetryButton = Ui.Button("Réessayer", () => { if (!loading && snapshot == null && Account.SelectedItem is CloudEnvironmentAccountChoice choice) _ = LoadRepositories(choice); }); Grid.SetColumn(RetryButton, 1); repositoryFooter.Children.Add(RetryButton); Grid.SetRow(repositoryFooter, 2); repositoryPage.Children.Add(repositoryFooter);
            var settings = new StackPanel(); pages[2].Children.Add(settings); settings.Children.Add(Ui.Text("Nom de l’environnement", 13)); settings.Children.Add(EnvironmentName);
            EnvironmentName.Text = original?.Name ?? ""; EnvironmentName.IsReadOnly = original != null; AutomationProperties.SetName(EnvironmentName, "Nom de l’environnement Cloud");
            if (original != null) settings.Children.Add(Ui.Text("Pour changer le nom, utilisez l’action Renommer de l’environnement.", 12, true));
            settings.Children.Add(Ui.Text("Accès Internet", 13)); settings.Children.Add(Network);
            Network.ItemsSource = original == null ? CloudEnvironmentPreferences.Labels : new[] { "Conserver la règle actuelle" }.Concat(CloudEnvironmentPreferences.Labels).ToArray(); Network.SelectedIndex = initialNetworkIndex; AutomationProperties.SetName(Network, "Accès réseau de l’environnement Cloud");
            const string networkHelp = "Toutes les adresses autorise les connexions Internet sortantes. Les identifiants restent limités à leurs domaines autorisés. Le choix par défaut des nouvelles créations se règle dans Paramètres → Environnements Cloud.";
            Network.ToolTip = networkHelp; AutomationProperties.SetHelpText(Network, networkHelp);
            settings.Children.Add(Ui.Text("Dossier de travail Cloud", 13)); settings.Children.Add(Directory); Directory.Text = original?.Cwd ?? ""; AutomationProperties.SetName(Directory, "Dossier de travail de l’environnement Cloud");
            settings.Children.Add(Ui.Text("Chemin absolu, par exemple /workspace. Laissez vide à la création pour utiliser le dossier défini par Codex.", 12, true));
            var startup = pages[3]; startup.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); startup.RowDefinitions.Add(new RowDefinition()); startup.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); startup.RowDefinitions.Add(new RowDefinition());
            startup.Children.Add(Ui.Text("Commandes d’installation (facultatif)", 13)); Grid.SetRow(Install, 1); startup.Children.Add(Install); Install.Text = original?.InstallScript ?? "";
            var startLabel = Ui.Text("Instructions de démarrage (facultatif)", 13); startLabel.Margin = new Thickness(0, 8, 0, 6); Grid.SetRow(startLabel, 2); startup.Children.Add(startLabel); Grid.SetRow(Start, 3); startup.Children.Add(Start); Start.Text = original?.StartSkill ?? "";
            AutomationProperties.SetName(Install, "Commandes d’installation Cloud"); AutomationProperties.SetName(Start, "Instructions de démarrage Cloud");
            pages[4].Children.Add(Review); AutomationProperties.SetName(Review, "Récapitulatif de l’environnement Cloud");
            var footer = new StackPanel(); Grid.SetRow(footer, 3); root.Children.Add(footer); ErrorText.Foreground = Brushes.Firebrick; ErrorText.MaxHeight = 58; footer.Children.Add(ErrorText); stopped.MaxHeight = 42; footer.Children.Add(stopped);
            AutomationProperties.SetLiveSetting(Status, AutomationLiveSetting.Polite); AutomationProperties.SetLiveSetting(ErrorText, AutomationLiveSetting.Polite);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(actions);
            BackButton = Ui.Button("Retour", () => { if (snapshot == null && Step > 0) ShowStep(Step - 1); }); actions.Children.Add(BackButton);
            cancel = Ui.Button("Annuler", Close); Grid.SetColumn(cancel, 2); actions.Children.Add(cancel);
            NextButton = Ui.Button("Continuer", async () => await Next(), true); NextButton.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(NextButton, 3); actions.Children.Add(NextButton);
            Search.TextChanged += delegate { Filter(); }; Account.SelectionChanged += delegate { if (closed || snapshot != null) return; Search.Text = ""; if (Account.SelectedItem is CloudEnvironmentAccountChoice choice) { ShowStep(1); _ = LoadRepositories(choice); } };
            Network.SelectionChanged += delegate { UpdateNavigation(); };
            foreach (var field in new[] { EnvironmentName, Directory, Install, Start }) field.TextChanged += delegate { if (snapshot == null) { ErrorText.Text = ""; UpdateNavigation(); } };
            ConfirmDiscard = () => Ui.Confirm(this, "Abandonner la configuration de cet environnement ?", "Configuration en cours");
            Closing += delegate(object sender, CancelEventArgs args) { if (submitting && !Saved) { args.Cancel = true; return; } if (!Saved && Dirty() && !ConfirmDiscard()) args.Cancel = true; };
            Closed += delegate { closed = true; ++loadGeneration; CancelReview(); loadCancellation?.Cancel(); lifetime.Cancel(); lifetime.Dispose(); };
            ShowStep(0);
            var initial = accounts.SingleOrDefault(choice => choice.Id == initialInstanceId) ?? (original != null ? accounts.Single() : null); if (initial != null) Account.SelectedItem = initial;
        }

        private static TextBox Multiline(bool readOnly = false) => new TextBox { IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, MaxLength = 262144, MinHeight = 40 };
        private static Grid Choice(ComboBox choice, string hint)
        {
            var root = new Grid(); root.Children.Add(choice); var text = Ui.Text(hint, 14, true); text.Margin = new Thickness(10, 0, 32, 10); text.VerticalAlignment = VerticalAlignment.Center; text.IsHitTestVisible = false; root.Children.Add(text);
            choice.SelectionChanged += delegate { text.Visibility = choice.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed; choice.ToolTip = (choice.SelectedItem as CloudEnvironmentAccountChoice)?.Label; }; return root;
        }
        private bool Dirty() => !String.IsNullOrEmpty(EnvironmentName.Text) && original == null || EnvironmentName.Text != (original?.Name ?? "") || Install.Text != (original?.InstallScript ?? "") || Start.Text != (original?.StartSkill ?? "") || Directory.Text != (original?.Cwd ?? "") || Network.SelectedIndex != initialNetworkIndex || rows.Any(row => row.Selected != (original?.Repositories?.Any(repo => repo.RepositoryId == row.Id) ?? false) || row.Selected && row.Ref != row.OriginalRef);
        private void ShowStep(int step)
        {
            CancelReview(); Step = step; ErrorText.Text = "";
            for (int i = 0; i < pages.Length; i++) { pages[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed; markers[i].SetResourceReference(Border.BackgroundProperty, i == step ? "AccentSoftBrush" : "CanvasBrush"); markers[i].SetResourceReference(Border.BorderBrushProperty, i <= step ? "AccentBrush" : "LineBrush"); }
            accountPanel.Visibility = step <= 1 ? Visibility.Visible : Visibility.Collapsed;
            subtitle.Text = new[] { "Choisissez le compte qui conserve les accès et les quotas.", "Sélectionnez un ou plusieurs dépôts, puis vérifiez leurs références.", "Nommez l’environnement et choisissez son cadre d’exécution.", "Préparez l’installation et les instructions qui seront utilisées dans le Cloud.", "Vérifiez toute la demande avant un envoi unique." }[step];
            if (step == 4) { Review.Text = Describe(BuildIntent()); _ = ArmReview(); }
            UpdateNavigation();
        }
        private void UpdateNavigation()
        {
            if (closed || NextButton == null) return;
            bool pinned = Account.SelectedItem is CloudEnvironmentAccountChoice selected && loadedAccount == Key(selected);
            NextButton.Content = Step == 4 ? original == null ? "Créer l’environnement" : "Enregistrer et publier" : "Continuer";
            NextButton.IsEnabled = snapshot == null && !submitting && (Step == 0 ? Account.SelectedItem != null : Step == 1 ? !loading && loaded && pinned && rows.Any(row => row.Selected) : Step != 4 || reviewReady);
            BackButton.IsEnabled = Step > 0 && snapshot == null && !submitting; BackButton.Visibility = Step == 0 ? Visibility.Hidden : Visibility.Visible; cancel.IsEnabled = !submitting;
            Account.IsEnabled = original == null && snapshot == null && !submitting; Repositories.IsReadOnly = snapshot != null; Repositories.IsEnabled = !loading && loaded && snapshot == null; Network.IsEnabled = snapshot == null;
            EnvironmentName.IsReadOnly = original != null || snapshot != null; Directory.IsReadOnly = Install.IsReadOnly = Start.IsReadOnly = snapshot != null;
            RetryButton.IsEnabled = !loading && snapshot == null; RetryButton.Visibility = loading || String.IsNullOrEmpty(Status.Text) ? Visibility.Collapsed : Visibility.Visible; RetryButton.Content = loaded ? "Actualiser" : "Réessayer";
            progress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed; ErrorText.Visibility = String.IsNullOrEmpty(ErrorText.Text) ? Visibility.Collapsed : Visibility.Visible; stopped.Visibility = String.IsNullOrEmpty(stopped.Text) ? Visibility.Collapsed : Visibility.Visible;
            stopped.ToolTip = stopped.Text;
            Status.ToolTip = Status.Text;
            count.Text = Repositories.Items.Count + " affiché(s) · " + rows.Count(row => row.Selected) + " sélectionné(s)";
        }
        private void Filter() { Repositories.ItemsSource = rows.Where(row => String.IsNullOrEmpty(Search.Text) || row.Name.IndexOf(Search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0 || row.Id.IndexOf(Search.Text, StringComparison.OrdinalIgnoreCase) >= 0).ToArray(); count.Text = Repositories.Items.Count + " affiché(s) · " + rows.Count(row => row.Selected) + " sélectionné(s)"; }
        private static string Key(CloudEnvironmentAccountChoice choice) => choice.Id + "/" + choice.AccountKey;
        private bool Current(long generation, string key) => !closed && generation == loadGeneration && Account.SelectedItem is CloudEnvironmentAccountChoice choice && Key(choice) == key;
        private async Task LoadRepositories(CloudEnvironmentAccountChoice choice)
        {
            if (closed || snapshot != null) return;
            loadCancellation?.Cancel(); var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request.CancelAfter(TimeSpan.FromSeconds(60)); loadCancellation = request;
            long generation = ++loadGeneration; string key = Key(choice);
            var local = loadedAccount == key ? rows.ToDictionary(row => row.Id, row => new CloudEnvironmentRepositoryRow { Id = row.Id, Name = row.Name, Ref = row.Ref, OriginalRef = row.OriginalRef, Depth = row.Depth, Selected = row.Selected }, StringComparer.Ordinal) : new Dictionary<string, CloudEnvironmentRepositoryRow>(StringComparer.Ordinal);
            loaded = false; loading = true; loadedAccount = local.Count > 0 ? key : null; if (local.Count == 0) rows.Clear(); Filter(); Status.Text = "Chargement des dépôts de " + choice.Label + "…"; UpdateNavigation();
            try
            {
                var catalog = await load(choice, request.Token); if (!Current(generation, key)) return;
                rows.Clear();
                var existing = (original?.Repositories ?? new List<CloudRepository>()).ToDictionary(repo => repo.RepositoryId, StringComparer.Ordinal);
                foreach (var repository in (catalog ?? new CloudGitHubRepository[0]).GroupBy(repo => repo.Id, StringComparer.Ordinal).Select(group => group.First()))
                {
                    existing.TryGetValue(repository.Id, out var previous);
                    local.TryGetValue(repository.Id, out var edited);
                    rows.Add(new CloudEnvironmentRepositoryRow { Id = repository.Id, Name = repository.FullName ?? "Dépôt GitHub", Ref = edited?.Ref ?? previous?.Ref ?? repository.DefaultBranch ?? "", OriginalRef = previous?.Ref ?? repository.DefaultBranch ?? "", Depth = edited?.Depth ?? previous?.Depth, Selected = edited?.Selected ?? previous != null }); existing.Remove(repository.Id); local.Remove(repository.Id);
                }
                foreach (var previous in existing.Values)
                {
                    local.TryGetValue(previous.RepositoryId, out var edited);
                    rows.Add(new CloudEnvironmentRepositoryRow { Id = previous.RepositoryId, Name = "Dépôt existant absent du catalogue", Ref = edited?.Ref ?? previous.Ref, OriginalRef = previous.Ref, Depth = previous.Depth, Selected = edited?.Selected ?? true, Missing = true }); local.Remove(previous.RepositoryId);
                }
                foreach (var edited in local.Values.Where(row => row.Selected)) rows.Add(new CloudEnvironmentRepositoryRow { Id = edited.Id, Name = "Dépôt sélectionné absent du catalogue · " + edited.Name, Ref = edited.Ref, OriginalRef = edited.OriginalRef, Depth = edited.Depth, Selected = true, Missing = true });
                rows.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)); foreach (var row in rows) row.Changed = UpdateNavigation;
                loaded = true; loadedAccount = key; Filter(); int missing = rows.Count(row => row.Missing);
                Status.Text = rows.Count == 0 ? "Aucun dépôt accessible. Vérifiez la connexion GitHub de ce compte, puis réessayez." : missing > 0 ? missing + " dépôt(s) existant(s) non retrouvé(s) : conservés avec leur référence exacte. Décochez uniquement pour les retirer explicitement." : rows.Count + " dépôt(s) accessible(s). Les références peuvent être modifiées dans la dernière colonne.";
            }
            catch (OperationCanceledException) { if (Current(generation, key)) Status.Text = "Le chargement a pris trop de temps. Réessayez ou choisissez un autre compte."; }
            catch (Exception exception) { if (Current(generation, key)) Status.Text = "Impossible de charger les dépôts : " + Program.SafeError(exception); }
            finally { if (Current(generation, key)) { loading = false; UpdateNavigation(); } if (ReferenceEquals(loadCancellation, request)) loadCancellation = null; request.Dispose(); }
        }
        private List<CloudRepository> SelectedRepositories()
        {
            Repositories.CommitEdit(DataGridEditingUnit.Cell, true); Repositories.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = rows.Where(row => row.Selected).ToArray(); if (selected.Length == 0) throw new InvalidOperationException("Choisissez au moins un dépôt GitHub."); if (selected.Length > 100) throw new InvalidOperationException("Limitez l’environnement à 100 dépôts.");
            foreach (var row in selected)
            {
                if (String.IsNullOrWhiteSpace(row.Ref) || row.Ref.Length > 1024 || row.Ref.Any(Char.IsControl)) throw new InvalidOperationException("Indiquez une référence Git valide pour " + row.Name + ".");
                if (String.IsNullOrWhiteSpace(row.Id) || row.Id.Length > 512 || row.Id.Any(Char.IsControl) || row.Depth.HasValue && row.Depth.Value < 1) throw new InvalidOperationException("Le dépôt ou sa profondeur Git est invalide.");
                if (original == null || row.Ref != row.OriginalRef)
                    if (row.Ref.Any(Char.IsWhiteSpace) || row.Ref.IndexOfAny(new[] { '~', '^', ':', '?', '*', '[', '\\' }) >= 0 || row.Ref.Contains("..") || row.Ref.Contains("@{") || row.Ref == "@" || row.Ref.StartsWith("-", StringComparison.Ordinal) || row.Ref.Split('/').Any(part => String.IsNullOrEmpty(part) || part.StartsWith(".", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(".lock", StringComparison.Ordinal))) throw new InvalidOperationException("La branche ou référence de " + row.Name + " n’est pas valide.");
            }
            return selected.Select(row => new CloudRepository { RepositoryId = row.Id, Ref = row.Ref, Depth = row.Depth }).ToList();
        }
        private CloudNetworkPolicy Policy()
        {
            int index = Network.SelectedIndex - (original == null ? 0 : 1); if (original != null && Network.SelectedIndex == 0) return null;
            if (index < 0 || index >= CloudEnvironmentPreferences.Policies.Length) throw new InvalidOperationException("Choisissez une règle d’accès Internet pour cet environnement.");
            return index == 1 ? new CloudNetworkPolicy { Type = "restricted", Presets = new List<string> { "package_managers" }, EgressHosts = new List<string>() } : new CloudNetworkPolicy { Type = index == 2 ? "unrestricted" : "disabled" };
        }
        private CloudEnvironmentWizardIntent BuildIntent(bool requireChange = true)
        {
            if (!(Account.SelectedItem is CloudEnvironmentAccountChoice account) || !loaded || loadedAccount != Key(account)) throw new InvalidOperationException("Rechargez les dépôts du compte propriétaire avant de continuer.");
            var repositories = SelectedRepositories(); string name = EnvironmentName.Text.Trim(), cwd = Directory.Text;
            if (original == null && (String.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(Char.IsControl))) throw new InvalidOperationException("Donnez un nom valide à l’environnement.");
            bool directoryChanged = cwd != (original?.Cwd ?? "");
            if ((original == null && cwd.Length > 0 || original != null && directoryChanged) && (!cwd.StartsWith("/", StringComparison.Ordinal) || cwd.Any(Char.IsControl) || cwd.Length > 4096)) throw new InvalidOperationException("Le dossier Cloud doit être un chemin absolu commençant par / (par exemple /workspace).");
            if (Install.Text.Length > 262144 || Start.Text.Length > 262144 || Install.Text.IndexOf('\0') >= 0 || Start.Text.IndexOf('\0') >= 0) throw new InvalidOperationException("Les instructions sont trop longues ou contiennent un caractère nul invalide.");
            var result = new CloudEnvironmentWizardIntent { Id = IntentId, Account = new CloudEnvironmentAccountChoice { Id = account.Id, AccountKey = account.AccountKey, Label = account.Label }, ConfigurationId = original?.Id };
            if (original == null) result.Create = new CloudConfigurationCreate { Name = name, Repositories = repositories, NetworkPolicy = Policy(), ShareSettings = "private", StartOnboarding = false, Cwd = cwd.Length == 0 ? null : cwd, InstallScript = Install.Text.Length == 0 ? null : Install.Text, StartSkill = Start.Text.Length == 0 ? null : Start.Text };
            else
            {
                var patch = new CloudDraftPatch { BaseVersionId = original.VersionId, ExpectedRevision = 0 };
                string Before(IEnumerable<CloudRepository> values) => Json.Write(values.OrderBy(repo => repo.RepositoryId, StringComparer.Ordinal).Select(repo => new { repo.RepositoryId, repo.Ref, repo.Depth }));
                if (Before(repositories) != Before(original.Repositories ?? new List<CloudRepository>())) patch.Repositories = repositories;
                if (Install.Text != (original.InstallScript ?? "")) patch.InstallScript = Install.Text;
                if (Start.Text != (original.StartSkill ?? "")) patch.StartSkill = Start.Text;
                if (directoryChanged) patch.Cwd = cwd; patch.NetworkPolicy = Policy();
                if (requireChange && patch.Repositories == null && patch.InstallScript == null && patch.StartSkill == null && patch.Cwd == null && patch.NetworkPolicy == null) throw new InvalidOperationException("Modifiez au moins un réglage avant d’enregistrer."); result.Patch = patch;
            }
            return result;
        }
        private string Describe(CloudEnvironmentWizardIntent intent)
        {
            var repositories = intent.Create?.Repositories ?? intent.Patch.Repositories ?? original.Repositories;
            var policy = intent.Create?.NetworkPolicy ?? intent.Patch?.NetworkPolicy ?? original?.NetworkPolicy;
            string network = policy == null ? "Règle non fournie" : policy.Type == "disabled" ? "Internet désactivé" : policy.Type == "unrestricted" ? "Internet : toutes les adresses" : "Restreint";
            if (policy?.Presets?.Count > 0) network += " · " + String.Join(", ", policy.Presets.Select(value => value == "package_managers" ? "Gestionnaires de paquets" : value));
            if (policy?.EgressHosts?.Count > 0) network += "\nDestinations : " + String.Join(", ", policy.EgressHosts);
            var lines = new List<string> { "COMPTE PROPRIÉTAIRE", intent.Account.Label, "", original == null ? "CRÉATION PRIVÉE · PRÉPARATION NON DÉCLENCHÉE" : "MODIFICATION · " + original.Name, "Nom : " + (intent.Create?.Name ?? original.Name), "", "DÉPÔTS ET RÉFÉRENCES" };
            lines.AddRange(repositories.Select(repo => (rows.FirstOrDefault(row => row.Id == repo.RepositoryId)?.Name ?? "Dépôt GitHub") + "\n  Référence : " + repo.Ref + " · Profondeur : " + (repo.Depth?.ToString() ?? "par défaut") + "\n  Identifiant : " + repo.RepositoryId));
            lines.AddRange(new[] { "", "RÉSEAU" + (original != null && intent.Patch.NetworkPolicy == null ? " · règle actuelle conservée" : ""), network, "", "DOSSIER CLOUD", Directory.Text.Length == 0 ? "Dossier défini par Codex" : Directory.Text, "", "COMMANDES D’INSTALLATION", Install.Text.Length == 0 ? "Aucune" : Install.Text, "", "INSTRUCTIONS DE DÉMARRAGE", Start.Text.Length == 0 ? "Aucune" : Start.Text });
            if (original != null) lines.AddRange(new[] { "", "CHANGEMENTS", String.Join(", ", new[] { intent.Patch.Repositories != null ? "dépôts/références" : null, intent.Patch.NetworkPolicy != null ? "réseau" : null, intent.Patch.Cwd != null ? "dossier" : null, intent.Patch.InstallScript != null ? "installation" : null, intent.Patch.StartSkill != null ? "démarrage" : null }.Where(value => value != null)) });
            return String.Join("\n", lines);
        }
        private void CancelReview() { ++reviewGeneration; reviewReady = false; reviewCancellation?.Cancel(); reviewCancellation = null; }
        private async Task ArmReview()
        {
            var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); reviewCancellation = request; long generation = reviewGeneration;
            try { await Task.Delay(Math.Max(350, System.Windows.Forms.SystemInformation.DoubleClickTime + 50), request.Token); if (!closed && generation == reviewGeneration && Step == 4) { reviewReady = true; UpdateNavigation(); } }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(reviewCancellation, request)) reviewCancellation = null; request.Dispose(); }
        }
        private async Task Next()
        {
            if (closed || submitting || !NextButton.IsEnabled || snapshot != null) return; ErrorText.Text = "";
            try
            {
                if (Step == 0) { ShowStep(1); return; }
                if (Step == 1) { SelectedRepositories(); ShowStep(2); return; }
                if (Step == 2) { BuildIntent(false); ShowStep(3); return; }
                if (Step == 3) { BuildIntent(); ShowStep(4); return; }
                snapshot = BuildIntent(); submitting = true; stopped.Text = "Enregistrement de la demande…"; UpdateNavigation();
                await submit(snapshot, lifetime.Token); Saved = true; Close();
            }
            catch (Exception exception)
            {
                if (!closed)
                {
                    ErrorText.Text = Program.SafeError(exception); ErrorText.ToolTip = ErrorText.Text;
                    if (snapshot != null)
                    {
                        bool knownRefusal = exception is CloudBackendException && !(exception is CloudBackendUncertainException);
                        stopped.Text = (knownRefusal ? "Refus connu. " : "Résultat à vérifier. ") + "Aucun renvoi depuis cet assistant. Référence : " + IntentId;
                    }
                }
            }
            finally { submitting = false; if (!closed) UpdateNavigation(); }
        }
    }
}
