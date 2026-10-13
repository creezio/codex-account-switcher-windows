using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal sealed partial class PerformancePage
    {
        internal readonly ListBox Instances = new ListBox();
        internal readonly ComboBox CompactInstances = new ComboBox();
        internal readonly TextBox InstanceSearch = new TextBox(), ProcessSearch = new TextBox();
        internal readonly DataGrid Processes = Table();
        internal readonly CheckBox Pause = new CheckBox { Content = "Pause" };
        internal readonly ComboBox Interval = new ComboBox { ItemsSource = new[] { "5 secondes", "10 secondes", "30 secondes" }, Width = 106 };
        internal Button RefreshAction, ShowAction, CheckAction, OverviewTab, ProcessesTab;
        internal readonly MenuItem CloseAction = new MenuItem { Header = "Fermer l’instance" }, RestartAction = new MenuItem { Header = "Redémarrer" };
        internal readonly ContextMenu ActionsMenu = new ContextMenu();
        internal readonly ComboBox FamilySelect = new ComboBox { ItemsSource = new[] { "Tous", "Fenêtres", "Moteur", "Outils", "Commandes" }, Width = 114 };
        internal readonly Dictionary<string, Button> FamilyButtons = new Dictionary<string, Button>();
        internal readonly Grid Workspace = new Grid(), DetailPanel = new Grid(), Metrics = new Grid();
        internal readonly Grid Overview = new Grid(), ProcessPanel = new Grid();
        private readonly Grid rail = new Grid(), compactSelector = new Grid(), selectedMetrics = new Grid(), summaryRows = new Grid();
        private readonly StackPanel topConsumers = new StackPanel();
        private readonly Grid titleRow = new Grid();
        private Grid headerGrid, actions;
        private Grid instanceSearchField;
        private Border surface;
        private readonly StackPanel chips = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Canvas graph = new Canvas { Height = 26, ClipToBounds = true };
        private readonly TextBlock systemRam = Label("—", 22), codexRam = Label("—", 22), cpu = Label("—", 22), count = Label("—", 22);
        private readonly TextBlock systemHint = Label("En attente de mesure", 11, true), stamp = Label("Mesure en cours…", 11, true);
        private readonly TextBlock detailTitle = Label("Choisissez une instance", 21), accountText = Label("", 12, true);
        private readonly TextBlock activityText = Label("", 12, true), recommendation = Label("", 11, true);
        private readonly TextBlock selectedRam = Label("—", 18), selectedCommit = Label("—", 18), selectedCpu = Label("—", 18), selectedCount = Label("—", 18);
        private readonly TextBlock processFooter = Label("", 11, true), processTitle = Label("Sélectionnez un processus pour ses détails", 12), processCount = Label("", 11, true), emptyText = Label("", 13, true);
        private readonly TextBlock subtitle = Label("Vos instances, leur activité et leur consommation.", 12, true);
        private Border graphBox;
        private DataGridColumn roleColumn;
        private bool compact, overviewVisible = true;

        private static TextBlock Label(string value, double size = 13, bool muted = false)
        {
            var text = Ui.Text(value, size, muted);
            text.Margin = new Thickness(0);
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.SetBinding(ToolTipProperty, new Binding("Text") { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
            return text;
        }

        private void BuildLayout()
        {
            Margin = new Thickness(24, 20, 24, 16);
            Header.Children.Clear();
            Commands.Visibility = Visibility.Collapsed;
            Notice.TextWrapping = TextWrapping.NoWrap;
            Notice.TextTrimming = TextTrimming.CharacterEllipsis;
            Notice.Margin = new Thickness(0, 4, 0, 8);
            Notice.SetBinding(ToolTipProperty, new Binding("Text") { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
            headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel();
            heading.Children.Add(Label("Performances", 26));
            subtitle.Margin = new Thickness(0, 4, 12, 0);
            heading.Children.Add(subtitle);
            headerGrid.Children.Add(heading);
            var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(headerActions, 1);
            headerGrid.Children.Add(headerActions);
            Interval.FontSize = 12;
            Interval.Margin = new Thickness(0, 0, 10, 0);
            Interval.ToolTip = "Fréquence des mesures automatiques. La collecte s’arrête lorsque cette page est masquée.";
            Interval.SelectedIndex = settings.Interval == 5 ? 0 : settings.Interval == 10 ? 1 : 2;
            Interval.SelectionChanged += delegate { settings.Interval = new[] { 5, 10, 30 }[Math.Max(0, Interval.SelectedIndex)]; Context.Store.WriteRecord("performance-settings.dpapi", settings); };
            AutomationProperties.SetName(Interval, "Fréquence d’actualisation");
            headerActions.Children.Add(Interval);
            Pause.Margin = new Thickness(0, 0, 12, 0);
            Pause.VerticalAlignment = VerticalAlignment.Center;
            Pause.FontSize = 12;
            Pause.ToolTip = "Suspendre les mesures automatiques. Actualiser reste disponible.";
            Pause.Checked += delegate { UpdateStamp(); };
            Pause.Unchecked += delegate { UpdateStamp(); };
            headerActions.Children.Add(Pause);
            RefreshAction = TaskButton("Actualiser", RefreshNow);
            RefreshAction.Margin = new Thickness(0);
            headerActions.Children.Add(RefreshAction);
            Header.Children.Add(headerGrid);

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Body.Children.Add(body);
            for (int n = 0; n < 4; n++) Metrics.ColumnDefinitions.Add(new ColumnDefinition());
            AddMetric("Mémoire du PC", systemRam, systemHint, 0, "Part de la mémoire physique utilisée par toutes les applications du PC.");
            AddMetric("RAM Codex", codexRam, Label("Mémoire résidente", 11, true), 1, "Somme des instances Codex attribuées. Les pages partagées peuvent être comptées dans plusieurs processus. Le Switcher et les origines non vérifiées sont séparés.");
            AddMetric("CPU Codex", cpu, Label("Part du CPU du PC", 11, true), 2, "Mesure entre deux relevés, rapportée à tous les cœurs du PC. ≥ signale une mesure partielle.");
            AddMetric("Processus", count, Label("PID comptés une fois", 11, true), 3, "Processus des instances Codex, y compris ceux d’origine non vérifiée. Le Switcher est séparé.");
            Metrics.Margin = new Thickness(0, 0, 0, 16);
            body.Children.Add(Metrics);
            Grid.SetRow(Workspace, 1);
            body.Children.Add(Workspace);
            Workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Workspace.RowDefinitions.Add(new RowDefinition());
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            Workspace.ColumnDefinitions.Add(new ColumnDefinition());

            instanceView = CollectionViewSource.GetDefaultView(groups);
            instanceView.Filter = item => { var row = (ResourceRow)item; return (row.Name + " " + row.Account).IndexOf(InstanceSearch.Text, StringComparison.CurrentCultureIgnoreCase) >= 0; };
            instanceView.SortDescriptions.Add(new SortDescription("SectionOrder", ListSortDirection.Ascending));
            instanceView.SortDescriptions.Add(new SortDescription("Order", ListSortDirection.Ascending));
            instanceView.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
            Instances.ItemsSource = instanceView;
            Instances.ItemTemplate = InstanceTemplate();
            Instances.SetResourceReference(Control.BackgroundProperty, "CanvasBrush");
            Instances.Padding = new Thickness(0);
            ScrollViewer.SetHorizontalScrollBarVisibility(Instances, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(Instances, true);
            var groupHeader = new DataTemplate();
            var groupText = new FrameworkElementFactory(typeof(TextBlock));
            groupText.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            groupText.SetValue(TextBlock.FontSizeProperty, 10.0);
            groupText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            groupText.SetValue(MarginProperty, new Thickness(8, 10, 0, 6));
            groupText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            groupHeader.VisualTree = groupText;
            Instances.GroupStyle.Add(new GroupStyle { HeaderTemplate = groupHeader });
            Instances.SelectionChanged += delegate { SelectInstance(Instances.SelectedItem as ResourceRow); };
            Instances.MouseDoubleClick += async delegate { if (ShowAction.IsEnabled) await RunAction(Show); };
            AutomationProperties.SetName(Instances, "Choisir une instance");
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rail.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(rail, 1);
            Workspace.Children.Add(rail);
            InstanceSearch.Margin = new Thickness(0, 0, 0, 4);
            InstanceSearch.FontSize = 12;
            InstanceSearch.TextChanged += delegate { FilterInstances(); };
            instanceSearchField = Ui.SearchField(InstanceSearch, "Rechercher une instance");
            var instanceWatermark = (TextBlock)instanceSearchField.Children[1];
            instanceWatermark.FontSize = 12;
            instanceWatermark.TextWrapping = TextWrapping.NoWrap;
            instanceWatermark.TextTrimming = TextTrimming.CharacterEllipsis;
            rail.Children.Add(instanceSearchField);
            Grid.SetRow(Instances, 1);
            rail.Children.Add(Instances);

            compactSelector.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            compactSelector.ColumnDefinitions.Add(new ColumnDefinition());
            CompactInstances.ItemsSource = instanceView;
            CompactInstances.DisplayMemberPath = "Name";
            CompactInstances.Margin = new Thickness(12, 0, 0, 8);
            CompactInstances.MinWidth = 140;
            CompactInstances.SelectionChanged += delegate { SelectInstance(CompactInstances.SelectedItem as ResourceRow); };
            AutomationProperties.SetName(CompactInstances, "Choisir une instance en petite fenêtre");
            Grid.SetColumn(CompactInstances, 1);
            compactSelector.Children.Add(CompactInstances);
            Grid.SetColumnSpan(compactSelector, 3);
            Workspace.Children.Add(compactSelector);

            surface = Surface(DetailPanel, new Thickness(16));
            surface.Margin = new Thickness(0);
            Grid.SetRow(surface, 1);
            Grid.SetColumn(surface, 2);
            Workspace.Children.Add(surface);
            for (int n = 0; n < 5; n++) DetailPanel.RowDefinitions.Add(new RowDefinition { Height = n == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            titleRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            titleRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailTitle.FontWeight = FontWeights.SemiBold;
            titleRow.Children.Add(detailTitle);
            accountText.Margin = new Thickness(0, 4, 0, 8);
            Grid.SetRow(accountText, 1);
            titleRow.Children.Add(accountText);
            DetailPanel.Children.Add(titleRow);
            actions = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ShowAction = TaskButton("Afficher Codex", Show, true);
            ShowAction.Margin = new Thickness(0, 0, 8, 0);
            ShowAction.FontSize = 12;
            actions.Children.Add(ShowAction);
            CheckAction = TaskButton("Vérifier l’activité", CheckActivity);
            CheckAction.FontSize = 12;
            CheckAction.Margin = new Thickness(0, 0, 0, 0);
            Grid.SetColumn(CheckAction, 1);
            actions.Children.Add(CheckAction);
            ActionsMenu.Items.Add(CloseAction);
            ActionsMenu.Items.Add(RestartAction);
            CloseAction.Click += async delegate { await RunAction(() => Act(false)); };
            RestartAction.Click += async delegate { await RunAction(() => Act(true)); };
            var more = Ui.Button("⋯", delegate { ActionsMenu.IsOpen = true; });
            more.Width = 36;
            more.MinHeight = 30;
            more.Margin = new Thickness(8, 0, 0, 0);
            more.Padding = new Thickness(0);
            more.ToolTip = "Fermer ou redémarrer cette instance";
            AutomationProperties.SetName(more, "Actions de l’instance");
            ActionsMenu.PlacementTarget = more;
            ActionsMenu.Placement = PlacementMode.Bottom;
            Grid.SetColumn(more, 3);
            actions.Children.Add(more);
            Grid.SetRow(actions, 1);
            DetailPanel.Children.Add(actions);

            for (int n = 0; n < 4; n++) selectedMetrics.ColumnDefinitions.Add(new ColumnDefinition());
            selectedMetrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var values = new[] { selectedRam, selectedCommit, selectedCpu, selectedCount };
            var labels = new[] { "RAM", "Engagée", "CPU", "Processus" };
            for (int n = 0; n < 4; n++)
            {
                var metric = new StackPanel { Margin = new Thickness(0, 2, 6, 2) };
                metric.Children.Add(Label(labels[n], 11, true));
                values[n].Margin = new Thickness(0, 2, 0, 0);
                metric.Children.Add(values[n]);
                Grid.SetColumn(metric, n);
                selectedMetrics.Children.Add(metric);
            }
            graphBox = new Border { Child = graph, Margin = new Thickness(4, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center };
            graph.ToolTip = "Évolution de la RAM de cette instance · 60 mesures au maximum · échelle ajustée";
            Grid.SetColumn(graphBox, 4);
            selectedMetrics.Children.Add(graphBox);
            graph.SizeChanged += delegate { DrawGraph(); };
            selectedMetrics.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(selectedMetrics, 2);
            DetailPanel.Children.Add(selectedMetrics);

            var info = new Grid();
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.38, GridUnitType.Star) });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.62, GridUnitType.Star) });
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            activityText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            AutomationProperties.SetName(activityText, "Activité des chats de l’instance");
            info.Children.Add(activityText);
            activityText.Margin = new Thickness(0, 0, 6, 6);
            recommendation.Margin = new Thickness(8, 0, 0, 6);
            Grid.SetColumn(recommendation, 1);
            info.Children.Add(recommendation);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            OverviewTab = Ui.Button("Vue d’ensemble", () => SelectTab(true));
            ProcessesTab = Ui.Button("Processus", () => SelectTab(false));
            foreach (var tab in new[] { OverviewTab, ProcessesTab }) { tab.FontSize = 12; tab.MinHeight = 30; tab.Padding = new Thickness(10, 5, 10, 5); tab.Margin = new Thickness(0, 0, 6, 8); tabs.Children.Add(tab); }
            Grid.SetRow(tabs, 2);
            Grid.SetColumnSpan(tabs, 2);
            info.Children.Add(tabs);
            Grid.SetRow(info, 3);
            DetailPanel.Children.Add(info);

            Grid.SetRow(Overview, 4);
            Overview.RowDefinitions.Add(new RowDefinition());
            Overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Overview.Children.Add(summaryRows);
            Grid.SetRow(topConsumers, 1);
            Overview.Children.Add(topConsumers);
            DetailPanel.Children.Add(Overview);

            Grid.SetRow(ProcessPanel, 4);
            ProcessPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ProcessPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ProcessPanel.RowDefinitions.Add(new RowDefinition());
            ProcessPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition());
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ProcessSearch.FontSize = 12;
            ProcessSearch.MinHeight = 32;
            ProcessSearch.Margin = new Thickness(0);
            var search = Ui.SearchField(ProcessSearch, "Rechercher un processus ou un PID");
            // SearchField's default bottom margin is intended for form fields; this toolbar has no extra row gap.
            ((TextBlock)search.Children[1]).Margin = new Thickness(11, 0, 8, 0);
            toolbar.Children.Add(search);
            ProcessSearch.TextChanged += delegate { FilterProcesses(); };
            FamilySelect.Margin = new Thickness(8, 0, 0, 0);
            FamilySelect.FontSize = 12;
            FamilySelect.SelectedIndex = 0;
            FamilySelect.SelectionChanged += delegate { if (FamilySelect.SelectedItem != null) SelectFamily(FamilySelect.SelectedItem.ToString()); };
            AutomationProperties.SetName(FamilySelect, "Famille de processus");
            Grid.SetColumn(FamilySelect, 1);
            toolbar.Children.Add(FamilySelect);
            ProcessPanel.Children.Add(toolbar);
            foreach (string label in FamilySelect.ItemsSource)
            {
                var button = Ui.Button(label, () => SelectFamily(label));
                button.MinHeight = 28;
                button.FontSize = 11;
                button.Padding = new Thickness(9, 4, 9, 4);
                button.Margin = new Thickness(0, 0, 5, 0);
                FamilyButtons[label] = button;
                chips.Children.Add(button);
            }
            chips.Margin = new Thickness(8, 0, 0, 0);
            chips.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chips, 1);
            toolbar.Children.Add(chips);
            processView = CollectionViewSource.GetDefaultView(processes);
            processView.Filter = item => { var p = (ResourceProcessRow)item; return (family == "Tous" || family == p.Family) && (p.Name + " " + p.Role + " " + p.Pid).IndexOf(ProcessSearch.Text, StringComparison.CurrentCultureIgnoreCase) >= 0; };
            processView.SortDescriptions.Add(new SortDescription("MemoryBytes", ListSortDirection.Descending));
            Processes.ItemsSource = processView;
            AddColumn("PID", "Pid", 54);
            AddColumn("Processus", "Name", 1.1, null, true);
            roleColumn = AddColumn("Rôle", "Role", 1.3, null, true);
            AddColumn("RAM", "Ram", 84, "MemoryBytes");
            AddColumn("CPU", "Cpu", 62, "CpuNumber");
            Processes.SelectionChanged += delegate { ProcessFooter(); };
            Processes.SizeChanged += delegate { ResizeColumns(); };
            AutomationProperties.SetName(Processes, "Processus de l’instance");
            Grid.SetRow(Processes, 2);
            ProcessPanel.Children.Add(Processes);
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(emptyText, 2);
            emptyText.IsHitTestVisible = false;
            ProcessPanel.Children.Add(emptyText);
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            footer.Children.Add(processTitle);
            processFooter.Margin = new Thickness(0, 3, 0, 0);
            footer.Children.Add(processFooter);
            Grid.SetRow(footer, 3);
            ProcessPanel.Children.Add(footer);
            DetailPanel.Children.Add(ProcessPanel);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition());
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.Children.Add(stamp);
            processCount.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(processCount, 1);
            bottom.Children.Add(processCount);
            Grid.SetRow(bottom, 2);
            body.Children.Add(bottom);
            SizeChanged += delegate { ApplyResponsiveLayout(); };
            DetailPanel.SizeChanged += delegate { ApplyResponsiveLayout(); };
            Overview.SizeChanged += delegate { ApplyResponsiveLayout(); };
            SelectFamily("Tous");
            SelectTab(true);
        }

        private static DataTemplate InstanceTemplate()
        {
            var template = new DataTemplate(typeof(ResourceRow));
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            void Line(string property, double size, bool muted, Thickness margin)
            {
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetBinding(TextBlock.TextProperty, new Binding(property));
                text.SetBinding(ToolTipProperty, new Binding(property));
                text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
                text.SetValue(TextBlock.FontSizeProperty, size);
                text.SetValue(MarginProperty, margin);
                text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
                if (property == "Name") text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
                stack.AppendChild(text);
            }
            Line("Name", 13, false, new Thickness(0));
            Line("Account", 11, true, new Thickness(0, 3, 0, 0));
            Line("Footprint", 11, true, new Thickness(0, 6, 0, 0));
            template.VisualTree = stack;
            return template;
        }

        private static Border Surface(UIElement child, Thickness padding)
        {
            var card = Ui.Card(child);
            card.Padding = padding;
            return card;
        }

        private void AddMetric(string title, TextBlock value, TextBlock hint, int column, string description)
        {
            var stack = new StackPanel();
            stack.Children.Add(Label(title, 12, true));
            value.FontWeight = FontWeights.SemiBold;
            value.Margin = new Thickness(0, 4, 0, 2);
            stack.Children.Add(value);
            stack.Children.Add(hint);
            var card = Surface(stack, new Thickness(14, 12, 10, 12));
            card.Margin = new Thickness(0, 0, column == 3 ? 0 : 10, 0);
            card.ToolTip = description;
            Grid.SetColumn(card, column);
            Metrics.Children.Add(card);
        }

        private Button TaskButton(string text, Func<Task> action, bool primary = false)
        {
            Button button = null;
            button = Ui.Button(text, async delegate { if (!button.IsEnabled) return; button.IsEnabled = false; await RunAction(action); button.IsEnabled = true; RenderDetail(); }, primary);
            return button;
        }

        private async Task RunAction(Func<Task> action)
        {
            try { await action(); } catch (Exception e) { Error(e); } finally { RenderDetail(); }
        }

        private static DataGrid Table()
        {
            var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false, CanUserResizeColumns = false, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 30, ColumnHeaderHeight = 30, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, EnableRowVirtualization = true, EnableColumnVirtualization = true, FontSize = 12, Margin = new Thickness(0), BorderThickness = new Thickness(0, 1, 0, 1) };
            ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
            table.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            table.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            table.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
            table.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "LineBrush");
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SurfaceBrush")));
            rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
            var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("AccentSoftBrush")));
            rowStyle.Triggers.Add(selected);
            table.RowStyle = rowStyle;
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("CanvasBrush")));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("MutedBrush")));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 0, 7, 0)));
            table.ColumnHeaderStyle = headerStyle;
            var cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var selection = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selection.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("AccentSoftBrush")));
            selection.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
            cell.Triggers.Add(selection);
            table.CellStyle = cell;
            return table;
        }

        private DataGridColumn AddColumn(string title, string property, double width, string sort = null, bool star = false)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
            style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            var column = new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel), MinWidth = star ? 30 : width, SortMemberPath = sort ?? property, ElementStyle = style };
            Processes.Columns.Add(column);
            return column;
        }

        internal void SelectTab(bool overview)
        {
            overviewVisible = overview;
            Overview.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            ProcessPanel.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
            foreach (var tab in new[] { OverviewTab, ProcessesTab })
            {
                bool active = tab == (overview ? OverviewTab : ProcessesTab);
                tab.SetResourceReference(Control.BackgroundProperty, active ? "AccentSoftBrush" : "SurfaceBrush");
                tab.SetResourceReference(Control.ForegroundProperty, active ? "AccentTextBrush" : "MutedBrush");
                tab.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
            UpdateProcessCount();
        }

        private void SelectFamily(string value)
        {
            family = value;
            if (!Equals(FamilySelect.SelectedItem, value)) FamilySelect.SelectedItem = value;
            foreach (var pair in FamilyButtons)
            {
                pair.Value.SetResourceReference(Control.BackgroundProperty, pair.Key == value ? "AccentSoftBrush" : "SurfaceBrush");
                pair.Value.SetResourceReference(Control.ForegroundProperty, pair.Key == value ? "AccentTextBrush" : "MutedBrush");
            }
            if (processView != null) FilterProcesses();
        }

        private void FilterProcesses()
        {
            var previous = Processes.SelectedItem;
            processView.Refresh();
            if (previous != null && processView.Contains(previous)) Processes.SelectedItem = previous;
            ProcessFooter();
            UpdateProcessCount();
        }

        private void UpdateProcessCount()
        {
            int shown = processView?.Cast<object>().Count() ?? 0;
            processCount.Text = overviewVisible ? "" : shown + " / " + processes.Count + " processus";
            emptyText.Text = processes.Count == 0 ? "Aucun processus pour cette instance." : "Aucun résultat · modifiez les filtres.";
            emptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ProcessFooter()
        {
            var row = Processes.SelectedItem as ResourceProcessRow;
            processTitle.Text = row == null ? "Sélectionnez un processus pour ses détails" : row.Name + "  ·  PID " + row.Pid + "  ·  " + row.Role;
            processFooter.Text = row == null ? "RAM résidente · les pages partagées peuvent être comptées plusieurs fois." : "Parent " + row.Parent + "  ·  engagée " + row.Commit + "  ·  " + row.Started + "  ·  " + row.Association;
            processTitle.ToolTip = processTitle.Text + "\n" + processFooter.Text;
        }

        private void UpdateOverview()
        {
            var codex = snapshot.Groups.Where(g => g.Scope.Id != "switcher").ToArray();
            codexRam.Text = (codex.Any(g => g.Partial) ? "≥ " : "") + Memory(codex.Sum(g => g.Resident));
            cpu.Text = codex.Any(g => g.Cpu.HasValue) ? (codex.Any(g => g.CpuPartial) ? "≥ " : "") + Cpu(codex.Sum(g => g.Cpu ?? 0)) : codex.All(g => g.Processes.Length == 0) ? "0 %" : "—";
            count.Text = (codex.Sum(g => g.Processes.Length) + snapshot.Unassigned.Length).ToString();
            if (snapshot.TotalPhysical > 0)
            {
                double used = 100d * (snapshot.TotalPhysical - snapshot.AvailablePhysical) / snapshot.TotalPhysical;
                systemRam.Text = used.ToString("0") + " %";
                systemHint.Text = Memory(snapshot.AvailablePhysical) + " libres";
                systemRam.ToolTip = used >= 85 ? "Mémoire du PC fortement sollicitée. Une instance vérifiée au repos peut être fermée pour libérer sa mémoire." : "Mémoire physique utilisée sur le PC.";
            }
            else { systemRam.Text = "—"; systemHint.Text = "Non mesurée"; }
            UpdateStamp();
        }

        private void UpdateStamp()
        {
            stamp.Text = snapshot == null ? "Mesure en cours…" : (Pause.IsChecked == true ? "En pause · " : "Actualisé · ") + snapshot.Captured.ToLocalTime().ToString("HH:mm:ss");
            stamp.ToolTip = snapshot == null ? "" : "Collecte Windows : " + snapshot.CollectionMilliseconds.ToString("0") + " ms. RAM résidente incluant les pages partagées. Mémoire engagée = allocations privées, différente du jeu de travail privé du Gestionnaire des tâches.";
        }

        private void DrawSummary()
        {
            summaryRows.Children.Clear();
            summaryRows.RowDefinitions.Clear();
            var all = selected?.Group.Processes ?? new ResourceProcess[0];
            var rows = all.GroupBy(p => ResourceProcessRow.FamilyOf(p.Role ?? ProcessResources.Role(p.Name, null))).Select(g => new { Family = g.Key, Bytes = g.Sum(p => p.Resident ?? 0), Count = g.Count() }).OrderByDescending(g => g.Bytes).ToArray();
            if (rows.Length == 0) { var empty = Label("Cette instance est fermée ou aucun processus n’est mesuré.", 12, true); empty.VerticalAlignment = VerticalAlignment.Center; summaryRows.Children.Add(empty); }
            else
            {
                summaryRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var heading = Label("Répartition de la RAM", 12, true);
                heading.Margin = new Thickness(0, 2, 0, 6);
                summaryRows.Children.Add(heading);
                long max = Math.Max(1, rows.Max(r => r.Bytes));
                for (int n = 0; n < rows.Length; n++)
                {
                    summaryRows.RowDefinitions.Add(new RowDefinition());
                    var item = new Grid { Margin = new Thickness(0, 0, 0, 2), MaxHeight = 44 };
                    item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                    item.ColumnDefinitions.Add(new ColumnDefinition());
                    item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                    item.Children.Add(Label(rows[n].Family + " · " + rows[n].Count, 12));
                    var bar = new ProgressBar { Minimum = 0, Maximum = max, Value = rows[n].Bytes, Height = 7, Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = rows[n].Count + " processus · " + Memory(rows[n].Bytes) };
                    Grid.SetColumn(bar, 1);
                    item.Children.Add(bar);
                    var amount = Label(Memory(rows[n].Bytes), 12);
                    amount.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(amount, 2);
                    item.Children.Add(amount);
                    Grid.SetRow(item, n + 1);
                    summaryRows.Children.Add(item);
                }
                summaryRows.RowDefinitions.Add(new RowDefinition());
            }
            topConsumers.Children.Clear();
            if (all.Length > 0)
            {
                var title = Label("Les plus gourmands", 12, true);
                title.Margin = new Thickness(0, 10, 0, 8);
                topConsumers.Children.Add(title);
                foreach (var p in all.Where(p => p.Resident.HasValue).OrderByDescending(p => p.Resident).Take(3))
                {
                    var item = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                    item.ColumnDefinitions.Add(new ColumnDefinition());
                    item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    item.Children.Add(Label(p.Name + " · PID " + p.Pid + " · " + p.Role, 12));
                    var memory = Label(Memory(p.Resident.Value), 12);
                    memory.Margin = new Thickness(8, 0, 0, 0);
                    Grid.SetColumn(memory, 1);
                    item.Children.Add(memory);
                    topConsumers.Children.Add(item);
                }
            }
        }

        private void ApplyResponsiveLayout()
        {
            if (ActualWidth <= 0) return;
            compact = ActualWidth < 740;
            bool shortWindow = ActualHeight < 550;
            bool veryShort = ActualHeight < 480;
            var searchParent = (Panel)instanceSearchField.Parent;
            if (searchParent != (compact ? compactSelector : rail))
            {
                searchParent.Children.Remove(instanceSearchField);
                Grid.SetRow(instanceSearchField, 0);
                Grid.SetColumn(instanceSearchField, 0);
                (compact ? compactSelector : rail).Children.Add(instanceSearchField);
            }
            InstanceSearch.MinHeight = compact && veryShort ? 30 : 36;
            InstanceSearch.Margin = new Thickness(0, 0, 0, compact ? (veryShort ? 6 : 8) : 4);
            CompactInstances.MinHeight = compact && veryShort ? 30 : 36;
            CompactInstances.Margin = new Thickness(12, 0, 0, compact && veryShort ? 6 : 8);
            rail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            compactSelector.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            Workspace.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 232);
            Workspace.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 16);
            subtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            headerGrid.Margin = new Thickness(0, 0, 0, compact ? 10 : 16);
            selectedMetrics.Visibility = ActualHeight < 620 ? Visibility.Collapsed : Visibility.Visible;
            graphBox.Visibility = DetailPanel.ActualWidth < 540 ? Visibility.Collapsed : Visibility.Visible;
            selectedMetrics.ColumnDefinitions[4].Width = new GridLength(graphBox.Visibility == Visibility.Visible ? 90 : 0);
            topConsumers.Visibility = DetailPanel.RowDefinitions[4].ActualHeight < 250 ? Visibility.Collapsed : Visibility.Visible;
            chips.Visibility = compact || DetailPanel.ActualWidth < 650 ? Visibility.Collapsed : Visibility.Visible;
            FamilySelect.Visibility = chips.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (roleColumn != null) roleColumn.Visibility = DetailPanel.ActualWidth < 500 ? Visibility.Collapsed : Visibility.Visible;
            Metrics.Margin = new Thickness(0, 0, 0, compact ? 10 : 16);
            foreach (Border metric in Metrics.Children)
            {
                metric.Padding = compact ? new Thickness(10, veryShort ? 6 : 8, 8, veryShort ? 6 : 8) : new Thickness(14, 12, 10, 12);
                ((StackPanel)metric.Child).Children[2].Visibility = compact && shortWindow ? Visibility.Collapsed : Visibility.Visible;
            }
            foreach (var value in new[] { systemRam, codexRam, cpu, count }) value.FontSize = compact ? (veryShort ? 16 : 18) : 22;
            detailTitle.FontSize = compact ? 18 : 21;
            surface.Padding = compact ? new Thickness(10) : new Thickness(16);
            foreach (var button in new[] { ShowAction, CheckAction }) { button.MinHeight = compact ? 30 : 36; button.Padding = compact ? new Thickness(10, 5, 10, 5) : new Thickness(14, 8, 14, 8); }
            actions.Margin = new Thickness(0, 0, 0, compact ? 6 : 10);
            detailTitle.Visibility = compact && veryShort ? Visibility.Collapsed : Visibility.Visible;
            accountText.Margin = new Thickness(0, compact && veryShort ? 0 : 4, 0, compact ? 4 : 8);
            activityText.Visibility = compact && shortWindow ? Visibility.Collapsed : Visibility.Visible;
            recommendation.Visibility = compact && shortWindow ? Visibility.Collapsed : Visibility.Visible;
            accountText.Text = selected == null ? "Modifiez la recherche dans le sélecteur." : selected.Account + (selectedMetrics.Visibility == Visibility.Collapsed ? "  ·  RAM " + selected.Ram + "  ·  CPU " + selected.Cpu : "") + "  ·  " + selected.Count + " processus";
            accountText.FontSize = compact ? 11 : 12;
            accountText.ToolTip = (selected?.Account ?? "") + "\nRAM " + selected?.Ram + " · engagée " + selected?.Commit + " · CPU " + selected?.Cpu + "\n" + (selected?.Activity ?? "") + "\n" + recommendation.Text;
            foreach (var tab in new[] { OverviewTab, ProcessesTab }) { tab.MinHeight = compact ? 28 : 30; tab.Margin = new Thickness(0, 0, 6, compact ? 4 : 8); }
            processFooter.Visibility = compact && shortWindow ? Visibility.Collapsed : Visibility.Visible;
            ResizeColumns();
        }

        private void ResizeColumns()
        {
            if (Processes.Columns.Count < 5 || Processes.ActualWidth <= 0) return;
            // Explicit responsive widths also handle a virtualized grid first measured while its tab was hidden.
            double remaining = Math.Max(30, Processes.ActualWidth - 54 - 84 - 62 - 32);
            bool role = roleColumn.Visibility == Visibility.Visible;
            Processes.Columns[1].Width = new DataGridLength(role ? remaining * 0.46 : remaining);
            roleColumn.Width = new DataGridLength(role ? Math.Max(30, remaining * 0.54) : 30);
        }
    }
}
