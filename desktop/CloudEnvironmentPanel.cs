using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudEnvironmentPanel : Grid, IDisposable
    {
        private readonly DesktopContext context;
        private readonly ShellWindow shell;
        private readonly DesktopInstance owner;
        private readonly TextBlock account = Ui.Text("", 13, true);
        private readonly TextBlock status = Ui.Text("", 13, true);
        private readonly TextBlock github = Ui.Text("", 12, true);
        private readonly StackPanel details = new StackPanel();
        private readonly Button refresh, create;
        internal readonly ComboBox EnvironmentChoice = new ComboBox { DisplayMemberPath = "Name" };
        internal readonly ListBox EnvironmentList = new ListBox { DisplayMemberPath = "Name" };
        internal readonly TextBox EnvironmentSearch=new TextBox();
        internal ContextMenu ActionsMenu { get; private set; }
        internal Func<string, string, Task> CreateMissionFixture;
        private readonly Grid workspace = new Grid();
        private readonly List<CloudConfiguration> configurations = new List<CloudConfiguration>();
        private readonly List<CloudGitHubRepository> repositories = new List<CloudGitHubRepository>();
        private readonly Dictionary<string, CloudEnvironmentUpdateResult> updates = new Dictionary<string, CloudEnvironmentUpdateResult>(StringComparer.Ordinal);
        private readonly Dictionary<string, CloudEnvironmentSetupResult> setups = new Dictionary<string, CloudEnvironmentSetupResult>(StringComparer.Ordinal);
        private CancellationTokenSource operation;
        private bool busy, mutating, changing, disposed, loaded, githubFailed, composingBusy;
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private CloudConfiguration selected;
        private string email;
        internal Func<CloudBackend> ClientFactory = null;
        internal Func<CloudConfigurationCreate, string, CancellationToken, Task<CloudConfiguration>> CreateFixture = null;
        internal Func<string, string, string, CancellationToken, Task<CloudConfiguration>> RenameFixture = null;
        internal Func<string, IDictionary<string, string>, string, CancellationToken, Task<CloudEnvironmentSetupResult>> SetupStartFixture = null;
        internal Func<string, CancellationToken, Task<CloudEnvironmentSetupResult>> SetupFollowFixture = null;
        internal Func<string, CancellationToken, Task<CloudEnvironmentSetupResult>> SetupPublishFixture = null;
        internal Func<CancellationToken, Task<CloudGitHubRepositoryPage>> GitHubFixture = null;
        internal Func<CloudEnvironmentAccountChoice,CancellationToken,Task<CloudGitHubRepository[]>> WizardRepositoriesFixture;
        internal Func<CloudEnvironmentAccountChoice,CloudConfigurationCreate,string,CancellationToken,Task<CloudConfiguration>> WizardCreateFixture;
        internal Func<string,CloudDraftPatch,string,CancellationToken,Task<CloudEnvironmentUpdateResult>> UpdateFixture;

        internal CloudEnvironmentPanel(DesktopContext context, ShellWindow shell, DesktopInstance owner, string email)
        {
            this.context = context;
            this.shell = shell;
            this.owner = owner;
            this.email = email;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            var top = new StackPanel{Margin=new Thickness(0,0,0,12)};
            Children.Add(top);
            var toolbar=new DockPanel{LastChildFill=true};
            var tools=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(tools,Dock.Right);toolbar.Children.Add(tools);
            create = Ui.AsyncButton("Nouvel environnement", Create, ShowError, true);create.Margin=new Thickness(0,0,6,0);create.Padding=new Thickness(12,8,12,8);tools.Children.Add(create);
            refresh = Ui.AsyncButton("↻", () => Reload(), ShowError);refresh.Margin=new Thickness(0);refresh.Padding=new Thickness(11,8,11,8);refresh.ToolTip="Actualiser les environnements et GitHub";AutomationProperties.SetName(refresh,"Actualiser le Cloud");tools.Children.Add(refresh);
            var title=Ui.Text("Environnements",20);title.VerticalAlignment=VerticalAlignment.Center;title.Margin=new Thickness(0,0,10,0);toolbar.Children.Add(title);top.Children.Add(toolbar);
            account.Margin=new Thickness(0,7,0,2);account.FontSize=12;
            top.Children.Add(account);
            UpdateOwnerName(owner.Name, email);
            AutomationProperties.SetName(EnvironmentChoice, "Environnement Cloud de cette instance");
            AutomationProperties.SetName(status, "État de la connexion Codex Cloud");
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            status.FontSize=12;status.Margin=new Thickness(0,5,0,0);status.MaxHeight=48;top.Children.Add(status);
            AutomationProperties.SetName(github, "État des dépôts GitHub de cette instance");
            workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(185)});workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});workspace.ColumnDefinitions.Add(new ColumnDefinition());SetRow(workspace,1);Children.Add(workspace);
            var left=new DockPanel();workspace.Children.Add(left);var caption=Ui.Text("VOS ENVIRONNEMENTS",10,true);caption.Margin=new Thickness(8,0,0,10);DockPanel.SetDock(caption,Dock.Top);left.Children.Add(caption);
            var filter=Ui.SearchField(EnvironmentSearch,"Rechercher…");DockPanel.SetDock(filter,Dock.Top);left.Children.Add(filter);AutomationProperties.SetName(EnvironmentSearch,"Rechercher un environnement");EnvironmentSearch.ToolTip="Filtrer cette liste. L’environnement sélectionné reste visible.";EnvironmentSearch.TextChanged+=delegate{if(!changing)FilterCatalog();};left.Children.Add(EnvironmentList);
            AutomationProperties.SetName(EnvironmentList,"Environnements de ce compte");
            EnvironmentList.ClearValue(ItemsControl.DisplayMemberPathProperty);
            var template=new DataTemplate(typeof(CloudConfiguration));var stack=new FrameworkElementFactory(typeof(StackPanel));
            var name=new FrameworkElementFactory(typeof(TextBlock));name.SetBinding(TextBlock.TextProperty,new Binding("Name"));name.SetBinding(FrameworkElement.ToolTipProperty,new Binding("Name"));name.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);name.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);name.SetValue(FrameworkElement.MaxHeightProperty,40d);name.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);name.SetValue(TextBlock.FontSizeProperty,13d);stack.AppendChild(name);
            template.VisualTree=stack;EnvironmentList.ItemTemplate=template;
            var right=new DockPanel();SetColumn(right,2);workspace.Children.Add(right);DockPanel.SetDock(EnvironmentChoice,Dock.Top);right.Children.Add(EnvironmentChoice);
            var scroll = new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };right.Children.Add(scroll);
            SizeChanged+=delegate{bool compact=ActualWidth<720;left.Visibility=compact?Visibility.Collapsed:Visibility.Visible;workspace.ColumnDefinitions[0].Width=new GridLength(compact?0:185);workspace.ColumnDefinitions[1].Width=new GridLength(compact?0:16);EnvironmentChoice.Visibility=compact?Visibility.Visible:Visibility.Collapsed;};
            EnvironmentChoice.SelectionChanged += async delegate
            {
                if (changing || busy || composingBusy || disposed) return;
                try { await ReadSelected(); } catch (Exception error) { ShowError(error); }
            };
            EnvironmentList.SelectionChanged+=async delegate{
                if(changing||busy||composingBusy||disposed)return;
                changing=true;EnvironmentChoice.SelectedItem=EnvironmentList.SelectedItem;changing=false;
                try{await ReadSelected();}catch(Exception error){ShowError(error);}
            };
            Loaded += async delegate
            {
                if (loaded || disposed) return;
                loaded = true;
                try { await Reload(); } catch (Exception error) { ShowError(error); }
            };
            UpdateButtons();
            Render();
        }

        internal void UpdateOwnerName(string name, string accountEmail)
        {
            owner.Name = name;
            email = accountEmail;
            UpdateAccountSummary();
        }

        private void UpdateAccountSummary()
        {
            account.Text = (email ?? "Compte à connecter")+(githubFailed?" · GitHub indisponible":repositories.Count>0?" · "+repositories.Count+" dépôt(s) GitHub":"");
            account.TextTrimming=TextTrimming.CharacterEllipsis;account.TextWrapping=TextWrapping.NoWrap;
            account.ToolTip="Compte : "+(email??"À connecter")+" · Instance : "+owner.Name+(githubFailed?"\n"+github.Text:"");
        }

        private DesktopInstance Current(AccountService accounts)
        {
            var instance = accounts.Data.Instances.SingleOrDefault(value => value.Id == owner.Id && !value.Archived);
            if (instance == null || instance.AccountKey != owner.AccountKey)
                throw new InvalidOperationException("Le compte de cette instance a changé. Revenez à la liste des instances avant de continuer.");
            return instance;
        }

        private async Task<CloudBackend> Open()
        {
            if (ClientFactory != null) return ClientFactory();
            if (context.Fixture) throw new InvalidOperationException("Connexion Cloud non configurée dans cette recette.");
            return await context.Read(accounts => CloudInstanceConnection.Open(accounts, Current(accounts)));
        }

        internal async Task Reload(string preferredId = null)
        {
            if (busy || composingBusy || disposed) return;
            if (context.Fixture && ClientFactory == null)
            {
                status.Text = "Aucun appel Cloud n’est effectué avec les comptes fictifs de cette recette.";
                return;
            }
            var keepId = preferredId ?? selected?.Id;
            Begin("Lecture des environnements du compte " + email + "…");
            try
            {
                var found = new List<CloudConfiguration>();
                using (var client = await Open())
                {
                    foreach (var scope in new[] { "user", "workspace" })
                    {
                        string cursor = null;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        do
                        {
                            var page = await client.List(scope, cursor, operation.Token);
                            if (page.Data != null) found.AddRange(page.Data);
                            cursor = page.NextCursor;
                            if (!String.IsNullOrEmpty(cursor) && !seen.Add(cursor))
                                throw new InvalidOperationException("Le Cloud a renvoyé une page déjà reçue. La liste précédente est conservée.");
                            if (seen.Count >= 50)
                                throw new InvalidOperationException("Le catalogue est trop volumineux pour être chargé complètement. La liste précédente est conservée.");
                        } while (!String.IsNullOrEmpty(cursor));
                    }
                    if (disposed) return;
                    var complete=found.Where(value => !String.IsNullOrWhiteSpace(value.Id)).GroupBy(value => value.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                    var choice=complete.FirstOrDefault(value=>value.Id==keepId)??complete.FirstOrDefault();
                    var verified=choice==null?null:await client.Get(choice.Id,operation.Token);
                    if(disposed)return;
                    configurations.Clear();configurations.AddRange(complete);selected=verified;
                    changing = true;
                    EnvironmentChoice.ItemsSource = configurations.ToArray();
                    EnvironmentChoice.SelectedItem = choice;
                    FilterCatalog();
                    changing = false;
                }
                if (!context.Fixture)
                {
                    var recent = await context.Read(accounts => new { Updates = CloudEnvironmentUpdate.Recent(accounts, Current(accounts)), Setups = CloudEnvironmentSetup.Recent(accounts, Current(accounts)) });
                    updates.Clear();
                    foreach (var update in recent.Updates) if (!updates.ContainsKey(update.ConfigurationId)) updates.Add(update.ConfigurationId, update);
                    setups.Clear();
                    foreach (var setup in recent.Setups) if (!setups.ContainsKey(setup.ConfigurationId)) setups.Add(setup.ConfigurationId, setup);
                }
                await LoadRepositories();
                if (disposed) return;
                Render();
                status.Text = configurations.Count == 0
                    ? "Aucun environnement accessible à ce compte. Choisissez un dépôt GitHub pour créer le premier."
                    : configurations.Count + " environnement(s) lus sur le compte Cloud · " + DateTime.Now.ToString("HH:mm:ss");
            }
            finally { End(); }
        }

        private async Task LoadRepositories()
        {
            if (context.Fixture && GitHubFixture == null) return;
            try
            {
                var found = new List<CloudGitHubRepository>();
                if (context.Fixture) found.AddRange((await GitHubFixture(operation.Token)).Data);
                else
                {
                    using (var client = await context.Read(accounts => CloudInstanceConnection.OpenGitHub(accounts, Current(accounts))))
                    {
                        bool more = true;
                        for (int page = 1; page <= 50 && more; page++)
                        {
                            github.Text = "Lecture des dépôts GitHub du compte " + email + " · page " + page + "…";
                            var data = await client.ListRepositories(page, operation.Token);
                            found.AddRange(data.Data);
                            more = data.HasNextPage ?? data.Data.Count == 20;
                            if (page == 50 && more) throw new InvalidOperationException("Le catalogue dépasse 1 000 dépôts. La liste précédente est conservée.");
                        }
                    }
                }
                if (disposed) return;
                repositories.Clear();
                repositories.AddRange(found.GroupBy(value => value.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(value => value.FullName, StringComparer.CurrentCultureIgnoreCase));
                github.Text = repositories.Count == 0 ? "Aucun dépôt GitHub accessible à ce compte. Connectez GitHub dans Codex sur cette instance puis actualisez." : repositories.Count + " dépôt(s) GitHub accessibles au compte " + email + ". Les droits d’envoi de modifications seront vérifiés lors de chaque mission.";
                githubFailed=false;UpdateAccountSummary();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                github.Text = "Lecture GitHub impossible : " + Program.SafeError(error) + " Actualisez après avoir vérifié la connexion GitHub de ce compte. La dernière liste complète est conservée.";
                githubFailed=true;UpdateAccountSummary();
            }
        }

        private async Task ReadSelected()
        {
            if (!(EnvironmentChoice.SelectedItem is CloudConfiguration value)) return;
            changing=true;EnvironmentList.SelectedItem=configurations.FirstOrDefault(item=>item.Id==value.Id);changing=false;
            Begin("Lecture de « " + value.Name + " »…");
            try
            {
                using (var client = await Open()) selected = await client.Get(value.Id, operation.Token);
                if (disposed) return;
                FilterCatalog();
                Render();
                status.Text = "Configuration relue sur le compte " + email + ".";
            }
            catch
            {
                changing=true;var previous=configurations.FirstOrDefault(item=>item.Id==selected?.Id);EnvironmentChoice.SelectedItem=previous;EnvironmentList.SelectedItem=previous;changing=false;
                throw;
            }
            finally { End(); }
        }

        private void FilterCatalog()
        {
            bool previous=changing;changing=true;
            var rows=configurations.Where(value=>value.Id==selected?.Id||(value.Name??"").IndexOf(EnvironmentSearch.Text??"",StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();
            EnvironmentList.ItemsSource=rows;EnvironmentList.SelectedItem=rows.FirstOrDefault(value=>value.Id==selected?.Id);changing=previous;
        }

        private void Render()
        {
            if(ActionsMenu!=null)ActionsMenu.IsOpen=false;
            details.Children.Clear();
            if (selected == null)
            {
                details.Children.Add(Ui.Card(Ui.Text(configurations.Count==0?"Ajoutez votre premier environnement pour confier une mission à ce compte.":"Sélectionnez un environnement pour voir son dépôt et ses actions.", 14, true)));
                return;
            }
            var environment = selected;
            updates.TryGetValue(environment.Id, out var update);
            setups.TryGetValue(environment.Id, out var setup);
            bool initial = !environment.VersionRevision.HasValue || environment.VersionRevision.Value <= 1;
            bool updatePending=update!=null&&update.State!="completed"&&update.State!="failed";
            bool setupPending=setup!=null&&setup.State!="completed"&&setup.State!="failed";
            bool ready=!initial&&environment.Status=="ready"&&environment.Draft==null&&!String.IsNullOrEmpty(environment.LatestReadyVersionId);
            var summary = new StackPanel();
            var summaryHeader=new DockPanel();
            ActionsMenu=new ContextMenu{IsEnabled=!busy&&!composingBusy};
            var menu=Ui.Button("⋯",()=>{ActionsMenu.PlacementTarget=(UIElement)summaryHeader.Children[0];ActionsMenu.Placement=PlacementMode.Bottom;ActionsMenu.IsOpen=true;});
            menu.ContextMenu=ActionsMenu;menu.Margin=new Thickness(8,0,0,0);menu.Padding=new Thickness(11,5,11,5);menu.ToolTip="Actions de l’environnement";AutomationProperties.SetName(menu,"Actions de l’environnement");DockPanel.SetDock(menu,Dock.Right);summaryHeader.Children.Add(menu);
            var name=Ui.Text(environment.Name,20);name.Margin=new Thickness(0,0,0,7);name.FontWeight=FontWeights.SemiBold;summaryHeader.Children.Add(name);summary.Children.Add(summaryHeader);
            var badge=Ui.Text(State(environment),12,true);badge.Margin=new Thickness(0,0,0,14);if(ready)badge.SetResourceReference(TextBlock.ForegroundProperty,"AccentTextBrush");summary.Children.Add(badge);
            foreach (var repository in environment.Repositories ?? new List<CloudRepository>())
            {
                var known = repositories.FirstOrDefault(value => value.Id == repository.RepositoryId);
                summary.Children.Add(Ui.Text((known?.FullName ?? repository.RepositoryId)+"  ·  "+(String.IsNullOrEmpty(repository.Ref)?"Référence du Cloud":repository.Ref),13,true));
            }
            if(environment.Repositories==null||environment.Repositories.Count==0)summary.Children.Add(Ui.Text("Aucun dépôt associé",13,true));
            var actions=Ui.Actions(summary);actions.Margin=new Thickness(0,12,0,0);
            void Primary(string label,Func<Task> action,bool enabled=true){var button=Ui.AsyncButton(label,action,ShowError,true);button.IsEnabled=!busy&&!composingBusy&&enabled;actions.Children.Add(button);}
            if(setupPending)
            {
                if(setup.State=="ready-to-publish")Primary("Publier l’environnement",()=>FollowSetup(setup,true));
                else Primary("Suivre la préparation",()=>FollowSetup(setup,false));
                summary.Children.Add(Ui.Text(SetupState(setup),12,true));
            }
            else if(updatePending)
            {
                Primary("Suivre la publication",()=>Follow(update));
                summary.Children.Add(Ui.Text(UpdateState(update),12,true));
            }
            else if(initial)Primary("Préparer l’environnement…",()=>Setup(environment),environment.IsOwner==true&&environment.Draft==null&&String.IsNullOrEmpty(environment.ThreadId)&&setup==null);
            else if(ready)Primary("Créer une mission",()=>CreateMission(environment));
            else summary.Children.Add(Ui.Text("Actualisez cet environnement pour vérifier sa disponibilité.",13,true));
            var rename=MenuAction("Renommer l’environnement",()=>Rename(environment));rename.IsEnabled=!busy&&environment.IsOwner!=false;ActionsMenu.Items.Add(rename);
            var edit=MenuAction("Modifier et publier…",()=>Edit(environment));
            edit.IsEnabled=!busy&&!initial&&environment.IsOwner==true&&environment.Draft==null&&!updatePending;
            if(!edit.IsEnabled)edit.ToolTip=initial?"Terminez la préparation initiale avant de modifier cet environnement.":environment.Draft!=null?"Un brouillon existe déjà ; il ne sera pas remplacé.":"Ce compte doit être propriétaire et aucune publication ne doit rester à rapprocher.";
            ActionsMenu.Items.Add(edit);
            if(setupPending&&setup.State=="ready-to-publish"){var follow=MenuAction("Suivre la préparation",()=>FollowSetup(setup,false));follow.IsEnabled=!busy;ActionsMenu.Items.Add(follow);}
            details.Children.Add(Ui.Card(summary));
            // A failed former edit does not invalidate the published version. Keep its evidence out of the ready-state summary.
            if(update?.State=="failed"||setup?.State=="failed")
            {
                var previous=new StackPanel();previous.Children.Add(Ui.Text(update?.State=="failed"?UpdateState(update):SetupState(setup),12,true));
                if(ready)previous.Children.Add(Ui.Text("La version publiée reste disponible. Aucune nouvelle demande n’est envoyée automatiquement.",12,true));
                details.Children.Add(new Expander{Header="Une précédente opération nécessite votre attention",Content=previous,Margin=new Thickness(0,0,0,12)});
            }
            var configuration = new StackPanel();
            configuration.Children.Add(Ui.Text("Ces réglages préparent le poste de travail de l’agent Cloud.",12,true));
            AddReadOnly(configuration, "Installation", environment.InstallScript);
            AddReadOnly(configuration, "Dossier de travail", environment.Cwd);
            AddReadOnly(configuration, "Instructions de démarrage", environment.StartSkill);
            if(String.IsNullOrEmpty(environment.InstallScript)&&String.IsNullOrEmpty(environment.Cwd)&&String.IsNullOrEmpty(environment.StartSkill))configuration.Children.Add(Ui.Text("Aucun réglage supplémentaire renseigné.",12,true));
            details.Children.Add(new Expander{Header="Configuration de l’environnement",Content=configuration,Margin=new Thickness(0,0,0,12)});
            var diagnostic = new StackPanel();
            var githubDiagnostic=Ui.Text(github.Text,12,true);AutomationProperties.SetName(githubDiagnostic,"État des dépôts GitHub de cette instance");diagnostic.Children.Add(githubDiagnostic);
            diagnostic.Children.Add(Ui.Text("Identifiant : " + environment.Id, 12, true));
            if (!String.IsNullOrEmpty(environment.VersionId)) diagnostic.Children.Add(Ui.Text("Version : " + environment.VersionId, 12, true));
            if (!String.IsNullOrEmpty(environment.EnvironmentId)) diagnostic.Children.Add(Ui.Text("Exécution : " + environment.EnvironmentId, 12, true));
            if (update != null){diagnostic.Children.Add(Ui.Text("Publication : " + update.Id + " · étape : " + update.Stage, 12, true));diagnostic.Children.Add(Ui.Text(UpdateState(update),12,true));}
            if (setup != null)
            {
                diagnostic.Children.Add(Ui.Text("Préparation : " + setup.Id + " · étape : " + setup.Stage, 12, true));
                if (!String.IsNullOrEmpty(setup.ThreadId)) diagnostic.Children.Add(Ui.Text("Conversation de préparation : " + setup.ThreadId, 12, true));
            }
            details.Children.Add(new Expander { Header = "Diagnostic et identifiants", Content = diagnostic });
        }

        private MenuItem MenuAction(string label,Func<Task> action)
        {
            var item=new MenuItem{Header=label};AutomationProperties.SetName(item,label);
            item.Click+=async delegate{if(!item.IsEnabled)return;item.IsEnabled=false;try{await action();}catch(Exception error){ShowError(error);}finally{item.IsEnabled=true;}};
            return item;
        }

        private async Task CreateMission(CloudConfiguration environment)
        {
            if(busy||composingBusy||disposed)return;
            if(context.Fixture){if(CreateMissionFixture!=null)await CreateMissionFixture(owner.Id,environment.Id);return;}
            var jobs=new CloudJobsWindow(context,shell);jobs.Show();await jobs.StartNewMission(owner.Id,environment.Id);
        }

        internal void SetFixtureUpdate(CloudEnvironmentUpdateResult update)
        {
            if(!context.Fixture)throw new InvalidOperationException("Cette simulation est réservée à la recette locale.");
            updates[update.ConfigurationId]=update;Render();
        }

        private static void AddReadOnly(Panel panel, string label, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            panel.Children.Add(Ui.Text(label, 12, true));
            panel.Children.Add(new TextBox { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private static string State(CloudConfiguration environment)
        {
            if (environment.Draft != null) return "Brouillon de modification enregistré · publication non confirmée";
            if (environment.VersionRevision.HasValue && environment.VersionRevision.Value <= 1) return "Brouillon enregistré · préparation à terminer";
            if (!String.IsNullOrEmpty(environment.LatestReadyVersionId)) return environment.Status=="ready"?"Prêt · version publiée disponible":"Version publiée · disponibilité à vérifier";
            return "Configuration enregistrée" + (environment.VersionRevision.HasValue ? " · révision " + environment.VersionRevision.Value : "") + " · disponibilité d’exécution non vérifiée";
        }

        private static string UpdateState(CloudEnvironmentUpdateResult update)
        {
            if (update.State == "completed") return "Dernière publication vérifiée sur le compte Cloud.";
            if (update.State == "publishing") return "Publication en cours · actualisez son suivi pour connaître le résultat.";
            if (update.State == "verifying") return "Publication envoyée · disponibilité de la nouvelle version encore à vérifier.";
            if (update.State == "failed") return "Publication arrêtée · " + (update.FailureCode ?? "échec") + ". Actualisez avant une nouvelle modification.";
            return "Résultat de publication incertain · vérifiez son suivi avant toute nouvelle demande. Aucun nouvel envoi automatique.";
        }

        private static string SetupState(CloudEnvironmentSetupResult setup)
        {
            if (setup.State == "completed") return "Environnement préparé et publication vérifiée sur ce compte Cloud.";
            if (setup.State == "ready-to-publish") return "Le chat de préparation a terminé son tour. Vérifiez son résultat dans Codex avant de publier l’environnement.";
            if (setup.State == "awaiting-input") return "Le chat de préparation attend une réponse dans Codex. Reprenez-le sur ce compte avant de poursuivre.";
            if (setup.State == "awaiting-approval") return "Le chat de préparation attend une approbation dans Codex. Le Switcher ne peut pas la donner à votre place.";
            if (setup.State == "publishing") return "Publication de l’environnement en cours · consultez son suivi.";
            if (setup.State == "verifying") return "Publication envoyée · nouvelle version prête encore à vérifier.";
            if (setup.State == "running") return "Préparation en cours dans un chat Codex Cloud de ce compte · consultez son suivi.";
            if (setup.State == "failed") return "Préparation arrêtée · " + (setup.FailureCode ?? "échec") + ". Vérifiez le chat avant une nouvelle demande.";
            return "Résultat de préparation incertain · consultez le suivi. Aucun chat ni message ne sera recréé automatiquement.";
        }

        private async Task Setup(CloudConfiguration environment)
        {
            if (busy || composingBusy || disposed) return;
            var configured = environment.Repositories ?? new List<CloudRepository>();
            if (configured.Count == 0) throw new InvalidOperationException("Choisissez d’abord un dépôt pour cet environnement Cloud.");
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var repository in configured)
            {
                var known = repositories.SingleOrDefault(value => value.Id == repository.RepositoryId);
                if (known == null) throw new InvalidOperationException("Ce dépôt n’a pas été retrouvé dans le catalogue GitHub de ce compte. Actualisez avant de préparer l’environnement.");
                names.Add(repository.RepositoryId, known.FullName);
            }
            var form = new EditWindow(shell, "Préparer l’environnement Codex Cloud", "Démarrer la préparation");
            form.Fields.Children.Add(Ui.Text("Compte : " + email + " · Instance : " + owner.Name, 13, true));
            form.Fields.Children.Add(Ui.Text("Un nouveau chat Cloud préparera cet environnement. La publication restera une action séparée. Les dépôts ci-dessous ont été retrouvés dans le catalogue GitHub de ce compte.", 13, true));
            foreach (var repository in configured) form.Fields.Children.Add(Ui.Text(names[repository.RepositoryId] + " · " + repository.Ref, 14));
            string operationId = Guid.NewGuid().ToString("N");
            CloudEnvironmentSetupResult outcome = null;
            form.Save = async () =>
            {
                Begin("Création du chat de préparation Cloud…", true, TimeSpan.FromMinutes(3));
                try
                {
                    if (context.Fixture && SetupStartFixture != null) outcome = await SetupStartFixture(environment.Id, names, operationId, operation.Token);
                    else await context.Mutate(async accounts => { outcome = await CloudEnvironmentSetup.Start(accounts, Current(accounts), environment.Id, names, operationId, operation.Token); });
                    setups[environment.Id] = outcome;
                }
                catch
                {
                    setups[environment.Id] = new CloudEnvironmentSetupResult { Id = operationId, ConfigurationId = environment.Id, State = "uncertain", Stage = "À vérifier" };
                    ReconcileRequired(form, operationId);
                    throw;
                }
                finally { End(); }
            };
            form.ShowDialog();
            if (!form.Saved || outcome == null || disposed) return;
            await Reload(environment.Id);
            status.Text = SetupState(outcome);
        }

        private async Task FollowSetup(CloudEnvironmentSetupResult setup, bool publish)
        {
            if (busy || composingBusy || disposed) return;
            Begin(publish ? "Demande de publication de l’environnement Cloud…" : "Lecture du suivi de préparation Cloud…", true, TimeSpan.FromMinutes(15));
            CloudEnvironmentSetupResult outcome = null;
            try
            {
                if (context.Fixture && (publish ? SetupPublishFixture != null : SetupFollowFixture != null))
                    outcome = await (publish ? SetupPublishFixture(setup.Id, operation.Token) : SetupFollowFixture(setup.Id, operation.Token));
                else await context.Mutate(async accounts => { outcome = publish ? await CloudEnvironmentSetup.Publish(accounts, Current(accounts), setup.Id, operation.Token) : await CloudEnvironmentSetup.Follow(accounts, Current(accounts), setup.Id, operation.Token); });
                setups[setup.ConfigurationId] = outcome;
                if (outcome.Configuration != null && selected?.Id == outcome.ConfigurationId) selected = outcome.Configuration;
                status.Text = SetupState(outcome);
            }
            finally { End(); }
        }

        private async Task Edit(CloudConfiguration environment)
        {
            if (busy || composingBusy || disposed) return;
            if(environment.IsOwner!=true||environment.Draft!=null||!environment.VersionRevision.HasValue||environment.VersionRevision.Value<=1)
                throw new InvalidOperationException("Cet environnement doit être publié, sans brouillon, et appartenir au compte de cette instance avant une modification.");
            composingBusy=true;UpdateButtons();
            CloudEnvironmentUpdateResult outcome=null;
            try
            {
                var choices=await WizardAccounts(true);
                if(disposed)return;
                var form=new CloudEnvironmentWizard(shell,choices,LoadWizardRepositories,async(intent,token)=>
                {
                    ValidateEditIntent(intent,environment);
                    await VerifyWizardAccount(intent.Account);
                    Begin("Enregistrement et préparation de la publication Cloud…",true,TimeSpan.FromMinutes(15));
                    using(var linked=CancellationTokenSource.CreateLinkedTokenSource(operation.Token,token))
                    try
                    {
                        if(context.Fixture){if(UpdateFixture==null)throw new InvalidOperationException("Modification Cloud non configurée dans cette recette.");outcome=await UpdateFixture(environment.Id,intent.Patch,intent.Id,linked.Token);}
                        else await context.Mutate(async accounts=>{outcome=await CloudEnvironmentUpdate.Update(accounts,Pinned(accounts,intent.Account),environment.Id,intent.Patch,intent.Id,linked.Token);});
                        updates[environment.Id]=outcome;
                    }
                    catch(Exception error)
                    {
                        var failure=MutationFailure(error);
                        updates[environment.Id]=new CloudEnvironmentUpdateResult{Id=intent.Id,ConfigurationId=environment.Id,State=failure is CloudBackendUncertainException?"uncertain":"failed",Stage="À vérifier",FailureCode=failure.Code,HttpStatus=failure.HttpStatus,RequestId=failure.RequestId};
                        status.Text=UpdateState(updates[environment.Id]);throw failure;
                    }
                    finally{End();}
                },lifetime.Token,owner.Id,environment);
                form.ShowDialog();
                if(!form.Saved||outcome==null||disposed)return;
            }
            finally{composingBusy=false;if(!disposed){UpdateButtons();Render();}}
            await Reload(environment.Id);
            status.Text=UpdateState(outcome);
        }

        private void ValidateEditIntent(CloudEnvironmentWizardIntent intent,CloudConfiguration environment)
        {
            if(intent?.Account==null||intent.Account.Id!=owner.Id||intent.Account.AccountKey!=owner.AccountKey||intent.ConfigurationId!=environment.Id||intent.Patch==null||intent.Create!=null||intent.Patch.BaseVersionId!=environment.VersionId||intent.Patch.ExpectedRevision!=0)
                throw new InvalidOperationException("Le compte, l’environnement ou la version de cette modification ont changé. Fermez l’assistant et actualisez avant une nouvelle demande.");
        }

        private async Task Follow(CloudEnvironmentUpdateResult update)
        {
            if (busy || composingBusy || disposed) return;
            Begin("Vérification de la publication Cloud…", true, TimeSpan.FromMinutes(15));
            CloudEnvironmentUpdateResult outcome = null;
            try
            {
                await context.Mutate(async accounts => { outcome = await CloudEnvironmentUpdate.Follow(accounts, Current(accounts), update.Id, operation.Token); });
                updates[update.ConfigurationId] = outcome;
                if (outcome.Configuration != null && selected?.Id == outcome.ConfigurationId) selected = outcome.Configuration;
                status.Text = UpdateState(outcome);
            }
            finally { End(); }
        }

        private async Task<CloudEnvironmentAccountChoice[]> WizardAccounts(bool edit)
        {
            return await context.Read(accounts=>accounts.Data.Instances.Where(instance=>!instance.Archived&&!String.IsNullOrEmpty(instance.AccountKey)&&(!edit||instance.Id==owner.Id&&instance.AccountKey==owner.AccountKey))
                .Select(instance=>new CloudEnvironmentAccountChoice{Id=instance.Id,AccountKey=instance.AccountKey,Label=instance.Name+" · "+(accounts.Data.Profiles.FirstOrDefault(profile=>profile.Key==instance.AccountKey)?.Email??"Compte associé")}).ToArray());
        }

        private static DesktopInstance Pinned(AccountService accounts,CloudEnvironmentAccountChoice choice)
        {
            var instance=choice==null?null:accounts.Data.Instances.SingleOrDefault(value=>value.Id==choice.Id&&!value.Archived);
            if(instance==null||String.IsNullOrEmpty(choice.AccountKey)||instance.AccountKey!=choice.AccountKey)
                throw new InvalidOperationException("Le compte associé à cette instance a changé. Fermez l’assistant et rechargez les comptes avant une nouvelle demande.");
            return instance;
        }

        private Task<bool> VerifyWizardAccount(CloudEnvironmentAccountChoice choice)=>context.Read(accounts=>{Pinned(accounts,choice);return true;});

        private async Task<CloudGitHubRepository[]> LoadWizardRepositories(CloudEnvironmentAccountChoice choice,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();await VerifyWizardAccount(choice);
            CloudGitHubRepository[] found;
            if(context.Fixture)
            {
                if(WizardRepositoriesFixture!=null)found=await WizardRepositoriesFixture(choice,token);
                else if(choice.Id==owner.Id&&GitHubFixture!=null)found=(await GitHubFixture(token)).Data.ToArray();
                else throw new InvalidOperationException("Catalogue GitHub de ce compte non configuré dans cette recette.");
            }
            else
            {
                var repositoriesForAccount=new List<CloudGitHubRepository>();
                using(var client=await context.Read(accounts=>CloudInstanceConnection.OpenGitHub(accounts,Pinned(accounts,choice))))
                {
                    bool more=true;
                    for(int page=1;page<=50&&more;page++)
                    {
                        token.ThrowIfCancellationRequested();var result=await client.ListRepositories(page,token);repositoriesForAccount.AddRange(result.Data);
                        more=result.HasNextPage??result.Data.Count==20;
                        if(page==50&&more)throw new InvalidOperationException("Le catalogue dépasse 1 000 dépôts. Réduisez les accès de cette connexion GitHub avant de continuer.");
                    }
                }
                found=repositoriesForAccount.ToArray();
            }
            token.ThrowIfCancellationRequested();await VerifyWizardAccount(choice);
            return found.GroupBy(value=>value.Id,StringComparer.Ordinal).Select(group=>group.First()).OrderBy(value=>value.FullName,StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private async Task Create()
        {
            if(busy||composingBusy||disposed)return;
            composingBusy=true;UpdateButtons();
            CloudConfiguration created=null;CloudEnvironmentAccountChoice target=null;
            try
            {
                var choices=await WizardAccounts(false);
                if(disposed)return;
                if(choices.Length==0)throw new InvalidOperationException("Connectez un compte permanent à une instance avant de créer un environnement Cloud.");
                var preferences=await context.Read(accounts=>context.Store.ReadRecord<CloudEnvironmentPreferences>(CloudEnvironmentPreferences.Record));
                if(disposed)return;
                var form=new CloudEnvironmentWizard(shell,choices,LoadWizardRepositories,async(intent,token)=>
                {
                    if(intent?.Account==null||intent.Create==null||intent.Patch!=null||!String.IsNullOrEmpty(intent.ConfigurationId))throw new InvalidOperationException("La création de cet environnement n’est plus cohérente. Fermez cet assistant et préparez une nouvelle demande.");
                    await VerifyWizardAccount(intent.Account);target=intent.Account;
                    Begin("Création du brouillon Cloud sur « "+target.Label+" »…",true);
                    using(var linked=CancellationTokenSource.CreateLinkedTokenSource(operation.Token,token))
                    try
                    {
                        if(context.Fixture)
                        {
                            if(WizardCreateFixture!=null)created=await WizardCreateFixture(target,intent.Create,intent.Id,linked.Token);
                            else if(target.Id==owner.Id&&CreateFixture!=null)created=await CreateFixture(intent.Create,intent.Id,linked.Token);
                            else throw new InvalidOperationException("Création Cloud de ce compte non configurée dans cette recette.");
                        }
                        else await context.Mutate(async accounts=>{created=await CloudCommands.Create(accounts,Pinned(accounts,target),intent.Create,intent.Id,linked.Token);});
                    }
                    catch(Exception error)
                    {
                        var failure=MutationFailure(error);
                        status.Text=failure is CloudBackendUncertainException?"Enregistrement incertain sur « "+target.Label+" » · relisez les environnements de ce compte avant toute nouvelle création · "+intent.Id:"Enregistrement arrêté sur « "+target.Label+" » · vérifiez ce compte avant une nouvelle demande · "+intent.Id;
                        throw failure;
                    }
                    finally{End();}
                },lifetime.Token,owner.Id,defaultNetwork:preferences.NewEnvironmentNetwork);
                form.ShowDialog();
                if(!form.Saved||created==null||disposed)return;
            }
            finally{composingBusy=false;if(!disposed){UpdateButtons();Render();}}
            if(target.Id==owner.Id&&target.AccountKey==owner.AccountKey)
            {
                await Reload(created.Id);
                status.Text="Brouillon « "+created.Name+" » créé et relu sur le compte "+email+". Aucun agent n’a été lancé.";
            }
            else status.Text="Brouillon « "+created.Name+" » créé sur « "+target.Label+" ». Sélectionnez cette instance pour le préparer. Le compte affiché ici reste inchangé.";
        }

        private static CloudBackendException MutationFailure(Exception error)
        {
            if(error is CloudBackendUncertainException uncertain)return uncertain;
            var native=error as CloudBackendException;
            // Known refusals remain failures. After entering a mutation, a local error or
            // incompatible success response cannot prove that the server made no change.
            bool successStatus=native?.HttpStatus>=200&&native.HttpStatus<300;
            bool transientStatus=native?.HttpStatus==408||native?.HttpStatus>=500;
            if(native!=null&&native.Code!="invalid-response"&&!successStatus&&!transientStatus)return native;
            return new CloudBackendUncertainException(native?.Code??"erreur locale après le début de l’opération",native?.HttpStatus,native?.RequestId);
        }

        private async Task Rename(CloudConfiguration environment)
        {
            if (busy || composingBusy || disposed) return;
            var form = new EditWindow(shell, "Renommer l’environnement Cloud") { Height = 340, MinHeight = 340 };
            form.Fields.Children.Add(Ui.Text("Compte : " + email, 13, true));
            var name = Ui.Input("Nom de l’environnement", environment.Name, form.Fields);
            name.MaxLength = 256;
            form.Dirty = () => name.Text != environment.Name;
            string operationId = Guid.NewGuid().ToString("N");
            form.Save = async () =>
            {
                string label = name.Text.Trim();
                if (label.Length == 0) throw new InvalidOperationException("Saisissez un nom.");
                name.IsReadOnly = true;
                Begin("Enregistrement du nom dans Codex Cloud…", true);
                try
                {
                    if (context.Fixture && RenameFixture != null) await RenameFixture(environment.Id, label, operationId, operation.Token);
                    else await context.Mutate(async accounts => { await CloudCommands.Rename(accounts, Current(accounts), environment.Id, label, operationId, operation.Token); });
                }
                catch (CloudBackendUncertainException)
                {
                    ReconcileRequired(form, operationId);
                    throw;
                }
                catch
                {
                    RequestStopped(form, operationId);
                    throw;
                }
                finally { End(); }
            };
            form.ShowDialog();
            if (!form.Saved || disposed) return;
            await Reload(environment.Id);
            status.Text = "Nom enregistré et relu sur le compte Cloud " + email + ".";
        }

        private void ReconcileRequired(EditWindow form, string operationId)
        {
            form.SaveButton.Visibility = Visibility.Collapsed;
            form.Fields.Children.Add(Ui.Text("Le serveur n’a pas confirmé le résultat. Fermez cette fenêtre puis actualisez les environnements avant toute nouvelle demande. Référence de suivi : " + operationId, 13));
            status.Text = "Enregistrement incertain · actualisez et rapprochez le résultat avant de recommencer · " + operationId;
        }

        private void RequestStopped(EditWindow form, string operationId)
        {
            form.SaveButton.Visibility = Visibility.Collapsed;
            form.Fields.Children.Add(Ui.Text("L’enregistrement a été arrêté. Fermez cette fenêtre et actualisez le Cloud avant de préparer une nouvelle demande corrigée. Cette intention ne sera pas renvoyée. Référence de suivi : " + operationId, 13));
            status.Text = "Enregistrement arrêté · vérifiez le résultat et actualisez avant une nouvelle demande · " + operationId;
        }

        private void Begin(string message, bool mutation = false, TimeSpan? timeout = null)
        {
            busy = true;
            mutating = mutation;
            operation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
            status.Text = message;
            UpdateButtons();
        }

        private void End()
        {
            operation?.Dispose();
            operation = null;
            busy = false;
            mutating = false;
            if (!disposed) { UpdateButtons(); Render(); }
        }

        private void UpdateButtons()
        {
            refresh.IsEnabled = !busy&&!composingBusy;
            create.IsEnabled = !busy&&!composingBusy;
            create.ToolTip = "Choisissez un compte ; ses dépôts GitHub seront chargés automatiquement dans l’assistant.";
            EnvironmentChoice.IsEnabled = !busy&&!composingBusy && configurations.Count > 0;
            EnvironmentList.IsEnabled=EnvironmentChoice.IsEnabled;
            EnvironmentSearch.IsEnabled=!busy&&!composingBusy;
            if(ActionsMenu!=null)ActionsMenu.IsEnabled=!busy&&!composingBusy;
        }

        private void ShowError(Exception error)
        {
            if (disposed) return;
            var native = error as CloudBackendException;
            var uncertain = error is CloudBackendUncertainException;
            status.Text = uncertain
                ? "Résultat de l’enregistrement incertain. Actualisez avant de refaire cette action ; aucun nouvel envoi automatique n’est effectué."
                : "Codex Cloud : " + Program.SafeError(error);
            if (native != null)
            {
                status.Text += " · " + native.Code;
                if (native.HttpStatus.HasValue) status.Text += " · HTTP " + native.HttpStatus.Value;
                if (!String.IsNullOrEmpty(native.RequestId)) status.Text += " · diagnostic " + native.RequestId;
            }
            status.ToolTip=status.Text;
        }

        internal bool CanLeave()
        {
            if(composingBusy){status.Text="Terminez ou fermez l’assistant Cloud avant de changer d’instance ou d’onglet.";return false;}
            if (!busy) return true;
            if (!mutating) { operation?.Cancel(); return true; }
            status.Text = "Une opération Cloud est en cours. Attendez son résultat avant de changer d’instance ou d’onglet.";
            return false;
        }

        public void Dispose()
        {
            if(disposed)return;
            disposed = true;
            lifetime.Cancel();
            lifetime.Dispose();
            operation?.Cancel();
        }
    }
}
