using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Creezio.Switcher.Desktop
{
    internal sealed class PerformanceSettings
    {
        public int Interval { get; set; } = 5;
        public string SelectedInstance { get; set; }
    }

    internal sealed class ResourceRow : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Account { get; set; }
        public string State { get; set; }
        public string Ram { get; set; }
        public string Commit { get; set; }
        public string Cpu { get; set; }
        public double? CpuNumber { get; set; }
        public string Activity { get; set; }
        public string Status { get; set; }
        public bool Busy { get; set; }
        public int Count { get; set; }
        public long Bytes { get; set; }
        public long CommittedBytes { get; set; }
        public int Order { get; set; }
        public int SectionOrder => Id == "switcher" || Id == "unassigned" ? 1 : 0;
        public string Section => SectionOrder == 0 ? "Instances Codex" : "Système";
        public string Footprint => Count == 0 ? Status : Ram + "  ·  " + Cpu + "  ·  " + Status;
        internal ResourceGroup Group;
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    internal sealed class ResourceProcessRow : INotifyPropertyChanged
    {
        public int Pid { get; set; }
        public int Parent { get; set; }
        public string Name { get; set; }
        public string Role { get; set; }
        public string Ram { get; set; }
        public string Commit { get; set; }
        public string Cpu { get; set; }
        public string Started { get; set; }
        public string Association { get; set; }
        public long? MemoryBytes { get; set; }
        public long? CommitBytes { get; set; }
        public double? CpuNumber { get; set; }
        public string Family => FamilyOf(Role);
        internal long Identity;
        internal static string FamilyOf(string role)
        {
            if (role == "Moteur Codex") return "Moteur";
            if (role == "Commande / terminal") return "Commandes";
            if (role != null && (role.Contains("fenêtre") || role.Contains("Rendu") || role.Contains("GPU") || role.Contains("Application Codex") || role.Contains("plantage"))) return "Fenêtres";
            return "Outils";
        }
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    internal sealed partial class PerformancePage : PageView
    {
        private readonly ObservableCollection<ResourceRow> groups = new ObservableCollection<ResourceRow>();
        private readonly ObservableCollection<ResourceProcessRow> processes = new ObservableCollection<ResourceProcessRow>();
        private readonly Dictionary<string, ResourceActivity> activities = new Dictionary<string, ResourceActivity>();
        private readonly Dictionary<string, Queue<double>> histories = new Dictionary<string, Queue<double>>();
        private readonly HashSet<string> checking = new HashSet<string>();
        private readonly PerformanceSettings settings;
        private ICollectionView instanceView, processView;
        private ResourceSnapshot snapshot;
        private ResourceRow selected;
        private bool loading, acting, selecting;
        private string family = "Tous";
        private string processFingerprint, instanceFingerprint;
        internal Func<string, bool> ConfirmAction;

        internal PerformancePage(DesktopContext context, ShellWindow shell) : base(context, shell, "Performances", "")
        {
            settings = context.Store.ReadRecord<PerformanceSettings>("performance-settings.dpapi");
            if (!new[] { 5, 10, 30 }.Contains(settings.Interval)) settings.Interval = 5;
            BuildLayout();
            ConfirmAction = message => Ui.Confirm(Shell, message, "Gestion de l’instance");
            RenderDetail();
        }

        internal static string Memory(long bytes) => bytes >= 1024L * 1024 * 1024 ? (bytes / 1073741824d).ToString("0.00", CultureInfo.CurrentCulture) + " Gio" : (bytes / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) + " Mio";
        private static string Cpu(double? value) => value.HasValue ? value.Value.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "—";
        internal Task RefreshNow() => Load(true);
        public override Task Refresh() => Load(false);

        private async Task Load(bool force)
        {
            if (loading || acting || !force && (Pause.IsChecked == true || snapshot != null && (DateTime.UtcNow - snapshot.Captured).TotalSeconds < settings.Interval)) return;
            loading = true;
            RefreshAction.IsEnabled = false;
            try
            {
                snapshot = await Context.Resources();
                string previous = selected?.Id ?? settings.SelectedInstance;
                selecting = true;
                var ids = new HashSet<string>();
                int order = 0;
                foreach (var group in snapshot.Groups)
                {
                    ids.Add(group.Scope.Id);
                    ResourceActivity cached;
                    if (activities.TryGetValue(group.Scope.Id, out cached) && (group.Scope.State?.Running != true || cached.Pid != group.Scope.State.DesktopPid || cached.Started != group.Scope.State.DesktopStartTicks)) activities.Remove(group.Scope.Id);
                    var row = groups.FirstOrDefault(r => r.Id == group.Scope.Id);
                    if (row == null) { row = new ResourceRow { Id = group.Scope.Id }; groups.Add(row); }
                    row.Group = group;
                    row.Name = group.Scope.Name;
                    row.Account = group.Scope.Account;
                    row.State = group.Scope.State?.Caption ?? "État inconnu";
                    row.Bytes = group.Resident;
                    row.CommittedBytes = group.PrivateBytes;
                    row.Ram = (group.Partial ? "≥ " : "") + Memory(group.Resident);
                    row.Commit = (group.Partial ? "≥ " : "") + Memory(group.PrivateBytes);
                    row.Cpu = (group.CpuPartial && group.Cpu.HasValue ? "≥ " : "") + Cpu(group.Cpu);
                    row.CpuNumber = group.Cpu;
                    row.Count = group.Processes.Length;
                    row.Order = order++;
                    UpdateActivityRow(row);
                    RecordHistory(row);
                    row.Changed();
                }
                if (snapshot.Unassigned.Length > 0)
                {
                    ids.Add("unassigned");
                    var row = groups.FirstOrDefault(r => r.Id == "unassigned");
                    if (row == null) { row = new ResourceRow { Id = "unassigned" }; groups.Add(row); }
                    row.Group = new ResourceGroup { Scope = new ResourceScope { Id = row.Id, Name = "Origine non vérifiée" }, Processes = snapshot.Unassigned };
                    row.Name = "Origine non vérifiée";
                    row.Account = "Attribution impossible";
                    row.State = "À examiner";
                    row.Bytes = snapshot.Unassigned.Sum(p => p.Resident ?? 0);
                    row.CommittedBytes = snapshot.Unassigned.Sum(p => p.PrivateBytes ?? 0);
                    row.Ram = "≥ " + Memory(row.Bytes);
                    row.Commit = "≥ " + Memory(row.CommittedBytes);
                    row.Cpu = "—";
                    row.Count = snapshot.Unassigned.Length;
                    row.Order = order;
                    UpdateActivityRow(row);
                    RecordHistory(row);
                    row.Changed();
                }
                foreach (var removed in groups.Where(r => !ids.Contains(r.Id)).ToArray()) groups.Remove(removed);
                foreach (string removed in activities.Keys.Where(id => !ids.Contains(id)).ToArray()) activities.Remove(removed);
                foreach (string removed in histories.Keys.Where(id => !ids.Contains(id)).ToArray()) histories.Remove(removed);
                string orderStamp = String.Join("|", groups.Select(r => r.Id + ":" + r.Order + ":" + r.Name + ":" + r.Account));
                if (orderStamp != instanceFingerprint) { instanceView.Refresh(); instanceFingerprint = orderStamp; }
                SetSelected(instanceView.Cast<ResourceRow>().FirstOrDefault(r => r.Id == previous) ?? instanceView.Cast<ResourceRow>().FirstOrDefault());
                selecting = false;
                UpdateOverview();
                RenderDetail();
                ResourceActivity last;
                if (selected?.Group.Scope.State?.Running == true && selected.SectionOrder == 0 && (!activities.TryGetValue(selected.Id, out last) || DateTime.UtcNow - last.Checked > TimeSpan.FromSeconds(30))) _ = ObserveActivity();
            }
            finally { selecting = false; loading = false; RefreshAction.IsEnabled = true; }
        }

        private void UpdateActivityRow(ResourceRow row)
        {
            ResourceActivity cached;
            activities.TryGetValue(row.Id, out cached);
            bool headless=(row.Group.Scope.Background??new ResourceRoot[0]).Any(root=>row.Group.Processes.Any(p=>p.Pid==root.Pid&&p.Started==root.Started));
            row.Busy = cached?.State == "busy";
            row.Activity = row.Id == "switcher" ? "Supervision et relais" : row.Id == "unassigned" ? "Aucune fermeture automatique" : row.Group.Scope.State?.Running != true ? (headless?"Moteur sans fenêtre · suivi dans Tâches":row.Count > 0 ? "Processus encore présents · état à vérifier" : "Instance fermée") : cached?.Message ?? "Activité non vérifiée";
            row.Status = row.Id == "switcher" ? "Supervision" : row.Id == "unassigned" ? "À examiner" : row.Group.Scope.State?.Running != true ? (headless?"Sans fenêtre":row.Count > 0 ? "À vérifier" : "Fermée") : row.Busy ? "Actif" : "Ouverte";
        }

        private void RecordHistory(ResourceRow row)
        {
            Queue<double> history;
            if (!histories.TryGetValue(row.Id, out history)) histories[row.Id] = history = new Queue<double>();
            history.Enqueue(row.Bytes / 1048576d);
            while (history.Count > 60) history.Dequeue();
        }

        private void SetSelected(ResourceRow row)
        {
            bool changed = selected?.Id != row?.Id;
            selected = row;
            bool previous = selecting;
            selecting = true;
            Instances.SelectedItem = CompactInstances.SelectedItem = row;
            selecting = previous;
            if (changed) { Processes.SelectedItem = null; Processes.CurrentCell = new DataGridCellInfo(); processFooter.Text = ""; }
        }

        private void SelectInstance(ResourceRow row)
        {
            if (selecting) return;
            SetSelected(row);
            settings.SelectedInstance = row?.Id;
            Context.Store.WriteRecord("performance-settings.dpapi", settings);
            RenderDetail();
            if (row?.Group.Scope.State?.Running == true && row.SectionOrder == 0) _ = ObserveActivity();
        }

        private void FilterInstances()
        {
            string id = selected?.Id;
            selecting = true;
            instanceView.Refresh();
            SetSelected(instanceView.Cast<ResourceRow>().FirstOrDefault(r => r.Id == id) ?? instanceView.Cast<ResourceRow>().FirstOrDefault());
            selecting = false;
            RenderDetail();
        }

        private void RenderDetail()
        {
            var row = selected;
            detailTitle.Text = row?.Name ?? (groups.Count > 0 ? "Aucune instance trouvée" : "Choisissez une instance");
            accountText.Text = row == null ? "Modifiez la recherche dans le sélecteur." : row.Account + "  ·  " + row.Count + " processus";
            selectedRam.Text = row?.Ram ?? "—";
            selectedCommit.Text = row?.Commit ?? "—";
            selectedCpu.Text = row?.Cpu ?? "—";
            selectedCount.Text = row?.Count.ToString() ?? "—";
            ResourceActivity activity = null;
            if (row != null) activities.TryGetValue(row.Id, out activity);
            activityText.Text = row?.Activity ?? "";
            activityText.ToolTip = row?.Activity + (activity?.Titles.Length > 0 ? "\n" + String.Join("\n", activity.Titles.Take(10)) : "");
            bool running = row?.Group.Scope.State?.Running == true;
            bool managed = row?.Group.Scope.Managed == true;
            bool known = running && row.Group.Scope.State.Phase != "unknown" && row.Group.Processes.Any(p => p.Pid == row.Group.Scope.State.DesktopPid && p.Started > 0 && p.Started == row.Group.Scope.State.DesktopStartTicks);
            ShowAction.Content = running ? "Afficher Codex" : "Ouvrir Codex";
            System.Windows.Automation.AutomationProperties.SetName(ShowAction, ShowAction.Content.ToString());
            ShowAction.IsEnabled = row != null && row.SectionOrder == 0 && !acting;
            CheckAction.IsEnabled = running && row.SectionOrder == 0 && !acting && !checking.Contains(row.Id);
            CloseAction.IsEnabled = RestartAction.IsEnabled = known && managed && !acting && activity?.State != "busy";
            recommendation.Text = row == null ? "" : row.Group.Scope.Primary ? "Instance principale protégée · fermeture depuis Codex." : row.Id == "switcher" ? "Le Switcher supervise les instances et le relais." : row.Id == "unassigned" ? "Origine inconnue · aucun arrêt individuel proposé." : !running ? "Ouvrir Codex relance cette instance avec son compte." : activity?.State == "busy" ? "Un chat travaille ou attend votre réponse · fermeture protégée." : "Fermer ou redémarrer vérifie tous les chats de cette instance.";
            recommendation.ToolTip = recommendation.Text + "\nUne faible consommation ne prouve pas qu’un chat est au repos. Les actions portent sur l’instance entière et ses sous-processus, jamais sur un PID isolé.";
            UpdateProcesses(row?.Group.Processes ?? new ResourceProcess[0]);
            DrawSummary();
            DrawGraph();
            ProcessFooter();
            ApplyResponsiveLayout();
        }

        private void UpdateProcesses(ResourceProcess[] source)
        {
            string fingerprint = selected?.Id + "|" + String.Join("|", source.Select(p => p.Pid + ":" + p.Started + ":" + p.Parent + ":" + p.Name + ":" + p.Role + ":" + p.Resident + ":" + p.PrivateBytes + ":" + p.CpuPercent + ":" + p.Association));
            if (fingerprint == processFingerprint) return;
            processFingerprint = fingerprint;
            var selectedProcess = Processes.SelectedItem as ResourceProcessRow;
            var scroll = Ui.Descendants(Processes).OfType<ScrollViewer>().FirstOrDefault();
            double offset = scroll?.VerticalOffset ?? 0;
            var keys = new HashSet<string>();
            foreach (var p in source)
            {
                string key = p.Pid + ":" + p.Started;
                keys.Add(key);
                var item = processes.FirstOrDefault(r => r.Pid == p.Pid && r.Identity == p.Started);
                if (item == null) { item = new ResourceProcessRow { Pid = p.Pid, Identity = p.Started }; processes.Add(item); }
                item.Parent = p.Parent;
                item.Name = p.Name;
                item.Role = p.Role ?? ProcessResources.Role(p.Name, null);
                item.Ram = p.Resident.HasValue ? Memory(p.Resident.Value) : "Non mesuré";
                item.Commit = p.PrivateBytes.HasValue ? Memory(p.PrivateBytes.Value) : "Non mesuré";
                item.Cpu = Cpu(p.CpuPercent);
                item.Started = p.Started > 0 ? new DateTime(p.Started, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss") : "Inconnu";
                item.Association = p.Association ?? "Non vérifiée";
                item.MemoryBytes = p.Resident;
                item.CommitBytes = p.PrivateBytes;
                item.CpuNumber = p.CpuPercent;
                item.Changed();
            }
            foreach (var stale in processes.Where(p => !keys.Contains(p.Pid + ":" + p.Identity)).ToArray()) processes.Remove(stale);
            processView.Refresh();
            if (selectedProcess != null && processView.Contains(selectedProcess)) Processes.SelectedItem = selectedProcess;
            Processes.UpdateLayout();
            scroll?.ScrollToVerticalOffset(offset);
            UpdateProcessCount();
        }

        private async Task ObserveActivity()
        {
            ResourceActivity last;
            if (selected != null && activities.TryGetValue(selected.Id, out last) && DateTime.UtcNow - last.Checked < TimeSpan.FromSeconds(30)) return;
            try { await CheckActivity(); } catch (Exception e) { Error(e); }
        }

        private async Task CheckActivity()
        {
            var row = selected;
            if (row?.Group.Scope.State?.Running != true || row.SectionOrder != 0 || !checking.Add(row.Id)) return;
            CheckAction.IsEnabled = false;
            try
            {
                var state = row.Group.Scope.State;
                if (selected == row) activityText.Text = "Lecture de l’activité des chats…";
                var found = await Context.ResourceActivity(row.Id);
                var current = groups.FirstOrDefault(r => r.Id == row.Id);
                // Never apply a response from an older process identity to a new launch.
                if (current?.Group.Scope.State?.Running != true || current.Group.Scope.State.DesktopPid != state.DesktopPid || current.Group.Scope.State.DesktopStartTicks != state.DesktopStartTicks) return;
                found.Pid = state.DesktopPid;
                found.Started = state.DesktopStartTicks;
                activities[row.Id] = found;
                UpdateActivityRow(current);
                current.Changed();
            }
            finally { checking.Remove(row.Id); RenderDetail(); }
        }

        private async Task Show()
        {
            var row = selected;
            if (row == null || row.SectionOrder != 0 || acting) return;
            acting = true;
            RenderDetail();
            try { Notice.Text = await Context.OpenOrShowInstance(row.Id); }
            finally { acting = false; RenderDetail(); }
        }

        private async Task Act(bool restart)
        {
            var row = selected;
            if (acting || row?.Group.Scope.Managed != true || row.Group.Scope.State?.Running != true || !(restart ? RestartAction : CloseAction).IsEnabled) return;
            var expected = row.Group.Scope.State;
            if (!ConfirmAction((restart ? "Redémarrer" : "Fermer") + " « " + row.Name + " » et ses sous-processus ? Son compte et ses conversations seront conservés. Les autres instances resteront ouvertes.")) return;
            acting = true;
            RenderDetail();
            try
            {
                Notice.Text = "Vérification complète des chats de « " + row.Name + " »…";
                await Context.RequireIdleResourceInstance(row.Id);
                Notice.Text = await Context.CloseOrRestartInstance(row.Id, restart, expected);
                activities.Remove(row.Id);
            }
            finally { acting = false; RenderDetail(); }
            await RefreshNow();
        }

        private void DrawGraph()
        {
            graph.Children.Clear();
            Queue<double> history;
            if (selected == null || !histories.TryGetValue(selected.Id, out history) || history.Count < 2) return;
            double width = Math.Max(1, graph.ActualWidth), height = Math.Max(1, graph.ActualHeight), max = Math.Max(1, history.Max());
            var line = new Polyline { StrokeThickness = 2 };
            line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            int index = 0;
            foreach (double value in history) line.Points.Add(new Point(width * index++ / (history.Count - 1), height - 3 - Math.Max(0, height - 6) * value / max));
            graph.Children.Add(line);
        }
    }
}
