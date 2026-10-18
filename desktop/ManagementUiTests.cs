using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class ManagementUiAcceptance
    {
        private static async Task Until(Func<bool> ready){for(int n=0;n<500;n++){if(ready())return;await Task.Delay(10);}throw new Exception("La recette des compétences et objectifs a dépassé son délai.");}
        private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        internal static async Task RunAll(DesktopContext context,ShellWindow shell,Action<bool,string> assert)
        {
            string instance=new string('a',32),other=new string('b',32),skill=new string('c',64),native=new string('d',64),revision=new string('e',64),nextRevision=new string('f',64),thread="01900000-0000-7000-8000-000000000001";
            var user=new SkillUiRow{Id=skill,Name="recette-ui",Description="Compétence fictive",Origin="user",Revision=revision,Editable=true,CanCustomize=true,Installed=true,Enabled=true};
            var system=new SkillUiRow{Id=native,Name="Préinstallée",Description="Source protégée fictive",Origin="system",Installed=true,Enabled=true};
            string document="---\nname: recette-ui\ndescription: Recette isolée\n---\n\nInstructions initiales.";
            var saveGate=new TaskCompletionSource<SkillUiMutation>();var keys=new List<string>();var bodies=new List<string>();int saves=0,reads=0;
            var adapter=new SkillCatalogUiService{
                Instances=()=>Task.FromResult(new[]{new DesktopInstance{Id=instance,Name="Instance fictive A"},new DesktopInstance{Id=other,Name="Instance fictive B"}}),
                Catalog=id=>Task.FromResult(new SkillUiCatalog{InstanceId=id,Skills=id==instance?new[]{system,user}:new SkillUiRow[0]}),
                Read=(id,selected)=>{reads++;return Task.FromResult(new SkillUiDocument{InstanceId=id,Skill=selected==native?system:user,Document=selected==native?"Instructions préinstallées protégées.":document,Files=new[]{new SkillUiFile{Path="SKILL.md",Bytes=60}}});},
                Save=(id,key,request)=>{saves++;keys.Add(key);bodies.Add(Json.Write(request));if(saves==1)return saveGate.Task;document=request.Document;user.Revision=nextRevision;return Task.FromResult(new SkillUiMutation{State="deployed",Skill=user,OperationId=instance,Message="Recette : modification confirmée."});}
            };
            var page=new SkillCatalogPage(context,shell,adapter){ConfirmDiscard=message=>true};await page.Refresh();
            assert(page.Skills.Items.Count==2,"compétences : catalogue conserve la compétence préinstallée sans révision et la compétence utilisateur");
            page.Skills.SelectedItem=system;await Until(()=>reads==1&&page.DocumentInput.Text.Contains("préinstallées"));
            assert(page.DocumentInput.IsReadOnly&&!page.SaveButton.IsEnabled,"compétences : source protégée sans paquet copiable reste lisible et non modifiable");
            page.Skills.SelectedItem=user;await Until(()=>reads==2&&page.DocumentInput.Text==document);page.DocumentInput.Text+="\nBrouillon conservé.";await page.Refresh();
            assert(page.Dirty&&page.DocumentInput.Text.Contains("Brouillon conservé"),"compétences : actualisation conserve le document non enregistré et sa révision");
            Click(page.SaveButton);Click(page.SaveButton);
            assert(saves==1&&!page.SaveButton.IsEnabled,"compétences : double clic n’admet qu’une seule modification et bloque le second envoi");
            saveGate.SetException(new IOException("Recette : réponse perdue."));await Until(()=>page.RetryButton.IsEnabled);
            assert(!page.SaveButton.IsEnabled&&!page.CanLeave()&&page.DocumentInput.Text.Contains("Brouillon conservé"),"compétences : réponse perdue conserve brouillon et demande, sans annoncer un succès");
            Click(page.RetryButton);await Until(()=>saves==2&&!page.Dirty);
            assert(keys.Count==2&&keys[0]==keys[1]&&bodies[0]==bodies[1],"compétences : retrouver le reçu réutilise exactement la clé et le paquet de la demande initiale");
            assert(page.StatusText.Contains("confirmée")&&page.CanLeave(),"compétences : reçu confirmé actualise la révision et libère le catalogue");

            var current=new ConversationUiSnapshot{InstanceId=instance,ChatId=thread,ThreadId=thread,Capabilities=new ConversationUiCapabilities{CanReadGoal=true,CanWriteGoal=true,CanManageSchedules=true,TimeZoneId="Romance Standard Time"},Goal=new GoalUiValue{Objective="Terminer la recette",Status="active",TokenBudget=5000,TokensUsed=123,Version=revision}};
            ConversationUiSnapshot Copy()=>SkillCatalogUiService.Convert<ConversationUiSnapshot>(current);
            var goalGate=new TaskCompletionSource<ConversationUiSnapshot>();var goalKeys=new List<string>();var goalBodies=new List<string>();int goalSaves=0,scheduleSaves=0;object scheduleBody=null;
            var management=new ConversationManagementUiService{
                Read=(id,chat)=>Task.FromResult(Copy()),
                Goal=(id,chat,key,body)=>{goalSaves++;goalKeys.Add(key);goalBodies.Add(Json.Write(body));if(goalSaves==1)return goalGate.Task;current.Goal.Status="paused";current.Goal.TokenBudget=6000;current.Goal.Version=nextRevision;return Task.FromResult(Copy());},
                SaveSchedule=(id,chat,key,body)=>{scheduleSaves++;scheduleBody=Json.Read<object>(Json.Write(body));return Task.FromResult(Copy());}
            };
            var window=new ConversationManagementWindow(context,shell,instance,thread,management){Confirm=message=>true};await window.Refresh();
            window.GoalStatus.SelectedValue="paused";window.Budget.Text="6000";await window.Refresh();
            assert(window.GoalStatus.SelectedValue as string=="paused"&&window.Budget.Text=="6000","objectif : actualisation conserve le statut et le budget non enregistrés");
            Click(window.SaveGoalButton);Click(window.SaveGoalButton);
            assert(goalSaves==1&&!window.SaveGoalButton.IsEnabled&&!window.NewScheduleButton.IsEnabled,"objectif : modification admise une seule fois et autres mutations désactivées pendant sa réponse");
            var sent=Json.Read<Dictionary<string,object>>(goalBodies[0]);
            assert(!sent.ContainsKey("objective")&&Json.Str(Json.Get(sent,"expectedVersion"))==revision,"objectif : changement du budget et statut conserve l’historique et la version lue");
            var limitedBody=ConversationManagementWindow.GoalBody("Mission limitée","budgetLimited","6000",new GoalUiValue{Objective="Mission limitée",Status="budgetLimited",TokenBudget=5000,Version=revision});
            assert(!limitedBody.ContainsKey("status")&&!limitedBody.ContainsKey("objective")&&Convert.ToInt64(limitedBody["tokenBudget"])==6000,"objectif : modifier seulement un budget limité ne renvoie pas le statut système ni le texte");
            goalGate.SetException(new IOException("Recette : reçu objectif perdu."));await Until(()=>window.RetryButton.IsEnabled);
            assert(!window.SaveGoalButton.IsEnabled&&window.Budget.Text=="6000"&&window.StatusText.Contains("vérifier"),"objectif : reçu perdu n’efface pas le brouillon et ne promet aucun succès");
            Click(window.RetryButton);await Until(()=>goalSaves==2&&window.NewScheduleButton.IsEnabled);
            assert(goalKeys[0]==goalKeys[1]&&goalBodies[0]==goalBodies[1],"objectif : reprise explicite retrouve la même mutation sans changer clé ou paramètres");
            Click(window.NewScheduleButton);window.ScheduleName.Text="Revue fictive";window.Prompt.Text="Consigne fictive qui ne sera jamais exécutée.";
            assert(window.ScheduleStatus.SelectedValue as string=="PAUSED","planification : préparation d’une tâche toujours en pause par défaut");
            Click(window.SaveScheduleButton);await Until(()=>scheduleSaves==1&&window.NewScheduleButton.IsEnabled);
            assert(Json.Str(Json.Get(scheduleBody,"status"))=="PAUSED"&&Json.Str(Json.Get(scheduleBody,"timeZoneId"))=="Romance Standard Time","planification : enregistrement transmet la pause explicite et le fuseau du PC, sans démarrer de worker réel");
            current.Capabilities.CanWriteGoal=false;current.Capabilities.GoalIssue="Recette : objectif natif indisponible.";await window.Refresh();
            assert(!window.SaveGoalButton.IsEnabled&&!window.ClearGoalButton.IsEnabled&&window.NewScheduleButton.IsEnabled,"objectif : indisponibilité native désactive seulement ses mutations, planification reste indépendante");
            window.Close();

            var ownerView=new ConversationUiSnapshot{InstanceId=instance,ChatId=thread,ThreadId=thread,Capabilities=new ConversationUiCapabilities{CanReadGoal=true,CanWriteGoal=false,CanManageSchedules=true,TimeZoneId="Romance Standard Time",GoalIssue="Recette : propriétaire natif temporairement indisponible."}};
            ConversationUiSnapshot OwnerCopy()=>SkillCatalogUiService.Convert<ConversationUiSnapshot>(ownerView);
            int ownerWrites=0;var ownerBodies=new List<Dictionary<string,object>>();
            var ownerService=new ConversationManagementUiService{
                Read=(id,chat)=>Task.FromResult(OwnerCopy()),
                Goal=(id,chat,key,body)=>{ownerWrites++;ownerBodies.Add(Json.Read<Dictionary<string,object>>(Json.Write(body)));if(ownerView.Goal==null)ownerView.Goal=new GoalUiValue{Objective="Objectif depuis l’instance ouverte",Status="paused",TokenBudget=1000,Version=revision};else{ownerView.Goal.TokenBudget=2000;ownerView.Goal.Version=nextRevision;}return Task.FromResult(OwnerCopy());}
            };
            var ownerWindow=new ConversationManagementWindow(context,shell,instance,thread,ownerService){Confirm=message=>true};await ownerWindow.Refresh();
            assert(!ownerWindow.Objective.IsEnabled&&!ownerWindow.SaveGoalButton.IsEnabled&&ownerWindow.GoalSummaryText==ownerView.Capabilities.GoalIssue,"objectif : refus du propriétaire natif affiché exactement sans imposer de fermer l’instance");
            ownerView.Capabilities.CanWriteGoal=true;ownerView.Capabilities.GoalIssue="";await ownerWindow.Refresh();
            assert(ownerWindow.Objective.IsEnabled&&Equals(ownerWindow.SaveGoalButton.Content,"Créer l’objectif")&&ownerWindow.GoalSummaryText=="Aucun objectif enregistré.","objectif : capacité native rétablie active la création et le motif vide ne masque pas le résumé");
            ownerWindow.Objective.Text="Objectif depuis l’instance ouverte";ownerWindow.GoalStatus.SelectedValue="paused";ownerWindow.Budget.Text="1000";Click(ownerWindow.SaveGoalButton);await Until(()=>ownerWrites==1&&ownerWindow.ClearGoalButton.IsEnabled);
            assert(Json.Str(ownerBodies[0]["expectedVersion"])==""&&Json.Str(ownerBodies[0]["objective"])==ownerView.Goal.Objective&&Equals(ownerWindow.SaveGoalButton.Content,"Enregistrer l’objectif")&&ownerWindow.GoalSummaryText.Contains("0 jetons utilisés"),"objectif : création conserve le reçu natif et affiche l’utilisation confirmée par le propriétaire");
            ownerWindow.Budget.Text="2000";ownerView.Capabilities.CanWriteGoal=false;ownerView.Capabilities.GoalIssue="Recette : propriétaire changé, actualisez avant de reprendre.";await ownerWindow.Refresh();
            assert(ownerWindow.Budget.Text=="2000"&&!ownerWindow.SaveGoalButton.IsEnabled&&ownerWindow.GoalSummaryText==ownerView.Capabilities.GoalIssue&&ownerWrites==1,"objectif : changement de propriétaire conserve le brouillon sans envoyer une seconde mutation");
            ownerView.Capabilities.CanWriteGoal=true;ownerView.Capabilities.GoalIssue="";await ownerWindow.Refresh();
            assert(ownerWindow.Budget.Text=="2000"&&ownerWindow.SaveGoalButton.IsEnabled,"objectif : actualisation d’une capacité retrouvée conserve le budget à enregistrer");
            Click(ownerWindow.SaveGoalButton);await Until(()=>ownerWrites==2&&ownerWindow.RefreshButton.IsEnabled);
            assert(!ownerBodies[1].ContainsKey("objective")&&Json.Str(ownerBodies[1]["expectedVersion"])==revision&&Convert.ToInt64(ownerBodies[1]["tokenBudget"])==2000,"objectif : modification via le propriétaire garde le texte et la version native lus");
            ownerWindow.Close();
        }
    }
}
