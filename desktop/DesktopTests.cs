using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal static class DesktopTests
    {
        internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int n = 0; n < VisualTreeHelper.GetChildrenCount(parent); n++)
            {
                var child = VisualTreeHelper.GetChild(parent, n);
                yield return child;
                foreach (var nested in Descendants(child))
                    yield return nested;
            }
        }
        internal static int Run(Application app, string root)
        {
            int passed = 0;
            var report = new List<string>();
            Action<bool, string> assert = (ok, name) => { if (!ok) throw new Exception(name); passed++; report.Add("PASS " + name); };
            Directory.CreateDirectory(root);
            var dataRoot = Path.Combine(root, "data");
            string marker = Path.Combine(root, ".switcher-ui-fixture");
            if (Directory.Exists(dataRoot) && !File.Exists(marker))
                throw new InvalidOperationException("Dossier de test existant sans marqueur ; conservé.");
            File.WriteAllText(marker, "UI fixture; no real credentials");
            new Vault(dataRoot).Save(new VaultData());
            var service = new AccountService(dataRoot);
            service.Data.Profiles.Clear();
            service.Data.Instances.Clear();
            InstanceRules.Normalize(service.Data);
            service.Settings.AutoResetCredits = false;
            service.Settings.AutoRefresh = false;
            service.Settings.CodexHome = Path.Combine(dataRoot, "home");
            Directory.CreateDirectory(service.Settings.CodexHome);
            service.Vault.SaveSettings(service.Settings);
            for (int n = 0; n < 4; n++)
            {
                string auth = FakeAuth(n);
                service.Data.Profiles.Add(new Profile { Key = AuthIdentity.Parse(auth).Key, AuthJson = auth, Label = new[] { "Compte personnel", "Équipe produit", "Compte de test", "Un nom très long pour vérifier la disposition des comptes et des champs" }[n], Email = "compte-" + n + "@example.com", Plan = "Plus", QuotaTimeUtc = DateTime.UtcNow.ToString("o"), Quotas = new List<QuotaBucket> { new QuotaBucket { Name = "codex", Primary = new QuotaWindow { Minutes = 300, Remaining = 83 - n * 20 }, Secondary = new QuotaWindow { Minutes = 10080, Remaining = 67 - n * 20 } } }, ResetCredits = new ResetCredits { AvailableCount = n % 2 } });
            }
            service.Save();
            var context = new DesktopContext(dataRoot, true);
            var policy = new RelayPolicy();
            policy.Projects.Add(new RelayProject { Id = "projet-demo", Name = "Application de démonstration", Workspace = dataRoot });
            foreach (string id in new[] { "developpement", "revue" })
                context.Store.Register(new RelayChannel { Id = id, Name = id, AccountKey = id, Home = dataRoot, Workspace = dataRoot, Email = id + "@example.com", Enabled = true, AnchorThreadId = "fixture" });
            policy.Agents.Add(new RelayAgent { Channel = "revue", Description = "Relecture et validation", Capabilities = "review,test" });
            RelayPolicies.Save(context.Store, policy);
            context.Store.WriteRecord("assistance-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.dpapi",new AssistanceTicket{Id="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",Channel="developpement",Thread="fixture",Title="Préciser le périmètre avant modification",Reason="La demande contient deux interprétations possibles. Quelle option faut-il retenir ?",Context="Données fictives de recette. Le responsable doit préciser les écrans concernés.",State="pending",Created=DateTime.UtcNow.ToString("o")});
            for (int n = 0; n < 57; n++)
            {
                string id = n.ToString("x32");
                var message = new RelayMessage { Id = id, SchemaVersion = 3, Title = n == 56 ? "Correction du menu de navigation" : "Revue des modifications " + n, SourceChannelId = "developpement", SourceThreadId = "fixture", TargetChannelId = "revue", Workspace = dataRoot, State = "completed", ReturnState = "delivered", Outcome = "succeeded", Result = "Vérification terminée. La navigation et les données enregistrées sont cohérentes.", Prompt = "Relire les modifications et vérifier les cas limites.", CreatedUtc = DateTime.UtcNow.AddMinutes(-n).ToString("o"), Job = new RelayJobSpec { Project = "projet-demo" } };
                context.Store.Save(message);
            }
            var shell = new ShellWindow(context) { ShowInTaskbar = false, Opacity = 0 };
            app.MainWindow = shell;
            shell.Loaded += async delegate
            {
                try
                {
                    assert(!shell.Navigation.Items.Contains("Agents")&&!shell.Navigation.Items.Contains("Projets"),"parcours simple sans canaux ni projets dans la navigation");
                    var inspectInstance=shell.Dispatcher.BeginInvoke(new Action(()=>{
                        var form=Application.Current.Windows.OfType<EditWindow>().Single(w=>w.Title=="Votre instance Codex");
                        try{
                            assert(Descendants(form).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Compte permanent").Items.Count==5,"création propose connexion ou comptes existants");
                            assert(Descendants(form).OfType<Button>().Any(b=>Object.Equals(b.Content,"Préparer et ouvrir")),"création expose une action unique de préparation");
                            assert(!Descendants(form).OfType<TextBox>().Any(t=>System.Windows.Automation.AutomationProperties.GetName(t).Contains("canal")),"création ne demande aucun canal");
                            Capture(form,Path.Combine(root,"Creation-instance.png"));
                        }finally{form.Close();}
                    }),DispatcherPriority.ContextIdle);
                    await InstanceWizard.Open(context,shell);await inspectInstance.Task;
                    foreach (string page in shell.Pages.Keys)
                    {
                        shell.Navigate(page);
                        await shell.Pages[page].Refresh();
                        await Task.Delay(40);
                        shell.UpdateLayout();
                        assert(shell.CurrentPage == page && Object.Equals(shell.Navigation.SelectedItem, page), "page active et sidebar : " + page);
                        Capture(shell, Path.Combine(root, page + ".png"));
                    }
                    shell.Navigate("Comptes");
                    var assistance=(AssistancePage)shell.Pages["Assistance"];
                    shell.Navigate("Assistance");await assistance.Refresh();shell.UpdateLayout();
                    assert(assistance.List.Items.Count==1&&Descendants(assistance).OfType<Button>().Any(b=>Object.Equals(b.Content,"Répondre au chat")),"assistance affiche la demande et son action de réponse");
                    Exception composerError=null;
                    var inspectComposer=shell.Dispatcher.BeginInvoke(new Action(()=>{
                        var form=Application.Current.Windows.OfType<EditWindow>().Single(w=>w.Title=="Envoyer un message");
                        try{
                            var inputs=Descendants(form).OfType<TextBox>().ToArray();var chat=inputs.Single(t=>System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("Identifiant du chat"));
                            assert(!chat.IsEnabled,"nouveau chat ne demande pas d'identifiant existant");
                            var mode=Descendants(form).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Conversation");mode.SelectedIndex=1;
                            assert(chat.IsEnabled,"ciblage active l'identifiant du chat");
                            inputs.Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="Message").Text="Brouillon conservé pendant la saisie";
                            assert(form.Dirty(),"composeur détecte le brouillon");Capture(form,Path.Combine(root,"compose-message.png"));
                        }catch(Exception e){composerError=e;}finally{form.ConfirmDiscard=()=>true;form.Close();}
                    }),DispatcherPriority.ApplicationIdle);
                    await DirectComposer.Open(context,shell,null);await inspectComposer;if(composerError!=null)throw composerError;
                    shell.Navigate("Comptes");
                    await shell.Pages["Comptes"].Refresh();
                    var accounts = (AccountsPage)shell.Pages["Comptes"];
                    accounts.List.SelectedIndex = 2;
                    var id = ((ItemRow)accounts.List.SelectedItem).Id;
                    await accounts.Refresh();
                    assert(((ItemRow)accounts.List.SelectedItem).Id == id, "sélection conservée pendant actualisation");
                    var held = new TaskCompletionSource<bool>();
                    var mutation = context.Mutate(a => held.Task);
                    shell.Navigate("Instances");
                    shell.Navigate("Agents");
                    assert(shell.CurrentPage == "Agents" && Object.Equals(shell.Navigation.SelectedItem, "Agents"), "navigation réactive pendant une opération en attente");
                    held.SetResult(true);
                    await mutation;
                    await shell.Pages["Agents"].Refresh();
                    shell.Navigate("Paramètres");
                    var preferences = (PreferencesPage)shell.Pages["Paramètres"];
                    await preferences.Refresh();
                    shell.UpdateLayout();
                    var theme = Descendants(preferences).OfType<ComboBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Thème");
                    theme.SelectedItem = "Sombre";
                    preferences.ConfirmDiscard = () => false;
                    shell.Navigate("Comptes");
                    assert(shell.CurrentPage == "Paramètres" && Object.Equals(shell.Navigation.SelectedItem, "Paramètres"), "refus d'abandon des réglages conserve la page et sa sélection");
                    preferences.ConfirmDiscard = () => true;
                    shell.Navigate("Comptes");
                    assert(shell.CurrentPage == "Comptes", "abandon confirmé des réglages autorise la navigation");
                    var jobs = (JobsPage)shell.Pages["Tâches"];
                    shell.Navigate("Tâches");
                    await jobs.Refresh();
                    assert(jobs.List.Items.Count == 50, "historique paginé à 50 lignes");
                    var next = Descendants(jobs).OfType<Button>().Single(b => Object.Equals(b.Content, "Suivantes"));
                    next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(150);
                    await jobs.Refresh();
                    assert(jobs.List.Items.Count == 7, "seconde page exacte");
                    var search = Descendants(jobs).OfType<TextBox>().First(t => System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("Rechercher"));
                    search.Text = "Correction du menu";
                    await jobs.Refresh();
                    assert(jobs.List.Items.Count == 1 && !next.IsEnabled, "recherche remet à la première page et désactive Suivantes");
                    search.Text = "absent";
                    search.Text = "projet-demo";
                    await jobs.Refresh();
                    assert(jobs.List.Items.Count == 50, "dernière recherche appliquée et recherche par projet");
                    search.Text = "";
                    shell.Navigate("Comptes");
                    shell.Width = 1000;
                    shell.Height = 700;
                    await Task.Delay(40);
                    shell.UpdateLayout();
                    Capture(shell, Path.Combine(root, "compact.png"));
                    assert(shell.ActualWidth <= 1000, "fenêtre compacte");
                    shell.Theme("Sombre");
                    Capture(shell, Path.Combine(root, "dark.png"));
                    shell.Theme("Clair");
                    shell.Width = 800;
                    shell.Height = 550;
                    await Task.Delay(40);
                    shell.UpdateLayout();
                    Capture(shell, Path.Combine(root, "small.png"));
                    assert(Math.Abs(shell.ActualWidth - 800) < 1 && Math.Abs(shell.ActualHeight - 550) < 1, "fenêtre utilisable sur une petite surface logique");
                    shell.Width = 1000;
                    shell.Height = 700;
                    var source = new Dictionary<string, object> { { "outer", new Dictionary<string, object> { { "value", 12 }, { "flag", true }, { "rows", new object[] { "a", 3 } } } } };
                    var roundtrip = Json.Read<object>(Json.Write(source));
                    assert(Json.Number(Json.Get(Json.Get(roundtrip, "outer"), "value")) == 12, "JSON compatible avec objets de transport");
                    var restored = new AccountService(dataRoot);
                    assert(restored.Data.Profiles.Count == 4, "coffre DPAPI relu après écriture .NET 10");
                    var p = RelayPolicies.Load(context.Store);
                    var draft = new RelayProject { Id = "draft", Name = "Brouillon", Workspace = dataRoot };
                    var fields = new StackPanel();
                    var editor = new PolicyEditor(context.Store, p, draft, fields, true);
                    assert(!editor.Dirty, "éditeur initial propre");
                    var input = fields.Children.OfType<TextBox>().First();
                    input.Text = "Projet modifié";
                    assert(editor.Dirty, "éditeur détecte la modification");
                    editor.Apply();
                    assert(draft.Name == "Projet modifié", "éditeur applique la saisie");
                    var dialog = new EditWindow(shell, "Brouillon de test") { ShowInTaskbar = false, Opacity = 0 };
                    bool asked = false;
                    dialog.Dirty = () => true;
                    dialog.ConfirmDiscard = () => { asked = true; return false; };
                    dialog.Show();
                    dialog.Close();
                    assert(asked && dialog.IsVisible, "refus d'abandon conserve le brouillon");
                    dialog.ConfirmDiscard = () => true;
                    dialog.Close();
                    assert(!dialog.IsVisible, "abandon explicite ferme le brouillon");
                    var configDialog = new EditWindow(shell, "Projet · recette") { ShowInTaskbar = false, Opacity = 0 };
                    var draftPolicy = RelayPolicies.Load(context.Store);
                    var project = draftPolicy.Projects.Single();
                    var configEditor = new PolicyEditor(context.Store, draftPolicy, project, configDialog.Fields, false);
                    configDialog.Save = () => { configEditor.Apply(); RelayPolicies.ValidateReferences(draftPolicy, context.Store); RelayPolicies.Save(context.Store, draftPolicy); return Task.CompletedTask; };
                    configDialog.Show();
                    var nameInput = Descendants(configDialog).OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Nom");
                    nameInput.Text = "Projet enregistré par clic";
                    Descendants(configDialog).OfType<Button>().Single(b => Object.Equals(b.Content, "Enregistrer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(80);
                    assert(RelayPolicies.Load(context.Store).Projects.Single().Name == "Projet enregistré par clic", "clic Enregistrer persiste la configuration");
                    assert(configDialog.Saved && !configDialog.IsVisible, "confirmation de sauvegarde et fermeture de l'éditeur");
                    var invalid = new EditWindow(shell, "Validation de saisie") { ShowInTaskbar = false, Opacity = 0 };
                    invalid.Save = () => throw new InvalidOperationException("Valeur de test invalide");
                    invalid.Show();
                    Descendants(invalid).OfType<Button>().Single(b => Object.Equals(b.Content, "Enregistrer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(40);
                    assert(invalid.IsVisible && !invalid.Saved && Descendants(invalid).OfType<TextBlock>().Any(t => t.Text.Contains("Valeur de test invalide")), "erreur de sauvegarde visible, éditeur conservé");
                    shell.Theme("Sombre");
                    Capture(invalid, Path.Combine(root, "editor-dark.png"));
                    assert(((SolidColorBrush)invalid.Background).Color == ((SolidColorBrush)Application.Current.Resources["CanvasBrush"]).Color, "éditeur suit le thème sombre");
                    shell.Theme("Clair");
                    invalid.Close();
                    var model = RelayPolicies.Load(context.Store);
                    TemplateForm.Apply(context.Store, model, "modele-revue", "developpement", "revue", 0);
                    RelayPolicies.ValidateReferences(model, context.Store);
                    assert(model.Projects.Last().Delegation == "explicit" && !model.Rules.Last().Enabled, "modèle sans délégation automatique implicite");
                    assert(ConfigPreviewForm.Difference(RelayPolicies.Load(context.Store), model).Contains("modele-revue"), "aperçu des changements du modèle");
                    var failed = context.Store.Message(56.ToString("x32"));
                    failed.State = "failed";
                    failed.Error = "Échec contrôlé du test";
                    context.Store.Save(failed);
                    shell.Navigate("Accueil");
                    await shell.Pages["Accueil"].Refresh();
                    shell.UpdateLayout();
                    assert(Descendants(shell.Pages["Accueil"]).OfType<TextBlock>().Any(t => t.Text == failed.Title), "accueil signale aussi les tâches échouées terminées");
                    failed.State = "completed";
                    failed.Error = null;
                    context.Store.Save(failed);
                    shell.Navigate("Tâches");
                    search.Text = "aucun résultat";
                    await jobs.Refresh();
                    shell.UpdateLayout();
                    assert(jobs.List.Items.Count == 0 && Descendants(jobs).OfType<TextBlock>().Any(t => t.Text == "Aucun élément à afficher"), "état vide explicite après recherche");
                    search.Text = "";
                    await jobs.Refresh();
                    foreach (double scale in new[] { 1.25, 1.5, 2.0 })
                        Capture(shell, Path.Combine(root, "scale-" + ((int)(scale * 100)) + ".png"), scale);
                    assert(Descendants(shell).OfType<Button>().Where(b => b.IsVisible).All(b => b.Focusable), "actions accessibles au clavier");
                    report.Add(passed + " tests UI réussis");
                    File.WriteAllLines(Path.Combine(root, "results.txt"), report);
                    shell.ExitForTest();
                    app.Shutdown(0);
                }
                catch (Exception e) { report.Add("FAIL " + e); File.WriteAllLines(Path.Combine(root, "results.txt"), report); app.Shutdown(1); }
            };
            return app.Run(shell);
        }
        internal static int VerifyCompatibility(string root)
        {
            var service = new AccountService(Path.GetFullPath(root));
            var store = new RelayStore(Path.Combine(root, "relay"));
            if (service.Data.Profiles.Count != 4 || service.Data.Profiles[0].Label != "Framework → .NET 10" || RelayPolicies.Load(store).Projects.Single().Name != "Framework compatible" || store.Messages().Count != 57)
                throw new Exception("Interopérabilité inverse incorrecte");
            File.WriteAllText(Path.Combine(root, "compatibility-ok.txt"), "PASS .NET 10 relit le coffre et la configuration écrits par Framework");
            return 0;
        }
        private static void Capture(Window window, string path, double scale = 1)
        {
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var target = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using(var dc=drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(content),null,new Rect(0,0,content.ActualWidth,content.ActualHeight));
            target.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (var stream = File.Create(path))
                encoder.Save(stream);
        }
        private static string FakeAuth(int n)
        {
            string account = "fixture-" + n;
            string claims = Json.Write(new Dictionary<string, object> { { "sub", account }, { "email", "demo@example.com" }, { "https://api.openai.com/auth", new { chatgpt_account_id = account, chatgpt_plan_type = "plus" } } });
            string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Json.Write(new
            {
                auth_mode = "chatgpt",
                tokens = new
                {
                    access_token = "fictional-access-token",
                    refresh_token = "fictional-refresh",
                    id_token = "e30." + encoded + ".fake",
                    account_id = account
                }
            });
        }
    }
}
