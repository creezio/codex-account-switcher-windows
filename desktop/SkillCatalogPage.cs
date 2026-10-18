using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class SkillUiRow
    {
        public string Id{get;set;} public string Name{get;set;} public string Description{get;set;} public string Origin{get;set;} public string Scope{get;set;}
        public string Revision{get;set;} public string Issue{get;set;} public bool Editable{get;set;} public bool CanCustomize{get;set;} public bool Installed{get;set;} public bool Enabled{get;set;}
        public string Label=>Name+" · "+SkillCatalogPage.Origin(Origin)+(Installed?Enabled?" · activée":" · désactivée":" · disponibilité non confirmée");
        public override string ToString()=>Label;
    }
    internal sealed class SkillUiFile {public string Path{get;set;}public long Bytes{get;set;}}
    internal sealed class SkillUiCatalog {public string InstanceId{get;set;}public SkillUiRow[] Skills{get;set;}=new SkillUiRow[0];public string[] Warnings{get;set;}=new string[0];}
    internal sealed class SkillUiDocument {public string InstanceId{get;set;}public SkillUiRow Skill{get;set;}public string Document{get;set;}public SkillUiFile[] Files{get;set;}=new SkillUiFile[0];public bool FilesTruncated{get;set;}}
    internal sealed class SkillUiMutation {public string OperationId{get;set;}public string State{get;set;}public string Message{get;set;}public SkillUiRow Skill{get;set;}}
    internal sealed class SkillCatalogUiService
    {
        internal Func<Task<DesktopInstance[]>> Instances;
        internal Func<string,Task<SkillUiCatalog>> Catalog;
        internal Func<string,string,Task<SkillUiDocument>> Read;
        internal Func<string,string,PortalSkillSaveRequest,Task<SkillUiMutation>> Save;
        internal Func<string,string,PortalSkillImportRequest,Task<SkillUiMutation>> Import;
        internal Func<string,string,PortalSkillDeployRequest,Task<SkillUiMutation>> Deploy;
        internal static T Convert<T>(object value)=>Json.Read<T>(Json.Write(value));
        internal static SkillCatalogUiService Create(DesktopContext context)
        {
            if(context.Fixture)return new SkillCatalogUiService{Instances=()=>context.Read(a=>a.Data.Instances.Where(i=>!i.Archived).ToArray()),Catalog=id=>Task.FromException<SkillUiCatalog>(new InvalidOperationException("Le catalogue de recette n’est pas configuré ; aucune instance réelle n’a été lue."))};
            var source=new PortalSkillService(context.Store);
            return new SkillCatalogUiService{
                Instances=()=>context.Read(a=>a.Data.Instances.Where(i=>!i.Archived).ToArray()),
                Catalog=id=>Task.Run(()=>Convert<SkillUiCatalog>(source.CatalogLocal(id))),Read=(id,skill)=>Task.Run(()=>Convert<SkillUiDocument>(source.ReadLocal(id,skill))),
                Save=(id,key,value)=>Task.Run(()=>Convert<SkillUiMutation>(source.SaveLocal(id,key,value))),Import=(id,key,value)=>Task.Run(()=>Convert<SkillUiMutation>(source.ImportLocal(id,key,value))),Deploy=(id,key,value)=>Task.Run(()=>Convert<SkillUiMutation>(source.DeployLocal(id,key,value)))
            };
        }
    }
    internal sealed class SkillCatalogPage : PageView
    {
        private readonly SkillCatalogUiService service;
        internal readonly ComboBox Instances=new ComboBox{DisplayMemberPath="Name",SelectedValuePath="Id"},Recipient=new ComboBox{DisplayMemberPath="Name",SelectedValuePath="Id"};
        internal readonly TextBox Search=new TextBox(),NameInput=new TextBox(),DocumentInput=new TextBox{AcceptsReturn=true,AcceptsTab=true,TextWrapping=TextWrapping.NoWrap,MinHeight=260,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
        internal readonly ListBox Skills=new ListBox();
        internal readonly CheckBox Replace=new CheckBox{Content="Remplacer la version personnalisée existante dans cette instance"};
        internal readonly Button SaveButton,RetryButton,DeployButton;
        internal Func<string,bool> ConfirmDiscard;
        private readonly Button refresh,newSkill,import;
        private readonly TextBlock detail=Ui.Text("Sélectionnez une compétence pour lire ses instructions et ses fichiers.",12,true),files=Ui.Text("",12,true);
        private SkillUiCatalog catalog,targetCatalog;
        private SkillUiDocument document;
        private DesktopInstance[] instances=new DesktopInstance[0];
        private string instanceId;
        private bool busy,changing,editing;
        private int epoch;
        private Func<Task<SkillUiMutation>> pending;
        private bool pendingSelectSaved;
        internal bool Dirty=>editing&&(document==null||NameInput.Text!=document.Skill.Name||DocumentInput.Text!=document.Document);
        internal string StatusText=>Notice.Text;
        internal SkillCatalogPage(DesktopContext context,ShellWindow shell,SkillCatalogUiService adapter=null):base(context,shell,"Compétences","Consultez les compétences natives, de plugins et personnalisées ; créez une copie propre à chaque instance.")
        {
            service=adapter??SkillCatalogUiService.Create(context);ConfirmDiscard=message=>Ui.Confirm(shell,message,"Brouillon de compétence");
            refresh=OwnCommand("Actualiser les compétences",Refresh);
            newSkill=OwnCommand("Créer une compétence",()=>{if(!CanDiscard())return Task.CompletedTask;document=null;editing=true;NameInput.Text="ma-competence";DocumentInput.Text="---\nname: ma-competence\ndescription: Décrivez quand utiliser cette compétence.\n---\n\n# Instructions\n\nDécrivez les étapes à suivre.\n";detail.Text="Nouvelle compétence · 64 Kio maximum. Le champ name doit correspondre au nom du document.";files.Text="";Controls();return Task.CompletedTask;},true);
            import=OwnCommand("Importer une archive ZIP",ImportArchive);
            RetryButton=OwnCommand("Retrouver la même demande",Retry);
            var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(310)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(18)});grid.ColumnDefinitions.Add(new ColumnDefinition());Body.Children.Add(grid);
            var rail=new DockPanel();grid.Children.Add(rail);var filters=new StackPanel();DockPanel.SetDock(filters,Dock.Top);rail.Children.Add(filters);filters.Children.Add(Ui.Text("Instance",12));filters.Children.Add(Instances);filters.Children.Add(Ui.SearchField(Search,"Rechercher une compétence"));rail.Children.Add(Skills);
            var editor=new StackPanel();var scroll=new ScrollViewer{Content=editor,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(scroll,2);grid.Children.Add(scroll);
            editor.Children.Add(detail);editor.Children.Add(Ui.Text("Nom de la compétence (identique à name)",12));editor.Children.Add(NameInput);editor.Children.Add(Ui.Text("Document SKILL.md",12));editor.Children.Add(DocumentInput);editor.Children.Add(files);
            SaveButton=Action("Enregistrer la compétence",Save,true);editor.Children.Add(SaveButton);
            editor.Children.Add(Ui.Text("Déployer la version enregistrée vers une autre instance",16));editor.Children.Add(Ui.Text("Les fichiers sont copiés sans envoyer de message ni exécuter la compétence. Enregistrez d’abord votre brouillon.",12,true));editor.Children.Add(Recipient);editor.Children.Add(Replace);
            DeployButton=Action("Déployer la compétence",Deploy);editor.Children.Add(DeployButton);
            AutomationProperties.SetName(Instances,"Instance du catalogue de compétences");AutomationProperties.SetName(Skills,"Catalogue complet des compétences");AutomationProperties.SetName(NameInput,"Nom de la compétence");AutomationProperties.SetName(DocumentInput,"Instructions SKILL.md");AutomationProperties.SetName(Recipient,"Instance destinataire de la compétence");AutomationProperties.SetName(Notice,"État du catalogue de compétences");
            Instances.SelectionChanged+=async delegate{if(changing)return;string selected=Instances.SelectedValue as string;if(selected==instanceId)return;if(!CanDiscard()){changing=true;Instances.SelectedValue=instanceId;changing=false;return;}instanceId=selected;document=null;editing=false;NameInput.Text="";DocumentInput.Text="";targetCatalog=null;Recipient.SelectedIndex=-1;await LoadCatalog();};
            Skills.SelectionChanged+=async delegate{if(changing)return;var row=Skills.SelectedItem as SkillUiRow;if(row==null||row.Id==document?.Skill.Id)return;if(!CanDiscard()){changing=true;Skills.SelectedItem=catalog?.Skills.SingleOrDefault(s=>s.Id==document?.Skill.Id);changing=false;return;}await Read(row.Id);};
            Recipient.SelectionChanged+=async delegate{if(changing)return;targetCatalog=null;Replace.IsChecked=false;if(!busy&&Recipient.SelectedValue is string target){try{var value=await service.Catalog(target);if((string)Recipient.SelectedValue==target&&value.InstanceId==target)targetCatalog=value;}catch(Exception error){Error(error);}Controls();}};
            Search.TextChanged+=delegate{Render();};NameInput.TextChanged+=delegate{Controls();};DocumentInput.TextChanged+=delegate{Controls();};Replace.Checked+=delegate{Controls();};Replace.Unchecked+=delegate{Controls();};Controls();
        }
        internal static string Origin(string origin)=>origin=="system"?"Préinstallée":origin=="plugin"?"Fournie par un plugin":"Personnalisée";
        private Button Action(string caption,Func<Task> action,bool primary=false){Button button=null;button=Ui.Button(caption,async()=>{if(!button.IsEnabled)return;try{await action();}catch(Exception error){Error(error);}finally{Controls();}},primary);return button;}
        private Button OwnCommand(string caption,Func<Task> action,bool primary=false){var button=Action(caption,action,primary);Commands.Children.Add(button);return button;}
        private bool CanDiscard()=>!Dirty||ConfirmDiscard("Abandonner le brouillon de cette compétence ?");
        public override bool CanLeave(){if(busy||pending!=null){Notice.Text="Attendez la réponse ou retrouvez la même demande avant de quitter ce catalogue.";return false;}return CanDiscard();}
        public override async Task Refresh()
        {
            if(busy)return;
            var values=await service.Instances();if(busy)return;
            instances=values;changing=true;Instances.ItemsSource=instances;Instances.SelectedValue=instances.Any(i=>i.Id==instanceId)?instanceId:instances.FirstOrDefault()?.Id;instanceId=Instances.SelectedValue as string;Recipient.ItemsSource=instances.Where(i=>i.Id!=instanceId).ToArray();changing=false;
            await LoadCatalog();
        }
        private async Task LoadCatalog()
        {
            if(instanceId==null)return;int turn=++epoch;string target=instanceId;busy=true;Controls();
            try{var value=await service.Catalog(target);if(turn!=epoch||target!=instanceId)return;if(value.InstanceId!=target)throw new InvalidOperationException("Le catalogue ne correspond pas à l’instance sélectionnée.");catalog=value;Recipient.ItemsSource=instances.Where(i=>i.Id!=target).ToArray();Render();Notice.Text=String.Join("\n",value.Warnings??new string[0]);}
            catch(Exception error){if(turn==epoch)Error(error);}finally{if(turn==epoch){busy=false;Controls();}}
        }
        private void Render(){string selected=document?.Skill.Id;changing=true;Skills.ItemsSource=(catalog?.Skills??new SkillUiRow[0]).Where(row=>(row.Name+" "+row.Description+" "+Origin(row.Origin)).IndexOf(Search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();Skills.SelectedItem=Skills.Items.Cast<SkillUiRow>().FirstOrDefault(row=>row.Id==selected);changing=false;}
        private void Controls()
        {
            bool locked=busy||pending!=null;refresh.IsEnabled=!busy;newSkill.IsEnabled=!locked&&instanceId!=null;import.IsEnabled=newSkill.IsEnabled;RetryButton.IsEnabled=!busy&&pending!=null;RetryButton.Visibility=pending==null?Visibility.Collapsed:Visibility.Visible;
            Instances.IsEnabled=!locked;Skills.IsEnabled=!locked;bool editable=editing&&(document==null||document.Skill.Editable||document.Skill.CanCustomize);NameInput.IsReadOnly=!editable;DocumentInput.IsReadOnly=!editable;NameInput.IsEnabled=!locked;DocumentInput.IsEnabled=!locked;
            SaveButton.IsEnabled=!locked&&editable&&(!String.IsNullOrWhiteSpace(NameInput.Text))&&(!String.IsNullOrWhiteSpace(DocumentInput.Text))&&(Dirty||document?.Skill.Editable==false);
            Recipient.IsEnabled=!locked&&document?.Skill.Revision!=null&&!Dirty;Replace.IsEnabled=Recipient.IsEnabled;var collision=targetCatalog?.Skills.FirstOrDefault(row=>row.Origin=="user"&&row.Name==document?.Skill.Name);Replace.Visibility=collision==null?Visibility.Collapsed:Visibility.Visible;
            DeployButton.IsEnabled=Recipient.IsEnabled&&targetCatalog!=null&&Recipient.SelectedValue is string&&(collision==null||Replace.IsChecked==true);
        }
        private async Task Read(string id)
        {
            if(busy)return;busy=true;Controls();string target=instanceId;
            try{var value=await service.Read(target,id);if(value.InstanceId!=target||value.Skill?.Id!=id)throw new InvalidOperationException("La compétence reçue ne correspond pas à votre sélection.");document=value;editing=true;NameInput.Text=value.Skill.Name;DocumentInput.Text=value.Document;detail.Text=Origin(value.Skill.Origin)+" · "+(value.Skill.Editable?"Modification dans cette instance":value.Skill.CanCustomize?"Origine protégée : enregistrer créera une copie personnalisée":"Lecture seule")+(String.IsNullOrEmpty(value.Skill.Issue)?"":"\n"+value.Skill.Issue);files.Text=String.Join("\n",value.Files.Select(file=>file.Path+" · "+file.Bytes+" octets"))+(value.FilesTruncated?"\nInventaire de fichiers partiel.":"");targetCatalog=null;Recipient.SelectedIndex=-1;Replace.IsChecked=false;}
            catch(Exception error){Error(error);}finally{busy=false;Controls();}
        }
        private async Task Execute(Func<Task<SkillUiMutation>> action,bool selectSaved)
        {
            if(busy||pending!=null&&!Object.ReferenceEquals(action,pending))return;pending=action;pendingSelectSaved=selectSaved;busy=true;epoch++;Controls();
            try{var result=await action();if(result.State!="deployed"||result.Skill==null)throw new InvalidOperationException("La réception du déploiement reste à vérifier.");pending=null;Notice.Text=result.Message??"Déploiement confirmé.";busy=false;await LoadCatalog();if(selectSaved&&result.Skill!=null)await Read(result.Skill.Id);Notice.Text=result.Message??"Déploiement confirmé.";}
            catch(Exception error){if(error is WebhookException rejected&&rejected.StatusCode>=400&&rejected.StatusCode<500)pending=null;Notice.Text=Program.SafeError(error)+(pending==null?"":" Retrouvez la même demande avant une nouvelle modification.");}
            finally{busy=false;Controls();}
        }
        private Task Retry()=>pending==null?Task.CompletedTask:Execute(pending,pendingSelectSaved);
        private Task Save()
        {
            if(!SaveButton.IsEnabled)return Task.CompletedTask;string target=instanceId,key=Guid.NewGuid().ToString("N");var request=new PortalSkillSaveRequest{Name=NameInput.Text,Document=DocumentInput.Text,SourceSkillId=document?.Skill.Id,ExpectedRevision=document?.Skill.Revision};
            return Execute(()=>service.Save(target,key,request),true);
        }
        private async Task ImportArchive()
        {
            if(busy||pending!=null)return;
            var picker=new Microsoft.Win32.OpenFileDialog{Filter="Compétence ZIP|*.zip",Multiselect=false};if(picker.ShowDialog(Shell)!=true)return;var info=new FileInfo(picker.FileName);if(info.Length>2097152)throw new InvalidOperationException("L’archive dépasse 2 Mio.");
            byte[] bytes=await Task.Run(()=>File.ReadAllBytes(picker.FileName));if(bytes.Length>2097152)throw new InvalidOperationException("L’archive dépasse 2 Mio.");string target=instanceId,key=Guid.NewGuid().ToString("N");var request=new PortalSkillImportRequest{ArchiveBase64=Convert.ToBase64String(bytes)};await Execute(()=>service.Import(target,key,request),false);
        }
        private Task Deploy()
        {
            if(!DeployButton.IsEnabled)return Task.CompletedTask;string target=(string)Recipient.SelectedValue,key=Guid.NewGuid().ToString("N");var collision=targetCatalog.Skills.FirstOrDefault(row=>row.Origin=="user"&&row.Name==document.Skill.Name);var request=new PortalSkillDeployRequest{SourceInstanceId=instanceId,SkillId=document.Skill.Id,SourceRevision=document.Skill.Revision,Name=document.Skill.Name,ExpectedRevision=collision?.Revision};
            return Execute(()=>service.Deploy(target,key,request),false);
        }
    }
}
