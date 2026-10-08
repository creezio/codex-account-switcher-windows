using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    public sealed class AppearanceSettings
    {
        public string Theme { get; set; } = "Système";
        public bool Advanced { get; set; }
    }
    internal sealed class PreferencesPage : PageView
    {
        private readonly StackPanel fields = new StackPanel();
        private TextBox exe, home, max, perAccount, checkMinutes;
        private CheckBox autoRefresh, autoReset, notify, background, persistent, autoInstall;
        private ComboBox theme;
        private string saved;
        private bool loaded; internal Func<bool> ConfirmDiscard;
        public PreferencesPage(DesktopContext c, ShellWindow s) : base(c, s, "Paramètres", "Réglez l'application, les intégrations et le fonctionnement en arrière-plan.")
        {
            ConfirmDiscard = () => Ui.Confirm(Shell, "Abandonner les réglages non enregistrés ?", "Modifications en cours");
            Body.Children.Add(new ScrollViewer { Content = fields });
            Command("Enregistrer les réglages", Save, true);
            Command("Intégration Codex", () => IntegrationEditor.Open(Context, Shell));
            Command("Diagnostic", Diagnostic);
            Command("Mises à jour", async delegate { Notice.Text = await ReleaseCheck.Read(); });
        }
        private string Snapshot()
        {
            return exe.Text + "|" + home.Text + "|" + max.Text + "|" + perAccount.Text + "|" + autoRefresh.IsChecked + "|" + autoReset.IsChecked + "|" + notify.IsChecked + "|" + background.IsChecked + "|" + persistent.IsChecked + "|" + autoInstall.IsChecked + "|" + checkMinutes.Text + "|" + theme.SelectedItem;
        }
        public override async Task Refresh()
        {
            if (loaded)
                return;
            loaded = true;
            try
            {
                var state = await Context.Read(a => new { Settings = a.Settings, Usage = UsageCoordinator.Policies(Context.Store), Relay = RelayPolicies.Load(Context.Store), Appearance = Context.Store.ReadRecord<AppearanceSettings>("appearance.dpapi") });
                var appearance = new StackPanel();
                theme = Ui.Select("Thème", new[] { "Système", "Clair", "Sombre" }, state.Appearance.Theme, appearance);
                theme.SelectionChanged += delegate
                {
                    Shell.Theme((string)theme.SelectedItem);
                };
                fields.Children.Add(new GroupBox { Header = "Apparence", Content = appearance });
                var supervision = new StackPanel();
                autoRefresh = Ui.Check("Actualiser les limites de tous les comptes", state.Settings.AutoRefresh, supervision);
                autoReset = Ui.Check("Autoriser les réinitialisations automatiques par défaut", state.Settings.AutoResetCredits, supervision);
                supervision.Children.Add(Ui.Text("Chaque compte peut remplacer ce réglage depuis sa fiche. Aucune réinitialisation n'est consommée sans disponibilité confirmée.", 13, true));
                notify = Ui.Check("Afficher les notifications", state.Settings.Notifications, supervision);
                background = Ui.Check("Continuer la supervision quand le switcher est quitté", state.Usage.Background, supervision);
                fields.Children.Add(new GroupBox { Header = "Limites d'utilisation", Content = supervision });
                var relay = new StackPanel();
                persistent = Ui.Check("Garder le relais actif même sans tâche", state.Relay.KeepWorkerRunning, relay);
                autoInstall = Ui.Check("Vérifier et entretenir automatiquement le plugin et les skills", state.Settings.MaintainIntegration, relay);
                checkMinutes = Ui.Input("Vérification au démarrage puis toutes les X minutes (1 à 120)", state.Settings.IntegrationCheckMinutes.ToString(), relay);
                relay.Children.Add(Ui.Text("Actif tant que le switcher est ouvert, y compris près de l'horloge. Les installations manquantes sont réparées. Une désactivation volontaire dans Codex reste signalée jusqu'à une réparation demandée.",13,true));
                max = Ui.Input("Tâches simultanées au total (1 à 16)", state.Relay.MaxParallel.ToString(), relay);
                perAccount = Ui.Input("Tâches simultanées par compte (1 à 16)", state.Relay.MaxPerAccount.ToString(), relay);
                fields.Children.Add(new GroupBox { Header = "Collaboration", Content = relay });
                var advanced = new StackPanel();
                exe = Ui.Input("Exécutable Codex CLI", state.Settings.CodexExecutable, advanced);
                advanced.Children.Add(Ui.Button("Choisir l'exécutable", () => { string path = Ui.File("Exécutable|*.exe"); if (path != null) exe.Text = path; }));
                home = Ui.Input("Dossier de la session habituelle", state.Settings.CodexHome, advanced);
                advanced.Children.Add(Ui.Button("Choisir le dossier", () => { string path = Ui.Folder(); if (path != null) home.Text = path; }));
                fields.Children.Add(new Expander { Header = "Chemins avancés", Content = advanced, Margin = new Thickness(0, 10, 0, 20) });
                var maintenance = new StackPanel();
                maintenance.Children.Add(Ui.AsyncButton("Restaurer la connexion précédente", async delegate { if (!Ui.Confirm(Shell, "Restaurer la connexion précédente de la session habituelle ? Ses clients Codex doivent être fermés.", "Restaurer la connexion")) return; await Context.Mutate(a => { a.RestorePrevious(); return Task.CompletedTask; }); Notice.Text = "Connexion précédente restaurée."; }, Error));
                maintenance.Children.Add(Ui.AsyncButton("Espaces Git et fichiers", () => AdvancedTools.Workspace(Context, Shell), Error));
                maintenance.Children.Add(Ui.AsyncButton("Examiner les temporaires", () => AdvancedTools.Maintenance(Context, Shell), Error));
                maintenance.Children.Add(Ui.AsyncButton("Exporter le diagnostic expurgé", ExportDiagnostic, Error));
                maintenance.Children.Add(Ui.AsyncButton("Restaurer la configuration précédente…", async delegate { if (Ui.Confirm(Shell, "Restaurer la configuration précédente du relais ?", "Restaurer")) { RelayPolicies.Restore(Context.Store); Notice.Text = "Configuration du relais restaurée."; } await Task.CompletedTask; }, Error));
                maintenance.Children.Add(Ui.Text("Les diagnostics exportés excluent les comptes, chemins privés, prompts et jetons. Les conversations et profils ne sont jamais purgés automatiquement.", 13, true));
                fields.Children.Add(new GroupBox { Header = "Maintenance", Content = maintenance });
                saved = Snapshot();
            }
            catch { loaded = false; throw; }
        }
        public override bool CanLeave()
        {
            if (!loaded || saved == null || saved == Snapshot())
                return true;
            bool discard = ConfirmDiscard();
            if (discard)
            {
                loaded = false;
                fields.Children.Clear();
                Shell.Theme(Context.Store.ReadRecord<AppearanceSettings>("appearance.dpapi").Theme);
            }
            return discard;
        }
        private async Task Save()
        {
            if (saved == null)
                return;
            int total, account;
            int interval;
            if(!Int32.TryParse(checkMinutes.Text,out interval)||interval<1||interval>120)throw new InvalidOperationException("Choisissez un intervalle de 1 à 120 minutes.");
            if (!Int32.TryParse(max.Text, out total) || !Int32.TryParse(perAccount.Text, out account) || total < 1 || total > 16 || account < 1 || account > 16)
                throw new InvalidOperationException("Les limites de tâches doivent être comprises entre 1 et 16.");
            if (!File.Exists(exe.Text) || !Directory.Exists(home.Text) || !Path.IsPathRooted(home.Text))
                throw new InvalidOperationException("Choisissez un exécutable et un dossier Codex existants.");
            await Context.Mutate(a => { a.Settings.CodexExecutable = Path.GetFullPath(exe.Text); a.Settings.CodexHome = Path.GetFullPath(home.Text); a.Settings.AutoRefresh = autoRefresh.IsChecked == true; a.Settings.AutoResetCredits = autoReset.IsChecked == true; a.Settings.Notifications = notify.IsChecked == true; a.Settings.MaintainIntegration = autoInstall.IsChecked == true; a.Settings.IntegrationCheckMinutes = interval; a.Vault.SaveSettings(a.Settings); var usage = UsageCoordinator.Policies(Context.Store); usage.Background = background.IsChecked == true; UsageCoordinator.SavePolicies(Context.Store, usage); var policy = RelayPolicies.Load(Context.Store); policy.MaxParallel = total; policy.MaxPerAccount = account; policy.KeepWorkerRunning = persistent.IsChecked == true; policy.AutoInstallManaged = false; RelayPolicies.Save(Context.Store, policy); Context.Store.WriteRecord("appearance.dpapi", new AppearanceSettings { Theme = (string)theme.SelectedItem, Advanced = Context.Store.ReadRecord<AppearanceSettings>("appearance.dpapi").Advanced }); if (!Context.Fixture) { if (usage.Background) UsageCoordinator.Ensure(Context.Store); if (policy.KeepWorkerRunning) RelayWorker.Ensure(Context.Store); } return Task.CompletedTask; });
            saved = Snapshot();
            Notice.Text = "Réglages enregistrés.";
        }
        private async Task Diagnostic()
        {
            string report = await Context.Read(a => ProductDiagnostics.Report(a, Context.Store));
            var d = new Window { Owner = Shell, Title = "Diagnostic", Width = 700, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            d.Content = new TextBox { Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(24) };
            d.ShowDialog();
        }
        private async Task ExportDiagnostic()
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Diagnostic JSON|*.json", FileName = "diagnostic-switcher.json" };
            if (picker.ShowDialog(Shell) != true)
                return;
            string text = await Context.Read(a => ProductDiagnostics.Report(a, Context.Store));
            SafeFiles.AtomicWrite(picker.FileName, System.Text.Encoding.UTF8.GetBytes(text));
            Notice.Text = "Diagnostic exporté.";
        }
    }
    internal static class IntegrationEditor
    {
        internal static async Task Open(DesktopContext context, ShellWindow shell)
        {
            var data = await context.Read(a => new { Instances = a.Data.Instances.Where(i => !i.Archived).Select(i => new { Name = i.Name, Home = a.Instances.Home(i) }).ToArray(), Executable = a.Settings.CodexExecutable });
            var d = new Window { Owner = shell, Title = "Intégration Codex", Width = 650, Height = 530, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var stack = new StackPanel { Margin = new Thickness(24) };
            d.Content = new ScrollViewer { Content = stack };
            stack.Children.Add(Ui.Text("Connectez vos agents", 25));
            stack.Children.Add(Ui.Text("Installez les skills et les outils du relais sur chaque profil participant. Ouvrez ensuite un nouveau chat pour les charger.", 14, true));
            var selected = Ui.Select("Instance", data.Instances.Select(x => x.Name), data.Instances.FirstOrDefault()?.Name, stack);
            var report = Ui.Text("", 14, true);
            stack.Children.Add(report);
            Action status = () => { try { if (selected.SelectedIndex >= 0) report.Text = RelayIntegration.Status(context.Store, data.Instances[selected.SelectedIndex].Home).Status; } catch (Exception e) { report.Text = Program.SafeError(e); } };
            selected.SelectionChanged += delegate
            {
                status();
            };
            status();
            stack.Children.Add(Ui.AsyncButton("Installer ou mettre à jour", async delegate { if (selected.SelectedIndex < 0) return; var state = await RelayIntegration.Install(context.Store, data.Instances[selected.SelectedIndex].Home, data.Executable, true, CancellationToken.None); report.Text = state.Status + "\nOuvrez un nouveau chat dans cette instance pour vérifier le chargement."; }, e => report.Text = Program.SafeError(e), true));
            stack.Children.Add(Ui.AsyncButton("Vérifier les plugins installés", async delegate { if (selected.SelectedIndex < 0) return; var inventory = await RelayIntegration.Inventory(data.Executable, data.Instances[selected.SelectedIndex].Home, CancellationToken.None); report.Text = "Un plugin installé ne prouve pas l'accès à ses ressources privées.\n" + Json.Write(inventory); }, e => report.Text = Program.SafeError(e)));
            stack.Children.Add(Ui.Button("Fermer", d.Close));
            d.ShowDialog();
        }
    }
    internal static class ConnectionEditor
    {
        internal static Task Open(DesktopContext context, ShellWindow shell)
        {
            var d = new Window { Owner = shell, Title = "Connecter une instance", Width = 680, Height = 650, MinWidth = 480, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var stack = new StackPanel { Margin = new Thickness(24) };
            d.Content = new ScrollViewer { Content = stack };
            stack.Children.Add(Ui.Text("Connecter un chat au relais", 24));
            stack.Children.Add(Ui.Text("1. Installez l'intégration sur le profil choisi.\n2. Ouvrez un chat dans cette instance et choisissez ses permissions.\n3. Collez la consigne ci-dessous ; le compte et le dossier seront vérifiés.", 14, true));
            var name = Ui.Input("Nom de la connexion (minuscules et tirets)", "agent-revue", stack);
            var folder = Ui.Input("Dossier partagé du projet", "", stack);
            stack.Children.Add(Ui.Button("Choisir un dossier", () => { string path = Ui.Folder(); if (path != null) folder.Text = path; }));
            var full = Ui.Check("Exiger Accès complet confirmé dans ce chat", false, stack);
            var preview = Ui.Input("Consigne à coller dans Codex", "", stack, true);
            preview.IsReadOnly = true;
            var status = Ui.Text("", 13, true);
            stack.Children.Add(status);
            stack.Children.Add(Ui.Button("Préparer et copier la consigne", () => { try { RelayStore.ChannelId(name.Text); string workspace = RelayStore.WorkspacePath(folder.Text); string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CreezioRelay.exe"); if (!File.Exists(exe)) throw new InvalidOperationException("CreezioRelay.exe absent de la livraison."); preview.Text = "Je veux connecter cette conversation au relais local et autoriser les demandes entre mes canaux connectés pour ce projet. Exécute la commande suivante et confirme le compte, le dossier et les permissions. Ne change pas de compte ni de permissions.\n\n@{operation='register';channel=" + DesktopRuntime.QuotePS(name.Text) + ";workspace=" + DesktopRuntime.QuotePS(workspace) + ";requireFullAccess=" + (full.IsChecked == true ? "$true" : "$false") + "} | ConvertTo-Json -Compress | & " + DesktopRuntime.QuotePS(exe); Clipboard.SetText(preview.Text); status.Text = "Consigne copiée. Collez-la dans le chat destinataire."; } catch (Exception e) { status.Text = Program.SafeError(e); } }, true));
            stack.Children.Add(Ui.Button("Fermer", d.Close));
            d.ShowDialog();
            return Task.CompletedTask;
        }
    }
}
