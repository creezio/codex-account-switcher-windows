using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Documents;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task CloudTimelineAcceptance(DesktopContext context, ShellWindow shell, DesktopInstance owner, string root, Action<bool, string> assert)
        {
            var clock = new DateTimeOffset(2026, 10, 16, 21, 4, 0, TimeSpan.Zero);
            const string issueUrl = "https://github.com/creezio/projetqa-app/issues/37";
            const string pullUrl = "https://github.com/creezio/projetqa-app/pull/57";
            var mission = new CloudJob
            {
                SchemaVersion = 2, Id = "c7e8f90123454abc890123456789abcd", Title = "Issue37 — Garder le bail avant mutation de l’index Meili",
                InstanceId = owner.Id, InstanceName = owner.Name, AccountKey = owner.AccountKey,
                SourceThread = "source-timeline-fixture", SourceAccount = owner.AccountKey,
                ConfigurationId = "environment-timeline-fixture", ThreadId = "cloud-timeline-fixture", TurnId = "turn-timeline-fixture",
                Repository = "creezio/projetqa-app", Branch = "codex/lisibi/bail-indexation/lot-8-03", Lot = "lot-8-03", Feature = "bail-indexation",
                Specification = issueUrl, Prompt = "Mission de recette locale, associée à l’issue #37. Aucun appel fournisseur ni travail réel.",
                Result = "Livraison déclarée : " + pullUrl + "\n" + String.Join("\n", Enumerable.Range(0, 120).Select(index => "Preuve fictive et sélectionnable de la recette chronologie · ligne " + index)),
                State = "completed", ReturnToSource = true, ReturnState = "delivered",
                CreatedUtc = clock.AddMinutes(-45).ToString("o"), UpdatedUtc = clock.AddSeconds(-3).ToString("o")
            };
            var observations = Enumerable.Range(1, 12).Select(index => new CloudJobMonitorEvent
            {
                TimeUtc = clock.AddMinutes(-30 + index * 2).ToString("o"), Kind = "fixture-observation",
                Message = "Chronologie fictive · étape " + index.ToString("00") + " : observation conservée sans appel Cloud ni action sur le dépôt."
            }).ToList();
            observations.Add(new CloudJobMonitorEvent { TimeUtc = clock.AddMinutes(-4).ToString("o"), Kind = "state", Message = "État de la mission : completed." });
            observations.Add(new CloudJobMonitorEvent { TimeUtc = clock.AddMinutes(-3).ToString("o"), Kind = "notification-delivered", Message = "Notification acceptée par le chat orchestrateur. Son traitement peut encore être en cours." });
            var snapshot = new CloudJobMonitorSnapshot
            {
                LastCheckedUtc = clock.AddMinutes(-4).ToString("o"), NotificationState = "delivered", NotificationKind = "result",
                NotificationSentUtc = clock.AddMinutes(-3).ToString("o"), NotificationAttempts = 1
            };
            int reads = 0, cloudActions = 0;
            var opened = new List<string>();
            var window = new CloudJobsWindow(context, shell) { Opacity = 0, ShowInTaskbar = false, Width = 1400, Height = 900 };
            var dashboard = window.Dashboard;
            var layoutRoot = (FrameworkElement)window.Content;
            var originalTransform = layoutRoot.LayoutTransform;
            window.JobsFixture = () => { reads++; return Task.FromResult(new[] { Json.Read<CloudJob>(Json.Write(mission)) }); };
            window.MonitoringFixture = job =>
            {
                var copy = Json.Read<CloudJobMonitorSnapshot>(Json.Write(snapshot));
                copy.Events = Json.Read<CloudJobMonitorEvent[]>(Json.Write(observations));
                return copy;
            };
            dashboard.ClockFixture = () => clock;
            dashboard.OpenGithubFixture = url => opened.Add(url);
            window.AdvanceFixture = (id, token) => { cloudActions++; throw new InvalidOperationException("Aucun appel Cloud depuis la recette de chronologie."); };
            window.SubmitFixture = (spec, token) => { cloudActions++; throw new InvalidOperationException("Aucune mission réelle depuis la recette de chronologie."); };
            Hyperlink[] Links(FrameworkElement element) => Descendants(element).OfType<TextBlock>().SelectMany(text => text.Inlines.OfType<Hyperlink>()).ToArray();
            TextBlock Age() => Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetName(text) == "Ancienneté de la mission");
            UIElement[] TimelineNodes() => ((Panel)dashboard.TimelineBody).Children.Cast<UIElement>().ToArray();
            try
            {
                window.Show();
                await Until(() => window.JobsList.Items.Count == 1 && window.RefreshTimerRunning);
                window.UpdateLayout();
                assert(dashboard.DetailTabs.SelectedIndex == 0 && Object.Equals(((TabItem)dashboard.DetailTabs.Items[0]).Header, "Chronologie") && ReferenceEquals(((TabItem)dashboard.DetailTabs.Items[0]).Content, dashboard.TimelineScroll), "mission s’ouvre directement sur sa chronologie et conserve le résultat dans un onglet distinct");
                assert(Descendants(dashboard.TimelineBody).OfType<TextBlock>().Count(text => text.Text.StartsWith("Chronologie fictive · étape ", StringComparison.Ordinal)) == 12 && Descendants(dashboard.TimelineBody).OfType<TextBlock>().Any(text => text.Text.Contains("100 transitions")), "chronologie montre les observations datées et explique la portée de l’historique conservé");
                var shortcuts = Links(dashboard.GithubShortcuts);
                assert(shortcuts.Any(link => link.NavigateUri.AbsoluteUri == issueUrl) && shortcuts.Any(link => link.NavigateUri.AbsoluteUri == pullUrl), "issue du cahier des charges et PR du résultat sont directement accessibles au-dessus de la chronologie");
                foreach (string expected in new[] { issueUrl, pullUrl }) shortcuts.Single(link => link.NavigateUri.AbsoluteUri == expected).RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                assert(opened.SequenceEqual(new[] { issueUrl, pullUrl }) && cloudActions == 0, "clics issue et PR transmettent uniquement les URL GitHub exactes à la fixture sans navigateur ni appel Cloud");

                var initialNodes = TimelineNodes();
                var initialLinks = Links(dashboard.GithubShortcuts);
                string initialAge = Age().Text;
                dashboard.TimelineScroll.ScrollToVerticalOffset(270); window.UpdateLayout();
                double timelineOffset = dashboard.TimelineScroll.VerticalOffset;
                clock = clock.AddMinutes(7);
                await window.RefreshNow(); window.UpdateLayout();
                assert(Age().Text != initialAge && Age().Text.Contains("52 min") && initialNodes.SequenceEqual(TimelineNodes()) && initialLinks.SequenceEqual(Links(dashboard.GithubShortcuts)), "horloge avance les âges sans changement de mission et sans reconstruire les étapes ni les liens");
                assert(timelineOffset > 0 && Math.Abs(dashboard.TimelineScroll.VerticalOffset - timelineOffset) < 2 && dashboard.DetailTabs.SelectedIndex == 0, "actualisation des âges conserve la position de lecture de la chronologie");

                dashboard.DetailTabs.SelectedIndex = 1; window.UpdateLayout();
                window.ResultText.Select(18, 13); window.ResultText.ScrollToVerticalOffset(110); window.UpdateLayout();
                double resultOffset = window.ResultText.VerticalOffset;
                string selectedMission = window.SelectedJobId;
                clock = clock.AddMinutes(2);
                await window.RefreshNow(); window.UpdateLayout();
                assert(window.SelectedJobId == selectedMission && dashboard.DetailTabs.SelectedIndex == 1 && window.ResultText.SelectionStart == 18 && window.ResultText.SelectionLength == 13 && resultOffset > 0 && Math.Abs(window.ResultText.VerticalOffset - resultOffset) < 2 && initialNodes.SequenceEqual(TimelineNodes()), "mise à jour temporelle en lecture du résultat préserve mission, onglet, texte sélectionné et défilement");

                observations.Add(new CloudJobMonitorEvent { TimeUtc = clock.ToString("o"), Kind = "check-recovered", Message = "Observation récente fictive ajoutée pendant la lecture du résultat." });
                await window.RefreshNow(); window.UpdateLayout();
                assert(dashboard.DetailTabs.SelectedIndex == 1 && window.ResultText.SelectionStart == 18 && window.ResultText.SelectionLength == 13 && Math.Abs(window.ResultText.VerticalOffset - resultOffset) < 2 && Descendants(dashboard.TimelineBody).OfType<TextBlock>().Any(text => text.Text == "Observation récente fictive ajoutée pendant la lecture du résultat."), "nouvelle observation actualise la chronologie sans déplacer la lecture ni la sélection du résultat");

                dashboard.DetailTabs.SelectedIndex = 0; window.UpdateLayout();
                dashboard.TimelineScroll.ScrollToVerticalOffset(340); window.UpdateLayout();
                var anchor = Descendants(dashboard.TimelineBody).OfType<TextBlock>()
                    .Where(text => text.Text.StartsWith("Chronologie fictive · étape ", StringComparison.Ordinal))
                    .Select(text => new { text.Text, Y = text.TranslatePoint(new Point(0, 0), dashboard.TimelineScroll).Y })
                    .Where(item => item.Y >= 0 && item.Y < dashboard.TimelineScroll.ActualHeight - 30)
                    .OrderBy(item => item.Y).First();
                double beforeInsertion = dashboard.TimelineScroll.VerticalOffset;
                clock = clock.AddMinutes(1);
                observations.Add(new CloudJobMonitorEvent { TimeUtc = clock.ToString("o"), Kind = "source-check-error", Message = "Nouvelle étape ajoutée avant la zone actuellement lue, sans modifier la mission historique." });
                await window.RefreshNow(); window.UpdateLayout();
                var retained = Descendants(dashboard.TimelineBody).OfType<TextBlock>().Single(text => text.Text == anchor.Text);
                double retainedY = retained.TranslatePoint(new Point(0, 0), dashboard.TimelineScroll).Y;
                assert(dashboard.DetailTabs.SelectedIndex == 0 && window.SelectedJobId == selectedMission && Math.Abs(retainedY - anchor.Y) < 3 && dashboard.TimelineScroll.VerticalOffset > beforeInsertion, "insertion d’une observation plus récente maintient la même étape à l’écran grâce à l’ancre de lecture");

                // Earlier scroll/anchor checks retain their original viewport.
                // Only this large-layout assertion needs a screen-independent surface.
                layoutRoot.LayoutTransform = new ScaleTransform(0.5, 0.5); window.UpdateLayout();
                await Until(() => layoutRoot.ActualWidth >= 1400 && layoutRoot.ActualHeight >= 900);
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                dashboard.TimelineScroll.ScrollToTop(); window.UpdateLayout();
                assert(window.JobsList.ActualHeight > 350 && dashboard.TimelineScroll.ActualHeight > 300 && !Descendants(dashboard.TimelineScroll).OfType<ScrollViewer>().Any(scroll => !ReferenceEquals(scroll, dashboard.TimelineScroll)), "grand écran donne la place à la chronologie sans empiler des défilements internes");
                Capture(window, Path.Combine(root, "Cloud-timeline-GitHub.png"));

                layoutRoot.LayoutTransform = Transform.Identity; window.Width = 880; window.Height = 580; window.UpdateLayout();
                await Until(() => Math.Abs(window.ActualWidth - 880) < 1 && Math.Abs(window.ActualHeight - 580) < 1);
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                assert(window.JobsList.IsVisible && window.JobsList.ActualWidth > 700, "format compact présente d’abord la liste des missions sur toute la largeur disponible");
                dashboard.OpenSelectedDetails(); window.UpdateLayout();
                Capture(window, Path.Combine(root, "Cloud-timeline-compact.png"));
                assert(!window.JobsList.IsVisible && dashboard.TimelineScroll.IsVisible && dashboard.TimelineScroll.ActualHeight > 80 && dashboard.GithubShortcuts.IsVisible, "fiche compacte garde les liens GitHub et une chronologie lisible dans son propre espace");
                assert(reads >= 4 && cloudActions == 0 && opened.Count == 2, "recette chronologie reste entièrement locale sans soumission, progression Cloud ou lancement de navigateur réel");
            }
            finally { layoutRoot.LayoutTransform = originalTransform; if (window.IsLoaded) window.Close(); }
            assert(!window.RefreshTimerRunning, "fermeture de la recette chronologie arrête son actualisation visuelle");
        }
    }
}
