using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task CloudPreparedQueueTests(DesktopContext context, ShellWindow shell, string root, Action<bool, string> assert)
        {
            CloudQueueSourceLabelTests(root, assert);
            var source = new RelaySession { Channel = "fixture-orchestrator", Thread = "fixture-source-chat", Home = Path.Combine(root, "queue-source"), Account = "fixture-source-account" };
            var choices = new[] { new CloudMissionAccountChoice { Id = "fixture-a", AccountKey = "account-a", Label = "Camille · camille@example.test" }, new CloudMissionAccountChoice { Id = "fixture-b", AccountKey = "account-b", Label = "Alex · alex@example.test" } };
            var team = new CloudQueueTeam { Id = "fixture-team", Name = "Équipe de développement", Repository = "example/app", Source = source, Mandate = "Réaliser les mandats préparés et livrer des PR à relire.", Enabled = true, ExplicitDelegation = true, MaximumParallel = 3, Targets = new List<CloudQueueTarget> { new CloudQueueTarget { InstanceId = choices[0].Id, ExpectedAccountKey = choices[0].AccountKey, ConfigurationId = "environment-a", ConfigurationName = "Application · Tests isolés" }, new CloudQueueTarget { InstanceId = choices[1].Id, ExpectedAccountKey = choices[1].AccountKey, ConfigurationId = "environment-b", ConfigurationName = "Application · Développement" } } };
            CloudPreparedMission Mission(string id, string title, string state, string blocker = null) => new CloudPreparedMission { Id = id, TeamId = team.Id, Title = title, Prompt = "Modifier uniquement le périmètre autorisé, vérifier le résultat et livrer une PR.", State = state, BlockReason = blocker, Repository = team.Repository, Feature = "recherche", Lot = "lot-2-03", Specification = "https://github.com/example/app/issues/12", EligibleInstanceIds = choices.Select(choice => choice.Id).ToList(), Model = "gpt-6.1-sol", ReasoningEffort = "ultra", PermissionProfile = ":danger-full-access", ExplicitDelegation = true, CreatedUtc = "2026-10-17T00:00:00Z" };
            var ready = Mission("prepared-ready", "Améliorer la recherche", "queued"); ready.Priority = 10;
            var done = Mission("prepared-review", "Vérifier le catalogue", "completed", "awaiting-review"); done.ObservedJobId = "job-reviewed"; done.ObservedThreadId = "thread-reviewed"; done.ObservedTurnId = "turn-reviewed";
            var blocked = Mission("prepared-dependent", "Intégrer le résultat du catalogue", "queued", "dependency-not-accepted"); blocked.Dependencies.Add(new CloudQueueDependency { MissionId = done.Id, Mode = "accepted" });
            var running = Mission("prepared-running", "Corriger les filtres", "submitted"); running.Target = team.Targets[0];
            var uncertain = Mission("prepared-uncertain", "Examiner une confirmation perdue", "uncertain", "submission-uncertain"); uncertain.ObservedJobId = "uncertain-job";
            var failed = Mission("prepared-failed", "Examiner un échec terminal", "failed"); failed.ObservedJobId = "failed-job"; failed.ObservedThreadId = "failed-thread"; failed.ObservedTurnId = "failed-turn";
            var other = new CloudQueueTeam { Id = "other-team", Name = "Autre équipe", Repository = "example/other", Source = source, Enabled = true };
            var alert = new CloudQueueIdleAlert { TeamId = team.Id, TeamName = team.Name, EpisodeId = "idle-episode", Active = true, Reason = "dependency-not-accepted", SinceUtc = "2026-10-17T00:05:00Z", RaisedUtc = "2026-10-17T00:10:00Z" };
            var state = new CloudQueueSnapshot { Teams = new List<CloudQueueTeam> { team, other }, Missions = new List<CloudPreparedMission> { ready, done, blocked, running, uncertain, failed }, Alerts = new List<CloudQueueIdleAlert> { alert } };
            int reads = 0, environmentReads = 0, cancellations = 0, acceptances = 0, rejections = 0, pauses = 0, acknowledgements = 0, starts = 0, resumes = 0;
            var resumePending = new TaskCompletionSource<bool>(); string resumedId = null, resumedSource = null;
            context.EnsureCloudQueueWorkerFixture = () => starts++;
            var view = new CloudPreparedQueueView(context, shell)
            {
                SnapshotFixture = () => { reads++; return Task.FromResult(state); }, AccountsFixture = () => Task.FromResult(choices), WorkerFixture = () => new CloudQueueWorkerState { Error = "Attente du moteur compatible ; les missions existantes continuent." },
                EnvironmentLoader = (choice, token) => { environmentReads++; throw new InvalidOperationException("Un affichage passif ne doit pas charger le Cloud."); },
                SourcesFixture = () => Task.FromResult(new[] { new CloudQueueSourceChoice { Label = "Chef · chat de recette", Source = source } }),
                ConfigureFixture = (configured, origin) => Task.CompletedTask, PrepareFixture = (mission, origin) => Task.CompletedTask,
                PauseFixture = (id, paused, origin) => { pauses++; team.Paused = paused; return Task.CompletedTask; },
                CancelFixture = (id, origin) => { cancellations++; state.Missions.Single(mission => mission.Id == id).State = "cancelled"; return Task.CompletedTask; },
                ResumeMissionFixture = async (id, origin) => { resumes++; resumedId = id; resumedSource = origin.Thread; await resumePending.Task; state.Missions.Single(mission => mission.Id == id).ErrorCode = null; },
                AcceptFixture = (mission, origin) => { assert(mission.ObservedJobId == done.ObservedJobId && mission.ObservedThreadId == done.ObservedThreadId && mission.ObservedTurnId == done.ObservedTurnId && origin.Thread == source.Thread, "validation de la file conserve le reçu exact et son orchestrateur"); acceptances++; mission.State = "accepted"; return Task.CompletedTask; },
                RejectFixture = (mission, origin) => { rejections++; mission.State = "rejected"; return Task.CompletedTask; },
                AcknowledgeFixture = (value, origin) => { acknowledgements++; value.AcknowledgedUtc = "2026-10-17T00:11:00Z"; return Task.CompletedTask; },
                ConfirmAcceptanceFixture = () => true, ConfirmRejectionFixture = () => true
            };
            var window = new Window { Owner = shell, Content = view, Width = 1100, Height = 750, Opacity = 0, ShowInTaskbar = false };
            try
            {
                window.Show(); await Until(() => view.Teams.Items.Count == 2); window.UpdateLayout();
                string Count(string caption) => Descendants(view).OfType<TextBlock>().Single(text => AutomationProperties.GetName(text) == "Missions préparées " + caption).Text;
                assert(Count("prêtes") == "1" && Count("en cours") == "1" && Count("bloquées") == "3" && Count("à valider") == "1", "file préparée distingue missions prêtes, exécutées, bloquées et à valider");
                assert(view.Error.Text.Contains("moteur compatible") && view.AlertText.IsVisible && view.AlertText.Text.Contains("validation du chef"), "erreur du moteur et vraie cause de l’alerte durable sont visibles sans appel Cloud");
                view.SelectMission(blocked.Id); var dependency = Descendants(view).OfType<Button>().First(button => button.Content is string text && text.StartsWith(done.Title)); dependency.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); assert(view.SelectedMissionId == done.Id, "lien de dépendance retrouve la mission préalable dans la liste");
                assert(view.AcceptButton.IsVisible && !view.CancelButton.IsVisible && !view.RejectButton.IsVisible, "un travail terminé propose la revue et non une annulation ou un nouvel envoi");
                view.AcceptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => acceptances == 1 && !view.AcceptButton.IsVisible); assert(done.State == "accepted", "validation après revue exige un geste explicite et actualise la mission");
                view.SelectMission(uncertain.Id); assert(!view.AcceptButton.IsVisible && !view.RejectButton.IsVisible && !view.CancelButton.IsVisible, "un envoi incertain ne propose ni libération des fichiers ni validation ni annulation trompeuse");
                view.SelectMission(failed.Id); view.RejectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => rejections == 1); assert(failed.State == "rejected" && acceptances == 1, "exclusion après vérification reste distincte d’une acceptation");
                view.SelectMission(ready.Id); view.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => cancellations == 1); assert(ready.State == "cancelled", "annulation n’affecte que le mandat sélectionné avant son attribution");
                view.PauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => pauses == 1); assert(team.Paused && view.PauseButton.Content.ToString().Contains("Reprendre"), "pause d’équipe change l’action en reprise sans arrêter les missions parties");
                view.PauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => pauses == 2); assert(!team.Paused, "une équipe en pause se reprend depuis la même action");
                view.AlertButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => acknowledgements == 1); assert(view.AlertText.IsVisible && view.AlertText.Text.Contains("Signalement vu") && !view.AlertButton.IsEnabled, "accuser réception laisse l’alerte active visible jusqu’à la récupération réelle");
                view.Teams.SelectedItem = other; assert(view.Missions.Items.Count == 0 && Count("prêtes") == "0", "changer d’équipe actualise liste et indicateurs sans mélange"); view.Teams.SelectedItem = team; view.SelectMission(running.Id);
                foreach (var size in new[] { Tuple.Create(1100.0, 750.0), Tuple.Create(1366.0, 768.0), Tuple.Create(820.0, 650.0) })
                {
                    window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout();
                    var bounds = view.DetailScroll.TransformToAncestor(view).TransformBounds(new Rect(view.DetailScroll.RenderSize));
                    assert(bounds.Bottom <= view.ActualHeight + 1 && bounds.Right <= view.ActualWidth + 1 && view.DetailScroll.ViewportHeight > 150 && view.Missions.ActualHeight > 150, "file préparée utilise l’espace sans scroll parent à " + size.Item1 + "×" + size.Item2);
                    Capture(window, Path.Combine(root, "File-preparee-" + size.Item1 + "x" + size.Item2 + ".png"));
                }
                var claim = Mission("prepared-reconnect", "Revérifier une attribution gelée", "claimed"); claim.ErrorCode = "team-binding-changed"; claim.Target = team.Targets[0]; claim.Request = new CloudJobSpec { Id = claim.Id, Instance = claim.Target.InstanceId, ExpectedAccountKey = claim.Target.ExpectedAccountKey, ConfigurationId = claim.Target.ConfigurationId }; state.Missions.Add(claim);
                bool restricted = true;
                foreach (string waitingState in new[] { "claimed", "uncertain" })
                {
                    claim.State = waitingState; claim.ObservedJobId = null; claim.ErrorCode = "team-binding-changed"; await view.RefreshNow(); view.SelectMission(claim.Id); restricted &= view.ResumeMissionButton.IsVisible;
                    claim.ObservedJobId = "already-observed"; await view.RefreshNow(); restricted &= !view.ResumeMissionButton.IsVisible;
                    claim.ObservedJobId = null; claim.ErrorCode = "submission-uncertain"; await view.RefreshNow(); restricted &= !view.ResumeMissionButton.IsVisible;
                }
                claim.State = "queued"; claim.ErrorCode = "team-binding-changed"; await view.RefreshNow(); restricted &= !view.ResumeMissionButton.IsVisible;
                assert(restricted, "revérifier et reprendre apparaît uniquement pour une attribution claimed ou uncertain sans job observé et bloquée par son identité");
                claim.State = "claimed"; string pinnedTarget = Json.Write(claim.Target), pinnedRequest = Json.Write(claim.Request); await view.RefreshNow(); view.SelectMission(claim.Id); view.ResumeMissionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => resumes == 1);
                assert(!view.ResumeMissionButton.IsEnabled, "revérification d’une mission reste désactivée pendant l’action en cours"); view.ResumeMissionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); assert(resumes == 1, "double clic ne crée pas une deuxième demande de reprise");
                resumePending.SetResult(true); await Until(() => !view.ResumeMissionButton.IsVisible);
                assert(resumedId == claim.Id && resumedSource == source.Thread && Json.Write(claim.Target) == pinnedTarget && Json.Write(claim.Request) == pinnedRequest, "reprise conserve la mission, son orchestrateur, sa cible et sa requête figées sans nouveau choix de compte");
                assert(environmentReads == 0 && starts == 0 && reads > 0, "consultation, filtres et gestes de recette ne pollent ni ne démarrent un Cloud réel");
            }
            finally { window.Close(); }
            await Until(() => !view.TimerRunning); assert(!view.TimerRunning, "fermer la file préparée arrête son actualisation locale");
            await CloudQueueTeamWizardTests(shell, team, choices, source, root, assert);
            await CloudPreparedMissionWizardTests(shell, team, choices, done, root, assert);
        }

        private static void CloudQueueSourceLabelTests(string root, Action<bool, string> assert)
        {
            string home = Path.Combine(root, "queue-source-index"); Directory.CreateDirectory(home); string index = Path.Combine(home, "session_index.jsonl");
            string nativeTitle = "  Piloter ProjetQA — revue et lots  ";
            string entries = Json.Write(new { id = "outside-tail", thread_name = "Ancien titre hors limite" }) + "\n" + new string('x', CloudQueueSourceLabels.MaximumIndexBytes + 10) + "\nnot-json\n" + Json.Write(new { id = "source-chat", thread_name = "Ancien nom" }) + "\n" + Json.Write(new { id = "source-chat", thread_name = nativeTitle }) + "\n" + Json.Write(new { id = "unregistered-chat", thread_name = "Ne doit pas être proposé" }) + "\n";
            File.WriteAllText(index, entries, new System.Text.UTF8Encoding(false)); IDictionary<string, string> titles;
            using (var activeWriter = new FileStream(index, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) titles = CloudQueueSourceLabels.ReadTitles(home);
            assert(titles["source-chat"] == nativeTitle && !titles.ContainsKey("outside-tail"), "titres natifs lus sans verrouiller l’index, dans une borne de 2 Mio et sans altérer le nom exact");
            int reads = 0; var channels = new[] { new RelayChannel { Id = "source-a", Name = "Équipe A", Email = "a@example.test", Home = home, AccountKey = "account-a", AnchorThreadId = "source-chat", Enabled = true }, new RelayChannel { Id = "source-b", Name = "Équipe B", Email = "b@example.test", Home = home + Path.DirectorySeparatorChar, AccountKey = "account-a", AnchorThreadId = "fallback-chat", Enabled = true } };
            var choices = CloudQueueSourceLabels.Choices(channels, new[] { new CloudJob { SourceChannel = "source-a", SourceThread = "previous-job-chat" } }, value => { reads++; return titles; });
            assert(reads == 1 && choices.Length == 3 && choices.Single(choice => choice.Source.Thread == "source-chat").Label.Contains(nativeTitle) && choices.Single(choice => choice.Source.Thread == "fallback-chat").Label.EndsWith("Chat fallback-chat", StringComparison.Ordinal) && !choices.Any(choice => choice.Source.Thread == "unregistered-chat"), "choix de l’orchestrateur conserve les sources autorisées, affiche leur titre et ne relit qu’une fois chaque profil");
            assert(CloudQueueSourceLabels.ReadTitles(Path.Combine(home, "absent")).Count == 0, "index natif absent laisse disponible le libellé de secours sans contenu de conversation");
        }

        private static async Task CloudQueueTeamWizardTests(ShellWindow shell, CloudQueueTeam team, CloudMissionAccountChoice[] choices, RelaySession source, string root, Action<bool, string> assert)
        {
            var slow = new TaskCompletionSource<CloudConfiguration[]>(); int calls = 0, saves = 0; CloudQueueTeam saved = null;
            var sources = new[] { new CloudQueueSourceChoice { Label = "Orchestrateur enregistré · conversation fictive", Source = source } };
            var form = new CloudQueueTeamWizard(shell, choices, sources, (choice, token) => { calls++; return choice.Id == choices[0].Id ? slow.Task : Task.FromResult(new[] { new CloudConfiguration { Id = "environment-b", Name = "Environnement du compte Alex" } }); }, (value, origin) => { saves++; saved = value; return Task.CompletedTask; }, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false, ConfirmDiscard = () => true };
            try
            {
                form.Show(); form.TeamName.Text = "Équipe de recette"; form.Repository.Text = "example/app"; form.Mandate.Text = "Réaliser les mandats autorisés sans fusion automatique."; form.Source.SelectedItem = sources[0]; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => form.Step == 1);
                form.Account.SelectedItem = choices[0]; await Until(() => calls == 1 && form.Loading); form.Account.SelectedItem = choices[1]; await Until(() => calls == 2 && !form.Loading && form.Environment.Items.Count == 1); slow.SetResult(new[] { new CloudConfiguration { Id = "wrong-environment", Name = "Réponse obsolète de Camille" } }); await Task.Yield();
                assert(form.LoadedAccountKey == choices[1].Id + "/" + choices[1].AccountKey && ((CloudConfiguration)form.Environment.Items[0]).Id == "environment-b", "assistant équipe charge les environnements au choix du compte et ignore une réponse ancienne");
                form.Environment.SelectedIndex = 0; form.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); assert(form.Pool.Items.Count == 1, "compte et environnement choisi sont associés au pool par une action explicite"); form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => form.Step == 2); form.Parallel.Text = "3"; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !String.IsNullOrEmpty(form.ErrorText.Text)); assert(saves == 0, "enregistrer une équipe nécessite une autorisation explicite du mandat");
                Capture(form, Path.Combine(root, "Assistant-equipe-Cloud.png")); form.Authorization.IsChecked = true; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => form.Saved);
                assert(saves == 1 && saved.MaximumParallel == 3 && saved.Source.Thread == source.Thread && saved.Targets.Single().InstanceId == choices[1].Id && saved.Targets.Single().ExpectedAccountKey == choices[1].AccountKey && saved.Targets.Single().ConfigurationId == "environment-b" && saved.Targets.Single().ConfigurationName.Contains("Alex"), "équipe enregistrée conserve chat exact, compte, environnement et capacité choisis");
            }
            finally { if (!form.Saved) form.Close(); }
            var late = new TaskCompletionSource<CloudConfiguration[]>(); bool cancelled = false;
            var closing = new CloudQueueTeamWizard(shell, choices, sources, (choice, token) => { token.Register(() => cancelled = true); return late.Task; }, (value, origin) => throw new Exception("Aucun enregistrement attendu"), CancellationToken.None) { Opacity = 0, ShowInTaskbar = false, ConfirmDiscard = () => true };
            closing.Show(); closing.Account.SelectedItem = choices[0]; await Until(() => closing.Loading); closing.Close(); late.SetResult(new[] { new CloudConfiguration { Id = "late", Name = "Réponse après fermeture" } }); await Task.Yield(); assert(cancelled && closing.Environment.Items.Count == 0, "fermer l’assistant annule sa lecture et ignore toute réponse tardive");
        }

        private static async Task CloudPreparedMissionWizardTests(ShellWindow shell, CloudQueueTeam team, CloudMissionAccountChoice[] choices, CloudPreparedMission prerequisite, string root, Action<bool, string> assert)
        {
            int saves = 0; var captured = new List<CloudPreparedMission>();
            var execution = new CloudTaskExecutionOptions { Model = "gpt-6.1-sol", ReasoningEffort = "ultra", PermissionProfile = ":danger-full-access" };
            var form = new CloudPreparedMissionWizard(shell, team, new[] { prerequisite }, choices, execution, mission => { saves++; captured.Add(Json.Read<CloudPreparedMission>(Json.Write(mission))); if (saves == 1) throw new InvalidOperationException("Échec fictif avant confirmation"); return Task.CompletedTask; }, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false, ConfirmDiscard = () => true };
            try
            {
                form.Show(); form.MissionTitle.Text = "Mandat de recette"; form.Mandate.Text = "Réaliser uniquement cette modification fictive et vérifier le résultat."; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => form.Step == 1); form.Feature.Text = "recherche"; form.Lot.Text = "lot-2-04"; form.Specification.Text = "https://github.com/example/app/issues/14"; form.Reserved.Text = "src/recherche"; form.Priority.SelectedIndex = 1; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => form.Step == 2);
                form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !String.IsNullOrEmpty(form.ErrorText.Text)); assert(form.Step == 2 && saves == 0, "préparer un mandat exige de cocher explicitement un compte éligible");
                form.Workers.Items.OfType<CheckBox>().First().IsChecked = true; var dependency = (StackPanel)form.Dependencies.Items[0]; dependency.Children.OfType<CheckBox>().Single().IsChecked = true; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => form.Step == 3); Capture(form, Path.Combine(root, "Assistant-mandat-prepare.png")); form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !String.IsNullOrEmpty(form.ErrorText.Text)); assert(saves == 0, "un mandat complet ne part pas sans son autorisation finale");
                form.Authorization.IsChecked = true; execution.Model = "changed-after-review"; form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => saves == 1 && !form.Saving); assert(!form.Saved && !form.BackButton.IsEnabled && !form.Mandate.IsEnabled, "échec de confirmation conserve une intention immuable et empêche de modifier le mandat"); form.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => form.Saved);
                assert(saves == 2 && Json.Write(captured[0]) == Json.Write(captured[1]) && captured[0].Id == form.MissionId && captured[0].EligibleInstanceIds.SequenceEqual(new[] { choices[0].Id }) && captured[0].Dependencies.Single().Mode == "accepted" && captured[0].Priority == 10 && captured[0].Model == "gpt-6.1-sol" && captured[0].ReasoningEffort == "ultra" && captured[0].PermissionProfile == ":danger-full-access", "reprise réutilise le même ID et les mêmes réglages figés, dépendance validée par défaut et compte explicitement autorisé");
            }
            finally { if (!form.Saved) form.Close(); }
            int explicitSaves = 0; CloudPreparedMission explicitIntent = null;
            var unconfigured = new CloudPreparedMissionWizard(shell, team, new CloudPreparedMission[0], choices, new CloudTaskExecutionOptions(), mission => { explicitSaves++; explicitIntent = mission; return Task.CompletedTask; }, CancellationToken.None) { Opacity = 0, ShowInTaskbar = false, ConfirmDiscard = () => true };
            try
            {
                unconfigured.Show(); unconfigured.MissionTitle.Text = "Mandat avec choix explicites"; unconfigured.Mandate.Text = "Travail fictif sans préférence utilisateur préexistante."; unconfigured.Feature.Text = "fixture"; unconfigured.Lot.Text = "lot-1"; unconfigured.Specification.Text = "Issue fictive"; unconfigured.Reserved.Text = "src/fixture"; unconfigured.Workers.Items.OfType<CheckBox>().First().IsChecked = true; unconfigured.ShowStep(3); unconfigured.Authorization.IsChecked = true;
                unconfigured.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !String.IsNullOrEmpty(unconfigured.ErrorText.Text)); assert(explicitSaves == 0 && unconfigured.Model.Text == "" && unconfigured.Effort.SelectedIndex == -1 && unconfigured.Permission.SelectedIndex == -1, "absence de préférences n’impose aucun modèle ni accès complet et exige les trois choix");
                unconfigured.Model.Text = "custom-account-model"; unconfigured.Effort.SelectedItem = "high"; unconfigured.Permission.SelectedIndex = 0; unconfigured.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => unconfigured.Saved);
                assert(explicitSaves == 1 && explicitIntent.Model == "custom-account-model" && explicitIntent.ReasoningEffort == "high" && explicitIntent.PermissionProfile == ":read-only", "l’utilisateur sans préférences choisit les réglages du mandat sans modifier les réglages globaux");
            }
            finally { if (!unconfigured.Saved) unconfigured.Close(); }
        }
    }
}
