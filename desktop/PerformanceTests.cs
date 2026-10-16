using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task PerformanceAcceptance(DesktopContext context, ShellWindow shell, string target, string root, Action<bool, string> assert)
        {
            var page = (PerformancePage)shell.Pages["Performances"];
            int samples = 0, activities = 0, closed = 0, opened = 0;
            string activity = "busy";
            bool running = true, rich = false;
            long identity = DateTime.UtcNow.AddHours(-2).Ticks;
            ResourceProcess Process(int pid, string name, int parent, long memory) => new ResourceProcess { Pid = pid, Parent = parent, Started = identity, Name = name, Role = ProcessResources.Role(name, null), Resident = memory, PrivateBytes = memory + 1024 * 1024, CpuPercent = 1.5, Association = "Descendant vérifié" };
            context.ResourceSnapshotFixture = () =>
            {
                samples++;
                var main = new ResourceScope { Id = "local", Name = "Mon compte principal", Account = "principal@example.com", Primary = true, State = new InstanceState { Running = true, WindowReady = true, DesktopPid = 100, DesktopStartTicks = identity, Phase = "running" } };
                var other = new ResourceScope { Id = target, Name = "Atelier", Account = "atelier@example.com", Managed = true, State = new InstanceState { Running = running, WindowReady = running, DesktopPid = 200, DesktopStartTicks = identity, Phase = running ? "running" : "stopped" } };
                var children = running ? new[] { Process(200, "ChatGPT.exe", 10, 600L * 1024 * 1024), Process(201, "node.exe", 200, 200L * 1024 * 1024) } : new ResourceProcess[0];
                if (rich && running) children = children.Concat(Enumerable.Range(0, 80).Select(n => Process(202 + n, new[] { "ChatGPT.exe", "codex.exe", "node.exe", "pwsh.exe", "CreezioRelay.exe" }[n % 5], 200, (80 - n) * 1024L * 1024))).ToArray();
                var closedScope = new ResourceScope { Id = "closed", Name = "Relecture", Account = "relecture@example.com", Managed = true, State = new InstanceState { Phase = "stopped" } };
                var studioScope = new ResourceScope { Id = "studio", Name = "Studio", Account = "studio@example.com", Managed = true, State = new InstanceState { Phase = "stopped" } };
                var switcher = new ResourceScope { Id = "switcher", Name = "Account Switcher", Account = "Application de gestion", State = new InstanceState { Running = true, DesktopPid = 300, DesktopStartTicks = identity, Phase = "running" } };
                ResourceGroup Group(ResourceScope scope, ResourceProcess[] rows, double? cpu) => new ResourceGroup { Scope = scope, Processes = rows, Resident = rows.Sum(p => p.Resident ?? 0), PrivateBytes = rows.Sum(p => p.PrivateBytes ?? 0), Cpu = cpu };
                return new ResourceSnapshot { Captured = DateTime.UtcNow, TotalPhysical = 16L * 1024 * 1024 * 1024, AvailablePhysical = 1024L * 1024 * 1024, CollectionMilliseconds = 8, Groups = new[] { Group(main, new[] { Process(100, "ChatGPT.exe", 0, 1024L * 1024 * 1024) }, 3), Group(other, children, running ? (double?)2 : null), Group(closedScope, new ResourceProcess[0], null), Group(studioScope, new ResourceProcess[0], null), Group(switcher, new[] { Process(300, "CodexAccountSwitcher.exe", 0, 128L * 1024 * 1024) }, 0.5) }, Unassigned = new[] { Process(999, "codex.exe", 444, 20L * 1024 * 1024) } };
            };
            context.ResourceActivityFixture = id => { activities++; return Task.FromResult(new ResourceActivity { State = activity, Message = activity == "busy" ? "1 chat actif" : activity == "visible_idle" ? "Chats visibles au repos" : "Activité inconnue", Titles = activity == "busy" ? new[] { "Mission de test" } : new string[0], Checked = DateTime.UtcNow }); };
            var lifecycle = context.InstanceLifecycleFixture;
            var window = context.InstanceWindowFixture;
            context.InstanceLifecycleFixture = (id, restart, expected) => { closed++; assert(id == target && expected.DesktopPid == 200, "tableau de bord ferme uniquement l’identité sélectionnée"); running = false; return Task.FromResult("Instance fermée : Atelier"); };
            context.InstanceWindowFixture = id => { opened++; assert(id == target, "ouvrir cible le compte de l’instance sélectionnée"); running = true; return Task.FromResult("Instance ouverte : Atelier"); };
            ResourceRow Row(string id) => page.CompactInstances.Items.Cast<ResourceRow>().Single(r => r.Id == id);
            async Task Check() { int before = activities; page.CheckAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => activities > before && page.CheckAction.IsEnabled); }
            void Close() => page.CloseAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            void Visible(FrameworkElement element, string name)
            {
                var rect = element.TransformToAncestor(page).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                assert(element.IsVisible && rect.Width > 0 && rect.Height > 0 && rect.Left >= -1 && rect.Top >= -1 && rect.Right <= page.ActualWidth + 1 && rect.Bottom <= page.ActualHeight + 1, name);
            }
            void Viewport(string name)
            {
                shell.UpdateLayout();
                File.AppendAllText(Path.Combine(root, "performance-layout.txt"), name + " · page " + page.ActualWidth + "x" + page.ActualHeight + " · table " + page.Processes.ActualWidth + "x" + page.Processes.ActualHeight + " · widths " + String.Join(",", page.Processes.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.ActualWidth)) + " · scroll " + String.Join(",", Descendants(page.Processes).OfType<ScrollViewer>().Select(s => s.ScrollableWidth + "/" + s.ViewportWidth)) + "\n");
                Visible(page.Metrics, name + " · résumé global visible sans défilement");
                Visible(page.ShowAction, name + " · ouvrir/afficher accessible");
                Visible(page.CheckAction, name + " · contrôle d’activité accessible");
                Visible(page.Processes, name + " · liste contenue dans la fenêtre");
                Visible(page.ActualWidth < 740 ? (FrameworkElement)page.CompactInstances : page.Instances, name + " · sélection d’instance toujours accessible");
                Visible(page.InstanceSearch, name + " · recherche d’instance accessible");
                assert(!Ancestors(page.Processes).OfType<ScrollViewer>().Any(), name + " · aucune zone défilante autour du tableau");
                assert(Descendants(page.Processes).OfType<ScrollViewer>().All(s => s.ScrollableWidth < 1), name + " · aucune colonne hors du champ horizontal");
                assert(page.Processes.ActualHeight >= 64, name + " · plusieurs lignes de processus visibles");
            }
            var rootElement = (FrameworkElement)shell.Content;
            double width = shell.Width, height = shell.Height;
            try
            {
                shell.Navigate("Performances"); await page.RefreshNow(); shell.UpdateLayout();
                await Task.Delay(100); shell.UpdateLayout();
                assert(shell.CurrentPage == "Performances" && page.IsLoaded, "recette connectée à la page affichée dans la fenêtre");
                assert(shell.Navigation.Items.Contains("Performances") && page.Instances.Items.Count == 6, "performances accessible sans réglages avancés, système séparé des instances");
                assert(page.Overview.IsVisible && !page.ProcessPanel.IsVisible, "ouverture sur la vue d’ensemble lisible");
                page.Instances.SelectedItem = Row("local");
                assert(!page.CloseAction.IsEnabled && !page.RestartAction.IsEnabled, "instance principale protégée dans le tableau de bord");
                page.Instances.SelectedItem = Row("unassigned");
                assert(!page.CloseAction.IsEnabled && !page.ShowAction.IsEnabled, "processus non attribués jamais proposés à l’arrêt");
                page.Instances.SelectedItem = Row("switcher");
                assert(!page.CloseAction.IsEnabled && !page.ShowAction.IsEnabled, "supervision du Switcher séparée et protégée");
                page.Instances.SelectedItem = Row(target); await Check();
                assert(page.Processes.Items.Count == 2 && !page.CloseAction.IsEnabled && !page.RestartAction.IsEnabled && Descendants(page).OfType<TextBlock>().Any(t => Convert.ToString(t.ToolTip).Contains("Mission de test")), "activité réelle et titres consultables, fermeture bloquée pendant un chat");
                assert(ReferenceEquals(page.CompactInstances.SelectedItem, page.Instances.SelectedItem), "sélecteurs large et compact synchronisés");
                Capture(shell, Path.Combine(root, "Performances-overview.png"));
                page.ProcessesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); shell.UpdateLayout();
                assert(page.ProcessPanel.IsVisible && !page.Overview.IsVisible, "onglet Processus ouvre uniquement le détail des processus");
                var selected = page.Instances.SelectedItem;
                page.Processes.SelectedIndex = 1;
                var process = page.Processes.SelectedItem;
                await page.RefreshNow();
                assert(ReferenceEquals(selected, page.Instances.SelectedItem) && ReferenceEquals(process, page.Processes.SelectedItem), "actualisation conserve l’instance et le processus sélectionnés");
                assert(Descendants(page).OfType<TextBlock>().Any(t => t.Text.Contains("Parent 200") && t.Text.Contains("engagée")), "détails du processus sélectionné disponibles dans le pied fixe");
                page.Pause.IsChecked = true; int paused = samples; await page.Refresh();
                assert(samples == paused, "pause arrête les mesures automatiques");
                await page.RefreshNow(); assert(samples == paused + 1, "actualisation manuelle disponible pendant la pause");
                page.Interval.SelectedIndex = 2;
                assert(context.Store.ReadRecord<PerformanceSettings>("performance-settings.dpapi").Interval == 30, "fréquence de mesure persistante");
                assert(context.Store.ReadRecord<PerformanceSettings>("performance-settings.dpapi").SelectedInstance == target, "préférence d’instance conservée");

                rich = true; await page.RefreshNow(); shell.UpdateLayout();
                assert(page.Processes.Items.Count == 82 && ((ResourceProcessRow)page.Processes.Items[0]).MemoryBytes == 600L * 1024 * 1024, "tri numérique RAM, beaucoup de processus sans agrandir la page");
                var processScroll = Descendants(page.Processes).OfType<ScrollViewer>().First();
                processScroll.ScrollToVerticalOffset(18); shell.UpdateLayout(); double offset = processScroll.VerticalOffset;
                var selectorPosition = page.Instances.TransformToAncestor(page).Transform(new Point(0, 0));
                await page.RefreshNow(); shell.UpdateLayout();
                assert(offset > 0 && Math.Abs(processScroll.VerticalOffset - offset) < 1 && page.Instances.TransformToAncestor(page).Transform(new Point(0, 0)) == selectorPosition, "mesure conserve le défilement des processus et le sélecteur fixe");
                page.ProcessSearch.Text = "201";
                assert(page.Processes.Items.Count == 1 && ((ResourceProcessRow)page.Processes.Items[0]).Pid == 201, "recherche PID filtre sans changer d’instance");
                page.ProcessSearch.Text = "introuvable";
                assert(page.Processes.Items.Count == 0 && Descendants(page).OfType<TextBlock>().Any(t => t.Text.StartsWith("Aucun résultat")), "filtre vide expliqué dans la liste");
                page.ProcessSearch.Text = ""; page.FamilyButtons["Moteur"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(page.Processes.Items.Count == 16 && page.Processes.Items.Cast<ResourceProcessRow>().All(p => p.Family == "Moteur"), "famille Moteur sélectionne uniquement le moteur Codex");
                page.FamilyButtons["Commandes"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(page.Processes.Items.Count == 16 && page.Processes.Items.Cast<ResourceProcessRow>().All(p => p.Name == "pwsh.exe"), "famille Commandes distingue les terminaux");
                await page.RefreshNow(); assert(page.Processes.Items.Count == 16 && Equals(page.FamilySelect.SelectedItem, "Commandes"), "filtres conservés pendant une mesure");
                page.FamilyButtons["Tous"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                page.InstanceSearch.Text = "relecture@example.com";
                assert(page.Instances.Items.Count == 1 && Equals(page.ShowAction.Content, "Ouvrir Codex"), "recherche de compte retrouve une instance fermée et son action d’ouverture");
                page.InstanceSearch.Text = ""; page.Instances.SelectedItem = Row(target);
                assert(page.Processes.Items.Count == 82 && Equals(page.ShowAction.Content, "Afficher Codex"), "passage d’instance remet le bon compte et ses processus");
                processScroll = Descendants(page.Processes).OfType<ScrollViewer>().First();
                // Filtering through an empty source can rebuild the template; wait for the new viewer before sending scroll commands.
                await Until(() => processScroll.IsLoaded);
                processScroll.ScrollToTop(); page.Processes.SelectedItem = page.Processes.Items.Cast<ResourceProcessRow>().First(p => p.Pid == 200); shell.UpdateLayout();
                await page.Dispatcher.InvokeAsync(() => page.Processes.ScrollIntoView(page.Processes.SelectedItem), System.Windows.Threading.DispatcherPriority.ContextIdle);
                await Until(() => processScroll.VerticalOffset < 1);
                Viewport("1280 × 860"); Capture(shell, Path.Combine(root, "Performances.png"));
                shell.Theme("Sombre"); shell.UpdateLayout(); Capture(shell, Path.Combine(root, "Performances-dark.png")); shell.Theme("Clair");
                foreach (var size in new[] { new Size(1024, 720), new Size(800, 560), new Size(800, 500), new Size(1440, 900) })
                {
                    shell.Width = size.Width; shell.Height = size.Height;
                    await Task.Delay(100);
                    await shell.Dispatcher.InvokeAsync(() => shell.UpdateLayout());
                    Capture(shell, Path.Combine(root, "Performances-" + size.Width + "x" + size.Height + ".png"));
                    Viewport(size.Width + " × " + size.Height);
                    if (size.Width == 800)
                    {
                        page.CompactInstances.SelectedItem = Row("local");
                        assert(!page.CloseAction.IsEnabled && page.Processes.Items.Cast<ResourceProcessRow>().Single().Pid == 100, "petite fenêtre · changement d’instance immédiat via le sélecteur");
                        page.CompactInstances.SelectedItem = Row(target);
                    }
                }
                shell.Width = 1280; shell.Height = 860;
                foreach (double scale in new[] { 1.25, 1.5 })
                {
                    rootElement.LayoutTransform = new ScaleTransform(scale, scale); shell.UpdateLayout();
                    await Task.Delay(100); shell.UpdateLayout();
                    Viewport("échelle " + scale);
                    Capture(shell, Path.Combine(root, "Performances-scale-" + (int)(scale * 100) + ".png"), scale);
                }
                rootElement.LayoutTransform = Transform.Identity; shell.Width = width; shell.Height = height; shell.UpdateLayout();
                await Task.Delay(100); shell.UpdateLayout();
                page.OverviewTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); shell.UpdateLayout();
                await Task.Delay(50); shell.UpdateLayout();
                assert(Descendants(page).OfType<TextBlock>().Any(t => t.Text == "Les plus gourmands" && t.IsVisible), "vue d’ensemble montre les plus gourmands en grande fenêtre");
                Capture(shell, Path.Combine(root, "Performances-overview-full.png"));
                page.ProcessesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var activityFixture = context.ResourceActivityFixture;
                var delayedActivity = new TaskCompletionSource<ResourceActivity>();
                context.ResourceActivityFixture = id => { activities++; return id == target ? delayedActivity.Task : activityFixture(id); };
                page.CheckAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(!page.CheckAction.IsEnabled, "lecture d’activité en cours sans double requête");
                page.Instances.SelectedItem = Row("local");
                assert(page.CheckAction.IsEnabled && page.Processes.Items.Cast<ResourceProcessRow>().Single().Pid == 100, "une lecture lente ne bloque pas le passage à une autre instance");
                delayedActivity.SetResult(new ResourceActivity { State = "busy", Message = "Atelier attend une réponse", Checked = DateTime.UtcNow });
                await Until(() => Row(target).Activity == "Atelier attend une réponse");
                assert(!Descendants(page).OfType<TextBlock>().Any(t => t.Text == "Atelier attend une réponse"), "réponse tardive rangée dans la bonne instance sans remplacer le détail courant");
                context.ResourceActivityFixture = activityFixture; page.Instances.SelectedItem = Row(target); await Check();

                rich = false; activity = "unknown"; identity += TimeSpan.TicksPerMinute; await page.RefreshNow();
                assert(!page.Processes.Items.Contains(process) && ReferenceEquals(selected, page.Instances.SelectedItem), "PID réutilisé reçoit une nouvelle ligne et conserve l’instance");
                assert(Row(target).Activity == "Activité inconnue", "nouvelle identité sans réemploi de l’activité de l’ancien lancement");
                await Check(); page.ConfirmAction = message => true; Close();
                await Until(() => page.CloseAction.IsEnabled);
                assert(closed == 0 && Descendants(page).OfType<TextBlock>().Any(t => t.Text.Contains("pas vérifiable")), "contrôle complet refuse une activité inconnue sans fermer");
                activity = "visible_idle"; await Check(); page.ConfirmAction = message => false; Close();
                assert(closed == 0, "annuler la fermeture conserve l’instance");
                page.ConfirmAction = message => true; Close();
                await Until(() => closed == 1 && !page.CloseAction.IsEnabled); await page.RefreshNow();
                assert(Row(target).Count == 0 && page.ShowAction.IsEnabled && Equals(page.ShowAction.Content, "Ouvrir Codex"), "fermeture vérifiée actualise compteurs et action de réouverture");
                page.ShowAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => opened == 1 && page.ShowAction.IsEnabled); await page.RefreshNow();
                assert(Row(target).Count == 2 && Equals(page.ShowAction.Content, "Afficher Codex"), "réouvrir l’instance met à jour son statut et ses processus");
                page.InstanceSearch.Text = "aucun-compte"; shell.UpdateLayout();
                assert(page.Instances.Items.Count == 0 && !page.ShowAction.IsEnabled && page.Processes.Items.Count == 0, "aucune instance trouvée sans laisser le détail du compte précédent");
            }
            finally
            {
                rootElement.LayoutTransform = Transform.Identity;
                shell.Width = width; shell.Height = height;
                context.InstanceLifecycleFixture = lifecycle; context.InstanceWindowFixture = window;
                context.ResourceSnapshotFixture = () => new ResourceSnapshot { Captured = DateTime.UtcNow, Groups = new ResourceGroup[0], Unassigned = new ResourceProcess[0] };
                context.ResourceActivityFixture = null;
                page.InstanceSearch.Text = ""; page.ProcessSearch.Text = "";
                page.Pause.IsChecked = false; page.Interval.SelectedIndex = 0;
            }
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> Ancestors(DependencyObject node)
        {
            while ((node = VisualTreeHelper.GetParent(node)) != null) yield return node;
        }
    }
}
