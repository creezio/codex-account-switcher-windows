using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class JobsPage : CollectionPage
    {
        private int page, request;
        private bool hasNext;
        private readonly ComboBox filter;
        private readonly Button previous, next;
        public JobsPage(DesktopContext c, ShellWindow s) : base(c, s, "Tâches", "Suivez les demandes, leurs résultats et les interventions nécessaires.")
        {
            Command("Nouvelle tâche", async delegate { await JobComposer.Open(Context, Shell, null); await Refresh(); }, true);
            filter = new ComboBox { ItemsSource = new[] { "Toutes", "À traiter", "En cours", "Terminées" }, SelectedIndex = 0, Width = 150, Margin = new Thickness(0, 0, 8, 6) };
            Commands.Children.Add(filter);
            filter.SelectionChanged += async delegate { page = 0; await Refresh(); };
            previous = Command("Précédentes", async delegate { page = Math.Max(0, page - 1); await Refresh(); });
            next = Command("Suivantes", async delegate { if (hasNext) page++; await Refresh(); });
            var worker = new Button { Content = "Contrôle du relais" };
            Commands.Children.Add(worker);
            var menu = new ContextMenu();
            worker.Click += delegate
            {
                menu.PlacementTarget = worker;
                menu.IsOpen = true;
            };
            foreach (string mode in new[] { "Reprendre les départs", "Suspendre les départs", "Terminer puis arrêter", "Arrêter le suivi" })
            {
                string selected = mode;
                var item = new MenuItem { Header = mode };
                menu.Items.Add(item);
                item.Click += async delegate { try { if (selected == "Arrêter le suivi") { if (!Ui.Confirm(Shell, "Arrêter le suivi du relais ? Les tâches déjà actives dans Codex continuent.", "Arrêter le suivi")) return; RelayWorker.Stop(Context.Store); } else { RelayDispatch.Set(Context.Store, selected == "Reprendre les départs" ? "running" : selected == "Suspendre les départs" ? "paused" : "drain"); if (selected != "Suspendre les départs") RelayWorker.Resume(Context.Store); } await Refresh(); } catch (Exception e) { Error(e); } };
            }
        }
        protected override Task SearchChanged()
        {
            page = 0;
            return Refresh();
        }
        public override async Task Refresh()
        {
            if (filter == null)
                return;
            int generation = ++request;
            string query = Search.Text;
            int state = filter.SelectedIndex, index = page;
            var rows = await Context.Read(a => Context.Store.Query(m => RelayForm.Filter(new[] { m }, query, state).Any(), index * 50, 51));
            if (generation != request)
                return;
            hasNext = rows.Count > 50;
            previous.IsEnabled = page > 0;
            next.IsEnabled = hasNext;
            Rows(rows.Take(50).Select(m => new ItemRow { Id = m.Id, Revision = m.UpdatedUtc, Title = m.Title, Summary = m.SourceChannelId + " → " + m.TargetChannelId, State = ProductUx.JobState(m) + " · " + RelayForm.State(m.ReturnState), Value = m }), false);
            Notice.Text = "Page " + (page + 1) + " · " + RelayDispatch.Caption(Context.Store);
        }
        protected override async void ShowSelected()
        {
            Details.Children.Clear();
            var row = List.SelectedItem as ItemRow;
            if (row == null)
                return;
            try
            {
                var m = await Context.Read(a => Context.Store.Message(row.Id));
                if ((List.SelectedItem as ItemRow)?.Id != row.Id)
                    return;
                Details.Children.Clear();
                Details.Children.Add(Ui.Text(m.Title, 23));
                Details.Children.Add(Ui.Text(ProductUx.JobState(m), 15));
                var resolution = new StackPanel();
                resolution.Children.Add(Ui.Text("Prochaine action", 16));
                resolution.Children.Add(Ui.Text(ProductUx.Resolution(m), 14, true));
                Details.Children.Add(Ui.Card(resolution));
                var actions = Ui.Actions(Details);
                if (!String.IsNullOrEmpty(m.TargetThreadId))
                    actions.Children.Add(Ui.AsyncButton("Ouvrir dans Codex", async delegate { await new DesktopRelayTransport().Call(Context.Store.Channel(m.TargetChannelId), "navigate_to_codex_page", new { threadId = m.TargetThreadId }, CancellationToken.None); }, Error, true));
                if (m.State == "completed")
                    actions.Children.Add(Ui.AsyncButton("Continuer l'échange", async delegate { await JobComposer.Open(Context, Shell, m); await Refresh(); }, Error));
                if (m.State == "queued" || m.State == "children")
                    actions.Children.Add(Ui.AsyncButton("Annuler la demande", async delegate { Context.Store.RequestCancel(m.Id); await Refresh(); }, Error));
                if (m.State == "waiting" && !String.IsNullOrEmpty(m.TargetThreadId))
                    actions.Children.Add(Ui.AsyncButton("Arrêter dans Codex…", async delegate { Context.Store.RequestCancel(m.Id); await new DesktopRelayTransport().Call(Context.Store.Channel(m.TargetChannelId), "navigate_to_codex_page", new { threadId = m.TargetThreadId }, CancellationToken.None); Notice.Text = "Utilisez Arrêter dans le chat Codex ouvert."; }, Error));
                if (m.State == "uncertain" || m.ReturnState == "uncertain" || m.State == "attention")
                    actions.Children.Add(Ui.AsyncButton("Vérifier le résultat", async delegate { Context.Store.Recheck(m.Id); RelayWorker.Ensure(Context.Store); await Refresh(); }, Error));
                Details.Children.Add(Ui.Text("Résultat", 18));
                var result = new TextBox { Text = m.Result ?? "Le résultat apparaîtra ici lorsque le destinataire aura répondu.", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                Details.Children.Add(result);
                var requestBody = new StackPanel();
                requestBody.Children.Add(Ui.Text(m.Prompt ?? "", 14));
                Details.Children.Add(new Expander { Header = "Demande initiale", Content = requestBody, Margin = new Thickness(0, 12, 0, 12) });
                var timeline = new StackPanel();
                foreach (var e in m.Events ?? new List<RelayEvent>())
                {
                    timeline.Children.Add(Ui.Text(e.At + " · " + RelayForm.State(e.State), 12, true));
                    timeline.Children.Add(Ui.Text(e.Detail ?? "", 13));
                }
                Details.Children.Add(new Expander { Header = "Progression", Content = timeline, Margin = new Thickness(0, 0, 0, 12) });
                var technical = new StackPanel();
                technical.Children.Add(Ui.Text("Identifiant : " + m.Id + "\nDossier : " + m.Workspace + "\nPermissions : " + (m.ObservedPermission ?? "À vérifier") + "\nRoutage : " + m.RoutingReason, 12, true));
                technical.Children.Add(Ui.AsyncButton("Classer après vérification…", async delegate { if (Ui.Confirm(Shell, "Confirmez-vous avoir vérifié dans Codex ce qui a été exécuté ? Cette action classe la demande sans la renvoyer.", "Classer")) { Context.Store.CloseReviewed(m.Id); await Refresh(); } }, Error));
                Details.Children.Add(new Expander { Header = "Détails techniques", Content = technical });
            }
            catch (Exception e) { Error(e); }
        }
    }
    internal static class JobComposer
    {
        internal static Task Open(DesktopContext context, ShellWindow shell, RelayMessage previous)
        {
            var channels = context.Store.Channels().Where(c => c.Enabled).ToArray();
            if (channels.Length == 0)
                throw new InvalidOperationException("Connectez au moins une instance depuis Agents pour envoyer une tâche.");
            var policy = RelayPolicies.Load(context.Store);
            var d = new EditWindow(shell, previous == null ? "Nouvelle tâche" : "Continuer l'échange");
            var from = Ui.Select("Conversation source", channels.Select(c => c.Id), previous?.SourceChannelId ?? channels[0].Id, d.Fields);
            var to = Ui.Select("Destinataire", new[] { "Automatique selon mes règles" }.Concat(channels.Select(c => c.Id)), previous?.TargetChannelId ?? "Automatique selon mes règles", d.Fields);
            var project = Ui.Select("Projet", new[] { "" }.Concat(policy.Projects.Select(p => p.Id)), previous?.Job?.Project ?? "", d.Fields);
            var title = Ui.Input("Objet", previous?.Title ?? "", d.Fields);
            var prompt = Ui.Input("Travail à effectuer", "", d.Fields, true);
            var access = Ui.Select("Action autorisée", new[] { "Lecture et analyse", "Modifier les fichiers", "Action externe" }, "Lecture et analyse", d.Fields);
            var back = Ui.Check("Renvoyer le résultat dans la conversation source", true, d.Fields);
            d.Fields.Children.Add(Ui.Text("La source est le chat connecté de cet agent. Depuis un autre chat, utilisez le skill du relais. Les permissions du destinataire sont vérifiées avant exécution.", 12, true));
            var options = new StackPanel();
            var kind = Ui.Input("Type de tâche", previous?.Job?.Kind ?? "general", options);
            var caps = Ui.Input("Capacités requises (virgules)", previous?.Job?.Capabilities ?? "", options);
            var resource = Ui.Select("Ressource", new[] { "" }.Concat(policy.Resources.Select(r => r.Id)), previous?.Job?.Resource ?? "", options);
            var revision = Ui.Input("Version prête", "", options);
            var hashes = new Dictionary<string, string>();
            var files = Ui.Text("Aucun fichier joint", 12, true);
            options.Children.Add(files);
            options.Children.Add(Ui.Button("Vérifier des fichiers…", () => { try { var source = context.Store.Channel((string)from.SelectedItem); var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, InitialDirectory = source.Workspace }; if (picker.ShowDialog(d) == true) { hashes = WorkspaceService.Manifest(source.Workspace, picker.FileNames); files.Text = hashes.Count + " fichiers · empreintes vérifiées avant exécution"; } } catch (Exception e) { files.Text = Program.SafeError(e); } }));
            d.Fields.Children.Add(new Expander { Header = "Options avancées", Content = options, Margin = new Thickness(0, 16, 0, 12) });
            var preview = Ui.Text("Vérifiez le destinataire et l'action avant l'envoi.", 13, true);
            d.Fields.Children.Add(preview);
            string id = Guid.NewGuid().ToString("N");
            Func<RelayJobSpec> spec = () => new RelayJobSpec { Id = id, From = (string)from.SelectedItem, To = to.SelectedIndex == 0 ? null : (string)to.SelectedItem, Project = (string)project.SelectedItem, Title = title.Text.Trim(), Prompt = prompt.Text, Kind = kind.Text, Capabilities = caps.Text, Resource = (string)resource.SelectedItem, Access = new[] { "read", "write", "external" }[access.SelectedIndex], ReturnToSource = back.IsChecked == true, ExplicitDelegation = true, ReplyTo = previous?.Id, Revision = revision.Text, Files = hashes };
            d.Fields.Children.Add(Ui.AsyncButton("Vérifier le routage sans envoyer", async delegate { var request = spec(); var choices = await Task.Run(() => new RelayRouter(context.Store).Preview(request)); preview.Text = String.Join("\n", choices.Select(c => c.Channel + " · " + (c.Ready ? "Prêt" : "Indisponible") + " · " + c.Reason)); }, e => preview.Text = Program.SafeError(e)));
            if (previous != null)
            {
                from.IsEnabled = false;
                to.IsEnabled = false;
                access.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "read", "write", "external" }, previous.Job?.Access));
            }
            string initial = Json.Write(spec());
            d.Dirty = () => Json.Write(spec()) != initial;
            d.Save = async delegate { var request = spec(); if (String.IsNullOrWhiteSpace(request.Title) || String.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidOperationException("Renseignez l'objet et le travail à effectuer."); await new RelayRouter(context.Store).Submit(request, previous?.SourceThreadId ?? context.Store.Channel(request.From).AnchorThreadId, CancellationToken.None); RelayWorker.Ensure(context.Store); };
            // This dialog submits a request; give its primary command the actual action name.
            d.Loaded += delegate
            {
                foreach (var button in Ui.Descendants(d).OfType<Button>().Where(b => Object.Equals(b.Content, "Enregistrer")))
                    button.Content = "Envoyer la tâche";
            };
            d.ShowDialog();
            return Task.CompletedTask;
        }
    }
}
