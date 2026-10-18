using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // External HTTP input never selects a native endpoint. Only the locally
    // configured, frozen route can select an account, workspace and chat.
    internal static class WebhookDispatch
    {
        internal static void RequireExecutionMode(WebhookRoute route,InstanceState state)
        {
            if(route.Access=="headless"&&(state==null||state.Running||state.Phase=="unknown"||state.Phase=="starting"||state.Phase=="stopping"))throw new InvalidOperationException("Cette entree autorise les envois sans fenetre. La lecture et les objectifs restent disponibles pendant l'ouverture de Codex ; aucun envoi n'est bascule vers sa fenetre.");
        }
        private static void VerifyBinding(RelayStore store,WebhookRoute route)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));
            var instance=NamedInstances.Resolve(accounts,route.InstanceId);
            if(instance==null||instance.AccountKey!=route.AccountKey||!RelayStore.SamePath(accounts.Instances.Home(instance),route.Home))throw new InvalidOperationException("L'association compte-instance de cette route a change. Creez une nouvelle demande apres verification de la route.");
            NamedInstances.VerifyAccount(accounts,instance);RelayStore.WorkspacePath(route.Workspace);
            if(route.Access=="headless"&&!instance.HeadlessEnabled)throw new InvalidOperationException("Le mode sans fenetre n'est pas autorise pour cette instance.");
            RequireExecutionMode(route,accounts.Instances.Runtime.Probe(instance));
        }
        private static async Task<RelayChannel> Connect(RelayStore store,WebhookRoute route,CancellationToken token)
        {
            VerifyBinding(store,route);var accounts=new AccountService(Path.GetDirectoryName(store.Root));
            var instance=NamedInstances.Resolve(accounts,route.InstanceId);
            // Execution retains the granted mode. Metadata reads through an
            // open desktop do not authorize sending a headless job to its GUI.
            return await NamedInstances.Connect(store,accounts,instance,route.Workspace,token,route.ThreadId,route.Access=="headless");
        }
        private static async Task EnsureEngine(RelayStore store,CancellationToken token)
        {
            RelayWorker.Ensure(store);
            for(int n=0;n<30;n++){
                token.ThrowIfCancellationRequested();var state=RelayWorker.Status(store);
                if(DesktopRuntime.SameProcess(state.Pid,state.Started)&&state.Version==RelayWorker.Version)return;
                if(RelayWorker.Paused(store))throw new InvalidOperationException("Les departs du relais sont suspendus dans le Switcher.");
                await Task.Delay(100,token);
            }
            throw new InvalidOperationException("Le moteur de relais termine sa mise a jour cooperative. La demande reste en attente.");
        }
        internal static Task Process(RelayStore store,WebhookJob job,CancellationToken token)
        {return Process(store,job,token,(route,ct)=>Connect(store,route,ct),route=>VerifyBinding(store,route),ct=>EnsureEngine(store,ct),null);}
        internal static async Task Process(RelayStore store,WebhookJob job,CancellationToken token,Func<WebhookRoute,CancellationToken,Task<RelayChannel>> connect,Action<WebhookRoute> verify,Func<CancellationToken,Task> ensure,IRelayTransport transport)
        {
            if(!Webhooks.Config(store).Enabled)return;
            // The durable relay ID is the webhook ID. This reconciles a crash
            // between local admission and receipt persistence without a resend.
            if(store.Exists(job.Id)){
                var existing=store.Message(job.Id);
                if(existing.WebhookJobId!=job.Id||!Matches(existing,job)){Block(store,job,"Un autre travail utilise cet identifiant. Aucun nouvel envoi effectue.");return;}
                Mirror(store,job,existing);return;
            }
            if(job.State=="monitoring"||!String.IsNullOrEmpty(job.RelayJobId)){job.State="uncertain";job.Error="Le recu natif de cette demande est introuvable. Aucun renvoi automatique.";Webhooks.SaveJob(store,job);return;}
            try{ValidateRoute(store,job);verify(job.RouteSnapshot);}catch(InvalidOperationException e){Block(store,job,e.Message);return;}
            PortalResolvedComposer resolved=null;
            if(job.Options!=null&&!job.Options.Empty){
                try{var route=job.RouteSnapshot;var grant=new PortalInstanceGrant{InstanceId=route.InstanceId,AccountKey=route.AccountKey,Home=route.Home,Workspace=route.Workspace,Access=route.Access,Model=route.Model,ReasoningEffort=route.ReasoningEffort,PermissionProfile=route.PermissionProfile};resolved=await PortalComposer.Resolve(store,job.RouteId,grant,job.Options,route.Workspace,token).ConfigureAwait(false);ValidateRoute(store,job);verify(job.RouteSnapshot);}
                catch(OperationCanceledException){throw;}catch(InvalidOperationException e){Block(store,job,WebhookChats.Safe(e.Message,1600));return;}
            }
            RelayChannel channel;
            try{
                await ensure(token);channel=await connect(job.RouteSnapshot,token);
            }catch(OperationCanceledException){throw;}
            catch(Exception){job.State="waiting";job.Error="Instance ou moteur indisponible. La demande reste en file ; verifiez l'ouverture et les permissions de l'instance.";Webhooks.SaveJob(store,job);return;}
            if(channel.AccountKey!=job.RouteSnapshot.AccountKey||!RelayStore.SamePath(channel.Home,job.RouteSnapshot.Home)||!RelayStore.SamePath(channel.Workspace,job.RouteSnapshot.Workspace)){Block(store,job,"Le canal natif ne correspond pas au compte et au dossier de cette route.");return;}
            try{ValidateRoute(store,job);verify(job.RouteSnapshot);}catch(InvalidOperationException e){Block(store,job,e.Message);return;}
            string text=(String.IsNullOrWhiteSpace(job.RouteSnapshot.PromptPrefix)?"":job.RouteSnapshot.PromptPrefix.Trim()+"\n\n")+(resolved==null||String.IsNullOrWhiteSpace(resolved.PromptPrefix)?"":resolved.PromptPrefix+"\n\n")+
                "Demande de l'application externe (route configuree : "+job.RouteSnapshot.Name+"). Respecte le dossier, les outils et les permissions autorises de cette instance.\n\n"+job.Prompt+
                (String.IsNullOrWhiteSpace(job.Context)?"":"\n\nContexte fourni par l'application externe :\n"+job.Context);
            if(text.Length>24000){Block(store,job,"Le prompt, le contexte et les references selectionnees depassent 24000 caracteres. Reduisez la demande.");return;}
            var spec=new RelayJobSpec{Id=job.Id,To=channel.Id,Title=job.Title,Prompt=text,Access="write",ExplicitDelegation=true};
            try{
                var admitted=await new DirectMessages(store,transport).Submit(spec,job.RouteSnapshot.ThreadId,token,m=>{
                    m.WebhookJobId=job.Id;m.WebhookRouteName=job.RouteSnapshot.Name;
                    m.RequestedModel=job.Options!=null&&job.Options.Model!=null?job.Options.Model:job.RouteSnapshot.Model;m.RequestedReasoningEffort=job.Options!=null&&job.Options.ReasoningEffort!=null?job.Options.ReasoningEffort:job.RouteSnapshot.ReasoningEffort;m.RequestedPermission=job.RouteSnapshot.PermissionProfile;
                    m.ComposerOptions=job.Options;m.ResolvedComposer=resolved;
                    m.RoutingReason="Webhook · route "+job.RouteSnapshot.Name;
                });
                Mirror(store,job,admitted);
            }catch(OperationCanceledException){throw;}
            catch(Exception){
                if(store.Exists(job.Id)){Mirror(store,job,store.Message(job.Id));return;}
                job.State="waiting";job.Error="Admission locale en attente. Verifiez la configuration du destinataire ; aucun envoi natif n'a ete effectue.";Webhooks.SaveJob(store,job);
            }
        }
        private static void Block(RelayStore store,WebhookJob job,string error){job.State="blocked";job.Error=error;Webhooks.SaveJob(store,job);}
        private static void ValidateRoute(RelayStore store,WebhookJob job)
        {
            var current=Webhooks.Route(store,job.RouteId);var frozen=job.RouteSnapshot;
            string expectedId=Webhooks.ExecutionRouteId(current.Id,job.PortalInstanceId,job.Options);
            if(frozen==null||expectedId!=frozen.Id||!current.Enabled||current.Revision!=frozen.Revision)throw new InvalidOperationException("Cette route a ete modifiee ou desactivee depuis la reception. Creez une nouvelle demande avec un nouvel identifiant apres verification.");
            if(!String.IsNullOrEmpty(job.PortalInstanceId))Portal.ValidateJob(store,job);
        }
        private static bool Matches(RelayMessage message,WebhookJob job)
        {
            var route=job.RouteSnapshot;string expectedModel=job.Options!=null&&job.Options.Model!=null?job.Options.Model:route==null?null:route.Model,expectedEffort=job.Options!=null&&job.Options.ReasoningEffort!=null?job.Options.ReasoningEffort:route==null?null:route.ReasoningEffort;
            return route!=null&&message.OperatorOrigin&&message.TargetAccountKey==route.AccountKey&&
                RelayStore.SamePath(message.TargetHome,route.Home)&&RelayStore.SamePath(message.TargetWorkspace??message.Workspace,route.Workspace)&&
                message.RequestedThreadId==route.ThreadId&&message.RequestedModel==expectedModel&&message.RequestedReasoningEffort==expectedEffort&&message.RequestedPermission==route.PermissionProfile&&Json.Write(message.ComposerOptions)==Json.Write(job.Options);
        }
        internal static void ValidateBeforeDispatch(RelayStore store,RelayMessage message)
        {
            if(String.IsNullOrEmpty(message.WebhookJobId))return;
            if(!Webhooks.Config(store).Enabled){message.BlockReason="paused";throw new InvalidOperationException("Les nouveaux departs webhook sont desactives.");}
            try{
                var job=Webhooks.Job(store,message.WebhookJobId);ValidateRoute(store,job);
                if(message.Id!=job.Id||!Matches(message,job))throw new InvalidOperationException("Le mandat natif ne correspond pas a la route webhook autorisee.");
                if(job.Options!=null&&!job.Options.Empty){var resolved=message.ResolvedComposer;if(resolved==null||resolved.RootRouteId!=job.RouteId||resolved.InstanceId!=job.RouteSnapshot.InstanceId||resolved.AccountKey!=job.RouteSnapshot.AccountKey||!RelayStore.SamePath(resolved.Home,job.RouteSnapshot.Home)||resolved.OptionsHash!=Webhooks.Hash(job.Options.Canonical()))throw new InvalidOperationException("Les options resolues ne correspondent pas au mandat accepte.");var route=job.RouteSnapshot;PortalAttachments.Require(store,job.RouteId,new PortalInstanceGrant{InstanceId=route.InstanceId,AccountKey=route.AccountKey,Home=route.Home,Workspace=route.Workspace},job.Options.Attachments);}
            }catch(InvalidOperationException){message.State="cancelled";message.BlockReason="webhook-route";throw;}
        }
        internal static void ApplyExecution(RelayMessage message,IDictionary<string,object> args)
        {
            if(String.IsNullOrEmpty(message.WebhookJobId)||!message.OperatorOrigin)return;
            if(!String.IsNullOrWhiteSpace(message.RequestedModel))args["model"]=message.RequestedModel;
            if(!String.IsNullOrWhiteSpace(message.RequestedReasoningEffort))args["thinking"]=message.RequestedReasoningEffort;
            if(message.ResolvedComposer!=null&&message.TargetProvider=="codex-headless")args["creezioComposer"]=message.ResolvedComposer;
        }
        private static void Mirror(RelayStore store,WebhookJob job,RelayMessage message)
        {
            job.RelayJobId=message.Id;job.ThreadId=message.TargetThreadId;job.Result=message.Result;job.Error=message.Error;
            job.State=message.State=="closed"?"cancelled":new[]{"completed","failed","cancelled","uncertain"}.Contains(message.State)?message.State:message.State=="attention"||new[]{"permissions","approval","user-input"}.Contains(message.BlockReason)?"attention":message.State=="queued"?"waiting":"monitoring";
            Webhooks.SaveJob(store,job);
        }
    }
}
