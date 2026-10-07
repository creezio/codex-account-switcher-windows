using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class InstanceResourcesView : Grid, IDisposable
    {
        private readonly DesktopContext context;
        private readonly ShellWindow shell;
        private readonly DesktopInstance owner;
        private readonly string ownerHome;
        private readonly InstanceResources service;
        private InstanceInventory inventory;
        private Dictionary<string,string> initial=new Dictionary<string,string>(),draft=new Dictionary<string,string>();
        private readonly Dictionary<string,DesktopInstance> clients;
        private readonly Dictionary<string,string> clientHomes;
        private string client,revision;
        private TunnelGrant[] legacyGrants=new TunnelGrant[0];
        private readonly ComboBox recipient,plugin;
        internal readonly TextBox Search=new TextBox();
        private readonly StackPanel rows=new StackPanel();
        private readonly TextBlock state=Ui.Text("",13,true),summary=Ui.Text("",13),pageLabel=Ui.Text("",12,true);
        private readonly Button save,previous,next;
        private readonly Button refresh;
        private bool changing,busy,disposed;
        private int page;
        private CancellationTokenSource discovery;
        private DateTime attempted;
        internal Func<bool> ConfirmDiscard;
        internal bool Dirty=>Snapshot(initial)!=Snapshot(draft);
        private static string Snapshot(Dictionary<string,string> value)=>String.Join("|",value.OrderBy(p=>p.Key).Select(p=>p.Key+":"+p.Value));
        internal InstanceResourcesView(DesktopContext context,ShellWindow shell,DesktopInstance owner)
        {
            this.context=context;this.shell=shell;this.owner=owner;service=new InstanceResources(context.Store);
            var accounts=context.Accounts();ownerHome=accounts.Instances.Home(owner);
            clients=accounts.Data.Instances.Where(i=>!i.Archived&&i.Id!=owner.Id&&!String.IsNullOrEmpty(i.AccountKey)).ToDictionary(i=>i.Id);
            clientHomes=clients.ToDictionary(p=>p.Key,p=>accounts.Instances.Home(p.Value));
            inventory=service.Cached(owner.Id,owner.AccountKey,ownerHome);
            state.Text=String.IsNullOrEmpty(inventory.Updated)?"Ouvrez Codex pour détecter les ressources de ce compte.":$"Dernier inventaire · {inventory.Plugins.Count} plugins · {inventory.Resources.Count} ressources";
            ConfirmDiscard=()=>Ui.Confirm(shell,"Abandonner les changements d'accès non enregistrés ?","Accès en cours de modification");
            RowDefinitions.Add(new RowDefinition());RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var content=new StackPanel();var top=new StackPanel();content.Children.Add(top);
            var title=new DockPanel();top.Children.Add(title);
            refresh=Ui.AsyncButton("Actualiser",()=>Discover(true),e=>state.Text=Program.SafeError(e));title.Children.Add(refresh);
            DockPanel.SetDock(refresh,Dock.Right);state.VerticalAlignment=VerticalAlignment.Center;title.Children.Add(state);
            var audience=new Grid{Margin=new Thickness(0,6,0,4)};audience.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});audience.ColumnDefinitions.Add(new ColumnDefinition());
            var audienceLabel=Ui.Text("Partager avec",13);audienceLabel.Margin=new Thickness(0,0,12,10);audienceLabel.VerticalAlignment=VerticalAlignment.Center;audience.Children.Add(audienceLabel);
            recipient=new ComboBox();System.Windows.Automation.AutomationProperties.SetName(recipient,"Configurer les accès pour");Grid.SetColumn(recipient,1);audience.Children.Add(recipient);
            recipient.ItemsSource=clients.Values.Select(i=>new KeyValuePair<string,string>(i.Name,i.Id)).ToArray();recipient.DisplayMemberPath="Key";recipient.SelectedValuePath="Value";recipient.SelectedIndex=clients.Count>0?0:-1;
            recipient.ToolTip="Chaque instance dispose de ses propres accès. Cochez les ressources ci-dessous puis enregistrez.";top.Children.Add(audience);
            var filters=new Grid();filters.ColumnDefinitions.Add(new ColumnDefinition());filters.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(180)});top.Children.Add(filters);
            Search.Margin=new Thickness(0,0,12,8);filters.Children.Add(Ui.SearchField(Search,"Rechercher une ressource"));
            plugin=new ComboBox{DisplayMemberPath="Key",SelectedValuePath="Value"};Grid.SetColumn(plugin,1);filters.Children.Add(plugin);System.Windows.Automation.AutomationProperties.SetName(plugin,"Filtrer les plugins");
            var actions=Ui.Actions(top);actions.Children.Add(Ui.Button("Cocher les résultats affichés",()=>{if(busy)return;foreach(var r in Visible().Where(Selectable))if(!draft.ContainsKey(r.Key))draft[r.Key]="read";Render();}));actions.Children.Add(Ui.Button("Tout décocher",()=>{if(busy)return;draft.Clear();Render();}));
            content.Children.Add(rows);var scroll=new ScrollViewer{Content=content};Children.Add(scroll);
            var bottom=new StackPanel{Margin=new Thickness(0,10,0,0)};Grid.SetRow(bottom,1);Children.Add(bottom);bottom.Children.Add(summary);
            var footer=Ui.Actions(bottom);save=Ui.Button("Enregistrer les accès",async()=>{try{await Save();}catch(Exception e){state.Text=Program.SafeError(e);}finally{UpdateSummary();}},true);footer.Children.Add(save);
            footer.Children.Add(Ui.Button("Annuler les changements",()=>{if(busy)return;draft=new Dictionary<string,string>(initial);Render();}));
            previous=Ui.Button("Précédentes",()=>{page--;Render();});next=Ui.Button("Suivantes",()=>{page++;Render();});footer.Children.Add(previous);footer.Children.Add(pageLabel);footer.Children.Add(next);
            recipient.SelectionChanged+=delegate{if(changing)return;if(!CanLeave()){changing=true;recipient.SelectedValue=client;changing=false;return;}client=recipient.SelectedValue as string;LoadSelection();};
            Search.TextChanged+=delegate{page=0;Render();scroll.ScrollToTop();};plugin.SelectionChanged+=delegate{page=0;Render();scroll.ScrollToTop();};
            client=recipient.SelectedValue as string;LoadFilters();LoadSelection();
        }
        private void LoadFilters()
        {string old=plugin.SelectedValue as string;plugin.ItemsSource=new[]{new KeyValuePair<string,string>("Tous les plugins","")}.Concat(inventory.Plugins.Select(p=>new KeyValuePair<string,string>(p.Name,p.Key))).ToArray();plugin.SelectedValue=inventory.Plugins.Any(p=>p.Key==old)?old:"";}
        private void LoadSelection()
        {initial=client==null?new Dictionary<string,string>():service.Selections(inventory,client);draft=new Dictionary<string,string>(initial);revision=client==null?null:service.Revision(owner.Id,client);legacyGrants=new ToolTunnel(context.Store).Grants().Where(g=>g.Enabled&&g.Instance==owner.Id&&String.IsNullOrEmpty(g.CatalogClient)).ToArray();page=0;Render();}
        private IEnumerable<InstanceResource> Filtered()
        {string selected=plugin.SelectedValue as string;return inventory.Resources.Where(r=>(String.IsNullOrEmpty(selected)||r.Plugin==selected)&&(r.Title+" "+r.Kind).IndexOf(Search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).OrderBy(r=>r.Kind).ThenBy(r=>r.Title);}
        private InstanceResource[] Visible()=>Filtered().Skip(page*40).Take(40).ToArray();
        private bool Legacy(InstanceResource r)
        {
            if(client==null)return false;
            return legacyGrants.Any(g=>g.Sources.Any(s=>RelayStore.SamePath(s.Key,clientHomes[client])&&s.Value==clients[client].AccountKey)&&g.Tools.Any(t=>InstanceResources.PluginKey(t)==r.Plugin)&&(String.IsNullOrEmpty(g.ResourceField)||(g.ResourceField==r.Field&&(g.ResourceValue==r.Value||(g.ResourceValues?.Contains(r.Value)??false)))));
        }
        private bool Selectable(InstanceResource r)=>client!=null&&!busy&&!r.NativeEditor&&InstanceResources.Fresh(inventory)&&!Legacy(r);
        private void UpdateSummary()
        {
            int added=draft.Keys.Except(initial.Keys).Count(),removed=initial.Keys.Except(draft.Keys).Count(),changed=draft.Count(p=>initial.ContainsKey(p.Key)&&initial[p.Key]!=p.Value);
            summary.Text=client==null?"Créez une autre instance pour configurer ses accès.":Dirty?$"À enregistrer · {added} ajout(s), {removed} retrait(s), {changed} droit(s) modifié(s)":$"{draft.Count} ressource(s) partagée(s) avec {clients[client].Name}";
            save.IsEnabled=client!=null&&Dirty&&!busy;
        }
        private void Render()
        {
            if(disposed||save==null)return;rows.Children.Clear();int total=Filtered().Count();page=Math.Max(0,Math.Min(page,Math.Max(0,(total-1)/40)));
            previous.IsEnabled=page>0;next.IsEnabled=(page+1)*40<total;pageLabel.Text=total==0?"":$"{page*40+1}–{Math.Min(total,(page+1)*40)} / {total}";
            previous.Visibility=next.Visibility=pageLabel.Visibility=total>40?Visibility.Visible:Visibility.Collapsed;
            var unmatched=draft.Keys.Where(k=>!inventory.Resources.Any(r=>r.Key==k)).ToArray();
            if(unmatched.Length>0){rows.Children.Add(Ui.Text($"{unmatched.Length} ressource(s) déjà partagée(s) absente(s) de cet inventaire. Actualisez avant de modifier ces accès, ou utilisez Tout décocher pour les révoquer.",13));}
            foreach(var resource in Visible()){
                bool legacy=Legacy(resource);var card=new StackPanel();var heading=new DockPanel();card.Children.Add(heading);
                var check=new CheckBox{IsChecked=draft.ContainsKey(resource.Key),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,3,12,0),IsEnabled=Selectable(resource)};
                System.Windows.Automation.AutomationProperties.SetName(check,"Partager "+resource.Title);DockPanel.SetDock(check,Dock.Left);heading.Children.Add(check);
                var text=new StackPanel();text.Children.Add(Ui.Text(resource.Title,16));string pluginName=inventory.Plugins.FirstOrDefault(p=>p.Key==resource.Plugin)?.Name??resource.Kind;
                text.Children.Add(Ui.Text(resource.Kind+" · "+pluginName+" · "+resource.Access,12,true));heading.Children.Add(text);
                var choices=new ComboBox{ItemsSource=resource.CanModify?new[]{"Lecture","Lecture et modification"}:new[]{"Lecture"},SelectedIndex=draft.TryGetValue(resource.Key,out var mode)&&mode=="edit"?1:0,IsEnabled=check.IsEnabled&&check.IsChecked==true,MaxWidth=250,HorizontalAlignment=HorizontalAlignment.Left};
                System.Windows.Automation.AutomationProperties.SetName(choices,"Droits pour "+resource.Title);choices.Width=resource.CanModify?184:90;DockPanel.SetDock(choices,Dock.Right);heading.Children.Insert(1,choices);
                if(resource.Kind=="Site")card.Children.Add(Ui.Text("Modification des métadonnées uniquement. La publication d’une nouvelle version reste indisponible.",12,true));
                if(resource.NativeEditor)card.Children.Add(Ui.Text("Éditeur natif requis · cette ressource n’est pas partageable par le tunnel actuel.",12,true));
                if(legacy){card.Children.Add(Ui.Text("Accès géré par une règle avancée existante.",12,true));card.Children.Add(Ui.Button("Voir la règle avancée",()=>shell.Navigate("Outils partagés")));}
                check.Checked+=delegate{draft[resource.Key]=choices.SelectedIndex==1?"edit":"read";choices.IsEnabled=true;UpdateSummary();};check.Unchecked+=delegate{draft.Remove(resource.Key);choices.IsEnabled=false;UpdateSummary();};
                choices.SelectionChanged+=delegate{if(check.IsChecked==true){draft[resource.Key]=choices.SelectedIndex==1?"edit":"read";UpdateSummary();}};
                var resourceCard=Ui.Card(card);resourceCard.Padding=new Thickness(12);resourceCard.Margin=new Thickness(0,0,0,8);rows.Children.Add(resourceCard);
            }
            if(total==0)rows.Children.Add(Ui.Text(String.IsNullOrWhiteSpace(inventory.Updated)?"Les ressources apparaîtront après la détection du compte.":"Aucune ressource correspondant à cette recherche.",15,true));
            foreach(var p in inventory.Plugins.Where(p=>p.State!="ready"&&(String.IsNullOrEmpty(plugin.SelectedValue as string)||p.Key==(string)plugin.SelectedValue))){
                var item=new StackPanel();item.Children.Add(Ui.Text(p.Name+" · "+p.Tools+" outils",16));item.Children.Add(Ui.Text(p.Message,13,true));
                if(p.State=="unsupported")item.Children.Add(Ui.AsyncButton("Choisir ses opérations…",async()=>{if(!CanLeave())return;await ((ToolSharingPage)shell.Pages["Outils partagés"]).Edit(null,owner.Id);LoadSelection();},e=>state.Text=Program.SafeError(e)));
                rows.Children.Add(Ui.Card(item));
            }
            UpdateSummary();
        }
        internal async Task Discover(bool force=false)
        {
            if(disposed||busy||context.Fixture||(!force&&(InstanceResources.Fresh(inventory)||DateTime.UtcNow-attempted<TimeSpan.FromMinutes(1))))return;
            if(Dirty){state.Text="Enregistrez ou annulez les changements avant d’actualiser.";return;}
            attempted=DateTime.UtcNow;busy=true;refresh.IsEnabled=false;Render();state.Text="Détection des plugins, Sites et Pages du compte…";discovery=new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try{var found=await service.Discover(owner.Id,discovery.Token);if(disposed)return;inventory=found;LoadFilters();LoadSelection();state.Text=$"{inventory.Plugins.Count} plugins connectés · {inventory.Resources.Count} ressources détectées · {DateTime.Now:t}";}
            catch(OperationCanceledException){if(!disposed)state.Text="Détection interrompue. Le dernier inventaire est conservé.";}
            catch(Exception e){if(!disposed)state.Text=Program.SafeError(e)+" Le dernier inventaire est conservé.";}
            finally{busy=false;discovery?.Dispose();discovery=null;if(!disposed){refresh.IsEnabled=true;Render();}}
        }
        private async Task Save()
        {
            if(client==null||busy||!Dirty)return;busy=true;recipient.IsEnabled=false;Render();state.Text="Vérification et enregistrement des accès…";
            try{
                var target=clients[client];await service.Save(inventory,client,clientHomes[client],target.AccountKey,new Dictionary<string,string>(draft),revision,CancellationToken.None);
                LoadSelection();state.Text="Accès enregistrés. Ils seront appliqués aux prochains appels de cette instance.";
            }finally{busy=false;recipient.IsEnabled=true;Render();}
        }
        internal bool CanLeave()
        {if(busy&&discovery!=null){discovery.Cancel();return true;}if(busy){state.Text="Enregistrement en cours. Attendez sa fin avant de changer d’instance.";return false;}if(!Dirty)return true;if(!ConfirmDiscard())return false;draft=new Dictionary<string,string>(initial);Render();return true;}
        public void Dispose(){disposed=true;discovery?.Cancel();}
    }
}
