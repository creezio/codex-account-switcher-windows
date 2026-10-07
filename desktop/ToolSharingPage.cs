using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class ToolSharingPage : CollectionPage
    {
        private bool history;
        internal ToolSharingPage(DesktopContext c,ShellWindow s):base(c,s,"Outils partagés","Utilisez les plugins d’une autre instance, directement depuis votre agent. Aucun prompt n’est envoyé au propriétaire.")
        {
            Command("Partager des outils",()=>Edit(null),true);
            Command("Mes partages",delegate{history=false;return Refresh();});
            Command("Historique des appels",delegate{history=true;return Refresh();});
        }
        private ToolTunnel Tunnel {get{return new ToolTunnel(Context.Store);}}
        public override Task Refresh()
        {
            if(history)Rows(Tunnel.Recent().Select(c=>new ItemRow{Id=c.Id,Title=c.GrantName,Summary=c.Tool,State=State(c.State),Revision=c.Updated,Value=c}));
            else Rows(Tunnel.Grants().Select(g=>new ItemRow{Id=g.Id,Title=g.Name,Summary=g.Tools.Count+" outil(s) · "+g.Sources.Count+" instance(s) cliente(s)",State=!g.Enabled?"Désactivé":String.IsNullOrEmpty(g.ResourceField)?"Actif · contenu du plugin":"Actif · ressource précise",Revision=g.Revision,Value=g}));
            Notice.Text=history?"Les réponses sont chiffrées et les identifiants sensibles masqués. Un appel incertain n’est jamais renvoyé automatiquement.":"Codex doit rester ouvert chez le propriétaire. Les transferts de fichiers et la publication de nouvelles versions Sites ne sont pas disponibles dans ce tunnel.";
            return Task.CompletedTask;
        }
        internal static string State(string value)
        {switch(value){case "completed":return "Réponse reçue";case "tool_error":return "Erreur du connecteur";case "blocked":return "Bloqué avant envoi";case "uncertain":return "Résultat incertain · à vérifier";case "executing":return "Envoyé · réponse en attente ou à vérifier";default:return "Préparation";}}
        protected override void ShowSelected()
        {
            Details.Children.Clear();var row=List.SelectedItem as ItemRow;if(row==null)return;
            Details.Children.Add(Ui.Text(row.Title,22));Details.Children.Add(Ui.Text(row.State,14,true));
            if(row.Value is TunnelGrant g){
                var owner=Context.Accounts().Data.Instances.FirstOrDefault(i=>i.Id==g.Instance);
                Details.Children.Add(Ui.Text("Propriétaire : "+(owner?.Name??g.Instance)));
                Details.Children.Add(Ui.Text(String.IsNullOrEmpty(g.ResourceField)?"Périmètre : contenu accessible aux outils sélectionnés.":g.ResourceValues?.Count>0?"Périmètre : "+g.ResourceValues.Count+" ressources sélectionnées":"Périmètre : "+g.ResourceField+" = "+g.ResourceValue,13,true));
                var actions=Ui.Actions(Details);actions.Children.Add(Ui.AsyncButton("Configurer",()=>String.IsNullOrEmpty(g.CatalogClient)?Edit(g):((InstancesPage)Shell.Pages["Instances"]).Open(g.Instance),Error));
                actions.Children.Add(Ui.AsyncButton("Désactiver",async delegate{Tunnel.Disable(g.Id);await Refresh();},Error));
                Details.Children.Add(Ui.Text("Dans Codex : « Utilise les outils de "+(owner?.Name??"cette instance")+" pour… »",14));
                foreach(var t in g.Tools)Details.Children.Add(Ui.Text((t.ReadOnly?"Lecture · ":"Action · ")+t.Name,13,true));
            }else if(row.Value is TunnelCall call){
                Details.Children.Add(Ui.Text(call.Tool,13));Details.Children.Add(Ui.Text("Appel : "+call.Id+"\nCréé : "+call.Created,12,true));
                if(!String.IsNullOrEmpty(call.Error))Details.Children.Add(Ui.Text(call.Error,14));
                Details.Children.Add(Ui.Text("Le chat émetteur peut récupérer la réponse avec read_shared_tool_result. Une réponse reçue ne confirme pas à elle seule la publication : l’agent vérifie aussi le reçu du service.",13,true));
            }
        }
        internal Task Edit(TunnelGrant old,string ownerId=null)
        {
            var accounts=Context.Accounts();var instances=accounts.Data.Instances.Where(i=>!i.Archived&&!String.IsNullOrEmpty(i.AccountKey)).ToArray();
            if(instances.Length<2)throw new InvalidOperationException("Préparez au moins deux instances avec un compte permanent.");
            var d=new EditWindow(Shell,old==null?"Partager les outils d’une instance":"Configurer le partage");
            var label=Ui.Input("Nom du partage",old?.Name??"",d.Fields);
            d.Fields.Children.Add(Ui.Text("Instance propriétaire",13));
            var owner=new ComboBox{ItemsSource=instances.Select(i=>new KeyValuePair<string,DesktopInstance>(i.Name,i)).ToArray(),DisplayMemberPath="Key",SelectedValuePath="Value",SelectedValue=instances.FirstOrDefault(i=>i.Id==(old?.Instance??ownerId))??instances[0]};d.Fields.Children.Add(owner);
            var info=Ui.Text("Chargez les plugins disponibles sur le compte propriétaire.",13,true);d.Fields.Children.Add(info);
            var group=Ui.Select("Plugin",new string[0],null,d.Fields);
            var write=Ui.Check("Afficher aussi les outils qui modifient ou publient",old?.Tools.Any(t=>!t.ReadOnly)??false,d.Fields);
            var scope=Ui.Select("Périmètre",new[]{"Contenu des outils sélectionnés","Un site précis","Une page précise","Champ personnalisé"},String.IsNullOrEmpty(old?.ResourceField)?"Contenu des outils sélectionnés":old.ResourceField=="project_id"?"Un site précis":old.ResourceField=="page_id"?"Une page précise":"Champ personnalisé",d.Fields);
            var resourcePanel=new StackPanel();d.Fields.Children.Add(resourcePanel);
            var customFieldPanel=new StackPanel();resourcePanel.Children.Add(customFieldPanel);
            var field=Ui.Input("Champ d’identifiant",old?.ResourceField??"",customFieldPanel);
            var resource=Ui.Input("Identifiant exact du site ou de la page",old?.ResourceValue??"",resourcePanel);
            d.Fields.Children.Add(Ui.Text("Outils autorisés · cochez les opérations utiles",13));
            var toolPanel=new StackPanel();var toolScroll=new ScrollViewer{Content=toolPanel,MaxHeight=210,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};d.Fields.Children.Add(toolScroll);
            d.Fields.Children.Add(Ui.Text("Instances clientes autorisées",13));var sources=new StackPanel();d.Fields.Children.Add(sources);
            var sourceChecks=new Dictionary<DesktopInstance,CheckBox>();var toolChecks=new Dictionary<TunnelTool,CheckBox>();
            TunnelTool[] catalog=old?.Tools.ToArray()??new TunnelTool[0];int generation=0;
            var selectedTools=new HashSet<string>(catalog.Select(t=>t.Server+"/"+t.Name));
            var selectionInfo=Ui.Text("",12,true);d.Fields.Children.Insert(d.Fields.Children.IndexOf(toolScroll),selectionInfo);
            Action summarize=()=>selectionInfo.Text=selectedTools.Count+" outil(s) sélectionné(s), tous plugins et filtres confondus.";
            var selectionActions=Ui.Actions(d.Fields);d.Fields.Children.Remove(selectionActions);d.Fields.Children.Insert(d.Fields.Children.IndexOf(toolScroll),selectionActions);
            selectionActions.Children.Add(Ui.Button("Sélectionner les outils affichés",()=>{foreach(var p in toolChecks.Where(p=>p.Value.IsEnabled)){p.Value.IsChecked=true;selectedTools.Add(p.Key.Server+"/"+p.Key.Name);}summarize();}));
            selectionActions.Children.Add(Ui.Button("Tout décocher",()=>{foreach(var p in toolChecks)p.Value.IsChecked=false;selectedTools.Clear();summarize();}));
            Action renderTools=delegate{
                toolPanel.Children.Clear();toolChecks.Clear();string prop=scope.SelectedIndex==0?"":scope.SelectedIndex==1?"project_id":scope.SelectedIndex==2?"page_id":field.Text.Trim();
                selectedTools.RemoveWhere(key=>!catalog.Any(t=>t.Server+"/"+t.Name==key&&ToolTunnel.Limitation(t).Length==0&&(prop.Length==0||Json.Obj(Json.Get(t.Schema,"properties")).ContainsKey(prop))));
                foreach(var t in catalog.Where(t=>t.Group==(string)group.SelectedItem&&(write.IsChecked==true||t.ReadOnly)&&(prop.Length==0||Json.Obj(Json.Get(t.Schema,"properties")).ContainsKey(prop)))){
                    string limitation=ToolTunnel.Limitation(t);var check=Ui.Check((limitation.Length>0?"Indisponible · ":t.ReadOnly?"Lecture · ":"Action · ")+t.Name,selectedTools.Contains(t.Server+"/"+t.Name),toolPanel);check.ToolTip=limitation.Length>0?limitation:t.Description;check.IsEnabled=limitation.Length==0;toolChecks[t]=check;
                    check.Click+=delegate{string key=t.Server+"/"+t.Name;if(check.IsChecked==true)selectedTools.Add(key);else selectedTools.Remove(key);summarize();};
                }
                summarize();
            };
            Action renderSources=delegate{
                sources.Children.Clear();sourceChecks.Clear();var selected=(DesktopInstance)owner.SelectedValue;
                foreach(var i in instances.Where(i=>i.Id!=selected.Id)){
                    string home=accounts.Instances.Home(i);sourceChecks[i]=Ui.Check(i.Name,old?.Sources.Any(p=>RelayStore.SamePath(p.Key,home)&&p.Value==i.AccountKey)??false,sources);
                }
            };
            Action renderScope=delegate{resourcePanel.Visibility=scope.SelectedIndex==0?Visibility.Collapsed:Visibility.Visible;customFieldPanel.Visibility=scope.SelectedIndex==3?Visibility.Visible:Visibility.Collapsed;field.IsEnabled=scope.SelectedIndex==3;if(scope.SelectedIndex==1)field.Text="project_id";else if(scope.SelectedIndex==2)field.Text="page_id";renderTools();};
            var load=Ui.AsyncButton("Charger les plugins",async delegate{
                int current=++generation;string id=((DesktopInstance)owner.SelectedValue).Id;info.Text="Lecture des outils du compte propriétaire…";
                var loaded=await Tunnel.Discover(id,CancellationToken.None);
                if(current!=generation)return;catalog=loaded;group.ItemsSource=catalog.Select(t=>t.Group).Distinct().ToArray();group.SelectedItem=old?.Tools.FirstOrDefault()?.Group;if(group.SelectedIndex<0)group.SelectedIndex=0;renderTools();info.Text=catalog.Length+" outils découverts. Sélectionnez le plugin puis les opérations à partager.";
                if(String.IsNullOrWhiteSpace(label.Text))label.Text=(string)group.SelectedItem+" via "+((DesktopInstance)owner.SelectedValue).Name;
            },e=>info.Text=Program.SafeError(e));d.Fields.Children.Insert(4,load);
            owner.SelectionChanged+=delegate{generation++;selectedTools.Clear();catalog=new TunnelTool[0];group.ItemsSource=new string[0];renderTools();renderSources();info.Text="Rechargez les plugins de cette instance.";};
            group.SelectionChanged+=delegate{renderTools();};write.Click+=delegate{renderTools();};scope.SelectionChanged+=delegate{renderScope();};field.TextChanged+=delegate{if(scope.SelectedIndex==3)renderTools();};
            group.ItemsSource=catalog.Select(t=>t.Group).Distinct().ToArray();if(group.Items.Count>0)group.SelectedIndex=0;renderSources();renderScope();
            d.Fields.Children.Add(Ui.Text("Les opérations sélectionnées seront autorisées aux instances cochées. Changer de périmètre retire les outils incompatibles. Les permissions du connecteur restent applicables. Les appels exigeant un fichier local restent indisponibles.",12,true));
            Func<string> draft=()=>Json.Write(new{label=label.Text,owner=((DesktopInstance)owner.SelectedValue).Id,scope=scope.SelectedIndex,field=field.Text,resource=resource.Text,tools=selectedTools.OrderBy(x=>x).ToArray(),sources=sourceChecks.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key.Id).OrderBy(x=>x).ToArray()});string initial=draft();d.Dirty=()=>draft()!=initial;
            d.Save=async delegate{
                var selected=(DesktopInstance)owner.SelectedValue;
                var grant=new TunnelGrant{Id=old?.Id??Guid.NewGuid().ToString("N"),Name=label.Text.Trim(),Instance=selected.Id,Enabled=true,ResourceField=scope.SelectedIndex==0?"":field.Text.Trim(),ResourceValue=scope.SelectedIndex==0?"":resource.Text.Trim(),Tools=catalog.Where(t=>selectedTools.Contains(t.Server+"/"+t.Name)).ToList(),Sources=sourceChecks.Where(p=>p.Value.IsChecked==true).ToDictionary(p=>accounts.Instances.Home(p.Key),p=>p.Key.AccountKey)};
                Tunnel.SaveGrant(grant);history=false;await Refresh();
            };
            d.ShowDialog();return Task.CompletedTask;
        }
    }
}
