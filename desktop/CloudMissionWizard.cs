using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudMissionAccountChoice
    {
        public string Label { get; set; }
        public string Id { get; set; }
        public string AccountKey { get; set; }
    }

    // The controls stay alive while steps change: going back never discards a mandate.
    internal sealed class CloudMissionWizard : Window
    {
        internal readonly ComboBox Account = new ComboBox { DisplayMemberPath = "Label" };
        internal readonly ComboBox Environment = new ComboBox { DisplayMemberPath = "Name" };
        internal readonly TextBox MissionTitle = new TextBox { MaxLength = 200 };
        internal readonly TextBox Mandate = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, MaxLength = 24000, MinHeight = 100 };
        internal readonly Button NextButton, BackButton, RetryButton, CancelButton;
        internal readonly TextBlock LoadStatus = Ui.Text("", 13, true), ErrorText = Ui.Text("", 13);
        internal Func<bool> ConfirmDiscard;
        internal bool Saved { get; private set; }
        internal string MissionId { get; } = Guid.NewGuid().ToString("N");
        internal int Step { get; private set; }
        internal bool Loading => loading;
        internal bool Submitting => submitting;
        internal bool IntentLocked => snapshot != null;
        internal string LoadedAccountKey => loadedAccount;
        private readonly Func<CloudMissionAccountChoice, CancellationToken, Task<CloudConfiguration[]>> load;
        private readonly Func<CloudJobSpec, CancellationToken, Task> submit;
        private readonly CancellationTokenSource lifetime;
        private CancellationTokenSource loadCancellation;
        private CancellationTokenSource reviewCancellation;
        private readonly Grid[] panels = new Grid[4];
        private readonly Border[] markers = new Border[4];
        private readonly TextBlock[] markerText = new TextBlock[4];
        private readonly StackPanel accountPanel = new StackPanel();
        private readonly TextBlock subtitle = Ui.Text("", 14, true), summaryAccount = Ui.Text("", 14), summaryEnvironment = Ui.Text("", 14), summaryTitle = Ui.Text("", 16);
        private readonly TextBox summaryMandate = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, MinHeight = 48 };
        private readonly ProgressBar progress = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 8, 0, 14) };
        private readonly TextBlock footerNote = Ui.Text("", 12, true);
        private long loadGeneration;
        private long reviewGeneration;
        private bool closed, loading, submitting, loaded, reviewReady;
        private string loadedAccount;
        private string requestedEnvironment, requestedAccount;
        private CloudJobSpec snapshot;

        internal CloudMissionWizard(Window owner, CloudMissionAccountChoice[] accounts,
            Func<CloudMissionAccountChoice, CancellationToken, Task<CloudConfiguration[]>> load,
            Func<CloudJobSpec, CancellationToken, Task> submit, CancellationToken token, string instanceId = null, string configurationId = null)
        {
            this.load = load; this.submit = submit;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            Owner = owner; Title = "Nouvelle mission Codex Cloud"; ShowInTaskbar = false;
            Width = Math.Min(760, SystemParameters.WorkArea.Width); Height = Math.Min(680, SystemParameters.WorkArea.Height);
            MinWidth = 560; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
            root.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush");
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            var heading = new StackPanel(); root.Children.Add(heading);
            heading.Children.Add(Ui.Text("Nouvelle mission Cloud", 25)); heading.Children.Add(subtitle);
            var steps = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 20) };
            string[] names = { "Compte", "Environnement", "Mission", "Vérifier" };
            for (int i = 0; i < 4; i++)
            {
                markerText[i] = Ui.Text((i + 1) + "  " + names[i], 12); markerText[i].Margin = new Thickness(0); markerText[i].VerticalAlignment = VerticalAlignment.Center;
                markers[i] = new Border { Child = markerText[i], Padding = new Thickness(10, 10, 6, 10), Margin = new Thickness(0, 0, i == 3 ? 0 : 7, 0), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
                steps.Children.Add(markers[i]);
            }
            heading.Children.Add(steps);
            Grid.SetRow(accountPanel, 1); root.Children.Add(accountPanel);
            accountPanel.Children.Add(Ui.Text("Quel compte réalise cette mission ?", 16)); accountPanel.Children.Add(SelectionField(Account, "Choisir un compte"));
            Account.ItemsSource = accounts; AutomationProperties.SetName(Account, "Compte exécutant la mission Cloud");
            var body = new Grid { Margin = new Thickness(0, 12, 0, 8) }; Grid.SetRow(body, 2); root.Children.Add(body);
            for (int i = 0; i < panels.Length; i++) { panels[i] = new Grid(); body.Children.Add(panels[i]); }
            var intro = new StackPanel(); panels[0].Children.Add(intro);
            intro.Children.Add(Ui.Text("Choisissez une instance et son compte associé.", 17));
            intro.Children.Add(Ui.Text("Ses environnements Cloud apparaîtront automatiquement. Chaque compte garde ses propres accès GitHub et ses quotas.", 14, true));
            var environmentPanel = new StackPanel(); panels[1].Children.Add(environmentPanel);
            environmentPanel.Children.Add(Ui.Text("Dans quel environnement ?", 18));
            environmentPanel.Children.Add(Ui.Text("Choisissez un environnement publié et prêt pour le travail.", 13, true));
            environmentPanel.Children.Add(SelectionField(Environment, "Choisir un environnement")); environmentPanel.Children.Add(progress); environmentPanel.Children.Add(LoadStatus);
            AutomationProperties.SetName(Environment, "Environnement publié de la mission Cloud");
            AutomationProperties.SetName(LoadStatus, "État du chargement des environnements Cloud"); AutomationProperties.SetLiveSetting(LoadStatus, AutomationLiveSetting.Polite);
            RetryButton = Ui.Button("Réessayer", () => { if (!loading && Account.SelectedItem is CloudMissionAccountChoice choice) _ = Load(choice); });
            environmentPanel.Children.Add(RetryButton);
            var environmentHelp = Ui.Text("Pour préparer un nouvel environnement : Instances → Cloud. Revenez ensuite ici et actualisez la liste.", 12, true); environmentHelp.Margin = new Thickness(0, 16, 0, 0); environmentPanel.Children.Add(environmentHelp);
            var mission = panels[2];
            mission.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); mission.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mission.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); mission.RowDefinitions.Add(new RowDefinition()); mission.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mission.Children.Add(Ui.Text("Comment s’appelle cette mission ?", 15)); Grid.SetRow(MissionTitle, 1); mission.Children.Add(MissionTitle);
            var mandateLabel = Ui.Text("Que doit faire l’agent ?", 15); mandateLabel.Margin = new Thickness(0, 16, 0, 8); Grid.SetRow(mandateLabel, 2); mission.Children.Add(mandateLabel);
            Grid.SetRow(Mandate, 3); mission.Children.Add(Mandate);
            var mandateHelp = Ui.Text("Précisez l’objectif, les fichiers ou dépôts autorisés, les vérifications et le résultat attendu.", 12, true); mandateHelp.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(mandateHelp, 4); mission.Children.Add(mandateHelp);
            AutomationProperties.SetName(MissionTitle, "Titre de la mission"); AutomationProperties.SetName(Mandate, "Mandat, périmètre autorisé et résultat attendu");
            var review = panels[3]; review.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); review.RowDefinitions.Add(new RowDefinition()); review.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var reviewTop = new StackPanel(); review.Children.Add(reviewTop);
            reviewTop.Children.Add(Ui.Text("Tout est prêt pour l’envoi", 19));
            var summaryTargets = new Grid { Margin = new Thickness(0, 0, 0, 10) }; summaryTargets.ColumnDefinitions.Add(new ColumnDefinition()); summaryTargets.ColumnDefinitions.Add(new ColumnDefinition());
            var summaryPerson = new StackPanel(); summaryPerson.Children.Add(Ui.Text("Compte exécutant", 12, true)); summaryPerson.Children.Add(summaryAccount);
            var summaryPlace = new StackPanel(); summaryPlace.Children.Add(Ui.Text("Environnement", 12, true)); summaryPlace.Children.Add(summaryEnvironment);
            var accountCard = new Border { Child = summaryPerson, Padding = new Thickness(10, 8, 10, 0), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 7, 0) }; accountCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            var environmentCard = new Border { Child = summaryPlace, Padding = new Thickness(10, 8, 10, 0), CornerRadius = new CornerRadius(7) }; environmentCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            summaryTargets.Children.Add(accountCard); Grid.SetColumn(environmentCard, 1); summaryTargets.Children.Add(environmentCard); reviewTop.Children.Add(summaryTargets); reviewTop.Children.Add(summaryTitle);
            summaryAccount.MaxHeight = summaryEnvironment.MaxHeight = 44; summaryAccount.TextTrimming = summaryEnvironment.TextTrimming = TextTrimming.CharacterEllipsis;
            summaryTitle.MaxHeight = 52; summaryTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(summaryMandate, 1); review.Children.Add(summaryMandate); AutomationProperties.SetName(summaryMandate, "Mandat à envoyer");
            var policy = Ui.Text("Une branche distincte sera utilisée. Aucune fusion automatique. Les fichiers locaux ne sont pas synchronisés ; le résultat sera disponible dans Missions Cloud.", 12, true); policy.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(policy, 2); review.Children.Add(policy);
            var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(footer, 3); root.Children.Add(footer);
            ErrorText.Foreground = Brushes.Firebrick; ErrorText.MaxHeight = 62; footer.Children.Add(ErrorText); footer.Children.Add(footerNote);
            AutomationProperties.SetName(ErrorText, "Erreur de configuration de la mission Cloud"); AutomationProperties.SetLiveSetting(ErrorText, AutomationLiveSetting.Polite);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(actions);
            BackButton = Ui.Button("Retour", () => { if (!submitting && snapshot == null && Step > 0) ShowStep(Step - 1); }); actions.Children.Add(BackButton);
            CancelButton = Ui.Button("Annuler", Close); Grid.SetColumn(CancelButton, 2); actions.Children.Add(CancelButton);
            NextButton = Ui.Button("Continuer", async () => await Next(), true); NextButton.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(NextButton, 3); actions.Children.Add(NextButton);
            ConfirmDiscard = () => Ui.Confirm(this, "Abandonner la configuration de cette mission ?", "Mission en cours de préparation");
            Account.SelectionChanged += delegate
            {
                if (snapshot != null || closed) return;
                loadCancellation?.Cancel(); ++loadGeneration; loaded = false; loadedAccount = null; Environment.ItemsSource = null; ErrorText.Text = "";
                if (Account.SelectedItem is CloudMissionAccountChoice choice) { ShowStep(1); _ = Load(choice); }
                else { loading = false; ShowStep(0); }
            };
            Environment.SelectionChanged += delegate { UpdateNavigation(); };
            MissionTitle.TextChanged += delegate { ErrorText.Text = ""; }; Mandate.TextChanged += delegate { ErrorText.Text = ""; };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (submitting && !Saved) { args.Cancel = true; return; }
                if (!Saved && (!String.IsNullOrEmpty(MissionTitle.Text) || !String.IsNullOrEmpty(Mandate.Text)) && !ConfirmDiscard()) args.Cancel = true;
            };
            Closed += delegate { closed = true; ++loadGeneration; loadCancellation?.Cancel(); CancelReviewArm(); lifetime.Cancel(); lifetime.Dispose(); };
            ShowStep(0);
            if (!String.IsNullOrEmpty(instanceId))
            {
                var requested = accounts.SingleOrDefault(choice => choice.Id == instanceId);
                if (requested != null)
                {
                    requestedAccount = requested.Id + "/" + requested.AccountKey;
                    requestedEnvironment = configurationId;
                    Account.SelectedItem = requested;
                }
            }
        }

        private static Grid SelectionField(ComboBox choice, string hint)
        {
            var field = new Grid(); field.Children.Add(choice);
            var placeholder = Ui.Text(hint, 14, true); placeholder.IsHitTestVisible = false;
            placeholder.Margin = new Thickness(10, 0, 32, 10); placeholder.VerticalAlignment = VerticalAlignment.Center; field.Children.Add(placeholder);
            choice.SelectionChanged += delegate
            {
                placeholder.Visibility = choice.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
                choice.ToolTip = choice.SelectedItem is CloudConfiguration environment ? environment.Name : (choice.SelectedItem as CloudMissionAccountChoice)?.Label;
            };
            return field;
        }

        private void ShowStep(int step)
        {
            CancelReviewArm();
            Step = step; ErrorText.Text = "";
            for (int i = 0; i < panels.Length; i++)
            {
                panels[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
                markers[i].SetResourceReference(Border.BackgroundProperty, i == step ? "SurfaceBrush" : "CanvasBrush");
                markers[i].SetResourceReference(Border.BorderBrushProperty, i <= step ? "AccentBrush" : "LineBrush");
                markerText[i].FontWeight = i == step ? FontWeights.SemiBold : FontWeights.Normal;
                markerText[i].SetResourceReference(TextBlock.ForegroundProperty, i <= step ? "TextBrush" : "MutedBrush");
            }
            accountPanel.Visibility = step <= 1 ? Visibility.Visible : Visibility.Collapsed;
            subtitle.Text = new[] { "Un compte, un environnement, une mission.", "Les environnements du compte sélectionné se chargent automatiquement.", "Décrivez le travail et le résultat attendu.", "Vérifiez le compte et le mandat avant de déléguer." }[step];
            if (step == 3)
            {
                summaryAccount.Text = (Account.SelectedItem as CloudMissionAccountChoice)?.Label;
                summaryEnvironment.Text = (Environment.SelectedItem as CloudConfiguration)?.Name;
                summaryTitle.Text = MissionTitle.Text; summaryMandate.Text = Mandate.Text;
                summaryAccount.ToolTip = summaryAccount.Text; summaryEnvironment.ToolTip = summaryEnvironment.Text; summaryTitle.ToolTip = summaryTitle.Text;
            }
            UpdateNavigation();
            if (step == 3 && snapshot == null) _ = ArmReview();
        }

        private void CancelReviewArm()
        {
            ++reviewGeneration; reviewReady = false;
            reviewCancellation?.Cancel(); reviewCancellation = null;
        }

        private async Task ArmReview()
        {
            var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            reviewCancellation = request; long generation = reviewGeneration;
            try
            {
                // A double click on Continue must not become a click on Send at the same position.
                int delay = Math.Max(350, System.Windows.Forms.SystemInformation.DoubleClickTime + 50);
                await Task.Delay(delay, request.Token);
                if (!closed && generation == reviewGeneration && Step == 3 && snapshot == null)
                {
                    reviewReady = true; UpdateNavigation();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(reviewCancellation, request)) reviewCancellation = null;
                request.Dispose();
            }
        }

        private void UpdateNavigation()
        {
            if (closed) return;
            bool current = Account.SelectedItem is CloudMissionAccountChoice choice && loadedAccount == choice.Id + "/" + choice.AccountKey;
            BackButton.IsEnabled = Step > 0 && !submitting && snapshot == null;
            BackButton.Visibility = Step == 0 ? Visibility.Hidden : Visibility.Visible;
            NextButton.Content = Step == 3 ? snapshot == null ? "Envoyer la mission" : "Réessayer l’envoi" : "Continuer";
            AutomationProperties.SetName(NextButton, (string)NextButton.Content);
            NextButton.IsEnabled = !submitting && (Step != 3 || reviewReady || snapshot != null) && (snapshot != null || (Step == 0 ? Account.SelectedItem != null : Step != 1 || loaded && current && Environment.SelectedItem != null));
            CancelButton.IsEnabled = !submitting; Account.IsEnabled = snapshot == null && !submitting; Environment.IsEnabled = !loading && loaded && snapshot == null && !submitting;
            MissionTitle.IsReadOnly = Mandate.IsReadOnly = snapshot != null;
            progress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = loading || !loaded && String.IsNullOrEmpty(LoadStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
            RetryButton.IsEnabled = !loading && !submitting && snapshot == null;
            RetryButton.Content = loaded && Environment.Items.Count > 0 ? "Actualiser la liste" : "Réessayer";
            footerNote.Text = submitting ? "Enregistrement de la mission…" : snapshot != null ? "Cette intention est conservée. Une reprise utilise le même identifiant de mission." : "";
            footerNote.Visibility = String.IsNullOrEmpty(footerNote.Text) ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Visibility = String.IsNullOrEmpty(ErrorText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task Load(CloudMissionAccountChoice choice)
        {
            if (closed || snapshot != null) return;
            loadCancellation?.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request.CancelAfter(TimeSpan.FromSeconds(60));
            loadCancellation = request; long generation = ++loadGeneration; string key = choice.Id + "/" + choice.AccountKey;
            loading = true; loaded = false; loadedAccount = null; Environment.ItemsSource = null; ErrorText.Text = "";
            LoadStatus.Text = "Chargement des environnements de " + choice.Label + "…"; UpdateNavigation();
            try
            {
                var found = await load(choice, request.Token);
                if (!CurrentLoad(generation, key)) return;
                Environment.ItemsSource = found ?? new CloudConfiguration[0]; loadedAccount = key; loaded = true;
                if (requestedAccount == key && !String.IsNullOrEmpty(requestedEnvironment))
                    Environment.SelectedItem = Environment.Items.Cast<CloudConfiguration>().SingleOrDefault(environment => environment.Id == requestedEnvironment);
                requestedAccount = requestedEnvironment = null;
                LoadStatus.Text = Environment.Items.Count == 0 ? "Aucun environnement publié et prêt sur ce compte. Préparez-en un dans Instances → Cloud." : Environment.Items.Count + " environnement(s) disponible(s). Choisissez celui de cette mission.";
            }
            catch (OperationCanceledException)
            {
                if (CurrentLoad(generation, key)) LoadStatus.Text = "Le chargement a pris trop de temps. Réessayez ou choisissez un autre compte.";
            }
            catch (Exception exception)
            {
                if (CurrentLoad(generation, key)) LoadStatus.Text = "Impossible de charger les environnements de ce compte : " + Program.SafeError(exception);
            }
            finally
            {
                if (CurrentLoad(generation, key)) { loading = false; UpdateNavigation(); }
                if (ReferenceEquals(loadCancellation, request)) loadCancellation = null;
                request.Dispose();
            }
        }

        private bool CurrentLoad(long generation, string key)
        {
            var selected = Account.SelectedItem as CloudMissionAccountChoice;
            return !closed && generation == loadGeneration && selected != null && key == selected.Id + "/" + selected.AccountKey;
        }

        private async Task Next()
        {
            if (closed || submitting || !NextButton.IsEnabled) return;
            ErrorText.Text = "";
            try
            {
                if (Step == 0) { ShowStep(1); return; }
                if (Step == 1) { ShowStep(2); MissionTitle.Focus(); return; }
                if (Step == 2)
                {
                    if (String.IsNullOrWhiteSpace(MissionTitle.Text)) { MissionTitle.Focus(); throw new InvalidOperationException("Donnez un titre à cette mission."); }
                    if (String.IsNullOrWhiteSpace(Mandate.Text)) { Mandate.Focus(); throw new InvalidOperationException("Décrivez le travail à confier à l’agent."); }
                    ShowStep(3); return;
                }
                if (snapshot == null)
                {
                    var selected = Account.SelectedItem as CloudMissionAccountChoice; var environment = Environment.SelectedItem as CloudConfiguration;
                    if (selected == null || environment == null || !loaded || loadedAccount != selected.Id + "/" + selected.AccountKey) throw new InvalidOperationException("Choisissez un environnement du compte exécutant avant l’envoi.");
                    snapshot = new CloudJobSpec { Id = MissionId, Instance = selected.Id, ExpectedAccountKey = selected.AccountKey, ConfigurationId = environment.Id, Title = MissionTitle.Text, Prompt = Mandate.Text, ExplicitDelegation = true, ReturnToSource = false };
                }
                submitting = true; UpdateNavigation();
                await submit(snapshot, lifetime.Token);
                Saved = true; Close();
            }
            catch (Exception exception) { if (!closed) ErrorText.Text = Program.SafeError(exception); }
            finally { submitting = false; if (!closed) UpdateNavigation(); }
        }
    }
}
