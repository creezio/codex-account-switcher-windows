using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudMissionTimelineEntry
    {
        public string TimeUtc { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public bool Observed { get; set; }
    }

    internal sealed class CloudMissionGithubLink
    {
        public string Url { get; set; }
        public string Label { get; set; }
        public string Kind { get; set; }
        public string Origin { get; set; }
    }

    // A local projection of durable observations, never a new Cloud/GitHub call.
    // In particular UpdatedUtc is a persistence timestamp, not a completion date.
    internal static class CloudMissionPresentation
    {
        internal const int MaximumLinkText = 250000;
        internal const int MaximumLinks = 100;
        internal const int MaximumAncestorDepth = 8;
        private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
        private static readonly Regex ZonedStamp = new Regex(@"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        private static readonly Regex RepositoryPattern = new Regex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant, PatternTimeout);
        private static readonly Regex UrlPattern = new Regex(@"(?<![A-Za-z0-9_:/.-])https://github\.com/[^\s<>""'`]{1,2048}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        private static readonly Regex GithubResourcePattern = new Regex(@"^https://github\.com/(?<owner>[A-Za-z0-9][A-Za-z0-9-]{0,38})/(?<repository>[A-Za-z0-9_.-]{1,100})/(?<kind>issues|pull)/(?<number>[1-9][0-9]{0,9})/?(?:[?#][^\s<>""']*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        private static readonly Regex ShortReferencePattern = new Regex(@"(?<![A-Za-z0-9_])(?<kind>issues?|PR|pull\s+requests?)\s*(?:#\s*)?(?<number>[1-9][0-9]{0,9})(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        private static readonly Regex StatePattern = new Regex(@"^État de la mission\s*:\s*(?<state>[a-z-]+)\.?$", RegexOptions.CultureInvariant, PatternTimeout);

        internal static bool TryInstant(string value, out DateTimeOffset instant)
        {
            instant = default(DateTimeOffset);
            return value != null && value.Length <= 80 && ZonedStamp.IsMatch(value) &&
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
        }

        internal static string RelativeTime(string value, DateTimeOffset now)
        {
            if (!TryInstant(value, out var instant)) return "Date indisponible";
            var delta = now - instant;
            bool future = delta < TimeSpan.Zero;
            double seconds = Math.Abs(delta.TotalSeconds);
            if (seconds < 1) return future ? "Dans moins d’une seconde" : "À l’instant";
            string elapsed;
            if (seconds < 60) elapsed = ((long)Math.Floor(seconds)).ToString(CultureInfo.InvariantCulture) + " s";
            else if (seconds < 3600) elapsed = ((long)Math.Floor(seconds / 60)).ToString(CultureInfo.InvariantCulture) + " min";
            else if (seconds < 86400)
            {
                long hours = (long)Math.Floor(seconds / 3600), minutes = (long)Math.Floor(seconds % 3600 / 60);
                elapsed = hours.ToString(CultureInfo.InvariantCulture) + " h" + (minutes == 0 ? "" : " " + minutes.ToString(CultureInfo.InvariantCulture) + " min");
            }
            else
            {
                long days = (long)Math.Floor(seconds / 86400), hours = (long)Math.Floor(seconds % 86400 / 3600);
                elapsed = days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " jour" : " jours") + (hours == 0 ? "" : " " + hours.ToString(CultureInfo.InvariantCulture) + " h");
            }
            return (future ? "Dans " : "Il y a ") + elapsed;
        }

        internal static string AbsoluteTime(string value)
        {
            return TryInstant(value, out var instant) ? instant.ToLocalTime().ToString("dd/MM/yyyy · HH:mm:ss", CultureInfo.GetCultureInfo("fr-FR")) : "Date indisponible";
        }

        internal static bool HasReadError(CloudJobMonitorSnapshot monitor) => !String.IsNullOrWhiteSpace(monitor?.LastCheckError);
        internal static bool HasConfirmedObservation(CloudJobMonitorSnapshot monitor) => !String.IsNullOrEmpty(monitor?.LastKnownState) && TryInstant(monitor.LastSuccessfulCheckUtc, out _);
        private static CloudJobMonitorEvent LastHistoricalState(CloudJobMonitorSnapshot monitor) => (monitor?.Events ?? new CloudJobMonitorEvent[0]).Where(item => item != null && ReadState(item) != null && TryInstant(item.TimeUtc, out _)).OrderByDescending(item => DateTimeOffset.Parse(item.TimeUtc, CultureInfo.InvariantCulture)).FirstOrDefault();
        internal static string KnownState(CloudJob job, CloudJobMonitorSnapshot monitor) => HasConfirmedObservation(monitor) ? monitor.LastKnownState : ReadStateOrNull(LastHistoricalState(monitor)) ?? job?.State;
        private static string ReadStateOrNull(CloudJobMonitorEvent item) => item == null ? null : ReadState(item);
        internal static string KnownStateUtc(CloudJobMonitorSnapshot monitor) => HasConfirmedObservation(monitor) ? monitor.LastSuccessfulCheckUtc : LastHistoricalState(monitor)?.TimeUtc;
        internal static bool NeedsCurrentRead(CloudJob job, CloudJobMonitorSnapshot monitor, DateTimeOffset now)
        {
            if (HasReadError(monitor)) return true;
            if (job?.State != "running") return false;
            if (!HasConfirmedObservation(monitor) || monitor.LastKnownState != job.State) return true;
            return !TryInstant(monitor.LastSuccessfulCheckUtc, out var success) || now < success || now - success > TimeSpan.FromSeconds(CloudJobMonitoring.IntervalSeconds * 2);
        }
        internal static string StateCaption(CloudJob job, CloudJobMonitorSnapshot monitor, DateTimeOffset now)
        {
            if (job?.State == "uncertain") return "Envoi à réconcilier";
            if (HasReadError(monitor)) return "Erreur de suivi · dernier état connu";
            if (NeedsCurrentRead(job, monitor, now)) return "État à actualiser";
            return StateLabel(job?.State);
        }
        internal static string KnownStateCaption(CloudJob job, CloudJobMonitorSnapshot monitor, DateTimeOffset now)
        {
            string stamp = KnownStateUtc(monitor), prefix = HasConfirmedObservation(monitor) ? "Dernier état confirmé" : "Dernier état consigné (historique)";
            return prefix + " : " + StateLabel(KnownState(job, monitor)) + (stamp == null ? " · date indisponible" : " · " + AbsoluteTime(stamp) + " · " + RelativeTime(stamp, now));
        }
        internal static string TrackingExplanation(CloudJob job, CloudJobMonitorSnapshot monitor, DateTimeOffset now)
        {
            if (job?.State == "uncertain") return "L’envoi du mandat n’est pas confirmé. Il faut rapprocher son message et son tour exact ; aucun nouveau mandat n’est envoyé automatiquement. Un chat ouvert ou au repos ne prouve pas que cette mission travaille.\n" + KnownStateCaption(job, monitor, now);
            if (!NeedsCurrentRead(job, monitor, now)) return "";
            return (HasReadError(monitor) ? "La dernière lecture Cloud a échoué." : "Aucune lecture Cloud assez récente ne confirme l’état actuel.") + "\n" + KnownStateCaption(job, monitor, now) + "\nAucune activité actuelle n’est confirmée. La lecture est retentée toutes les 60 secondes." + (String.IsNullOrEmpty(monitor?.NextCheckUtc) ? "" : "\nProchain essai : " + AbsoluteTime(monitor.NextCheckUtc) + ".");
        }

        internal static IReadOnlyList<CloudMissionTimelineEntry> Timeline(CloudJob job, CloudJobMonitorSnapshot monitor)
        {
            var entries = new List<CloudMissionTimelineEntry>();
            if (job == null) return entries;
            monitor = monitor ?? new CloudJobMonitorSnapshot();
            Action<string, string, string, string, bool> add = (stamp, kind, title, detail, observed) =>
            {
                entries.Add(new CloudMissionTimelineEntry { TimeUtc = TryInstant(stamp, out var time) ? time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null, Kind = kind, Title = title, Detail = detail, Observed = observed });
            };
            add(job.CreatedUtc, "created", job.Kind == "follow-up" ? "Correction confiée" : "Mission confiée", job.Kind == "follow-up" ? "Nouveau cycle dans la même conversation et la même branche." : "Mission enregistrée par le Switcher.", false);
            if (!String.IsNullOrEmpty(job.ExecutionSetupStartedUtc))
                add(job.ExecutionSetupStartedUtc, "execution-setup-started", "Préparation des réglages natifs", "Début de la tentative enregistrée avant l’envoi du mandat.", false);
            if (!String.IsNullOrEmpty(job.ExecutionSetupFinishedUtc))
                add(job.ExecutionSetupFinishedUtc, "execution-setup-finished", "Préparation des réglages terminée", job.ExecutionSetupState == "not-needed" ? "Les réglages natifs correspondaient déjà à la demande." : "Fin de la préparation enregistrée par le Switcher.", false);
            if (job.ObservedExecution != null || !String.IsNullOrEmpty(job.ExecutionObservedUtc))
            {
                var observation = job.ObservedExecution;
                string detail = observation == null ? "Aucun détail natif disponible pour cette observation." :
                    String.Join(" · ", new[] { observation.Model, observation.ReasoningEffort, observation.PermissionProfile == ":danger-full-access" ? "Accès complet" : observation.PermissionProfile }.Where(value => !String.IsNullOrEmpty(value))) +
                    (observation.Verified && job.ExecutionState == "verified" ? " · Réglages vérifiés" : " · Correspondance à vérifier");
                add(job.ExecutionObservedUtc, "execution-observed", "Réglages natifs observés", detail, true);
            }
            bool currentStateRecorded = false;
            foreach (var item in (monitor.Events ?? new CloudJobMonitorEvent[0]).Where(value => value != null).TakeLast(CloudJobMonitoring.MaximumEvents))
            {
                string state = ReadState(item);
                currentStateRecorded |= state != null && state == job.State;
                string title = EventTitle(item.Kind, state);
                string detail = state == null ? item.Message : "État constaté par le suivi du Switcher ; l’action Cloud peut être antérieure à ce contrôle.";
                add(item.TimeUtc, item.Kind ?? "event", title, detail, item.Kind == "state" || item.Kind == "check-error" || item.Kind == "check-recovered" || item.Kind == "source-check-error");
            }
            if (!currentStateRecorded && !String.IsNullOrEmpty(job.State))
                add(null, "current-state", "État enregistré : " + StateLabel(job.State), "Aucun horodatage de cet état n’est disponible dans le journal conservé. Cet enregistrement ne prouve pas l’état actuel ; la date de sauvegarde ne permet pas de le déduire.", false);
            if (HasConfirmedObservation(monitor))
                add(monitor.LastSuccessfulCheckUtc, "last-successful-check", "Dernière lecture Cloud réussie", "Dernier état confirmé : " + StateLabel(monitor.LastKnownState) + ". Cette observation ne prouve pas une activité ultérieure.", true);
            if (!String.IsNullOrEmpty(monitor.LastCheckedUtc))
                add(monitor.LastCheckedUtc, "last-check", "Dernière tentative de lecture Cloud", String.IsNullOrEmpty(monitor.LastCheckError) ? "Début du dernier contrôle enregistré localement ; sa date seule ne confirme pas une lecture réussie." : "La tentative signale une erreur : " + monitor.LastCheckError, false);
            if (!String.IsNullOrEmpty(monitor.NotificationSentUtc) && !(monitor.Events ?? new CloudJobMonitorEvent[0]).Any(item => item != null && item.Kind == "notification-delivered" && SameDeliveryInstant(item.TimeUtc, monitor.NotificationSentUtc)))
                add(monitor.NotificationSentUtc, "notification-delivered", "Notification acceptée par l’orchestrateur", "Codex a accepté le message. Cela ne confirme pas la fin de son traitement par l’orchestrateur.", false);
            else if (monitor.NotificationState == "delivered" && String.IsNullOrEmpty(monitor.NotificationSentUtc) && !(monitor.Events ?? new CloudJobMonitorEvent[0]).Any(item => item != null && item.Kind == "notification-delivered"))
                add(null, "notification-delivered", "Notification acceptée par l’orchestrateur", "L’envoi est enregistré, mais son horodatage n’est pas disponible. Le traitement par l’orchestrateur n’est pas confirmé.", false);
            // Stable order; callers can reverse known dates while retaining undated facts.
            return entries.OrderBy(entry => entry.TimeUtc == null ? 1 : 0).ThenBy(entry => entry.TimeUtc, StringComparer.Ordinal).ToArray();
        }

        // Event() and NotificationSentUtc are stamped separately in one save;
        // retain historical deliveries but do not duplicate this same action.
        private static bool SameDeliveryInstant(string first, string second) => TryInstant(first, out var left) && TryInstant(second, out var right) && Math.Abs((left - right).TotalSeconds) <= 1;
        private static string ReadState(CloudJobMonitorEvent item)
        {
            if (item.Kind != "state" || item.Message == null || item.Message.Length > 200) return null;
            var match = StatePattern.Match(item.Message);
            return match.Success ? match.Groups["state"].Value : null;
        }
        private static string StateLabel(string state)
        {
            switch (state)
            {
                case "reserved": return "Mission réservée";
                case "queued": return "En attente";
                case "creating": return "Création du chat Cloud";
                case "prepared": return "Chat préparé";
                case "sending": return "Envoi du mandat";
                case "running": return "En cours";
                case "attention": return "Intervention requise";
                case "completed": return "Terminée";
                case "failed": return "Échec";
                case "uncertain": return "Envoi à réconcilier";
                case "cancelled": return "Annulée";
                default: return "État à vérifier";
            }
        }
        private static string EventTitle(string kind, string state)
        {
            if (state != null) return "État observé : " + StateLabel(state);
            switch (kind)
            {
                case "state": return "Changement d’état observé";
                case "check-error": return "Contrôle Cloud en échec";
                case "check-recovered": return "Connexion Cloud rétablie";
                case "source-check-error": return "Traitement du retour non vérifiable";
                case "notification-pending": return "Notification en attente";
                case "notification-sending": return "Envoi de la notification";
                case "notification-delivered": return "Notification acceptée par l’orchestrateur";
                case "notification-source-busy": return "Orchestrateur occupé";
                case "notification-source-offline": return "Orchestrateur indisponible";
                case "notification-permissions": return "Permissions de retour à vérifier";
                case "notification-uncertain": return "Envoi de la notification à vérifier";
                case "notification-attention": return "Notification bloquée";
                default: return "Événement enregistré";
            }
        }

        internal static IReadOnlyList<CloudMissionGithubLink> GithubLinks(CloudJob job)
        {
            var links = new List<CloudMissionGithubLink>();
            if (job == null) return links;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, string, string> add = (url, label, kind, origin) =>
            {
                if (links.Count < MaximumLinks && seen.Add(url)) links.Add(new CloudMissionGithubLink { Url = url, Label = label, Kind = kind, Origin = origin });
            };
            string repository = ValidRepository(job.Repository);
            int remaining = MaximumLinkText;
            foreach (var field in new[] { Tuple.Create(job.Specification, "Cahier des charges"), Tuple.Create(job.Result, "Résultat déclaré par l’agent"), Tuple.Create(job.Title, "Titre de la mission"), Tuple.Create(job.Prompt, "Mandat") })
            {
                if (remaining <= 0 || links.Count >= MaximumLinks) break;
                string text = field.Item1 ?? "";
                if (text.Length > remaining) text = BoundedTextWindow(text, remaining);
                remaining -= text.Length;
                try
                {
                    foreach (Match match in UrlPattern.Matches(text))
                    {
                        string candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', ')', ']', '}');
                        // Validate the original string before System.Uri can
                        // normalize escaped characters or ../ path segments.
                        var resource = GithubResourcePattern.Match(candidate);
                        if (!resource.Success) continue;
                        string linkedRepository = resource.Groups["owner"].Value + "/" + resource.Groups["repository"].Value;
                        if (ValidRepository(linkedRepository) == null) continue;
                        string route = resource.Groups["kind"].Value.ToLowerInvariant();
                        string kind = route == "pull" ? "pull" : "issue", number = resource.Groups["number"].Value;
                        add("https://github.com/" + linkedRepository + "/" + route + "/" + number, (kind == "pull" ? "PR #" : "Issue #") + number + " · " + linkedRepository, kind, field.Item2);
                    }
                    if (repository == null) continue;
                    // Short references require an explicit issue/PR prefix and an
                    // identified repository; arbitrary #123 values are not links.
                    foreach (Match match in ShortReferencePattern.Matches(text))
                    {
                        string prefix = match.Groups["kind"].Value;
                        bool pull = prefix.Equals("PR", StringComparison.OrdinalIgnoreCase) || prefix.StartsWith("pull", StringComparison.OrdinalIgnoreCase);
                        string number = match.Groups["number"].Value;
                        add("https://github.com/" + repository + (pull ? "/pull/" : "/issues/") + number, (pull ? "PR #" : "Issue #") + number + " · " + repository, pull ? "pull" : "issue", field.Item2);
                    }
                }
                catch (RegexMatchTimeoutException) { /* Bounded partial projection; never block the dashboard. */ }
            }
            if (repository != null)
            {
                // Reserve only remaining slots: issue/PR evidence has priority.
                if (ValidBranch(job.Branch)) add("https://github.com/" + repository + "/tree/" + String.Join("/", job.Branch.Split('/').Select(Uri.EscapeDataString)), "Branche · " + job.Branch, "branch", "Branche déclarée de la mission");
                add("https://github.com/" + repository, "Dépôt · " + repository, "repository", "Dépôt de la mission");
            }
            return links;
        }

        internal static IReadOnlyList<CloudMissionGithubLink> GithubLinks(CloudJob job, IEnumerable<CloudJob> history)
        {
            var links = GithubLinks(job).ToList();
            if (job == null || job.Kind != "follow-up" || history == null || String.IsNullOrEmpty(job.Id)) return links;
            var seen = new HashSet<string>(links.Select(link => link.Url), StringComparer.OrdinalIgnoreCase);
            // Duplicate IDs are ambiguous: never select one arbitrary version.
            var jobs = history.Where(value => value != null && !String.IsNullOrEmpty(value.Id)).Take(10000)
                .GroupBy(value => value.Id, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { job.Id };
            var current = job;
            for (int depth = 1; depth <= MaximumAncestorDepth && current.Kind == "follow-up" && links.Count < MaximumLinks; depth++)
            {
                if (String.IsNullOrEmpty(current.ParentId) || !visited.Add(current.ParentId) || !jobs.TryGetValue(current.ParentId, out var parent) || !SameMissionScope(current, parent)) break;
                foreach (var link in GithubLinks(parent).Where(value => value.Kind == "issue" || value.Kind == "pull"))
                {
                    if (links.Count >= MaximumLinks) break;
                    if (!seen.Add(link.Url)) continue;
                    links.Add(new CloudMissionGithubLink { Url = link.Url, Label = link.Label, Kind = link.Kind, Origin = link.Origin + " · Mission précédente" + (depth == 1 ? "" : " (niveau " + depth.ToString(CultureInfo.InvariantCulture) + ")") });
                }
                current = parent;
            }
            return links;
        }

        private static bool SameMissionScope(CloudJob child, CloudJob parent)
        {
            string repository = ValidRepository(child.Repository), parentRepository = ValidRepository(parent.Repository);
            if (String.IsNullOrEmpty(child.AccountKey) || String.IsNullOrEmpty(child.InstanceId) || String.IsNullOrEmpty(child.ThreadId) || !ValidBranch(child.Branch) || repository == null || parentRepository == null) return false;
            return child.AccountKey == parent.AccountKey && child.InstanceId == parent.InstanceId && child.ThreadId == parent.ThreadId && child.Branch == parent.Branch && String.Equals(repository, parentRepository, StringComparison.OrdinalIgnoreCase)
                && child.SourceAccount == parent.SourceAccount && child.SourceThread == parent.SourceThread && child.SourceChannel == parent.SourceChannel
                && String.Equals((child.SourceHome ?? "").TrimEnd('\\', '/'), (parent.SourceHome ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string BoundedTextWindow(string text, int maximum)
        {
            if (maximum < 3) return "";
            int headLength = maximum / 2, tailLength = maximum - headLength - 1;
            string head = text.Substring(0, headLength), tail = text.Substring(text.Length - tailLength);
            // Keep both the opening context and the delivery at the end. Drop
            // partial tokens at cut boundaries so truncation cannot turn an
            // invalid URL into a valid link or join two unrelated fragments.
            if (!Char.IsWhiteSpace(text[headLength]) && !Char.IsWhiteSpace(head[head.Length - 1]))
            {
                int boundary = head.Length - 1;
                while (boundary >= 0 && !Char.IsWhiteSpace(head[boundary])) boundary--;
                head = boundary < 0 ? "" : head.Substring(0, boundary + 1);
            }
            int tailStart = text.Length - tailLength;
            if (!Char.IsWhiteSpace(text[tailStart - 1]) && !Char.IsWhiteSpace(tail[0]))
            {
                int boundary = 0;
                while (boundary < tail.Length && !Char.IsWhiteSpace(tail[boundary])) boundary++;
                tail = boundary == tail.Length ? "" : tail.Substring(boundary);
            }
            return head + "\n" + tail;
        }

        private static string ValidRepository(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 160) return null;
            string repository = value.Trim();
            if (repository.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                // Do not use Uri.AbsolutePath here: it normalizes traversal and
                // percent escapes before the repository syntax is checked.
                repository = repository.Substring("https://github.com/".Length).TrimEnd('/');
            }
            if (!RepositoryPattern.IsMatch(repository)) return null;
            string name = repository.Substring(repository.IndexOf('/') + 1);
            return name == "." || name == ".." || name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? null : repository;
        }

        private static bool ValidBranch(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 512 || value.StartsWith("/", StringComparison.Ordinal) || value.EndsWith("/", StringComparison.Ordinal) || value.Contains("..") || value.Contains("@{") || value.Any(character => Char.IsWhiteSpace(character) || Char.IsControl(character) || "~^:?*[\\".IndexOf(character) >= 0)) return false;
            return value.Split('/').All(segment => segment.Length > 0 && segment != "." && !segment.StartsWith(".", StringComparison.Ordinal) && !segment.EndsWith(".", StringComparison.Ordinal) && !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
        }
    }
}
