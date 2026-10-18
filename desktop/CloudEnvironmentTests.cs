using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private sealed class CloudUiHandler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
            internal int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++;
                return Reply(request, token);
            }
        }

        private static async Task CloudAcceptance(DesktopContext context, ShellWindow shell, InstancesPage instances, DesktopInstance owner, string root, Action<bool, string> assert)
        {
            const string first = "11111111-2222-3333-4444-555555555555~asenvcfg_first";
            const string second = "11111111-2222-3333-4444-555555555555~asenvcfg_second";
            const string added = "11111111-2222-3333-4444-555555555555~asenvcfg_added";
            const string secret = "PRIVATE_CLOUD_ERROR_MUST_NOT_BE_DISPLAYED";
            bool published=false;
            var names = new Dictionary<string, string> { { first, "Brouillon cloud" }, { second, "Autre environnement" } };
            object Configuration(string id) => new { id, name = names[id], is_owner = true, share_settings = "private", status = published ? "ready" : "pending", version_id = "11111111-2222-3333-4444-555555555555~cecfgver_fixture", version_revision = published ? 2 : 1, latest_ready_version_id=published?"11111111-2222-3333-4444-555555555555~cecfgver_fixture":null, repositories = new[] { new { repository_id = "github/123", @ref = "refs/heads/main", depth = 1 } }, network_policy = new { type = "disabled" } };
            HttpResponseMessage Response(object value, HttpStatusCode code = HttpStatusCode.OK) => new HttpResponseMessage(code) { Content = new StringContent(Json.Write(value), Encoding.UTF8, "application/json") };
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> catalog = (request, token) =>
            {
                string query = request.RequestUri.Query;
                if (query.Contains("scope=user")) return Task.FromResult(Response(new { data = names.Keys.Select(Configuration).Take(1).ToArray(), next_cursor = "page-2" }));
                if (query.Contains("scope=workspace")) return Task.FromResult(Response(new { data = names.Keys.Select(Configuration).ToArray(), next_cursor = (string)null }));
                return Task.FromResult(Response(Configuration(Uri.UnescapeDataString(request.RequestUri.Segments.Last()))));
            };
            // Page two must terminate; workspace intentionally repeats the first entry.
            var handler = new CloudUiHandler { Reply = (request, token) => request.RequestUri.Query.Contains("cursor=")
                ? Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null })) : catalog(request, token) };
            var identity = new AuthIdentity { Key = "cloud-fixture:user", AccountId = "cloud-fixture", AccessToken = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { { "exp", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 }, { "https://api.openai.com/auth", new { chatgpt_account_id = "cloud-fixture", chatgpt_user_id = "user" } } }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture" };
            using (var http = new HttpClient(handler))
            {
                instances.ShowTab(3);
                shell.UpdateLayout();
                await Task.Delay(30);
                var cloud = Descendants(instances).OfType<CloudEnvironmentPanel>().Single();
                TextBlock Status() => Descendants(cloud).OfType<TextBlock>().Single(text => AutomationProperties.GetName(text) == "État de la connexion Codex Cloud");
                Button Button(string label) => Descendants(cloud).OfType<Button>().Single(button => Object.Equals(button.Content, label)||AutomationProperties.GetName(button)==label);
                MenuItem Menu(string label)=>cloud.ActionsMenu.Items.OfType<MenuItem>().Single(item=>Object.Equals(item.Header,label));
                void Invoke(string label){var button=Descendants(cloud).OfType<Button>().SingleOrDefault(item=>Object.Equals(item.Content,label)||AutomationProperties.GetName(item)==label);if(button!=null)button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));else Menu(label).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}
                assert(Status().Text.Contains("Aucun appel Cloud") && handler.Requests == 0, "onglet Cloud de recette ne contacte jamais les comptes réels");
                cloud.ClientFactory = () => new CloudBackend(http, () => identity, CloudBackend.Capture(identity));
                cloud.GitHubFixture = token => Task.FromResult(new CloudGitHubRepositoryPage { Data = new List<CloudGitHubRepository> { new CloudGitHubRepository { Id = "github/123", FullName = "fixture/project", DefaultBranch = "main" } }, Page = 1, HasNextPage = false });
                await cloud.Reload(first);
                shell.UpdateLayout();
                assert(cloud.EnvironmentChoice.Items.Count == 2 && handler.Requests == 4, "Cloud charge les pages des deux périmètres et déduplique leurs environnements");
                int beforeFilter=handler.Requests;cloud.EnvironmentSearch.Text="aucun résultat de recette";
                assert(cloud.EnvironmentList.Items.Count==1&&((CloudConfiguration)cloud.EnvironmentList.SelectedItem).Id==first&&handler.Requests==beforeFilter,"filtre local conserve l’environnement sélectionné sans nouvel appel réseau");cloud.EnvironmentSearch.Clear();
                assert(Descendants(cloud).OfType<TextBlock>().Any(text => text.Text.Contains("@example.com")&&Convert.ToString(text.ToolTip).Contains("Instance : " + owner.Name)), "Cloud conserve le compte visible et l’instance dans son information contextuelle");
                assert(Descendants(cloud).OfType<TextBlock>().Any(text => text.Text == "Brouillon enregistré · préparation à terminer") && !Descendants(cloud).OfType<TextBlock>().Any(text => text.Text.Contains("publié avec succès")), "configuration initiale affichée comme brouillon sans promesse de publication");
                assert(Button("Nouvel environnement").IsEnabled && Menu("Renommer l’environnement").IsEnabled, "actions Cloud accessibles après relecture réussie");
                assert(Descendants(cloud).OfType<TextBlock>().Any(text => text.Text.Contains("1 dépôt(s) GitHub")&&text.Text.Contains("@example.com")), "catalogue GitHub appartient au compte de l’instance affichée");
                Capture(shell, Path.Combine(root, "Instance-cloud.png"));

                int before = handler.Requests;
                handler.Reply = (request, token) => Task.FromResult(Response(new { error = new { code = "denied", message = secret } }, HttpStatusCode.Forbidden));
                Button("Actualiser le Cloud").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => Status().Text.Contains("403") && Button("Actualiser le Cloud").IsEnabled);
                assert(handler.Requests == before + 1 && cloud.EnvironmentChoice.Items.Count == 2 && !Status().Text.Contains(secret), "erreur Cloud expliquée sans secret ni relance et sans effacer la liste précédente");
                cloud.EnvironmentChoice.SelectedItem=cloud.EnvironmentChoice.Items.Cast<CloudConfiguration>().Single(value=>value.Id==second);
                await Until(()=>Status().Text.Contains("403")&&cloud.EnvironmentChoice.IsEnabled);
                assert(((CloudConfiguration)cloud.EnvironmentChoice.SelectedItem).Id==first&&((CloudConfiguration)cloud.EnvironmentList.SelectedItem).Id==first,"échec de lecture d’un autre environnement restaure la sélection correspondant à la fiche affichée");

                var waiting = new TaskCompletionSource<bool>();
                handler.Reply = async (request, token) => { waiting.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return Response(new { data = new object[0] }); };
                var reading = cloud.Reload();
                await waiting.Task;
                assert(!Button("Actualiser le Cloud").IsEnabled && !cloud.EnvironmentChoice.IsEnabled && cloud.CanLeave(), "lecture Cloud désactive les doubles actions et reste annulable");
                try { await reading; } catch (OperationCanceledException) { }
                handler.Reply = (request, token) => request.RequestUri.Query.Contains("cursor=") ? Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null })) : catalog(request, token);
                await cloud.Reload(first);

                handler.Reply = (request, token) => Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null }));
                await cloud.Reload();
                assert(cloud.EnvironmentChoice.Items.Count == 0 && Button("Nouvel environnement").IsEnabled, "premier environnement peut être créé depuis GitHub sans configuration Cloud préexistante");
                handler.Reply = (request, token) => request.RequestUri.Query.Contains("cursor=") ? Task.FromResult(Response(new { data = new object[0], next_cursor = (string)null })) : catalog(request, token);
                await cloud.Reload(first);

                var githubSuccess=cloud.GitHubFixture;cloud.GitHubFixture=token=>throw new InvalidOperationException("Catalogue GitHub indisponible dans cette recette");
                await cloud.Reload(first);
                assert(Button("Nouvel environnement").IsEnabled,"création reste accessible après un échec GitHub du panneau car l’assistant peut choisir un autre compte");
                cloud.GitHubFixture=githubSuccess;await cloud.Reload(first);

                int created = 0;
                var operationIds = new List<string>();
                var previousCloudPreferences = context.Store.ReadRecord<CloudEnvironmentPreferences>(CloudEnvironmentPreferences.Record);
                context.Store.WriteRecord(CloudEnvironmentPreferences.Record, new CloudEnvironmentPreferences { NewEnvironmentNetwork = "unrestricted" });
                cloud.CreateFixture = (request, id, token) =>
                {
                    created++;
                    operationIds.Add(id);
                    assert(request.Name == "Création contrôlée" && request.ShareSettings == "private" && !request.StartOnboarding && request.NetworkPolicy.Type == "unrestricted" && request.Repositories.Single().RepositoryId == "github/123", "formulaire Cloud transmet le réseau préféré par l’utilisateur en conservant le partage privé sans lancer d’agent");
                    names[added] = request.Name;
                    return Task.FromResult(new CloudConfiguration { Id = added, Name = request.Name });
                };
                async Task Dialog(string title, string button, Func<EditWindow, Task> inspect)
                {
                    var done = new TaskCompletionSource<bool>();
                    _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        EditWindow form = null;
                        try { form = await WaitDialog(title); await inspect(form); done.SetResult(true); }
                        catch (Exception error) { done.SetException(error); }
                        finally { if (form != null && !form.Saved) { form.ConfirmDiscard = () => true; form.Close(); } }
                    }), DispatcherPriority.ContextIdle);
                    Invoke(button);
                    await done.Task;
                    await Until(() => Button("Actualiser le Cloud").IsEnabled);
                }
                async Task EnvironmentDialog(string button,Func<CloudEnvironmentWizard,Task> inspect)
                {
                    var done=new TaskCompletionSource<bool>();
                    _=shell.Dispatcher.BeginInvoke(new Action(async()=>
                    {
                        CloudEnvironmentWizard form=null;
                        try
                        {
                            await Until(()=>Application.Current.Windows.OfType<CloudEnvironmentWizard>().Any(value=>value.IsLoaded));
                            form=Application.Current.Windows.OfType<CloudEnvironmentWizard>().Single(value=>value.IsLoaded);
                            await inspect(form);done.SetResult(true);
                        }
                        catch(Exception error){done.SetException(error);}
                        finally{if(form!=null&&!form.Saved){form.ConfirmDiscard=()=>true;form.Close();}}
                    }),DispatcherPriority.ContextIdle);
                    Invoke(button);await done.Task;await Until(()=>Button("Actualiser le Cloud").IsEnabled);
                }
                void Next(CloudEnvironmentWizard form)=>form.NextButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                async Task PrepareCreation(CloudEnvironmentWizard form,string name,string targetId=null)
                {
                    if(targetId!=null)form.Account.SelectedItem=form.Account.Items.Cast<CloudEnvironmentAccountChoice>().Single(value=>value.Id==targetId);
                    await Until(()=>!form.Loading&&form.RepositoryRows.Count>0);
                    form.RepositoryRows.First().Selected=true;Next(form);await Until(()=>form.Step==2);
                    form.EnvironmentName.Text=name;Next(form);await Until(()=>form.Step==3);Next(form);await Until(()=>form.Step==4&&form.NextButton.IsEnabled);
                }
                await EnvironmentDialog("Nouvel environnement",async form=>
                {
                    await PrepareCreation(form,"Création contrôlée");
                    assert(form.Network.SelectedIndex==2&&form.Review.Text.Contains("Internet : toutes les adresses")&&!cloud.CanLeave()&&!Button("Actualiser le Cloud").IsEnabled,"assistant relit la préférence locale toutes destinations et la montre dans le résumé en protégeant le panneau");
                    Next(form);await Until(()=>form.Saved);
                });
                await Until(() => cloud.EnvironmentChoice.Items.Count == 3 && Status().Text.Contains("créé et relu"));
                assert(created == 1 && operationIds.Single().Length == 32, "validation du formulaire envoie une seule intention identifiée avant relecture");
                context.Store.WriteRecord(CloudEnvironmentPreferences.Record, previousCloudPreferences);

                int renamed = 0;
                cloud.RenameFixture = (id, name, operationId, token) =>
                {
                    renamed++;
                    assert(id == added && name == "Nom confirmé", "renommage Cloud cible uniquement l’environnement sélectionné");
                    names[id] = name;
                    return Task.FromResult(new CloudConfiguration { Id = id, Name = name });
                };
                await Dialog("Renommer l’environnement Cloud", "Renommer l’environnement", async form =>
                {
                    Descendants(form).OfType<TextBox>().Single().Text = "Nom confirmé";
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => form.Saved);
                });
                await Until(() => Status().Text.Contains("Nom enregistré et relu"));
                assert(renamed == 1 && Descendants(cloud).OfType<TextBlock>().Any(text => text.Text == "Nom confirmé"), "renommage confirmé seulement après relecture du catalogue et du détail");

                int refused = 0;
                cloud.CreateFixture = (request, id, token) => { refused++; throw new CloudBackendException("fixture", "Refus de recette", 400); };
                await EnvironmentDialog("Nouvel environnement",async form=>
                {
                    await PrepareCreation(form,"Création refusée");Next(form);await Until(()=>form.IntentLocked&&!form.Submitting);
                    assert(!form.Saved&&form.EnvironmentName.IsReadOnly&&refused==1&&!form.NextButton.IsEnabled,"échec connu fige l’intention et interdit un second envoi depuis cet assistant");
                    Next(form);assert(refused==1,"double clic après refus ne rejoue pas la création");
                });

                int uncertain=0;
                cloud.CreateFixture=(request,id,token)=>{uncertain++;throw new CloudBackendUncertainException("réponse perdue");};
                await EnvironmentDialog("Nouvel environnement",async form=>
                {
                    await PrepareCreation(form,"Résultat incertain");Next(form);await Until(()=>form.IntentLocked&&!form.Submitting);
                    assert(!form.Saved&&uncertain==1&&!form.NextButton.IsEnabled&&Descendants(form).OfType<TextBlock>().Any(text=>text.Text.Contains("Référence :")&&text.Text.Contains(form.IntentId)),"création incertaine reste figée avec sa référence de suivi durable");
                });
                int localCreateWrites=0;
                cloud.CreateFixture=(request,id,token)=>{localCreateWrites++;throw new IOException("Échec local simulé après réponse de création");};
                await EnvironmentDialog("Nouvel environnement",async form=>
                {
                    await PrepareCreation(form,"Réponse reçue puis erreur locale");Next(form);await Until(()=>form.IntentLocked&&!form.Submitting);
                    assert(form.ErrorText.Text.Contains("incertain")&&!form.ErrorText.Text.Contains("arrêtée")&&Status().Text.Contains("relisez")&&!form.NextButton.IsEnabled,"erreur locale après entrée en création reste incertaine et invite à relire, jamais à recréer");
                    Next(form);assert(localCreateWrites==1,"erreur locale après création ne rejoue jamais la même intention");
                });
                cloud.CreateFixture=(request,id,token)=>throw new CloudBackendException("invalid-response","Réponse de succès non interprétable",200);
                await EnvironmentDialog("Nouvel environnement",async form=>
                {
                    await PrepareCreation(form,"Réponse succès invalide");Next(form);await Until(()=>form.IntentLocked&&!form.Submitting);
                    assert(form.ErrorText.Text.Contains("incertain")&&!form.NextButton.IsEnabled,"réponse invalide après succès HTTP reste incertaine et ne devient pas un refus confirmé");
                });

                var other=await context.Read(accounts=>accounts.Data.Instances.Where(instance=>!instance.Archived&&!String.IsNullOrEmpty(instance.AccountKey)&&instance.Id!=owner.Id).Select(instance=>new{instance.Id,instance.AccountKey}).First());
                int repositoryReads=0;
                cloud.WizardRepositoriesFixture=(choice,token)=>
                {
                    repositoryReads++;return Task.FromResult(new[]{new CloudGitHubRepository{Id=choice.Id==owner.Id?"github/123":"github/other",FullName=choice.Id==owner.Id?"fixture/project":"fixture/other-project",DefaultBranch="main"}});
                };
                cloud.WizardCreateFixture=(choice,request,id,token)=>
                {
                    assert(choice.Id==other.Id&&choice.AccountKey==other.AccountKey&&request.Repositories.Single().RepositoryId=="github/other","création d’un autre compte utilise son identité permanente et son propre dépôt GitHub");
                    return Task.FromResult(new CloudConfiguration{Id="foreign-environment-not-in-owner-catalog",Name=request.Name});
                };
                int requestsBeforeForeign=handler.Requests;
                await EnvironmentDialog("Nouvel environnement",async form=>{await PrepareCreation(form,"Autre compte",other.Id);Next(form);await Until(()=>form.Saved);});
                assert(handler.Requests==requestsBeforeForeign&&((CloudConfiguration)cloud.EnvironmentChoice.SelectedItem).Id==added&&Status().Text.Contains("Sélectionnez cette instance"),"création sur autre compte ne recharge jamais son identifiant dans le catalogue du propriétaire affiché");

                async Task ChangeOwnerKey(string key)=>await context.Mutate(accounts=>{accounts.Data.Instances.Single(value=>value.Id==owner.Id).AccountKey=key;accounts.Save();return Task.CompletedTask;});
                await EnvironmentDialog("Nouvel environnement",async form=>
                {
                    await Until(()=>!form.Loading&&form.RepositoryRows.Count>0);
                    form.Account.SelectedItem=form.Account.Items.Cast<CloudEnvironmentAccountChoice>().Single(value=>value.Id==other.Id);await Until(()=>!form.Loading&&form.RepositoryRows.Any(row=>row.Id=="github/other"));
                    try
                    {
                        await ChangeOwnerKey("fixture-account-changed");int readsBeforeDrift=repositoryReads;
                        form.Account.SelectedItem=form.Account.Items.Cast<CloudEnvironmentAccountChoice>().Single(value=>value.Id==owner.Id);
                        await Until(()=>!form.Loading&&form.Status.Text.Contains("a changé"));
                        assert(repositoryReads==readsBeforeDrift&&!form.NextButton.IsEnabled,"liaison de compte modifiée bloque la lecture GitHub avant tout appel du compte sélectionné");
                    }
                    finally{await ChangeOwnerKey(owner.AccountKey);}
                });
                int blockedCreates=0;cloud.WizardCreateFixture=(choice,request,id,token)=>{blockedCreates++;return Task.FromResult(new CloudConfiguration{Id=added,Name=request.Name});};
                await EnvironmentDialog("Nouvel environnement",async form=>
                {
                    await PrepareCreation(form,"Compte changé avant envoi");
                    try
                    {
                        await ChangeOwnerKey("fixture-account-changed");Next(form);await Until(()=>form.IntentLocked&&!form.Submitting);
                        assert(blockedCreates==0&&form.ErrorText.Text.Contains("a changé"),"liaison de compte modifiée avant confirmation interdit l’envoi de création");
                    }
                    finally{await ChangeOwnerKey(owner.AccountKey);}
                });
                cloud.WizardCreateFixture=null;

                int preparations = 0, follows = 0, publications = 0;
                string setupId = null;
                cloud.SetupStartFixture = (id, repositories, operationId, token) =>
                {
                    preparations++; setupId = operationId;
                    assert(id == added && repositories.Count == 1 && repositories["github/123"] == "fixture/project" && operationId.Length == 32, "préparation conserve le compte sélectionné et le nom GitHub explicitement confirmé");
                    return Task.FromResult(new CloudEnvironmentSetupResult { Id = operationId, ConfigurationId = id, ThreadId = "setup-thread", TurnId = "setup-turn", State = "running", Stage = "setup-running" });
                };
                cloud.SetupFollowFixture = (id, token) =>
                {
                    follows++;
                    assert(id == setupId, "suivi réutilise uniquement l’identifiant de préparation existant");
                    return Task.FromResult(new CloudEnvironmentSetupResult { Id = id, ConfigurationId = added, ThreadId = "setup-thread", TurnId = "setup-turn", State = publications == 0 ? "ready-to-publish" : "completed", Stage = publications == 0 ? "setup-completed" : "completed" });
                };
                cloud.SetupPublishFixture = (id, token) =>
                {
                    publications++;
                    assert(id == setupId, "publication cible la préparation existante sans recréer de chat");
                    return Task.FromResult(new CloudEnvironmentSetupResult { Id = id, ConfigurationId = added, ThreadId = "setup-thread", TurnId = "setup-turn", State = "publishing", Stage = "approval-pending" });
                };
                assert(!Menu("Modifier et publier…").IsEnabled && Button("Préparer l’environnement…").IsEnabled, "révision initiale propose préparation et protège la modification d’une version non publiée");
                await Dialog("Préparer l’environnement Codex Cloud", "Préparer l’environnement…", async form =>
                {
                    assert(!Descendants(form).OfType<TextBox>().Any() && Descendants(form).OfType<TextBlock>().Any(text => text.Text.Contains("fixture/project")), "préparation utilise le nom GitHub fourni par le catalogue sans saisie d’identifiant technique");
                    form.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => form.Saved);
                });
                await Until(() => Status().Text.Contains("Préparation en cours"));
                assert(preparations == 1 && publications == 0 && !Descendants(cloud).OfType<Button>().Any(button => Object.Equals(button.Content, "Publier l’environnement")), "démarrer une préparation ne publie pas automatiquement l’environnement");
                Invoke("Suivre la préparation");
                await Until(() => Descendants(cloud).OfType<Button>().Any(button => Object.Equals(button.Content, "Publier l’environnement")));
                assert(follows == 1 && publications == 0, "fin de préparation propose une publication explicitement séparée");
                Invoke("Publier l’environnement");
                await Until(() => Status().Text.Contains("Publication de l’environnement en cours"));
                Invoke("Suivre la préparation");
                await Until(() => Status().Text.Contains("publication vérifiée"));
                assert(preparations == 1 && publications == 1 && follows == 2, "suivi terminé ne rejoue ni chat ni prompt ni publication");
                published=true;await cloud.Reload(added);shell.UpdateLayout();
                string missionOwner=null,missionEnvironment=null;cloud.CreateMissionFixture=(instanceId,environmentId)=>{missionOwner=instanceId;missionEnvironment=environmentId;return Task.CompletedTask;};
                assert(Button("Créer une mission").IsEnabled&&!Descendants(cloud).OfType<Button>().Any(item=>Object.Equals(item.Content,"Renommer l’environnement")||Object.Equals(item.Content,"Modifier et publier…")),"environnement prêt : une action de mission, les réglages sont regroupés dans le menu");
                Invoke("Créer une mission");await Until(()=>missionOwner!=null);
                assert(missionOwner==owner.Id&&missionEnvironment==added,"créer une mission préserve exactement l’instance et l’environnement sélectionnés sans appel Cloud de mutation");
                int edits=0;
                cloud.UpdateFixture=(id,patch,operationId,token)=>
                {
                    edits++;assert(id==added&&operationId.Length==32&&patch.BaseVersionId=="11111111-2222-3333-4444-555555555555~cecfgver_fixture"&&patch.ExpectedRevision==0,"édition guidée fige le compte, l’environnement et sa version de départ");
                    assert(patch.Repositories.Single().Ref=="refs/heads/feature/guided-ui"&&patch.Repositories.Single().Depth==1&&patch.NetworkPolicy==null,"édition conserve la profondeur et la politique réseau tant qu’elles ne sont pas changées");
                    return Task.FromResult(new CloudEnvironmentUpdateResult{Id=operationId,ConfigurationId=id,State="completed",Stage="completed"});
                };
                await EnvironmentDialog("Modifier et publier…",async form=>
                {
                    await Until(()=>!form.Loading&&form.RepositoryRows.Count>0);
                    assert(form.Account.Items.Count==1&&!form.Account.IsEnabled&&form.EnvironmentName.IsReadOnly,"modification verrouille le compte propriétaire et garde le renommage séparé");
                    form.RepositoryRows.Single(row=>row.Id=="github/123").Ref="refs/heads/feature/guided-ui";
                    Next(form);await Until(()=>form.Step==2);Next(form);await Until(()=>form.Step==3);Next(form);await Until(()=>form.Step==4&&form.NextButton.IsEnabled);Next(form);await Until(()=>form.Saved);
                });
                assert(edits==1&&Status().Text.Contains("publication vérifiée"),"modification guidée envoie une seule intention à la simulation explicite sans contacter une API réelle");
                int localEditWrites=0;
                cloud.UpdateFixture=(id,patch,operationId,token)=>{localEditWrites++;throw new IOException("Échec local simulé après réponse de modification");};
                await EnvironmentDialog("Modifier et publier…",async form=>
                {
                    await Until(()=>!form.Loading&&form.RepositoryRows.Count>0);
                    Next(form);await Until(()=>form.Step==2);Next(form);await Until(()=>form.Step==3);form.Install.Text="python --version";
                    Next(form);await Until(()=>form.Step==4&&form.NextButton.IsEnabled);Next(form);await Until(()=>form.IntentLocked&&!form.Submitting);
                    assert(form.ErrorText.Text.Contains("incertain")&&!form.NextButton.IsEnabled,"erreur locale après modification conserve un résultat incertain dans l’assistant");
                    Next(form);assert(localEditWrites==1,"erreur locale après modification ne rejoue pas la demande");
                });
                assert(Button("Suivre la publication").IsEnabled&&!Menu("Modifier et publier…").IsEnabled,"modification localement incertaine garde son suivi et bloque une nouvelle modification avant rapprochement");
                cloud.SetFixtureUpdate(new CloudEnvironmentUpdateResult{Id="fixture-failed-update",ConfigurationId=added,State="failed",Stage="draft-creation",FailureCode="unexpected-html"});shell.UpdateLayout();
                assert(Button("Créer une mission").IsEnabled&&!Descendants(cloud).OfType<Button>().Any(item=>Object.Equals(item.Content,"Suivre la publication"))&&!cloud.ActionsMenu.Items.OfType<MenuItem>().Any(item=>Object.Equals(item.Header,"Suivre la publication")),"ancien échec terminal ne masque pas la version prête et ne propose plus un suivi inutile");
                assert(Descendants(cloud).OfType<Expander>().All(expander=>!expander.IsExpanded),"configuration, diagnostic et ancienne erreur restent repliés par défaut");
                double originalWidth=shell.Width,originalHeight=shell.Height;
                foreach(var size in new[]{new Size(1280,860),new Size(980,620)}){
                    shell.Width=size.Width;shell.Height=size.Height;shell.UpdateLayout();await Task.Delay(20);shell.UpdateLayout();
                    var primary=Button("Créer une mission");var refresh=Button("Actualiser le Cloud");
                    bool Within(FrameworkElement element){var point=element.TranslatePoint(new Point(),shell);return point.X>=0&&point.Y>=0&&point.X+element.ActualWidth<=shell.ActualWidth+1&&point.Y+element.ActualHeight<=shell.ActualHeight+1;}
                    assert(Within(primary)&&Within(refresh)&&cloud.ActualHeight>200,"actions Cloud visibles sans défilement imbriqué à "+size.Width+" × "+size.Height);
                    assert(size.Width==1280?cloud.EnvironmentList.IsVisible:cloud.EnvironmentChoice.IsVisible,"catalogue Cloud s’adapte à la largeur "+size.Width);
                    Capture(shell,Path.Combine(root,"Instance-cloud-"+size.Width+"x"+size.Height+".png"));
                }
                shell.Width=originalWidth;shell.Height=originalHeight;shell.UpdateLayout();
                instances.ShowTab(1);
                shell.UpdateLayout();
            }
        }
    }
}
