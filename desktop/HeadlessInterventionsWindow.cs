using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal sealed class HeadlessInterventionsWindow:Window
    {
        private readonly DesktopContext context;
        private readonly ComboBox requests=new ComboBox{DisplayMemberPath="Label"};
        private readonly StackPanel body=new StackPanel();
        private readonly TextBlock notice=Ui.Text("",13,true),error=Ui.Text("",13);
        private readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
        private readonly Dictionary<string,TextBox> answers=new Dictionary<string,TextBox>();
        private readonly Dictionary<string,Dictionary<string,string>> drafts=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
        private readonly Button accept,decline;
        private HeadlessApproval selected;
        private string stamp;
        private bool loading,closed,busy;
        private sealed class Row {public string Label {get;set;} public HeadlessApproval Request;}
        internal Func<Task<HeadlessApproval[]>> PendingFixture;
        internal Func<string,string,IDictionary<string,string[]>,Task> RespondFixture;
        internal bool RefreshTimerRunning=>timer.IsEnabled;
        internal HeadlessInterventionsWindow(DesktopContext context,Window owner)
        {
            this.context=context;Owner=owner;Title="Interventions des agents sans fenêtre";Width=740;Height=610;MinWidth=570;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");
            var root=new DockPanel{Margin=new Thickness(24)};Content=root;
            var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
            header.Children.Add(Ui.Text("Interventions",25));header.Children.Add(Ui.Text("Vérifiez le compte et la demande. Votre réponse est transmise au moteur de cette mission uniquement.",13,true));header.Children.Add(requests);header.Children.Add(notice);header.Children.Add(error);
            var footer=new WrapPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            accept=Ui.AsyncButton("Autoriser cette demande",()=>Respond("accept"),e=>error.Text=Program.SafeError(e),true);footer.Children.Add(accept);
            decline=Ui.AsyncButton("Refuser",()=>Respond("decline"),e=>error.Text=Program.SafeError(e));footer.Children.Add(decline);
            footer.Children.Add(Ui.Button("Fermer",()=>Close()));root.Children.Add(new ScrollViewer{Content=body,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
            System.Windows.Automation.AutomationProperties.SetName(requests,"Demandes en attente");
            requests.SelectionChanged+=delegate{Show((requests.SelectedItem as Row)?.Request);};
            timer.Tick+=async delegate{try{await RefreshNow();}catch(Exception e){error.Text=Program.SafeError(e);}};
            Loaded+=async delegate{try{await RefreshNow();if(!closed)timer.Start();}catch(Exception e){error.Text=Program.SafeError(e);}};
            Closed+=delegate{closed=true;timer.Stop();};
        }
        internal async Task RefreshNow()
        {
            if(loading||closed)return;loading=true;
            try{
                var pending=PendingFixture!=null?await PendingFixture():await context.Read(a=>new HeadlessApprovals(context.Store).Pending());
                if(closed)return;
                string fingerprint=Json.Write(pending.Select(r=>new{r.Id,r.Summary,r.DetailsVerified}));
                if(stamp!=fingerprint){string keep=selected?.Id;stamp=fingerprint;var names=context.Accounts().Data.Instances.ToDictionary(i=>i.Id,i=>i.Name);requests.ItemsSource=pending.Select(p=>new Row{Label=(names.TryGetValue(p.InstanceId,out var name)?name:p.InstanceId)+" · "+(p.Method.Contains("UserInput")?"Question":"Autorisation"),Request=p}).ToArray();requests.SelectedItem=requests.Items.Cast<Row>().FirstOrDefault(r=>r.Request.Id==keep)??requests.Items.Cast<Row>().FirstOrDefault();if(pending.Length==0)Show(null);}
                var activeIds=new HashSet<string>(pending.Select(p=>p.Id),StringComparer.Ordinal);foreach(string id in drafts.Keys.Where(id=>!activeIds.Contains(id)).ToArray())drafts.Remove(id);
                notice.Text=pending.Length==0?"Aucune intervention en attente.":pending.Length+" demande(s) en attente. Les demandes expirent après cinq minutes.";
            }finally{loading=false;}
        }
        private void Show(HeadlessApproval request)
        {
            if(selected!=null&&answers.Count>0){drafts[selected.Id]=answers.ToDictionary(pair=>pair.Key,pair=>pair.Value.Text,StringComparer.Ordinal);foreach(string id in drafts.Keys.Where(id=>id!=selected.Id).Take(Math.Max(0,drafts.Count-64)).ToArray())drafts.Remove(id);}
            selected=request;answers.Clear();body.Children.Clear();error.Text="";accept.IsEnabled=CanAccept;decline.IsEnabled=request!=null&&!busy;
            if(request==null)return;
            var accounts=context.Accounts();var instance=accounts.Data.Instances.FirstOrDefault(i=>i.Id==request.InstanceId);var account=accounts.Data.Profiles.FirstOrDefault(p=>p.Key==request.AccountKey);
            body.Children.Add(Ui.Text(instance?.Name??request.InstanceId,20));body.Children.Add(Ui.Text(account?.Email??"Compte lié à cette mission",14,true));
            body.Children.Add(Ui.Text(request.Summary??"",13));
            if(request.Method=="item/fileChange/requestApproval"&&!request.DetailsVerified)body.Children.Add(Ui.Text("Les fichiers et modifications natifs sont en cours de récupération. Vous pouvez refuser ; l'autorisation reste désactivée tant que leur périmètre n'est pas connu.",13,true));
            bool input=request.Method=="item/tool/requestUserInput";accept.Content=input?"Envoyer mes réponses":"Autoriser cette demande";
            if(DateTime.TryParse(request.ExpiresUtc,out var expiry))body.Children.Add(Ui.Text("Expire à "+expiry.ToLocalTime().ToString("HH:mm:ss"),12,true));
            foreach(var question in RelayEngine.Rows(request.Questions)){
                string id=Json.Str(Json.Get(question,"id"));string title=Json.Str(Json.Get(question,"question"));
                if(String.IsNullOrWhiteSpace(id))continue;
                string value="";if(drafts.TryGetValue(request.Id,out var saved))saved.TryGetValue(id,out value);var answer=Ui.Input(title,value??"",body,true);answers[id]=answer;
                foreach(var option in RelayEngine.Rows(Json.Get(question,"options"))){string label=Json.Str(Json.Get(option,"label"));if(label.Length==0)continue;body.Children.Add(Ui.Button(label,()=>answer.Text=label));}
            }
            if(request.Method=="item/permissions/requestApproval")body.Children.Add(Ui.Text("L’autorisation porte uniquement sur les permissions affichées et sur le tour en cours.",13,true));
        }
        private bool CanAccept=>selected!=null&&!busy&&(selected.Method!="item/fileChange/requestApproval"||selected.DetailsVerified);
        private async Task Respond(string decision)
        {
            if(selected==null||busy)return;
            string id=selected.Id;var values=selected.Method=="item/tool/requestUserInput"&&decision=="accept"?answers.ToDictionary(p=>p.Key,p=>new[]{p.Value.Text}):null;
            busy=true;accept.IsEnabled=decline.IsEnabled=false;
            try{
                if(RespondFixture!=null)await RespondFixture(id,decision,values);
                else await context.Mutate(a=>{new HeadlessApprovals(context.Store).Respond(id,decision,values);return Task.CompletedTask;});
                drafts.Remove(id);
                stamp=null;await RefreshNow();
            }finally{busy=false;accept.IsEnabled=CanAccept;decline.IsEnabled=selected!=null;}
        }
    }
}
