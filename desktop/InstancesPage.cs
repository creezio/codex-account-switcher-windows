using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;

namespace Creezio.Switcher.Desktop
{
    internal sealed class InstancesPage : PageView
    {
        internal readonly ListBox List=new ListBox();
        private readonly TextBox search=new TextBox();
        private readonly TextBlock heading=Ui.Text("Choisissez une instance",24),accountLine=Ui.Text("",14,true);
        private readonly ContentControl detail=new ContentControl();
        private readonly WrapPanel tabs=new WrapPanel();
        private readonly Button[] tabButtons=new Button[4];
        private readonly ComboBox compactChoice=new ComboBox{DisplayMemberPath="Title",Visibility=Visibility.Collapsed};
        private readonly MenuItem rename,copyThread;
        internal readonly ContextMenu ActionsMenu=new ContextMenu();
        private readonly Button more;
        private readonly WrapPanel lifecycle=new WrapPanel();
        private readonly Dictionary<string,string> operations=new Dictionary<string,string>();
        internal Func<string,string,bool> ConfirmLifecycle;
        private readonly TextBlock verification=Ui.Text("",13);
        private readonly Dictionary<string,Verification> verifications=new Dictionary<string,Verification>();
        private sealed class Verification { public bool Busy; public string Message; }
        private bool archived,changing;
        private int tab;
        private string selected;
        private string identity;
        private string listStamp;
        private string renderedAccount;
        private string lifecycleStamp;
        private Info current;
        private InstanceResourcesView resources;
        private CloudEnvironmentPanel cloud;
        private sealed class Info
        {public DesktopInstance Instance;public Profile Account;public InstanceState State;public RelayIntegrationState Integration;public string Active;}
        internal InstancesPage(DesktopContext context,ShellWindow shell):base(context,shell,"Instances","Un compte, ses ressources et ses accès, réunis dans chaque espace Codex.")
        {
            Header.Visibility=Visibility.Collapsed;
            var split=new Grid();split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(235)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(20)});split.ColumnDefinitions.Add(new ColumnDefinition());Body.Children.Add(split);
            var left=new DockPanel();split.Children.Add(left);
            var listActions=new DockPanel{LastChildFill=true,Margin=new Thickness(0,0,0,12)};
            var add=Ui.AsyncButton("+",async()=>{if(!CanLeave())return;await InstanceWizard.Open(Context,Shell);await Refresh();},Error);add.ToolTip="Créer une instance";AutomationProperties.SetName(add,"Créer une instance");add.Margin=new Thickness(4,0,0,0);add.Padding=new Thickness(10,7,10,7);DockPanel.SetDock(add,Dock.Right);listActions.Children.Add(add);
            var archiveMenu=new ContextMenu();var archiveItem=new MenuItem{Header="Afficher les archives",IsCheckable=true};AutomationProperties.SetName(archiveItem,"Afficher les archives");archiveMenu.Items.Add(archiveItem);
            archiveItem.Click+=async delegate{if(!CanLeave()){archiveItem.IsChecked=archived;return;}archived=archiveItem.IsChecked;try{await Refresh();}catch(Exception error){Error(error);}};
            Button ArchiveButton(){Button button=null;button=Ui.Button("⋯",()=>{archiveItem.IsChecked=archived;archiveMenu.PlacementTarget=button;archiveMenu.Placement=PlacementMode.Bottom;archiveMenu.IsOpen=true;});button.ContextMenu=archiveMenu;button.ToolTip="Options de la liste d’instances";AutomationProperties.SetName(button,"Options de la liste d’instances");button.Margin=new Thickness(4,0,0,0);button.Padding=new Thickness(10,7,10,7);return button;}
            var archive=ArchiveButton();DockPanel.SetDock(archive,Dock.Right);listActions.Children.Add(archive);
            var pageTitle=Ui.Text("Instances",22);pageTitle.VerticalAlignment=VerticalAlignment.Center;pageTitle.Margin=new Thickness(0);listActions.Children.Add(pageTitle);
            DockPanel.SetDock(listActions,Dock.Top);left.Children.Add(listActions);
            var searchField=Ui.SearchField(search,"Rechercher une instance");DockPanel.SetDock(searchField,Dock.Top);left.Children.Add(searchField);left.Children.Add(List);
            System.Windows.Automation.AutomationProperties.SetName(search,"Rechercher une instance");System.Windows.Automation.AutomationProperties.SetName(List,"Vos instances");
            var template=new DataTemplate(typeof(ItemRow));var panel=new FrameworkElementFactory(typeof(StackPanel));
            foreach(string property in new[]{"Title","Summary","State"}){var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(property));text.SetValue(TextBlock.MarginProperty,new Thickness(0,0,0,3));text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.NoWrap);text.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);text.SetValue(TextBlock.FontSizeProperty,property=="Title"?14d:12d);if(property=="Title")text.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);if(property=="State")text.SetResourceReference(TextBlock.ForegroundProperty,"AccentTextBrush");panel.AppendChild(text);}template.VisualTree=panel;List.ItemTemplate=template;
            var right=new DockPanel();Grid.SetColumn(right,2);split.Children.Add(right);var header=new StackPanel();
            var compactBar=new DockPanel{Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,10)};
            var compactArchive=ArchiveButton();DockPanel.SetDock(compactArchive,Dock.Right);compactBar.Children.Add(compactArchive);
            var compactAdd=Ui.AsyncButton("+",async()=>{if(!CanLeave())return;await InstanceWizard.Open(Context,Shell);await Refresh();},Error);compactAdd.Margin=new Thickness(6,0,0,0);compactAdd.ToolTip="Créer une instance";AutomationProperties.SetName(compactAdd,"Créer une instance");DockPanel.SetDock(compactAdd,Dock.Right);compactBar.Children.Add(compactAdd);compactChoice.Margin=new Thickness(0);compactBar.Children.Add(compactChoice);header.Children.Add(compactBar);
            var instanceHeader=new Grid{Margin=new Thickness(0,0,0,8)};instanceHeader.ColumnDefinitions.Add(new ColumnDefinition());instanceHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var identityHeader=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)};heading.FontSize=23;heading.Margin=new Thickness(0,0,0,3);heading.TextWrapping=TextWrapping.NoWrap;heading.TextTrimming=TextTrimming.CharacterEllipsis;accountLine.FontSize=12;accountLine.Margin=new Thickness(0);identityHeader.Children.Add(heading);identityHeader.Children.Add(accountLine);instanceHeader.Children.Add(identityHeader);
            lifecycle.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(lifecycle,1);instanceHeader.Children.Add(lifecycle);header.Children.Add(instanceHeader);
            rename=MenuAction("Renommer l’instance",Rename);rename.IsEnabled=false;ActionsMenu.Items.Add(rename);
            copyThread=MenuAction("Conversations et projets…",()=>{if(current!=null&&CanLeave())new TransfersWindow(Context,Shell,current.Instance).ShowDialog();return Task.CompletedTask;});copyThread.IsEnabled=false;ActionsMenu.Items.Add(copyThread);
            more=Ui.Button("⋯",()=>{ActionsMenu.PlacementTarget=more;ActionsMenu.Placement=PlacementMode.Bottom;ActionsMenu.IsOpen=true;});more.ContextMenu=ActionsMenu;more.Padding=new Thickness(12,7,12,7);more.Margin=new Thickness(0);more.ToolTip="Actions de l’instance";AutomationProperties.SetName(more,"Actions de l’instance");
            ConfirmLifecycle=(text,title)=>Ui.Confirm(Shell,text,title);
            verification.Visibility=Visibility.Collapsed;System.Windows.Automation.AutomationProperties.SetName(verification,"Résultat de la vérification");System.Windows.Automation.AutomationProperties.SetLiveSetting(verification,System.Windows.Automation.AutomationLiveSetting.Polite);header.Children.Add(verification);
            var tabLine=new Border{BorderThickness=new Thickness(0,0,0,1),Child=tabs,Margin=new Thickness(0,0,0,16)};tabLine.SetResourceReference(Border.BorderBrushProperty,"LineBrush");header.Children.Add(tabLine);DockPanel.SetDock(header,Dock.Top);right.Children.Add(header);right.Children.Add(detail);
            System.Windows.Automation.AutomationProperties.SetName(compactChoice,"Instance sélectionnée");
            SizeChanged+=delegate{bool compact=ActualWidth<850;left.Visibility=compact?Visibility.Collapsed:Visibility.Visible;split.ColumnDefinitions[0].Width=new GridLength(compact?0:235);split.ColumnDefinitions[1].Width=new GridLength(compact?0:20);compactBar.Visibility=compact?Visibility.Visible:Visibility.Collapsed;compactChoice.Visibility=compact?Visibility.Visible:Visibility.Collapsed;};
            compactChoice.SelectionChanged+=delegate{if(changing)return;List.SelectedItem=compactChoice.SelectedItem;};
            for(int n=0;n<4;n++){int index=n;tabButtons[n]=Ui.Button(new[]{"Compte","Ressources & accès","Activité","Cloud"}[n],()=>ShowTab(index));tabButtons[n].Padding=new Thickness(10,10,10,11);tabButtons[n].Margin=new Thickness(0,0,8,-1);tabButtons[n].MinHeight=40;tabs.Children.Add(tabButtons[n]);}
            List.SelectionChanged+=async delegate{if(changing)return;try{var row=List.SelectedItem as ItemRow;if(row==null)return;if(row.Id!=selected&&!CanLeave()){changing=true;List.SelectedItem=List.Items.Cast<ItemRow>().FirstOrDefault(r=>r.Id==selected);compactChoice.SelectedItem=List.SelectedItem;changing=false;return;}changing=true;compactChoice.SelectedItem=row;changing=false;await Select(row);}catch(Exception e){changing=false;Error(e);}};
            search.TextChanged+=async delegate{try{if(changing||!CanLeave())return;await Refresh();}catch(Exception e){Error(e);}};
            Shell.Closed+=delegate{resources?.Dispose();cloud?.Dispose();};
        }
        public override async Task Refresh()
        {
            var data=await Context.Read(a=>a.Data.Instances.Where(i=>archived||!i.Archived).Select(i=>{
                var state=Context.InstanceState(a,i);var profile=a.Data.Profiles.FirstOrDefault(p=>p.Key==i.AccountKey);var info=new Info{Instance=i,Account=profile,State=state,Active=Context.Fixture?i.AccountKey:a.Instances.ActiveKey(i),Integration=RelayIntegration.Status(Context.Store,a.Instances.Home(i))};
                return new ItemRow{Id=i.Id,Title=i.Name,Summary=profile?.Email??"Compte à connecter",State=i.Archived?"Archivée":i.AccountKey!=null&&info.Active!=i.AccountKey?"À reconnecter":state.Caption,Value=info};}).ToArray());
            bool dirty=resources?.Dirty==true;
            // Metadata can change while access edits are pending; only an identity change would destroy that draft.
            if(dirty&&!data.Any(r=>r.Id==selected&&Identity(r)==identity))return;
            var rows=data.Where(r=>(r.Title+" "+r.Summary).IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0||(dirty&&r.Id==selected)).ToArray();
            string fingerprint=Json.Write(rows.Select(r=>new{r.Id,r.Title,r.Summary,r.State}));
            if(fingerprint!=listStamp){listStamp=fingerprint;changing=true;List.ItemsSource=rows;List.SelectedItem=rows.FirstOrDefault(r=>r.Id==selected)??rows.FirstOrDefault();compactChoice.ItemsSource=rows;compactChoice.SelectedItem=List.SelectedItem;changing=false;}
            if(List.SelectedItem is ItemRow chosen)await Select(rows.First(r=>r.Id==chosen.Id));else if(selected!=null){resources?.Dispose();resources=null;cloud?.Dispose();cloud=null;selected=null;identity=null;current=null;heading.Text="Aucune instance";accountLine.Text="Créez une instance ou modifiez votre recherche.";detail.Content=null;rename.IsEnabled=false;copyThread.IsEnabled=false;UpdateVerification();RenderLifecycle();}
        }
        private static string Identity(ItemRow row){var info=(Info)row.Value;return row.Id+"/"+info.Instance.AccountKey+"/"+info.Instance.Archived;}
        private async Task Select(ItemRow row)
        {
            current=(Info)row.Value;string stamp=Identity(row);rename.IsEnabled=true;copyThread.IsEnabled=!current.Instance.Archived&&current.Instance.AccountKey!=null;
            bool changed=identity!=stamp;identity=stamp;heading.Text=row.Title;accountLine.Text=(current.Account?.Email??"Compte à connecter")+" · "+(current.Account?.Plan??"Connexion requise")+" · "+row.State;
            if(changed){Notice.Text="";resources?.Dispose();resources=null;cloud?.Dispose();cloud=null;selected=row.Id;if(current.Instance.AccountKey!=null&&!current.Instance.Archived)resources=new InstanceResourcesView(Context,Shell,current.Instance);ShowTab(tab);}
            else if(tab==0)RenderAccount();
            resources?.UpdateOwnerName(current.Instance.Name);cloud?.UpdateOwnerName(current.Instance.Name,current.Account?.Email);UpdateVerification();RenderLifecycle();
            if(resources!=null)await resources.Discover();
        }
        private void RenderLifecycle()
        {
            string stamp=current==null?null:Json.Write(new{current.Instance.Id,current.Instance.Name,current.Instance.Archived,current.State.Running,current.State.WindowReady,current.State.Phase,current.State.Message,operation=operations.TryGetValue(current.Instance.Id,out var pending)?pending:null});
            if(stamp!=null&&stamp==lifecycleStamp)return;lifecycleStamp=stamp;
            lifecycle.Children.Clear();while(ActionsMenu.Items.Count>2)ActionsMenu.Items.RemoveAt(2);
            if(current==null)return;
            if(current.Instance.Archived){lifecycle.Children.Add(more);return;}
            var info=current;var state=info.State;bool busy=operations.TryGetValue(info.Instance.Id,out var progress);
            bool blocked=busy||state.Phase=="unknown"||state.Phase=="stopping"||state.Phase=="starting";
            var open=Ui.AsyncButton(busy?progress:state.WindowReady?"Afficher la fenêtre":state.Running?"Rouvrir la fenêtre":"Ouvrir Codex",()=>ManageInstance(info,"open"),Error,true);
            open.IsEnabled=!blocked;open.Margin=new Thickness(0,0,7,0);open.Padding=new Thickness(12,8,12,8);lifecycle.Children.Add(open);
            if(state.Running){
                ActionsMenu.Items.Add(new Separator());
                var restart=MenuAction("Redémarrer",()=>ManageInstance(info,"restart"));restart.IsEnabled=!busy&&state.Phase!="unknown"&&state.Phase!="stopping";ActionsMenu.Items.Add(restart);
                var close=MenuAction("Fermer",()=>ManageInstance(info,"close"));close.IsEnabled=restart.IsEnabled;ActionsMenu.Items.Add(close);
            }
            lifecycle.Children.Add(more);
            if(!busy&&state.Running&&!state.WindowReady)Notice.Text=state.Phase=="background"?"La fenêtre est fermée ; Codex fonctionne encore en arrière-plan.":state.Message??state.Caption;
            if(!busy&&!state.Running&&state.Phase=="error")Notice.Text=state.Message??"Le lancement a échoué. Réessayez ou consultez le diagnostic.";
        }
        private MenuItem MenuAction(string label,Func<Task> action)
        {
            var item=new MenuItem{Header=label};AutomationProperties.SetName(item,label);
            item.Click+=async delegate{if(!item.IsEnabled)return;item.IsEnabled=false;try{await action();}catch(Exception error){Error(error);}finally{item.IsEnabled=true;}};
            return item;
        }
        private async Task ManageInstance(Info info,string action)
        {
            string id=info.Instance.Id;if(operations.ContainsKey(id))return;
            if(action!="open"&&!ConfirmLifecycle((action=="restart"?"Redémarrer":"Fermer")+" « "+info.Instance.Name+" » ? Ses tâches en cours seront interrompues. Enregistrez vos modifications avant de continuer. Son compte et ses conversations seront conservés.",action=="restart"?"Redémarrer l’instance":"Fermer l’instance"))return;
            operations[id]=action=="open"?"Ouverture en cours…":action=="restart"?"Redémarrage en cours…":"Fermeture en cours…";
            Notice.Text=operations[id]+" · "+info.Instance.Name;RenderLifecycle();
            try {Notice.Text=action=="open"?await Context.OpenOrShowInstance(id):await Context.CloseOrRestartInstance(id,action=="restart",info.State);}
            finally {operations.Remove(id);await Refresh();RenderLifecycle();}
        }
        private async Task Rename()
        {
            if(current==null)return;
            var instance=current.Instance;
            var form=new EditWindow(Shell,"Renommer l’instance"){MinHeight=340,Height=340};
            var name=Ui.Input("Nom de l’instance",instance.Name,form.Fields);
            form.Fields.Children.Add(Ui.Text("Utilisez ensuite ce nom dans Codex : « Délègue cette mission à … ».",13,true));
            form.Dirty=()=>name.Text!=instance.Name;
            form.Loaded+=delegate{name.Focus();name.SelectAll();};
            form.Save=()=>Context.Mutate(a=>{a.Instances.Rename(a.Data.Instances.Single(i=>i.Id==instance.Id),name.Text);return Task.CompletedTask;});
            form.ShowDialog();
            if(!form.Saved)return;
            changing=true;search.Clear();changing=false;
            await Refresh();
        }
        private void UpdateVerification()
        {
            var result=selected!=null&&verifications.TryGetValue(selected,out var value)?value:null;
            verification.Text=result?.Message??"";verification.Visibility=result==null?Visibility.Collapsed:Visibility.Visible;
        }
        private async Task Verify(Info info)
        {
            string id=info.Instance.Id;
            if(verifications.TryGetValue(id,out var previous)&&previous.Busy)return;
            var result=new Verification{Busy=true,Message="Vérification en cours… Contrôle et réparation du plugin et des skills. Cela peut prendre quelques minutes."};
            verifications[id]=result;UpdateVerification();if(tab==0)RenderAccount();
            try {
                var state=await Context.VerifyIntegration(id);
                string time=DateTime.Now.ToString("HH:mm:ss");
                result.Message=(state.Healthy?"Vérification réussie":"Intégration à corriger")+" · "+time+" · "+(state.Status??"Aucun résultat fourni.");
                await Refresh();
            } catch(Exception e){result.Message="Échec de la vérification · "+DateTime.Now.ToString("HH:mm:ss")+" · "+Program.SafeError(e);}
            finally {result.Busy=false;UpdateVerification();if(current?.Instance.Id==id&&tab==0)RenderAccount();}
        }
        internal async Task Open(string id,bool showResources=true)
        {Shell.Navigate("Instances");if(Shell.CurrentPage!="Instances")return;await Refresh();var row=List.Items.Cast<ItemRow>().FirstOrDefault(r=>r.Id==id);if(row!=null){if(row.Id!=selected&&!CanLeave())return;changing=true;List.SelectedItem=row;compactChoice.SelectedItem=row;changing=false;await Select(row);}if(showResources)ShowTab(1);}
        internal void ShowTab(int index)
        {
            if(index!=tab&&cloud?.CanLeave()==false)return;
            tab=index;for(int n=0;n<tabButtons.Length;n++){
                var button=tabButtons[n];button.SetResourceReference(Control.BackgroundProperty,"CanvasBrush");button.SetResourceReference(Control.ForegroundProperty,n==index?"AccentTextBrush":"MutedBrush");button.SetResourceReference(Control.BorderBrushProperty,n==index?"AccentBrush":"CanvasBrush");button.BorderThickness=new Thickness(0,0,0,n==index?3:0);button.FontWeight=n==index?FontWeights.SemiBold:FontWeights.Normal;
                AutomationProperties.SetHelpText(button,n==index?"Onglet sélectionné":"Afficher l’onglet");
            }
            if(current==null)return;
            if(index==0)RenderAccount();
            else if(index==1)detail.Content=resources??(object)Ui.Card(Ui.Text("Connectez le compte de cette instance pour détecter ses ressources.",15,true));
            else if(index==3){
                if(current.Instance.AccountKey==null||current.Instance.Archived)detail.Content=Ui.Card(Ui.Text("Connectez le compte de cette instance pour accéder à ses environnements Cloud.",15,true));
                else{cloud??=new CloudEnvironmentPanel(Context,Shell,current.Instance,current.Account?.Email);detail.Content=cloud;}
            }
            else{
                var activity=new StackPanel();var grants=new ToolTunnel(Context.Store).Grants();var calls=new ToolTunnel(Context.Store).Recent().Where(c=>c.Instance==selected||grants.Any(g=>g.Id==c.Grant&&g.Instance==selected)).Take(30).ToArray();
                activity.Children.Add(Ui.Text("Derniers appels aux outils de cette instance",18));
                foreach(var call in calls){var card=new StackPanel();card.Children.Add(Ui.Text(call.GrantName,15));card.Children.Add(Ui.Text(call.Tool,12,true));card.Children.Add(Ui.Text(ToolSharingPage.State(call.State),13,true));if(!String.IsNullOrEmpty(call.Error))card.Children.Add(Ui.Text(call.Error,13));card.Children.Add(Ui.Text(DateTime.TryParse(call.Updated,out var when)?when.ToLocalTime().ToString("dd/MM à HH:mm:ss"):call.Updated,12,true));activity.Children.Add(Ui.Card(card));}
                if(calls.Length==0)activity.Children.Add(Ui.Text("Aucun appel enregistré pour cette instance.",14,true));detail.Content=new ScrollViewer{Content=activity};
            }
        }
        private void RenderAccount()
        {
            bool verifying=verifications.TryGetValue(current.Instance.Id,out var check)&&check.Busy;
            string stamp=Json.Write(new{current.Instance.Id,current.Instance.Name,current.Instance.Archived,current.Instance.AccountKey,current.Active,current.State.Running,current.State.Phase,current.State.Message,current.Integration.Status,current.Integration.Healthy,current.Integration.Updated,current.Integration.InstalledVersion,verifying,quota=current.Account?.Quotas,fresh=current.Account?.IsFresh});
            if(detail.Content is ScrollViewer previous&&Object.Equals(previous.Tag,stamp))return;
            var info=current;var i=info.Instance;var body=new StackPanel();var account=new StackPanel();account.Children.Add(Ui.Text("COMPTE ASSOCIÉ",11,true));account.Children.Add(Ui.Text(info.Account?.Label??"À connecter",21));account.Children.Add(Ui.Text(info.Account?.Email??"Connectez un compte GPT pour préparer cet espace.",14,true));
            var actions=Ui.Actions(account);
            if(i.Archived)actions.Children.Add(Ui.AsyncButton("Restaurer l’instance",()=>Change(a=>{a.Instances.Archive(a.Data.Instances.Single(x=>x.Id==i.Id),false);return Task.CompletedTask;}),Error,true));
            else if(i.AccountKey==null)actions.Children.Add(Ui.AsyncButton("Connecter un compte",async()=>{await InstanceWizard.Open(Context,Shell,i.Id);await Refresh();},Error,true));
            body.Children.Add(Ui.Card(account));
            if(info.Account!=null){
                var limits=new StackPanel();limits.Children.Add(Ui.Text("Limites d’utilisation",18));
                foreach(var bucket in info.Account.Quotas)foreach(var q in new[]{bucket.Primary,bucket.Secondary}.Where(q=>q!=null)){
                    limits.Children.Add(Ui.Text(q.Caption+" · "+ProductUx.Percent(q.Remaining)+" restants",15));limits.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=q.Remaining??0,Height=6,Margin=new Thickness(0,0,0,8)});limits.Children.Add(Ui.Text(q.ResetCaption+(info.Account.IsFresh?"":" · dernière valeur connue"),12,true));}
                if(info.Account.Quotas.Count==0)limits.Children.Add(Ui.Text("Les limites n’ont pas encore été actualisées.",13,true));
                var limitActions=Ui.Actions(limits);limitActions.Children.Add(Ui.AsyncButton("Actualiser les limites",async()=>{await UsageCoordinator.Refresh(Context.Store,info.Account.Key,false,CancellationToken.None);await Refresh();},Error));limitActions.Children.Add(Ui.AsyncButton("Réinitialisations",()=>UsageEditor.Open(Context,Shell,info.Account.Key),Error));body.Children.Add(Ui.Card(limits));
            }
            var integration=new StackPanel();integration.Children.Add(Ui.Text("Intégration & ressources",18));integration.Children.Add(Ui.Text(info.Integration.Status??"Préparation à venir",13,true));
            if(DateTime.TryParse(info.Integration.Updated,out var updated))integration.Children.Add(Ui.Text("Dernière vérification : "+updated.ToLocalTime().ToString("dd/MM à HH:mm:ss")+(String.IsNullOrEmpty(info.Integration.InstalledVersion)?"":" · v"+info.Integration.InstalledVersion),12,true));
            var links=Ui.Actions(integration);links.Children.Add(Ui.Button("Voir les ressources",()=>ShowTab(1),true));
            var verify=Ui.AsyncButton(verifying?"Vérification en cours…":"Vérifier l’intégration",()=>Verify(info),Error);System.Windows.Automation.AutomationProperties.SetName(verify,"Vérifier l’intégration");verify.IsEnabled=!verifying&&!i.Archived;links.Children.Add(verify);body.Children.Add(Ui.Card(integration));
            body.Children.Add(Ui.Text("Dans Codex : « Délègue cette mission à "+i.Name+" » ou « Utilise les ressources de "+i.Name+" ».",14,true));
            var extra=new StackPanel();if(!i.IsLocal&&!i.Archived&&!info.State.Running){extra.Children.Add(Ui.AsyncButton("Archiver cette instance…",async()=>{if(!Ui.Confirm(Shell,"Archiver cette instance en conservant ses données ?","Gérer l’instance"))return;await Change(a=>{a.Instances.Archive(a.Data.Instances.Single(x=>x.Id==i.Id),true);return Task.CompletedTask;});},Error));}
            if(info.State.NetworkWarning||info.State.Phase=="error")extra.Children.Add(Ui.Text(info.State.Message,13,true));body.Children.Add(new Expander{Header="Gestion de l’instance",Content=extra,Margin=new Thickness(0,12,0,0)});
            var scroll=renderedAccount==i.Id&&detail.Content is ScrollViewer existing&&existing.Tag!=null?existing:new ScrollViewer();
            scroll.Content=body;scroll.Tag=stamp;renderedAccount=i.Id;detail.Content=scroll;
        }
        public override bool CanLeave()=>(cloud?.CanLeave()??true)&&(resources?.CanLeave()??true);
    }
}
