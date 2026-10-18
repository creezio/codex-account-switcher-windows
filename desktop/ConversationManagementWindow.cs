using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class ConversationUiCapabilities {public bool CanReadGoal{get;set;}public bool CanWriteGoal{get;set;}public bool CanManageSchedules{get;set;}public string TimeZoneId{get;set;}public string GoalIssue{get;set;}}
    internal sealed class GoalUiValue {public string Objective{get;set;}public string Status{get;set;}public long? TokenBudget{get;set;}public long TokensUsed{get;set;}public double TimeUsedSeconds{get;set;}public string Version{get;set;}}
    internal sealed class ScheduleUiValue
    {
        public string Id{get;set;}public long Revision{get;set;}public string Name{get;set;}public string Prompt{get;set;}public string Status{get;set;}public string Frequency{get;set;}public int? IntervalMinutes{get;set;}public string LocalTime{get;set;}public string[] DaysOfWeek{get;set;}=new string[0];public string TimeZoneId{get;set;}public string NextRunUtc{get;set;}public string LastRunUtc{get;set;}public string State{get;set;}public string Error{get;set;}
        public override string ToString()=>Name+" · "+(Status=="ACTIVE"?"Active":"En pause")+(String.IsNullOrEmpty(State)?"":" · "+State)+(String.IsNullOrEmpty(Error)?"":" · à vérifier");
    }
    internal sealed class ConversationUiSnapshot {public string InstanceId{get;set;}public string ChatId{get;set;}public string ThreadId{get;set;}public ConversationUiCapabilities Capabilities{get;set;}public GoalUiValue Goal{get;set;}public ScheduleUiValue[] Schedules{get;set;}=new ScheduleUiValue[0];}
    internal sealed class ConversationManagementUiService
    {
        internal Func<string,string,Task<ConversationUiSnapshot>> Read;
        internal Func<string,string,string,object,Task<ConversationUiSnapshot>> Goal,SaveSchedule;
        internal Func<string,string,string,string,object,Task<ConversationUiSnapshot>> DeleteSchedule;
        internal static ConversationManagementUiService Create(DesktopContext context)
        {
            if(context.Fixture)return new ConversationManagementUiService{Read=(instance,chat)=>Task.FromException<ConversationUiSnapshot>(new InvalidOperationException("La discussion de recette n’est pas configurée ; aucune instance réelle n’a été contactée."))};
            var source=new PortalConversationManagement(context.Store);
            return new ConversationManagementUiService{
                Read=(instance,chat)=>Task.Run(()=>SkillCatalogUiService.Convert<ConversationUiSnapshot>(source.ReadLocal(instance,chat))),
                Goal=(instance,chat,key,body)=>Task.Run(()=>SkillCatalogUiService.Convert<ConversationUiSnapshot>(source.GoalLocal(instance,chat,key,body))),
                SaveSchedule=(instance,chat,key,body)=>Task.Run(()=>SkillCatalogUiService.Convert<ConversationUiSnapshot>(source.SaveScheduleLocal(instance,chat,key,body))),
                DeleteSchedule=(instance,chat,id,key,body)=>Task.Run(()=>SkillCatalogUiService.Convert<ConversationUiSnapshot>(source.DeleteScheduleLocal(instance,chat,id,key,body)))
            };
        }
    }
    internal sealed class ConversationManagementWindow : Window
    {
        private readonly ConversationManagementUiService service;
        private readonly string instance,chat;
        private ConversationUiSnapshot snapshot;
        private GoalUiValue goalBase;
        private ScheduleUiValue scheduleBase;
        private Func<Task<ConversationUiSnapshot>> pending;
        private string pendingKind;
        private bool busy,changing,goalEdited,scheduleEdited,hasSchedule;
        internal readonly TextBox Objective=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=110},Budget=new TextBox(),ScheduleName=new TextBox(),Prompt=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=100},Interval=new TextBox{Text="60"},LocalTime=new TextBox{Text="09:00"};
        internal readonly ComboBox GoalStatus=new ComboBox{DisplayMemberPath="Value",SelectedValuePath="Key"},ScheduleStatus=new ComboBox{DisplayMemberPath="Value",SelectedValuePath="Key"},Frequency=new ComboBox{DisplayMemberPath="Value",SelectedValuePath="Key"};
        internal readonly ListBox Schedules=new ListBox();
        internal readonly Button SaveGoalButton,ClearGoalButton,SaveScheduleButton,DeleteScheduleButton,NewScheduleButton,RefreshButton,RetryButton,ReloadGoalButton;
        internal Func<string,bool> Confirm;
        private readonly Dictionary<string,CheckBox> days=new Dictionary<string,CheckBox>();
        private readonly TextBlock notice=Ui.Text("",13),goalSummary=Ui.Text("",12,true),scheduleSummary=Ui.Text("",12,true);
        internal string StatusText=>notice.Text;
        internal ConversationManagementWindow(DesktopContext context,Window owner,string instanceId,string chatId,ConversationManagementUiService adapter=null)
        {
            service=adapter??ConversationManagementUiService.Create(context);instance=instanceId;chat=chatId;Owner=owner;Title="Tâches et objectif de la discussion";Width=940;Height=760;MinWidth=700;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");Confirm=message=>Ui.Confirm(this,message,"Tâches et objectif");
            var root=new DockPanel{Margin=new Thickness(22)};Content=root;var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);header.Children.Add(Ui.Text("Tâches et objectif",25));header.Children.Add(Ui.Text("Discussion "+chat+" · instance "+instance,12,true));
            var commands=Ui.Actions(header);RefreshButton=Action("Actualiser les réglages",Refresh);commands.Children.Add(RefreshButton);RetryButton=Action("Retrouver la même demande",Retry);commands.Children.Add(RetryButton);header.Children.Add(notice);AutomationProperties.SetName(notice,"État des tâches et de l’objectif");
            var tabs=new TabControl();root.Children.Add(tabs);var goal=new StackPanel();var goalScroll=new ScrollViewer{Content=goal,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};tabs.Items.Add(new TabItem{Header="Objectif",Content=goalScroll});goal.Children.Add(goalSummary);Add(goal,"Objectif de la discussion",Objective);GoalStatus.ItemsSource=new[]{Pair("active","Actif"),Pair("paused","En pause"),Pair("blocked","Bloqué"),Pair("complete","Terminé"),Pair("usageLimited","Limite d’utilisation atteinte"),Pair("budgetLimited","Budget atteint")};Add(goal,"Statut de l’objectif",GoalStatus);Add(goal,"Budget de jetons (vide : sans limite)",Budget);goal.Children.Add(Ui.Text("Modifier seulement le statut ou le budget conserve l’historique. Modifier le texte remplace l’objectif et remet son utilisation à zéro.",12,true));var goalActions=Ui.Actions(goal);SaveGoalButton=Action("Enregistrer l’objectif",SaveGoal,true);ClearGoalButton=Action("Supprimer l’objectif",ClearGoal);goalActions.Children.Add(SaveGoalButton);goalActions.Children.Add(ClearGoalButton);
            var schedule=new StackPanel();tabs.Items.Add(new TabItem{Header="Tâches planifiées",Content=new ScrollViewer{Content=schedule,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});schedule.Children.Add(Ui.Text("Les tâches sont exécutées par le Switcher sur ce PC. Gardez-le démarré. Une nouvelle tâche est préparée en pause ; son activation autorise l’envoi de la consigne aux horaires choisis.",12,true));schedule.Children.Add(scheduleSummary);Schedules.MaxHeight=170;schedule.Children.Add(Schedules);NewScheduleButton=Ui.Button("Préparer une tâche en pause",NewSchedule,true);schedule.Children.Add(NewScheduleButton);Add(schedule,"Nom de la tâche",ScheduleName);Add(schedule,"Consigne pour cette discussion",Prompt);ScheduleStatus.ItemsSource=new[]{Pair("PAUSED","En pause"),Pair("ACTIVE","Active · autoriser les exécutions")};Add(schedule,"Statut de la tâche",ScheduleStatus);Frequency.ItemsSource=new[]{Pair("interval","À intervalle régulier"),Pair("daily","Chaque jour"),Pair("weekly","Chaque semaine")};Add(schedule,"Fréquence",Frequency);Add(schedule,"Intervalle en minutes (5 à 10 080)",Interval);Add(schedule,"Heure locale du PC (HH:mm)",LocalTime);var week=Ui.Actions(schedule);string[] ids={"MO","TU","WE","TH","FR","SA","SU"},labels={"Lundi","Mardi","Mercredi","Jeudi","Vendredi","Samedi","Dimanche"};for(int n=0;n<ids.Length;n++){var box=new CheckBox{Content=labels[n],Margin=new Thickness(0,0,12,0)};days[ids[n]]=box;week.Children.Add(box);box.Checked+=delegate{if(!changing)scheduleEdited=true;Controls();};box.Unchecked+=delegate{if(!changing)scheduleEdited=true;Controls();};}var scheduleActions=Ui.Actions(schedule);SaveScheduleButton=Action("Enregistrer la tâche",SaveSchedule,true);DeleteScheduleButton=Action("Supprimer la tâche",DeleteSchedule);scheduleActions.Children.Add(SaveScheduleButton);scheduleActions.Children.Add(DeleteScheduleButton);
            foreach(var field in new[]{Objective,Budget})field.TextChanged+=delegate{if(!changing)goalEdited=true;Controls();};GoalStatus.SelectionChanged+=delegate{if(!changing)goalEdited=true;Controls();};foreach(var field in new[]{ScheduleName,Prompt,Interval,LocalTime})field.TextChanged+=delegate{if(!changing)scheduleEdited=true;Controls();};ScheduleStatus.SelectionChanged+=delegate{if(!changing)scheduleEdited=true;Controls();};Frequency.SelectionChanged+=delegate{if(!changing)scheduleEdited=true;Controls();};Schedules.SelectionChanged+=delegate{if(!changing&&Schedules.SelectedItem is ScheduleUiValue selected){if(scheduleEdited&&!Confirm("Abandonner le brouillon de tâche ?"))return;ShowSchedule(selected);}};
            ReloadGoalButton=Action("Recharger l’objectif enregistré",async()=>{if(goalEdited&&!Confirm("Abandonner le brouillon d’objectif et relire la version enregistrée ?"))return;goalEdited=false;await Refresh();});goalActions.Children.Add(ReloadGoalButton);
            Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs args){if(busy||pending!=null){notice.Text="Attendez la réponse ou retrouvez la même demande avant de fermer.";args.Cancel=true;}else if((goalEdited||scheduleEdited)&&!Confirm("Fermer et abandonner les brouillons non enregistrés ?"))args.Cancel=true;};Controls();
        }
        private static KeyValuePair<string,string> Pair(string key,string label)=>new KeyValuePair<string,string>(key,label);
        private static void Add(Panel panel,string name,Control field){panel.Children.Add(Ui.Text(name,12));panel.Children.Add(field);AutomationProperties.SetName(field,name);}
        private void Error(Exception error){notice.Text=Program.SafeError(error);}
        private Button Action(string caption,Func<Task> action,bool primary=false){Button button=null;button=Ui.Button(caption,async()=>{if(!button.IsEnabled)return;try{await action();}catch(Exception error){Error(error);}finally{Controls();}},primary);return button;}
        private void Controls()
        {
            bool locked=busy||pending!=null;RefreshButton.IsEnabled=!busy;RetryButton.Visibility=pending==null?Visibility.Collapsed:Visibility.Visible;RetryButton.IsEnabled=!busy&&pending!=null;bool write=!locked&&snapshot?.Capabilities.CanWriteGoal==true;Objective.IsEnabled=write;Budget.IsEnabled=write;GoalStatus.IsEnabled=write;SaveGoalButton.IsEnabled=write&&!String.IsNullOrWhiteSpace(Objective.Text)&&(GoalStatus.SelectedValue as string==goalBase?.Status||new[]{"active","paused","blocked","complete"}.Contains(GoalStatus.SelectedValue as string));ClearGoalButton.IsEnabled=write&&goalBase!=null;
            bool schedules=!locked&&snapshot?.Capabilities.CanManageSchedules==true;NewScheduleButton.IsEnabled=schedules;Schedules.IsEnabled=schedules;foreach(var control in new Control[]{ScheduleName,Prompt,ScheduleStatus,Frequency,Interval,LocalTime})control.IsEnabled=schedules&&hasSchedule;foreach(var box in days.Values)box.IsEnabled=schedules&&hasSchedule&&(string)Frequency.SelectedValue=="weekly";Interval.Visibility=(string)Frequency.SelectedValue=="interval"?Visibility.Visible:Visibility.Collapsed;LocalTime.Visibility=(string)Frequency.SelectedValue=="interval"?Visibility.Collapsed:Visibility.Visible;SaveScheduleButton.IsEnabled=schedules&&hasSchedule&&!String.IsNullOrWhiteSpace(ScheduleName.Text)&&!String.IsNullOrWhiteSpace(Prompt.Text);DeleteScheduleButton.IsEnabled=schedules&&scheduleBase!=null;
            if(ReloadGoalButton!=null)ReloadGoalButton.IsEnabled=!locked;
        }
        internal async Task Refresh()
        {
            if(busy)return;busy=true;Controls();try{Accept(await service.Read(instance,chat),"");}catch(Exception error){Error(error);}finally{busy=false;Controls();}
        }
        private void Accept(ConversationUiSnapshot value,string saved)
        {
            if(value==null||value.InstanceId!=instance||value.ChatId!=chat||value.Capabilities==null)throw new InvalidOperationException("Les réglages reçus ne correspondent pas à cette discussion.");snapshot=value;changing=true;
            if(!goalEdited||saved=="goal"){goalBase=value.Goal;Objective.Text=value.Goal?.Objective??"";Budget.Text=value.Goal?.TokenBudget?.ToString(CultureInfo.InvariantCulture)??"";GoalStatus.SelectedValue=value.Goal?.Status??"paused";goalEdited=false;}
            goalSummary.Text=value.Capabilities.GoalIssue??(value.Goal==null?"Aucun objectif enregistré.":(value.Goal.TokensUsed+" jetons utilisés · "+Math.Round(value.Goal.TimeUsedSeconds/60)+" min · "+(value.Goal.TokenBudget.HasValue?"budget "+value.Goal.TokenBudget:"sans budget")));
            Schedules.ItemsSource=value.Schedules;scheduleSummary.Text="Ordonnanceur Switcher · fuseau du PC : "+value.Capabilities.TimeZoneId+" · "+value.Schedules.Length+" tâche(s).";
            if(saved=="schedule"){hasSchedule=false;scheduleBase=null;scheduleEdited=false;ScheduleName.Text="";Prompt.Text="";}
            changing=false;if(saved!="")notice.Text="Modification confirmée par le Switcher.";Controls();
        }
        private void NewSchedule(){if(scheduleEdited&&!Confirm("Abandonner le brouillon de tâche ?"))return;ShowSchedule(new ScheduleUiValue{Status="PAUSED",Frequency="daily",IntervalMinutes=60,LocalTime="09:00",DaysOfWeek=new[]{"MO"}});scheduleBase=null;}
        private void ShowSchedule(ScheduleUiValue value){changing=true;scheduleBase=value;hasSchedule=true;ScheduleName.Text=value.Name??"";Prompt.Text=value.Prompt??"";ScheduleStatus.SelectedValue=value.Status;Frequency.SelectedValue=value.Frequency;Interval.Text=(value.IntervalMinutes??60).ToString(CultureInfo.InvariantCulture);LocalTime.Text=value.LocalTime??"09:00";foreach(var pair in days)pair.Value.IsChecked=(value.DaysOfWeek??new string[0]).Contains(pair.Key);scheduleEdited=false;changing=false;Controls();}
        private async Task Execute(Func<Task<ConversationUiSnapshot>> action,string kind)
        {
            if(busy||pending!=null&&!Object.ReferenceEquals(action,pending))return;pending=action;pendingKind=kind;busy=true;Controls();try{var result=await action();Accept(result,kind);pending=null;}catch(Exception error){if(error is WebhookException rejected&&rejected.StatusCode>=400&&rejected.StatusCode<500)pending=null;notice.Text=Program.SafeError(error)+(pending==null?"":" La réception reste à vérifier ; retrouvez la même demande.");}finally{busy=false;Controls();}
        }
        private Task Retry()=>pending==null?Task.CompletedTask:Execute(pending,pendingKind);
        internal static Dictionary<string,object> GoalBody(string objective,string status,string budget,GoalUiValue previous)
        {
            if(String.IsNullOrWhiteSpace(objective)||objective.Trim().Length>4000||status!=previous?.Status&&!new[]{"active","paused","blocked","complete"}.Contains(status))throw new InvalidOperationException("Renseignez un objectif et un statut modifiable.");long? tokens=null;if(!String.IsNullOrWhiteSpace(budget)){long parsed;if(!Int64.TryParse(budget,NumberStyles.None,CultureInfo.InvariantCulture,out parsed)||parsed<1||parsed>1000000000)throw new InvalidOperationException("Le budget doit être un entier de 1 à 1 000 000 000, ou vide.");tokens=parsed;}
            var body=new Dictionary<string,object>{{"action","set"},{"expectedVersion",previous?.Version??""}};if(status!=previous?.Status)body["status"]=status;if(objective.Trim()!=previous?.Objective)body["objective"]=objective.Trim();if(tokens!=previous?.TokenBudget)body["tokenBudget"]=tokens;if(body.Count==2)throw new InvalidOperationException("Aucune modification de l’objectif à enregistrer.");return body;
        }
        private Task SaveGoal(){if(!SaveGoalButton.IsEnabled)return Task.CompletedTask;var body=GoalBody(Objective.Text,(string)GoalStatus.SelectedValue,Budget.Text,goalBase);string key=Guid.NewGuid().ToString("N");return Execute(()=>service.Goal(instance,chat,key,body),"goal");}
        private Task ClearGoal(){if(!ClearGoalButton.IsEnabled||!Confirm("Supprimer l’objectif et son suivi d’utilisation ?"))return Task.CompletedTask;var body=new{action="clear",expectedVersion=goalBase.Version};string key=Guid.NewGuid().ToString("N");return Execute(()=>service.Goal(instance,chat,key,body),"goal");}
        private Task SaveSchedule()
        {
            if(!SaveScheduleButton.IsEnabled)return Task.CompletedTask;var body=new Dictionary<string,object>{{"name",ScheduleName.Text.Trim()},{"prompt",Prompt.Text.Trim()},{"status",(string)ScheduleStatus.SelectedValue},{"frequency",(string)Frequency.SelectedValue},{"timeZoneId",snapshot.Capabilities.TimeZoneId}};if(scheduleBase?.Id!=null){body["id"]=scheduleBase.Id;body["revision"]=scheduleBase.Revision;}
            if((string)Frequency.SelectedValue=="interval"){int minutes;if(!Int32.TryParse(Interval.Text,out minutes)||minutes<5||minutes>10080)throw new InvalidOperationException("Choisissez un intervalle de 5 à 10 080 minutes.");body["intervalMinutes"]=minutes;}else{TimeSpan time;if(!TimeSpan.TryParseExact(LocalTime.Text,"hh\\:mm",CultureInfo.InvariantCulture,out time)||time.TotalHours>=24)throw new InvalidOperationException("Renseignez une heure locale HH:mm.");body["localTime"]=LocalTime.Text;if((string)Frequency.SelectedValue=="weekly"){string[] selected=days.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray();if(selected.Length==0)throw new InvalidOperationException("Choisissez au moins un jour.");body["daysOfWeek"]=selected;}}
            string key=Guid.NewGuid().ToString("N");return Execute(()=>service.SaveSchedule(instance,chat,key,body),"schedule");
        }
        private Task DeleteSchedule(){if(!DeleteScheduleButton.IsEnabled||!Confirm("Supprimer la tâche « "+scheduleBase.Name+" » ?"))return Task.CompletedTask;string id=scheduleBase.Id,key=Guid.NewGuid().ToString("N");object body=new{revision=scheduleBase.Revision};return Execute(()=>service.DeleteSchedule(instance,chat,id,key,body),"schedule");}
    }
}
