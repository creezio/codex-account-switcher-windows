using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class TransfersWindow:Window
    {
        private readonly DesktopContext context;private readonly DesktopInstance source;private readonly Transfers service;
        private readonly StackPanel body=new StackPanel();private readonly TextBlock status=Ui.Text("",13);
        internal static readonly string[][] Actions={
            new[]{"copy_thread","Copier une conversation","Continuer ailleurs avec le même historique. L’original reste disponible."},
            new[]{"transfer_thread","Transférer une conversation","Vérifier la copie, puis archiver l’original. Nouvel identifiant ; archivage réversible."},
            new[]{"link_project","Ouvrir les fichiers d’un projet","Retrouver le projet dans une autre instance, avec les mêmes fichiers et sans importer les conversations."},
            new[]{"copy_project","Cloner un projet local","Recréer le projet et ses conversations actives. Choisir un dossier commun ou une copie indépendante des fichiers."},
            new[]{"transfer_project","Transférer les conversations d’un projet","Importer et vérifier toutes ses conversations actives, puis archiver les originales. Les fichiers et le projet source restent en place."}
        };
        internal TransfersWindow(DesktopContext context,Window owner,DesktopInstance source)
        {
            this.context=context;this.source=source;service=new Transfers(context.Store);Owner=owner;Title="Conversations et projets";Width=790;Height=Math.Min(850,SystemParameters.WorkArea.Height);MinWidth=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");FontFamily=new System.Windows.Media.FontFamily("Segoe UI");
            body.SetResourceReference(Panel.BackgroundProperty,"CanvasBrush");Content=new ScrollViewer{Content=body,Margin=new Thickness(24),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};body.Children.Add(Ui.Text(Title,24));body.Children.Add(Ui.Text("Depuis « "+source.Name+" » · choisissez le résultat souhaité.",14,true));
            foreach(var action in Actions){var card=new StackPanel();card.Children.Add(Ui.Button(action[1],()=>OpenAction(action[0]),action[0]=="copy_thread"));card.Children.Add(Ui.Text(action[2],13,true));body.Children.Add(Ui.Card(card));}
            body.Children.Add(Ui.Text("Chaque instance utilise son propre compte. Les accès aux pages, sites et plugins ne sont pas transférés. La synchronisation d’une même conversation en direct entre comptes reste indisponible.",13,true));body.Children.Add(status);Loaded+=delegate{Recent();};
        }
        private StackPanel history;
        private void OpenAction(string kind){if(kind=="copy_thread")new ThreadCopyWindow(context,this,source).ShowDialog();else new TransferWindow(context,this,source,kind).ShowDialog();Recent();}
        private void Recent()
        {
            if(history!=null)body.Children.Remove(history);history=new StackPanel();body.Children.Add(history);
            try{var records=service.Recent(source.Id);if(records.Length==0)return;history.Children.Add(Ui.Text("Opérations enregistrées",18));foreach(var r in records){var row=new StackPanel();row.Children.Add(Ui.Text(r.Request.Title,15));row.Children.Add(Ui.Text(r.Message,13,true));row.Children.Add(Ui.Button(r.State=="completed"?"Voir le résultat":"Reprendre / vérifier",()=>{new TransferWindow(context,this,source,r.Request.Kind,r).ShowDialog();Recent();}));history.Children.Add(Ui.Card(row));}}catch(Exception e){status.Text=Program.SafeError(e);}
        }
    }
    internal sealed class TransferWindow:Window
    {
        private readonly DesktopContext context;private readonly DesktopInstance source;private readonly string kind;private readonly Transfers service;
        internal readonly ComboBox Destination=new ComboBox{DisplayMemberPath="Name"};internal readonly ListBox Items=new ListBox{Height=170,DisplayMemberPath="Title"};
        internal readonly TextBox NameInput=new TextBox{MaxLength=160},FolderInput=new TextBox(),Query=new TextBox();
        internal readonly CheckBox Independent=new CheckBox{Content="Créer une copie indépendante des fichiers",Margin=new Thickness(0,12,0,8)};
        internal readonly Button PrepareButton,RunButton;internal readonly TextBlock Status=Ui.Text("",13);
        private readonly StackPanel inputs=new StackPanel(),results=new StackPanel();private readonly Button more;
        private TransferRecord record;private bool busy;private string cursor,loadedQuery;private readonly List<CopyThreadChoice> choices=new List<CopyThreadChoice>();
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        internal TransferWindow(DesktopContext context,Window owner,DesktopInstance source,string kind,TransferRecord record=null,Transfers service=null)
        {
            this.context=context;this.source=source;this.kind=kind;this.record=record;this.service=service??new Transfers(context.Store);
            Owner=owner;Title=TransfersWindow.Actions.Single(a=>a[0]==kind)[1];Width=800;Height=Math.Min(840,SystemParameters.WorkArea.Height);MinWidth=500;MinHeight=550;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");FontFamily=new System.Windows.Media.FontFamily("Segoe UI");
            var dock=new DockPanel{Margin=new Thickness(24)};dock.SetResourceReference(Panel.BackgroundProperty,"CanvasBrush");Content=dock;var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);header.Children.Add(Ui.Text(Title,24));header.Children.Add(Ui.Text("Depuis « "+source.Name+" »",14,true));
            var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);footer.Children.Add(Status);System.Windows.Automation.AutomationProperties.SetLiveSetting(Status,System.Windows.Automation.AutomationLiveSetting.Polite);var actions=Ui.Actions(footer);PrepareButton=Ui.AsyncButton("Préparer et afficher le résumé",Prepare,Error);RunButton=Ui.AsyncButton("Exécuter",Run,Error,true);actions.Children.Add(PrepareButton);actions.Children.Add(RunButton);actions.Children.Add(Ui.Button("Fermer",Close));
            var body=new StackPanel();dock.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});body.Children.Add(inputs);inputs.Children.Add(Ui.Text(Transfers.ProjectKind(kind)?"Projet local source":"Conversation source",14));
            var search=Ui.Actions(inputs);Query.Width=380;search.Children.Add(Query);search.Children.Add(Ui.AsyncButton("Rechercher",()=>Load(false),Error));inputs.Children.Add(Items);more=Ui.AsyncButton("Afficher la suite",()=>Load(true),Error);inputs.Children.Add(more);
            inputs.Children.Add(Ui.Text("Instance destinataire",14));inputs.Children.Add(Destination);inputs.Children.Add(Ui.Text("Nom à destination",14));inputs.Children.Add(NameInput);
            if(kind=="copy_project"||kind=="transfer_project"){inputs.Children.Add(Independent);inputs.Children.Add(Ui.Text("Nouveau dossier absolu pour la copie indépendante",13,true));inputs.Children.Add(FolderInput);FolderInput.IsEnabled=false;Independent.Checked+=delegate{FolderInput.IsEnabled=true;};Independent.Unchecked+=delegate{FolderInput.IsEnabled=false;};}
            Items.SelectionChanged+=delegate{if(record==null&&Items.SelectedItem is CopyThreadChoice c)NameInput.Text=c.Title.Length>150?c.Title.Substring(0,150):c.Title;Update();};Destination.SelectionChanged+=delegate{Update();};NameInput.TextChanged+=delegate{Update();};
            body.Children.Add(Ui.Text(TransfersWindow.Actions.Single(a=>a[0]==kind)[2],13,true));body.Children.Add(Ui.Text("Projets locaux et conversations actives uniquement. Les conversations archivées et cloud sont exclues. Codex peut nécessiter un redémarrage de l’instance destinataire pour afficher un nouveau projet ; l’opération restera en attente et les sources seront conservées.",13,true));body.Children.Add(results);
            foreach(var pair in new[]{Tuple.Create<DependencyObject,string>(Items,"Élément source"),Tuple.Create<DependencyObject,string>(Destination,"Instance destinataire"),Tuple.Create<DependencyObject,string>(NameInput,"Nom à destination"),Tuple.Create<DependencyObject,string>(Query,"Rechercher"),Tuple.Create<DependencyObject,string>(FolderInput,"Dossier indépendant")})System.Windows.Automation.AutomationProperties.SetName(pair.Item1,pair.Item2);
            Loaded+=async delegate{try{if(this.record==null){Destination.ItemsSource=await context.Read(a=>a.Data.Instances.Where(i=>i.Id!=source.Id&&!i.Archived&&i.AccountKey!=null).ToArray());Destination.SelectedIndex=0;await Load(false);}else Render();Update();}catch(Exception e){Error(e);}};
            Closing+=(_,e)=>{if(busy){e.Cancel=true;Status.Text="L’opération est en cours. Attendez son résultat avant de fermer.";}};Closed+=delegate{lifetime.Cancel();lifetime.Dispose();};Update();
        }
        private void Error(Exception e){Status.Text=Program.SafeError(e);Update();}
        private void Update(){inputs.IsEnabled=!busy&&record==null;PrepareButton.Visibility=record==null?Visibility.Visible:Visibility.Collapsed;PrepareButton.IsEnabled=!busy&&Items.SelectedItem!=null&&Destination.SelectedItem!=null&&!String.IsNullOrWhiteSpace(NameInput.Text);RunButton.IsEnabled=!busy&&record!=null&&record.State!="completed";RunButton.Content=record!=null&&record.State!="ready"?"Reprendre / vérifier":"Exécuter";results.IsEnabled=!busy;}
        private async Task Load(bool next)
        {
            if(busy||record!=null)return;busy=true;Update();try{
                string query=next?loadedQuery:Query.Text;
                if(Transfers.ProjectKind(kind)){var list=await Task.Run(()=>service.Projects(source.Id,lifetime.Token));choices.Clear();choices.AddRange(list.Where(p=>p.Name.IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0).Select(p=>new CopyThreadChoice{Id=p.Id,Title=p.Name,Folder=String.Join(" ; ",p.Roots)}));cursor=null;}
                else{var page=await new ThreadCopies(context.Store).List(source.Id,query,next?cursor:null,lifetime.Token);if(!next)choices.Clear();choices.AddRange(page.Threads.Where(t=>choices.All(c=>c.Id!=t.Id)));cursor=page.Cursor;}
                loadedQuery=query;Items.ItemsSource=choices.ToArray();more.Visibility=String.IsNullOrEmpty(cursor)?Visibility.Collapsed:Visibility.Visible;Status.Text=choices.Count+" élément(s) disponible(s).";
            }finally{busy=false;Update();}
        }
        internal async Task Prepare()
        {
            if(busy||record!=null||!(Items.SelectedItem is CopyThreadChoice selected)||!(Destination.SelectedItem is DesktopInstance target))return;
            var request=new TransferRequest{Id=Guid.NewGuid().ToString("N"),Kind=kind,Source=source.Id,Destination=target.Id,Item=selected.Id,Title=NameInput.Text.Trim(),Folder=Independent.IsChecked==true?FolderInput.Text.Trim():null};
            busy=true;Update();Status.Text="Vérification des comptes, de l’historique et des fichiers…";try{record=await Task.Run(()=>service.Prepare(request,lifetime.Token));Render();}finally{busy=false;Update();}
        }
        internal async Task Run()
        {
            if(busy||record==null)return;busy=true;Update();var progress=new Progress<string>(text=>Status.Text=text);
            try{record=await Task.Run(()=>service.Run(record.Request.Id,lifetime.Token,progress));Render();}
            finally{record=service.Recent(source.Id).FirstOrDefault(r=>r.Request.Id==record.Request.Id)??record;busy=false;Render();Update();}
        }
        private async Task RefreshCodex()
        {
            if(busy||record==null)return;
            var target=await context.Read(a=>{var i=a.Data.Instances.Single(x=>x.Id==record.Request.Destination);return new{Instance=i,State=context.InstanceState(a,i)};});
            if(target.State.Running&&MessageBox.Show(this,"Fermer puis rouvrir uniquement « "+target.Instance.Name+" » pour y enregistrer le projet ? Cette action sera refusée si un chat local travaille ou attend une réponse. Enregistrez vos modifications dans cette instance avant de poursuivre.","Actualiser le Codex destinataire",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            busy=true;Update();bool reopen=false;
            try{
                if(target.State.Running){Status.Text="Vérification de tous les chats destinataires…";await new TransferBackend(context.Store).RequireIdleInstance(target.Instance.Id,lifetime.Token);await context.CloseOrRestartInstance(target.Instance.Id,false,target.State);reopen=true;}
                record=await Task.Run(()=>service.Run(record.Request.Id,lifetime.Token));
                await context.OpenOrShowInstance(target.Instance.Id);reopen=false;
                record=await Task.Run(()=>service.Run(record.Request.Id,lifetime.Token,new Progress<string>(text=>Dispatcher.BeginInvoke(new Action(()=>Status.Text=text)))));
            }finally{
                string reopenError=null;
                if(reopen){try{await context.OpenOrShowInstance(target.Instance.Id);}catch(Exception e){reopenError="Rouvrez l’instance depuis Instances : "+Program.SafeError(e);}}
                busy=false;Render();if(reopenError!=null)Status.Text=reopenError;Update();
            }
        }
        private void Render()
        {
            if(record==null)return;Status.Text=record.Message;results.Children.Clear();results.Children.Add(Ui.Text(record.Request.Title+" · "+record.Items.Length+" conversation(s)",18));
            if(record.State=="awaiting_codex")results.Children.Add(Ui.AsyncButton("Actualiser Codex et reprendre",RefreshCodex,Error,true));
            results.Children.Add(Ui.Text(record.Files==null?"Fichiers : dossier(s) commun(s).":"Fichiers indépendants : "+record.Files.Destination+" · "+record.Files.Files.Length+" fichier(s) · "+record.Files.Bytes.ToString("N0")+" octets.",13,true));
            foreach(var item in record.Items){var row=Ui.Actions(results);row.Children.Add(Ui.Text(item.Title+" · "+(item.Archived?"source archivée":item.Target!=null?"copie vérifiée":"à importer"),13));if(item.Target!=null)row.Children.Add(Ui.AsyncButton("Ouvrir dans Codex",()=>service.Open(record,item,lifetime.Token),Error));}
        }
    }
}
