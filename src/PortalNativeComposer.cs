using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    // Version-sensitive private desktop coordination protocol. Routing is never
    // chosen by remote input: a verified native receipt, unique local GUID and
    // the selected desktop PID/account/home must all agree before a rich turn.
    internal static class PortalNativeComposer
    {
        internal static bool RequiresNative(RelayMessage message,RelayChannel target)
        {return message.ComposerOptions!=null&&AgentProviders.Kind(target)=="codex"&&(message.ComposerOptions.Attachments.Count>0||message.ComposerOptions.Plugins.Count>0||message.ComposerOptions.Skills.Count>0||message.ComposerOptions.CollaborationMode!=null||RelayPermissions.IsExplicitProfile(message.ComposerOptions.PermissionProfile));}
        internal static string ExplicitPermission(RelayMessage message)
        {
            string profile=message.ComposerOptions==null?null:message.ComposerOptions.PermissionProfile;if(!RelayPermissions.IsExplicitProfile(profile))return null;
            if(!message.OperatorOrigin||String.IsNullOrEmpty(message.WebhookJobId)||message.ResolvedComposer==null||message.ResolvedComposer.PermissionProfile!=profile)throw new InvalidOperationException("La selection explicite des permissions n'est pas liee a cette demande verifiee.");return profile;
        }
        internal static string InheritedHeadlessPermission(RelayStore store,RelayChannel target,RelayMessage message)
        {if(!message.OperatorOrigin||String.IsNullOrEmpty(message.WebhookJobId)||String.IsNullOrEmpty(message.TargetThreadId)||AgentProviders.Kind(target)!="codex-headless")return null;return HeadlessRelayTransport.RequireOwned(store,target,message.TargetThreadId).PermissionProfile;}
        internal static RelayChannel PreflightChannel(RelayChannel target,RelayMessage message)
        {
            if(AgentProviders.Kind(target)!="codex"||ExplicitPermission(message)==null||!message.NewConversation||message.DispatchPhase!="preflight"||!String.IsNullOrEmpty(message.TargetThreadId))throw new InvalidOperationException("Preparation native explicite non confirmee.");
            // Only the no-tools creation preflight may bypass our legacy full
            // default. Preserve all native identity fields and gateway arguments.
            var copy=Json.Read<RelayChannel>(Json.Write(target));copy.RequireFullAccess=false;return copy;
        }
        internal static object Packet(string thread,string text,string messageId,PortalResolvedComposer resolved,string fallbackModel,string fallbackEffort,string workspace=null)
        {
            Guid parsed;if(!Guid.TryParse(thread,out parsed))throw new InvalidOperationException("Discussion native invalide.");var input=new List<object>{new{type="text",text=text,text_elements=new object[0]}};input.AddRange(resolved.InputItems??new object[0]);
            var request=new Dictionary<string,object>{{"threadId",thread},{"input",input.ToArray()},{"clientUserMessageId",messageId}};string model=resolved.Model??fallbackModel,effort=resolved.ReasoningEffort??fallbackEffort;if(!String.IsNullOrEmpty(model))request["model"]=model;if(!String.IsNullOrEmpty(effort))request["effort"]=effort;var mode=PortalComposer.Collaboration(resolved,fallbackModel,fallbackEffort);if(mode!=null)request["collaborationMode"]=mode;
            foreach(var setting in RelayPermissions.NativeProfile(resolved.PermissionProfile,workspace))request[setting.Key]=setting.Value;
            // Inherit supplies no overrides. Only an explicit bounded user
            // selection changes this chat; no global policy or approval decision.
            return new{conversationId=thread,turnStart=new{request=request,context=new Dictionary<string,object>()}};
        }
        private static bool HasRollout(string home,string thread)
        {
            long ms=Convert.ToInt64(thread.Replace("-","").Substring(0,12),16);var day=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ms);
            foreach(int shift in new[]{0,-1,1}){var date=day.AddDays(shift);string folder=Path.Combine(home,"sessions",date.ToString("yyyy",System.Globalization.CultureInfo.InvariantCulture),date.ToString("MM",System.Globalization.CultureInfo.InvariantCulture),date.ToString("dd",System.Globalization.CultureInfo.InvariantCulture));SafeFiles.RejectLinks(folder);if(Directory.Exists(folder)&&Directory.GetFiles(folder,"rollout-*"+thread+".jsonl",SearchOption.TopDirectoryOnly).Length>0)return true;}return false;
        }
        private static string VerifyUnique(AccountService accounts,DesktopInstance selected,string thread)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(thread??"",@"\A[a-f0-9]{8}-[a-f0-9]{4}-7[a-f0-9]{3}-[a-f0-9]{4}-[a-f0-9]{12}\z"))throw new InvalidOperationException("Ce format de discussion ne permet pas de verifier son profil natif.");
            var matches=accounts.Data.Instances.Where(i=>HasRollout(accounts.Instances.Home(i),thread)).ToArray();if(matches.Length!=1||matches[0].Id!=selected.Id)throw new InvalidOperationException("Cette discussion est absente ou son identifiant existe dans plusieurs profils Codex. Envoi natif enrichi refuse pour eviter une confusion de compte.");return WebhookChats.NativeWorkspace(accounts.Instances.Home(selected),thread);
        }
        internal static void RequireWorkspace(string expected,string actual)
        {if(String.IsNullOrWhiteSpace(expected)||String.IsNullOrWhiteSpace(actual)||!RelayStore.SamePath(expected,actual))throw new InvalidOperationException("Le dossier natif de la discussion a change. Aucun message enrichi envoye.");}
        internal static string ExpectedWorkspace(RelayMessage message,WebhookJob job,WebhookJob root=null,RelayMessage creation=null)
        {
            if(message.NewConversation)return message.NativeComposerWorkspace;
            if(!System.Text.RegularExpressions.Regex.IsMatch(job.PortalChatId??"",@"\A[a-f0-9]{32}\z"))return job.RouteSnapshot.Workspace;
            if(root==null||creation==null||root.Id!=job.PortalChatId||root.RouteId!=job.RouteId||root.PortalInstanceId!=job.PortalInstanceId||!String.IsNullOrEmpty(root.PortalChatId)||!String.IsNullOrEmpty(root.ConversationId)||root.ThreadId!=message.TargetThreadId||creation.Id!=root.Id||creation.WebhookJobId!=root.Id||!creation.OperatorOrigin||!creation.NewConversation||creation.TargetThreadId!=root.ThreadId||creation.TargetAccountKey!=message.TargetAccountKey||creation.TargetAccountKey!=job.RouteSnapshot.AccountKey||!RelayStore.SamePath(creation.TargetHome,message.TargetHome)||!RelayStore.SamePath(creation.TargetHome,job.RouteSnapshot.Home)||String.IsNullOrWhiteSpace(creation.NativeComposerWorkspace)||root.RouteSnapshot==null||!RelayStore.SamePath(root.RouteSnapshot.Workspace,job.RouteSnapshot.Workspace))throw new InvalidOperationException("Le recu du dossier natif de la discussion creee est absent ou incoherent.");
            return creation.NativeComposerWorkspace;
        }
        private static string NativeWorkspace(RelayStore store,RelayMessage message)
        {var job=Webhooks.Job(store,message.WebhookJobId);if(!message.NewConversation&&System.Text.RegularExpressions.Regex.IsMatch(job.PortalChatId??"",@"\A[a-f0-9]{32}\z")){var root=Webhooks.Job(store,job.PortalChatId);return ExpectedWorkspace(message,job,root,store.Message(root.Id));}return ExpectedWorkspace(message,job);}
        internal static void PinWorkspace(RelayStore store,RelayChannel channel,RelayMessage message)
        {if(RequiresNative(message,channel))VerifyTarget(store,channel,message,true);}
        private static InstanceState VerifyTarget(RelayStore store,RelayChannel channel,RelayMessage message,bool allowPin=false)
        {
            WebhookDispatch.ValidateBeforeDispatch(store,message);var accounts=new AccountService(Path.GetDirectoryName(store.Root));var job=Webhooks.Job(store,message.WebhookJobId);var instance=NamedInstances.Resolve(accounts,job.RouteSnapshot.InstanceId);NamedInstances.VerifyAccount(accounts,instance);
            if(instance.AccountKey!=channel.AccountKey||instance.AccountKey!=message.TargetAccountKey||!RelayStore.SamePath(accounts.Instances.Home(instance),channel.Home)||!RelayStore.SamePath(channel.Home,message.TargetHome))throw new InvalidOperationException("Le compte ou le profil cible a change.");var state=accounts.Instances.Runtime.Probe(instance);
            if(!state.Running||state.DesktopPid!=channel.ServerPid||state.DesktopStartTicks!=channel.ServerStartTicks||!DesktopRuntime.SameProcess(state.DesktopPid,state.DesktopStartTicks))throw new InvalidOperationException("L'instance native a change depuis la preparation du message.");
            string actualWorkspace=VerifyUnique(accounts,instance,message.TargetThreadId);if(message.NewConversation){var receipt=store.Message(message.Id);if(!receipt.OperatorOrigin||receipt.WebhookJobId!=job.Id||receipt.TargetThreadId!=message.TargetThreadId||!receipt.NewConversation)throw new InvalidOperationException("Le recu natif de creation est absent.");
                // A projectless native chat chooses its own cwd. Pin that owned
                // session_meta only after our creation preflight, then reject moves.
                if(String.IsNullOrWhiteSpace(message.NativeComposerWorkspace)&&allowPin&&message.DispatchPhase=="preflight"){message.NativeComposerWorkspace=actualWorkspace;store.Save(message);}RequireWorkspace(message.NativeComposerWorkspace,actualWorkspace);
            }else RequireWorkspace(NativeWorkspace(store,message),actualWorkspace);
            return state;
        }
        internal static async Task<object> Send(RelayStore store,RelayChannel channel,RelayMessage message,string text,CancellationToken token)
        {
            if(message.ResolvedComposer==null)throw new InvalidOperationException("Les options du composeur ne sont pas resolues.");VerifyTarget(store,channel,message);var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=NamedInstances.Resolve(accounts,channel.InstanceId);string pipe;
            using(var client=NamedInstances.OpenDesktop(store,accounts,instance,token,out pipe)){
                var snapshot=await client.Call(message.TargetThreadId,"read_thread",new{threadId=message.TargetThreadId,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token).ConfigureAwait(false);DirectMessages.RequireThread(snapshot,message.TargetThreadId);RequireWorkspace(NativeWorkspace(store,message),Json.Str(Json.Get(Json.Get(snapshot,"thread"),"cwd")));if(DirectMessages.Busy(snapshot))throw new InvalidOperationException("La discussion native est occupee. Aucun message enrichi envoye.");
            }
            using(var ipc=new Client(accounts)){
                await ipc.Initialize(token).ConfigureAwait(false);string owner=await ipc.Owner(message.TargetThreadId,null,token).ConfigureAwait(false);string confirmed=await ipc.Owner(message.TargetThreadId,owner,token).ConfigureAwait(false);if(owner!=confirmed)throw new InvalidOperationException("Le proprietaire natif de la discussion a change.");
                VerifyTarget(store,channel,message);string workspace=NativeWorkspace(store,message);
                string permission=ExplicitPermission(message);if(permission!=null){var changed=await ipc.Request("thread-follower-update-thread-settings",new{conversationId=message.TargetThreadId,threadSettings=RelayPermissions.NativeProfile(permission,workspace)},owner,2,token).ConfigureAwait(false);if(!Object.Equals(Json.Get(changed,"applied"),true))throw new InvalidOperationException("Codex n'a pas confirme les permissions demandees pour cette discussion. Aucun mandat transmis.");VerifyTarget(store,channel,message);}
                var result=await ipc.Request("thread-follower-start-turn",Packet(message.TargetThreadId,text,message.Id,message.ResolvedComposer,message.RequestedModel,message.RequestedReasoningEffort,workspace),owner,2,token).ConfigureAwait(false);
                string turn=Json.Str(Json.Get(Json.Get(Json.Get(result,"result"),"turn"),"id"));if(String.IsNullOrEmpty(turn))throw new InvalidOperationException("Le proprietaire natif n'a pas retourne de recu de tour. Aucun renvoi automatique.");return Json.Read<object>(Json.Write(new{threadId=message.TargetThreadId,turnId=turn}));
            }
        }
        private sealed class Client : IDisposable
        {
            [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
            private readonly NamedPipeClientStream stream;private string client="initializing-client";
            internal Client(AccountService accounts){stream=new NamedPipeClientStream(".","codex-ipc",PipeDirection.InOut,PipeOptions.Asynchronous);try{stream.Connect(2000);uint pid;if(!GetNamedPipeServerProcessId(stream.SafePipeHandle,out pid))throw new IOException("Identite du routeur IPC indisponible.");using(var process=Process.GetProcessById((int)pid)){long started=process.StartTime.ToUniversalTime().Ticks;if(!accounts.Data.Instances.Select(i=>accounts.Instances.Runtime.Probe(i)).Any(s=>s.Running&&s.DesktopPid==(int)pid&&s.DesktopStartTicks==started))throw new InvalidOperationException("Le routeur IPC n'appartient a aucune instance Codex connue.");string file=Path.GetFileName(process.MainModule.FileName);if(!new[]{"ChatGPT.exe","Codex.exe"}.Contains(file,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Routeur IPC natif non reconnu.");}}catch{stream.Dispose();throw;}}
            private async Task Exact(byte[] bytes,CancellationToken token){int at=0;while(at<bytes.Length){int got=await stream.ReadAsync(bytes,at,bytes.Length-at,token).ConfigureAwait(false);if(got==0)throw new EndOfStreamException();at+=got;}}
            private async Task Write(object value,CancellationToken token){byte[] bytes=Encoding.UTF8.GetBytes(Json.Write(value));if(bytes.Length>4000000)throw new IOException("Message natif trop volumineux.");byte[] header=BitConverter.GetBytes(bytes.Length);await stream.WriteAsync(header,0,header.Length,token).ConfigureAwait(false);await stream.WriteAsync(bytes,0,bytes.Length,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);}
            internal async Task<object> Request(string method,object args,string target,int version,CancellationToken token){string id=Guid.NewGuid().ToString("D");using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){deadline.CancelAfter(TimeSpan.FromSeconds(15));using(deadline.Token.Register(()=>stream.Dispose())){var request=new Dictionary<string,object>{{"type","request"},{"requestId",id},{"sourceClientId",client},{"version",version},{"method",method},{"params",args},{"timeoutMs",10000}};if(target!=null)request["targetClientId"]=target;await Write(request,deadline.Token).ConfigureAwait(false);for(int frame=0;frame<128;frame++){byte[] header=new byte[4];await Exact(header,deadline.Token).ConfigureAwait(false);int count=BitConverter.ToInt32(header,0);if(count<=0||count>8000000)throw new IOException("Trame IPC native invalide.");byte[] bytes=new byte[count];await Exact(bytes,deadline.Token).ConfigureAwait(false);var reply=Json.Read<object>(Encoding.UTF8.GetString(bytes));string type=Json.Str(Json.Get(reply,"type"));if(type=="client-discovery-request"){await Write(new{type="client-discovery-response",requestId=Json.Get(reply,"requestId"),response=new{canHandle=false}},deadline.Token).ConfigureAwait(false);continue;}if(type!="response"||Json.Str(Json.Get(reply,"requestId"))!=id)continue;if(Json.Str(Json.Get(reply,"resultType"))!="success")throw new InvalidOperationException("Le proprietaire natif a refuse cette demande enrichie. Aucun renvoi automatique.");if(target!=null&&Json.Str(Json.Get(reply,"handledByClientId"))!=target)throw new InvalidOperationException("Le client natif destinataire a change.");lastHandled=Json.Str(Json.Get(reply,"handledByClientId"));return Json.Get(reply,"result");}throw new IOException("Trop de trames IPC sans reponse ciblee.");}}}
            internal async Task Initialize(CancellationToken token){var response=await Request("initialize",new{clientType="desktop"},null,0,token).ConfigureAwait(false);client=Json.Str(Json.Get(response,"clientId"));if(client.Length==0)throw new IOException("Initialisation IPC absente.");}
            internal async Task<string> Owner(string thread,string expected,CancellationToken token){var result=await Request("thread-owner-discovery",new{hostId="local",conversationId=thread},expected,1,token).ConfigureAwait(false);if(!Object.Equals(Json.Get(result,"supportsUntrustedAppInput"),true))throw new InvalidOperationException("Version du proprietaire natif incompatible.");
                // Discovery's client ID is carried in the envelope. Obtain it
                // through the dedicated response helper rather than guessing.
                string owner=expected??lastHandled;Guid parsed;if(!Guid.TryParse(owner,out parsed))throw new IOException("Identifiant du proprietaire natif absent.");return owner;}
            private string lastHandled;
            public void Dispose(){stream.Dispose();}
        }
    }
}
