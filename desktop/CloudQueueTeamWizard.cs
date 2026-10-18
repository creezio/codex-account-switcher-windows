using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudQueueTeamWizard : CloudQueueWizard
    {
        internal readonly TextBox TeamName = new TextBox { MaxLength = 100 }, Repository = new TextBox { MaxLength = 200 };
        internal readonly TextBox Mandate = new TextBox { MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 65, MaxHeight = 90 };
        internal readonly ComboBox Source = new ComboBox { DisplayMemberPath = "Label" }, Account = new ComboBox { DisplayMemberPath = "Label" }, Environment = new ComboBox { DisplayMemberPath = "Name" };
        internal readonly TextBox Parallel = new TextBox { Text = "2", MaxLength = 1 }, IdleAfter = new TextBox { Text = "5", MaxLength = 4 };
        internal readonly CheckBox AlertsEnabled = new CheckBox { Content = "Signaler quand l’équipe reste sans mission attribuée", IsChecked = true }, Authorization = new CheckBox { Content = "J’autorise le Switcher à attribuer les mandats préparés à cette équipe." };
        internal readonly TextBlock LoadStatus = Ui.Text("Choisissez un compte : ses environnements se chargent automatiquement.", 12, true);
        internal readonly ListBox Pool = new ListBox { DisplayMemberPath = "Label", BorderThickness = new Thickness(0) };
        internal readonly Button AddButton, RemoveButton, RetryButton;
        internal bool Loading => loading;
        internal string LoadedAccountKey => loadedKey;
        private readonly Func<CloudMissionAccountChoice, CancellationToken, Task<CloudConfiguration[]>> load;
        private readonly Func<CloudQueueTeam, RelaySession, Task> configure;
        private readonly CloudQueueTeam original;
        private readonly List<TargetChoice> targets = new List<TargetChoice>();
        private readonly TextBlock review = Ui.Text("", 13, true);
        private readonly string id;
        private long loadGeneration;
        private CancellationTokenSource request;
        private bool loading, loaded;
        private string loadedKey;
        private CloudQueueTeam intent;
        private RelaySession intentSource;
        private sealed class TargetChoice { public string Label { get; set; } internal CloudQueueTarget Target; }
        internal CloudQueueTeamWizard(Window owner, CloudMissionAccountChoice[] accounts, CloudQueueSourceChoice[] sources, Func<CloudMissionAccountChoice, CancellationToken, Task<CloudConfiguration[]>> load, Func<CloudQueueTeam, RelaySession, Task> configure, CancellationToken token, CloudQueueTeam existing = null)
            : base(owner, existing == null ? "Créer une équipe Cloud" : "Configurer l’équipe Cloud", new[] { "Équipe", "Comptes", "Autoriser" }, "Enregistrer l’équipe", token)
        {
            this.load = load; this.configure = configure; original = existing == null ? null : Json.Read<CloudQueueTeam>(Json.Write(existing)); id = original?.Id ?? Guid.NewGuid().ToString("N");
            var identity = new StackPanel(); Panels[0].Children.Add(identity); Labeled(identity, "Nom de l’équipe", TeamName); Labeled(identity, "Dépôt GitHub (organisation/dépôt)", Repository); Labeled(identity, "Chat orchestrateur qui recevra les retours", Source); Source.ItemsSource = sources;
            Labeled(identity, "Mandat de cette équipe et périmètre autorisé", Mandate); identity.Children.Add(Ui.Text("Le compte et le chat choisis restent les destinataires exacts des résultats. Aucun jeton de session n’est à saisir.", 12, true));
            var workers = Panels[1]; for (int n = 0; n < 5; n++) workers.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); workers.RowDefinitions.Add(new RowDefinition());
            workers.Children.Add(Ui.Text("Associez un environnement à chaque compte de l’équipe", 16)); var accountField = new StackPanel(); Labeled(accountField, "Compte", Account); Account.ItemsSource = accounts; Grid.SetRow(accountField, 1); workers.Children.Add(accountField);
            var environmentField = new StackPanel(); Labeled(environmentField, "Environnement publié sur ce compte", Environment); Grid.SetRow(environmentField, 2); workers.Children.Add(environmentField); Grid.SetRow(LoadStatus, 3); workers.Children.Add(LoadStatus); AutomationProperties.SetName(LoadStatus, "Chargement automatique des environnements de l’équipe"); AutomationProperties.SetLiveSetting(LoadStatus, AutomationLiveSetting.Polite);
            var actions = new WrapPanel(); AddButton = Ui.Button("Ajouter ce compte à l’équipe", AddTarget); RetryButton = Ui.AsyncButton("Réessayer", async () => { if (Account.SelectedItem is CloudMissionAccountChoice choice) await Load(choice); }, exception => ErrorText.Text = Program.SafeError(exception)); RemoveButton = Ui.Button("Retirer le compte sélectionné", () => { if (Pool.SelectedItem is TargetChoice choice) { targets.Remove(choice); PaintTargets(); } }); actions.Children.Add(AddButton); actions.Children.Add(RetryButton); actions.Children.Add(RemoveButton); Grid.SetRow(actions, 4); workers.Children.Add(actions); Grid.SetRow(Pool, 5); workers.Children.Add(Pool); AutomationProperties.SetName(Pool, "Comptes et environnements autorisés dans l’équipe");
            var settings = new Grid(); settings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); settings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); settings.RowDefinitions.Add(new RowDefinition()); settings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Panels[2].Children.Add(settings);
            var limits = new Grid(); limits.ColumnDefinitions.Add(new ColumnDefinition()); limits.ColumnDefinitions.Add(new ColumnDefinition()); var cap = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; Labeled(cap, "Missions simultanées de l’équipe (1 à 8)", Parallel); var idle = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; Labeled(idle, "Signaler l’inactivité après (minutes)", IdleAfter); limits.Children.Add(cap); Grid.SetColumn(idle, 1); limits.Children.Add(idle); settings.Children.Add(limits);
            AlertsEnabled.Margin = new Thickness(0, 10, 0, 12); Grid.SetRow(AlertsEnabled, 1); settings.Children.Add(AlertsEnabled); var reviewScroll = new ScrollViewer { Content = review, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(reviewScroll, 2); settings.Children.Add(reviewScroll); Authorization.Content = new TextBlock { Text = "J’autorise le Switcher à attribuer les mandats préparés à cette équipe, dans le périmètre indiqué.", TextWrapping = TextWrapping.Wrap }; Authorization.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(Authorization, 3); settings.Children.Add(Authorization); AutomationProperties.SetName(Authorization, "Autorisation explicite de l’équipe Cloud");
            if (original != null)
            {
                TeamName.Text = original.Name; Repository.Text = original.Repository; Repository.IsReadOnly = true; Source.IsEnabled = false; Mandate.Text = original.Mandate; Parallel.Text = original.MaximumParallel.ToString(); IdleAfter.Text = original.IdleAfterMinutes.ToString(); AlertsEnabled.IsChecked = original.IdleAlertsEnabled;
                Source.SelectedItem = sources.FirstOrDefault(choice => SameSource(choice.Source, original.Source));
                foreach (var target in original.Targets ?? new List<CloudQueueTarget>()) { var account = accounts.FirstOrDefault(choice => choice.Id == target.InstanceId); targets.Add(new TargetChoice { Target = Json.Read<CloudQueueTarget>(Json.Write(target)), Label = (account?.Label ?? target.InstanceId) + "\n" + (target.ConfigurationName ?? target.ConfigurationId) }); }
            }
            Account.SelectionChanged += async delegate { if (!Frozen && Account.SelectedItem is CloudMissionAccountChoice choice) await Load(choice); }; Environment.SelectionChanged += delegate { UpdateAdd(); }; Pool.SelectionChanged += delegate { RemoveButton.IsEnabled = !Frozen && Pool.SelectedItem != null; }; base.Closed += delegate { request?.Cancel(); }; PaintTargets(); UpdateAdd();
        }
        private static bool SameSource(RelaySession left, RelaySession right) => left != null && right != null && left.Channel == right.Channel && left.Thread == right.Thread && left.Account == right.Account && RelayStore.SamePath(left.Home, right.Home);
        private void PaintTargets() { Pool.ItemsSource = null; Pool.ItemsSource = targets.ToArray(); RemoveButton.IsEnabled = !Frozen && Pool.SelectedItem != null; UpdateAdd(); }
        private void UpdateAdd()
        {
            var choice = Account.SelectedItem as CloudMissionAccountChoice; AddButton.IsEnabled = !Frozen && !loading && loaded && choice != null && loadedKey == choice.Id + "/" + choice.AccountKey && Environment.SelectedItem is CloudConfiguration;
            Environment.IsEnabled = !Frozen && loaded && !loading; RetryButton.Visibility = !loading && !loaded && choice != null ? Visibility.Visible : Visibility.Collapsed;
        }
        private async Task Load(CloudMissionAccountChoice choice)
        {
            if (IsClosed || Frozen) return; request?.Cancel(); var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); cancellation.CancelAfter(TimeSpan.FromSeconds(60)); request = cancellation; long version = ++loadGeneration; string key = choice.Id + "/" + choice.AccountKey; loading = true; loaded = false; loadedKey = null; Environment.ItemsSource = null; LoadStatus.Text = "Chargement des environnements de " + choice.Label + "…"; UpdateAdd();
            bool Current() => !IsClosed && !Frozen && version == loadGeneration && Account.SelectedItem is CloudMissionAccountChoice current && current.Id + "/" + current.AccountKey == key;
            try
            {
                var values = await load(choice, cancellation.Token); if (!Current()) return; Environment.ItemsSource = values ?? new CloudConfiguration[0]; loadedKey = key; loaded = true; LoadStatus.Text = Environment.Items.Count == 0 ? "Aucun environnement prêt sur ce compte. Préparez-en un dans Instances → Cloud." : Environment.Items.Count + " environnement(s) prêt(s). Choisissez celui à associer.";
            }
            catch (OperationCanceledException) { if (Current()) LoadStatus.Text = "Chargement interrompu ou trop long. Réessayez."; }
            catch (Exception exception) { if (Current()) LoadStatus.Text = "Impossible de charger ce compte : " + Program.SafeError(exception); }
            finally { if (Current()) { loading = false; UpdateAdd(); } if (ReferenceEquals(request, cancellation)) request = null; cancellation.Dispose(); }
        }
        private void AddTarget()
        {
            if (!AddButton.IsEnabled || !(Account.SelectedItem is CloudMissionAccountChoice account) || !(Environment.SelectedItem is CloudConfiguration environment)) return;
            targets.RemoveAll(value => value.Target.InstanceId == account.Id); targets.Add(new TargetChoice { Label = account.Label + "\n" + environment.Name, Target = new CloudQueueTarget { InstanceId = account.Id, ExpectedAccountKey = account.AccountKey, ConfigurationId = environment.Id, ConfigurationName = environment.Name } }); PaintTargets(); LoadStatus.Text = "Compte ajouté · " + account.Label + " / " + environment.Name;
        }
        protected override bool Dirty() => !String.IsNullOrEmpty(TeamName?.Text) || !String.IsNullOrEmpty(Mandate?.Text) || targets.Count > 0;
        protected override async Task Continue()
        {
            if (Frozen) { await Commit(() => configure(intent, intentSource)); return; }
            if (Step == 0) { if (String.IsNullOrWhiteSpace(TeamName.Text) || String.IsNullOrWhiteSpace(Repository.Text) || String.IsNullOrWhiteSpace(Mandate.Text) || !(Source.SelectedItem is CloudQueueSourceChoice)) throw new InvalidOperationException("Renseignez le nom, le dépôt, le chat orchestrateur et le mandat autorisé de cette équipe."); if (!Regex.IsMatch(Repository.Text.Trim(), @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") || Repository.Text.Trim().Split('/').Any(part => part == "." || part == "..")) throw new InvalidOperationException("Indiquez le dépôt sous la forme organisation/nom."); ShowStep(1); return; }
            if (Step == 1) { if (targets.Count == 0) throw new InvalidOperationException("Ajoutez au moins un compte avec son environnement avant de continuer."); review.Text = TeamName.Text + " · " + Repository.Text + "\n" + (Source.SelectedItem as CloudQueueSourceChoice)?.Label + "\n\n" + String.Join("\n\n", targets.Select(value => value.Label)) + "\n\n" + Mandate.Text + "\n\nLa limite globale reste prioritaire. Une pause arrête les prochaines attributions ; les missions déjà parties continuent. Les comptes et environnements sont revérifiés avant chaque admission."; ShowStep(2); return; }
            if (!int.TryParse(Parallel.Text, out int maximum) || maximum < 1 || maximum > 8) throw new InvalidOperationException("Choisissez une capacité d’équipe entre 1 et 8.");
            if (!int.TryParse(IdleAfter.Text, out int minutes) || minutes < 1 || minutes > 1440) throw new InvalidOperationException("Choisissez une durée d’inactivité entre 1 et 1 440 minutes.");
            if (Authorization.IsChecked != true) throw new InvalidOperationException("Autorisez explicitement le périmètre de cette équipe avant l’enregistrement.");
            intentSource = Json.Read<RelaySession>(Json.Write(((CloudQueueSourceChoice)Source.SelectedItem).Source)); intent = new CloudQueueTeam { Id = id, Name = TeamName.Text.Trim(), Repository = Repository.Text.Trim(), Mandate = Mandate.Text.Trim(), Source = intentSource, Targets = targets.Select(value => Json.Read<CloudQueueTarget>(Json.Write(value.Target))).ToList(), MaximumParallel = maximum, Enabled = original?.Enabled ?? true, Paused = original?.Paused ?? false, IdleAfterMinutes = minutes, IdleAlertsEnabled = AlertsEnabled.IsChecked == true, ExplicitDelegation = true }; await Commit(() => configure(intent, intentSource));
        }
    }
}
