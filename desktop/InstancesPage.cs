using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Creezio.Switcher.Desktop
{
    internal sealed class InstancesPage : PageView
    {
        internal readonly ListBox List=new ListBox();
        private readonly TextBox search=new TextBox();
        private readonly TextBlock heading=Ui.Text("Choisissez une instance",24),accountLine=Ui.Text("",14,true);
        private readonly ContentControl detail=new ContentControl();
        private readonly WrapPanel tabs=new WrapPanel();
        private readonly Button[] tabButtons=new Button[3];
        private readonly ComboBox compactChoice=new ComboBox{DisplayMemberPath="Title",Visibility=Visibility.Collapsed};
        private bool archived,changing;
        private int tab;
        private string selected;
        private string identity;
        private string listStamp;
        private Info current;
        private InstanceResourcesView resources;
        private sealed class Info
        {public DesktopInstance Instance;public Profile Account;public InstanceState State;public RelayIntegrationState Integration;public string Active;}
        internal InstancesPage(DesktopContext context,ShellWindow shell):base(context,shell,"Instances","Un compte, ses ressources et ses accès, réunis dans chaque espace Codex.")
        {
            Command("Créer une instance",async()=>{if(!CanLeave())return;await InstanceWizard.Open(Context,Shell);await Refresh();},true);
            Command("Archives",async()=>{if(!CanLeave())return;archived=!archived;await Refresh();});
            var split=new Grid();split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(.26,GridUnitType.Star),MinWidth=170});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(20)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(.74,GridUnitType.Star)});Body.Children.Add(split);
            var left=new DockPanel();split.Children.Add(left);var label=Ui.Text("VOS INSTANCES",11,true);DockPanel.SetDock(label,Dock.Top);left.Children.Add(label);var searchField=Ui.SearchField(search,"Rechercher une instance");DockPanel.SetDock(searchField,Dock.Top);left.Children.Add(searchField);left.Children.Add(List);
            System.Windows.Automation.AutomationProperties.SetName(search,"Rechercher une instance");System.Windows.Automation.AutomationProperties.SetName(List,"Vos instances");
            var template=new DataTemplate(typeof(ItemRow));var panel=new FrameworkElementFactory(typeof(StackPanel));
            foreach(string property in new[]{"Title","Summary","State"}){var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(property));text.SetValue(TextBlock.MarginProperty,new Thickness(0,0,0,6));text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);text.SetValue(TextBlock.FontSizeProperty,property=="Title"?16d:12d);if(property=="Title")text.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);if(property=="State")text.SetResourceReference(TextBlock.ForegroundProperty,"AccentTextBrush");panel.AppendChild(text);}template.VisualTree=panel;List.ItemTemplate=template;
            var right=new DockPanel();Grid.SetColumn(right,2);split.Children.Add(right);var header=new StackPanel();header.Children.Add(compactChoice);header.Children.Add(heading);header.Children.Add(accountLine);header.Children.Add(tabs);DockPanel.SetDock(header,Dock.Top);right.Children.Add(header);right.Children.Add(detail);
            System.Windows.Automation.AutomationProperties.SetName(compactChoice,"Instance sélectionnée");
            SizeChanged+=delegate{bool compact=ActualWidth<750;left.Visibility=compact?Visibility.Collapsed:Visibility.Visible;split.ColumnDefinitions[0].MinWidth=compact?0:170;split.ColumnDefinitions[0].Width=compact?new GridLength(0):new GridLength(.26,GridUnitType.Star);split.ColumnDefinitions[1].Width=new GridLength(compact?0:20);compactChoice.Visibility=compact?Visibility.Visible:Visibility.Collapsed;heading.Visibility=compact?Visibility.Collapsed:Visibility.Visible;Header.Children[1].Visibility=compact?Visibility.Collapsed:Visibility.Visible;};
            compactChoice.SelectionChanged+=delegate{if(changing)return;List.SelectedItem=compactChoice.SelectedItem;};
            for(int n=0;n<3;n++){int index=n;tabButtons[n]=Ui.Button(new[]{"Compte","Ressources & accès","Activité"}[n],()=>ShowTab(index));tabs.Children.Add(tabButtons[n]);}
            List.SelectionChanged+=async delegate{if(changing)return;try{var row=List.SelectedItem as ItemRow;if(row==null)return;if(row.Id!=selected&&!CanLeave()){changing=true;List.SelectedItem=List.Items.Cast<ItemRow>().FirstOrDefault(r=>r.Id==selected);compactChoice.SelectedItem=List.SelectedItem;changing=false;return;}changing=true;compactChoice.SelectedItem=row;changing=false;await Select(row);}catch(Exception e){changing=false;Error(e);}};
            search.TextChanged+=async delegate{try{if(!CanLeave())return;await Refresh();}catch(Exception e){Error(e);}};
            Shell.Closed+=delegate{resources?.Dispose();};
        }
        public override async Task Refresh()
        {
            var data=await Context.Read(a=>a.Data.Instances.Where(i=>archived||!i.Archived).Select(i=>{
                var state=Context.Fixture?new InstanceState{Running=false,Phase="stopped"}:a.Instances.Runtime.Probe(i);var profile=a.Data.Profiles.FirstOrDefault(p=>p.Key==i.AccountKey);var info=new Info{Instance=i,Account=profile,State=state,Active=Context.Fixture?i.AccountKey:a.Instances.ActiveKey(i),Integration=RelayIntegration.Status(Context.Store,a.Instances.Home(i))};
                return new ItemRow{Id=i.Id,Title=i.Name,Summary=profile?.Email??"Compte à connecter",State=i.Archived?"Archivée":i.AccountKey!=null&&info.Active!=i.AccountKey?"À reconnecter":state.Running?"Ouverte":i.IsLocal?"Session actuelle":"Fermée",Value=info};}).ToArray());
            var rows=data.Where(r=>(r.Title+" "+r.Summary).IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();
            if(resources?.Dirty==true)return;
            string fingerprint=Json.Write(rows.Select(r=>new{r.Id,r.Title,r.Summary,r.State}));
            if(fingerprint!=listStamp){listStamp=fingerprint;changing=true;List.ItemsSource=rows;List.SelectedItem=rows.FirstOrDefault(r=>r.Id==selected)??rows.FirstOrDefault();compactChoice.ItemsSource=rows;compactChoice.SelectedItem=List.SelectedItem;changing=false;}
            if(List.SelectedItem is ItemRow chosen)await Select(rows.First(r=>r.Id==chosen.Id));else if(selected!=null){resources?.Dispose();resources=null;selected=null;identity=null;current=null;heading.Text="Aucune instance";accountLine.Text="Créez une instance ou modifiez votre recherche.";detail.Content=null;}
        }
        private async Task Select(ItemRow row)
        {
            current=(Info)row.Value;string stamp=row.Id+"/"+current.Instance.AccountKey+"/"+current.Instance.Archived;
            bool changed=identity!=stamp;identity=stamp;heading.Text=row.Title;accountLine.Text=(current.Account?.Email??"Compte à connecter")+" · "+(current.Account?.Plan??"Connexion requise")+" · "+row.State;
            if(changed){resources?.Dispose();resources=null;selected=row.Id;if(current.Instance.AccountKey!=null&&!current.Instance.Archived)resources=new InstanceResourcesView(Context,Shell,current.Instance);ShowTab(tab);}
            else if(tab==0)RenderAccount();
            if(resources!=null)await resources.Discover();
        }
        internal async Task Open(string id,bool showResources=true)
        {Shell.Navigate("Instances");if(Shell.CurrentPage!="Instances")return;await Refresh();var row=List.Items.Cast<ItemRow>().FirstOrDefault(r=>r.Id==id);if(row!=null){if(row.Id!=selected&&!CanLeave())return;changing=true;List.SelectedItem=row;compactChoice.SelectedItem=row;changing=false;await Select(row);}if(showResources)ShowTab(1);}
        internal void ShowTab(int index)
        {
            tab=index;for(int n=0;n<tabButtons.Length;n++)tabButtons[n].SetResourceReference(FrameworkElement.StyleProperty,n==index?"PrimaryButton":typeof(Button));
            if(current==null)return;
            if(index==0)RenderAccount();
            else if(index==1)detail.Content=resources??(object)Ui.Card(Ui.Text("Connectez le compte de cette instance pour détecter ses ressources.",15,true));
            else{
                var activity=new StackPanel();var grants=new ToolTunnel(Context.Store).Grants();var calls=new ToolTunnel(Context.Store).Recent().Where(c=>c.Instance==selected||grants.Any(g=>g.Id==c.Grant&&g.Instance==selected)).Take(30).ToArray();
                activity.Children.Add(Ui.Text("Derniers appels aux outils de cette instance",18));
                foreach(var call in calls){var card=new StackPanel();card.Children.Add(Ui.Text(call.GrantName,15));card.Children.Add(Ui.Text(ToolSharingPage.State(call.State),13,true));card.Children.Add(Ui.Text(call.Updated,12,true));activity.Children.Add(Ui.Card(card));}
                if(calls.Length==0)activity.Children.Add(Ui.Text("Aucun appel enregistré pour cette instance.",14,true));detail.Content=new ScrollViewer{Content=activity};
            }
        }
        private void RenderAccount()
        {
            string stamp=Json.Write(new{current.Instance.Id,current.Instance.Name,current.Instance.Archived,current.Instance.AccountKey,current.Active,current.State.Running,current.State.Phase,current.State.Message,current.Integration.Status,quota=current.Account?.Quotas,fresh=current.Account?.IsFresh});
            if(detail.Content is ScrollViewer previous&&Object.Equals(previous.Tag,stamp))return;
            var info=current;var i=info.Instance;var body=new StackPanel();var account=new StackPanel();account.Children.Add(Ui.Text("COMPTE ASSOCIÉ",11,true));account.Children.Add(Ui.Text(info.Account?.Label??"À connecter",21));account.Children.Add(Ui.Text(info.Account?.Email??"Connectez un compte GPT pour préparer cet espace.",14,true));
            var actions=Ui.Actions(account);
            if(i.Archived)actions.Children.Add(Ui.AsyncButton("Restaurer l’instance",()=>Change(a=>{a.Instances.Archive(a.Data.Instances.Single(x=>x.Id==i.Id),false);return Task.CompletedTask;}),Error,true));
            else if(i.AccountKey==null)actions.Children.Add(Ui.AsyncButton("Connecter un compte",async()=>{await InstanceWizard.Open(Context,Shell,i.Id);await Refresh();},Error,true));
            else if(!i.IsLocal)actions.Children.Add(Ui.AsyncButton(info.State.Running?"Instance ouverte":"Ouvrir Codex",async()=>{if(info.State.Running)return;await Context.MaintainIntegrations(false,i.Id);await Change(a=>a.Instances.Start(a.Data.Instances.Single(x=>x.Id==i.Id),CancellationToken.None));},Error,true));
            actions.Children.Add(Ui.AsyncButton("Renommer",async()=>{string name=Ui.Prompt(Shell,"Renommer l’instance","Nom",i.Name);if(name!=null)await Change(a=>{a.Instances.Rename(a.Data.Instances.Single(x=>x.Id==i.Id),name);return Task.CompletedTask;});},Error));body.Children.Add(Ui.Card(account));
            if(info.Account!=null){
                var limits=new StackPanel();limits.Children.Add(Ui.Text("Limites d’utilisation",18));
                foreach(var bucket in info.Account.Quotas)foreach(var q in new[]{bucket.Primary,bucket.Secondary}.Where(q=>q!=null)){
                    limits.Children.Add(Ui.Text(q.Caption+" · "+ProductUx.Percent(q.Remaining)+" restants",15));limits.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=q.Remaining??0,Height=6,Margin=new Thickness(0,0,0,8)});limits.Children.Add(Ui.Text(q.ResetCaption+(info.Account.IsFresh?"":" · dernière valeur connue"),12,true));}
                if(info.Account.Quotas.Count==0)limits.Children.Add(Ui.Text("Les limites n’ont pas encore été actualisées.",13,true));
                var limitActions=Ui.Actions(limits);limitActions.Children.Add(Ui.AsyncButton("Actualiser les limites",async()=>{await UsageCoordinator.Refresh(Context.Store,info.Account.Key,false,CancellationToken.None);await Refresh();},Error));limitActions.Children.Add(Ui.AsyncButton("Réinitialisations",()=>UsageEditor.Open(Context,Shell,info.Account.Key),Error));body.Children.Add(Ui.Card(limits));
            }
            var integration=new StackPanel();integration.Children.Add(Ui.Text("Intégration & ressources",18));integration.Children.Add(Ui.Text(info.Integration.Status??"Préparation à venir",13,true));var links=Ui.Actions(integration);links.Children.Add(Ui.Button("Voir les ressources",()=>ShowTab(1),true));links.Children.Add(Ui.AsyncButton("Vérifier l’intégration",async()=>{await Context.MaintainIntegrations(true,i.Id,true);await Refresh();},Error));body.Children.Add(Ui.Card(integration));
            body.Children.Add(Ui.Text("Dans Codex : « Délègue cette mission à "+i.Name+" » ou « Utilise les ressources de "+i.Name+" ».",14,true));
            var extra=new StackPanel();if(!i.IsLocal&&!i.Archived){extra.Children.Add(Ui.AsyncButton(info.State.Running?"Fermer cette instance…":"Archiver cette instance…",async()=>{if(!Ui.Confirm(Shell,info.State.Running?"Fermer cette instance et interrompre ses tâches en cours ?":"Archiver cette instance en conservant ses données ?","Gérer l’instance"))return;await Change(async a=>{var target=a.Data.Instances.Single(x=>x.Id==i.Id);if(info.State.Running)await a.Instances.Runtime.Stop(target,CancellationToken.None);else a.Instances.Archive(target,true);});},Error));}
            if(info.State.NetworkWarning||info.State.Phase=="error")extra.Children.Add(Ui.Text(info.State.Message,13,true));body.Children.Add(new Expander{Header="Gestion de l’instance",Content=extra,Margin=new Thickness(0,12,0,0)});detail.Content=new ScrollViewer{Content=body,Tag=stamp};
        }
        public override bool CanLeave()=>resources?.CanLeave()??true;
    }
}
