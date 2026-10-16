using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class DirectComposer
    {
        internal static Task Open(DesktopContext context, ShellWindow shell, RelayMessage previous)
        {
            var channels = context.Store.Channels().Where(c => c.Enabled).ToArray();
            if (channels.Length == 0) throw new InvalidOperationException("Connectez un agent depuis Agents avant l'envoi.");
            var d = new EditWindow(shell, "Envoyer un message");
            var target = Ui.Select("Instance destinataire", channels.Select(c => c.Id), previous?.TargetChannelId ?? channels[0].Id, d.Fields);
            var mode = Ui.Select("Conversation", new[] { "Nouvelle conversation", "Conversation existante" }, previous == null ? "Nouvelle conversation" : "Conversation existante", d.Fields);
            var choices = new ComboBox { Margin = new Thickness(0, 6, 0, 6), DisplayMemberPath = "Title" };
            d.Fields.Children.Add(choices);
            var thread = Ui.Input("Identifiant du chat (sélection ou collage)", previous?.TargetThreadId ?? "", d.Fields);
            var info = Ui.Text("Le résultat restera dans Tâches. Aucun chat source n'est nécessaire.", 13, true);
            d.Fields.Children.Add(info);
            int generation = 0;
            d.Fields.Children.Add(Ui.AsyncButton("Charger les conversations", async delegate
            {
                string channel = (string)target.SelectedItem; int current = ++generation;
                var list = await new DirectMessages(context.Store).Conversations(channel, CancellationToken.None);
                if (current == generation && channel == (string)target.SelectedItem) { choices.ItemsSource = list; info.Text = list.Length + " conversations récentes. Pour un autre chat, collez son identifiant."; }
            }, e => info.Text = Program.SafeError(e)));
            choices.SelectionChanged += delegate { if (choices.SelectedItem is ConversationChoice c) { thread.Text = c.Id; mode.SelectedIndex = 1; } };
            target.SelectionChanged += delegate { generation++; choices.ItemsSource = null; thread.Text = ""; };
            mode.SelectionChanged += delegate { choices.IsEnabled = thread.IsEnabled = mode.SelectedIndex == 1; };
            choices.IsEnabled = thread.IsEnabled = mode.SelectedIndex == 1;
            var project = Ui.Select("Projet configuré (facultatif)", new[] { "" }.Concat(RelayPolicies.Load(context.Store).Projects.Select(p => p.Id)), previous?.Job?.Project ?? "", d.Fields);
            var title = Ui.Input("Objet", previous?.Title ?? "", d.Fields);
            var prompt = Ui.Input("Message", "", d.Fields, true);
            var access = Ui.Select("Action autorisée", new[] { "Lecture et analyse", "Modifier les fichiers", "Action externe" }, "Lecture et analyse", d.Fields);
            d.Fields.Children.Add(Ui.Text("Un chat occupé reste en attente. Les permissions natives de Codex sont conservées ; une demande d'approbation se traite dans le chat concerné.", 12, true));
            string id = Guid.NewGuid().ToString("N");
            Func<RelayJobSpec> request = () => new RelayJobSpec { Id = id, To = (string)target.SelectedItem, Project = (string)project.SelectedItem, Title = title.Text.Trim(), Prompt = prompt.Text, Access = new[] { "read", "write", "external" }[access.SelectedIndex] };
            string initial = Json.Write(request()) + thread.Text + mode.SelectedIndex;
            d.Dirty = () => Json.Write(request()) + thread.Text + mode.SelectedIndex != initial;
            d.Save = async delegate
            {
                if (mode.SelectedIndex == 1 && String.IsNullOrWhiteSpace(thread.Text)) throw new InvalidOperationException("Choisissez une conversation ou collez son identifiant.");
                await new DirectMessages(context.Store).Submit(request(), mode.SelectedIndex == 0 ? null : thread.Text, CancellationToken.None);
                RelayWorker.Ensure(context.Store);
            };
            d.Loaded += delegate { foreach (var b in Ui.Descendants(d).OfType<Button>().Where(b => Object.Equals(b.Content, "Enregistrer"))) b.Content = "Envoyer"; };
            d.ShowDialog();
            return Task.CompletedTask;
        }
    }
}
