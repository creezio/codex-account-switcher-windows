using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal abstract class CloudQueueWizard : Window
    {
        internal readonly Button NextButton, BackButton, CancelButton;
        internal readonly TextBlock ErrorText = Ui.Text("", 12);
        internal Func<bool> ConfirmDiscard;
        internal int Step { get; private set; }
        internal bool Saved { get; private set; }
        internal bool Saving { get; private set; }
        protected readonly Grid[] Panels;
        protected readonly CancellationTokenSource Lifetime;
        protected readonly TextBlock Subtitle = Ui.Text("", 13, true);
        protected bool IsClosed;
        protected bool Frozen;
        private readonly Border[] markers;
        private readonly string finish;
        protected CloudQueueWizard(Window owner, string title, string[] labels, string finish, CancellationToken token)
        {
            this.finish = finish; Owner = owner; Title = title; Width = 780; Height = 680; MinWidth = 650; MinHeight = 570; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush"); Lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var root = new Grid { Margin = new Thickness(25, 22, 25, 18) }; Content = root; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel(); heading.Children.Add(Ui.Text(title, 25)); heading.Children.Add(Subtitle); var steps = new UniformGrid { Columns = labels.Length, Margin = new Thickness(0, 12, 0, 16) }; markers = new Border[labels.Length];
            for (int n = 0; n < labels.Length; n++) { var text = Ui.Text((n + 1) + "  " + labels[n], 12); text.Margin = new Thickness(0); markers[n] = new Border { Child = text, Padding = new Thickness(9), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, n + 1 == labels.Length ? 0 : 7, 0) }; steps.Children.Add(markers[n]); } heading.Children.Add(steps); root.Children.Add(heading);
            var body = new Grid { Margin = new Thickness(0, 0, 0, 10) }; Panels = new Grid[labels.Length]; for (int n = 0; n < labels.Length; n++) { Panels[n] = new Grid(); body.Children.Add(Panels[n]); } Grid.SetRow(body, 1); root.Children.Add(body);
            var footer = new Grid(); footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ErrorText.Margin = new Thickness(0, 4, 0, 8); footer.Children.Add(ErrorText); AutomationProperties.SetLiveSetting(ErrorText, AutomationLiveSetting.Polite);
            var actions = new DockPanel { LastChildFill = false }; CancelButton = Ui.Button("Annuler", Close); DockPanel.SetDock(CancelButton, Dock.Right); actions.Children.Add(CancelButton); NextButton = Ui.AsyncButton("Continuer", Next, exception => ErrorText.Text = Program.SafeError(exception), true); DockPanel.SetDock(NextButton, Dock.Right); actions.Children.Add(NextButton); BackButton = Ui.Button("Retour", () => ShowStep(Step - 1)); actions.Children.Add(BackButton); Grid.SetRow(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer);
            Closing += (sender, args) => { if (Saved) return; if (Saving) { args.Cancel = true; return; } if (Dirty() && !(ConfirmDiscard?.Invoke() ?? Ui.Confirm(this, "Abandonner ce brouillon ? Aucune mission n’a encore été envoyée.", "Fermer l’assistant"))) args.Cancel = true; };
            base.Closed += delegate { IsClosed = true; Lifetime.Cancel(); }; ShowStep(0);
        }
        protected abstract bool Dirty();
        protected abstract Task Continue();
        private async Task Next() { if (IsClosed || Saving) return; ErrorText.Text = ""; try { await Continue(); } catch (Exception exception) { ErrorText.Text = Program.SafeError(exception); } }
        internal void ShowStep(int step)
        {
            if (step < 0 || step >= Panels.Length || Saving || Frozen) return; Step = step;
            for (int n = 0; n < Panels.Length; n++) { Panels[n].Visibility = n == step ? Visibility.Visible : Visibility.Collapsed; markers[n].SetResourceReference(Border.BorderBrushProperty, n == step ? "AccentBrush" : "LineBrush"); markers[n].SetResourceReference(Border.BackgroundProperty, n == step ? "SelectedBrush" : "SurfaceBrush"); }
            Subtitle.Text = "Étape " + (step + 1) + " sur " + Panels.Length; UpdateNavigation();
        }
        protected void UpdateNavigation()
        {
            BackButton.Visibility = Step == 0 ? Visibility.Hidden : Visibility.Visible; BackButton.IsEnabled = !Saving && !Frozen; NextButton.Content = Step + 1 == Panels.Length ? finish : "Continuer"; NextButton.IsEnabled = !Saving; CancelButton.IsEnabled = !Saving;
        }
        protected async Task Commit(Func<Task> save)
        {
            Saving = true; Frozen = true; foreach (var panel in Panels) panel.IsEnabled = false; UpdateNavigation();
            try { await save(); Saved = true; Close(); }
            finally { Saving = false; if (!IsClosed) { UpdateNavigation(); NextButton.Content = "Réessayer le même enregistrement"; } }
        }
        protected static void Labeled(Panel target, string label, Control field) { target.Children.Add(Ui.Text(label, 12, true)); target.Children.Add(field); AutomationProperties.SetName(field, label); }
    }

    internal sealed class CloudPreparedMissionWizard : CloudQueueWizard
    {
        internal readonly TextBox MissionTitle = new TextBox { MaxLength = 200 };
        internal readonly TextBox Mandate = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, MaxLength = 24000 };
        internal readonly TextBox Feature = new TextBox { MaxLength = 100 }, Lot = new TextBox { MaxLength = 80 }, Specification = new TextBox { MaxLength = 1000 };
        internal readonly TextBox Reserved = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 60, MaxHeight = 75 };
        internal readonly ComboBox Priority = new ComboBox { ItemsSource = new[] { "Normale", "Haute", "Urgente" }, SelectedIndex = 0 };
        internal readonly TextBox Model = new TextBox { MaxLength = 100 };
        internal readonly ComboBox Effort = new ComboBox { ItemsSource = new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" } }, Permission = new ComboBox { DisplayMemberPath = "Label" };
        internal readonly CheckBox Authorization = new CheckBox { Content = "J’autorise l’attribution automatique de ce mandat aux comptes cochés." };
        internal readonly ListBox Workers = new ListBox { BorderThickness = new Thickness(0) }, Dependencies = new ListBox { BorderThickness = new Thickness(0) };
        internal string MissionId { get; } = Guid.NewGuid().ToString("N");
        private readonly CloudQueueTeam team;
        private readonly CloudTaskExecutionOptions execution;
        private readonly Func<CloudPreparedMission, Task> prepare;
        private readonly List<Tuple<CheckBox, string>> workers = new List<Tuple<CheckBox, string>>();
        private readonly List<Tuple<CheckBox, ComboBox, string>> dependencies = new List<Tuple<CheckBox, ComboBox, string>>();
        private readonly TextBlock review = Ui.Text("", 13), reviewScope = Ui.Text("", 12, true);
        private CloudPreparedMission intent;
        private sealed class PermissionChoice { public string Label { get; set; } internal string Id; }
        internal CloudPreparedMissionWizard(Window owner, CloudQueueTeam team, IEnumerable<CloudPreparedMission> missions, CloudMissionAccountChoice[] accounts, CloudTaskExecutionOptions execution, Func<CloudPreparedMission, Task> prepare, CancellationToken token)
            : base(owner, "Préparer une mission Cloud", new[] { "Mandat", "Livraison", "Attribution", "Autoriser" }, "Ajouter à la file", token)
        {
            this.team = Json.Read<CloudQueueTeam>(Json.Write(team)); this.execution = Json.Read<CloudTaskExecutionOptions>(Json.Write(execution)); this.prepare = prepare;
            var mandate = Panels[0]; mandate.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); mandate.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); mandate.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); mandate.RowDefinitions.Add(new RowDefinition()); mandate.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mandate.Children.Add(Ui.Text("Quel résultat l’agent doit-il livrer ?", 17)); Grid.SetRow(MissionTitle, 1); mandate.Children.Add(MissionTitle); var label = Ui.Text("Contexte, périmètre, vérifications et critères de réussite", 12, true); label.Margin = new Thickness(0, 12, 0, 5); Grid.SetRow(label, 2); mandate.Children.Add(label); Grid.SetRow(Mandate, 3); mandate.Children.Add(Mandate); var note = Ui.Text("Ce mandat sera envoyé tel quel. Incluez les références utiles, sans clé ni secret.", 12, true); note.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(note, 4); mandate.Children.Add(note); AutomationProperties.SetName(MissionTitle, "Titre du mandat préparé"); AutomationProperties.SetName(Mandate, "Mandat préparé complet");
            var delivery = new StackPanel(); Panels[1].Children.Add(delivery); delivery.Children.Add(Ui.Text(team.Repository, 16)); delivery.Children.Add(Ui.Text("La branche reprend le compte, la fonctionnalité et le lot.", 12, true));
            var pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition()); var left = new StackPanel { Margin = new Thickness(0, 8, 8, 10) }; var right = new StackPanel { Margin = new Thickness(8, 8, 0, 10) }; Labeled(left, "Fonctionnalité", Feature); Labeled(right, "Numéro du lot", Lot); pair.Children.Add(left); Grid.SetColumn(right, 1); pair.Children.Add(right); delivery.Children.Add(pair); Labeled(delivery, "Issue ou référence du cahier des charges", Specification);
            var priorityLine = new StackPanel { Margin = new Thickness(0, 10, 0, 10) }; Labeled(priorityLine, "Priorité dans la file", Priority); delivery.Children.Add(priorityLine); Labeled(delivery, "Fichiers ou dossiers réservés (un chemin relatif par ligne)", Reserved); delivery.Children.Add(Ui.Text("Ex. src/recherche et tests/recherche. Ils évitent les missions concurrentes sur les mêmes zones.", 11, true));
            var assignment = Panels[2]; assignment.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); assignment.RowDefinitions.Add(new RowDefinition()); assignment.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); assignment.Children.Add(Ui.Text("Quels comptes et quels préalables autorisez-vous ?", 17));
            var columns = new Grid { Margin = new Thickness(0, 8, 0, 5) }; columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
            var workersPanel = new Grid { Margin = new Thickness(0, 0, 10, 0) }; workersPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); workersPanel.RowDefinitions.Add(new RowDefinition()); workersPanel.Children.Add(Ui.Text("COMPTES ÉLIGIBLES", 11, true)); Grid.SetRow(Workers, 1); workersPanel.Children.Add(Workers); columns.Children.Add(workersPanel);
            foreach (var target in team.Targets ?? new List<CloudQueueTarget>()) { string targetId = target.InstanceId; var choice = accounts.FirstOrDefault(account => account.Id == targetId && account.AccountKey == target.ExpectedAccountKey); var check = new CheckBox { Content = new TextBlock { Text = choice?.Label ?? "Compte indisponible · " + targetId, TextWrapping = TextWrapping.Wrap }, IsEnabled = choice != null, Margin = new Thickness(5, 7, 5, 10) }; AutomationProperties.SetName(check, "Autoriser " + (choice?.Label ?? targetId)); workers.Add(Tuple.Create(check, targetId)); Workers.Items.Add(check); }
            var dependenciesPanel = new Grid { Margin = new Thickness(10, 0, 0, 0) }; dependenciesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); dependenciesPanel.RowDefinitions.Add(new RowDefinition()); dependenciesPanel.Children.Add(Ui.Text("MISSIONS PRÉALABLES", 11, true)); Grid.SetRow(Dependencies, 1); dependenciesPanel.Children.Add(Dependencies); Grid.SetColumn(dependenciesPanel, 1); columns.Children.Add(dependenciesPanel);
            foreach (var mission in missions.Where(value => value.TeamId == team.Id && value.State != "cancelled")) { var check = new CheckBox { Content = new TextBlock { Text = mission.Title, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 4) }; var mode = new ComboBox { ItemsSource = new[] { "Après validation par le chef", "Dès la fin du travail" }, SelectedIndex = 0, IsEnabled = false, FontSize = 12 }; check.Checked += delegate { mode.IsEnabled = true; }; check.Unchecked += delegate { mode.IsEnabled = false; }; var row = new StackPanel { Margin = new Thickness(5, 5, 5, 12) }; row.Children.Add(check); row.Children.Add(mode); Dependencies.Items.Add(row); dependencies.Add(Tuple.Create(check, mode, mission.Id)); }
            if (Dependencies.Items.Count == 0) Dependencies.Items.Add(Ui.Text("Aucune autre mission dans cette équipe.", 12, true)); Grid.SetRow(columns, 1); assignment.Children.Add(columns); var depHelp = Ui.Text("Par défaut, une dépendance attend la revue et l’intégration par le chef. « Dès la fin » est un choix explicite.", 12, true); Grid.SetRow(depHelp, 2); assignment.Children.Add(depHelp);
            var last = Panels[3]; last.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); last.RowDefinitions.Add(new RowDefinition()); last.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); last.Children.Add(Ui.Text("Vérifiez le mandat avant de l’autoriser", 18)); var reviewBody = new StackPanel(); reviewBody.Children.Add(review);
            var settings = new Grid { Margin = new Thickness(0, 10, 0, 12) }; settings.ColumnDefinitions.Add(new ColumnDefinition()); settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            var modelPanel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; Labeled(modelPanel, "Modèle", Model); Model.Text = this.execution.Model ?? ""; Model.ToolTip = "Identifiant exact du modèle disponible sur les comptes sélectionnés ; vérifié avant tout envoi.";
            var effortPanel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; Labeled(effortPanel, "Raisonnement", Effort); Effort.SelectedItem = this.execution.ReasoningEffort;
            var permissionPanel = new StackPanel(); Labeled(permissionPanel, "Permissions", Permission); var permissionChoices = new List<PermissionChoice> { new PermissionChoice { Label = "Lecture seule", Id = ":read-only" }, new PermissionChoice { Label = "Lecture et écriture", Id = ":workspace-write" }, new PermissionChoice { Label = "Accès complet", Id = ":danger-full-access" } }; if (this.execution.PermissionProfile != null && !permissionChoices.Any(choice => choice.Id == this.execution.PermissionProfile)) permissionChoices.Add(new PermissionChoice { Label = "Profil : " + this.execution.PermissionProfile, Id = this.execution.PermissionProfile }); Permission.ItemsSource = permissionChoices; Permission.SelectedItem = permissionChoices.FirstOrDefault(choice => choice.Id == this.execution.PermissionProfile);
            settings.Children.Add(modelPanel); Grid.SetColumn(effortPanel, 1); settings.Children.Add(effortPanel); Grid.SetColumn(permissionPanel, 2); settings.Children.Add(permissionPanel); reviewBody.Children.Add(settings); reviewBody.Children.Add(Ui.Text("Vos préférences préremplissent ces choix. Aucune permission n’est élargie automatiquement ; les trois valeurs doivent être choisies et seront vérifiées sur le compte exécutant.", 11, true)); reviewBody.Children.Add(reviewScope);
            var reviewScroll = new ScrollViewer { Content = reviewBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(reviewScroll, 1); last.Children.Add(reviewScroll); Authorization.Content = new TextBlock { Text = "J’autorise l’attribution automatique de ce mandat aux comptes cochés.", TextWrapping = TextWrapping.Wrap }; Authorization.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(Authorization, 2); last.Children.Add(Authorization); AutomationProperties.SetName(Authorization, "Autorisation explicite du mandat préparé");
        }
        protected override bool Dirty() => !String.IsNullOrEmpty(MissionTitle?.Text) || !String.IsNullOrEmpty(Mandate?.Text);
        protected override async Task Continue()
        {
            if (Frozen) { await Commit(() => prepare(intent)); return; }
            if (Step == 0) { if (String.IsNullOrWhiteSpace(MissionTitle.Text) || String.IsNullOrWhiteSpace(Mandate.Text)) throw new InvalidOperationException("Donnez un titre et un mandat complet à cette mission."); ShowStep(1); return; }
            if (Step == 1) { if (String.IsNullOrWhiteSpace(Feature.Text) || String.IsNullOrWhiteSpace(Lot.Text) || String.IsNullOrWhiteSpace(Specification.Text)) throw new InvalidOperationException("Renseignez la fonctionnalité, le lot et sa référence pour retrouver la livraison."); if (!Regex.IsMatch(Lot.Text, @"^(?:lot-)?[0-9]{1,6}(?:[-.][0-9]{1,6}){0,3}$", RegexOptions.IgnoreCase)) throw new InvalidOperationException("Utilisez un numéro de lot, par exemple lot-2-03."); Reservations(); ShowStep(2); return; }
            if (Step == 2)
            {
                if (!workers.Any(value => value.Item1.IsEnabled && value.Item1.IsChecked == true)) throw new InvalidOperationException("Cochez au moins un compte autorisé à réaliser cette mission.");
                review.Text = MissionTitle.Text + "\n" + team.Name + " · " + team.Repository + "\n" + Feature.Text + " / " + Lot.Text + "\n" + workers.Count(value => value.Item1.IsChecked == true) + " compte(s) autorisé(s), " + dependencies.Count(value => value.Item1.IsChecked == true) + " préalable(s).";
                reviewScope.Text = Mandate.Text + "\n\nLe Switcher n’invente pas de mission, ne fusionne aucune branche et transmet le résultat au chat orchestrateur configuré. Les réglages choisis seront figés pour ce mandat."; ShowStep(3); return;
            }
            if (Authorization.IsChecked != true) throw new InvalidOperationException("Autorisez explicitement ce mandat pour l’ajouter à la file.");
            var settings = CloudTaskExecutionOptions.Validate(new CloudTaskExecutionOptions { Model = Model.Text, ReasoningEffort = Effort.SelectedItem as string, PermissionProfile = (Permission.SelectedItem as PermissionChoice)?.Id });
            if (settings.Model == null || settings.ReasoningEffort == null || settings.PermissionProfile == null) throw new InvalidOperationException("Choisissez le modèle, le raisonnement et les permissions de ce mandat avant de l’autoriser.");
            intent = new CloudPreparedMission { Id = MissionId, TeamId = team.Id, Title = MissionTitle.Text.Trim(), Prompt = Mandate.Text, Repository = team.Repository, Feature = Feature.Text.Trim(), Lot = Lot.Text.Trim(), Specification = Specification.Text.Trim(), Priority = Priority.SelectedIndex == 2 ? 100 : Priority.SelectedIndex == 1 ? 10 : 0, ReservedFiles = Reservations(), EligibleInstanceIds = workers.Where(value => value.Item1.IsEnabled && value.Item1.IsChecked == true).Select(value => value.Item2).ToList(), Dependencies = dependencies.Where(value => value.Item1.IsChecked == true).Select(value => new CloudQueueDependency { MissionId = value.Item3, Mode = value.Item2.SelectedIndex == 1 ? "completed" : "accepted" }).ToList(), Model = settings.Model, ReasoningEffort = settings.ReasoningEffort, PermissionProfile = settings.PermissionProfile, ExplicitDelegation = true };
            await Commit(() => prepare(intent));
        }
        private List<string> Reservations()
        {
            var values = Reserved.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
            if (values.Length < 1 || values.Length > 100) throw new InvalidOperationException("Réservez de 1 à 100 chemins de fichiers ou dossiers pour ce mandat.");
            return values.Select(CloudMissionQueue.ReservationPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
