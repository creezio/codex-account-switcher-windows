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
        private sealed class PluginFixtureConnection:IToolTunnelConnection
        {
            internal static readonly TunnelTool[] Tools={
                new TunnelTool{Server="private_mcp",Name="records.list",Title="Consulter les dossiers",Group="Outils de l’équipe",ReadOnly=true,Description="Afficher les dossiers accessibles au compte.",Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{}}")},
                new TunnelTool{Server="private_mcp",Name="records.update",Title="Modifier un dossier",Group="Outils de l’équipe",ReadOnly=false,Description="Modifier les informations du dossier choisi.",Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{}}")},
                new TunnelTool{Server="codex_apps",Name="sites.get_site",Group="Sites",ReadOnly=true,Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"}}}")}
            };
            public Task<TunnelTool[]> Inventory(System.Threading.CancellationToken token)=>Task.FromResult(Json.Read<TunnelTool[]>(Json.Write(Tools)));
            public Task<object> Invoke(string server,string tool,object args,System.Threading.CancellationToken token)=>throw new Exception("UI must not invoke provider actions");
            public void Dispose(){}
        }
        private static async Task<EditWindow> WaitDialog(string title)
        {
            for(int attempt=0;attempt<500;attempt++){
                var form=Application.Current.Windows.OfType<EditWindow>().FirstOrDefault(w=>w.Title==title&&w.IsLoaded);
                if(form!=null)return form;await Task.Delay(10);
            }
            throw new Exception("Dialog did not load: "+title);
        }
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
        private static async Task Until(Func<bool> condition)
        {
            for(int attempt=0;attempt<500;attempt++){if(condition())return;await Task.Delay(10);}
            throw new Exception("UI condition timed out");
        }
        private static async Task InstanceActions(DesktopContext context,ShellWindow shell,InstancesPage instances,InstanceResourcesView resources,DesktopInstance owner,DesktopInstance client,string root,Action<bool,string> assert)
        {
            Button RenameButton()=>Descendants(instances).OfType<Button>().Single(b=>Object.Equals(b.Content,"Renommer l’instance"));
            Button VerifyButton(){instances.UpdateLayout();return Descendants(instances).OfType<Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Vérifier l’intégration");}
            TextBlock Result()=>Descendants(instances).OfType<TextBlock>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="Résultat de la vérification");
            async Task RenameTo(string value,bool validate)
            {
                var done=new TaskCompletionSource<bool>();
                _=shell.Dispatcher.BeginInvoke(new Action(async()=>{
                    EditWindow form=null;
                    try {
                        form=await WaitDialog("Renommer l’instance");var input=Descendants(form).OfType<TextBox>().Single();
                        if(validate){
                            input.Text=" ";form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>form.SaveButton.IsEnabled);
                            assert(!form.Saved&&form.IsVisible&&Descendants(form).OfType<TextBlock>().Any(t=>t.Text.Contains("non valide")),"renommage vide refusé dans le formulaire ouvert");
                            input.Text=client.Name;form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>form.SaveButton.IsEnabled);
                            assert(!form.Saved&&input.Text==client.Name&&Descendants(form).OfType<TextBlock>().Any(t=>t.Text.Contains("déjà utilisé")),"nom déjà pris expliqué sans fermer ni effacer la saisie");
                        }
                        input.Text=value;Capture(form,Path.Combine(root,"Renommer-instance.png"));form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>form.Saved);done.SetResult(true);
                    }catch(Exception e){done.SetException(e);}finally{if(form!=null&&!form.Saved){form.ConfirmDiscard=()=>true;form.Close();}}
                }),DispatcherPriority.ContextIdle);
                RenameButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await done.Task;
                await Until(()=>instances.List.Items.Cast<ItemRow>().Any(r=>r.Id==owner.Id&&r.Title==value.Trim()));shell.UpdateLayout();
            }
            var before=await context.Read(a=>new{Home=a.Instances.Home(a.Data.Instances.Single(i=>i.Id==owner.Id)),Account=a.Data.Instances.Single(i=>i.Id==owner.Id).AccountKey});
            await RenameTo("  Atelier renommé  ",true);
            var renamed=new AccountService(context.Root);var saved=renamed.Data.Instances.Single(i=>i.Id==owner.Id);
            assert(saved.Name=="Atelier renommé"&&saved.AccountKey==before.Account&&renamed.Instances.Home(saved)==before.Home,"renommage persistant conserve le compte et le dossier");
            assert(resources.Dirty&&ReferenceEquals(resources,Descendants(instances).OfType<InstanceResourcesView>().Single())&&Descendants(instances).OfType<TextBlock>().Any(t=>t.Text=="Atelier renommé"),"titre et liste actualisés sans perdre le brouillon des accès");
            for(int index=0;index<3;index++){instances.ShowTab(index);shell.UpdateLayout();assert(RenameButton().IsVisible&&RenameButton().IsEnabled,"renommer accessible dans l’onglet "+index);}
            instances.ShowTab(0);shell.UpdateLayout();
            assert(Descendants(instances).OfType<TextBlock>().Any(t=>t.Text.Contains("Délègue cette mission à Atelier renommé")),"exemple de délégation utilise le nouveau nom");
            var cancelRename=new TaskCompletionSource<bool>();
            _=shell.Dispatcher.BeginInvoke(new Action(async()=>{
                try{var form=await WaitDialog("Renommer l’instance");Descendants(form).OfType<TextBox>().Single().Text="Changement abandonné";form.ConfirmDiscard=()=>true;Descendants(form).OfType<Button>().Single(b=>Object.Equals(b.Content,"Annuler")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));cancelRename.SetResult(true);}catch(Exception e){cancelRename.SetException(e);}
            }),DispatcherPriority.ContextIdle);
            RenameButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await cancelRename.Task;
            assert(new AccountService(context.Root).Data.Instances.Single(i=>i.Id==owner.Id).Name=="Atelier renommé","annuler le renommage ne modifie pas le nom enregistré");
            await RenameTo(owner.Name,false);

            var held=new TaskCompletionSource<bool>();int calls=0;
            context.VerifyIntegrationFixture=async(home,settings)=>{calls++;await held.Task;context.Store.WriteRecord("integration-"+RelayIntegration.HomeKey(home)+".dpapi",new RelayIntegrationState{Home=home,Healthy=true,Status="Intégration installée et vérifiée",Updated=DateTime.UtcNow.ToString("o"),InstalledVersion=RelayWorker.Version});};
            var accountScroll=Descendants(instances).OfType<ScrollViewer>().Single(s=>s.Tag is string);accountScroll.ScrollToBottom();shell.UpdateLayout();double offset=accountScroll.VerticalOffset;
            VerifyButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));shell.UpdateLayout();
            assert(Result().Text.StartsWith("Vérification en cours")&&!VerifyButton().IsEnabled,"clic montre immédiatement la progression et désactive le bouton");
            assert(offset>0&&accountScroll.VerticalOffset>=offset-1,"vérification conserve le défilement de la fiche");
            await Until(()=>calls==1);await instances.Refresh();shell.UpdateLayout();VerifyButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(calls==1&&!VerifyButton().IsEnabled,"actualisation et double clic ne relancent pas une vérification");
            instances.ShowTab(1);shell.UpdateLayout();assert(Result().Text.StartsWith("Vérification en cours")&&resources.Dirty,"progression visible dans Ressources sans perdre les modifications");
            held.SetResult(true);await Until(()=>Result().Text.StartsWith("Vérification réussie"));instances.ShowTab(0);shell.UpdateLayout();await Until(()=>VerifyButton().IsEnabled);
            assert(Descendants(instances).OfType<TextBlock>().Any(t=>t.Text.StartsWith("Dernière vérification :")&&t.Text.Contains(RelayWorker.Version)),"succès avec heure et version persistées");
            VerifyButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>calls==2);await Until(()=>Result().Text.StartsWith("Vérification réussie")&&VerifyButton().IsEnabled);
            assert(calls==2,"nouveau clic confirmé même lorsque l’intégration est déjà saine");
            Capture(shell,Path.Combine(root,"Instance-verification.png"));

            context.VerifyIntegrationFixture=(home,settings)=>{settings.CodexExecutable=Path.Combine(root,"codex-inexistant.exe");return IntegrationMaintenance.Check(context.Store,home,settings,true,true,System.Threading.CancellationToken.None);};
            VerifyButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>Result().Text.StartsWith("Intégration à corriger")&&VerifyButton().IsEnabled);
            assert(!RelayIntegration.Status(context.Store,before.Home).Healthy&&Result().Text.Length>40,"échec enregistré par la maintenance affiché sans faux succès");
            context.VerifyIntegrationFixture=(home,settings)=>throw new InvalidOperationException("Erreur de contrôle de recette");
            VerifyButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>Result().Text.Contains("Erreur de contrôle de recette")&&VerifyButton().IsEnabled);
            assert(Result().Text.StartsWith("Échec de la vérification"),"exception expliquée et nouvelle tentative possible");
            instances.ShowTab(1);shell.UpdateLayout();assert(resources.Dirty,"succès et échecs de vérification préservent les accès non enregistrés");
        }
        internal static int Run(Application app, string root)
        {
            int passed = 0;
            var report = new List<string>();
            Action<bool, string> assert = (ok, name) => { File.AppendAllText(Path.Combine(root,"progress.txt"),(ok?"PASS ":"FAIL ")+name+"\n"); if (!ok) throw new Exception(name); passed++; report.Add("PASS " + name); };
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
            var toolOwner=service.Instances.Create("Propriétaire démo");service.Instances.BindAccount(toolOwner,service.Data.Profiles[0]);
            var toolClient=service.Instances.Create("Développement démo");service.Instances.BindAccount(toolClient,service.Data.Profiles[1]);
            var context = new DesktopContext(dataRoot, true);
            var contract=CompatibilitySmoke.Contract();contract.Signature=ToolTunnel.Signature(contract);context.Store.WriteRecord("compat-contract.dpapi",contract);
            var toolFixture=new TunnelGrant{Id="cccccccccccccccccccccccccccccccc",Name="Pages via Propriétaire démo",Instance=toolOwner.Id,InstanceName=toolOwner.Name,Account=toolOwner.AccountKey,Home=service.Instances.Home(toolOwner),Enabled=true,Revision="fixture",ResourceField="page_id",ResourceValue="page_demo",Sources=new Dictionary<string,string>{{service.Instances.Home(toolClient),toolClient.AccountKey}},Tools=new List<TunnelTool>{new TunnelTool{Server="codex_apps",Name="chatgpt_space.read_page",Group="Pages",ReadOnly=true,Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"page_id\":{\"type\":\"string\"}}}")}}};
            context.Store.WriteRecord("tool-grants.dpapi",new List<TunnelGrant>{toolFixture});
            var pagesKey=InstanceResources.PluginKey(toolFixture.Tools[0]);
            var catalog=new InstanceInventory{Instance=toolOwner.Id,Account=toolOwner.AccountKey,Home=service.Instances.Home(toolOwner),Updated=DateTime.UtcNow.ToString("o")};
            catalog.Plugins.Add(new InstancePlugin{Key=pagesKey,Name="Pages",Tools=12,State="ready"});
            catalog.Plugins.Add(new InstancePlugin{Key="sites",Name="Sites",Tools=35,State="ready"});
            catalog.Plugins.Add(new InstancePlugin{Key=InstanceResources.PluginKey(PluginFixtureConnection.Tools[0]),Name="Outils de l’équipe",Tools=2,State="unsupported",Message="Ce plugin ne propose pas de catalogue de ressources. Choisissez les opérations à partager."});
            foreach(var r in new[]{new InstanceResource{Title="Feuille de route produit",Kind="Page",Plugin=pagesKey,Field="page_id",Value="page_roadmap",CanModify=true,Access="Modification autorisée"},new InstanceResource{Title="Portail client",Kind="Site",Plugin="sites",Field="project_id",Value="site_portal",CanModify=true,Access="Propriétaire"},new InstanceResource{Title="Budget prévisionnel",Kind="Tableur",Plugin=pagesKey,Field="page_id",Value="page_sheet",NativeEditor=true,Access="Lecture"}}){r.Key=InstanceResources.ResourceKey(r.Plugin,r.Field,r.Value);catalog.Resources.Add(r);}
            context.Store.WriteRecord(InstanceResources.CacheName(toolOwner.Id),catalog);
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
                    assert(!shell.Navigation.Items.Contains("Agents")&&!shell.Navigation.Items.Contains("Projets")&&!shell.Navigation.Items.Contains("Comptes")&&!shell.Navigation.Items.Contains("Outils partagés"),"navigation simple centrée sur les instances");
                    var inspectInstanceDone=new TaskCompletionSource<bool>();
                    _=shell.Dispatcher.BeginInvoke(new Action(async()=>{
                      try{
                        var form=await WaitDialog("Votre instance Codex");
                        try{
                            assert(Descendants(form).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Compte permanent").Items.Count==5,"création propose connexion ou comptes existants");
                            assert(Descendants(form).OfType<Button>().Any(b=>Object.Equals(b.Content,"Préparer et ouvrir")),"création expose une action unique de préparation");
                            assert(!Descendants(form).OfType<TextBox>().Any(t=>System.Windows.Automation.AutomationProperties.GetName(t).Contains("canal")),"création ne demande aucun canal");
                            Capture(form,Path.Combine(root,"Creation-instance.png"));
                        }finally{form.Close();}
                        inspectInstanceDone.SetResult(true);
                      }catch(Exception e){inspectInstanceDone.SetException(e);}
                    }),DispatcherPriority.ContextIdle);
                    await InstanceWizard.Open(context,shell);await inspectInstanceDone.Task;
                    foreach (string page in shell.Pages.Keys)
                    {
                        shell.Navigate(page);
                        await shell.Pages[page].Refresh();
                        await Task.Delay(40);
                        shell.UpdateLayout();
                        assert(shell.CurrentPage == page && Object.Equals(shell.Navigation.SelectedItem, page), "page active et sidebar : " + page);
                        Capture(shell, Path.Combine(root, page + ".png"));
                    }
                    var shared=(ToolSharingPage)shell.Pages["Outils partagés"];
                    shell.Navigate("Outils partagés");await shared.Refresh();shell.UpdateLayout();
                    assert(shared.List.Items.Count==1&&Descendants(shared).OfType<TextBlock>().Any(t=>t.Text.Contains("page_demo")),"outils partagés affichent le propriétaire et la ressource autorisée");
                    var inspectShare=shell.Dispatcher.BeginInvoke(new Action(()=>{
                        var form=Application.Current.Windows.OfType<EditWindow>().Single(w=>w.Title=="Configurer le partage");
                        try{
                            form.UpdateLayout();var ownerChoice=Descendants(form).OfType<ComboBox>().Single(c=>c.DisplayMemberPath=="Key");
                            assert(Descendants(ownerChoice).OfType<TextBlock>().Any(t=>t.Text=="Propriétaire démo"),"sélecteur affiche réellement le nom de l’instance plutôt que son type technique");
                            assert(!form.Dirty(),"ouvrir un partage existant ne crée pas de faux changement");
                            assert(Descendants(form).OfType<CheckBox>().Any(c=>System.Windows.Automation.AutomationProperties.GetName(c).Contains("Développement démo")&&c.IsChecked==true),"partage conserve les instances clientes choisies");
                            assert(Descendants(form).OfType<TextBox>().Any(t=>t.Text=="page_demo"),"partage conserve la restriction de ressource");
                            assert(!Descendants(form).OfType<ComboBox>().SelectMany(c=>c.Items.Cast<object>()).Any(x=>Object.Equals(x,"Un site précis")||Object.Equals(x,"Une page précise")),"réglages avancés sans types de ressources imposés aux plugins");
                            Capture(form,Path.Combine(root,"Partage-outils.png"));form.Dirty=()=>false;
                        }finally{form.Dirty=()=>false;form.Close();}
                    }),DispatcherPriority.ContextIdle);
                    await shared.Edit(toolFixture);await inspectShare.Task;
                    var instances=(InstancesPage)shell.Pages["Instances"];await instances.Open(toolOwner.Id);shell.UpdateLayout();
                    var advancedToggle=Descendants(shell).OfType<CheckBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Réglages avancés");advancedToggle.IsChecked=false;advancedToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var resources=Descendants(instances).OfType<InstanceResourcesView>().Single();
                    assert(Descendants(instances).OfType<TextBlock>().Any(t=>t.Text.Contains("compte-0@example.com")),"instance affiche son compte permanent");
                    assert(!Descendants(resources).OfType<TextBlock>().Any(t=>t.Text.Contains("page_roadmap")),"ressources présentées par nom sans identifiant technique");
                    var share=Descendants(resources).OfType<CheckBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Partager Feuille de route produit");
                    assert(share.IsEnabled&&!share.IsChecked.Value,"ressource détectée décochée par défaut");
                    assert(!Descendants(resources).OfType<CheckBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Partager Budget prévisionnel").IsEnabled,"éditeur natif indisponible signalé sans faux partage");
                    share.IsChecked=true;assert(resources.Dirty,"cocher prépare un changement explicite");
                    resources.Search.Text="portail";shell.UpdateLayout();resources.Search.Text="";shell.UpdateLayout();
                    assert(Descendants(resources).OfType<CheckBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Partager Feuille de route produit").IsChecked==true,"filtrage conserve les ressources cochées");
                    await instances.Refresh();assert(resources.Dirty,"actualisation périodique conserve le brouillon");
                    resources.PluginServiceFactory=()=>new PluginAccess(context.Store,new ToolTunnel(context.Store,id=>new TunnelOwner{Id=toolOwner.Id,Name=toolOwner.Name,Home=service.Instances.Home(toolOwner),Account=toolOwner.AccountKey},o=>new PluginFixtureConnection(),s=>{}));
                    var inspectPluginDone=new TaskCompletionSource<bool>();
                    _=shell.Dispatcher.BeginInvoke(new Action(async()=>{
                        EditWindow form=null;
                        try{
                            form=await WaitDialog("Outils de l’équipe");
                            for(int attempt=0;attempt<300&&!Descendants(form).OfType<CheckBox>().Any();attempt++)await Task.Delay(10);
                            form.UpdateLayout();
                            assert(Descendants(form).OfType<TextBlock>().Any(t=>t.Text.Contains("Depuis Propriétaire démo")&&t.Text.Contains("Pour Développement démo")),"plugin conserve propriétaire et destinataire du parcours");
                            assert(!Descendants(form).OfType<ComboBox>().Any()&&!Descendants(form).OfType<TextBox>().Any(t=>System.Windows.Automation.AutomationProperties.GetName(t).Contains("Identifiant")),"plugin sans nouveau sélecteur ni périmètre site page");
                            assert(Descendants(form).OfType<CheckBox>().Count()==2&&!form.SaveButton.IsEnabled,"chargement automatique des seules actions du plugin sans sélection implicite");
                            var read=Descendants(form).OfType<CheckBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Consulter les dossiers");read.IsChecked=true;
                            var actionSearch=Descendants(form).OfType<TextBox>().Single();actionSearch.Text="modifier";form.UpdateLayout();actionSearch.Text="";form.UpdateLayout();
                            assert(Descendants(form).OfType<CheckBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Consulter les dossiers").IsChecked==true,"recherche du plugin conserve les actions cochées");
                            Capture(form,Path.Combine(root,"Plugin-actions.png"));
                            form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            for(int attempt=0;attempt<300&&!form.Saved;attempt++)await Task.Delay(10);
                            assert(form.Saved,"enregistrement contextuel du plugin réussi depuis le bouton");
                            var saved=new ToolTunnel(context.Store).Grants().Single(g=>g.CatalogPlugin==InstanceResources.PluginKey(PluginFixtureConnection.Tools[0]));
                            assert(saved.Instance==toolOwner.Id&&saved.CatalogClient==toolClient.Id&&saved.Tools.Single().Name=="records.list","interface persiste uniquement le bon plugin la bonne action et le bon client");
                            inspectPluginDone.SetResult(true);
                        }catch(Exception e){inspectPluginDone.SetException(e);}finally{if(form!=null&&!form.Saved){form.ConfirmDiscard=()=>true;form.Close();}}
                    }),DispatcherPriority.ContextIdle);
                    Descendants(resources).OfType<Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Configurer les actions de Outils de l’équipe").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await inspectPluginDone.Task;
                    assert(resources.Dirty,"enregistrer les actions du plugin conserve le brouillon des ressources");
                    await InstanceActions(context,shell,instances,resources,toolOwner,toolClient,root,assert);
                    resources.ConfirmDiscard=()=>false;shell.Navigate("Accueil");assert(shell.CurrentPage=="Instances","navigation protège les changements de partage");
                    instances.ShowTab(0);shell.UpdateLayout();Capture(shell,Path.Combine(root,"Instance-compte.png"));instances.ShowTab(1);shell.UpdateLayout();
                    assert(resources.Dirty,"onglets Compte et Ressources conservent le brouillon");
                    Capture(shell,Path.Combine(root,"Instance-ressources.png"));shell.Theme("Sombre");shell.UpdateLayout();Capture(shell,Path.Combine(root,"Instance-ressources-dark.png"));shell.Theme("Clair");
                    double width=shell.Width,height=shell.Height;shell.Width=800;shell.Height=550;await Task.Delay(50);shell.UpdateLayout();
                    assert(((ItemRow)Descendants(instances).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Instance sélectionnée").SelectedItem).Id==toolOwner.Id,"sélecteur compact reflète exactement l’instance active");
                    var saveResources=Descendants(resources).OfType<Button>().Single(b=>Object.Equals(b.Content,"Enregistrer les accès"));var savePosition=saveResources.TranslatePoint(new Point(),shell);
                    assert(savePosition.Y>=0&&savePosition.Y+saveResources.ActualHeight<=shell.ActualHeight,"enregistrement des accès accessible en petite fenêtre");Capture(shell,Path.Combine(root,"Instance-ressources-compact.png"));shell.Width=width;shell.Height=height;
                    resources.ConfirmDiscard=()=>true;shell.Navigate("Accueil");assert(shell.CurrentPage=="Accueil"&&!resources.Dirty,"abandon confirmé restaure les autorisations précédentes");
                    await instances.Open(toolOwner.Id,false);instances.ShowTab(0);shell.UpdateLayout();
                    var pendingCheck=new TaskCompletionSource<bool>();var startedCheck=new TaskCompletionSource<bool>();
                    context.VerifyIntegrationFixture=async(home,settings)=>{startedCheck.SetResult(true);await pendingCheck.Task;context.Store.WriteRecord("integration-"+RelayIntegration.HomeKey(home)+".dpapi",new RelayIntegrationState{Home=home,Healthy=true,Status="Intégration installée et vérifiée",Updated=DateTime.UtcNow.ToString("o")});};
                    Descendants(instances).OfType<Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Vérifier l’intégration").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await startedCheck.Task;
                    await instances.Open(toolClient.Id,false);shell.UpdateLayout();pendingCheck.SetResult(true);await Task.Delay(100);await instances.Refresh();shell.UpdateLayout();
                    assert(Descendants(instances).OfType<TextBlock>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="Résultat de la vérification").Visibility==Visibility.Collapsed,"résultat d’une instance absent de la fiche d’une autre instance");
                    await instances.Open(toolOwner.Id,false);shell.UpdateLayout();
                    assert(Descendants(instances).OfType<TextBlock>().Any(t=>t.Text.StartsWith("Vérification réussie")),"résultat retrouvé en revenant sur l’instance vérifiée");
                    var windowCalls=new List<string>();var openWindows=new HashSet<string>{"local"};
                    context.InstanceStateFixture=i=>new InstanceState{Running=openWindows.Contains(i.Id),Phase=openWindows.Contains(i.Id)?"running":"stopped"};
                    context.InstanceWindowFixture=id=>{windowCalls.Add(id);openWindows.Add(id);return Task.FromResult("Fenêtre affichée : "+id);};
                    await instances.Open(toolOwner.Id,false);instances.ShowTab(0);shell.UpdateLayout();
                    Descendants(instances).OfType<Button>().Single(b=>Object.Equals(b.Content,"Ouvrir Codex")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(()=>{instances.UpdateLayout();return Descendants(instances).OfType<Button>().Any(b=>Object.Equals(b.Content,"Afficher la fenêtre"));});
                    assert(windowCalls.SequenceEqual(new[]{toolOwner.Id}),"ouvrir une instance fermée cible uniquement son identité puis propose Afficher la fenêtre");
                    Descendants(instances).OfType<Button>().Single(b=>Object.Equals(b.Content,"Afficher la fenêtre")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>windowCalls.Count==2);
                    await instances.Open("local",false);instances.ShowTab(0);shell.UpdateLayout();
                    Descendants(instances).OfType<Button>().Single(b=>Object.Equals(b.Content,"Afficher la fenêtre")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>windowCalls.Count==3);
                    assert(windowCalls[1]==toolOwner.Id&&windowCalls[2]=="local"&&!Descendants(instances).OfType<Button>().Any(b=>Object.Equals(b.Content,"Instance ouverte")),"les deux fenêtres ouvertes ont une vraie action ciblée sans bouton d’état inerte");
                    context.InstanceWindowFixture=id=>throw new InvalidOperationException("Fenêtre indisponible pour ce test");
                    await Task.Delay(30);Descendants(instances).OfType<Button>().Single(b=>Object.Equals(b.Content,"Afficher la fenêtre")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(()=>Descendants(instances).OfType<TextBlock>().Any(t=>t.Text=="Fenêtre indisponible pour ce test"));assert(true,"activation impossible expliquée dans la fiche");
                    context.Store.WriteRecord("tool-call-dddddddddddddddddddddddddddddddd.dpapi",new TunnelCall{Id="dddddddddddddddddddddddddddddddd",Instance=toolOwner.Id,Grant=toolFixture.Id,GrantName="Page de recette",Tool="codex_apps/chatgpt_space.edit_page",State="blocked",Error="Définition de l’outil modifiée : contrôle requis.",Updated=DateTime.UtcNow.ToString("o")});
                    await instances.Open(toolOwner.Id,false);instances.ShowTab(2);shell.UpdateLayout();
                    assert(Descendants(instances).OfType<TextBlock>().Any(t=>t.Text=="Définition de l’outil modifiée : contrôle requis.")&&Descendants(instances).OfType<TextBlock>().Any(t=>t.Text=="codex_apps/chatgpt_space.edit_page"),"activité expose le nom de l’outil et le motif exact du blocage");Capture(shell,Path.Combine(root,"Instance-activity-error.png"));
                    context.InstanceStateFixture=null;context.InstanceWindowFixture=null;
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
                    for(int attempt=0;attempt<100&&Math.Abs(shell.ActualWidth-1000)>=1;attempt++)await Task.Delay(20);
                    assert(Math.Abs(shell.ActualWidth-1000)<1, "fenêtre compacte");
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
                catch (Exception e) { report.Add("FAIL " + e); File.WriteAllLines(Path.Combine(root, "results.txt"), report); foreach(var view in Descendants(shell).OfType<InstanceResourcesView>())view.ConfirmDiscard=()=>true; app.Shutdown(1); }
            };
            return app.Run(shell);
        }
        internal static int VerifyCompatibility(string root)
        {
            var service = new AccountService(Path.GetFullPath(root));
            var store = new RelayStore(Path.Combine(root, "relay"));
            CompatibilitySmoke.CheckContract(store);
            if (service.Data.Profiles.Count != 4 || service.Data.Profiles[0].Label != "Framework → .NET 10" || RelayPolicies.Load(store).Projects.Single().Name != "Framework compatible" || store.Messages().Count != 57)
                throw new Exception("Interopérabilité inverse incorrecte");
            File.WriteAllText(Path.Combine(root, "compatibility-ok.txt"), "PASS .NET 10 relit le coffre, la configuration et le contrat d’outil Framework ; une vraie dérive reste refusée");
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
