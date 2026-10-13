using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Creezio.Switcher.Desktop
{
    internal sealed class PerformanceSettings {public int Interval {get;set;}=5;}
    internal sealed class ResourceRow:INotifyPropertyChanged
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Account {get;set;}
        public string State {get;set;}
        public string Ram {get;set;}
        public string Commit {get;set;}
        public string Cpu {get;set;}
        public double? CpuNumber {get;set;}
        public string Activity {get;set;}
        public int Count {get;set;}
        public long Bytes {get;set;}
        public long CommittedBytes {get;set;}
        internal ResourceGroup Group;
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Changed(){PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(""));}
    }
    internal sealed class ResourceProcessRow:INotifyPropertyChanged
    {
        public int Pid {get;set;} public int Parent {get;set;}
        public string Name {get;set;} public string Role {get;set;}
        public string Ram {get;set;} public string Commit {get;set;} public string Cpu {get;set;}
        public string Started {get;set;} public string Association {get;set;}
        public long? MemoryBytes {get;set;} public long? CommitBytes {get;set;} public double? CpuNumber {get;set;}
        internal long Identity;
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Changed(){PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(""));}
    }
    internal sealed class PerformancePage:PageView
    {
        internal readonly DataGrid Instances=Table(),Processes=Table();
        private readonly ObservableCollection<ResourceRow> groups=new ObservableCollection<ResourceRow>();
        private readonly ObservableCollection<ResourceProcessRow> processes=new ObservableCollection<ResourceProcessRow>();
        private readonly Dictionary<string,ResourceActivity> activities=new Dictionary<string,ResourceActivity>();
        private readonly TextBlock systemRam=Ui.Text("—",24),codexRam=Ui.Text("—",24),cpu=Ui.Text("—",24),count=Ui.Text("—",24);
        private readonly TextBlock systemHint=Ui.Text("",12,true),detail=Ui.Text("Choisissez une instance",19),activityText=Ui.Text("",13,true),recommendation=Ui.Text("",13,true),stamp=Ui.Text("",12,true);
        private readonly Queue<double> history=new Queue<double>();
        private readonly Canvas graph=new Canvas{Height=38,MinWidth=150,ClipToBounds=true};
        internal readonly CheckBox Pause=new CheckBox{Content="Mettre en pause",VerticalAlignment=VerticalAlignment.Center};
        internal readonly ComboBox Interval=new ComboBox{ItemsSource=new[]{"5 secondes","10 secondes","30 secondes"},Width=148};
        private readonly Button show,close,restart,check;
        private ResourceSnapshot snapshot;
        private int interval;
        private bool loading,acting,activityLoading;
        internal Func<string,bool> ConfirmAction;
        internal PerformancePage(DesktopContext context,ShellWindow shell):base(context,shell,"Performances","Comprendre la consommation de chaque instance et décider quoi fermer.")
        {
            interval=context.Store.ReadRecord<PerformanceSettings>("performance-settings.dpapi").Interval;if(!new[]{5,10,30}.Contains(interval))interval=5;
            Command("Actualiser",()=>RefreshNow());
            Interval.SelectedIndex=interval==5?0:interval==10?1:2;
            System.Windows.Automation.AutomationProperties.SetName(Interval,"Fréquence d’actualisation");
            Commands.Children.Add(Interval);Pause.Margin=new Thickness(16,0,0,8);Commands.Children.Add(Pause);
            Interval.SelectionChanged+=delegate{interval=new[]{5,10,30}[Math.Max(0,Interval.SelectedIndex)];context.Store.WriteRecord("performance-settings.dpapi",new PerformanceSettings{Interval=interval});};
            var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(0.42,GridUnitType.Star),MinHeight=165});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(0.58,GridUnitType.Star),MinHeight=110});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var scroll=new ScrollViewer{Content=layout,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Body.Children.Add(scroll);
            Body.SizeChanged+=delegate{layout.Height=Math.Max(Body.ActualWidth<760?900:650,Body.ActualHeight);};
            var cards=new WrapPanel();cards.Children.Add(Card("Mémoire du PC",systemRam,systemHint));cards.Children.Add(Card("RAM des instances Codex",codexRam,Ui.Text("Mémoire résidente · pages partagées incluses",11,true)));cards.Children.Add(Card("CPU des instances Codex",cpu,Ui.Text("Part du CPU total du PC",11,true)));cards.Children.Add(Card("Processus Codex suivis",count,Ui.Text("Chaque PID est compté une seule fois",11,true)));layout.Children.Add(cards);
            Instances.ItemsSource=groups;Instances.MinHeight=100;Grid.SetRow(Instances,1);layout.Children.Add(Instances);
            Column(Instances,"Instance","Name",185);Column(Instances,"Compte","Account",155);Column(Instances,"État","State",85);Column(Instances,"RAM","Ram",82,"Bytes");Column(Instances,"Engagée","Commit",95,"CommittedBytes");Column(Instances,"CPU","Cpu",65,"CpuNumber");Column(Instances,"Processus","Count",75);Column(Instances,"Activité","Activity",225);
            Instances.SelectionChanged+=delegate{RenderDetail();};
            var info=new StackPanel{Margin=new Thickness(0,16,0,0)};Grid.SetRow(info,2);layout.Children.Add(info);info.Children.Add(detail);info.Children.Add(activityText);info.Children.Add(recommendation);
            var actions=Ui.Actions(info);
            show=TaskButton("Afficher la fenêtre",Show);
            check=TaskButton("Vérifier l’activité",CheckActivity);
            close=TaskButton("Fermer l’instance",()=>Act(false));
            restart=TaskButton("Redémarrer",()=>Act(true));
            foreach(var button in new[]{show,check,close,restart})actions.Children.Add(button);
            Processes.ItemsSource=processes;Grid.SetRow(Processes,3);layout.Children.Add(Processes);
            Column(Processes,"PID","Pid",70);Column(Processes,"Processus","Name",170);Column(Processes,"Rôle","Role",235);Column(Processes,"RAM","Ram",90,"MemoryBytes");Column(Processes,"Mémoire engagée","Commit",130,"CommitBytes");Column(Processes,"CPU","Cpu",72,"CpuNumber");Column(Processes,"Parent","Parent",70);Column(Processes,"Démarré","Started",110);Column(Processes,"Attribution","Association",170);
            var footer=new StackPanel{Margin=new Thickness(0,10,0,0)};Grid.SetRow(footer,4);layout.Children.Add(footer);footer.Children.Add(stamp);footer.Children.Add(graph);
            graph.SizeChanged+=delegate{DrawGraph();};
            ConfirmAction=message=>Ui.Confirm(Shell,message,"Gestion de l’instance");
            System.Windows.Automation.AutomationProperties.SetName(Instances,"Consommation par instance");System.Windows.Automation.AutomationProperties.SetName(Processes,"Processus de l’instance");RenderDetail();
        }
        private Button TaskButton(string text,Func<Task> action)
        {Button button=null;button=Ui.Button(text,async delegate{if(!button.IsEnabled)return;button.IsEnabled=false;try{await action();}catch(Exception e){Error(e);}finally{RenderDetail();}});return button;}
        private static DataGrid Table()
        {
            var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeight=36,ColumnHeaderHeight=34,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,EnableRowVirtualization=true,EnableColumnVirtualization=true,Margin=new Thickness(0,8,0,0)};
            grid.SetResourceReference(Control.BackgroundProperty,"SurfaceBrush");grid.SetResourceReference(Control.ForegroundProperty,"TextBrush");grid.SetResourceReference(Control.BorderBrushProperty,"LineBrush");grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty,"LineBrush");
            var rowStyle=new Style(typeof(DataGridRow));rowStyle.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("SurfaceBrush")));rowStyle.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("TextBrush")));var selected=new Trigger{Property=DataGridRow.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("AccentSoftBrush")));rowStyle.Triggers.Add(selected);grid.RowStyle=rowStyle;
            var headerStyle=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("CanvasBrush")));headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("MutedBrush")));headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,0,8,0)));grid.ColumnHeaderStyle=headerStyle;
            var cell=new Style(typeof(DataGridCell));cell.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(7,4,7,4)));cell.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));var selection=new Trigger{Property=DataGridCell.IsSelectedProperty,Value=true};selection.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("AccentSoftBrush")));selection.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("TextBrush")));cell.Triggers.Add(selection);grid.CellStyle=cell;
            return grid;
        }
        private static void Column(DataGrid table,string title,string property,double width,string sort=null){var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.NoWrap));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(property)));style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center));table.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(property),Width=width,SortMemberPath=sort??property,ElementStyle=style});}
        private static Border Card(string title,TextBlock value,TextBlock hint)
        {var stack=new StackPanel();stack.Children.Add(Ui.Text(title,12,true));stack.Children.Add(value);stack.Children.Add(hint);var border=Ui.Card(stack);border.Width=216;border.Margin=new Thickness(0,0,10,10);border.Padding=new Thickness(15);return border;}
        internal static string Memory(long bytes)=>bytes>=1024L*1024*1024?(bytes/1073741824d).ToString("0.00",CultureInfo.CurrentCulture)+" Gio":(bytes/1048576d).ToString("0.0",CultureInfo.CurrentCulture)+" Mio";
        private static string Cpu(double? value)=>value.HasValue?value.Value.ToString("0.0",CultureInfo.CurrentCulture)+" %":"—";
        internal Task RefreshNow()=>Load(true);
        public override Task Refresh()=>Load(false);
        private async Task Load(bool force)
        {
            if(loading||acting||!force&&(Pause.IsChecked==true||snapshot!=null&&(DateTime.UtcNow-snapshot.Captured).TotalSeconds<interval))return;
            loading=true;try{
                snapshot=await Context.Resources();string selected=(Instances.SelectedItem as ResourceRow)?.Id;
                foreach(var group in snapshot.Groups){ResourceActivity cached;if(activities.TryGetValue(group.Scope.Id,out cached)&&(group.Scope.State?.Running!=true||cached.Pid!=group.Scope.State.DesktopPid||cached.Started!=group.Scope.State.DesktopStartTicks))activities.Remove(group.Scope.Id);}
                var ids=new HashSet<string>();foreach(var group in snapshot.Groups){ids.Add(group.Scope.Id);var row=groups.FirstOrDefault(r=>r.Id==group.Scope.Id);if(row==null){row=new ResourceRow{Id=group.Scope.Id};groups.Add(row);}row.Group=group;row.Name=group.Scope.Name;row.Account=group.Scope.Account;row.State=group.Scope.State?.Caption??"État inconnu";row.Bytes=group.Resident;row.CommittedBytes=group.PrivateBytes;row.Ram=(group.Partial?"≥ ":"")+Memory(group.Resident);row.Commit=(group.Partial?"≥ ":"")+Memory(group.PrivateBytes);row.Cpu=(group.CpuPartial&&group.Cpu.HasValue?"≥ ":"")+Cpu(group.Cpu);row.Count=group.Processes.Length;row.Activity=Activity(row);row.Changed();}
                if(snapshot.Unassigned.Length>0){ids.Add("unassigned");var row=groups.FirstOrDefault(r=>r.Id=="unassigned");if(row==null){row=new ResourceRow{Id="unassigned"};groups.Add(row);}row.Group=new ResourceGroup{Scope=new ResourceScope{Id=row.Id,Name="Origine non vérifiée"},Processes=snapshot.Unassigned};row.Name="Origine non vérifiée";row.Account="Attribution impossible";row.State="À examiner";row.Bytes=snapshot.Unassigned.Sum(p=>p.Resident??0);row.Ram=Memory(row.Bytes);row.Cpu="—";row.Count=snapshot.Unassigned.Length;row.Activity="Aucune fermeture automatique";row.Changed();}
                foreach(var removed in groups.Where(r=>!ids.Contains(r.Id)).ToArray())groups.Remove(removed);
                foreach(string removed in activities.Keys.Where(id=>!ids.Contains(id)).ToArray())activities.Remove(removed);
                Instances.SelectedItem=groups.FirstOrDefault(r=>r.Id==selected)??groups.Where(r=>r.Id!="switcher"&&r.Id!="unassigned").OrderByDescending(r=>r.Bytes).FirstOrDefault()??groups.FirstOrDefault();
                var codex=snapshot.Groups.Where(g=>g.Scope.Id!="switcher").ToArray();long memory=codex.Sum(g=>g.Resident);codexRam.Text=(codex.Any(g=>g.Partial)?"≥ ":"")+Memory(memory);cpu.Text=codex.Any(g=>g.Cpu.HasValue)?(codex.Any(g=>g.CpuPartial)?"≥ ":"")+Cpu(codex.Sum(g=>g.Cpu??0)):codex.All(g=>g.Processes.Length==0)?"0 %":"Mesure en cours";count.Text=(codex.Sum(g=>g.Processes.Length)+snapshot.Unassigned.Length).ToString();
                if(snapshot.TotalPhysical>0){double used=100d*(snapshot.TotalPhysical-snapshot.AvailablePhysical)/snapshot.TotalPhysical;systemRam.Text=used.ToString("0")+" % utilisée";systemHint.Text=Memory(snapshot.AvailablePhysical)+" disponibles / "+Memory(snapshot.TotalPhysical);Notice.Text=used>=85?"La mémoire du PC est fortement sollicitée. Fermer une instance au repos peut réduire la pression mémoire.":"";}else{systemRam.Text="Non disponible";systemHint.Text="Windows n’a pas fourni cette mesure.";}
                history.Enqueue(memory/1048576d);while(history.Count>60)history.Dequeue();DrawGraph();stamp.Text="Mesuré à "+snapshot.Captured.ToLocalTime().ToString("HH:mm:ss")+" · collecte "+snapshot.CollectionMilliseconds.ToString("0")+" ms · RAM partagée susceptible d’être comptée dans plusieurs processus";
                foreach(var row in groups){row.CpuNumber=row.Group.Cpu;row.Changed();}
                RenderDetail();var current=Instances.SelectedItem as ResourceRow;ResourceActivity last;
                if(current?.Group.Scope.State?.Running==true&&current.Id!="switcher"&&(!activities.TryGetValue(current.Id,out last)||DateTime.UtcNow-last.Checked>TimeSpan.FromSeconds(30)))await CheckActivity();
            }finally{loading=false;}
        }
        private string Activity(ResourceRow row)
        {if(row.Id=="switcher")return "Supervision et relais";if(row.Group.Scope.State?.Running!=true)return row.Count>0?"Processus encore présents · état à vérifier":"Instance fermée";ResourceActivity activity;return activities.TryGetValue(row.Id,out activity)?activity.Message:"Activité non vérifiée";}
        private void RenderDetail()
        {
            var row=Instances.SelectedItem as ResourceRow;detail.Text=row==null?"Choisissez une instance":row.Name+" · "+row.Count+" processus · RAM "+row.Ram+(row.Id=="unassigned"?"":" · engagée "+row.Commit);activityText.Text=row==null?"":row.Activity;
            ResourceActivity activity=null;if(row!=null&&activities.TryGetValue(row.Id,out activity)&&activity.Titles.Length>0)activityText.Text+="\n"+String.Join(" · ",activity.Titles.Take(4));
            bool running=row?.Group.Scope.State?.Running==true,managed=row?.Group.Scope.Managed==true,primary=row?.Group.Scope.Primary==true;
            bool known=running&&row.Group.Scope.State.Phase!="unknown"&&row.Group.Processes.Any(p=>p.Pid==row.Group.Scope.State.DesktopPid&&p.Started>0&&p.Started==row.Group.Scope.State.DesktopStartTicks);
            show.IsEnabled=row!=null&&row.Id!="switcher"&&row.Id!="unassigned"&&!acting;check.IsEnabled=running&&row.Id!="switcher"&&!acting&&!activityLoading;
            close.IsEnabled=restart.IsEnabled=known&&managed&&!acting&&activity?.State!="busy";
            recommendation.Text=row==null?"":primary?"Instance principale : gérez sa fermeture directement dans Codex pour préserver cette conversation.":row.Id=="switcher"?"Fermer le Switcher n’arrête pas les fenêtres Codex déjà ouvertes.":row.Id=="unassigned"?"Une origine inconnue ne signifie pas qu’un processus est inutile. Aucun arrêt individuel n’est proposé.":!running?"Instance fermée. Afficher la fenêtre permet de la rouvrir.":activity?.State=="busy"?"Un chat travaille ou attend votre réponse. La fermeture et le redémarrage restent bloqués.":"Fermer ou redémarrer vérifie tous les chats avant de toucher uniquement à cette instance. Une faible utilisation CPU ne prouve pas qu’elle est au repos.";
            var source=row?.Group.Processes??new ResourceProcess[0];var identities=new HashSet<string>();foreach(var p in source){string key=p.Pid+":"+p.Started;identities.Add(key);var item=processes.FirstOrDefault(r=>r.Pid==p.Pid&&r.Identity==p.Started);if(item==null){item=new ResourceProcessRow{Pid=p.Pid,Identity=p.Started};processes.Add(item);}item.Parent=p.Parent;item.Name=p.Name;item.Role=p.Role??ProcessResources.Role(p.Name,null);item.Ram=p.Resident.HasValue?Memory(p.Resident.Value):"Non mesuré";item.Commit=p.PrivateBytes.HasValue?Memory(p.PrivateBytes.Value):"Non mesuré";item.Cpu=Cpu(p.CpuPercent);item.Started=p.Started>0?new DateTime(p.Started,DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss"):"Inconnu";item.Association=p.Association??"Non vérifiée";item.Changed();}
            foreach(var stale in processes.Where(p=>!identities.Contains(p.Pid+":"+p.Identity)).ToArray())processes.Remove(stale);
            foreach(var item in processes){var raw=source.Single(p=>p.Pid==item.Pid&&p.Started==item.Identity);item.MemoryBytes=raw.Resident;item.CommitBytes=raw.PrivateBytes;item.CpuNumber=raw.CpuPercent;item.Changed();}
        }
        private async Task CheckActivity()
        {
            var row=Instances.SelectedItem as ResourceRow;if(activityLoading||row?.Group.Scope.State?.Running!=true||row.Id=="switcher")return;
            activityLoading=true;try{var state=row.Group.Scope.State;activityText.Text="Lecture de l’activité des chats…";var found=await Context.ResourceActivity(row.Id);
            // Results from an older launch must never label a new launch as idle.
            var current=groups.FirstOrDefault(r=>r.Id==row.Id);if(current==null||current.Group.Scope.State.DesktopPid!=state.DesktopPid||current.Group.Scope.State.DesktopStartTicks!=state.DesktopStartTicks)return;
            found.Pid=state.DesktopPid;found.Started=state.DesktopStartTicks;activities[row.Id]=found;current.Activity=found.Message;current.Changed();}finally{activityLoading=false;RenderDetail();}
        }
        private async Task Show(){var row=Instances.SelectedItem as ResourceRow;if(row==null||acting)return;acting=true;RenderDetail();try{Notice.Text=await Context.OpenOrShowInstance(row.Id);}finally{acting=false;RenderDetail();}}
        private async Task Act(bool restart)
        {
            var row=Instances.SelectedItem as ResourceRow;if(row?.Group.Scope.Managed!=true||row.Group.Scope.State?.Running!=true)return;
            if(!ConfirmAction((restart?"Redémarrer":"Fermer")+" « "+row.Name+" » ? Son compte et ses conversations seront conservés. Les autres instances resteront ouvertes."))return;
            acting=true;RenderDetail();try{Notice.Text="Vérification complète des chats de « "+row.Name+" »…";await Context.RequireIdleResourceInstance(row.Id);Notice.Text=await Context.CloseOrRestartInstance(row.Id,restart,row.Group.Scope.State);activities.Remove(row.Id);}finally{acting=false;RenderDetail();}await RefreshNow();
        }
        private void DrawGraph()
        {
            graph.Children.Clear();if(history.Count<2)return;double width=Math.Max(1,graph.ActualWidth),height=graph.Height,max=Math.Max(1,history.Max());var line=new Polyline{StrokeThickness=2};line.SetResourceReference(Shape.StrokeProperty,"AccentBrush");int index=0;foreach(double value in history)line.Points.Add(new Point(width*index++/(history.Count-1),height-3-(height-6)*value/max));graph.Children.Add(line);graph.ToolTip="Évolution de la RAM résidente des instances Codex · jusqu’à 60 mesures · échelle ajustée";
        }
    }
}
