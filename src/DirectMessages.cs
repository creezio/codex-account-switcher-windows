using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class ConversationChoice
    {
        public string Id {get;set;}
        public string Title {get;set;}
        public override string ToString(){return Title+" · "+Id;}
    }
    internal sealed class DirectMessages
    {
        private readonly RelayStore store;
        private readonly IRelayTransport transport;
        public DirectMessages(RelayStore data,IRelayTransport adapter=null){store=data;transport=adapter??AgentProviders.Create(data);}
        internal static void RequireThread(object snapshot,string id)
        {
            if(String.IsNullOrWhiteSpace(id)||Json.Str(Json.Get(Json.Get(snapshot,"thread"),"id"))!=id)throw new InvalidOperationException("Conversation non reconnue par cette instance.");
        }
        internal static bool Busy(object snapshot)
        {
            string status=Json.Str(Json.Get(Json.Get(Json.Get(snapshot,"thread"),"status"),"type"));
            return new[]{"active","running","inProgress"}.Contains(status)||RelayEngine.AttentionFlag(snapshot)!=null||RelayEngine.Rows(Json.Get(snapshot,"turns")).Any(t=>new[]{"inProgress","running"}.Contains(Json.Str(Json.Get(t,"status"))));
        }
        public async Task<ConversationChoice[]> Conversations(string channelId,CancellationToken token)
        {
            var channel=store.Channel(channelId);await Task.Run(()=>transport.Verify(channel),token);
            var listed=await transport.Call(channel,"list_threads",new{limit=100},token);
            return RelayEngine.Rows(Json.Get(listed,"pinnedThreads")).Concat(RelayEngine.Rows(Json.Get(listed,"threads")))
                .Where(t=>String.IsNullOrEmpty(Json.Str(Json.Get(t,"source")))||Json.Str(Json.Get(t,"source"))=="codex")
                .Select(t=>new ConversationChoice{Id=String.IsNullOrEmpty(Json.Str(Json.Get(t,"threadId")))?Json.Str(Json.Get(t,"id")):Json.Str(Json.Get(t,"threadId")),Title=Json.Str(Json.Get(t,"title"))})
                .Where(t=>!String.IsNullOrEmpty(t.Id)).GroupBy(t=>t.Id).Select(g=>g.First()).ToArray();
        }
        public async Task<RelayMessage> Submit(RelayJobSpec request,string threadId,CancellationToken token)
        {
            if(request==null||String.IsNullOrWhiteSpace(request.To))throw new InvalidOperationException("Choisissez un destinataire pour le message utilisateur.");
            // Never mutate the caller's object or accept an agent-supplied origin flag.
            var spec=Json.Read<RelayJobSpec>(Json.Write(request));var target=store.Channel(spec.To);
            spec.From=target.Id;spec.ExplicitDelegation=true;spec.ReturnToSource=false;spec.Parent=null;spec.ReplyTo=null;
            spec.DependsOn=new List<string>();spec.Files=spec.Files??new Dictionary<string,string>();
            spec.Id=spec.Id??Guid.NewGuid().ToString("N");RelayStore.MessageId(spec.Id);
            spec.Kind=String.IsNullOrWhiteSpace(spec.Kind)?"general":spec.Kind;RelayStore.ChannelId(spec.Kind);
            if(!new[]{"read","write","external"}.Contains(spec.Access))throw new InvalidOperationException("Action autorisée invalide.");
            threadId=String.IsNullOrWhiteSpace(threadId)?null:threadId.Trim();
            if(threadId!=null&&(threadId.Length>200||threadId.Any(Char.IsControl)))throw new InvalidOperationException("Identifiant de conversation invalide.");
            string hash=RelayPolicies.Fingerprint(spec);
            // An idempotent retry must remain readable even when the destination is offline.
            using(store.Lease("submission"))if(store.Exists(spec.Id))return Existing(spec,threadId,hash);
            await Task.Run(()=>transport.Verify(target),token);
            if(threadId!=null)RequireThread(await transport.Call(target,"read_thread",new{threadId=threadId,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token),threadId);
            var policy=RelayPolicies.Load(store);RelayRouter.ValidateDestination(policy,target,target,spec,true);RelayPolicies.VerifyFiles(target.Workspace,spec.Files);
            using(store.Lease("submission")){
                if(store.Exists(spec.Id))return Existing(spec,threadId,hash);
                return store.Enqueue(target.Id,null,target.Id,spec.Title,spec.Prompt,spec.Revision,false,null,spec.Id,m=>{
                    m.SchemaVersion=4;m.Job=spec;m.OriginVerified=true;m.SubmissionHash=hash;m.RootJobId=m.Id;
                    m.RequestedThreadId=threadId;m.TargetThreadId=threadId;m.NewConversation=threadId==null;
                    m.TargetWorkspace=target.Workspace;m.PolicyRevision=policy.Revision;m.ReturnMode="manual";
                    AgentProviders.Pin(m,target);
                    m.RoutingReason="Message utilisateur · destinataire explicite";m.DeadlineUtc=DateTime.UtcNow.AddHours(2).ToString("o");
                },true);
            }
        }
        private RelayMessage Existing(RelayJobSpec spec,string thread,string hash)
        {
            var old=store.Message(spec.Id);
            if(!old.OperatorOrigin||old.RequestedThreadId!=thread||old.Job==null||(old.SubmissionHash!=hash&&RelayPolicies.Fingerprint(old.Job)!=hash))throw new InvalidOperationException("Cet identifiant existe avec un contenu différent.");
            return old;
        }
    }
}
