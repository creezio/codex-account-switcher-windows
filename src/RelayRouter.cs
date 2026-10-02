using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class RelayRouter
    {
        private readonly RelayStore store;
        private readonly IRelayTransport transport;
        private readonly Func<string,double?> quota;
        public RelayRouter(RelayStore data,IRelayTransport adapter=null,Func<string,double?> score=null){store=data;transport=adapter??new DesktopRelayTransport();quota=score??AccountQuota;}
        private double? AccountQuota(string account)
        {
            return RelayQuota.Read(store,account);
        }
        public static bool Active(RelayMessage m){return new[]{"queued","sending","waiting","uncertain","attention"}.Contains(m.State);}
        internal static bool Executing(RelayMessage m){return new[]{"sending","waiting","uncertain","attention"}.Contains(m.State);}
        private static void CheckAgent(RelayPolicy policy,RelayChannel channel,RelayJobSpec spec,bool automatic)
        {
            var a=policy.Agents.FirstOrDefault(x=>x.Channel==channel.Id);
            if(a==null){if(automatic||!String.IsNullOrWhiteSpace(spec.Capabilities))throw new InvalidOperationException("Agent non configuré pour ce routage.");return;}
            if(!a.Enabled||(automatic&&!a.AutoRoute)||!RelayPolicies.Allows(a.Tasks,spec.Kind)||!RelayPolicies.Allows(a.Projects,spec.Project??""))throw new InvalidOperationException("Le profil d'agent n'autorise pas cette tâche.");
            if(!RelayPolicies.Has(a.Capabilities,spec.Capabilities))throw new InvalidOperationException("Capacités requises absentes du profil d'agent.");
        }
        public static void ValidateDestination(RelayPolicy p,RelayChannel source,RelayChannel target,RelayJobSpec spec)
        {
            if(!source.Enabled||!target.Enabled||source.Id==target.Id)throw new InvalidOperationException("Source ou destination indisponible.");
            if(!RelayStore.SamePath(source.Workspace,target.Workspace))throw new InvalidOperationException("Connectez les canaux au même dossier de projet, ou à un worktree distinct dédié à cette tâche.");
            CheckAgent(p,target,spec,String.IsNullOrWhiteSpace(spec.To));
            var project=p.Projects.FirstOrDefault(x=>x.Id==spec.Project);
            if(!String.IsNullOrEmpty(spec.Project)&&project==null)throw new InvalidOperationException("Projet non configuré.");
            if(project!=null){if(!RelayStore.SamePath(project.Workspace,source.Workspace)||!RelayPolicies.Allows(project.SourceChannels,source.Id)||!RelayPolicies.Allows(project.TargetChannels,target.Id))throw new InvalidOperationException("Canal ou dossier non autorisé pour ce projet.");}
            if(!String.IsNullOrEmpty(spec.Resource)){
                var resource=p.Resources.FirstOrDefault(x=>x.Id==spec.Resource&&x.Project==spec.Project);
                if(resource==null||!RelayPolicies.Allows(resource.Channels,target.Id))throw new InvalidOperationException("Ce canal n'est pas autorisé pour la ressource.");
                var a=p.Agents.FirstOrDefault(x=>x.Channel==target.Id);
                if(a==null||!RelayPolicies.Has(a.Capabilities,resource.Capabilities))throw new InvalidOperationException("Capacités de ressource non déclarées pour cet agent.");
            }
        }
        public object Describe(string sourceId)
        {
            var source=store.Channel(sourceId);var p=RelayPolicies.Load(store);
            var projects=p.Projects.Where(x=>RelayStore.SamePath(x.Workspace,source.Workspace)&&RelayPolicies.Allows(x.SourceChannels,sourceId)).ToArray();
            return new {version=1,source=source.Id,workspace=source.Workspace,
                projects=projects,
                agents=store.Channels().Where(c=>c.Enabled&&c.Id!=sourceId&&RelayStore.SamePath(c.Workspace,source.Workspace)).Select(c=>new {channel=c.Id,name=c.Name,profile=p.Agents.FirstOrDefault(a=>a.Channel==c.Id),online=DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks),remaining=quota(c.AccountKey),capabilityEvidence="user-declared; verify tools and resource access in destination chat"}).ToArray(),
                resources=p.Resources.Where(x=>projects.Any(project=>project.Id==x.Project)).ToArray(),
                rules=p.Rules.Where(x=>x.Enabled&&RelayPolicies.Allows(x.Sources,sourceId)&&(x.Project=="*"||projects.Any(project=>project.Id==x.Project))).ToArray()};
        }
        public async Task<RelayMessage> Submit(RelayJobSpec spec,string sourceThread,CancellationToken token)
        {
            if(spec==null)throw new InvalidOperationException("Demande manquante.");
            if(spec.DependsOn==null)spec.DependsOn=new List<string>();if(spec.Files==null)spec.Files=new Dictionary<string,string>();
            if(String.IsNullOrWhiteSpace(spec.Kind))spec.Kind="general";RelayStore.ChannelId(spec.Kind);
            if(!new[]{"read","write","external"}.Contains(spec.Access))throw new InvalidOperationException("Access doit être read, write ou external.");
            if(spec.Id==null)spec.Id=Guid.NewGuid().ToString("N");RelayStore.MessageId(spec.Id);
            string hash=RelayPolicies.Fingerprint(spec);
            var source=store.Channel(spec.From);transport.Verify(source);
            var snapshot=await transport.Call(source,"read_thread",new {threadId=sourceThread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token);
            if(Json.Str(Json.Get(Json.Get(snapshot,"thread"),"id"))!=sourceThread)throw new InvalidOperationException("Conversation source non reconnue.");
            using(store.Lease("submission")){
                if(store.Exists(spec.Id)){var old=store.Message(spec.Id);if(old.SourceThreadId!=sourceThread||old.SubmissionHash!=hash)throw new InvalidOperationException("Cette clé de soumission existe avec un contenu différent.");return old;}
                var policy=RelayPolicies.Load(store);var project=policy.Projects.FirstOrDefault(x=>x.Id==spec.Project);
                var rules=policy.Rules.Where(r=>r.Enabled&&RelayPolicies.Allows(r.Project,spec.Project??"")&&RelayPolicies.Allows(r.Task,spec.Kind)&&RelayPolicies.Allows(r.Sources,source.Id)).OrderByDescending(r=>r.Priority).ThenBy(r=>r.Id,StringComparer.Ordinal).ToList();
                if(!spec.ExplicitDelegation&&(project==null||project.Delegation!="rules"||rules.Count==0))throw new InvalidOperationException("Ce projet exige une demande explicite de délégation.");
                var messages=store.Messages();RelayMessage parent=null;
                if(!String.IsNullOrEmpty(spec.Parent)){
                    parent=store.Message(spec.Parent);
                    if(parent.TargetChannelId!=source.Id||parent.TargetThreadId!=sourceThread||!Active(parent)||parent.Job==null||parent.Job.Project!=spec.Project)throw new InvalidOperationException("Tâche parente ou conversation non autorisée.");
                    if(parent.Depth>=(project==null?3:project.MaxDepth))throw new InvalidOperationException("Profondeur de délégation atteinte.");
                }
                foreach(string dep in spec.DependsOn){var d=store.Message(dep);if(d.Id==spec.Id||d.SourceThreadId!=sourceThread||d.SourceChannelId!=source.Id||!RelayStore.SamePath(d.Workspace,source.Workspace)||(d.Job==null?null:d.Job.Project)!=spec.Project)throw new InvalidOperationException("Dépendance hors de cette conversation ou de ce projet.");}
                string group=parent==null?spec.Id:(parent.RootJobId??parent.Id);
                if(messages.Count(m=>(m.RootJobId??m.Id)==group)>=(project==null?32:project.MaxJobs))throw new InvalidOperationException("Nombre maximal de tâches du groupe atteint.");
                var candidates=new List<Tuple<RelayChannel,double,int,string>>();var rejected=new List<string>();
                foreach(var target in store.Channels().Where(c=>c.Id!=source.Id&&(String.IsNullOrEmpty(spec.To)||c.Id==spec.To))){
                    try{
                        ValidateDestination(policy,source,target,spec);
                        var rule=rules.FirstOrDefault(r=>RelayPolicies.Allows(r.Targets,target.Id)&&RelayPolicies.Has(policy.Agents.FirstOrDefault(a=>a.Channel==target.Id)==null?"":policy.Agents.First(a=>a.Channel==target.Id).Capabilities,r.Capabilities));
                        if((String.IsNullOrEmpty(spec.To)||!spec.ExplicitDelegation)&&rules.Count>0&&rule==null)throw new InvalidOperationException("Exclu par les règles correspondantes.");
                        var profile=policy.Agents.FirstOrDefault(x=>x.Channel==target.Id);double? remaining=quota(target.AccountKey);
                        bool quotaBlocked=profile!=null&&((!remaining.HasValue&&!profile.AllowUnknownQuota)||(remaining.HasValue&&remaining.Value<=profile.MinRemaining));
                        int load=messages.Count(m=>m.TargetChannelId==target.Id&&Active(m));
                        bool online=true;try{transport.Verify(target);}catch{online=false;}
                        double score=(quotaBlocked?-10000000:0)+(online?100000:0)+(rule==null?0:rule.Priority*1000)+(remaining??0)-load*100;
                        candidates.Add(Tuple.Create(target,score,load,(String.IsNullOrEmpty(spec.To)?"Routage configuré":"Destinataire explicite")+(rule==null?"":" · règle "+rule.Id)+" · "+(quotaBlocked?"attente de quota":online?"instance disponible":"attente de reconnexion")));
                    }catch(InvalidOperationException e){rejected.Add(target.Id+": "+e.Message);}
                }
                var chosen=candidates.OrderByDescending(x=>x.Item2).ThenBy(x=>x.Item1.Id,StringComparer.Ordinal).FirstOrDefault();
                if(chosen==null)throw new InvalidOperationException("Aucun agent compatible. "+String.Join(" ; ",rejected.Take(8)));
                RelayPolicies.VerifyFiles(source.Workspace,spec.Files);
                return store.Enqueue(source.Id,sourceThread,chosen.Item1.Id,spec.Title,spec.Prompt,spec.Revision,spec.ReturnToSource,spec.ReplyTo,spec.Id,m=>{
                    m.SchemaVersion=2;m.Job=spec;m.SubmissionHash=hash;m.RootJobId=group;m.Depth=parent==null?0:parent.Depth+1;m.OriginVerified=true;m.RoutingReason=chosen.Item4;m.TargetWorkspace=chosen.Item1.Workspace;m.DeadlineUtc=DateTime.UtcNow.AddMinutes(project==null?120:project.MaxMinutes).ToString("o");});
            }
        }
    }
}
