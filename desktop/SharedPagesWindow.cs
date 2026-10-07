using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal sealed class SharedPagesWindow : Window
    {
        private readonly Func<Task<SharedPageEntry[]>> list;
        private readonly Func<SharedPageEntry,Task<SharedPageDocument>> read;
        private readonly Func<SharedPageDocument,string,string,string,string,Task<SharedPageSave>> save;
        private readonly Func<SharedPageDocument,Task> openOwner;
        private readonly ListBox pages=new ListBox{DisplayMemberPath="Title",MinWidth=190};
        private readonly TextBlock status=Ui.Text("Chargement des Pages reçues…",13,true),title=Ui.Text("Pages reçues",26),subtitle=Ui.Text("",13,true);
        private readonly StackPanel blocks=new StackPanel();
        private readonly TextBox search=new TextBox(),draft=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=170,MaxHeight=320,VerticalContentAlignment=VerticalAlignment.Top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas")};
        private readonly StackPanel editor=new StackPanel{Visibility=Visibility.Collapsed};
        private readonly Button refresh,append,native,commit,cancel;
        private SharedPageEntry[] entries=Array.Empty<SharedPageEntry>();
        private SharedPageDocument document;
        private string blockId,original,writeId;
        private bool busy,uncertain,closed,selecting;
        internal Func<bool> ConfirmDiscard;
        internal bool Dirty=>editor.Visibility==Visibility.Visible&&draft.Text!=original;
        internal SharedPageDocument Current=>document;
        internal static void Open(DesktopContext context,Window parent,DesktopInstance instance)
        {
            var viewer=new SharedPages(context.Store);
            // This UI has its own audit identity; it never borrows or sends a chat prompt.
            RelaySession session=null;
            var window=new SharedPagesWindow(parent,instance.Name,
                ()=>Task.Run(()=>{session=viewer.DesktopSession(instance.Id);return viewer.List(session);}),
                e=>Task.Run(()=>viewer.Read(session,e.Share,e.Page,CancellationToken.None)),
                (d,id,block,text,readId)=>Task.Run(()=>viewer.Save(session,id,d.Share,d.Page,readId,block,text,CancellationToken.None)),
                d=>Task.Run(()=>viewer.OpenOwner(session,d.Share,d.Page,CancellationToken.None)));
            window.Show();
        }
        internal SharedPagesWindow(Window parent,string instance,Func<Task<SharedPageEntry[]>> list,Func<SharedPageEntry,Task<SharedPageDocument>> read,Func<SharedPageDocument,string,string,string,string,Task<SharedPageSave>> save,Func<SharedPageDocument,Task> openOwner)
        {
            this.list=list;this.read=read;this.save=save;this.openOwner=openOwner;Owner=parent;
            Title="Pages reçues · "+instance;Width=1040;Height=780;MinWidth=720;MinHeight=520;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty,"CanvasBrush");ConfirmDiscard=()=>Ui.Confirm(this,"Abandonner le brouillon non enregistré ?","Page partagée");
            var layout=new Grid{Margin=new Thickness(24)};layout.SetResourceReference(Panel.BackgroundProperty,"CanvasBrush");Content=layout;layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var header=new StackPanel();header.Children.Add(Ui.Text("PAGES REÇUES · "+instance,11,true));header.Children.Add(Ui.Text("Consulter les Pages partagées avec cette instance",20));header.Children.Add(Ui.Text("Le contenu et les modifications restent sur la Page originale, dans le compte propriétaire.",13,true));layout.Children.Add(header);
            var columns=new Grid{Margin=new Thickness(0,12,0,8)};Grid.SetRow(columns,1);layout.Children.Add(columns);columns.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(230)});columns.ColumnDefinitions.Add(new ColumnDefinition());
            var side=new DockPanel{Margin=new Thickness(0,0,20,0)};columns.Children.Add(side);var searchField=Ui.SearchField(search,"Rechercher une Page");DockPanel.SetDock(searchField,Dock.Top);side.Children.Add(searchField);side.Children.Add(pages);
            var body=new DockPanel();Grid.SetColumn(body,1);columns.Children.Add(body);
            var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);body.Children.Add(top);top.Children.Add(title);top.Children.Add(subtitle);
            var actions=Ui.Actions(top);refresh=Ui.Button("Actualiser",async()=>await Run(Refresh));append=Ui.Button("Ajouter du texte",()=>Begin(null));native=Ui.Button("Ouvrir chez le propriétaire",async()=>await Run(async()=>{await openOwner(document);status.Text="Ouverture demandée dans l’instance propriétaire.";}));actions.Children.Add(refresh);actions.Children.Add(append);actions.Children.Add(native);
            var editorCard=Ui.Card(editor);editorCard.Visibility=Visibility.Collapsed;editor.Tag=editorCard;DockPanel.SetDock(editorCard,Dock.Bottom);body.Children.Add(editorCard);
            editor.Children.Add(Ui.Text("Modifier le texte · Markdown",16));editor.Children.Add(draft);System.Windows.Automation.AutomationProperties.SetName(draft,"Brouillon de la Page");
            var edits=Ui.Actions(editor);commit=Ui.Button("Enregistrer sur la Page",async()=>await Run(Save,true),true);cancel=Ui.Button("Annuler",()=>{if(!Dirty||ConfirmDiscard())CloseEditor();});edits.Children.Add(commit);edits.Children.Add(cancel);
            editor.Children.Add(Ui.Text("Les autres blocs sont conservés. Un conflit préserve votre brouillon.",12,true));body.Children.Add(new ScrollViewer{Content=blocks});
            Grid.SetRow(status,2);layout.Children.Add(status);
            search.TextChanged+=delegate{Filter();};draft.TextChanged+=delegate{Controls();};
            pages.SelectionChanged+=async delegate{
                if(selecting||busy||pages.SelectedItem is not SharedPageEntry entry)return;
                if(Dirty&&!ConfirmDiscard()){selecting=true;pages.SelectedItem=entries.FirstOrDefault(e=>e.Share==document?.Share&&e.Page==document?.Page);selecting=false;return;}
                CloseEditor();await Run(async()=>{status.Text="Lecture de la Page originale…";Show(await read(entry));status.Text="Page chargée via le tunnel.";});
            };
            Loaded+=async delegate{await Run(async()=>{entries=await list();Filter();status.Text=entries.Length==0?"Aucune Page partagée avec cette instance. Dans l’instance propriétaire → Ressources & accès, cochez les Pages à partager.":$"{entries.Length} Page(s) reçue(s). Sélectionnez celle à consulter.";});};
            Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(busy||(Dirty&&!ConfirmDiscard())){e.Cancel=true;return;}closed=true;};Controls();
        }
        private void Filter(){if(Dirty)return;selecting=true;pages.ItemsSource=entries.Where(e=>(e.Title+" "+e.Owner).IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();selecting=false;}
        private void Controls()
        {
            refresh.IsEnabled=document!=null&&!busy&&!Dirty;append.IsEnabled=document?.CanEdit==true&&!busy&&editor.Visibility!=Visibility.Visible;native.IsEnabled=document!=null&&!busy;
            commit.IsEnabled=!busy&&!uncertain&&document?.CanEdit==true&&Dirty&&!String.IsNullOrWhiteSpace(draft.Text);cancel.IsEnabled=!busy;draft.IsEnabled=!busy;pages.IsEnabled=!busy;search.IsEnabled=!busy&&!Dirty;
            foreach(var b in blocks.Children.OfType<Border>().Select(c=>c.Child).OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Button>()))b.IsEnabled=!busy&&editor.Visibility!=Visibility.Visible&&document?.CanEdit==true;
        }
        private async Task Run(Func<Task> action,bool writing=false)
        {
            if(busy||closed)return;busy=true;Controls();
            try{await action();}catch(Exception e){status.Text=Program.SafeError(e);if(writing){uncertain=true;status.Text+=" Brouillon conservé. Vérifiez la Page originale avant toute nouvelle écriture.";}}
            finally{busy=false;if(!closed)Controls();}
        }
        private async Task Refresh(){if(document==null||Dirty)return;status.Text="Actualisation…";Show(await read(new SharedPageEntry{Share=document.Share,Page=document.Page}));status.Text="Vue actualisée.";}
        internal void Show(SharedPageDocument value)
        {
            document=value;title.Text=String.IsNullOrEmpty(value.Title)?"Page sans titre":value.Title;subtitle.Text="Via "+value.Owner+" · "+(value.CanEdit?"Lecture et modification":"Lecture seule")+" · "+(DateTime.TryParse(value.ReadAt,out var when)?when.ToLocalTime().ToString("HH:mm:ss"):"");blocks.Children.Clear();
            foreach(var block in value.Blocks){var content=new StackPanel();if(block.Kind=="agent_instructions")content.Children.Add(Ui.Text("Instructions de la Page · lecture seule",12,true));content.Children.Add(new FlowDocumentScrollViewer{Document=Markdown(block.Markdown),IsToolBarVisible=false,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
                if(value.CanEdit&&block.Kind=="markdown")content.Children.Add(Ui.Button("Modifier ce bloc",()=>Begin(block)));
                if(block.Markdown.Contains("project-file:")||block.Markdown.Contains("library-file:")||block.Markdown.Contains("!["))content.Children.Add(Ui.Text("Média ou pièce jointe : ouvrir dans l’instance propriétaire.",12,true));blocks.Children.Add(Ui.Card(content));}
            if(value.Blocks.Length==0)blocks.Children.Add(Ui.Text("Cette Page est vide.",15,true));Controls();
        }
        private void Begin(SharedPageBlock block)
        {if(busy||document?.CanEdit!=true||editor.Visibility==Visibility.Visible)return;blockId=block?.Id??"";original=block?.Markdown??"";writeId=null;uncertain=false;draft.Text=original;editor.Visibility=((Border)editor.Tag).Visibility=Visibility.Visible;Controls();draft.Focus();}
        private void CloseEditor(){editor.Visibility=((Border)editor.Tag).Visibility=Visibility.Collapsed;draft.Text="";original=null;writeId=null;uncertain=false;Controls();}
        private async Task Save()
        {
            if(!Dirty||uncertain)return;writeId??=Guid.NewGuid().ToString("N");status.Text="Enregistrement sur la Page originale…";
            var result=await save(document,writeId,blockId,draft.Text,document.ReadId);status.Text=result.Message;
            if(result.State=="saved"||result.State=="saved_unverified"){CloseEditor();if(result.Document!=null)Show(result.Document);else document.CanEdit=false;}
            else uncertain=true;
        }
        internal static FlowDocument Markdown(string text)
        {
            var doc=new FlowDocument{FontFamily=new FontFamily("Segoe UI"),FontSize=15,PagePadding=new Thickness(0),LineHeight=24};doc.SetResourceReference(FlowDocument.ForegroundProperty,"TextBrush");bool code=false;
            foreach(string line in text.Replace("\r\n","\n").Split('\n')){
                if(line.StartsWith("```")){code=!code;continue;}
                string value=line;var p=new Paragraph{Margin=new Thickness(0,0,0,6)};
                if(code){p.FontFamily=new FontFamily("Consolas");p.FontSize=13;p.Inlines.Add(new Run(value));}
                else{int level=line.TakeWhile(c=>c=='#').Count();if(level>0&&level<=6&&line.Length>level&&line[level]==' '){value=line.Substring(level+1);p.FontSize=Math.Max(16,26-level*2);p.FontWeight=FontWeights.SemiBold;}
                    if(value.StartsWith("- "))value="• "+value.Substring(2);if(value.StartsWith("> ")){value=value.Substring(2);p.Margin=new Thickness(16,4,0,8);p.FontStyle=FontStyles.Italic;}
                    foreach(var part in System.Text.RegularExpressions.Regex.Split(value,"(\\*\\*[^*]+\\*\\*|`[^`]+`)")){if(part.StartsWith("**")&&part.EndsWith("**")&&part.Length>=4)p.Inlines.Add(new Bold(new Run(part.Substring(2,part.Length-4))));else if(part.StartsWith("`")&&part.EndsWith("`")&&part.Length>=2)p.Inlines.Add(new Run(part.Substring(1,part.Length-2)){FontFamily=new FontFamily("Consolas")});else p.Inlines.Add(new Run(part));}}
                doc.Blocks.Add(p);
            }return doc;
        }
    }
}
