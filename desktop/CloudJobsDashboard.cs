using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    // This view is shared by the main navigation and the Cloud environment dialogs.
    internal sealed partial class CloudJobsDashboard : UserControl
    {
        private readonly DesktopContext context;
        private readonly ShellWindow shell;
        private readonly ObservableCollection<Row> rows=new ObservableCollection<Row>();
        private readonly ListBox list=new ListBox();
        private readonly ICollectionView view;
        private readonly Grid detail=new Grid(),split=new Grid();
        private readonly Grid header=new Grid(),toolbar=new Grid(),listPanel=new Grid();
        private readonly UniformGrid metrics=new UniformGrid{Columns=4};
        private readonly Border detailCard;
        private readonly Button backToList;
        private StackPanel headerCommands;
        private readonly StackPanel summary=new StackPanel(),journal=new StackPanel();
        private readonly TextBlock notice=Ui.Text("",12,true),error=Ui.Text("",13),title=Ui.Text("",21),account=Ui.Text("",13,true),missionState=Ui.Text("",12),notificationState=Ui.Text("",12);
        private readonly TextBlock cycleState=Ui.Text("",12),cycleInfo=Ui.Text("",13);
        private readonly Border cycleBadge;
        private readonly FrameworkElement cycleSection;
        private readonly Button parentButton;
        private readonly TextBlock empty=Ui.Text("Aucune mission pour cette vue.",14,true),emptyDetail=Ui.Text("Sélectionnez une mission pour suivre son résultat et le retour à l’orchestrateur.",15,true);
        private readonly TextBox search=new TextBox{MinHeight=36,Margin=new Thickness(0,0,12,0)};
        private readonly ComboBox accounts=new ComboBox{MinWidth=160,MaxWidth=250,DisplayMemberPath="Label",Margin=new Thickness(0)};
        private readonly TextBlock[] counts=new TextBlock[4];
        private readonly Button[] filters=new Button[4];
        private readonly TextBox result=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalContentAlignment=VerticalAlignment.Top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new Thickness(0)};
        private readonly TextBlock references=Ui.Text("",12,true),lifecycle=Ui.Text("",13),route=Ui.Text("",14),branch=Ui.Text("",13),runSettings=Ui.Text("",13),notification=Ui.Text("",13),jobError=Ui.Text("",13);
        private readonly Expander diagnostic;
        private readonly WrapPanel jobActions;
        private readonly Button checkButton,workerButton,cancelButton;
        private readonly ScrollViewer detailScroll,journalScroll;
        private readonly TabControl tabs=new TabControl{BorderThickness=new Thickness(0),Background=Brushes.Transparent};
        private CancellationTokenSource lifetime=new CancellationTokenSource();
        private readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
        private bool loading,closed,changing,composing,acting,compact,detailPane;
        private int generation,filter;
        private string detailStamp,selectedId,requestedSelection;
        internal Func<Task<CloudJob[]>> JobsFixture;
        internal Func<CloudJob,CloudJobMonitorSnapshot> MonitoringFixture;
        internal Func<string,CancellationToken,Task<CloudConfiguration[]>> EnvironmentsFixture;
        internal Func<CloudJobSpec,CancellationToken,Task<CloudJob>> SubmitFixture;
        internal Func<string,CancellationToken,Task<CloudJob>> AdvanceFixture;
        internal Action EnsureWorkerFixture;
        internal Func<DateTimeOffset> ClockFixture;
        internal Action<string> OpenGithubFixture;
        internal ListBox JobsList=>list;
        internal ScrollViewer DetailsScroll=>detailScroll;
        internal ScrollViewer TimelineScroll=>journalScroll;
        internal TextBox ResultText=>result;
        internal TextBox Search=>search;
        internal ComboBox AccountFilter=>accounts;
        internal TabControl DetailTabs=>tabs;
        internal int ActiveView=>filter;
        internal string SelectedJobId=>(list.SelectedItem as Row)?.Job.Id;
        internal bool RefreshTimerRunning=>timer.IsEnabled;
        internal bool Refreshing=>loading;
        internal Task RefreshNow()=>Refresh();
        private sealed class AccountChoice{public string Id{get;set;}public string Label{get;set;}}
        private sealed class Row:INotifyPropertyChanged
        {
            public string Title{get;set;}public string AccountLine{get;set;}public string Route{get;set;}public string Scope{get;set;}public string StateCaption{get;set;}public string ReturnCaption{get;set;}public string CycleCaption{get;set;}public string Recency{get;set;}public Visibility CycleVisibility{get;set;}
            public CloudJob Job{get;set;}public CloudJobMonitorSnapshot Monitor{get;set;}
            public event PropertyChangedEventHandler PropertyChanged;
            internal void Changed()=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(""));
        }
        internal CloudJobsDashboard(DesktopContext context,ShellWindow shell)
        {
            this.context=context;this.shell=shell;SetResourceReference(ForegroundProperty,"TextBrush");
            var root=new Grid();for(int n=0;n<4;n++)root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());Content=root;
            header.Margin=new Thickness(0,0,0,14);header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.Children.Add(header);
            var heading=new StackPanel();heading.Children.Add(Ui.Text("Délégations Cloud",26));heading.Children.Add(Ui.Text("Le Switcher suit les missions et prévient le chat qui les a confiées.",13,true));header.Children.Add(heading);
            headerCommands=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Top};Grid.SetColumn(headerCommands,1);header.Children.Add(headerCommands);headerCommands.Children.Add(Ui.AsyncButton("Nouvelle mission Cloud",Compose,Error,true));headerCommands.Children.Add(Ui.AsyncButton("Actualiser",Refresh,Error));
            metrics.Margin=new Thickness(0,0,0,14);Grid.SetRow(metrics,1);root.Children.Add(metrics);
            var captions=new[]{"En cours","À traiter","Retours en attente","Missions notifiées"};
            for(int n=0;n<counts.Length;n++)
            {
                var body=new StackPanel();counts[n]=Ui.Text("0",25);counts[n].FontWeight=FontWeights.SemiBold;counts[n].Margin=new Thickness(0,0,0,2);body.Children.Add(counts[n]);var label=Ui.Text(captions[n],12,true);label.Margin=new Thickness(0);body.Children.Add(label);
                var card=Ui.Card(body);card.Padding=new Thickness(14,10,14,10);card.Margin=new Thickness(0,0,n==3?0:10,0);metrics.Children.Add(card);AutomationProperties.SetName(counts[n],captions[n]);if(n==3)card.ToolTip="Missions dont la dernière notification a été acceptée par Codex ; traitement par l’orchestrateur non confirmé.";
            }
            toolbar.Margin=new Thickness(0,0,0,10);toolbar.ColumnDefinitions.Add(new ColumnDefinition());toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});toolbar.Children.Add(Ui.SearchField(search,"Rechercher une mission, une branche ou un lot"));Grid.SetColumn(accounts,1);toolbar.Children.Add(accounts);Grid.SetRow(toolbar,2);root.Children.Add(toolbar);AutomationProperties.SetName(accounts,"Filtrer les missions par compte exécutant");
            var status=new DockPanel{LastChildFill=true,Margin=new Thickness(0,0,0,10)};Grid.SetRow(status,3);root.Children.Add(status);var views=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(views,Dock.Left);status.Children.Add(views);
            var viewCaptions=new[]{"Toutes","En cours","À traiter","Historique"};for(int n=0;n<filters.Length;n++){int index=n;filters[n]=Ui.Button(viewCaptions[n],()=>SetView(index));filters[n].Padding=new Thickness(12,6,12,6);filters[n].MinHeight=32;filters[n].Margin=new Thickness(0,0,6,0);views.Children.Add(filters[n]);}
            var messages=new StackPanel{HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};notice.Margin=error.Margin=new Thickness(0);messages.Children.Add(notice);messages.Children.Add(error);status.Children.Add(messages);
            AutomationProperties.SetName(notice,"État du suivi des missions Cloud");AutomationProperties.SetLiveSetting(notice,AutomationLiveSetting.Polite);AutomationProperties.SetName(error,"Erreur des missions Cloud");AutomationProperties.SetLiveSetting(error,AutomationLiveSetting.Polite);
            split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(330),MinWidth=245,MaxWidth=440});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(14)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star),MinWidth=320});Grid.SetRow(split,4);root.Children.Add(split);
            listPanel.Children.Add(list);empty.HorizontalAlignment=HorizontalAlignment.Center;empty.VerticalAlignment=VerticalAlignment.Center;empty.Margin=new Thickness(24);empty.IsHitTestVisible=false;listPanel.Children.Add(empty);split.Children.Add(listPanel);
            list.BorderThickness=new Thickness(0);list.SetResourceReference(BackgroundProperty,"CanvasBrush");list.HorizontalContentAlignment=HorizontalAlignment.Stretch;ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);list.ItemTemplate=RowTemplate();AutomationProperties.SetName(list,"Missions Cloud et comptes exécutants");
            detailCard=Ui.Card(detail);detailCard.Padding=new Thickness(18);detailCard.Margin=new Thickness(0);Grid.SetColumn(detailCard,2);split.Children.Add(detailCard);detail.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});detail.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});detail.RowDefinitions.Add(new RowDefinition());
            var detailHeader=new StackPanel();backToList=Ui.Button("← Missions",()=>{detailPane=false;ResponsiveLayout();});backToList.Padding=new Thickness(8,4,8,4);backToList.Margin=new Thickness(0,0,0,8);backToList.Visibility=Visibility.Collapsed;detailHeader.Children.Add(backToList);detailHeader.Children.Add(title);detailHeader.Children.Add(account);var badges=new WrapPanel();badges.Children.Add(Badge(missionState));badges.Children.Add(Badge(notificationState));cycleBadge=Badge(cycleState);cycleBadge.Visibility=Visibility.Collapsed;badges.Children.Add(cycleBadge);detailHeader.Children.Add(badges);detail.Children.Add(detailHeader);jobActions=new WrapPanel{Margin=new Thickness(0,6,0,8)};Grid.SetRow(jobActions,1);detail.Children.Add(jobActions);
            AutomationProperties.SetName(cycleState,"Cycle de correction");
            AutomationProperties.SetName(missionState,"État observé de la mission Cloud");
            checkButton=Ui.AsyncButton("Vérifier la progression",()=>Check((list.SelectedItem as Row)?.Job),Error);workerButton=Ui.AsyncButton("Démarrer le suivi",EnsureWorker,Error);cancelButton=Ui.AsyncButton("Annuler cette mission",async()=>{var job=(list.SelectedItem as Row)?.Job;if(job==null)return;await context.Read(service=>new CloudJobs(context.Store).CancelQueued(job.Id));await Refresh();},Error);
            foreach(var button in new[]{checkButton,workerButton,cancelButton}){button.Padding=new Thickness(10,6,10,6);button.Margin=new Thickness(0,0,6,6);jobActions.Children.Add(button);}
            var cycleBody=new StackPanel();cycleBody.Children.Add(cycleInfo);parentButton=Ui.Button("Voir la mission précédente",()=>SelectParentMission());parentButton.Padding=new Thickness(10,6,10,6);cycleBody.Children.Add(parentButton);cycleSection=Section("CYCLE DE CORRECTION",cycleBody);cycleSection.Visibility=Visibility.Collapsed;
            detailScroll=new ScrollViewer{Content=summary,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};summary.Children.Add(cycleSection);summary.Children.Add(Section("TRAJET",route));summary.Children.Add(Section("SUIVI AUTOMATIQUE",lifecycle));summary.Children.Add(Section("NOTIFICATION À L’ORCHESTRATEUR",notification));summary.Children.Add(jobError);summary.Children.Add(Section("BRANCHE ET PÉRIMÈTRE",branch));summary.Children.Add(Section("EXÉCUTION OBSERVÉE",runSettings));diagnostic=new Expander{Header="Références techniques",Content=references,Margin=new Thickness(0,6,0,8)};summary.Children.Add(diagnostic);
            journalScroll=new ScrollViewer{Content=journal,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            var tabStyle=TabStyle();foreach(var tab in new[]{new TabItem{Header="Chronologie",Content=journalScroll},new TabItem{Header="Résultat",Content=result},new TabItem{Header="Détails",Content=detailScroll}}){tab.Style=tabStyle;tabs.Items.Add(tab);}tabs.Margin=new Thickness(0,4,0,0);Grid.SetRow(tabs,2);detail.Children.Add(tabs);emptyDetail.Margin=new Thickness(30);emptyDetail.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(emptyDetail,2);split.Children.Add(emptyDetail);detail.Visibility=Visibility.Collapsed;AutomationProperties.SetName(result,"Résultat de la mission Cloud");
            InitializeTimelinePresentation(detailHeader);
            view=CollectionViewSource.GetDefaultView(rows);view.Filter=Matches;list.ItemsSource=view;search.TextChanged+=delegate{if(!changing)ApplyFilter();};accounts.SelectionChanged+=delegate{if(!changing)ApplyFilter();};list.SelectionChanged+=delegate{if(!changing)ShowSelected();};SizeChanged+=delegate{ResponsiveLayout();};
            list.PreviewMouseLeftButtonUp+=(sender,args)=>{if(compact&&list.SelectedItem!=null&&ItemsControl.ContainerFromElement(list,args.OriginalSource as DependencyObject) is ListBoxItem)OpenSelectedDetails();};list.PreviewKeyDown+=(sender,args)=>{if(compact&&args.Key==System.Windows.Input.Key.Enter){OpenSelectedDetails();args.Handled=true;}};
            timer.Tick+=async delegate{try{await Refresh();}catch(Exception exception){Error(exception);}};
            Loaded+=async delegate{closed=false;generation++;if(lifetime.IsCancellationRequested){lifetime.Dispose();lifetime=new CancellationTokenSource();}try{await Refresh();}catch(Exception exception){Error(exception);}finally{if(!closed)timer.Start();}};
            Unloaded+=delegate{Suspend();};PaintViews();
        }
        internal void Suspend(){closed=true;generation++;timer.Stop();lifetime.Cancel();}
        internal void OpenSelectedDetails(){if(list.SelectedItem==null)return;detailPane=true;ResponsiveLayout();}
        internal bool SelectParentMission()
        {
            var current=(list.SelectedItem as Row)?.Job;
            var parent=rows.FirstOrDefault(row=>row.Job.Id==current?.ParentId);
            if(current?.Kind!="follow-up"||parent==null)return false;
            changing=true;
            try
            {
                search.Text="";accounts.SelectedIndex=0;filter=0;PaintViews();view.Refresh();list.SelectedItem=parent;
                empty.Visibility=view.IsEmpty?Visibility.Visible:Visibility.Collapsed;
            }
            finally{changing=false;}
            ShowSelected();list.ScrollIntoView(parent);OpenSelectedDetails();
            notice.Text="Mission précédente affichée · filtres réinitialisés.";
            return true;
        }
        private void ResponsiveLayout()
        {
            compact=ActualWidth<760||(ActualWidth<1000&&ActualHeight<600);bool showDetail=compact&&detailPane&&SelectedJobId!=null;
            Grid.SetColumn(headerCommands,compact?0:1);Grid.SetRow(headerCommands,compact?1:0);Grid.SetColumnSpan(headerCommands,compact?2:1);headerCommands.Margin=new Thickness(0,compact?4:0,0,0);
            metrics.Columns=ActualWidth<650?2:4;foreach(Border card in metrics.Children)card.Margin=new Thickness(0,0,8,metrics.Columns==2?8:0);
            metrics.Visibility=toolbar.Visibility=showDetail?Visibility.Collapsed:Visibility.Visible;
            header.Visibility=showDetail?Visibility.Collapsed:Visibility.Visible;
            split.ColumnDefinitions[0].MinWidth=compact?0:245;split.ColumnDefinitions[0].MaxWidth=compact?Double.PositiveInfinity:440;split.ColumnDefinitions[0].Width=compact?(showDetail?new GridLength(0):new GridLength(1,GridUnitType.Star)):new GridLength(ActualWidth<950?270:350);split.ColumnDefinitions[1].Width=new GridLength(compact?0:14);split.ColumnDefinitions[2].MinWidth=compact?0:320;split.ColumnDefinitions[2].Width=compact?(showDetail?new GridLength(1,GridUnitType.Star):new GridLength(0)):new GridLength(1,GridUnitType.Star);
            listPanel.Visibility=showDetail?Visibility.Collapsed:Visibility.Visible;detailCard.Visibility=compact&&!showDetail?Visibility.Collapsed:Visibility.Visible;emptyDetail.Visibility=!compact&&SelectedJobId==null?Visibility.Visible:Visibility.Collapsed;backToList.Visibility=showDetail?Visibility.Visible:Visibility.Collapsed;
        }
        private static FrameworkElement Section(string label,UIElement content)
        {var stack=new StackPanel{Margin=new Thickness(0,10,0,4)};var caption=Ui.Text(label,10,true);caption.FontWeight=FontWeights.SemiBold;caption.Margin=new Thickness(0,0,0,6);stack.Children.Add(caption);stack.Children.Add(content);return stack;}
        private static Border Badge(TextBlock text)
        {text.Margin=new Thickness(0);text.FontWeight=FontWeights.SemiBold;var border=new Border{Child=text,CornerRadius=new CornerRadius(5),Padding=new Thickness(8,5,8,5),Margin=new Thickness(0,0,8,2)};border.SetResourceReference(Border.BackgroundProperty,"AccentSoftBrush");text.SetResourceReference(TextBlock.ForegroundProperty,"AccentTextBrush");return border;}
        private static Style TabStyle()
        {
            var style=new Style(typeof(TabItem));style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("MutedBrush")));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));style.Setters.Add(new Setter(Control.FontSizeProperty,13d));
            var template=new ControlTemplate(typeof(TabItem));var underline=new FrameworkElementFactory(typeof(Border));underline.Name="Underline";underline.SetValue(Border.BorderThicknessProperty,new Thickness(0,0,0,2));underline.SetResourceReference(Border.BorderBrushProperty,"LineBrush");var header=new FrameworkElementFactory(typeof(ContentPresenter));header.SetValue(ContentPresenter.ContentSourceProperty,"Header");header.SetValue(System.Windows.Documents.TextElement.FontWeightProperty,FontWeights.SemiBold);header.SetValue(MarginProperty,new Thickness(12,8,12,9));underline.AppendChild(header);template.VisualTree=underline;
            var selected=new Trigger{Property=TabItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("AccentTextBrush")));selected.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("AccentBrush"),"Underline"));template.Triggers.Add(selected);style.Setters.Add(new Setter(Control.TemplateProperty,template));return style;
        }
        private static DataTemplate RowTemplate()
        {
            var template=new DataTemplate(typeof(Row));var stack=new FrameworkElementFactory(typeof(StackPanel));stack.SetValue(MarginProperty,new Thickness(4,4,4,6));
            foreach(var field in new[]{"Title","CycleCaption","Route","AccountLine","Scope","StateCaption","ReturnCaption","Recency"}){var label=new FrameworkElementFactory(typeof(TextBlock));label.SetBinding(TextBlock.TextProperty,new Binding(field));label.SetBinding(ToolTipProperty,new Binding(field));if(field=="CycleCaption")label.SetBinding(VisibilityProperty,new Binding("CycleVisibility"));label.SetValue(TextBlock.TextWrappingProperty,field=="Title"?TextWrapping.Wrap:TextWrapping.NoWrap);label.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);label.SetValue(TextBlock.FontSizeProperty,field=="Title"?14d:12d);label.SetValue(TextBlock.FontWeightProperty,field=="Title"||field=="StateCaption"||field=="CycleCaption"?FontWeights.SemiBold:FontWeights.Normal);label.SetValue(MarginProperty,new Thickness(0,0,0,field=="Title"?7:3));label.SetResourceReference(TextBlock.ForegroundProperty,field=="Title"?"TextBrush":field=="StateCaption"||field=="CycleCaption"?"AccentTextBrush":"MutedBrush");stack.AppendChild(label);}template.VisualTree=stack;return template;
        }
        internal static string State(string state)
        {
            switch(state){case"queued":return"En attente";case"creating":return"Création du chat Cloud";case"prepared":return"Chat préparé";case"sending":return"Envoi du mandat";case"running":return"En cours";case"attention":return"Intervention requise";case"completed":return"Terminée";case"failed":return"Échec";case"uncertain":return"Envoi à réconcilier";case"cancelled":return"Annulée";default:return"État à vérifier";}
        }
        internal static string NotificationCaption(string state)
        {
            switch(state){case"none":return"Retour non demandé";case"waiting":return"Retour à la fin du travail";case"pending":return"Notification en attente";case"source-busy":return"Orchestrateur occupé";case"source-offline":return"Orchestrateur hors ligne";case"permissions":return"Permissions à vérifier";case"sending":return"Notification en cours d’envoi";case"delivered":return"Notification envoyée";case"uncertain":return"Envoi à vérifier";case"attention":return"Notification bloquée";default:return"État de notification à vérifier";}
        }
        private static string NotificationCaption(CloudJobMonitorSnapshot monitor)=>monitor.NotificationKind=="intervention"&&monitor.NotificationState=="delivered"?"Intervention signalée":NotificationCaption(monitor.NotificationState);
        private static string CycleCaption(CloudJob job)=>job.Kind=="follow-up"?(job.FollowUpNumber.GetValueOrDefault()>0?"Correction n°"+job.FollowUpNumber.Value.ToString(CultureInfo.CurrentCulture):"Suite de mission"):"";
        private static string Lot(string value)=>"Lot "+(value.StartsWith("lot-",StringComparison.OrdinalIgnoreCase)?value.Substring(4):value);
        private static bool Running(CloudJob job)=>new[]{"queued","creating","prepared","sending","running","reserved"}.Contains(job.State);
        private bool CurrentRunning(Row row)=>Running(row.Job)&&!CloudMissionPresentation.NeedsCurrentRead(row.Job,row.Monitor,PresentationNow);
        private bool Attention(Row row)=>new[]{"attention","failed","uncertain"}.Contains(row.Job.State)||new[]{"uncertain","attention","source-offline","permissions"}.Contains(row.Monitor.NotificationState)||CloudMissionPresentation.NeedsCurrentRead(row.Job,row.Monitor,PresentationNow);
        private static bool Waiting(Row row)=>new[]{"pending","source-busy","source-offline","permissions","sending","uncertain","attention"}.Contains(row.Monitor.NotificationState);
        private bool Matches(object value)
        {
            var row=(Row)value;if(filter==1&&!CurrentRunning(row)||filter==2&&!Attention(row)||filter==3&&!new[]{"completed","failed","cancelled"}.Contains(row.Job.State))return false;if(accounts.SelectedItem is AccountChoice choice&&choice.Id!=null&&row.Job.AccountKey!=choice.Id)return false;
            string needle=search.Text.Trim();return needle.Length==0||new[]{row.Title,row.Route,row.AccountLine,row.CycleCaption,row.Job.Branch,row.Job.Lot,row.Job.Feature,row.Job.Repository,row.Job.ParentId,row.Job.RootJobId}.Any(text=>(text??"").IndexOf(needle,StringComparison.CurrentCultureIgnoreCase)>=0);
        }
        internal void SetView(int index){if(index<0||index>=filters.Length)throw new ArgumentOutOfRangeException(nameof(index));filter=index;PaintViews();ApplyFilter();}
        private void PaintViews(){for(int n=0;n<filters.Length;n++){filters[n].SetResourceReference(Control.BackgroundProperty,n==filter?"AccentSoftBrush":"CanvasBrush");filters[n].SetResourceReference(Control.ForegroundProperty,n==filter?"AccentTextBrush":"MutedBrush");filters[n].SetResourceReference(Control.BorderBrushProperty,n==filter?"AccentBrush":"CanvasBrush");filters[n].FontWeight=n==filter?FontWeights.SemiBold:FontWeights.Normal;}}
        private void ApplyFilter(){string id=SelectedJobId;changing=true;try{view.Refresh();list.SelectedItem=view.Cast<Row>().FirstOrDefault(row=>row.Job.Id==id)??view.Cast<Row>().FirstOrDefault();empty.Visibility=view.IsEmpty?Visibility.Visible:Visibility.Collapsed;if(list.SelectedItem!=null)list.ScrollIntoView(list.SelectedItem);}finally{changing=false;}ShowSelected();ResponsiveLayout();}
        private void Error(Exception exception){if(!closed)error.Text=Program.SafeError(exception);}
        private async Task Refresh()
        {
            if(loading||closed)return;loading=true;int currentGeneration=generation;
            try
            {
                var jobs=context.Fixture&&JobsFixture!=null?await JobsFixture():await context.Read(service=>new CloudJobs(context.Store).List());
                var incoming=await context.Read(service=>jobs.Select(job=>
                {
                    var snapshot=context.Fixture?MonitoringFixture?.Invoke(job)??new CloudJobMonitorSnapshot{NotificationState=job.ReturnToSource?(job.ReturnState=="none"?"waiting":job.ReturnState):"none",Events=new CloudJobMonitorEvent[0]}:new CloudJobMonitoring(context.Store).Read(job);
                    var target=service.Data.Instances.FirstOrDefault(instance=>instance.Id==job.InstanceId)?.Name??job.InstanceName??"Instance indisponible";var source=String.IsNullOrEmpty(job.SourceThread)?"Envoi depuis Switcher":(!String.IsNullOrEmpty(job.SourceAccount)?service.Data.Instances.FirstOrDefault(instance=>instance.AccountKey==job.SourceAccount)?.Name:null)??job.SourceAccount??"Chat orchestrateur";
                    return new Row{Job=job,Monitor=snapshot,Title=job.Title,StateCaption=CloudMissionPresentation.StateCaption(job,snapshot,PresentationNow),ReturnCaption=NotificationCaption(snapshot),CycleCaption=CycleCaption(job),CycleVisibility=job.Kind=="follow-up"?Visibility.Visible:Visibility.Collapsed,AccountLine=service.Data.Profiles.FirstOrDefault(profile=>profile.Key==job.AccountKey)?.Email??"Compte indisponible",Route=source+" → "+target,Scope=!String.IsNullOrEmpty(job.Lot)?Lot(job.Lot)+(String.IsNullOrEmpty(job.Feature)?"":" · "+job.Feature):job.Repository??job.Branch??"Branche en préparation"};
                }).ToArray());
                if(closed||currentGeneration!=generation)return;foreach(var row in incoming)row.Recency=RecencyOf(row.Job,row.Monitor);string id=requestedSelection??SelectedJobId;var scroll=Ui.Descendants(list).OfType<ScrollViewer>().FirstOrDefault();double offset=scroll?.VerticalOffset??0;changing=true;
                bool filterChanged=false;var existing=rows.ToDictionary(row=>row.Job.Id,StringComparer.Ordinal);var wanted=new HashSet<string>(jobs.Select(job=>job.Id),StringComparer.Ordinal);foreach(var removed in rows.Where(row=>!wanted.Contains(row.Job.Id)).ToArray())rows.Remove(removed);
                for(int index=0;index<incoming.Length;index++)
                {
                    var next=incoming[index];if(!existing.TryGetValue(next.Job.Id,out var row)){rows.Insert(Math.Min(index,rows.Count),next);continue;}
                    bool changed=row.Title!=next.Title||row.AccountLine!=next.AccountLine||row.Route!=next.Route||row.Scope!=next.Scope||row.StateCaption!=next.StateCaption||row.ReturnCaption!=next.ReturnCaption||row.CycleCaption!=next.CycleCaption;filterChanged|=changed||Attention(row)!=Attention(next);bool ageChanged=row.Recency!=next.Recency;row.Job=next.Job;row.Monitor=next.Monitor;row.Title=next.Title;row.AccountLine=next.AccountLine;row.Route=next.Route;row.Scope=next.Scope;row.StateCaption=next.StateCaption;row.ReturnCaption=next.ReturnCaption;row.CycleCaption=next.CycleCaption;row.CycleVisibility=next.CycleVisibility;row.Recency=next.Recency;if(changed||ageChanged)row.Changed();int position=rows.IndexOf(row);if(position!=index)rows.Move(position,index);
                }
                string accountId=(accounts.SelectedItem as AccountChoice)?.Id;var choices=incoming.GroupBy(row=>row.Job.AccountKey).Select(group=>new AccountChoice{Id=group.Key,Label=group.First().AccountLine}).OrderBy(choice=>choice.Label,StringComparer.CurrentCultureIgnoreCase).ToArray();
                string oldAccounts=Json.Write(accounts.Items.Cast<AccountChoice>().Select(choice=>new{choice.Id,choice.Label}));string newAccounts=Json.Write(new[]{new AccountChoice{Label="Tous les comptes"}}.Concat(choices).Select(choice=>new{choice.Id,choice.Label}));if(oldAccounts!=newAccounts){accounts.ItemsSource=new[]{new AccountChoice{Label="Tous les comptes"}}.Concat(choices).ToArray();accounts.SelectedItem=accounts.Items.Cast<AccountChoice>().FirstOrDefault(choice=>choice.Id==accountId)??accounts.Items[0];}
                if(filterChanged)view.Refresh();list.SelectedItem=view.Cast<Row>().FirstOrDefault(row=>row.Job.Id==id)??view.Cast<Row>().FirstOrDefault();empty.Visibility=view.IsEmpty?Visibility.Visible:Visibility.Collapsed;
                counts[0].Text=rows.Count(CurrentRunning).ToString(CultureInfo.CurrentCulture);counts[1].Text=rows.Count(Attention).ToString(CultureInfo.CurrentCulture);counts[2].Text=rows.Count(Waiting).ToString(CultureInfo.CurrentCulture);counts[3].Text=rows.Count(row=>row.Monitor.NotificationState=="delivered").ToString(CultureInfo.CurrentCulture);
                changing=false;requestedSelection=null;list.UpdateLayout();scroll?.ScrollToVerticalOffset(offset);notice.Text="Registre local · "+DateTime.Now.ToString("HH:mm:ss");ShowSelected();ResponsiveLayout();
            }
            finally{changing=false;loading=false;if(currentGeneration!=generation&&!closed)_=Refresh();}
        }
        private static string Time(string value)=>DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var instant)?instant.ToLocalTime().ToString("dd/MM · HH:mm:ss",CultureInfo.CurrentCulture):"Non encore consignée";
        private void ShowSelected()
        {
            RefreshRelativeTimes();
            var row=list.SelectedItem as Row;var job=row?.Job;if(job==null){detail.Visibility=Visibility.Collapsed;emptyDetail.Visibility=Visibility.Visible;selectedId=detailStamp=null;return;}var monitor=row.Monitor;
            var parent=job.Kind=="follow-up"?rows.FirstOrDefault(value=>value.Job.Id==job.ParentId):null;
            string caption=CloudMissionPresentation.StateCaption(job,monitor,PresentationNow);string stamp=Json.Write(new{job,row.AccountLine,row.Route,monitor,acting,caption,ParentTitle=parent?.Title,ParentPresent=parent!=null,ParentResult=parent?.Job.Result,HasCheck=!context.Fixture||AdvanceFixture!=null});if(stamp==detailStamp)return;bool same=selectedId==job.Id;double offset=same?detailScroll.VerticalOffset:0,inner=same?result.VerticalOffset:0,journalOffset=same?journalScroll.VerticalOffset:0;var timelineAnchor=same?CaptureTimelineAnchor():null;int selectionStart=same?result.SelectionStart:0,selectionLength=same?result.SelectionLength:0;bool expanded=same&&diagnostic.IsExpanded;
            selectedId=job.Id;detailStamp=stamp;detail.Visibility=Visibility.Visible;emptyDetail.Visibility=Visibility.Collapsed;title.Text=job.Title;title.MaxHeight=58;title.ToolTip=job.Title;account.Text=row.AccountLine;missionState.Text=caption;notificationState.Text=NotificationCaption(monitor);
            bool follows=job.Kind=="follow-up";cycleState.Text=CycleCaption(job);cycleBadge.Visibility=cycleSection.Visibility=follows?Visibility.Visible:Visibility.Collapsed;
            cycleInfo.Text=follows?CycleCaption(job)+"\n"+(parent==null?"Mission précédente absente du suivi local.":"Suite de « "+parent.Title+" ».")+"\nMême conversation et même branche ; résultat et notification propres à ce cycle.":"";
            parentButton.Visibility=follows&&parent!=null?Visibility.Visible:Visibility.Collapsed;parentButton.IsEnabled=!acting;
            route.Text=row.Route+(String.IsNullOrEmpty(job.SourceThread)?"\nAucun chat de retour associé.":"\nLe retour cible le chat d’origine de cette mission.");
            lifecycle.Text=ReadLifecycle(job,monitor);
            string returnExplanation=monitor.NotificationState=="delivered"?"Le message a été accepté par Codex. Cela ne confirme pas que l’orchestrateur a terminé de le traiter.":monitor.NotificationState=="none"?"Aucun retour vers un chat orchestrateur n’a été demandé pour cette mission.":monitor.NotificationState=="uncertain"?"Le Switcher vérifie l’accusé de réception sans renvoyer le message pour éviter un doublon.":monitor.NotificationState=="permissions"?"Vérifiez les permissions du chat orchestrateur dans Codex. Le Switcher les contrôlera de nouveau sans les modifier.":monitor.NotificationState=="attention"?"Le journal ou l’identité de la destination doit être vérifié. Aucun nouvel envoi automatique.":"Le Switcher assure le retour ; l’agent Cloud n’a pas besoin d’envoyer lui-même de message.";
            notification.Text=NotificationCaption(monitor)+(String.IsNullOrEmpty(monitor.NotificationSentUtc)?monitor.NotificationState=="delivered"?" · horodatage indisponible":"":" · "+Time(monitor.NotificationSentUtc))+"\n"+returnExplanation+"\n"+monitor.NotificationAttempts+" tentative(s)"+(String.IsNullOrEmpty(monitor.NotificationNextAttemptUtc)?"":" · prochain contrôle "+Time(monitor.NotificationNextAttemptUtc))+(String.IsNullOrEmpty(monitor.LastNotificationError)?"":"\n"+monitor.LastNotificationError);
            jobError.Text=String.Join("\n",new[]{job.Error,monitor.LastCheckError}.Where(text=>!String.IsNullOrEmpty(text)).Distinct());jobError.Visibility=String.IsNullOrEmpty(jobError.Text)?Visibility.Collapsed:Visibility.Visible;
            branch.Text=(job.Branch??"Branche en préparation")+(String.IsNullOrEmpty(job.Lot)?"":"\n"+Lot(job.Lot)+" · "+(job.Feature??"Fonctionnalité non renseignée"))+(String.IsNullOrEmpty(job.Repository)?"":"\n"+job.Repository);
            var observed=job.ObservedExecution;runSettings.Text=observed==null?"Réglages natifs pas encore observés. Les préférences demandées ne prouvent pas leur application.":(observed.Model??"Modèle non fourni")+" · "+(observed.ReasoningEffort??"Effort non fourni")+"\n"+(observed.PermissionProfile==":danger-full-access"?"Accès complet":observed.PermissionProfile??"Permissions non fournies")+" · "+(job.ExecutionState=="verified"?"Vérifié":"À vérifier")+"\nObservé le "+Time(job.ExecutionObservedUtc);
            checkButton.Visibility=(!context.Fixture||AdvanceFixture!=null)&&new[]{"queued","prepared","running","attention"}.Contains(job.State)?Visibility.Visible:Visibility.Collapsed;workerButton.Visibility=cancelButton.Visibility=job.State=="queued"?Visibility.Visible:Visibility.Collapsed;foreach(var button in jobActions.Children.OfType<Button>())button.IsEnabled=!acting;
            string text=job.Result??(job.State=="uncertain"?"Aucun résultat de ce mandat n’est confirmé. L’envoi doit être réconcilié avec son message et son tour exact avant toute reprise.":"Le résultat apparaîtra à la fin du travail Cloud. Vérifiez les modifications et les preuves dans GitHub avant intégration.");if(result.Text!=text)result.Text=text;result.Select(Math.Min(selectionStart,result.Text.Length),Math.Min(selectionLength,Math.Max(0,result.Text.Length-selectionStart)));
            references.Text="Mission : "+job.Id+"\nEnvironnement : "+job.ConfigurationId+"\nConversation Cloud : "+(job.ThreadId??"Pas encore créée")+"\nTour de ce cycle : "+(job.TurnId??"Pas encore envoyé")+(follows?"\nMission précédente : "+(job.ParentId??"Non fournie")+"\nMission initiale : "+(job.RootJobId??"Non fournie")+"\nTour précédent : "+(job.ParentTurnId??"Non fourni"):"")+"\nChat source : "+(job.SourceThread??"Aucun")+"\nTraitement local du retour : "+(job.SourceReturnState??"Non observé");
            RenderTimeline(job,monitor);diagnostic.IsExpanded=expanded;if(!same)tabs.SelectedIndex=0;UpdateLayout();result.ScrollToVerticalOffset(inner);detailScroll.ScrollToVerticalOffset(offset);RestoreTimelineAnchor(timelineAnchor,journalOffset);
        }
        private string ReadLifecycle(CloudJob job,CloudJobMonitorSnapshot monitor)=>CloudMissionPresentation.KnownStateCaption(job,monitor,PresentationNow)+"\nDernière tentative de lecture : "+Time(monitor.LastCheckedUtc)+"\n"+(!String.IsNullOrEmpty(monitor.NextCheckUtc)?"Prochaine tentative : "+Time(monitor.NextCheckUtc)+" · cadence de 60 s":CloudJobWorker.RuntimePending(job)?"Lecture à reprendre · cadence de 60 s":new[]{"completed","failed","cancelled"}.Contains(job.State)?"Suivi de l’exécution terminé.":"Lecture à reprendre après rapprochement ou traitement de l’intervention.");
        private async Task Check(CloudJob job)
        {
            if(job==null||acting||closed)return;acting=true;ShowSelected();try{using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)){timeout.CancelAfter(TimeSpan.FromSeconds(45));if(context.Fixture){if(AdvanceFixture==null)throw new InvalidOperationException("Aucun appel Cloud réel depuis cette recette.");await AdvanceFixture(job.Id,timeout.Token);}else await new CloudJobs(context.Store).Advance(job.Id,timeout.Token);}await Refresh();}finally{acting=false;if(!closed)ShowSelected();}
        }
        private Task EnsureWorker()
        {if(context.Fixture){if(EnsureWorkerFixture==null)throw new InvalidOperationException("Aucun suivi Cloud réel depuis cette recette.");EnsureWorkerFixture();}else CloudJobWorker.Ensure(context.Store);error.Text="";notice.Text="Suivi démarré · aucun nouvel envoi créé.";return Task.CompletedTask;}
        internal async Task<CloudConfiguration[]> LoadEnvironments(CloudMissionAccountChoice choice, CancellationToken token)
        {
            var target = await context.Read(accounts =>
            {
                var instance = NamedInstances.Resolve(accounts, choice.Id);
                if (instance.AccountKey != choice.AccountKey) throw new InvalidOperationException("Le compte associé à cette instance a changé. Fermez cet assistant et rechargez les comptes avant une nouvelle mission.");
                return new { Accounts = accounts, Instance = instance };
            });
            if (context.Fixture) return EnvironmentsFixture == null ? new CloudConfiguration[0] : await EnvironmentsFixture(choice.Id, token);
            using (var client = CloudInstanceConnection.Open(target.Accounts, target.Instance))
            {
                var found = new Dictionary<string, CloudConfiguration>(StringComparer.Ordinal);
                foreach (var scope in new[] { "user", "workspace" })
                {
                    string cursor = null; var seen = new HashSet<string>(StringComparer.Ordinal);
                    do
                    {
                        var page = await client.List(scope, cursor, token);
                        foreach (var entry in page.Data)
                        {
                            if (found.ContainsKey(entry.Id) || entry.VersionRevision.GetValueOrDefault() <= 1) continue;
                            var full = await client.Get(entry.Id, token);
                            if (full.Status == "ready" && full.Draft == null && !String.IsNullOrEmpty(full.LatestReadyVersionId)) found.Add(full.Id, full);
                        }
                        cursor = page.NextCursor;
                        if (cursor != null && !seen.Add(cursor)) throw new InvalidOperationException("Pagination Cloud incompatible.");
                        if (seen.Count > 50) throw new InvalidOperationException("Catalogue Cloud trop volumineux.");
                    } while (cursor != null);
                }
                return found.Values.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }
        internal Task StartNewMission(string instanceId = null, string configurationId = null) => Compose(instanceId, configurationId);
        private Task Compose() => Compose(null, null);
        private async Task Compose(string instanceId, string configurationId)
        {
            if (composing || closed) return;
            composing = true;
            try
            {
                var instances = await context.Read(accounts => accounts.Data.Instances.Where(instance => !instance.Archived && !String.IsNullOrEmpty(instance.AccountKey)).Select(instance => new CloudMissionAccountChoice { Id = instance.Id, AccountKey = instance.AccountKey, Label = instance.Name + " · " + accounts.Data.Profiles.Where(profile => profile.Key == instance.AccountKey).Select(profile => profile.Email).FirstOrDefault() }).ToArray());
                if (closed) return;
                if (instances.Length == 0) throw new InvalidOperationException("Connectez un compte permanent à une instance avant une mission Cloud.");
                string workerError = null;
                var form = new CloudMissionWizard(Window.GetWindow(this) ?? shell, instances, LoadEnvironments, async (spec, token) =>
                {
                    await context.Read(accounts =>
                    {
                        var current = accounts.Data.Instances.SingleOrDefault(value => value.Id == spec.Instance && !value.Archived);
                        if (current == null || current.AccountKey != spec.ExpectedAccountKey) throw new InvalidOperationException("Le compte associé à cette instance a changé. Fermez cet assistant et rechargez les comptes avant une nouvelle mission.");
                        return true;
                    });
                    if (context.Fixture) { if (SubmitFixture == null) throw new InvalidOperationException("Aucune mission réelle depuis cette recette."); await SubmitFixture(spec, token); }
                    else await new CloudJobs(context.Store).Submit(spec, null, token);
                    try { await EnsureWorker(); } catch (Exception exception) { workerError = "Mission enregistrée une seule fois, mais suivi indisponible : " + Program.SafeError(exception) + " Utilisez Démarrer le suivi pour reprendre cette mission."; }
                }, lifetime.Token, instanceId, configurationId);
                form.ShowDialog();
                if (form.Saved && !closed) { requestedSelection = form.MissionId; search.Text="";accounts.SelectedIndex=0;SetView(0);await Refresh();if(compact)OpenSelectedDetails();if (workerError != null) error.Text = workerError; }
            }
            finally { composing = false; }
        }
    }
}




