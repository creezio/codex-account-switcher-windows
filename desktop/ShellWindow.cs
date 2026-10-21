using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal sealed class ShellWindow : Window
    {
        internal readonly ListBox Navigation = new ListBox();
        internal readonly Dictionary<string, PageView> Pages = new Dictionary<string, PageView>();
        private readonly ContentControl content = new ContentControl();
        private readonly DesktopContext context;
        private readonly TextBlock status = Ui.Text("Prêt", 12, true);
        private readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }, usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        private readonly DispatcherTimer watchAlertsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer nativeHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        private readonly DispatcherTimer applicationUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        private bool checkingApplicationUpdate, updateExit, applicationReady;
        private readonly System.Threading.CancellationTokenSource nativeHealthLifetime = new System.Threading.CancellationTokenSource();
        private readonly System.Windows.Forms.NotifyIcon tray;
        private bool navigating, supervising, exit;
        private bool checkingWatchAlerts, lastNotificationWatch;
        private bool checkingNativeHealth, lastNotificationCompatibility, closed;
        private readonly Button compatibilityButton;
        private readonly Button watchAlertsButton;
        private ChatWatchesWindow chatWatches;
        private readonly HashSet<string> refreshing = new HashSet<string>();
        private CheckBox advancedNavigation;
        private string themeMode = "Système";
        private static readonly string[] AdvancedPages={"Agents","Projets","Comptes","Outils partagés"};
        private readonly HashSet<string> notified = new HashSet<string>(), lowNotified = new HashSet<string>();
        internal string CurrentPage
        {
            get; private set;
        }
        public ShellWindow(DesktopContext data,bool startHidden=false,string updateTransaction=null)
        {
            context = data;
            if(startHidden||updateTransaction!=null){ShowInTaskbar=false;Opacity=0;}
            if(updateTransaction!=null)IsEnabled=false;
            Title = "Codex Account Switcher";
            Width = Math.Min(1280, SystemParameters.WorkArea.Width);
            Height = Math.Min(860, SystemParameters.WorkArea.Height);
            MinWidth = 800;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;
            SetResourceReference(ForegroundProperty, "TextBrush");
            SetResourceReference(BackgroundProperty, "CanvasBrush");
            var root = new Grid();
            root.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush");
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            Content = root;
            Navigation.SetResourceReference(Control.BackgroundProperty, "CanvasBrush");
            Navigation.ItemTemplate = NavigationGlyph.Template();
            var side = new DockPanel { Margin = new Thickness(14, 22, 12, 14) };
            root.Children.Add(side);
            var brand = new StackPanel { Margin = new Thickness(12, 0, 0, 28) };
            brand.Children.Add(Ui.Text("Account Switcher", 18));
            brand.Children.Add(Ui.Text("CODEX · WINDOWS", 11, true));
            DockPanel.SetDock(brand, Dock.Top);
            side.Children.Add(brand);
            var footer = new StackPanel { Margin = new Thickness(8, 14, 0, 0) };
            watchAlertsButton = Ui.Button("Surveillances", ShowChatWatches);
            watchAlertsButton.Visibility = Visibility.Collapsed;
            watchAlertsButton.MaxWidth = 180;
            footer.Children.Add(watchAlertsButton);
            compatibilityButton = Ui.Button("Compatibilité Codex", ShowCompatibility);
            compatibilityButton.Visibility = Visibility.Collapsed;
            compatibilityButton.MaxWidth = 180;
            footer.Children.Add(compatibilityButton);
            footer.Children.Add(status);
            advancedNavigation = Ui.Check("Réglages avancés", context.Store.ReadRecord<AppearanceSettings>("appearance.dpapi").Advanced, footer);
            var applicationVersion=System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion??RelayWorker.Version;
            footer.Children.Add(Ui.Text(applicationVersion+" · Instances & Cloud", 11, true));
            DockPanel.SetDock(footer, Dock.Bottom);
            side.Children.Add(footer);
            side.Children.Add(Navigation);
            Grid.SetColumn(content, 1);
            root.Children.Add(content);
            Pages.Add("Accueil", new HomePage(context, this));
            Pages.Add("Comptes", new AccountsPage(context, this));
            Pages.Add("Instances", new InstancesPage(context, this));
            Pages.Add("Accès global", new GlobalAccessPage(context, this));
            Pages.Add("Compétences", new SkillCatalogPage(context, this));
            Pages.Add("Planifications", new ManagementPage(context, this));
            Pages.Add("Performances", new PerformancePage(context, this));
            Pages.Add("Stockage", new StoragePage(context, this));
            Pages.Add("Outils partagés", new ToolSharingPage(context, this));
            Pages.Add("Agents", new ConfigurationPage(context, this, true));
            Pages.Add("Projets", new ConfigurationPage(context, this, false));
            Pages.Add("Tâches", new JobsPage(context, this));
            Pages.Add("Délégations", new CloudDelegationsPage(context, this));
            Pages.Add("Webhooks", new WebhooksPage(context, this));
            Pages.Add("PC distants", new RemotePage(context, this));
            Pages.Add("Assistance", new AssistancePage(context, this));
            Pages.Add("Paramètres", new PreferencesPage(context, this));
            UpdateNavigation();
            advancedNavigation.Click += delegate {
                if(advancedNavigation.IsChecked!=true && AdvancedPages.Contains(CurrentPage) && !Pages[CurrentPage].CanLeave()){advancedNavigation.IsChecked=true;return;}
                string previous=CurrentPage; UpdateNavigation(); Navigate(AdvancedPages.Contains(previous) ? "Instances" : previous??"Accueil");
                var appearance=context.Store.ReadRecord<AppearanceSettings>("appearance.dpapi");appearance.Advanced=advancedNavigation.IsChecked==true;context.Store.WriteRecord("appearance.dpapi",appearance);
            };
            System.Windows.Automation.AutomationProperties.SetName(Navigation, "Navigation principale");
            Navigation.SelectionChanged += async delegate
            {
                if (navigating)
                    return;
                string next = Navigation.SelectedItem as string;
                if (next == null || next == CurrentPage)
                    return;
                if (CurrentPage != null && !Pages[CurrentPage].CanLeave())
                {
                    navigating = true;
                    Navigation.SelectedItem = CurrentPage;
                    navigating = false;
                    return;
                }
                CurrentPage = next;
                content.Content = Pages[next];
                await RefreshCurrent();
            };
            SourceInitialized += delegate
            {
                var source = (HwndSource)PresentationSource.FromVisual(this);
                source.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) => { if ((uint)m == SingleWindow.ActivateMessage) { ShowInTaskbar=true;Opacity=1;Show(); WindowState = WindowState.Normal; Activate(); handled = true; } return IntPtr.Zero; });
            };
            refreshTimer.Tick += async delegate { await RefreshCurrent(); };
            usageTimer.Tick += async delegate { await Supervise(); };
            watchAlertsTimer.Tick += async delegate { await RefreshChatWatchAlerts(); };
            nativeHealthTimer.Tick += async delegate { await RefreshNativeHealth(); };
            applicationUpdateTimer.Tick += async delegate { await CheckApplicationUpdateAutomatically(); };
            if (!context.Fixture)
            {
                tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Codex Account Switcher", Visible = true };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Ouvrir", null, delegate
                {
                    ShowInTaskbar=true;Opacity=1;
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                });
                menu.Items.Add("Quitter le switcher", null, delegate
                {
                    exit = true;
                    Close();
                });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate
                {
                    ShowInTaskbar=true;Opacity=1;
                    Show();
                    Activate();
                };
                tray.BalloonTipClicked += delegate
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                    if (lastNotificationCompatibility) { ShowCompatibility(); return; }
                    if (lastNotificationWatch) { ShowChatWatches(); return; }
                    Navigate("Délégations");
                    ((CloudDelegationsPage)Pages["Délégations"]).Workspace.ShowPrepared();
                };
            }
            Closing += delegate (object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!updateExit && CurrentPage != null && !Pages[CurrentPage].CanLeave())
                {
                    e.Cancel = true;
                    exit = false;
                    return;
                }
                if (!context.Fixture && !exit)
                {
                    e.Cancel = true;
                    Navigate("Accueil");
                    Hide();
                }
            };
            SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += UserAppearanceChanged;
            Closed += delegate
            {
                closed = true;
                nativeHealthLifetime.Cancel();
                context.StopResourceLinks();
                refreshTimer.Stop();
                usageTimer.Stop();
                watchAlertsTimer.Stop();
                nativeHealthTimer.Stop();
                applicationUpdateTimer.Stop();
                SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= UserAppearanceChanged;
                if (tray != null)
                    tray.Dispose();
            };
            Loaded += async delegate {
                if(startHidden){Hide();ShowInTaskbar=true;Opacity=1;}
                Theme(context.Store.ReadRecord<AppearanceSettings>("appearance.dpapi").Theme);
                if(updateTransaction!=null){
                    try{
                        await context.Read(a=>a.Data.Instances.Count);
                        if(!await ApplicationUpdates.ReadyToCommit(context.Store,updateTransaction,nativeHealthLifetime.Token)){exit=true;updateExit=true;Close();return;}
                    }catch(Exception){exit=true;updateExit=true;Close();return;}
                    IsEnabled=true;
                    if(!startHidden){ShowInTaskbar=true;Opacity=1;}
                }
                Navigate(Pages.ContainsKey(context.InitialPage)?context.InitialPage:"Accueil");
                if (context.ShowSurveillancesOnLoad) ShowChatWatches();
                if (!context.Fixture) {
                    refreshTimer.Start();usageTimer.Start();watchAlertsTimer.Start();nativeHealthTimer.Start();applicationUpdateTimer.Start();_ = RefreshNativeHealth(true);
                    try { await RefreshChatWatchAlerts();var initial = await context.Read(a => a.Data.Profiles.Where(p => p.ResetAttempt != null).Select(p => p.ResetAttempt.IdempotencyKey).ToArray());foreach (string id in initial) notified.Add(id);await context.Initialize();await Supervise();}
                    catch (Exception e) { status.Text = Program.SafeError(e); }
                    applicationReady=true;_ = CheckApplicationUpdateAutomatically();
                }
            };
        }
        internal async Task<bool> ApplyApplicationUpdate()
        {
            if(!context.Fixture&&OwnedWindows.Count>0)throw new InvalidOperationException("Fermez les fenêtres de réglages avant de redémarrer Switcher.");
            if(!context.Fixture&&(!applicationReady||supervising||checkingNativeHealth||checkingWatchAlerts))throw new InvalidOperationException("Switcher termine sa synchronisation. Réessayez dans quelques instants.");
            foreach(var page in Pages.Values)if(!page.CanLeave())return false;
            bool apply=await context.ApplicationUpdateApply();
            if(apply&&!context.Fixture){updateExit=true;exit=true;Close();}
            return apply;
        }
        private async Task CheckApplicationUpdateAutomatically()
        {
            if(context.Fixture||closed||checkingApplicationUpdate||!applicationReady)return;
            checkingApplicationUpdate=true;
            try{
                await ApplicationUpdates.Automatic(context.Store,nativeHealthLifetime.Token);
                var update=ApplicationUpdates.Read(context.Store);
                // Hidden Home has no draft or modal editor. A visible window
                // keeps its session and offers the explicit restart button.
                if(!closed&&update.AutoUpdate&&update.CanApply&&!IsVisible&&CurrentPage=="Accueil"&&OwnedWindows.Count==0&&!supervising&&!checkingNativeHealth&&!checkingWatchAlerts){
                    if(await ApplicationUpdates.Apply(context.Store,nativeHealthLifetime.Token,true)){updateExit=true;exit=true;Close();}
                }
            }catch(OperationCanceledException){}catch(Exception){/* The update panel retains the retryable receipt. */}
            finally{checkingApplicationUpdate=false;}
        }
        internal void ShowChatWatches()
        {
            if (chatWatches == null)
            {
                chatWatches = new ChatWatchesWindow(context, this);
                chatWatches.Closed += delegate { chatWatches = null; };
                chatWatches.Show();
            }
            else { if (!chatWatches.IsVisible) chatWatches.Show(); chatWatches.WindowState = WindowState.Normal; chatWatches.Activate(); }
        }
        private void Balloon(int timeout, string title, string message, System.Windows.Forms.ToolTipIcon icon, bool watch = false)
        {
            if (tray == null) return;
            lastNotificationCompatibility = false;
            lastNotificationWatch = watch;
            tray.ShowBalloonTip(timeout, title, message, icon);
        }
        internal void ShowCompatibility() { new CodexCompatibilityWindow(context, this).Show(); }
        // Independent of plugin, quota and Cloud queues: an update check must
        // neither block chat supervision nor wait behind its account gate.
        internal async Task RefreshNativeHealth(bool forceCompatibility = false)
        {
            if (context.Fixture || closed || checkingNativeHealth) return;
            checkingNativeHealth = true;
            try
            {
                try { await context.RestoreChatMonitoring(); }
                catch (Exception e) { if (!closed) { watchAlertsButton.Visibility = Visibility.Visible; watchAlertsButton.Content = "Suivi à vérifier"; watchAlertsButton.ToolTip = Program.SafeError(e); } }
                var report = await Task.Run(() => new CodexCompatibility(context.Store).Refresh(nativeHealthLifetime.Token, forceCompatibility));
                if (closed) return;
                compatibilityButton.Visibility = report.NeedsAttention ? Visibility.Visible : Visibility.Collapsed;
                compatibilityButton.Content = report.NeedsAttention ? "Compatibilité à vérifier" : "Compatibilité Codex";
                compatibilityButton.ToolTip = report.Detail;
                if (report.NeedsAttention && String.IsNullOrEmpty(report.NotifiedUtc) && tray != null && new AccountService(context.Root).Settings.Notifications)
                {
                    Balloon(10000, "Connexion Codex à vérifier", "Un contrôle de compatibilité a échoué. Consultez Paramètres → Compatibilité Codex.", System.Windows.Forms.ToolTipIcon.Warning);
                    lastNotificationCompatibility = true;
                    await Task.Run(() => new CodexCompatibility(context.Store).MarkNotified(report.EpisodeId));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!closed) { compatibilityButton.Visibility = Visibility.Visible; compatibilityButton.ToolTip = Program.SafeError(e); } }
            finally { checkingNativeHealth = false; }
        }
        // Read watch receipts outside DesktopContext's integration/usage gate.
        // A slow account refresh must never prevent an already recorded alert.
        internal async Task RefreshChatWatchAlerts()
        {
            if (checkingWatchAlerts || closed) return;
            checkingWatchAlerts = true;
            try
            {
                var snapshot = await Task.Run(() => {
                    var watches = new ChatWatches(context.Store).List();
                    return new { Watches = watches, Notifications = !context.Fixture && new AccountService(context.Root).Settings.Notifications, Running = !context.Fixture && ChatWatchWorker.Running(context.Store) };
                });
                if (closed) return;
                var alerts = snapshot.Watches.Where(ChatWatchesWindow.NeedsAttention).ToArray();
                bool enabled = snapshot.Watches.Any(w => w.Enabled);
                bool stale = snapshot.Watches.Any(w => ChatWatchesWindow.IsStale(w) && !String.IsNullOrEmpty(w.LastCheckedUtc));
                watchAlertsButton.Visibility = alerts.Length > 0 || enabled ? Visibility.Visible : Visibility.Collapsed;
                string caption = alerts.Length > 0 ? alerts.Length + " alerte(s) · Surveillances" : enabled && !snapshot.Running && !context.Fixture ? "Surveillance arrêtée" : stale ? "Suivi en retard" : "Conversations surveillées";
                watchAlertsButton.Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap };
                System.Windows.Automation.AutomationProperties.SetName(watchAlertsButton, caption);
                if (!snapshot.Notifications || tray == null) return;
                foreach (var watch in alerts.Where(w => String.IsNullOrEmpty(w.NotifiedUtc)))
                {
                    string message = watch.Name + " : " + ChatWatchesWindow.StateLabel(watch.State) + ". " + (watch.Detail ?? "Ouvrez les surveillances pour examiner la cause.");
                    if (message.Length > 240) message = message.Substring(0, 237) + "…";
                    Balloon(10000, "Une conversation nécessite votre attention", message, System.Windows.Forms.ToolTipIcon.Warning, true);
                    await Task.Run(() => new ChatWatches(context.Store).MarkNotified(watch.Id, watch.EpisodeId));
                }
            }
            catch (Exception e)
            {
                watchAlertsButton.Visibility = Visibility.Visible;
                watchAlertsButton.Content = "Suivi indisponible";
                watchAlertsButton.ToolTip = Program.SafeError(e);
            }
            finally { checkingWatchAlerts = false; }
        }
        internal void Navigate(string page)
        {
            if(AdvancedPages.Contains(page)&&advancedNavigation.IsChecked!=true){advancedNavigation.IsChecked=true;UpdateNavigation();}
            Navigation.SelectedItem = page;
        }
        private void UpdateNavigation()
        { Navigation.ItemsSource = new[]{"Accueil","Instances","Accès global","Compétences","Planifications","Délégations","Tâches","Webhooks","Performances","Stockage","Assistance","PC distants","Comptes","Outils partagés","Agents","Projets","Paramètres"}.Where(p=>advancedNavigation.IsChecked==true||!AdvancedPages.Contains(p)).ToArray(); }
        internal void ExitForTest()
        {
            exit = true;
            Close();
        }
        private async Task RefreshCurrent()
        {
            string page = CurrentPage;
            if(page=="Performances"&&!IsVisible)return;
            if (page == null || !refreshing.Add(page))
                return;
            try
            {
                await Pages[page].Refresh();
                if (CurrentPage == page){
                    var pending=await context.Read(a=>new HeadlessApprovals(context.Store).Pending());
                    status.Text = pending.Length>0?pending.Length+" intervention(s) · voir Tâches":"À jour · " + DateTime.Now.ToString("HH:mm");
                    if(!context.Fixture&&tray!=null&&await context.Read(a=>a.Settings.Notifications))foreach(var request in pending)if(notified.Add("headless:"+request.Id))Balloon(6000,"Un agent attend votre réponse","Ouvrez Tâches → Interventions dans Account Switcher.",System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            catch (Exception e) { if (CurrentPage == page) status.Text = Program.SafeError(e); }
            finally { refreshing.Remove(page); }
        }
        private void SystemAppearanceChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "HighContrast")
                Dispatcher.BeginInvoke(new Action(() => Theme(themeMode)));
        }
        private void UserAppearanceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (themeMode == "Système")
                Dispatcher.BeginInvoke(new Action(() => Theme(themeMode)));
        }
        private async Task Supervise()
        {
            if (supervising)
                return;
            supervising = true;
            try
            {
                await context.Supervise();
                var teamAlerts = await context.Read(a => a.Settings.Notifications
                    ? new CloudMissionQueue(context.Store).List().Alerts.Where(alert => alert.Active &&
                        String.IsNullOrEmpty(alert.AcknowledgedUtc) && String.IsNullOrEmpty(alert.NotifiedUtc)).ToArray()
                    : new CloudQueueIdleAlert[0]);
                if (tray != null)
                foreach (var alert in teamAlerts)
                {
                    Balloon(8000, "Équipe Cloud sans activité confirmée", alert.TeamName +
                        " : ouvrez Délégations → File préparée pour voir ce qui bloque.", System.Windows.Forms.ToolTipIcon.Warning);
                    await context.Read(a => { new CloudMissionQueue(context.Store).MarkIdleNotified(alert.TeamId, alert.EpisodeId); return true; });
                }
                var assistance=(AssistancePage)Pages["Assistance"];
                await assistance.FetchRemote();
                if(!context.Fixture&&await context.Read(a=>a.Settings.Notifications))foreach(var entry in assistance.Entries().Where(e=>e.Ticket.State=="pending"))
                    if(notified.Add("assistance:"+(entry.Remote?.Id??"local")+":"+entry.Ticket.Id)&&tray!=null)Balloon(6000,"Demande d'assistance",entry.Ticket.Title,System.Windows.Forms.ToolTipIcon.Info);
                var low = await context.Read(a => a.Settings.Notifications ? a.Data.Profiles.Where(p => p.IsFresh && p.Score.HasValue && p.Score <= 10 && UsageCoordinator.For(context.Store, p.Key).Notifications).Select(p => new { p.Key, p.Label, p.Score }).ToArray() : null);
                if (low != null)
                {
                    lowNotified.RemoveWhere(key => !low.Any(p => p.Key == key));
                    foreach (var p in low)
                    if (lowNotified.Add(p.Key) && tray != null)
                        Balloon(6000, "Limites Codex faibles", p.Label + " : " + ProductUx.Percent(p.Score) + " restants", System.Windows.Forms.ToolTipIcon.Warning);
                }
                var resets = await context.Read(a => a.Settings.Notifications ? a.Data.Profiles.Where(p => p.ResetAttempt != null && p.ResetAttempt.State == "recovered" && UsageCoordinator.For(context.Store, p.Key).Notifications).Select(p => new { Id = p.ResetAttempt.IdempotencyKey, Text = p.Label + " : " + p.ResetMessage }).ToArray() : null);
                if (resets != null)
                foreach (var reset in resets)
                if (notified.Add(reset.Id) && tray != null)
                    Balloon(6000, "Limites Codex réinitialisées", reset.Text, System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception e) { status.Text = Program.SafeError(e); }
            finally { supervising = false; }
        }
        internal void Theme(string mode)
        {
            themeMode = mode;
            if (SystemParameters.HighContrast)
            {
                Application.Current.Resources["CanvasBrush"] = SystemColors.WindowBrush;
                Application.Current.Resources["SurfaceBrush"] = SystemColors.WindowBrush;
                Application.Current.Resources["TextBrush"] = SystemColors.WindowTextBrush;
                Application.Current.Resources["MutedBrush"] = SystemColors.WindowTextBrush;
                Application.Current.Resources["LineBrush"] = SystemColors.WindowTextBrush;
                Application.Current.Resources["AccentBrush"] = SystemColors.HighlightBrush;
                Application.Current.Resources["AccentTextBrush"] = SystemColors.WindowTextBrush;
                Application.Current.Resources["AccentSoftBrush"] = SystemColors.ControlBrush;
                return;
            }
            bool dark = mode == "Sombre";
            if (mode == "Système")
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    dark = Object.Equals(key?.GetValue("AppsUseLightTheme"), 0);
            }
            var colors = dark ? new[] { "#151B20", "#202930", "#EDF3F6", "#AAB8C2", "#3D4B55", "#087F6B", "#203F3B" } : new[] { "#F5F7F9", "#FFFFFF", "#172329", "#5B6872", "#DCE3E8", "#087F6B", "#E2F3EF" };
            var keys = new[] { "CanvasBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "LineBrush", "AccentBrush", "AccentSoftBrush" };
            for (int n = 0; n < keys.Length; n++)
                Application.Current.Resources[keys[n]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[n]));
            Application.Current.Resources["AccentTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#65D9BD" : "#087F6B"));
        }
    }
    internal sealed class HomePage : PageView
    {
        private readonly StackPanel stack = new StackPanel();
        private string fingerprint;
        public HomePage(DesktopContext c, ShellWindow s) : base(c, s, "Votre espace de travail", "Vos comptes, vos instances et les tâches qui demandent votre attention.")
        {
            Command("Créer une instance", async delegate { await InstanceWizard.Open(Context, Shell); await Refresh(); }, true);
            Command("Mes instances", delegate
            {
                Shell.Navigate("Instances");
                return Task.CompletedTask;
            });
            Body.Children.Add(new ScrollViewer { Content = stack });
        }
        public override async Task Refresh()
        {
            var snapshot = await Context.Read(a => new { Accounts = a.Data.Profiles.Count, Instances = a.Data.Instances.Count(i => !i.Archived), Agents = RelayPolicies.Load(Context.Store).Agents.Count, Jobs = Context.Store.ActiveMessages(), Attention = Context.Store.Query(j => RelayForm.Filter(new[] { j }, "", 1).Any(), 0, 8), Total = Context.Store.Count(), Worker = RelayWorker.Running(Context.Store), Dispatch = RelayDispatch.Caption(Context.Store) });
            string stamp = Json.Write(snapshot);
            if (stamp == fingerprint)
                return;
            fingerprint = stamp;
            stack.Children.Clear();
            var guide = new StackPanel();
            guide.Children.Add(Ui.Text("Déléguez avec un nom",22));
            guide.Children.Add(Ui.Text("1. Créez une instance et connectez son compte.\n2. Le plugin et les skills sont préparés automatiquement.\n3. Dans Codex : « Analyse ces fichiers et délègue la relecture à Léa ».\nLe travail apparaît dans l'autre instance et sa réponse revient dans votre chat.",15,true));
            guide.Children.Add(Ui.Text("Une instance entièrement neuve doit avoir reçu un premier message dans Codex. Après une mise à jour du plugin, utilisez un nouveau chat pour charger ses outils.",13,true));
            stack.Children.Add(Ui.Card(guide));
            var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 8) };
            foreach (var item in new[] { new { Count = snapshot.Accounts, Label = "Comptes", Page = "Comptes" }, new { Count = snapshot.Instances, Label = "Instances", Page = "Instances" }, new { Count = snapshot.Jobs.Count, Label = "Tâches actives", Page = "Tâches" } })
            {
                var p = new StackPanel();
                p.Children.Add(Ui.Text(item.Count.ToString(), 30));
                p.Children.Add(Ui.Text(item.Label, 14, true));
                p.Children.Add(Ui.Button("Consulter", () => Shell.Navigate(item.Page)));
                var card = Ui.Card(p);
                card.Margin = new Thickness(0, 0, 12, 12);
                stats.Children.Add(card);
            }
            stack.Children.Add(stats);
            stack.Children.Add(Ui.Text("À votre attention", 20));
            var blocked = snapshot.Attention.ToArray();
            if (blocked.Length == 0)
            {
                var p = new StackPanel();
                p.Children.Add(Ui.Text("Aucune intervention requise", 17));
                p.Children.Add(Ui.Text("Les demandes bloquées et les actions à vérifier apparaîtront ici.", 14, true));
                stack.Children.Add(Ui.Card(p));
            }
            foreach (var job in blocked)
            {
                var p = new StackPanel();
                p.Children.Add(Ui.Text(job.Title, 17));
                p.Children.Add(Ui.Text(ProductUx.JobState(job) + " · " + ProductUx.Resolution(job), 14, true));
                p.Children.Add(Ui.Button("Voir la tâche", () => Shell.Navigate("Tâches")));
                stack.Children.Add(Ui.Card(p));
            }
            var activity = new StackPanel();
            activity.Children.Add(Ui.Text("Collaboration", 19));
            activity.Children.Add(Ui.Text(snapshot.Agents + " agents configurés · " + snapshot.Total + " tâches dans l'historique", 14, true));
            activity.Children.Add(Ui.Text((snapshot.Worker ? "Relais actif" : "Relais arrêté") + " · " + snapshot.Dispatch));
            stack.Children.Add(Ui.Card(activity));
            if (snapshot.Accounts == 0)
            {
                var p = new StackPanel();
                p.Children.Add(Ui.Text("Commencez avec votre compte", 19));
                p.Children.Add(Ui.Text("Importez la session actuelle ou connectez un autre compte. Vous pourrez ensuite créer vos instances.", 14, true));
                p.Children.Add(Ui.Button("Ajouter un compte", () => Shell.Navigate("Comptes"), true));
                stack.Children.Add(Ui.Card(p));
            }
        }
    }
}
