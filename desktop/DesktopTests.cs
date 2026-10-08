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
            assert(!Descendants(instances).OfType<Button>().Any(b=>Object.Equals(b.Content,"Consulter les Pages reçues")),"la fiche instance ne propose plus un éditeur de Pages");
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
                    context.InstanceStateFixture=i=>new InstanceState{Running=openWindows.Contains(i.Id),WindowReady=openWindows.Contains(i.Id),Phase=openWindows.Contains(i.Id)?"running":"stopped"};
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
                    bool background=true;var lifecycleCalls=new List<string>();
                    context.InstanceStateFixture=i=>new InstanceState{Running=openWindows.Contains(i.Id),WindowReady=openWindows.Contains(i.Id)&&!(background&&i.Id==toolOwner.Id),Phase=!openWindows.Contains(i.Id)?"stopped":background&&i.Id==toolOwner.Id?"background":"running"};
                    context.InstanceWindowFixture=id=>{windowCalls.Add(id);background=false;openWindows.Add(id);return Task.FromResult("Fenêtre rouverte");};
                    context.InstanceLifecycleFixture=(id,restart,state)=>{lifecycleCalls.Add(id+":"+restart);if(!restart)openWindows.Remove(id);background=false;return Task.FromResult(restart?"Instance redémarrée":"Instance fermée");};
                    Button LifecycleButton(string caption)=>Descendants(instances).OfType<Button>().Single(b=>Object.Equals(b.Content,caption));
                    await instances.Open(toolOwner.Id,false);shell.UpdateLayout();
                    assert(LifecycleButton("Rouvrir la fenêtre").IsEnabled&&LifecycleButton("Redémarrer").IsVisible&&LifecycleButton("Fermer").IsVisible,"instance en arrière-plan : rouvrir, redémarrer et fermer accessibles directement");
                    assert(((ItemRow)instances.List.SelectedItem).State=="En arrière-plan","la liste distingue un processus actif d’une fenêtre ouverte");
                    Capture(shell,Path.Combine(root,"Instance-background.png"));
                    LifecycleButton("Rouvrir la fenêtre").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>!background);await instances.Refresh();shell.UpdateLayout();
                    assert(LifecycleButton("Afficher la fenêtre").IsEnabled,"réouverture de fenêtre sans redémarrage");
                    instances.ConfirmLifecycle=(text,title)=>false;LifecycleButton("Fermer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(30);assert(lifecycleCalls.Count==0,"annulation de fermeture ne touche aucun processus");
                    string confirmation=null;instances.ConfirmLifecycle=(text,title)=>{confirmation=text;return true;};
                    var pendingRestart=new TaskCompletionSource<string>();context.InstanceLifecycleFixture=(id,restart,state)=>{lifecycleCalls.Add(id+":"+restart);return pendingRestart.Task;};
                    LifecycleButton("Redémarrer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>lifecycleCalls.Count==1);await instances.Refresh();shell.UpdateLayout();
                    assert(confirmation.Contains(toolOwner.Name)&&confirmation.Contains("interrompues")&&!LifecycleButton("Redémarrer").IsEnabled&&!LifecycleButton("Fermer").IsEnabled,"confirmation ciblée et commandes verrouillées pendant le redémarrage");
                    pendingRestart.SetResult("Instance redémarrée");await Until(()=>{instances.UpdateLayout();return LifecycleButton("Redémarrer").IsEnabled;});
                    context.InstanceLifecycleFixture=(id,restart,state)=>{lifecycleCalls.Add(id+":"+restart);openWindows.Remove(id);return Task.FromResult("Instance fermée");};
                    LifecycleButton("Fermer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>!openWindows.Contains(toolOwner.Id));await instances.Refresh();shell.UpdateLayout();
                    assert(LifecycleButton("Ouvrir Codex").IsEnabled&&openWindows.Contains("local")&&lifecycleCalls.SequenceEqual(new[]{toolOwner.Id+":True",toolOwner.Id+":False"}),"fermeture ciblée, réouverture proposée et autre instance préservée");
                    await instances.Open("local",false);shell.UpdateLayout();assert(LifecycleButton("Fermer").IsVisible&&LifecycleButton("Redémarrer").IsVisible,"commandes également disponibles pour la session habituelle sans l’arrêter pendant la recette");
                    instances.ConfirmLifecycle=(text,title)=>Ui.Confirm(shell,text,title);context.InstanceLifecycleFixture=null;
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
                    shell.WindowState = WindowState.Normal;
                    shell.Width = 1000;
                    shell.Height = 700;
                    await Task.Delay(40);
                    shell.UpdateLayout();
                    Capture(shell, Path.Combine(root, "compact.png"));
                    for(int attempt=0;attempt<100&&Math.Abs(shell.ActualWidth-1000)>=1;attempt++)await Task.Delay(20);
                    assert(Math.Abs(shell.ActualWidth-1000)<1, "fenêtre compacte (demandée="+shell.Width+", réelle="+shell.ActualWidth+", état="+shell.WindowState+")");
                    shell.Theme("Sombre");
                    Capture(shell, Path.Combine(root, "dark.png"));
                    shell.Theme("Clair");
                    shell.Width = 800;
                    shell.Height = 550;
                    await Task.Delay(40);
                    shell.UpdateLayout();
                    Capture(shell, Path.Combine(root, "small.png"));
                    assert(Math.Abs(shell.ActualWidth - 800) < 1 && Math.Abs(shell.ActualHeight - 550) < 1, "fenêtre utilisable sur une petite surface logique");
                    shell.WindowState = WindowState.Normal;
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
                    await SharedPageUi(shell,root,assert);
                    await InstanceWizardUi(shell,root,assert);
                    await AccountLoginUi(shell,root,assert);
                    report.Add(passed + " tests UI réussis");
                    File.WriteAllLines(Path.Combine(root, "results.txt"), report);
                    shell.ExitForTest();
                    app.Shutdown(0);
                }
                catch (Exception e) { report.Add("FAIL " + e); File.WriteAllLines(Path.Combine(root, "results.txt"), report); foreach(var view in Descendants(shell).OfType<InstanceResourcesView>())view.ConfirmDiscard=()=>true; app.Shutdown(1); }
            };
            return app.Run(shell);
        }
        private static async Task InstanceWizardUi(ShellWindow shell,string root,Action<bool,string> assert)
        {
            var context=new DesktopContext(Path.Combine(root,"wizard-data"),true);
            var initial=new AccountService(context.Root);initial.Vault.Save(new VaultData());
            initial=new AccountService(context.Root);initial.Settings.CodexHome=Path.Combine(context.Root,"home");Directory.CreateDirectory(initial.Settings.CodexHome);initial.Vault.SaveSettings(initial.Settings);
            var existing=initial.Import(FakeAuth(20),"Compte de recette");
            async Task Run(string name,int index,Func<EditWindow,Task> action,Func<System.Threading.CancellationToken,Task<string>> login=null,string instanceId=null)
            {
                var done=new TaskCompletionSource<bool>();
                _=shell.Dispatcher.BeginInvoke(new Action(async()=>{
                    EditWindow form=null;
                    try{form=await WaitDialog("Votre instance Codex");Descendants(form).OfType<TextBox>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("Nom à utiliser")).Text=name;Descendants(form).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Compte permanent").SelectedIndex=index;await action(form);done.SetResult(true);}
                    catch(Exception e){done.SetException(e);}finally{if(form!=null&&!form.Saved)form.Close();}
                }),DispatcherPriority.ContextIdle);
                await InstanceWizard.Open(context,shell,instanceId,login);await done.Task;
            }
            async Task Click(EditWindow form)
            {
                form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(()=>form.Saved||form.SaveButton.IsEnabled);
            }
            string Error(EditWindow form)=>String.Join(" | ",Descendants(form).OfType<TextBlock>().Where(t=>Equals(t.Foreground,Brushes.Firebrick)).Select(t=>t.Text));
            await Run("Nouvelle instance existante",1,async form=>{
                await Click(form);assert(form.Saved,"création réelle depuis le bouton avec compte existant : "+Error(form));
            });
            var saved=new AccountService(context.Root);var created=saved.Data.Instances.Single(i=>i.Name=="Nouvelle instance existante");
            assert(created.AccountKey==existing.Key&&created.AccountLocked,"création lie durablement le compte choisi");
            int logins=0;
            await Run("Certivan de recette",0,async form=>{
                await Click(form);assert(form.Saved,"création avec une connexion asynchrone : "+Error(form));
            },async token=>{logins++;await Task.Delay(20,token);return FakeAuth(21);});
            saved=new AccountService(context.Root);var fresh=saved.Data.Instances.Single(i=>i.Name=="Certivan de recette");
            assert(logins==1&&fresh.AccountLocked&&fresh.AccountKey==AuthIdentity.Parse(FakeAuth(21)).Key,"nouveau compte importé et associé à la bonne instance");
            int count=saved.Data.Instances.Count;
            await Run("  CERTIVAN DE RECETTE  ",0,async form=>{
                await Click(form);assert(!form.Saved&&Error(form).Contains("nom existe déjà"),"nom déjà utilisé expliqué avant la connexion");
            },token=>{logins++;throw new Exception("Connexion indésirable");});
            assert(logins==1&&new AccountService(context.Root).Data.Instances.Count==count,"doublon ne lance ni connexion ni création");
            await Run("",0,async form=>{await Click(form);assert(!form.Saved&&Error(form).Contains("Donnez un nom"),"nom vide refusé sans perdre le formulaire");});
            await Run("Connexion annulée",0,async form=>{
                form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var cancel=Descendants(form).OfType<Button>().Single(b=>Equals(b.Content,"Annuler la connexion"));await Until(()=>cancel.IsVisible);
                assert(!form.SaveButton.IsEnabled,"double soumission bloquée pendant la connexion");
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>form.SaveButton.IsEnabled);
                assert(!form.Saved&&cancel.Visibility==Visibility.Collapsed&&Descendants(form).OfType<TextBox>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("Nom à utiliser")).Text=="Connexion annulée","annulation conserve le nom et permet une nouvelle tentative");
                Descendants(form).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Compte permanent").SelectedIndex=1;await Click(form);assert(form.Saved,"nouvel essai avec un compte existant après annulation");
            },async token=>{await Task.Delay(30000,token);return FakeAuth(22);});
            assert(new AccountService(context.Root).Data.Profiles.Count==2,"annulation n’importe aucun compte incomplet");
            int attempts=0;
            await Run("Connexion à réessayer",0,async form=>{
                await Click(form);assert(!form.Saved&&Error(form).Contains("Échec simulé"),"échec de connexion affiché sans création d’instance");
                assert(!new AccountService(context.Root).Data.Instances.Any(i=>i.Name=="Connexion à réessayer"),"connexion échouée ne laisse pas d’instance partielle");
                await Click(form);assert(form.Saved,"connexion réussie au second essai sans rouvrir le formulaire");
            },async token=>{await Task.Delay(10,token);if(++attempts==1)throw new InvalidOperationException("Échec simulé de connexion");return FakeAuth(23);});
            context.LoginAuthFixture=async(present,token)=>{present("https://auth.openai.com/authorize?state=wizard-fixture-only&code_challenge=not-a-real-login");await Task.Delay(30000,token);return FakeAuth(24);};
            await Run("Instance avec lien manuel",0,async form=>{
                var browser=Descendants(form).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Navigateur pour la connexion");browser.SelectedIndex=1;
                form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var link=Descendants(form).OfType<TextBox>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="Lien de connexion");await Until(()=>link.IsVisible);await Task.Delay(80);form.UpdateLayout();
                var copy=Descendants(form).OfType<Button>().Single(b=>Equals(b.Content,"Copier le lien"));var scroller=Descendants(form).OfType<ScrollViewer>().First();
                var point=copy.TransformToAncestor(scroller).Transform(new Point(0,0));
                assert(point.Y>=0&&point.Y+copy.ActualHeight<=scroller.ActualHeight,"création affiche automatiquement le bouton Copier dans la zone visible");
                Capture(form,Path.Combine(root,"Instance-login-link.png"));
                Descendants(form).OfType<Button>().Single(b=>Equals(b.Content,"Annuler la connexion")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>form.SaveButton.IsEnabled);
                assert(!form.Saved&&link.Text==""&&!new AccountService(context.Root).Data.Instances.Any(i=>i.Name=="Instance avec lien manuel"),"annulation du lien manuel ne crée aucune instance partielle");
            });
        }
        private static async Task AccountLoginUi(ShellWindow shell,string root,Action<bool,string> assert)
        {
            var context=new DesktopContext(Path.Combine(root,"browser-login-data"),true);
            var profiles=new[]{new LoginBrowserProfile{Directory="Default",Label="Personnel · Default"},new LoginBrowserProfile{Directory="Profile 2",Label="Studio · Profile 2"}};
            var chrome=new LoginBrowser{Id="chrome",Label="Google Chrome",Executable="chrome.exe",Profiles=profiles};
            var opera=new LoginBrowser{Id="opera",Label="Opera",Executable="opera.exe",Profiles=new[]{profiles[0]}};
            context.LoginBrowsersFixture=LoginBrowsers.Basic().Concat(new[]{chrome,opera}).ToArray();
            var form=new EditWindow(shell,"Connexion de recette");
            var panel=new AccountLoginPanel(context,new Settings{LoginBrowserId="chrome",LoginBrowserProfile="Profile 2"},context.LoginBrowsersFixture);
            form.Fields.Children.Add(panel);form.Show();form.UpdateLayout();
            ComboBox Choice(string name)=>Descendants(form).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)==name);
            var browser=Choice("Navigateur pour la connexion");var profile=Choice("Profil du navigateur");
            var link=Descendants(form).OfType<TextBox>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="Lien de connexion");
            var copy=Descendants(form).OfType<Button>().Single(b=>Equals(b.Content,"Copier le lien"));
            var cancel=Descendants(form).OfType<Button>().Single(b=>Equals(b.Content,"Annuler la connexion"));
            assert(((LoginBrowser)browser.SelectedItem).Id=="chrome"&&((LoginBrowserProfile)profile.SelectedItem).Directory=="Profile 2","connexion restaure navigateur et profil enregistrés");
            browser.SelectedItem=opera;assert(profile.Items.Count==1&&((LoginBrowserProfile)profile.SelectedItem).Directory=="Default","changement de navigateur remplace la liste des profils");
            browser.SelectedItem=chrome;profile.SelectedIndex=1;
            const string url="https://auth.openai.com/authorize?state=ui-fixture-only&code_challenge=not-a-real-login";
            var finish=new TaskCompletionSource<string>();LoginBrowserTarget received=null;int launches=0;string copied=null;
            context.LoginAuthFixture=async (present,token)=>{await Task.Run(()=>present(url));return await finish.Task.WaitAsync(token);};
            context.LoginOpenFixture=(target,value)=>{received=target;launches++;assert(value==url,"lien OAuth transmis intact au navigateur");};
            panel.CopyText=value=>copied=value;
            var connection=panel.Connect();await Until(()=>link.IsVisible);
            assert(!browser.IsEnabled&&!profile.IsEnabled&&link.IsReadOnly&&link.Text==url,"connexion fige les choix et affiche un lien sélectionnable en lecture seule");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));assert(copied==url&&received.ProfileDirectory=="Profile 2"&&received.Id=="chrome","copie exacte du lien et ouverture dans le profil choisi");
            Capture(form,Path.Combine(root,"Browser-login.png"));
            finish.SetResult(FakeAuth(31));await connection;
            assert(link.Text==""&&!link.IsVisible&&browser.IsEnabled,"lien retiré après connexion et formulaire réutilisable");
            var saved=new AccountService(context.Root).Settings;assert(saved.LoginBrowserId=="chrome"&&saved.LoginBrowserProfile=="Profile 2","préférence enregistrée pour la prochaine connexion");
            browser.SelectedItem=context.LoginBrowsersFixture.Single(b=>b.Id=="manual");finish=new TaskCompletionSource<string>();
            context.LoginOpenFixture=(target,value)=>{assert(target.StartInfo(value)==null,"mode manuel ne construit aucune ouverture navigateur");};
            connection=panel.Connect();await Until(()=>link.IsVisible);copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(copied==url&&launches==1,"mode manuel fournit le lien sans lancer de navigateur");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));bool cancelled=false;try{await connection;}catch(InvalidOperationException e){cancelled=e.Message.Contains("annulée");}
            assert(cancelled&&link.Text==""&&!link.IsVisible&&browser.IsEnabled,"annulation masque l’ancien lien et permet de choisir une autre session");
            browser.SelectedItem=chrome;context.LoginOpenFixture=(target,value)=>throw new Exception("Browser missing");
            connection=panel.Connect();await Until(()=>link.IsVisible);
            assert(Descendants(form).OfType<TextBlock>().Any(t=>t.Text.Contains("L’ouverture du navigateur a échoué"))&&copy.IsEnabled,"échec ouverture conserve la connexion et le bouton Copier");
            finish.SetResult(FakeAuth(32));await connection;form.Close();
            var missing=new EditWindow(shell,"Profil disparu");var missingPanel=new AccountLoginPanel(context,new Settings{LoginBrowserId="chrome",LoginBrowserProfile="Deleted"},context.LoginBrowsersFixture);missing.Fields.Children.Add(missingPanel);missing.Show();missing.UpdateLayout();
            bool refused=false;try{await missingPanel.Connect();}catch(InvalidOperationException){refused=true;}
            assert(refused&&Descendants(missing).OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="Profil du navigateur").SelectedIndex==-1,"profil disparu demande un choix explicite sans basculer sur un autre compte");missing.Close();
            var unknown=new AccountLoginPanel(context,new Settings{LoginBrowserId="removed-browser"},context.LoginBrowsersFixture);
            var manual=((StackPanel)unknown.Children[0]).Children.OfType<ComboBox>().Single();
            assert(((LoginBrowser)manual.SelectedItem).Id=="manual","navigateur disparu propose la copie du lien au lieu du navigateur par défaut");
            // Exercise the same dialog used by the Accounts page, including its real Save button.
            context.LoginOpenFixture=(target,value)=>{};context.LoginAuthFixture=(present,token)=>{present(url);return Task.FromResult(FakeAuth(33));};
            var done=new TaskCompletionSource<bool>();
            _=shell.Dispatcher.BeginInvoke(new Action(async()=>{try{var dialog=await WaitDialog("Connecter un compte");dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>dialog.Saved);done.SetResult(true);}catch(Exception e){done.SetException(e);}}),DispatcherPriority.ContextIdle);
            string auth=await AccountLoginPanel.Open(context,shell);await done.Task;assert(auth==FakeAuth(33),"ajout d’un compte utilise le même formulaire et renvoie la connexion confirmée");
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
        private static async Task SharedPageUi(Window shell,string root,Action<bool,string> assert)
        {
            var doc=new SharedPageDocument{Share="share",Page="page-1",Title="Page de démonstration",Owner="Compte propriétaire",ReadId="read-1",ReadAt=DateTime.UtcNow.ToString("o"),CanEdit=true,Blocks=new[]{new SharedPageBlock{Id="block",Hash="hash",Kind="markdown",Markdown="# Une Page accessible\n\nTexte **partagé** avec une autre instance.\n- Même document\n- Aucun second prompt"},new SharedPageBlock{Id="instructions",Kind="agent_instructions",Hash="h2",Markdown="Instructions conservées en lecture seule."}}};
            int writes=0,opens=0;bool conflict=true;
            var window=new SharedPagesWindow(shell,"Instance cliente",()=>Task.FromResult(new[]{new SharedPageEntry{Share="share",Page="page-1",Title=doc.Title,Owner=doc.Owner,CanEdit=true}}),e=>Task.FromResult(doc),
                (d,id,block,text,readId)=>{writes++;assert(readId=="read-1"&&block=="block"&&System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-f0-9]{32}$"),"visionneuse transmet identité de lecture, bloc et identifiant stable");if(conflict)return Task.FromResult(new SharedPageSave{State="tool_error",Message="Conflit · brouillon conservé"});doc.Blocks[0].Markdown=text;return Task.FromResult(new SharedPageSave{State="saved",Message="Enregistrement confirmé",Document=doc});},d=>{opens++;return Task.CompletedTask;});
            window.ConfirmDiscard=()=>true;window.Show();await Until(()=>Descendants(window).OfType<ListBox>().Single().Items.Count==1);
            Descendants(window).OfType<ListBox>().Single().SelectedIndex=0;await Until(()=>window.Current!=null);window.UpdateLayout();
            Button FindButton(string name)=>Descendants(window).OfType<Button>().Single(b=>Object.Equals(b.Content,name));
            TextBox Draft()=>Descendants(window).OfType<TextBox>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="Brouillon de la Page");
            assert(Descendants(window).OfType<Button>().Count(b=>Object.Equals(b.Content,"Modifier ce bloc"))==1,"instructions visibles sans bouton de modification");
            Capture(window,Path.Combine(root,"Shared-page-reader.png"));
            FindButton("Modifier ce bloc").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Draft().Text="Mon brouillon éà😀";window.UpdateLayout();assert(window.Dirty&&!FindButton("Actualiser").IsEnabled,"brouillon protégé contre actualisation");
            FindButton("Enregistrer sur la Page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>writes==1);assert(window.Dirty&&Draft().Text=="Mon brouillon éà😀"&&!FindButton("Enregistrer sur la Page").IsEnabled,"conflit conserve le brouillon et bloque un renvoi");Capture(window,Path.Combine(root,"Shared-page-conflict.png"));
            FindButton("Annuler").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));conflict=false;FindButton("Modifier ce bloc").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Draft().Text="Modification confirmée";FindButton("Enregistrer sur la Page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>writes==2);assert(!window.Dirty&&window.Current.Blocks[0].Markdown=="Modification confirmée","sauvegarde confirmée et vue actualisée");
            FindButton("Ouvrir chez le propriétaire").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>opens==1);assert(opens==1,"navigation propriétaire explicite sans second prompt");
            doc.CanEdit=false;window.Show(doc);window.UpdateLayout();assert(!FindButton("Ajouter du texte").IsEnabled&&!Descendants(window).OfType<Button>().Any(b=>Object.Equals(b.Content,"Modifier ce bloc")),"lecture seule retire les contrôles de modification");
            window.Width=740;window.Height=560;window.UpdateLayout();Capture(window,Path.Combine(root,"Shared-page-compact.png"));assert(FindButton("Ouvrir chez le propriétaire").IsVisible,"navigation propriétaire accessible en petite fenêtre");window.Close();
        }
        internal static int SharedPagesLive(Application app,string instanceId,string root)
        {
            Directory.CreateDirectory(root);var service=new SharedPages(new RelayStore(RelayStore.DefaultRoot));var session=service.DesktopSession(instanceId);
            var window=new SharedPagesWindow(null,"Recette lecture réelle",()=>Task.Run(()=>service.List(session)),e=>Task.Run(()=>service.Read(session,e.Share,e.Page,System.Threading.CancellationToken.None)),
                (d,id,b,text,r)=>throw new InvalidOperationException("Read-only acceptance"),d=>Task.Run(()=>service.OpenOwner(session,d.Share,d.Page,System.Threading.CancellationToken.None)));
            window.Loaded+=async delegate{
                try{
                    await Until(()=>Descendants(window).OfType<ListBox>().Single().Items.Count>0);
                    var list=Descendants(window).OfType<ListBox>().Single();list.SelectedItem=list.Items.Cast<SharedPageEntry>().FirstOrDefault(e=>e.Title=="dgd")??list.Items[0];
                    for(int i=0;i<900&&window.Current==null;i++)await Task.Delay(100);
                    if(window.Current==null)throw new Exception("Live Page did not render");window.UpdateLayout();Capture(window,Path.Combine(root,"shared-page-live.png"));
                    if(!Descendants(window).OfType<FlowDocumentScrollViewer>().Any())throw new Exception("No rendered text blocks");
                    File.WriteAllText(Path.Combine(root,"live-ui-result.txt"),"PASS original shared Page rendered in WPF; "+window.Current.Blocks.Length+" blocks; no write or second model turn");
                    window.Close();app.Shutdown(0);
                }catch(Exception e){File.WriteAllText(Path.Combine(root,"live-ui-result.txt"),"FAIL "+e.Message);app.Shutdown(1);}
            };
            return app.Run(window);
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
