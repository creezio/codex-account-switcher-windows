using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class AccountsPage : CollectionPage
    {
        private CancellationTokenSource login; private readonly Button cancelLogin;
        public AccountsPage(DesktopContext c, ShellWindow s) : base(c, s, "Comptes", "Consultez les limites de vos comptes. Chaque instance conserve son compte permanent.")
        {
            Command("Ajouter un compte", async delegate { cancelLogin.Visibility = Visibility.Visible; login = new CancellationTokenSource(TimeSpan.FromMinutes(5)); Notice.Text = "Terminez la connexion dans votre navigateur. Vous pouvez continuer à naviguer."; try { await Change(async a => { var p = await a.Login(url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), login.Token); await UsageCoordinator.Refresh(Context.Store, p.Key, false, login.Token); }); Notice.Text = "Compte ajouté."; } finally { login.Dispose(); login = null; cancelLogin.Visibility = Visibility.Collapsed; } }, true);
            Command("Importer la session actuelle", () => Change(a => { a.ImportCurrent(); return Task.CompletedTask; }));
            Command("Actualiser les limites", async delegate { Notice.Text = "Lecture des limites…"; await Change(async a => { foreach (var p in a.Data.Profiles) await UsageCoordinator.Refresh(Context.Store, p.Key, false, CancellationToken.None); }); Notice.Text = "Limites actualisées."; });
            cancelLogin = Command("Annuler la connexion", delegate
            {
                login?.Cancel();
                return Task.CompletedTask;
            });
            cancelLogin.Visibility = Visibility.Collapsed;
            var more = new Button { Content = "Autres actions" };
            Commands.Children.Add(more);
            var menu = new ContextMenu();
            more.Click += delegate
            {
                menu.PlacementTarget = more;
                menu.IsOpen = true;
            };
            var import = new MenuItem { Header = "Importer un fichier auth.json" };
            menu.Items.Add(import);
            import.Click += async delegate { try { string path = Ui.File("Connexion Codex|auth.json"); if (path != null) await Change(a => { a.Import(SafeFiles.ReadText(path), null); return Task.CompletedTask; }); } catch (Exception e) { Error(e); } };
        }
        public override async Task Refresh()
        {
            var rows = await Context.Read(a => a.Data.Profiles.OrderBy(p => p.Label).Select(p => new ItemRow { Id = p.Key, Revision = p.QuotaTimeUtc + "|" + p.ResetCredits?.AvailableCount + "|" + p.AllInstances + "|" + String.Join(",", p.InstanceIds), Title = p.Label, Summary = p.Email + " · " + p.Plan, State = p.IsFresh ? ProductUx.Percent(p.Score) + " de limite restante" : "Limites à actualiser", Value = p }).ToArray());
            Rows(rows);
        }
        protected override void ShowSelected()
        {
            Details.Children.Clear();
            var row = List.SelectedItem as ItemRow;
            if (row == null)
                return;
            var p = (Profile)row.Value;
            Details.Children.Add(Ui.Text(p.Label, 23));
            Details.Children.Add(Ui.Text(p.Email, 14, true));
            foreach (var bucket in p.Quotas)
            {
                foreach (var q in new[] { bucket.Primary, bucket.Secondary }.Where(q => q != null))
                {
                    var card = new StackPanel();
                    card.Children.Add(Ui.Text(q.Caption + " · " + ProductUx.Percent(q.Remaining) + " restants", 17));
                    var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = q.Remaining ?? 0, Height = 6, Margin = new Thickness(0, 4, 0, 10) };
                    bar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
                    card.Children.Add(bar);
                    card.Children.Add(Ui.Text(q.ResetCaption, 12, true));
                    if (!p.IsFresh)
                        card.Children.Add(Ui.Text("Dernière valeur connue · à actualiser", 12, true));
                    Details.Children.Add(Ui.Card(card));
                }
            }
            var reset = new StackPanel();
            reset.Children.Add(Ui.Text("Réinitialisations des limites", 17));
            reset.Children.Add(Ui.Text(p.ResetCredits?.AvailableCount.HasValue == true ? p.ResetCredits.AvailableCount + " disponible(s)" : "Disponibilité à actualiser", 14, true));
            reset.Children.Add(Ui.Text("Ces réinitialisations rétablissent les quotas d'utilisation. Elles sont distinctes des crédits achetés.", 13, true));
            reset.Children.Add(Ui.AsyncButton("Configurer les réinitialisations", () => UsageEditor.Open(Context, Shell, p.Key), Error));
            Details.Children.Add(Ui.Card(reset));
            var actions = Ui.Actions(Details);
            actions.Children.Add(Ui.Button("Voir les instances", () => Shell.Navigate("Instances")));
            actions.Children.Add(Ui.AsyncButton("Renommer", async delegate { string name = Ui.Prompt(Shell, "Renommer le compte", "Nom", p.Label); if (name != null) await Change(a => { a.Data.Profiles.Single(x => x.Key == p.Key).Label = name; a.Save(); return Task.CompletedTask; }); }, Error));
            actions.Children.Add(Ui.AsyncButton("Retirer…", async delegate { if (Ui.Confirm(Shell, "Retirer ce compte du coffre ? Ses conversations et les connexions ouvertes restent conservées.", "Retirer le compte")) await Change(a => { a.Instances.Forget(a.Data.Profiles.Single(x => x.Key == p.Key)); return Task.CompletedTask; }); }, Error));
        }
        private async Task Scope(string key)
        {
            var data = await Context.Read(a => new { Profile = a.Data.Profiles.Single(p => p.Key == key), Instances = a.Data.Instances.ToArray() });
            var d = new EditWindow(Shell, "Instances autorisées");
            var all = Ui.Check("Toutes les instances, présentes et futures", data.Profile.AllInstances, d.Fields);
            var boxes = new Dictionary<string, CheckBox>();
            foreach (var i in data.Instances)
                boxes[i.Id] = Ui.Check(i.Name, data.Profile.InstanceIds.Contains(i.Id), d.Fields);
            Action enabled = () => { foreach (var box in boxes.Values) box.IsEnabled = all.IsChecked != true; };
            all.Checked += delegate
            {
                enabled();
            };
            all.Unchecked += delegate
            {
                enabled();
            };
            enabled();
            Func<string> snapshot = () => all.IsChecked + "|" + String.Join(",", boxes.Where(x => x.Value.IsChecked == true).Select(x => x.Key));
            string initial = snapshot();
            d.Dirty = () => snapshot() != initial;
            d.Save = () => Context.Mutate(a => { a.Instances.SetScope(a.Data.Profiles.Single(p => p.Key == key), all.IsChecked == true, boxes.Where(x => x.Value.IsChecked == true).Select(x => x.Key)); return Task.CompletedTask; });
            d.ShowDialog();
        }
    }
    internal static class UsageEditor
    {
        internal static async Task Open(DesktopContext c, ShellWindow owner, string key)
        {
            var current = await c.Read(a => new { Policy = UsageCoordinator.For(c.Store, key), Global = a.Settings.AutoResetCredits, Profile = a.Data.Profiles.Single(p => p.Key == key), History = c.Store.ReadRecord<UsageHistory>("reset-history-" + RelayReturns.Key(key) + ".dpapi") });
            var d = new EditWindow(owner, "Limites · " + current.Profile.Label);
            d.Fields.Children.Add(Ui.Text("Définissez quand utiliser une réinitialisation disponible.", 14, true));
            var modes = new[] { "Selon le réglage global", "Automatique", "Manuelle", "Désactivée" };
            var values = new[] { "inherit", "auto", "manual", "off" };
            var mode = Ui.Select("Politique de ce compte", modes, modes[Array.IndexOf(values, current.Policy.Mode)], d.Fields);
            var threshold = Ui.Input("Déclencher à ce pourcentage restant ou moins", current.Policy.Threshold.ToString(CultureInfo.CurrentCulture), d.Fields);
            var windows = new[] { "Toutes les fenêtres", "5 heures", "Hebdomadaire" };
            var windowValues = new[] { "all", "session", "weekly" };
            var window = Ui.Select("Fenêtre surveillée", windows, windows[Array.IndexOf(windowValues, current.Policy.Window)], d.Fields);
            var notify = Ui.Check("Notifier les réinitialisations de ce compte", current.Policy.Notifications, d.Fields);
            var effect = Ui.Text("", 13, true);
            d.Fields.Children.Add(effect);
            Action explain = () => { bool auto = mode.SelectedIndex == 1 || (mode.SelectedIndex == 0 && current.Global); effect.Text = auto ? "Automatisme activé pour ce compte utilisé : déclenchement au seuil choisi si une réinitialisation est disponible. Le serveur décide des fenêtres rétablies." : "Aucune réinitialisation automatique avec ce réglage."; };
            mode.SelectionChanged += delegate
            {
                explain();
            };
            explain();
            Func<string> snapshot = () => mode.SelectedIndex + "|" + threshold.Text + "|" + window.SelectedIndex + "|" + notify.IsChecked;
            string initial = snapshot();
            d.Dirty = () => snapshot() != initial;
            d.Save = () => c.Mutate(a => { double number; if (!Double.TryParse(threshold.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) || number < 0 || number > 25 || Double.IsNaN(number) || Double.IsInfinity(number)) throw new InvalidOperationException("Le seuil doit être compris entre 0 et 25 %."); var p = UsageCoordinator.Policies(c.Store); p.Accounts.RemoveAll(x => x.Account == key); p.Accounts.Add(new AccountUsagePolicy { Account = key, Mode = values[mode.SelectedIndex], Threshold = number, Window = windowValues[window.SelectedIndex], Notifications = notify.IsChecked == true }); UsageCoordinator.SavePolicies(c.Store, p); return Task.CompletedTask; });
            var manual = Ui.AsyncButton("Utiliser une réinitialisation maintenant…", async delegate { if (!Ui.Confirm(d, "Utiliser une réinitialisation disponible pour ce compte ? Les modifications du formulaire ne seront pas enregistrées par cette action.", "Réinitialisation manuelle")) return; await c.Mutate(async a => { await UsageCoordinator.Refresh(c.Store, key, true, CancellationToken.None, true); }); var result = c.Store.ReadRecord<Profile>(RelayQuota.Name(key)); effect.Text = result.ResetMessage ?? "Aucune réinitialisation disponible ou politique enregistrée désactivée."; }, e => effect.Text = Program.SafeError(e));
            manual.IsEnabled = current.Profile.ResetCredits?.AvailableCount > 0;
            d.Fields.Children.Add(manual);
            var history = new StackPanel();
            foreach (var e in current.History.Events.AsEnumerable().Reverse().Take(20))
            {
                history.Children.Add(Ui.Text(e.Time + " · " + e.State, 12, true));
                history.Children.Add(Ui.Text(e.Message ?? "", 13));
            }
            if (history.Children.Count == 0)
                history.Children.Add(Ui.Text("Aucune réinitialisation enregistrée.", 13, true));
            d.Fields.Children.Add(new Expander { Header = "Historique des réinitialisations", Content = history, Margin = new Thickness(0, 16, 0, 0) });
            d.ShowDialog();
        }
    }
}
