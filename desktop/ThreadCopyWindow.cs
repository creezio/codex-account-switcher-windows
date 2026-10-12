using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Creezio.Switcher.Desktop
{
    internal sealed class ThreadCopyWindow:Window
    {
        private readonly DesktopContext context;
        private readonly DesktopInstance source;
        private readonly IThreadCopies service;
        private readonly StackPanel inputs=new StackPanel(),recent=new StackPanel();
        internal readonly ListBox Conversations=new ListBox{DisplayMemberPath="Title",Height=190};
        internal readonly ComboBox Destination=new ComboBox{DisplayMemberPath="Name"};
        internal readonly TextBox Query=new TextBox(),CopyTitle=new TextBox{MaxLength=160};
        internal readonly TextBlock Status=Ui.Text("",13);
        internal readonly Button CopyButton,MoreButton;
        private readonly TextBlock folder=Ui.Text("",12,true);
        private readonly Expander recentPanel;
        private readonly List<CopyThreadChoice> rows=new List<CopyThreadChoice>();
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private string cursor,loadedQuery;
        private bool busy;
        private ThreadCopyRecord pending;
        internal ThreadCopyWindow(DesktopContext context,Window owner,DesktopInstance source,IThreadCopies service=null)
        {
            this.context=context;this.source=source;this.service=service??new ThreadCopies(context.Store);
            Owner=owner;Title="Copier une conversation";Width=740;Height=Math.Min(820,SystemParameters.WorkArea.Height);MinWidth=500;MinHeight=550;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
            SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");FontSize=14;FontFamily=new System.Windows.Media.FontFamily("Segoe UI");
            var dock=new DockPanel{Margin=new Thickness(24)};dock.SetResourceReference(Panel.BackgroundProperty,"CanvasBrush");Content=dock;
            var header=new StackPanel();header.Children.Add(Ui.Text(Title,24));header.Children.Add(Ui.Text("Depuis « "+source.Name+" »",14,true));DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
            var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);footer.Children.Add(Status);
            System.Windows.Automation.AutomationProperties.SetLiveSetting(Status,System.Windows.Automation.AutomationLiveSetting.Polite);
            var actions=Ui.Actions(footer);CopyButton=Ui.Button("Copier et ouvrir dans Codex",async()=>{try{await Submit();}catch(Exception e){Error(e);}finally{Update();}},true);CopyButton.IsEnabled=false;actions.Children.Add(CopyButton);actions.Children.Add(Ui.Button("Fermer",Close));
            var body=new StackPanel();dock.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});body.Children.Add(inputs);
            inputs.Children.Add(Ui.Text("Conversation à copier",15));var searchRow=Ui.Actions(inputs);Query.Width=410;Query.MinWidth=170;searchRow.Children.Add(Query);searchRow.Children.Add(Ui.AsyncButton("Rechercher",()=>Load(false),Error));Query.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter&&!busy){e.Handled=true;try{await Load(false);}catch(Exception error){Error(error);}}};
            inputs.Children.Add(Conversations);Conversations.SelectionChanged+=delegate{var chosen=Conversations.SelectedItem as CopyThreadChoice;CopyTitle.Text=chosen==null?"":ShortTitle(chosen.Title);folder.Text=chosen?.Folder??"";Update();};
            MoreButton=Ui.AsyncButton("Afficher la suite",()=>Load(true),Error);MoreButton.Visibility=Visibility.Collapsed;inputs.Children.Add(MoreButton);inputs.Children.Add(folder);
            inputs.Children.Add(Ui.Text("Vers quelle instance ?",14));inputs.Children.Add(Destination);Destination.SelectionChanged+=delegate{Update();};
            inputs.Children.Add(Ui.Text("Titre de la copie",14));inputs.Children.Add(CopyTitle);CopyTitle.TextChanged+=delegate{Update();};
            body.Children.Add(Ui.Text("Les deux instances doivent être ouvertes et la conversation au repos. L’original est conservé. La copie travaille dans le même dossier de ce PC : ses fichiers sont partagés, pas dupliqués.",13,true));
            body.Children.Add(Ui.Text("Le compte et les permissions de la destination s’appliquent. Les accès aux pages, sites et plugins du compte source ne sont pas transférés. Les conversations cloud ne sont pas importables ici.",13,true));
            recentPanel=new Expander{Header="Copies récentes",Content=recent,Margin=new Thickness(0,12,0,12)};body.Children.Add(recentPanel);
            foreach(var pair in new[]{Tuple.Create<DependencyObject,string>(Query,"Rechercher une conversation"),Tuple.Create<DependencyObject,string>(Conversations,"Conversation à copier"),Tuple.Create<DependencyObject,string>(Destination,"Instance destinataire"),Tuple.Create<DependencyObject,string>(CopyTitle,"Titre de la copie")})System.Windows.Automation.AutomationProperties.SetName(pair.Item1,pair.Item2);
            Loaded+=async delegate{try{await Initialize();}catch(Exception e){Error(e);}};
            Closing+=(_,e)=>{if(busy){e.Cancel=true;Status.Text="L’opération est en cours. Attendez son résultat avant de fermer cette fenêtre.";}};
            Closed+=delegate{lifetime.Cancel();lifetime.Dispose();};
        }
        private static string ShortTitle(string value){value=(value??"Conversation")+" · copie";return value.Length>160?value.Substring(0,160):value;}
        internal async Task Initialize()
        {
            var destinations=await context.Read(a=>a.Data.Instances.Where(i=>i.Id!=source.Id&&!i.Archived&&i.AccountKey!=null).ToArray());Destination.ItemsSource=destinations;if(destinations.Length>0)Destination.SelectedIndex=0;
            RenderRecent();await Load(false);if(destinations.Length==0)Status.Text="Créez et connectez une autre instance pour recevoir la copie.";
        }
        private void Update(){if(CopyButton!=null)CopyButton.IsEnabled=!busy&&pending==null&&Conversations.SelectedItem!=null&&Destination.SelectedItem!=null&&!String.IsNullOrWhiteSpace(CopyTitle.Text);}
        private void Busy(bool value){busy=value;inputs.IsEnabled=!value&&pending==null;recent.IsEnabled=!value;Update();}
        private void Error(Exception e){Status.Text=Program.SafeError(e);}
        internal async Task Load(bool more)
        {
            if(busy||pending!=null)return;string query=more?loadedQuery:Query.Text;string next=more?cursor:null;Busy(true);Status.Text="Chargement des conversations…";
            try{var page=await service.List(source.Id,query,next,lifetime.Token);if(!more)rows.Clear();rows.AddRange(page.Threads.Where(t=>rows.All(r=>r.Id!=t.Id)));loadedQuery=query;cursor=page.Cursor;Conversations.ItemsSource=rows.ToArray();MoreButton.Visibility=String.IsNullOrEmpty(cursor)?Visibility.Collapsed:Visibility.Visible;Status.Text=rows.Count==0?"Aucune conversation locale trouvée. Terminez un premier échange dans Codex, puis recherchez à nouveau.":rows.Count+" conversation(s) · choisissez celle à copier.";}
            finally{Busy(false);}
        }
        internal async Task Submit()
        {
            if(busy||pending!=null)return;var choice=Conversations.SelectedItem as CopyThreadChoice;var destination=Destination.SelectedItem as DesktopInstance;if(choice==null||destination==null)return;
            var request=new ThreadCopyRecord{Id=Guid.NewGuid().ToString("N"),Source=source.Id,Destination=destination.Id,SourceThread=choice.Id,Title=CopyTitle.Text.Trim()};await RunCopy(request);
        }
        private async Task RunCopy(ThreadCopyRecord request)
        {
            if(busy)return;Busy(true);Status.Text="Préparation, copie et vérification de l’historique…";
            try{var result=await service.Copy(request.Id,request.Source,request.Destination,request.SourceThread,request.Title,lifetime.Token);pending=result;Status.Text=result.Message;if(result.State=="completed"){try{await service.Open(result,lifetime.Token);Status.Text="Copie vérifiée et ouverte dans Codex. L’original est conservé.";}catch(Exception e){Status.Text="La copie est créée. Son ouverture a échoué : "+Program.SafeError(e)+" Utilisez Ouvrir dans les copies récentes.";}}}
            catch(Exception e){pending=service.Recent(source.Id).FirstOrDefault(r=>r.Id==request.Id);Error(e);if(pending!=null)Status.Text=(pending.Message??"Vérification à terminer depuis Copies récentes.")+" "+Status.Text;}
            finally{Busy(false);RenderRecent();if(pending!=null){recentPanel.IsExpanded=true;_ = Dispatcher.BeginInvoke(new Action(()=>recentPanel.BringIntoView()));}}
        }
        private void RenderRecent()
        {
            recent.Children.Clear();var records=service.Recent(source.Id);
            foreach(var record in records){var row=new StackPanel();row.Children.Add(Ui.Text(record.Title,14));row.Children.Add(Ui.Text(record.Message??"Vérification de la copie à terminer.",12,true));var buttons=Ui.Actions(row);
                if(!String.IsNullOrEmpty(record.TargetThread)&&(record.State=="completed"||record.State=="verification_failed"))buttons.Children.Add(Ui.AsyncButton("Ouvrir dans Codex",async()=>{if(busy)return;Busy(true);try{await service.Open(record,lifetime.Token);}finally{Busy(false);}},Error));
                if(record.State=="copying"||record.State=="uncertain"||record.State=="verifying"||record.State=="migrating")buttons.Children.Add(Ui.AsyncButton("Vérifier cette copie",()=>RunCopy(record),Error));recent.Children.Add(Ui.Card(row));}
            if(records.Length==0)recent.Children.Add(Ui.Text("Aucune copie effectuée depuis cette instance.",13,true));
        }
    }
}
