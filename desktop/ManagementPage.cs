using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class ManagementChatRow {public string Id{get;set;}public string Title{get;set;}public string UpdatedUtc{get;set;}public override string ToString()=>String.IsNullOrEmpty(Title)?Id:Title;}
    internal sealed class ManagementChatCatalog {public ManagementChatRow[] Chats{get;set;}=new ManagementChatRow[0];public bool Truncated{get;set;}}
    internal sealed class ManagementPage : PageView
    {
        internal readonly ComboBox Instances=new ComboBox{DisplayMemberPath="Name",SelectedValuePath="Id"};
        internal readonly TextBox Thread=new TextBox();
        internal readonly ListBox Chats=new ListBox{MaxHeight=360};
        internal Func<ConversationManagementWindow> WindowFactory=null;
        internal Func<string,Task<ManagementChatCatalog>> ListChatsFixture=null;
        private string instanceId;
        private int generation; private bool changing;
        internal ManagementPage(DesktopContext context,ShellWindow shell):base(context,shell,"Automatisations","Consultez et modifiez l’objectif et les tâches planifiées d’une discussion existante.")
        {
            var fields=new StackPanel();Body.Children.Add(fields);fields.Children.Add(Ui.Text("Instance",13));fields.Children.Add(Instances);fields.Children.Add(Ui.Text("Discussions enregistrées dans cette instance",13));fields.Children.Add(Chats);fields.Children.Add(Ui.Text("Identifiant de la discussion Codex existante",13));fields.Children.Add(Thread);fields.Children.Add(Ui.Text("Sélectionnez une discussion ci-dessus, ou copiez son lien codex://threads/… ici. La liste lit uniquement les métadonnées locales ; le contenu des échanges n’est pas chargé. Le Switcher vérifie l’appartenance à l’instance avant toute modification.",12,true));
            Command("Ouvrir les tâches et l’objectif",Open,true);System.Windows.Automation.AutomationProperties.SetName(Instances,"Instance de la discussion à gérer");System.Windows.Automation.AutomationProperties.SetName(Thread,"Identifiant de la discussion à gérer");
            System.Windows.Automation.AutomationProperties.SetName(Chats,"Discussions existantes à gérer");
            Instances.SelectionChanged+=async delegate{if(changing)return;string selected=Instances.SelectedValue as string;if(selected==instanceId)return;instanceId=selected;Thread.Text="";await LoadChats();};Chats.SelectionChanged+=delegate{if(Chats.SelectedItem is ManagementChatRow selected)Thread.Text=selected.Id;};
        }
        private async Task Open()
        {
            string thread=Thread.Text.Trim();const string prefix="codex://threads/";if(thread.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))thread=thread.Substring(prefix.Length);Guid parsed;if(!(Instances.SelectedValue is string instance)||!Guid.TryParse(thread,out parsed))throw new InvalidOperationException("Choisissez une instance et l’identifiant d’une discussion existante.");
            var window=WindowFactory?.Invoke()??new ConversationManagementWindow(Context,Shell,instance,thread);await window.Refresh();window.ShowDialog();
        }
        private async Task LoadChats()
        {
            int turn=++generation;string target=instanceId;if(target==null){Chats.ItemsSource=null;return;}Notice.Text="Lecture des métadonnées locales des discussions…";
            try{var value=Context.Fixture?(ListChatsFixture==null?new ManagementChatCatalog():await ListChatsFixture(target)):await Task.Run(()=>SkillCatalogUiService.Convert<ManagementChatCatalog>(new PortalConversationManagement(Context.Store).ListChatsLocal(target)));if(turn!=generation||target!=instanceId)return;Chats.ItemsSource=value.Chats;Notice.Text=value.Chats.Length==0?"Aucune discussion vérifiée dans l’index local. Vous pouvez renseigner un lien existant ci-dessous.":value.Truncated?"Liste locale partielle : 100 discussions récentes au maximum. Un autre lien existant peut être renseigné ci-dessous.":value.Chats.Length+" discussion(s) vérifiée(s), sans lecture des messages.";}
            catch(Exception error){if(turn==generation){Chats.ItemsSource=null;Error(error);}}
        }
        public override async Task Refresh(){string previous=instanceId;var values=await Context.Read(a=>a.Data.Instances.Where(i=>!i.Archived).ToArray());changing=true;Instances.ItemsSource=values;Instances.SelectedValue=previous;if(Instances.SelectedIndex<0&&Instances.Items.Count>0)Instances.SelectedIndex=0;string selected=Instances.SelectedValue as string;changing=false;if(selected!=instanceId){instanceId=selected;Thread.Text="";}await LoadChats();}
    }
}
