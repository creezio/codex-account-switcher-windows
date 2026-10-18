using System;
using System.Linq;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static void CloudMissionPresentationAcceptance(Action<bool, string> assert)
        {
            var now = DateTimeOffset.Parse("2026-10-16T21:00:00Z");
            assert(CloudMissionPresentation.RelativeTime("2026-10-16T20:59:18Z", now) == "Il y a 42 s", "chronologie calcule les secondes écoulées");
            assert(CloudMissionPresentation.RelativeTime("2026-10-16T20:55:00Z", now) == "Il y a 5 min", "chronologie calcule les minutes écoulées");
            assert(CloudMissionPresentation.RelativeTime("2026-10-16T19:25:00Z", now) == "Il y a 1 h 35 min", "chronologie conserve les minutes pour les actions datant de quelques heures");
            assert(CloudMissionPresentation.RelativeTime("2026-10-14T18:00:00Z", now) == "Il y a 2 jours 3 h", "chronologie calcule les jours écoulés");
            assert(CloudMissionPresentation.RelativeTime("2026-10-17T21:00:00Z", now) == "Dans 1 jour", "date future affichée comme future et jamais comme une action passée");
            assert(CloudMissionPresentation.RelativeTime("2026-10-16T21:00:00.500Z", now) == "Dans moins d’une seconde", "date future proche ne devient pas il y a zéro seconde");
            assert(CloudMissionPresentation.RelativeTime("2026-10-16T22:55:00+02:00", now) == "Il y a 5 min", "calcul chronologique respecte les décalages horaires absolus");
            assert(CloudMissionPresentation.RelativeTime("2026-10-16T20:55:00", now) == "Date indisponible" && CloudMissionPresentation.RelativeTime("invalide", now) == "Date indisponible", "dates ambiguës ou invalides ne reçoivent aucun horaire inventé");
            assert(CloudMissionPresentation.AbsoluteTime("2026-10-16T20:55:00Z").Contains("2026") && CloudMissionPresentation.AbsoluteTime(null) == "Date indisponible", "date complète locale disponible et dates absentes explicites");

            var job = new CloudJob { Id = new string('a', 32), Title = "Issue37 — protection du bail", Repository = "creezio/projetqa-app", Branch = "codex/lisibi/bail-indexation/lot-8-03", CreatedUtc = "2026-10-16T20:00:00Z", UpdatedUtc = "2026-10-16T20:59:00Z", State = "completed", ExecutionSetupStartedUtc = "2026-10-16T20:00:04Z", ExecutionSetupFinishedUtc = "2026-10-16T20:00:05Z", ExecutionState = "verified", ExecutionObservedUtc = "2026-10-16T20:00:06Z", ObservedExecution = new CloudTaskExecutionObservation { Model = "gpt-6.1-sol", ReasoningEffort = "ultra", PermissionProfile = ":danger-full-access", Verified = true } };
            var monitor = new CloudJobMonitorSnapshot { LastCheckedUtc = "2026-10-16T20:30:00Z", NotificationState = "delivered", NotificationSentUtc = "2026-10-16T20:31:00.005Z", Events = new[] {
                new CloudJobMonitorEvent { TimeUtc = "2026-10-16T20:01:00Z", Kind = "state", Message = "État de la mission : running." },
                new CloudJobMonitorEvent { TimeUtc = "2026-10-16T20:30:00Z", Kind = "state", Message = "État de la mission : completed." },
                new CloudJobMonitorEvent { TimeUtc = "2026-10-16T20:31:00Z", Kind = "notification-delivered", Message = "Notification acceptée par le chat orchestrateur. Son traitement peut encore être en cours." } } };
            var timeline = CloudMissionPresentation.Timeline(job, monitor);
            assert(timeline.Any(entry => entry.Kind == "created" && !entry.Observed && entry.TimeUtc.StartsWith("2026-10-16T20:00:00")), "chronologie utilise la date réelle de création");
            assert(timeline.Any(entry => entry.Title == "État observé : Terminée" && entry.Observed && entry.Detail.Contains("antérieure")), "fin Cloud présentée comme observation et non comme heure exacte du travail");
            assert(timeline.Count(entry => entry.Kind == "notification-delivered") == 1 && timeline.All(entry => entry.TimeUtc == null || !entry.TimeUtc.StartsWith("2026-10-16T20:59:00")), "notification dédupliquée malgré les microsecondes d’écart et date de sauvegarde exclue des faux événements");
            var historicalDeliveries = CloudMissionPresentation.Timeline(job, new CloudJobMonitorSnapshot { NotificationSentUtc = "2026-10-16T20:31:00.005Z", Events = new[] { new CloudJobMonitorEvent { TimeUtc = "2026-10-16T20:15:00Z", Kind = "notification-delivered" }, new CloudJobMonitorEvent { TimeUtc = "2026-10-16T20:31:00Z", Kind = "notification-delivered" } } });
            assert(historicalDeliveries.Count(entry => entry.Kind == "notification-delivered") == 2, "rapprochement d’horodatage préserve les autres livraisons historiques");
            assert(timeline.Any(entry => entry.Kind == "execution-observed" && entry.Detail.Contains("Accès complet")) && timeline.Any(entry => entry.Kind == "execution-setup-started"), "chronologie inclut la préparation et les réglages natifs effectivement observés");
            var old = CloudMissionPresentation.Timeline(new CloudJob { CreatedUtc = "2026-10-01T10:00:00Z", State = "completed", UpdatedUtc = "2026-10-16T20:59:00Z" }, new CloudJobMonitorSnapshot { NotificationState = "delivered" });
            assert(old.Any(entry => entry.Kind == "current-state" && entry.TimeUtc == null) && old.Any(entry => entry.Kind == "notification-delivered" && entry.TimeUtc == null), "ancienne mission et notification sans date restent explicitement non datées");
            assert(CloudMissionPresentation.Timeline(null, null).Count == 0 && CloudMissionPresentation.Timeline(new CloudJob { State = "failed" }, null).Any(entry => entry.Title.Contains("Échec")), "chronologie résiste aux anciennes données partielles sans inventer de fin");
            var withInvalid = CloudMissionPresentation.Timeline(new CloudJob { State = "failed" }, new CloudJobMonitorSnapshot { Events = new[] { null, new CloudJobMonitorEvent { Kind = "state", TimeUtc = "incorrect", Message = "État de la mission : failed." } } });
            assert(withInvalid.Any(entry => entry.Title.Contains("Échec") && entry.TimeUtc == null), "événement ancien sans date valide reste lisible en français");
            var unavailable = new CloudJobMonitorSnapshot { LastCheckedUtc = "2026-10-16T20:59:00Z", LastCheckError = "Lecture indisponible", Events = new[] { new CloudJobMonitorEvent { TimeUtc = "2026-10-16T20:21:00Z", Kind = "state", Message = "État de la mission : running." } } };
            var active = new CloudJob { State = "running", UpdatedUtc = "2026-10-16T20:59:59Z" };
            assert(CloudMissionPresentation.KnownStateUtc(unavailable) == "2026-10-16T20:21:00Z" && CloudMissionPresentation.TrackingExplanation(active, unavailable, now).Contains("historique") && !CloudMissionPresentation.HasConfirmedObservation(unavailable), "ancien suivi en erreur utilise seulement la transition historique datée et la distingue d’une lecture certaine");
            var unavailableTimeline = CloudMissionPresentation.Timeline(active, unavailable);
            assert(unavailableTimeline.Single(entry => entry.Kind == "last-check").Observed == false && unavailableTimeline.All(entry => entry.TimeUtc == null || !entry.TimeUtc.StartsWith("2026-10-16T20:59:59")), "tentative échouée et date de sauvegarde ne deviennent pas une preuve de travail actuel");
            var fresh = new CloudJobMonitorSnapshot { LastSuccessfulCheckUtc = now.ToString("o"), LastKnownState = "prepared" };
            assert(CloudMissionPresentation.NeedsCurrentRead(active, fresh, now), "lecture récente d’un autre état ne confirme pas un lancement ultérieur");
            fresh.LastKnownState = "running";
            assert(!CloudMissionPresentation.NeedsCurrentRead(active, fresh, now) && CloudMissionPresentation.NeedsCurrentRead(active, fresh, now.AddSeconds(121)), "état courant exige une lecture native correspondante encore récente");
            assert(CloudMissionPresentation.StateCaption(new CloudJob { State = "uncertain", ThreadId = "idle-without-turn" }, fresh, now) == "Envoi à réconcilier", "conversation existante et réglages récents ne prouvent pas l’envoi du mandat incertain");

            job.Specification = "https://github.com/creezio/projetqa-app/issues/37";
            job.Prompt = "Corriger issue #37, PR #56 puis #999 n’est pas une référence typée.";
            job.Result = "[PR livrée](https://github.com/creezio/projetqa-app/pull/56#discussion). https://github.com/creezio/autre/issues/7";
            var links = CloudMissionPresentation.GithubLinks(job);
            assert(links.Count(link => link.Url == "https://github.com/creezio/projetqa-app/issues/37") == 1 && links.Count(link => link.Url == "https://github.com/creezio/projetqa-app/pull/56") == 1, "liens issue et PR directs dédupliqués entre titre mandat cahier des charges et résultat");
            assert(links.Any(link => link.Url == "https://github.com/creezio/autre/issues/7") && !links.Any(link => link.Url.EndsWith("/999")), "URL explicite d’un autre dépôt respectée et numéro nu ignoré");
            assert(links.Any(link => link.Kind == "branch" && link.Url.EndsWith("/tree/codex/lisibi/bail-indexation/lot-8-03")) && links.Any(link => link.Kind == "repository"), "liens directs du dépôt et de la branche déclarés disponibles");
            assert(links.Single(link => link.Url.EndsWith("/autre/issues/7")).Origin == "Résultat déclaré par l’agent", "provenance déclarative des liens du résultat conservée sans fausse vérification GitHub");
            var hostile = CloudMissionPresentation.GithubLinks(new CloudJob { Repository = "owner/../repo", Branch = "../secret", Prompt = "Issue #4 PR #5 https://github.com.evil.test/a/b/pull/6 https://github.com@evil.test/a/b/pull/7 http://github.com/a/b/pull/8 https://github.com/a/b/pull/9/files https://github.com/a/%62/pull/10 javascript:alert(1) https://github.com/a/b/issues/12suffix https://github.com/a/b/pull/../issues/13 https://github.com/a/b/%2e%2e/c/issues/14 https://github.com:444/a/b/issues/15" });
            assert(hostile.Count == 0, "liens externes trompeurs traversées chemins ports ou références sans dépôt ne deviennent pas des boutons");
            assert(CloudMissionPresentation.GithubLinks(new CloudJob { Repository = "https://github.com/creezio/projetqa-app", Title = "Pull request #56 et Issue37" }).Count(link => link.Kind == "pull" || link.Kind == "issue") == 2, "références explicites contextualisées et dépôt URL GitHub pris en charge");
            assert(!CloudMissionPresentation.GithubLinks(new CloudJob { Repository = "a/b", Branch = "codex/x/../bad" }).Any(link => link.Kind == "branch"), "branche contenant une traversée ne produit pas de lien");
            assert(CloudMissionPresentation.GithubLinks(new CloudJob { Repository = "https://github.com/a/b/../c", Title = "PR #9" }).Count == 0 && CloudMissionPresentation.GithubLinks(new CloudJob { Repository = "https://github.com/a/%62", Title = "PR #9" }).Count == 0, "URL de dépôt validée avant toute normalisation de traversée ou d’échappement");
            assert(CloudMissionPresentation.GithubLinks(new CloudJob { Specification = "https://github.com/a/b/issues/2", Prompt = new string('x', CloudMissionPresentation.MaximumLinkText), Result = "Livré https://github.com/a/b/pull/3" }).Any(link => link.Url == "https://github.com/a/b/pull/3"), "résultat et PR de livraison prioritaires sur un mandat volumineux");
            string many = String.Join(" ", Enumerable.Range(1, 300).Select(index => "https://github.com/a/b/issues/" + index));
            assert(CloudMissionPresentation.GithubLinks(new CloudJob { Repository = "a/b", Prompt = many }).Count == CloudMissionPresentation.MaximumLinks, "projection des liens plafonnée même avec des centaines de références");
            assert(CloudMissionPresentation.GithubLinks(new CloudJob { Specification = "https://github.com/a/b/issues/2", Result = new string('x', CloudMissionPresentation.MaximumLinkText) + " https://github.com/a/b/pull/4" }).Any(link => link.Url.EndsWith("/pull/4")), "fenêtre début et fin conserve la PR au bout d’un résultat volumineux dans le budget partagé");
            assert(CloudMissionPresentation.GithubLinks(new CloudJob { Prompt = new string('x', CloudMissionPresentation.MaximumLinkText / 2) + " https://github.com/a/b/issues/2 " + new string('x', CloudMissionPresentation.MaximumLinkText / 2) }).Count == 0, "analyse des textes plafonnée et milieu hors fenêtre non analysé");
            string partialUrl = "https://github.com/a/b/issues/2suffix";
            string crossing = new string('x', CloudMissionPresentation.MaximumLinkText / 2 - partialUrl.IndexOf("suffix", StringComparison.Ordinal) - 1) + " " + partialUrl + new string('x', CloudMissionPresentation.MaximumLinkText);
            assert(CloudMissionPresentation.GithubLinks(new CloudJob { Result = crossing }).Count == 0, "coupure de fenêtre ne transforme pas un fragment d’URL invalide en lien GitHub");

            Func<string, CloudJob> scoped = id => new CloudJob { Id = id, AccountKey = "account-a", InstanceId = "instance-a", ThreadId = "cloud-chat-a", Branch = "codex/lisibi/bail-indexation/lot-8-03", Repository = "creezio/projetqa-app", SourceAccount = "source-account", SourceThread = "source-chat", SourceChannel = "source-channel", SourceHome = "C:/source" };
            var parent = scoped("parent"); parent.Result = "Livraison : https://github.com/creezio/projetqa-app/pull/56";
            var child = scoped("child"); child.Kind = "follow-up"; child.ParentId = parent.Id; child.Prompt = "Corriger le cas limite dans la même branche.";
            var inherited = CloudMissionPresentation.GithubLinks(child, new[] { parent });
            assert(inherited.Any(link => link.Kind == "pull" && link.Url.EndsWith("/pull/56") && link.Origin.Contains("Mission précédente")), "correction sans référence propre conserve le lien de la PR livrée par sa mission précédente exacte");
            child.Result = "https://github.com/creezio/projetqa-app/pull/56";
            assert(CloudMissionPresentation.GithubLinks(child, new[] { parent }).Count(link => link.Kind == "pull") == 1 && !CloudMissionPresentation.GithubLinks(child, new[] { parent }).Single(link => link.Kind == "pull").Origin.Contains("Mission précédente"), "référence propre prioritaire sur le lien hérité et sans doublon");
            child.Result = null; parent.AccountKey = "other-account";
            assert(!CloudMissionPresentation.GithubLinks(child, new[] { parent }).Any(link => link.Kind == "pull"), "aucun héritage de PR depuis un autre compte");
            parent.AccountKey = child.AccountKey; parent.Branch = "codex/other/feature/lot-1";
            assert(!CloudMissionPresentation.GithubLinks(child, new[] { parent }).Any(link => link.Kind == "pull"), "aucun héritage de PR depuis une autre branche");
            parent.Branch = child.Branch; parent.ThreadId = "other-cloud-chat";
            assert(!CloudMissionPresentation.GithubLinks(child, new[] { parent }).Any(link => link.Kind == "pull"), "aucun héritage de PR depuis une autre conversation Cloud");
            parent.ThreadId = child.ThreadId; parent.Repository = "creezio/other";
            assert(!CloudMissionPresentation.GithubLinks(child, new[] { parent }).Any(link => link.Kind == "pull"), "aucun héritage de PR depuis un autre dépôt");
            parent.Repository = child.Repository; parent.SourceThread = "other-orchestrator";
            assert(!CloudMissionPresentation.GithubLinks(child, new[] { parent }).Any(link => link.Kind == "pull"), "aucun héritage de PR depuis un autre chat orchestrateur");
            parent.SourceThread = child.SourceThread;
            assert(!CloudMissionPresentation.GithubLinks(child, new[] { parent, parent }).Any(link => link.Kind == "pull"), "historique avec ID parent ambigu ne choisit aucune version arbitraire");
            parent.Kind = "follow-up"; parent.ParentId = child.Id;
            assert(CloudMissionPresentation.GithubLinks(child, new[] { parent, child }).Count(link => link.Kind == "pull") == 1, "cycle de filiation interrompu sans dupliquer la PR ni boucler");
            var ancestry = Enumerable.Range(0, 12).Select(index => scoped("ancestor-" + index)).ToArray();
            for (int index = 0; index < ancestry.Length; index++) { ancestry[index].Kind = "follow-up"; ancestry[index].ParentId = index == ancestry.Length - 1 ? null : ancestry[index + 1].Id; ancestry[index].Result = "https://github.com/creezio/projetqa-app/pull/" + (100 + index); }
            child.ParentId = ancestry[0].Id;
            assert(CloudMissionPresentation.GithubLinks(child, ancestry).Count(link => link.Kind == "pull") == CloudMissionPresentation.MaximumAncestorDepth, "parcours des corrections plafonné à huit ancêtres vérifiés");
            child.Result = many;
            assert(CloudMissionPresentation.GithubLinks(child, ancestry).Count == CloudMissionPresentation.MaximumLinks, "liens propres et hérités partagent le même plafond de cent références");
        }
    }
}
