using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task HeadlessUiAcceptance(DesktopContext context,ShellWindow shell,DesktopInstance instance,string root,Action<bool,string> assert)
        {
            int reads=0;context.HeadlessAccountFixture=id=>{reads++;return Task.FromResult("Connexion fictive vérifiée");};
            var saved=new TaskCompletionSource<bool>();
            _=shell.Dispatcher.BeginInvoke(new Action(async()=>{
                EditWindow form=null;
                try{
                    form=await WaitDialog("Fonctionnement sans fenêtre");
                    var enabled=Descendants(form).OfType<CheckBox>().Single();var permissions=Descendants(form).OfType<ComboBox>().Single();
                    enabled.IsChecked=false;permissions.SelectedIndex=0;
                    Descendants(form).OfType<Button>().Single(b=>Object.Equals(b.Content,"Vérifier la connexion Cloud")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(()=>reads==1&&Descendants(form).OfType<TextBlock>().Any(t=>t.Text.Contains("Connexion fictive vérifiée")));
                    assert(!permissions.IsEnabled,"headless opt out disables permissions without opening Codex");
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>form.Saved);
                    var stored=context.Accounts().Data.Instances.Single(i=>i.Id==instance.Id);
                    assert(!stored.HeadlessEnabled&&stored.HeadlessSandbox=="read-only"&&stored.AccountKey==instance.AccountKey,"headless preferences persist without account replacement or cross thread UI access");saved.SetResult(true);
                }catch(Exception e){saved.SetException(e);}finally{if(form!=null&&!form.Saved){form.ConfirmDiscard=()=>true;form.Close();}}
            }),DispatcherPriority.ContextIdle);
            await HeadlessSettingsWindow.Open(context,shell,instance.Id);await saved.Task;
            await context.Mutate(a=>{a.Data.Instances.Single(i=>i.Id==instance.Id).HeadlessEnabled=true;a.Save();return Task.CompletedTask;});
            var pending=new[]{new HeadlessApproval{Id=Guid.NewGuid().ToString("N"),InstanceId=instance.Id,AccountKey=instance.AccountKey,ThreadId=Guid.NewGuid().ToString(),Method="item/tool/requestUserInput",Summary="Question fictive",ExpiresUtc=DateTime.UtcNow.AddMinutes(5).ToString("o"),Questions=Json.Read<object>("[{\"id\":\"choice\",\"question\":\"Quel résultat fictif ?\",\"options\":[{\"label\":\"Réponse A\"}]}]")}};
            int responses=0;IDictionary<string,string[]> received=null;
            var window=new HeadlessInterventionsWindow(context,shell){PendingFixture=()=>Task.FromResult(pending),RespondFixture=(id,decision,values)=>{responses++;received=values;pending=new HeadlessApproval[0];return Task.CompletedTask;}};
            window.Show();await window.RefreshNow();window.UpdateLayout();
            try{
                assert(responses==0,"showing background intervention never grants approval");
                var answer=Descendants(window).OfType<TextBox>().Single();answer.Text="Réponse explicite";await window.RefreshNow();window.UpdateLayout();
                assert(Descendants(window).OfType<TextBox>().Single().Text=="Réponse explicite","intervention polling preserves user input");
                var question=pending[0];var file=new HeadlessApproval{Id=Guid.NewGuid().ToString("N"),InstanceId=instance.Id,AccountKey=instance.AccountKey,ThreadId=question.ThreadId,Method="item/fileChange/requestApproval",Summary="Détails en cours",ExpiresUtc=question.ExpiresUtc};pending=new[]{question,file};await window.RefreshNow();window.UpdateLayout();
                assert(Descendants(window).OfType<TextBox>().Single().Text=="Réponse explicite","another intervention arriving preserves the selected question draft");
                var choices=Descendants(window).OfType<ComboBox>().Single();choices.SelectedIndex=1;window.UpdateLayout();
                assert(!Descendants(window).OfType<Button>().Single(b=>Object.Equals(b.Content,"Autoriser cette demande")).IsEnabled,"opaque file changes cannot be authorized");
                file.Summary="fixture.txt\n+changement natif fictif";file.DetailsVerified=true;await window.RefreshNow();window.UpdateLayout();
                assert(Descendants(window).OfType<Button>().Single(b=>Object.Equals(b.Content,"Autoriser cette demande")).IsEnabled,"verified native file details unlock only the explicit approval action");
                choices.SelectedIndex=0;window.UpdateLayout();assert(Descendants(window).OfType<TextBox>().Single().Text=="Réponse explicite","switching interventions restores the original answer draft");
                Capture(window,Path.Combine(root,"Interventions-sans-fenetre.png"));
                Descendants(window).OfType<Button>().Single(b=>Object.Equals(b.Content,"Envoyer mes réponses")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(()=>responses==1&&received!=null);
                assert(received["choice"].Single()=="Réponse explicite","only the explicit question response reaches the background worker");
            }finally{window.Close();}
            assert(!window.RefreshTimerRunning,"background interventions stop polling on close");
        }
    }
}
