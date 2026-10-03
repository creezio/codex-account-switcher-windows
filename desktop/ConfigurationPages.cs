using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class ConfigurationPage : CollectionPage
    {
        private readonly bool agents;
        public ConfigurationPage(DesktopContext c, ShellWindow s, bool agentPage) : base(c, s, agentPage ? "Agents" : "Projets", agentPage ? "Définissez les rôles et les comptes qui peuvent recevoir vos tâches." : "Regroupez vos dossiers, participants, ressources et règles de délégation.")
        {
            agents = agentPage;
            Command(agents ? "Ajouter un agent" : "Créer un projet", async delegate { var policy = RelayPolicies.Load(Context.Store); object item = agents ? (object)new RelayAgent { Channel = Context.Store.Channels().Select(x => x.Id).FirstOrDefault(x => !policy.Agents.Any(a => a.Channel == x)) } : new RelayProject { Id = "projet-" + Guid.NewGuid().ToString("N").Substring(0, 6), Name = "Nouveau projet" }; await Edit(item, true); }, true);
            if (agents)
            {
                Command("Connecter une instance", () => ConnectionEditor.Open(Context, Shell));
                Command("Installer l'intégration", () => IntegrationEditor.Open(Context, Shell));
            }
            else
            {
                Command("Modèles", async delegate { await AdvancedTools.Template(Context, Shell); await Refresh(); });
                Command("Importer une configuration", Import);
                Command("Exporter", Export);
            }
        }
        public override async Task Refresh()
        {
            var rows = await Context.Read(a => { var p = RelayPolicies.Load(Context.Store); var channels = Context.Store.Channels(); return agents ? p.Agents.Select(agent => { var ch = channels.FirstOrDefault(x => x.Id == agent.Channel); return new ItemRow { Id = agent.Channel, Revision = p.Revision, Title = String.IsNullOrWhiteSpace(agent.Description) ? agent.Channel : agent.Description, Summary = (ch?.Email ?? "Connexion à configurer") + " · " + agent.Capabilities, State = !agent.Enabled ? "Désactivé" : ch == null ? "À connecter" : !ch.Enabled ? "Connexion désactivée" : DesktopRuntime.SameProcess(ch.ServerPid, ch.ServerStartTicks) ? "Connecté" : "À reconnecter", Value = agent }; }).ToArray() : p.Projects.Select(project => new ItemRow { Id = project.Id, Revision = p.Revision, Title = project.Name ?? project.Id, Summary = project.Workspace, State = project.Delegation == "rules" ? "Selon vos règles" : "Délégation explicite", Value = project }).ToArray(); });
            Rows(rows);
        }
        protected override void ShowSelected()
        {
            Details.Children.Clear();
            var row = List.SelectedItem as ItemRow;
            if (row == null)
                return;
            Details.Children.Add(Ui.Text(row.Title, 23));
            Details.Children.Add(Ui.Text(row.Summary, 14, true));
            Details.Children.Add(Ui.Text(row.State, 14));
            var actions = Ui.Actions(Details);
            actions.Children.Add(Ui.AsyncButton("Modifier", () => Edit(row.Value, false), Error, true));
            actions.Children.Add(Ui.AsyncButton("Retirer…", async delegate { if (!Ui.Confirm(Shell, "Retirer cet élément de la configuration ? Les références restantes seront vérifiées avant enregistrement.", "Retirer")) return; await Context.Mutate(a => { var p = RelayPolicies.Load(Context.Store); if (agents) p.Agents.RemoveAll(x => x.Channel == row.Id); else p.Projects.RemoveAll(x => x.Id == row.Id); RelayPolicies.ValidateReferences(p, Context.Store); RelayPolicies.Save(Context.Store, p); return Task.CompletedTask; }); await Refresh(); }, Error));
            if (agents)
            {
                var a = (RelayAgent)row.Value;
                Details.Children.Add(Ui.Text("Rôle et périmètre", 18));
                Details.Children.Add(Ui.Text(a.Instructions ?? "Ajoutez des instructions pour préciser ce rôle.", 14, true));
                Details.Children.Add(Ui.Text("Projets autorisés : " + FriendlyTags(a.Projects), 14));
                Details.Children.Add(Ui.Text("Capacité : " + a.MaxConcurrent + " tâche(s) · réserve : " + a.MinRemaining + " %", 14));
                Details.Children.Add(Ui.Text("Les capacités déclarées ne donnent pas accès aux plugins privés. L'accès reste celui du compte destinataire.", 13, true));
            }
            else
            {
                var p = (RelayProject)row.Value;
                Details.Children.Add(Ui.Text("Participants", 18));
                Details.Children.Add(Ui.Text("Sources : " + FriendlyTags(p.SourceChannels) + "\nDestinataires : " + FriendlyTags(p.TargetChannels), 14, true));
                Details.Children.Add(Ui.Text("Résultats : " + GuidedEditor.Friendly(p.ReturnMode), 14));
                Details.Children.Add(Ui.Text(p.Instructions ?? "", 14, true));
                var policy = RelayPolicies.Load(Context.Store);
                Details.Children.Add(Ui.Text("Ressources du projet", 18));
                foreach (var r in policy.Resources.Where(r => r.Project == p.Id))
                {
                    var item = r;
                    Details.Children.Add(Ui.AsyncButton(r.Id, () => Edit(item, false), Error));
                }
                Details.Children.Add(Ui.AsyncButton("Ajouter une ressource", () => Edit(new RelayResource { Id = "ressource-" + Guid.NewGuid().ToString("N").Substring(0, 6), Project = p.Id, Channels = p.TargetChannels }, true), Error));
                Details.Children.Add(Ui.Text("Règles de délégation", 18));
                foreach (var r in policy.Rules.Where(r => r.Project == p.Id || r.Project == "*"))
                {
                    var item = r;
                    Details.Children.Add(Ui.AsyncButton((r.Enabled ? "Activée · " : "Désactivée · ") + r.Id, () => Edit(item, false), Error));
                }
                Details.Children.Add(Ui.AsyncButton("Ajouter une règle", () => Edit(new RelayRule { Id = "regle-" + Guid.NewGuid().ToString("N").Substring(0, 6), Project = p.Id, Enabled = false }, true), Error));
                Details.Children.Add(Ui.Text("Une nouvelle règle reste désactivée jusqu'à votre choix explicite.", 13, true));
            }
        }
        private static string FriendlyTags(string value)
        {
            return value == "*" ? "Tous" : value ?? "Aucun";
        }
        private async Task Edit(object original, bool added)
        {
            var policy = RelayPolicies.Load(Context.Store);
            string revision = policy.Revision;
            object draft = Json.Read<object>("{}");
            if (original is RelayAgent)
            {
                draft = Json.Read<RelayAgent>(Json.Write(original));
                if (added)
                    policy.Agents.Add((RelayAgent)draft);
                else
                    policy.Agents[policy.Agents.FindIndex(x => x.Channel == ((RelayAgent)original).Channel)] = (RelayAgent)draft;
            }
            else if (original is RelayProject)
            {
                draft = Json.Read<RelayProject>(Json.Write(original));
                if (added)
                    policy.Projects.Add((RelayProject)draft);
                else
                    policy.Projects[policy.Projects.FindIndex(x => x.Id == ((RelayProject)original).Id)] = (RelayProject)draft;
            }
            else if (original is RelayResource)
            {
                draft = Json.Read<RelayResource>(Json.Write(original));
                if (added)
                    policy.Resources.Add((RelayResource)draft);
                else
                    policy.Resources[policy.Resources.FindIndex(x => x.Id == ((RelayResource)original).Id)] = (RelayResource)draft;
            }
            else
            {
                draft = Json.Read<RelayRule>(Json.Write(original));
                if (added)
                    policy.Rules.Add((RelayRule)draft);
                else
                    policy.Rules[policy.Rules.FindIndex(x => x.Id == ((RelayRule)original).Id)] = (RelayRule)draft;
            }
            var dialog = new EditWindow(Shell, (added ? "Ajouter" : "Modifier") + " · " + (draft is RelayAgent ? "Agent" : draft is RelayProject ? "Projet" : draft is RelayResource ? "Ressource" : "Règle"));
            var editor = new PolicyEditor(Context.Store, policy, draft, dialog.Fields, added);
            bool removed = false;
            dialog.Dirty = () => removed || editor.Dirty;
            dialog.Save = () => Context.Mutate(a => { editor.Apply(); if (RelayPolicies.Load(Context.Store).Revision != revision) throw new InvalidOperationException("La configuration a changé ailleurs. Fermez puis rouvrez cet éditeur pour repartir des valeurs actuelles."); RelayPolicies.ValidateReferences(policy, Context.Store); RelayPolicies.Save(Context.Store, policy); return Task.CompletedTask; });
            if (!added && (draft is RelayResource || draft is RelayRule))
            {
                dialog.Fields.Children.Add(Ui.Button("Supprimer cet élément…", () => { if (!Ui.Confirm(dialog, "Supprimer cet élément lors de l'enregistrement ?", "Suppression")) return; if (draft is RelayResource) policy.Resources.Remove((RelayResource)draft); else policy.Rules.Remove((RelayRule)draft); removed = true; dialog.Fields.IsEnabled = false; }));
            }
            dialog.ShowDialog();
            await Refresh();
            ShowSelected();
        }
        private async Task Import()
        {
            string path = Ui.File("Configuration JSON|*.json");
            if (path == null)
                return;
            var next = RelayPolicies.Import(SafeFiles.ReadText(path));
            RelayPolicies.ValidateReferences(next, Context.Store);
            if (!AdvancedTools.Preview(Shell, "Importer la configuration", RelayPolicies.Load(Context.Store), next))
                return;
            await Context.Mutate(a => { RelayPolicies.Save(Context.Store, next); return Task.CompletedTask; });
            await Refresh();
        }
        private Task Export()
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Configuration JSON|*.json", FileName = "delegation.json" };
            if (picker.ShowDialog(Shell) == true)
                SafeFiles.AtomicWrite(picker.FileName, System.Text.Encoding.UTF8.GetBytes(RelayPolicies.Export(RelayPolicies.Load(Context.Store))));
            return Task.CompletedTask;
        }
    }
    internal sealed class PolicyEditor
    {
        private readonly List<Action> setters = new List<Action>();
        private readonly List<Func<string>> values = new List<Func<string>>();
        private readonly string initial;
        public bool Dirty
        {
            get
            {
                return Snapshot() != initial;
            }
        }
        private string Snapshot()
        {
            return String.Join("\n", values.Select(v => v()));
        }
        public void Apply()
        {
            foreach (var set in setters)
                set();
        }
        public PolicyEditor(RelayStore store, RelayPolicy policy, object item, StackPanel body, bool added)
        {
            var advanced = new StackPanel();
            var expander = new Expander { Header = "Options avancées", Content = advanced, Margin = new Thickness(0, 16, 0, 12) };
            var hidden = new[] { "Id", "Tasks", "MaxJobs", "MaxDepth", "MaxMinutes", "Model", "ReuseConversation", "ReturnDelaySeconds", "AllowedWorkspaces", "Priority", "ReassignQueued", "AllowUnknownQuota", "ExternalId" };
            foreach (var prop in item.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
            {
                var p = prop;
                string key = p.Name;
                var target = hidden.Contains(key) ? advanced : body;
                string label = GuidedEditor.Label(key);
                object value = p.GetValue(item, null);
                if (p.PropertyType == typeof(bool))
                {
                    var box = Ui.Check(label, (bool)value, target);
                    values.Add(() => box.IsChecked.ToString());
                    setters.Add(() => p.SetValue(item, box.IsChecked == true, null));
                    continue;
                }
                string[] choices = null;
                if (key == "Channel" || key == "PreferredAgent")
                    choices = new[] { "" }.Concat(store.Channels().Select(c => c.Id)).ToArray();
                if (key == "Project")
                    choices = (item is RelayRule ? new[] { "*" } : new[] { "" }).Concat(policy.Projects.Select(x => x.Id)).ToArray();
                if (key == "Permission")
                    choices = new[] { "inherit", "full-access" };
                if (key == "Delegation")
                    choices = new[] { "explicit", "rules" };
                if (key == "RoutingStrategy")
                    choices = new[] { "available", "balanced", "quota", "preferred" };
                if (key == "ReturnMode")
                    choices = new[] { "immediate", "batch", "manual" };
                if (key == "WorkspaceMode")
                    choices = new[] { "shared", "isolated" };
                if (choices != null)
                {
                    var labels = choices.Select(GuidedEditor.Friendly).ToArray();
                    var box = Ui.Select(label, labels, GuidedEditor.Friendly((string)value ?? ""), target);
                    if (key == "Channel" && !added)
                        box.IsEnabled = false;
                    values.Add(() => box.SelectedIndex.ToString());
                    setters.Add(() => p.SetValue(item, choices[Math.Max(0, box.SelectedIndex)], null));
                }
                else if (new[] { "Projects", "SourceChannels", "TargetChannels", "Channels", "Sources", "Targets" }.Contains(key))
                {
                    target.Children.Add(Ui.Text(label, 13));
                    var selected = RelayPolicies.Tags((string)value);
                    var options = (key == "Projects" ? policy.Projects.Select(x => x.Id) : store.Channels().Select(x => x.Id)).Concat(selected.Where(x => x != "*")).Distinct().ToArray();
                    var group = new StackPanel();
                    var all = Ui.Check("Tous", selected.Contains("*"), group);
                    var boxes = new Dictionary<string, CheckBox>();
                    foreach (var option in options)
                        boxes[option] = Ui.Check(option, selected.Contains(option), group);
                    Action enabled = () => { foreach (var b in boxes.Values) b.IsEnabled = all.IsChecked != true; };
                    all.Checked += delegate
                    {
                        enabled();
                    };
                    all.Unchecked += delegate
                    {
                        enabled();
                    };
                    enabled();
                    target.Children.Add(new Border { Child = group, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10) });
                    Func<string> read = () => all.IsChecked == true ? "*" : String.Join(",", boxes.Where(b => b.Value.IsChecked == true).Select(b => b.Key));
                    values.Add(read);
                    setters.Add(() => p.SetValue(item, read(), null));
                }
                else
                {
                    var input = Ui.Input(label, Convert.ToString(value, CultureInfo.CurrentCulture), target, new[] { "Instructions", "When", "AllowedWorkspaces" }.Contains(key));
                    if (key == "Id" && !added)
                        input.IsReadOnly = true;
                    values.Add(() => input.Text);
                    setters.Add(() => { object next = input.Text.Trim(); if (p.PropertyType == typeof(int)) { int number; if (!Int32.TryParse(input.Text, out number)) throw new InvalidOperationException(label + " : saisissez un nombre entier."); next = number; } else if (p.PropertyType == typeof(double)) { double number; if (!Double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) || Double.IsNaN(number) || Double.IsInfinity(number)) throw new InvalidOperationException(label + " : nombre invalide."); next = number; } p.SetValue(item, next, null); });
                    if (key == "Workspace")
                        target.Children.Add(Ui.Button("Choisir le dossier", () => { string folder = Ui.Folder(); if (folder != null) input.Text = folder; }));
                }
                var desc = (DescriptionAttribute)p.GetCustomAttributes(typeof(DescriptionAttribute), true).FirstOrDefault();
                if (desc != null && new[] { "Capabilities", "Permission", "Instructions", "When" }.Contains(key))
                    target.Children.Add(Ui.Text(desc.Description, 12, true));
            }
            body.Children.Add(expander);
            initial = Snapshot();
        }
    }
}
