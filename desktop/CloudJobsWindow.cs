using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudJobsWindow : Window
    {
        private readonly DesktopContext context;
        private readonly ShellWindow shell;
        private readonly ListBox list = new ListBox();
        private readonly StackPanel detail = new StackPanel();
        private readonly TextBlock notice = Ui.Text("", 13, true), error = Ui.Text("", 13);
        private readonly TextBlock title = Ui.Text("", 21), account = Ui.Text("", 14), branch = Ui.Text("", 13, true), jobError = Ui.Text("", 13);
        private readonly TextBox result = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock references = Ui.Text("", 12, true);
        private readonly Expander diagnostic;
        private readonly WrapPanel jobActions;
        private readonly ScrollViewer detailScroll;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private bool loading, closed, changing, composing, acting;
        private string listStamp, detailStamp, selectedId, requestedSelection;
        internal Func<Task<CloudJob[]>> JobsFixture = null;
        internal Func<string, CancellationToken, Task<CloudConfiguration[]>> EnvironmentsFixture = null;
        internal Func<CloudJobSpec, CancellationToken, Task<CloudJob>> SubmitFixture = null;
        internal Func<string, CancellationToken, Task<CloudJob>> AdvanceFixture = null;
        internal Action EnsureWorkerFixture = null;
        internal ListBox JobsList => list;
        internal ScrollViewer DetailsScroll => detailScroll;
        internal TextBox ResultText => result;
        internal string SelectedJobId => (list.SelectedItem as Row)?.Job.Id;
        internal bool RefreshTimerRunning => timer.IsEnabled;
        internal bool Refreshing => loading;
        internal Task RefreshNow() => Refresh();
        private sealed class Row
        {
            public string Title { get; set; }
            public string AccountLine { get; set; }
            public string StateCaption { get; set; }
            public CloudJob Job { get; set; }
        }
        internal CloudJobsWindow(DesktopContext context, ShellWindow shell)
        {
            this.context = context; this.shell = shell;
            Owner = shell; Title = "Missions Codex Cloud"; Width = 1050; Height = 730; MinWidth = 780; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new Grid { Margin = new Thickness(24) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); Content = root;
            var header = new StackPanel(); root.Children.Add(header);
            header.Children.Add(Ui.Text("Missions Codex Cloud", 25));
            header.Children.Add(Ui.Text("Un compte, un environnement prêt et une branche distincte pour chaque mission. Les fichiers locaux ne sont pas synchronisés automatiquement.", 13, true));
            var actions = Ui.Actions(header); actions.Children.Add(Ui.AsyncButton("Nouvelle mission Cloud", Compose, Error, true)); actions.Children.Add(Ui.AsyncButton("Actualiser", Refresh, Error));
            header.Children.Add(notice); header.Children.Add(error);
            AutomationProperties.SetName(notice, "État du suivi des missions Cloud"); AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(error, "Erreur des missions Cloud"); AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
            var split = new Grid(); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); split.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(split, 1); root.Children.Add(split); split.Children.Add(list);
            detailScroll = new ScrollViewer { Content = detail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(detailScroll, 2); split.Children.Add(detailScroll);
            var template = new DataTemplate(typeof(Row)); var row = new FrameworkElementFactory(typeof(StackPanel));
            foreach (string field in new[] { "Title", "AccountLine", "StateCaption" }) { var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(field)); label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); label.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 0, 5)); row.AppendChild(label); }
            template.VisualTree = row; list.ItemTemplate = template; AutomationProperties.SetName(list, "Missions Cloud et comptes exécutants");
            detail.Children.Add(title); detail.Children.Add(account); detail.Children.Add(branch); detail.Children.Add(jobError); jobActions = Ui.Actions(detail); detail.Children.Add(Ui.Text("Résultat", 17)); detail.Children.Add(result);
            diagnostic = new Expander { Header = "Diagnostic et références", Content = references }; detail.Children.Add(diagnostic); detail.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(result, "Résultat de la mission Cloud");
            timer.Tick += async delegate { try { await Refresh(); } catch (Exception e) { Error(e); } };
            list.SelectionChanged += delegate { if (!changing) ShowSelected(); };
            Loaded += async delegate { try { await Refresh(); } catch (Exception e) { Error(e); } finally { if (!closed) timer.Start(); } };
            Closed += delegate { closed = true; timer.Stop(); lifetime.Cancel(); };
        }
        internal static string State(string state)
        {
            switch (state)
            {
                case "queued": return "En attente";
                case "creating": return "Création du chat Cloud";
                case "prepared": return "Chat préparé";
                case "sending": return "Envoi du mandat";
                case "running": return "En cours";
                case "attention": return "Intervention requise";
                case "completed": return "Terminée";
                case "failed": return "Échec";
                case "uncertain": return "Résultat à vérifier";
                case "cancelled": return "Annulée";
                default: return "État à vérifier";
            }
        }
        private static string ReturnState(CloudJob job)
        {
            if (!job.ReturnToSource) return "Non demandé";
            switch (job.ReturnState)
            {
                case "delivered": return job.SourceReturnState=="running"?"Remis · traitement local en cours":job.SourceReturnState=="attention"?"Remis · traitement local à vérifier":new[]{"failed","interrupted"}.Contains(job.SourceReturnState)?"Remis · traitement local interrompu":job.SourceReturnState=="completed"?"Remis · traitement local terminé":"Remis au chat source";
                case "pending": return "Prêt pour remise";
                case "sending": return "Remise en cours";
                case "uncertain": return "Remise à vérifier";
                case "attention": return "Remise bloquée · résultat disponible";
                default: return "En attente du résultat";
            }
        }
        private void Error(Exception exception) { if (!closed) error.Text = Program.SafeError(exception); }
        private async Task Refresh()
        {
            if (loading || closed) return;
            loading = true;
            try
            {
                var jobs = context.Fixture && JobsFixture != null ? await JobsFixture() : await context.Read(accounts => new CloudJobs(context.Store).List());
                var rows = await context.Read(accounts => jobs.Select(job => new Row
                {
                    Job = job, Title = job.Title, StateCaption = State(job.State),
                    AccountLine = (accounts.Data.Instances.FirstOrDefault(instance => instance.Id == job.InstanceId)?.Name ?? job.InstanceName) + " · " + (accounts.Data.Profiles.FirstOrDefault(profile => profile.Key == job.AccountKey)?.Email ?? "Compte indisponible")
                }).ToArray());
                if (closed) return;
                string id = requestedSelection ?? (list.SelectedItem as Row)?.Job.Id;
                string stamp = Json.Write(rows.Select(value => new { value.Job.Id, value.Title, value.AccountLine, value.StateCaption }));
                if (stamp != listStamp)
                {
                    var scroll = Ui.Descendants(list).OfType<ScrollViewer>().FirstOrDefault(); double offset = scroll?.VerticalOffset ?? 0;
                    changing = true; list.ItemsSource = rows; list.SelectedItem = rows.FirstOrDefault(value => value.Job.Id == id) ?? rows.FirstOrDefault(); changing = false; listStamp = stamp;
                    list.UpdateLayout(); scroll?.ScrollToVerticalOffset(offset);
                }
                else
                {
                    foreach (var item in list.Items.Cast<Row>()) item.Job = jobs.Single(job => job.Id == item.Job.Id);
                    if (requestedSelection != null) list.SelectedItem = list.Items.Cast<Row>().FirstOrDefault(value => value.Job.Id == requestedSelection) ?? list.SelectedItem;
                }
                requestedSelection = null;
                notice.Text = jobs.Length == 0 ? "Aucune mission Cloud. Créez un environnement et terminez sa préparation dans Instances → Cloud." : jobs.Length + " mission(s) · " + DateTime.Now.ToString("HH:mm:ss");
                ShowSelected();
            }
            finally { changing = false; loading = false; }
        }
        private void ShowSelected()
        {
            var row = list.SelectedItem as Row; var job = row?.Job;
            if (job == null) { detail.Visibility = Visibility.Collapsed; selectedId = detailStamp = null; return; }
            string stamp = Json.Write(new { job.Id, job.Title, row.AccountLine, job.State, job.Branch, job.Result, job.Error, job.ConfigurationId, job.ThreadId, job.ReturnState, job.SourceReturnState, job.ReturnToSource, acting });
            if (stamp == detailStamp) return;
            bool same = selectedId == job.Id; double outer = same ? detailScroll.VerticalOffset : 0, inner = same ? result.VerticalOffset : 0;
            int selectionStart = same ? result.SelectionStart : 0, selectionLength = same ? result.SelectionLength : 0;
            bool expanded = same && diagnostic.IsExpanded;
            selectedId = job.Id; detailStamp = stamp; detail.Visibility = Visibility.Visible;
            title.Text = job.Title; account.Text = row.AccountLine + " · " + State(job.State); branch.Text = "Branche : " + (job.Branch ?? "Préparée lors de l’enregistrement");
            jobError.Text = job.Error ?? ""; jobError.Visibility = String.IsNullOrEmpty(job.Error) ? Visibility.Collapsed : Visibility.Visible;
            jobActions.Children.Clear();
            if ((!context.Fixture || AdvanceFixture != null) && new[] { "queued", "prepared", "running", "attention" }.Contains(job.State)) jobActions.Children.Add(Ui.AsyncButton("Vérifier la progression", () => Check(job), Error));
            if (job.State == "queued")
            {
                jobActions.Children.Add(Ui.AsyncButton("Démarrer le suivi", EnsureWorker, Error));
                jobActions.Children.Add(Ui.AsyncButton("Annuler cette mission", async () => { await context.Read(accounts => new CloudJobs(context.Store).CancelQueued(job.Id)); await Refresh(); }, Error));
            }
            foreach (var button in jobActions.Children.OfType<Button>()) button.IsEnabled = !acting;
            string text = job.Result ?? "Le résultat apparaîtra après la fin du tour Cloud. Une réponse du modèle reste à vérifier dans GitHub avant intégration.";
            if (result.Text != text) result.Text = text;
            result.Select(Math.Min(selectionStart, result.Text.Length), Math.Min(selectionLength, Math.Max(0, result.Text.Length - selectionStart)));
            references.Text = "Mission : " + job.Id + "\nEnvironnement : " + job.ConfigurationId + "\nConversation : " + (job.ThreadId ?? "Pas encore créée") + "\nRetour au chat source : " + ReturnState(job);
            diagnostic.IsExpanded = expanded;
            UpdateLayout(); result.ScrollToVerticalOffset(inner); detailScroll.ScrollToVerticalOffset(outer);
        }
        private async Task Check(CloudJob job)
        {
            if (acting || closed) return;
            acting = true; ShowSelected();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    if (context.Fixture) { if (AdvanceFixture == null) throw new InvalidOperationException("Aucun appel Cloud réel depuis cette recette."); await AdvanceFixture(job.Id, timeout.Token); }
                    else await new CloudJobs(context.Store).Advance(job.Id, timeout.Token);
                }
                await Refresh();
            }
            finally { acting = false; if (!closed) ShowSelected(); }
        }
        private Task EnsureWorker()
        {
            if (context.Fixture) { if (EnsureWorkerFixture == null) throw new InvalidOperationException("Aucun suivi Cloud réel depuis cette recette."); EnsureWorkerFixture(); }
            else CloudJobWorker.Ensure(context.Store);
            error.Text = ""; notice.Text = "Suivi démarré. La mission enregistrée conserve son identifiant ; aucun nouvel envoi n’a été créé.";
            return Task.CompletedTask;
        }
        private async Task<CloudConfiguration[]> LoadEnvironments(CloudMissionAccountChoice choice, CancellationToken token)
        {
            var target = await context.Read(accounts =>
            {
                var instance = NamedInstances.Resolve(accounts, choice.Id);
                if (instance.AccountKey != choice.AccountKey) throw new InvalidOperationException("Le compte associé à cette instance a changé. Fermez cet assistant et rechargez les comptes avant une nouvelle mission.");
                return new { Accounts = accounts, Instance = instance };
            });
            if (context.Fixture) return EnvironmentsFixture == null ? new CloudConfiguration[0] : await EnvironmentsFixture(choice.Id, token);
            using (var client = CloudInstanceConnection.Open(target.Accounts, target.Instance))
            {
                var found = new Dictionary<string, CloudConfiguration>(StringComparer.Ordinal);
                foreach (var scope in new[] { "user", "workspace" })
                {
                    string cursor = null; var seen = new HashSet<string>(StringComparer.Ordinal);
                    do
                    {
                        var page = await client.List(scope, cursor, token);
                        foreach (var entry in page.Data)
                        {
                            if (found.ContainsKey(entry.Id) || entry.VersionRevision.GetValueOrDefault() <= 1) continue;
                            var full = await client.Get(entry.Id, token);
                            if (full.Status == "ready" && full.Draft == null && !String.IsNullOrEmpty(full.LatestReadyVersionId)) found.Add(full.Id, full);
                        }
                        cursor = page.NextCursor;
                        if (cursor != null && !seen.Add(cursor)) throw new InvalidOperationException("Pagination Cloud incompatible.");
                        if (seen.Count > 50) throw new InvalidOperationException("Catalogue Cloud trop volumineux.");
                    } while (cursor != null);
                }
                return found.Values.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }
        internal Task StartNewMission(string instanceId = null, string configurationId = null) => Compose(instanceId, configurationId);
        private Task Compose() => Compose(null, null);
        private async Task Compose(string instanceId, string configurationId)
        {
            if (composing || closed) return;
            composing = true;
            try
            {
                var instances = await context.Read(accounts => accounts.Data.Instances.Where(instance => !instance.Archived && !String.IsNullOrEmpty(instance.AccountKey)).Select(instance => new CloudMissionAccountChoice { Id = instance.Id, AccountKey = instance.AccountKey, Label = instance.Name + " · " + accounts.Data.Profiles.Where(profile => profile.Key == instance.AccountKey).Select(profile => profile.Email).FirstOrDefault() }).ToArray());
                if (closed) return;
                if (instances.Length == 0) throw new InvalidOperationException("Connectez un compte permanent à une instance avant une mission Cloud.");
                string workerError = null;
                var form = new CloudMissionWizard(this, instances, LoadEnvironments, async (spec, token) =>
                {
                    await context.Read(accounts =>
                    {
                        var current = accounts.Data.Instances.SingleOrDefault(value => value.Id == spec.Instance && !value.Archived);
                        if (current == null || current.AccountKey != spec.ExpectedAccountKey) throw new InvalidOperationException("Le compte associé à cette instance a changé. Fermez cet assistant et rechargez les comptes avant une nouvelle mission.");
                        return true;
                    });
                    if (context.Fixture) { if (SubmitFixture == null) throw new InvalidOperationException("Aucune mission réelle depuis cette recette."); await SubmitFixture(spec, token); }
                    else await new CloudJobs(context.Store).Submit(spec, null, token);
                    try { await EnsureWorker(); } catch (Exception exception) { workerError = "Mission enregistrée une seule fois, mais suivi indisponible : " + Program.SafeError(exception) + " Utilisez Démarrer le suivi pour reprendre cette mission."; }
                }, lifetime.Token, instanceId, configurationId);
                form.ShowDialog();
                if (form.Saved && !closed) { requestedSelection = form.MissionId; await Refresh(); if (workerError != null) error.Text = workerError; }
            }
            finally { composing = false; }
        }
    }
}
