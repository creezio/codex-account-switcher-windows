using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CodexCompatibilityWindow : Window
    {
        private readonly DesktopContext context;
        private readonly StackPanel body = new StackPanel();
        private readonly TextBlock notice = Ui.Text("", 13, true), error = Ui.Text("", 13);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool checking, closed;
        internal Func<Task<CodexCompatibilityReport>> CheckFixture { get; set; }
        internal string Summary => notice.Text;
        internal CodexCompatibilityWindow(DesktopContext context, Window owner)
        {
            this.context = context; Owner = owner;
            Title = "Compatibilité Codex"; Width = Math.Min(830, SystemParameters.WorkArea.Width); Height = Math.Min(690, SystemParameters.WorkArea.Height);
            MinWidth = 610; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
            var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            header.Children.Add(Ui.Text("Compatibilité Codex", 26));
            header.Children.Add(Ui.Text("Le Switcher détecte les changements de version et vérifie les connexions natives existantes. Les instances fermées restent fermées et aucun prompt n’est envoyé.", 13, true));
            header.Children.Add(notice); header.Children.Add(error);
            var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            footer.Children.Add(Ui.AsyncButton("Refaire les vérifications", Check, e => error.Text = Program.SafeError(e), true));
            footer.Children.Add(Ui.Button("Fermer", Close));
            root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Loaded += async delegate { try { await RefreshNow(); } catch (Exception e) { error.Text = Program.SafeError(e); } };
            Closed += delegate { closed = true; lifetime.Cancel(); };
        }
        internal async Task RefreshNow()
        {
            var report = await Task.Run(() => new CodexCompatibility(context.Store).Read());
            if (!closed) Render(report);
        }
        private async Task Check()
        {
            if (checking || closed) return; checking = true;
            error.Text = ""; notice.Text = "Vérifications en cours · aucune mission de test ne sera exécutée";
            try
            {
                CodexCompatibilityReport report;
                if (context.Fixture)
                {
                    if (CheckFixture == null) throw new InvalidOperationException("Contrôle de compatibilité non configuré dans cette recette.");
                    report = await CheckFixture();
                }
                else report = await Task.Run(() => new CodexCompatibility(context.Store).Refresh(lifetime.Token, true));
                if (!closed) Render(report);
            }
            finally { checking = false; }
        }
        private static string Label(string state)
        {
            switch (state)
            {
                case "verified": return "Lecture native vérifiée";
                case "catalog-only": return "Commandes détectées · lecture non testée";
                case "not-tested": return "Non testée";
                case "incompatible": return "Incompatibilité détectée";
                case "unavailable": return "Connexion non vérifiable";
                case "attention": return "Vérification à examiner";
                default: return "Vérification en attente";
            }
        }
        private void Render(CodexCompatibilityReport report)
        {
            body.Children.Clear();
            notice.Text = Label(report.State) + " · dernier contrôle " + ChatWatchesWindow.Age(report.CheckedUtc);
            body.Children.Add(Ui.Text(report.Detail ?? "Aucun contrôle enregistré. Le Switcher vérifiera automatiquement les instances disponibles.", 14));
            var installed = new StackPanel();
            installed.Children.Add(Ui.Text("Versions détectées", 19));
            installed.Children.Add(Ui.Text("Application installée : " + (report.InstalledVersion ?? "Non détectée"), 15));
            installed.Children.Add(Ui.Text("CLI configuré : " + (report.CliVersion ?? "Non vérifié"), 14, true));
            installed.Children.Add(Ui.Text("Une instance déjà ouverte peut utiliser une version différente de l’application installée. Le Switcher vérifie chaque connexion séparément.", 12, true));
            body.Children.Add(Ui.Card(installed));
            body.Children.Add(Ui.Text("Instances", 20));
            var checks = report.Checks;
            if (checks == null || checks.Count == 0) body.Children.Add(Ui.Text("Aucune instance vérifiée pour cette version.", 13, true));
            else foreach (var check in checks)
            {
                var panel = new StackPanel();
                panel.Children.Add(Ui.Text(check.Name ?? check.InstanceId ?? "Connexion", 18));
                panel.Children.Add(Ui.Text(Label(check.State) + " · " + (String.IsNullOrEmpty(check.RunningVersion) ? "Aucune version active détectée" : "Version active : " + check.RunningVersion), 13));
                panel.Children.Add(Ui.Text(check.Detail ?? "", 13, true));
                panel.Children.Add(Ui.Text("Contrôlée " + ChatWatchesWindow.Age(check.CheckedUtc), 12, true));
                body.Children.Add(Ui.Card(panel));
            }
            body.Children.Add(Ui.Text("Ce contrôle vérifie l’identité de l’instance, le canal Windows, les commandes disponibles et, lorsqu’un chat existe déjà, sa lecture. Il ne valide pas une publication, une modification, un envoi de mission ou tous les plugins.", 12, true));
            var paths = new StackPanel();
            paths.Children.Add(Ui.Text("Application installée : " + (report.InstalledPath ?? "Non détectée") + "\nCLI configuré : " + (report.CliPath ?? "Non configuré"), 12, true));
            body.Children.Add(new Expander { Header = "Chemins techniques", Content = paths });
        }
    }
}
