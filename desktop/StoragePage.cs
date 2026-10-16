using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Creezio.Switcher.Desktop
{
    internal sealed class StorageScopeRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Group { get; set; }
    }

    internal sealed class StorageEntryRow : INotifyPropertyChanged
    {
        internal StorageEntry Entry;
        internal StorageCleanupResult Result;
        internal Action Changed;
        private bool chosen;
        public string Id => Entry.Id;
        public string Name => Entry.Name;
        public string Path => Entry.Path;
        public string Category => StoragePage.Kind(Entry.Kind);
        public string Size => Result?.Removed == true ? "—" : (Entry.Errors>0?"≥ ":"")+StoragePage.Size(Entry.Bytes);
        public bool CanClean => Entry.CanClean && Result?.Removed != true;
        public bool Chosen { get => chosen; set { bool next=value&&CanClean;if(next==chosen)return;chosen=next;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Chosen)));Changed?.Invoke(); } }
        public string Safety => Result!=null ? Result.Removed?"Supprimé":"Conservé · "+Result.Code : Entry.CanClean?"Nettoyable vérifié":Entry.Kind=="builds"||Entry.Kind=="dependencies"||Entry.Kind=="cache"?"À examiner":"Protégé";
        public string Reason => Result==null?Entry.Reason+(Entry.Errors>0?" · "+Entry.Errors+" élément(s) non mesuré(s)":""):Result.Code+" · "+Result.Message;
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Updated(){if(!CanClean)chosen=false;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(""));Changed?.Invoke();}
    }

    internal sealed class StoragePage : PageView,IDisposable
    {
        internal readonly ListBox Scopes=new ListBox();
        internal readonly ComboBox CompactScopes=new ComboBox{DisplayMemberPath="Name"};
        internal readonly TextBox Search=new TextBox();
        internal readonly DataGrid Entries=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,BorderThickness=new Thickness(0)};
        internal readonly Button AnalyzeButton,CancelButton,PreviewButton,OpenButton,ClearButton;
        internal readonly CheckBox AutoClean;
        internal readonly TextBlock AutomaticReportSummary=Ui.Text("Ouvrez ce panneau pour consulter le dernier contrôle enregistré.",12,true);
        internal readonly Button AutomaticReportButton;
        internal Func<StorageScope[],CancellationToken,Task<StorageSnapshot>> ScanFixture;
        internal Func<string[],CancellationToken,Task<StorageCleanupResult[]>> CleanFixture;
        internal Func<StorageEntry[],bool> ConfirmCleanup;
        internal Action<string> OpenFolderFixture;
        internal bool Busy=>scanning||cleaning||previewing;
        internal StorageSnapshot Snapshot=>snapshot;
        private readonly List<StorageEntryRow> rows=new List<StorageEntryRow>();
        private StorageScope[] scopes=new StorageScope[0];
        private readonly TextBlock pressure=Ui.Text("Aucune analyse effectuée",17),pressureHint=Ui.Text("L’analyse démarre uniquement à votre demande.",12,true);
        private readonly TextBlock eligible=Ui.Text("—",21),protectedCount=Ui.Text("—",21),locations=Ui.Text("—",21),detailTitle=Ui.Text("Tous les emplacements",20),detailHint=Ui.Text("",12,true),selection=Ui.Text("Sélectionnez un élément pour voir son emplacement et sa protection.",12,true),stamp=Ui.Text("Aucune mesure enregistrée",11,true);
        private readonly ProgressBar progress=new ProgressBar{IsIndeterminate=true,Height=3,Visibility=Visibility.Collapsed,Margin=new Thickness(0,3,0,7)};
        private readonly Grid workspace=new Grid();
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private CancellationTokenSource operation;
        private StorageSnapshot snapshot;
        private bool scanning,cleaning,previewing,changing,disposed,needsRescan;
        private string scopeId="all";
        private DataGridColumn categoryColumn;

        internal StoragePage(DesktopContext context,ShellWindow shell):base(context,shell,"Stockage","")
        {
            Margin=new Thickness(24,20,24,16);Header.Children.Clear();Commands.Visibility=Visibility.Collapsed;
            var header=new DockPanel();
            var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);header.Children.Add(actions);
            AnalyzeButton=Ui.Button("Analyser",()=>_ = Analyze(),true);AnalyzeButton.Margin=new Thickness(0,0,8,0);actions.Children.Add(AnalyzeButton);
            CancelButton=Ui.Button("Annuler l’analyse",()=>operation?.Cancel());CancelButton.Margin=new Thickness(0);CancelButton.Visibility=Visibility.Collapsed;actions.Children.Add(CancelButton);
            var title=new StackPanel();title.Children.Add(Ui.Text("Stockage",26));var subtitle=Ui.Text("Identifier ce qui prend de la place et ce qui peut être nettoyé.",12,true);title.Children.Add(subtitle);header.Children.Add(title);Header.Children.Add(header);
            Header.Children.Add(progress);
            var summary=new Grid{Margin=new Thickness(0,8,0,14)};summary.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.8,GridUnitType.Star)});summary.ColumnDefinitions.Add(new ColumnDefinition());summary.ColumnDefinitions.Add(new ColumnDefinition());summary.ColumnDefinitions.Add(new ColumnDefinition());
            var disk=new StackPanel();disk.Children.Add(pressure);disk.Children.Add(pressureHint);summary.Children.Add(Card(disk,0));
            summary.Children.Add(Metric("Nettoyable vérifié",eligible,"Taille logique estimée",1));summary.Children.Add(Metric("Éléments préservés",protectedCount,"Sources, données et autres éléments",2));summary.Children.Add(Metric("Emplacements",locations,"Instances, projets et système",3));Header.Children.Add(summary);
            var automatic=new StackPanel();AutoClean=Ui.Check("Nettoyer automatiquement les temporaires identifiés du Switcher",context.Store.ReadRecord<StorageSettings>("storage-settings.dpapi").AutoCleanOwnedTemps,automatic);AutoClean.FontSize=12;
            automatic.Children.Add(Ui.Text("Au démarrage et toutes les 10 minutes : uniquement les temporaires du Switcher avec un marqueur reconnu et aucun processus actif. Les projets, dépendances, profils et historiques ne sont jamais nettoyés automatiquement.",12,true));
            automatic.Children.Add(AutomaticReportSummary);AutomaticReportButton=Ui.Button("Voir le dernier rapport",()=>{try{new StorageAutomaticReportWindow(shell,LoadAutomaticReport()).ShowDialog();}catch(Exception error){Notice.Text=Program.SafeError(error);}});automatic.Children.Add(AutomaticReportButton);
            var autoSettings=new Expander{Header="Nettoyage automatique du Switcher",Content=automatic,Margin=new Thickness(0,0,0,10)};Header.Children.Add(autoSettings);
            autoSettings.Expanded+=delegate{try{LoadAutomaticReport();}catch(Exception error){AutomaticReportSummary.Text=Program.SafeError(error);}};
            AutoClean.Click+=delegate{try{var settings=context.Store.ReadRecord<StorageSettings>("storage-settings.dpapi");settings.AutoCleanOwnedTemps=AutoClean.IsChecked==true;context.Store.WriteRecord("storage-settings.dpapi",settings);}catch(Exception error){AutoClean.IsChecked=context.Store.ReadRecord<StorageSettings>("storage-settings.dpapi").AutoCleanOwnedTemps;Notice.Text=Program.SafeError(error);}};
            Notice.Margin=new Thickness(0,0,0,8);Notice.MaxHeight=48;AutomationProperties.SetLiveSetting(Notice,AutomationLiveSetting.Polite);
            workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(220)});workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(18)});workspace.ColumnDefinitions.Add(new ColumnDefinition());Body.Children.Add(workspace);
            var rail=new DockPanel();workspace.Children.Add(rail);var label=Ui.Text("EMPLACEMENTS",10,true);DockPanel.SetDock(label,Dock.Top);rail.Children.Add(label);rail.Children.Add(Scopes);
            AutomationProperties.SetName(Scopes,"Emplacements de stockage");AutomationProperties.SetName(CompactScopes,"Emplacement de stockage sélectionné");
            var template=new DataTemplate(typeof(StorageScopeRow));var stack=new FrameworkElementFactory(typeof(StackPanel));
            foreach(var property in new[]{"Name","Summary"}){var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(property));text.SetValue(TextBlock.FontSizeProperty,property=="Name"?13d:11d);text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.NoWrap);text.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);text.SetBinding(ToolTipProperty,new Binding(property));stack.AppendChild(text);}template.VisualTree=stack;Scopes.ItemTemplate=template;
            var detail=new Grid();Grid.SetColumn(detail,2);workspace.Children.Add(detail);detail.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});detail.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});detail.RowDefinitions.Add(new RowDefinition());detail.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var top=new StackPanel();top.Children.Add(CompactScopes);CompactScopes.Visibility=Visibility.Collapsed;detailTitle.Margin=new Thickness(0,0,0,3);top.Children.Add(detailTitle);detailHint.Text="Les tailles de dossiers sont logiques : liens, compression et recoupements peuvent différer de l’espace réellement utilisé.";top.Children.Add(detailHint);detail.Children.Add(top);
            var search=Ui.SearchField(Search,"Rechercher un nom, un type ou une raison");Grid.SetRow(search,1);detail.Children.Add(search);
            AddColumns();AutomationProperties.SetName(Entries,"Détails du stockage mesuré");Entries.SetResourceReference(Control.BackgroundProperty,"SurfaceBrush");Entries.SetResourceReference(Control.ForegroundProperty,"TextBrush");Entries.SetResourceReference(Control.BorderBrushProperty,"LineBrush");ScrollViewer.SetHorizontalScrollBarVisibility(Entries,ScrollBarVisibility.Disabled);Grid.SetRow(Entries,2);detail.Children.Add(Entries);
            var footer=new StackPanel{Margin=new Thickness(0,10,0,0)};Grid.SetRow(footer,3);detail.Children.Add(footer);selection.MaxHeight=54;footer.Children.Add(selection);stamp.Margin=new Thickness(0,0,0,8);footer.Children.Add(stamp);
            var bottom=Ui.Actions(footer);bottom.Margin=new Thickness(0);PreviewButton=Ui.Button("Prévisualiser le nettoyage",()=>_ = CleanSelected(),true);bottom.Children.Add(PreviewButton);OpenButton=Ui.Button("Ouvrir le dossier",OpenSelected);bottom.Children.Add(OpenButton);ClearButton=Ui.Button("Tout décocher",()=>{foreach(var row in rows)row.Chosen=false;});bottom.Children.Add(ClearButton);
            ConfirmCleanup=approved=>new StorageCleanupPreview(shell,approved).ShowDialog()==true;
            Scopes.SelectionChanged+=delegate{if(!changing&&Scopes.SelectedItem is StorageScopeRow row)SelectScope(row);};CompactScopes.SelectionChanged+=delegate{if(!changing&&CompactScopes.SelectedItem is StorageScopeRow row)SelectScope(row);};Search.TextChanged+=delegate{ApplyFilter();};Entries.SelectionChanged+=delegate{UpdateSelection();};
            SizeChanged+=delegate{bool compact=ActualWidth<850;rail.Visibility=compact?Visibility.Collapsed:Visibility.Visible;workspace.ColumnDefinitions[0].Width=new GridLength(compact?0:220);workspace.ColumnDefinitions[1].Width=new GridLength(compact?0:18);CompactScopes.Visibility=compact?Visibility.Visible:Visibility.Collapsed;categoryColumn.Visibility=compact?Visibility.Collapsed:Visibility.Visible;detailHint.Visibility=compact?Visibility.Collapsed:Visibility.Visible;detailTitle.Visibility=compact?Visibility.Collapsed:Visibility.Visible;subtitle.Visibility=compact?Visibility.Collapsed:Visibility.Visible;for(int i=2;i<4;i++){summary.Children[i].Visibility=compact?Visibility.Collapsed:Visibility.Visible;summary.ColumnDefinitions[i].Width=compact?new GridLength(0):new GridLength(1,GridUnitType.Star);}summary.ColumnDefinitions[0].Width=new GridLength(1.8,GridUnitType.Star);};
            shell.Closed+=delegate{Dispose();};SetScopes();UpdateButtons();
        }

        private Border Card(UIElement child,int column)
        {var card=Ui.Card(child);card.Padding=new Thickness(13,12,13,10);card.Margin=new Thickness(0,0,column==3?0:10,0);Grid.SetColumn(card,column);return card;}
        private Border Metric(string caption,TextBlock value,string hint,int column)
        {var body=new StackPanel();body.Children.Add(Ui.Text(caption,11,true));value.Margin=new Thickness(0,0,0,4);body.Children.Add(value);body.Children.Add(Ui.Text(hint,10,true));return Card(body,column);}
        private void AddColumns()
        {
            var check=new FrameworkElementFactory(typeof(CheckBox));check.SetValue(MarginProperty,new Thickness(6,2,6,2));check.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Center);check.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(StorageEntryRow.Chosen)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});check.SetBinding(IsEnabledProperty,new Binding(nameof(StorageEntryRow.CanClean)));check.SetBinding(AutomationProperties.NameProperty,new Binding(nameof(StorageEntryRow.Name)));
            Entries.Columns.Add(new DataGridTemplateColumn{Header="",CellTemplate=new DataTemplate{VisualTree=check},Width=new DataGridLength(32)});
            AddColumn("Élément",nameof(StorageEntryRow.Name),new DataGridLength(1.1,DataGridLengthUnitType.Star),130);
            categoryColumn=AddColumn("Type",nameof(StorageEntryRow.Category),new DataGridLength(90),70);
            AddColumn("Taille logique",nameof(StorageEntryRow.Size),new DataGridLength(92),80);
            AddColumn("Protection",nameof(StorageEntryRow.Safety),new DataGridLength(125),100);
            AddColumn("Raison / résultat",nameof(StorageEntryRow.Reason),new DataGridLength(1.4,DataGridLengthUnitType.Star),120);
        }
        private DataGridColumn AddColumn(string title,string property,DataGridLength width,double minimum)
        {var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));style.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.NoWrap));style.Setters.Add(new Setter(ToolTipProperty,new Binding(property)));var column=new DataGridTextColumn{Header=title,Binding=new Binding(property),ElementStyle=style,Width=width,MinWidth=minimum,IsReadOnly=true};Entries.Columns.Add(column);return column;}

        public override Task Refresh()=>Task.CompletedTask;
        internal async Task Analyze()
        {
            if(Busy||disposed)return;scanning=true;Notice.Text="Analyse des emplacements connus… Vous pouvez l’annuler sans modifier vos fichiers.";UpdateButtons();
            operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try
            {
                var inventory=await Context.StorageScopes();operation.Token.ThrowIfCancellationRequested();Notice.Text="Analyse de "+inventory.Length+" emplacement(s)… Les dossiers et fichiers protégés restent intacts.";
                StorageSnapshot measured;
                if(Context.Fixture){if(ScanFixture==null)throw new InvalidOperationException("Analyse disque réelle désactivée dans cette recette.");measured=await ScanFixture(inventory,operation.Token);}
                else measured=await Task.Run(()=>StorageMaintenance.Scan(inventory,operation.Token),operation.Token);
                operation.Token.ThrowIfCancellationRequested();if(disposed)return;
                scopes=inventory;snapshot=measured;needsRescan=false;rows.Clear();foreach(var entry in measured.Entries??new StorageEntry[0])rows.Add(new StorageEntryRow{Entry=entry,Changed=UpdateButtons});
                SetScopes();ApplyFilter();RenderOverview();Notice.Text=measured.Partial?"Analyse partielle : certains éléments n’ont pas pu être mesurés. Aucun fichier n’a été modifié.":"Analyse terminée. Cochez uniquement les temporaires vérifiés pour prévisualiser leur suppression.";
            }
            catch(OperationCanceledException){if(!disposed)Notice.Text="Analyse annulée. La dernière mesure complète est conservée ; aucun fichier n’a été modifié.";}
            catch(Exception error){if(!disposed)Notice.Text="Analyse impossible : "+Program.SafeError(error);}
            finally{operation?.Dispose();operation=null;scanning=false;if(!disposed)UpdateButtons();}
        }
        private void SetScopes()
        {
            string keep=scopeId;var list=new List<StorageScopeRow>{new StorageScopeRow{Id="all",Name="Tous les emplacements",Summary=snapshot==null?"Analyse à la demande":rows.Count+" éléments mesurés",Group="Vue d’ensemble"}};
            foreach(var scope in scopes.OrderBy(value=>ScopeOrder(value.Kind)).ThenBy(value=>value.Name,StringComparer.CurrentCultureIgnoreCase))list.Add(new StorageScopeRow{Id=scope.Id,Name=scope.Name,Summary=ScopeKind(scope.Kind)+" · "+rows.Count(value=>value.Entry.ScopeId==scope.Id)+" élément(s)",Group=ScopeKind(scope.Kind)});
            changing=true;Scopes.ItemsSource=list;CompactScopes.ItemsSource=list;var selected=list.FirstOrDefault(value=>value.Id==keep)??list[0];scopeId=selected.Id;Scopes.SelectedItem=CompactScopes.SelectedItem=selected;changing=false;detailTitle.Text=selected.Name;
        }
        private void SelectScope(StorageScopeRow row)
        {scopeId=row.Id;changing=true;Scopes.SelectedItem=CompactScopes.SelectedItem=row;changing=false;detailTitle.Text=row.Name;ApplyFilter();}
        private void ApplyFilter()
        {
            if(Entries==null)return;string keep=(Entries.SelectedItem as StorageEntryRow)?.Id,query=Search.Text??"";
            var visible=rows.Where(row=>(scopeId=="all"||row.Entry.ScopeId==scopeId)&&(row.Name+" "+row.Category+" "+row.Reason+" "+row.Path).IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0).OrderByDescending(row=>row.Entry.Bytes).ToArray();
            Entries.ItemsSource=visible;Entries.SelectedItem=visible.FirstOrDefault(row=>row.Id==keep)??visible.FirstOrDefault();UpdateSelection();UpdateButtons();
        }
        private void RenderOverview()
        {
            if(snapshot==null)return;
            string volume=scopes.Length==0?null:VolumeOf(scopes[0].Path);
            var otherVolumes=scopes.Select(scope=>VolumeOf(scope.Path)).Where(path=>!String.IsNullOrEmpty(path)&&!String.Equals(path,volume,StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            pressure.Text=snapshot.TotalBytes<=0?"Espace libre non disponible":(snapshot.FreeBytes<20L*1024*1024*1024?"⚠  ":"")+(volume??"Volume mesuré")+" · "+Size(snapshot.FreeBytes)+" libres / "+Size(snapshot.TotalBytes);
            pressureHint.Text=(snapshot.TotalBytes<=0?"Mesure du volume indisponible":snapshot.FreeBytes<20L*1024*1024*1024?"Espace faible":"Espace disponible")+" · seuil : 20 Gio"+(otherVolumes.Length>0?" · autres volumes : "+otherVolumes.Length+" (espace libre non mesuré)":"");
            pressureHint.ToolTip=otherVolumes.Length==0?pressureHint.Text:pressureHint.Text+"\n"+String.Join(", ",otherVolumes);
            pressure.SetResourceReference(TextBlock.ForegroundProperty,snapshot.FreeBytes<20L*1024*1024*1024?"TextBrush":"AccentTextBrush");eligible.Text=Size(rows.Where(row=>row.CanClean).Sum(row=>row.Entry.Bytes));protectedCount.Text=rows.Count(row=>!row.Entry.CanClean).ToString(CultureInfo.CurrentCulture);locations.Text=scopes.Length.ToString(CultureInfo.CurrentCulture);
            stamp.Text=(DateTimeOffset.TryParse(snapshot.CapturedUtc,out var captured)?"Mesure du "+captured.ToLocalTime().ToString("dd/MM à HH:mm:ss"):"Mesure disponible")+(snapshot.Partial?" · partielle":"")+" · aucune actualisation automatique";
        }
        private static string VolumeOf(string path){try{return Path.GetPathRoot(path);}catch(ArgumentException){return null;}}
        private void UpdateSelection()
        {var row=Entries.SelectedItem as StorageEntryRow;selection.Text=row==null?"Sélectionnez un élément pour voir son emplacement et sa protection.":row.Path+"\n"+row.Reason;selection.ToolTip=selection.Text;UpdateButtons();}
        private void UpdateButtons()
        {
            if(AnalyzeButton==null)return;AnalyzeButton.IsEnabled=!Busy&&!disposed;CancelButton.Visibility=scanning?Visibility.Visible:Visibility.Collapsed;CancelButton.IsEnabled=scanning;progress.Visibility=Busy?Visibility.Visible:Visibility.Collapsed;
            int count=rows.Count(row=>row.Chosen&&row.CanClean);PreviewButton.IsEnabled=count>0&&!Busy&&!disposed&&!needsRescan;PreviewButton.Content=count==0?"Prévisualiser le nettoyage":"Prévisualiser ("+count+")";ClearButton.IsEnabled=count>0&&!Busy;OpenButton.IsEnabled=Entries.SelectedItem is StorageEntryRow&&!cleaning;Scopes.IsEnabled=CompactScopes.IsEnabled=Search.IsEnabled=!cleaning;Entries.IsEnabled=!Busy;
        }
        internal async Task CleanSelected()
        {
            if(Busy||disposed||needsRescan)return;var approved=rows.Where(row=>row.Chosen&&row.CanClean).Select(row=>row.Entry).ToArray();if(approved.Length==0)return;
            bool confirmed;previewing=true;UpdateButtons();try{confirmed=ConfirmCleanup(approved);}catch(Exception error){Notice.Text="Prévisualisation impossible : "+Program.SafeError(error);return;}finally{previewing=false;UpdateButtons();}if(!confirmed)return;
            cleaning=true;Notice.Text="Nettoyage des seuls temporaires confirmés… Chaque chemin est contrôlé à nouveau avant suppression.";UpdateButtons();operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try
            {
                StorageCleanupResult[] results;
                var paths=approved.Select(entry=>entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if(Context.Fixture){if(CleanFixture==null)throw new InvalidOperationException("Nettoyage réel désactivé dans cette recette.");results=await CleanFixture(paths,operation.Token);}
                else results=await Task.Run(()=>StorageMaintenance.CleanRuntime(Context.Root,paths,operation.Token),operation.Token);
                if(disposed)return;foreach(var result in results){foreach(var row in rows.Where(value=>String.Equals(value.Path,result.Path,StringComparison.OrdinalIgnoreCase))){row.Result=result;row.Chosen=false;row.Updated();}}
                Notice.Text=results.Count(result=>result.Removed)+" temporaire(s) supprimé(s) · "+results.Count(result=>!result.Removed)+" conservé(s). Allocation retirée estimée : "+Size(results.Sum(result=>result.ReclaimedBytes))+". Les codes et raisons sont visibles dans la grille. Relancez l’analyse pour une nouvelle mesure du disque.";
                RenderOverview();UpdateSelection();
            }
            catch(OperationCanceledException){needsRescan=true;if(!disposed)Notice.Text="Nettoyage interrompu. Relancez une analyse avant une nouvelle sélection ; certains temporaires ont pu être supprimés.";}
            catch(Exception error){needsRescan=true;if(!disposed)Notice.Text="Résultat du nettoyage à vérifier : "+Program.SafeError(error)+". Relancez une analyse avant une nouvelle demande.";}
            finally{operation?.Dispose();operation=null;cleaning=false;if(!disposed)UpdateButtons();}
        }
        private void OpenSelected()
        {
            if(!(Entries.SelectedItem is StorageEntryRow row)||cleaning)return;
            try
            {
                if(Context.Fixture){if(OpenFolderFixture==null)throw new InvalidOperationException("Ouverture d’Explorer désactivée dans cette recette.");OpenFolderFixture(row.Path);return;}
                string folder=Directory.Exists(row.Path)?row.Path:Path.GetDirectoryName(row.Path);if(String.IsNullOrEmpty(folder)||!Directory.Exists(folder))throw new InvalidOperationException("Ce dossier n’est plus présent. Relancez une analyse.");
                var start=new ProcessStartInfo{FileName="explorer.exe",UseShellExecute=true};start.ArgumentList.Add(folder);Process.Start(start);
            }
            catch(Exception error){Notice.Text=Program.SafeError(error);}
        }
        internal StorageMaintenanceReport LoadAutomaticReport()
        {
            // Read an existing small local report only on explicit expansion/click.
            // This does not start maintenance, inspect folders, or run from Refresh().
            var report=Context.Store.ReadRecord<StorageMaintenanceReport>("storage-maintenance.dpapi");
            var results=report.Results??new StorageCleanupResult[0];
            AutomaticReportSummary.Text=DateTimeOffset.TryParse(report.CheckedUtc,out var captured)?"Dernier contrôle : "+captured.ToLocalTime().ToString("dd/MM à HH:mm:ss")+" · "+results.Count(result=>result.Removed)+" supprimé(s) · "+results.Count(result=>!result.Removed)+" conservé(s)":"Aucun contrôle automatique enregistré pour le moment.";
            return report;
        }
        public override bool CanLeave(){if(previewing){Notice.Text="Fermez la prévisualisation du nettoyage avant de changer de page.";return false;}if(cleaning){Notice.Text="Le nettoyage confirmé est en cours. Attendez son résultat avant de changer de page.";return false;}if(scanning)operation?.Cancel();return true;}
        public void Dispose(){if(disposed)return;disposed=true;lifetime.Cancel();operation?.Cancel();lifetime.Dispose();}
        internal static string Size(long bytes)=>bytes>=1073741824L?(bytes/1073741824d).ToString("0.##",CultureInfo.CurrentCulture)+" Gio":bytes>=1048576L?(bytes/1048576d).ToString("0.##",CultureInfo.CurrentCulture)+" Mio":bytes>=1024?(bytes/1024d).ToString("0.##",CultureInfo.CurrentCulture)+" Kio":Math.Max(0,bytes).ToString(CultureInfo.CurrentCulture)+" o";
        internal static string Kind(string value)=>value=="history"?"Historique":value=="data"?"Données":value=="cache"?"Cache":value=="temporary"?"Temporaire":value=="dependencies"?"Dépendances":value=="builds"?"Compilation":value=="sources"?"Sources":value=="runtime"?"Runtime":"Autre";
        private static int ScopeOrder(string value)=>value=="instance"?0:value=="project"?1:value=="switcher"?2:3;
        private static string ScopeKind(string value)=>value=="instance"?"Instance Codex":value=="project"?"Projet":value=="switcher"?"Account Switcher":"Système";
    }

    internal sealed class StorageCleanupPreview : Window
    {
        internal readonly Button ConfirmButton;
        internal StorageCleanupPreview(Window owner,StorageEntry[] entries)
        {
            Owner=owner;Title="Prévisualiser le nettoyage";ShowInTaskbar=false;Width=Math.Min(720,SystemParameters.WorkArea.Width);Height=Math.Min(530,SystemParameters.WorkArea.Height);MinWidth=560;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");
            var root=new Grid{Margin=new Thickness(24)};root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Content=root;
            var heading=new StackPanel();heading.Children.Add(Ui.Text("Supprimer ces temporaires ?",23));heading.Children.Add(Ui.Text(entries.Length+" dossier(s) runtime vérifié(s) · "+StoragePage.Size(entries.Sum(entry=>entry.Bytes))+" logiques estimés",14));heading.Children.Add(Ui.Text("Seuls les chemins ci-dessous seront proposés au moteur. Il vérifiera à nouveau les marqueurs, les processus actifs et le confinement de chaque dossier. Sources, comptes et historiques restent préservés.",12,true));root.Children.Add(heading);
            var list=new ListBox{ItemsSource=entries};var template=new DataTemplate(typeof(StorageEntry));var stack=new FrameworkElementFactory(typeof(StackPanel));foreach(var property in new[]{"Name","Path"}){var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(property));text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);text.SetValue(TextBlock.FontSizeProperty,property=="Name"?14d:12d);stack.AppendChild(text);}template.VisualTree=stack;list.ItemTemplate=template;AutomationProperties.SetName(list,"Chemins proposés au nettoyage");Grid.SetRow(list,1);root.Children.Add(list);
            var actions=new WrapPanel{Margin=new Thickness(0,16,0,0)};ConfirmButton=Ui.Button("Supprimer ces temporaires",()=>{DialogResult=true;},true);actions.Children.Add(ConfirmButton);actions.Children.Add(Ui.Button("Annuler",()=>{DialogResult=false;}));Grid.SetRow(actions,2);root.Children.Add(actions);
        }
    }

    internal sealed class StorageAutomaticReportWindow : Window
    {
        internal StorageAutomaticReportWindow(Window owner,StorageMaintenanceReport report)
        {
            Owner=owner;Title="Dernier nettoyage automatique";ShowInTaskbar=false;Width=Math.Min(780,SystemParameters.WorkArea.Width);Height=Math.Min(540,SystemParameters.WorkArea.Height);MinWidth=560;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");
            var root=new Grid{Margin=new Thickness(24)};root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Content=root;
            var header=new StackPanel();header.Children.Add(Ui.Text("Dernier contrôle du Switcher",22));header.Children.Add(Ui.Text(DateTimeOffset.TryParse(report.CheckedUtc,out var captured)?captured.ToLocalTime().ToString("dd/MM/yyyy à HH:mm:ss")+" · espace libre mesuré lors du contrôle : "+(report.FreeBytes>=0?StoragePage.Size(report.FreeBytes):"indisponible"):"Aucun contrôle enregistré.",12,true));header.Children.Add(Ui.Text("Ce rapport déjà enregistré ne lance aucune analyse et aucun nettoyage. Les refus et erreurs Windows sont conservés.",12,true));root.Children.Add(header);
            var list=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,RowHeaderWidth=0,HeadersVisibility=DataGridHeadersVisibility.Column,ItemsSource=report.Results??new StorageCleanupResult[0],BorderThickness=new Thickness(0)};ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);AutomationProperties.SetName(list,"Résultats du dernier nettoyage automatique");
            foreach(var column in new[]{new{Header="Chemin",Property="Path",Width=2d},new{Header="Code",Property="Code",Width=1d},new{Header="Résultat",Property="Message",Width=3d}}){var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));style.Setters.Add(new Setter(ToolTipProperty,new Binding(column.Property)));list.Columns.Add(new DataGridTextColumn{Header=column.Header,Binding=new Binding(column.Property),ElementStyle=style,Width=new DataGridLength(column.Width,DataGridLengthUnitType.Star)});}
            Grid.SetRow(list,1);root.Children.Add(list);var close=Ui.Button("Fermer",Close);close.Margin=new Thickness(0,16,0,0);Grid.SetRow(close,2);root.Children.Add(close);
        }
    }
}
