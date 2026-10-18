using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Creezio.Switcher.Desktop
{
    internal sealed partial class CloudJobsDashboard
    {
        private readonly WrapPanel githubShortcuts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock missionAge = Ui.Text("", 12, true);
        private readonly TextBlock trackingRead = Ui.Text("", 13);
        private readonly Dictionary<TextBlock, Tuple<string, string>> relativeLabels = new Dictionary<TextBlock, Tuple<string, string>>();
        private readonly List<Tuple<string, FrameworkElement>> timelineRows = new List<Tuple<string, FrameworkElement>>();
        private DateTimeOffset PresentationNow => ClockFixture?.Invoke() ?? DateTimeOffset.UtcNow;
        internal FrameworkElement TimelineBody => journal;
        internal FrameworkElement GithubShortcuts => githubShortcuts;

        private sealed class TimelineAnchor
        {
            internal string Key;
            internal double Y;
        }

        private void InitializeTimelinePresentation(StackPanel detailHeader)
        {
            summary.Children.Remove(cycleSection);
            missionAge.Margin = new Thickness(0, 2, 0, 0);
            AutomationProperties.SetName(missionAge, "Ancienneté de la mission");
            AutomationProperties.SetName(trackingRead, "État réel du suivi Cloud");
            AutomationProperties.SetName(githubShortcuts, "Liens GitHub de la mission");
            AutomationProperties.SetName(journalScroll, "Chronologie des actions enregistrées");
            detailHeader.Children.Add(missionAge);
            detailHeader.Children.Add(githubShortcuts);
        }

        private void BindRelative(TextBlock label, string stamp, string prefix = "")
        {
            relativeLabels[label] = Tuple.Create(stamp, prefix);
            label.Text = prefix + CloudMissionPresentation.RelativeTime(stamp, PresentationNow);
            label.ToolTip = CloudMissionPresentation.AbsoluteTime(stamp);
        }

        private void RefreshRelativeTimes()
        {
            var now = PresentationNow;
            foreach (var pair in relativeLabels)
            {
                string text = pair.Value.Item2 + CloudMissionPresentation.RelativeTime(pair.Value.Item1, now);
                if (pair.Key.Text != text) pair.Key.Text = text;
            }
            var current=list.SelectedItem as Row;
            if(current!=null){trackingRead.Text=CloudMissionPresentation.TrackingExplanation(current.Job,current.Monitor,now);lifecycle.Text=ReadLifecycle(current.Job,current.Monitor);}
        }

        private string RecencyOf(CloudJob job, CloudJobMonitorSnapshot monitor)
        {
            if(job.State=="uncertain"||CloudMissionPresentation.NeedsCurrentRead(job,monitor,PresentationNow)){
                string known=CloudMissionPresentation.KnownStateUtc(monitor);
                return known==null?"Dernier état connu · date indisponible":"Dernier état connu · "+CloudMissionPresentation.RelativeTime(known,PresentationNow);
            }
            var last = CloudMissionPresentation.Timeline(job, monitor)
                .Select(entry => entry.TimeUtc)
                .Where(stamp => DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                .OrderByDescending(stamp => DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture)).FirstOrDefault();
            return last == null ? "Historique non daté" : "Dernier événement · " + CloudMissionPresentation.RelativeTime(last, PresentationNow);
        }

        private TextBlock GithubLink(CloudMissionGithubLink reference, bool compactLabel = false)
        {
            var text = Ui.Text("", 13);
            text.Margin = new Thickness(0, 2, 16, 5);
            var link = new Hyperlink(new Run((compactLabel ? reference.Label.Split(new[] { " · " }, StringSplitOptions.None)[0] : reference.Label) + " ↗"))
            {
                NavigateUri = new Uri(reference.Url, UriKind.Absolute),
                ToolTip = reference.Url + "\n" + reference.Origin,
                TextDecorations = TextDecorations.Underline
            };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextBrush");
            AutomationProperties.SetName(link, "Ouvrir " + reference.Label + " sur GitHub");
            link.Click += (sender, args) =>
            {
                args.Handled = true;
                try
                {
                    var url = link.NavigateUri;
                    if (url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort || !String.IsNullOrEmpty(url.UserInfo))
                        throw new InvalidOperationException("Destination GitHub invalide.");
                    if (context.Fixture)
                    {
                        if (OpenGithubFixture == null) throw new InvalidOperationException("Aucun navigateur réel depuis cette recette.");
                        OpenGithubFixture(url.AbsoluteUri);
                    }
                    else Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception exception) { Error(exception); }
            };
            text.Inlines.Add(link);
            return text;
        }

        private void RenderTimeline(CloudJob job, CloudJobMonitorSnapshot monitor)
        {
            relativeLabels.Clear();
            BindRelative(missionAge, job.CreatedUtc, "Création · ");
            var links = CloudMissionPresentation.GithubLinks(job, rows.Select(row => row.Job));
            var artifacts = links.Where(link => link.Kind == "issue" || link.Kind == "pull").ToArray();
            githubShortcuts.Children.Clear();
            foreach (var link in artifacts.Take(3)) githubShortcuts.Children.Add(GithubLink(link, true));
            if (artifacts.Length > 3) githubShortcuts.Children.Add(Ui.Text("+ " + (artifacts.Length - 3) + " dans la chronologie", 12, true));
            githubShortcuts.Visibility = artifacts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if(trackingRead.Parent is Border previousTracking)previousTracking.Child=null;
            journal.Children.Clear();
            timelineRows.Clear();
            trackingRead.Text=CloudMissionPresentation.TrackingExplanation(job,monitor,PresentationNow);
            if(!String.IsNullOrEmpty(trackingRead.Text)){var warning=Ui.Card(trackingRead);warning.Padding=new Thickness(12);warning.Margin=new Thickness(0,0,0,12);warning.SetResourceReference(Border.BorderBrushProperty,"AccentBrush");journal.Children.Add(warning);}
            if (job.Kind == "follow-up") journal.Children.Add(cycleSection);
            if (links.Count > 0)
            {
                var section = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                var referencesPanel = new WrapPanel();
                foreach (var link in links.Take(8)) referencesPanel.Children.Add(GithubLink(link));
                section.Children.Add(referencesPanel);
                if (links.Count > 8)
                {
                    var remaining = new WrapPanel();
                    foreach (var link in links.Skip(8)) remaining.Children.Add(GithubLink(link));
                    section.Children.Add(new Expander { Header = "Autres références (" + (links.Count - 8) + ")", Content = remaining });
                }
                var explanation = Ui.Text("Liens mentionnés dans le mandat ou le résultat ; état à vérifier dans GitHub.", 11, true);
                explanation.Margin = new Thickness(0);
                section.Children.Add(explanation);
                journal.Children.Add(new Expander { Header = "Références GitHub (" + links.Count + ")", Content = section, Margin = new Thickness(0, 0, 0, 12) });
            }
            else journal.Children.Add(Ui.Text("Aucune référence GitHub exploitable dans cette mission.", 12, true));

            var entries = CloudMissionPresentation.Timeline(job, monitor)
                .OrderByDescending(entry => DateTimeOffset.TryParse(entry.TimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant) ? instant : DateTimeOffset.MinValue)
                .ToArray();
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var count = Ui.Text(entries.Length + " étapes", 12, true);
            DockPanel.SetDock(count, Dock.Right); heading.Children.Add(count);
            heading.Children.Add(Ui.Text("Suivi enregistré", 16));
            journal.Children.Add(heading);
            for (int index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var track = new Grid();
                var line = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "LineBrush");
                if (index < entries.Length - 1) track.Children.Add(line);
                var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
                dot.SetResourceReference(Shape.FillProperty, index == 0 ? "AccentBrush" : "MutedBrush");
                track.Children.Add(dot); row.Children.Add(track);
                var body = new StackPanel { Margin = new Thickness(8, 0, 0, 20) }; Grid.SetColumn(body, 1); row.Children.Add(body);
                var caption = Ui.Text(entry.Title, 14); caption.FontWeight = FontWeights.SemiBold; caption.Margin = new Thickness(0, 0, 0, 3); body.Children.Add(caption);
                var times = new WrapPanel();
                var relative = Ui.Text("", 12); relative.Margin = new Thickness(0, 0, 12, 3); relative.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
                BindRelative(relative, entry.TimeUtc, entry.Observed && entry.TimeUtc != null ? "Observé · " : "");
                AutomationProperties.SetName(relative, "Ancienneté de l’étape " + entry.Kind);
                times.Children.Add(relative);
                if (entry.TimeUtc != null)
                {
                    var absolute = Ui.Text(CloudMissionPresentation.AbsoluteTime(entry.TimeUtc), 11, true); absolute.Margin = new Thickness(0, 0, 0, 3); times.Children.Add(absolute);
                }
                body.Children.Add(times);
                if (!String.IsNullOrEmpty(entry.Detail) && entry.Detail != entry.Title)
                {
                    var message = Ui.Text(entry.Detail, 12, true); message.Margin = new Thickness(0); body.Children.Add(message);
                }
                journal.Children.Add(row);
                timelineRows.Add(Tuple.Create(String.Join("|", entry.TimeUtc, entry.Kind, entry.Title, entry.Detail), (FrameworkElement)row));
            }
            if (entries.Length == 0) journal.Children.Add(Ui.Text("Aucune étape datée n’a encore été consignée.", 13, true));
            var scope = Ui.Text("Historique du Switcher : jusqu’à 100 transitions conservées, complétées par les dates disponibles. Les commandes internes de l’agent Cloud ne sont pas enregistrées ici.", 11, true);
            scope.Margin = new Thickness(0, 4, 0, 6); journal.Children.Add(scope);
        }

        private TimelineAnchor CaptureTimelineAnchor()
        {
            if (!journalScroll.IsVisible || journalScroll.VerticalOffset < 1) return null;
            foreach (var row in timelineRows)
            {
                double y = row.Item2.TranslatePoint(new Point(0, 0), journalScroll).Y;
                if (y + row.Item2.ActualHeight > 0) return new TimelineAnchor { Key = row.Item1, Y = y };
            }
            return null;
        }

        private void RestoreTimelineAnchor(TimelineAnchor anchor, double offset)
        {
            var row = anchor == null ? null : timelineRows.FirstOrDefault(value => value.Item1 == anchor.Key);
            if (row == null) { journalScroll.ScrollToVerticalOffset(offset); return; }
            double y = row.Item2.TranslatePoint(new Point(0, 0), journalScroll).Y;
            journalScroll.ScrollToVerticalOffset(journalScroll.VerticalOffset + y - anchor.Y);
        }
    }
}
