using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // Human names are aliases. Persisted routing remains pinned to account, home and workspace.
    internal static class NamedInstances
    {
        internal static string ChannelId(string home,string workspace)
        {return "instance-"+RelayReturns.Key(Path.GetFullPath(home).TrimEnd('\\').ToUpperInvariant()+"|"+Path.GetFullPath(workspace).TrimEnd('\\').ToUpperInvariant());}
        internal static DesktopInstance Resolve(AccountService accounts,string name)
        {
            name=(name??"").Trim();
            var matches=accounts.Data.Instances.Where(i=>!i.Archived&&(i.Id==name||String.Equals(i.Name,name,StringComparison.OrdinalIgnoreCase))).ToArray();
            if(matches.Length!=1)throw new InvalidOperationException(matches.Length==0?"Instance introuvable. Consultez list_instances pour obtenir les noms disponibles.":"Plusieurs instances portent ce nom. Utilisez leur identifiant ou renommez-les dans le switcher.");
            return matches[0];
        }
        internal static void VerifyAccount(AccountService accounts,DesktopInstance instance)
        {
            if(String.IsNullOrEmpty(instance.AccountKey))throw new InvalidOperationException("Associez un compte à « "+instance.Name+" » dans Instances.");
            if(accounts.Instances.ActiveKey(instance)!=instance.AccountKey)throw new InvalidOperationException("Le compte connecté dans « "+instance.Name+" » ne correspond pas à son compte permanent. Reconnectez son compte d'origine dans Codex.");
        }
        internal static object[] Describe(RelayStore store)
        {
            var a=new AccountService(Path.GetDirectoryName(store.Root));
            return a.Data.Instances.Where(i=>!i.Archived).Select(i=>{
                string issue=null;try{VerifyAccount(a,i);}catch(Exception e){issue=Program.SafeError(e);}
                bool online=i.IsLocal?store.Channels().Any(c=>RelayStore.SamePath(c.Home,a.Instances.Home(i))&&DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks)):a.Instances.Runtime.Probe(i).Running;
                return (object)new{id=i.Id,name=i.Name,account=a.Data.Profiles.Where(p=>p.Key==i.AccountKey).Select(p=>p.Label).FirstOrDefault(),online=online,issue=issue,integration=RelayIntegration.Status(store,a.Instances.Home(i)).Status};
            }).ToArray();
        }
        internal static async Task<RelayChannel> BindSource(RelayStore store,CancellationToken token)
        {
            var a=new AccountService(Path.GetDirectoryName(store.Root));string home=CodexEnvironment.DefaultHome();
            var instance=a.Data.Instances.SingleOrDefault(i=>!i.Archived&&RelayStore.SamePath(a.Instances.Home(i),home));
            if(instance==null)throw new InvalidOperationException("Ce profil n'est pas une instance du switcher. Ajoutez-le dans Instances.");
            // This runs inside the actual source chat, never with another chat's environment.
            string workspace=RelayStore.WorkspacePath(Environment.CurrentDirectory);
            if(!instance.AccountLocked)a.Instances.Capture(instance);
            VerifyAccount(a,instance);
            string id=ChannelId(home,workspace);var existing=store.Channels().FirstOrDefault(c=>c.Id==id);
            if(existing!=null&&!existing.Enabled)throw new InvalidOperationException("Cette connexion a été désactivée dans les réglages avancés.");
            return await RelayCommand.Register(store,Json.Read<object>(Json.Write(new{channel=id,name=instance.Name,workspace=workspace,requireFullAccess=existing!=null&&existing.RequireFullAccess})),token);
        }
        private static string[] Anchors(string home)
        {
            string index=Path.Combine(home,"session_index.jsonl");
            if(!File.Exists(index))return new string[0];SafeFiles.RejectLinks(index);
            using(var stream=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                // Read metadata only, and cap the tail even for a very large profile.
                stream.Position=Math.Max(0,stream.Length-262144);
                using(var reader=new StreamReader(stream)){
                    if(stream.Position>0)reader.ReadLine();var ids=new System.Collections.Generic.List<string>();string line;
                    while((line=reader.ReadLine())!=null)try{string id=Json.Str(Json.Get(Json.Read<object>(line),"id"));Guid value;if(Guid.TryParse(id,out value))ids.Add(id);}catch(ArgumentException){}
                    return ids.AsEnumerable().Reverse().Distinct().Take(8).ToArray();
                }
            }
        }
        internal static async Task<RelayChannel> Connect(RelayStore store,AccountService a,DesktopInstance instance,string workspace,CancellationToken token)
        {
            VerifyAccount(a,instance);workspace=RelayStore.WorkspacePath(workspace);string home=a.Instances.Home(instance),id=ChannelId(home,workspace);
            var channels=store.Channels().Where(c=>RelayStore.SamePath(c.Home,home)&&c.AccountKey==instance.AccountKey&&AgentProviders.Codex(c)).ToArray();
            var old=channels.FirstOrDefault(c=>c.Id==id);
            if(old!=null&&!old.Enabled)throw new InvalidOperationException("Cette connexion a été désactivée dans les réglages avancés.");
            string pipe=null;int pid=0;long ticks=0;
            if(!instance.IsLocal){var state=a.Instances.Runtime.Probe(instance);if(state.Running){pipe=state.AppToolsPipe;pid=state.DesktopPid;ticks=state.DesktopStartTicks;}}
            if(String.IsNullOrEmpty(pipe)){
                var live=channels.FirstOrDefault(c=>DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks));
                if(live!=null){pipe=live.PipePath;pid=live.ServerPid;ticks=live.ServerStartTicks;}
            }
            if(String.IsNullOrEmpty(pipe))throw new InvalidOperationException("Ouvrez « "+instance.Name+" » dans le switcher. Pour la session habituelle, utilisez une fois le skill de délégation dans un chat de cette instance.");
            using(var client=new AppToolsClient(pipe,pid,ticks)){
                var anchors=Anchors(home).Concat(channels.Where(c=>c.Enabled).Select(c=>c.AnchorThreadId)).Where(t=>!String.IsNullOrEmpty(t)).Distinct().Take(12);
                string anchor=null;
                foreach(string candidate in anchors){token.ThrowIfCancellationRequested();try{var snapshot=await client.Call(candidate,"read_thread",new{threadId=candidate,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token);DirectMessages.RequireThread(snapshot,candidate);anchor=candidate;break;}catch(InvalidOperationException){}}
                if(anchor==null)throw new InvalidOperationException("Ouvrez un premier chat dans « "+instance.Name+" » et envoyez un message. Codex doit avoir créé une conversation avant de pouvoir recevoir une délégation.");
                var channel=new RelayChannel{Id=id,Name=instance.Name,Home=home,AccountKey=instance.AccountKey,Email=a.Data.Profiles.Where(p=>p.Key==instance.AccountKey).Select(p=>p.Email).FirstOrDefault(),PipePath=pipe,ServerPid=client.ServerPid,ServerStartTicks=client.ServerStartTicks,AnchorThreadId=anchor,Workspace=workspace,Enabled=true,InstanceId=instance.Id,RequireFullAccess=old!=null&&old.RequireFullAccess,ConnectedUtc=DateTime.UtcNow.ToString("o")};
                VerifyAccount(a,instance);store.Register(channel);return channel;
            }
        }
        internal static async Task<RelayMessage> Submit(RelayStore store,RelaySession session,string name,RelayJobSpec spec,CancellationToken token,IRelayTransport transport=null,Func<DesktopInstance,string,Task<RelayChannel>> connector=null)
        {
            if(spec==null||!spec.ExplicitDelegation)throw new InvalidOperationException("La délégation par nom exige une demande explicite de l'utilisateur. Les automatismes avancés utilisent submit_job et leurs règles configurées.");
            var a=new AccountService(Path.GetDirectoryName(store.Root));var destination=Resolve(a,name);var source=store.Channel(session.Channel);
            if(RelayStore.SamePath(a.Instances.Home(destination),source.Home))throw new InvalidOperationException("Choisissez une autre instance que celle de cette conversation.");
            RelayStore.MessageId(spec.Id);spec.From=session.Channel;spec.To=ChannelId(a.Instances.Home(destination),source.Workspace);spec.Project=null;spec.Kind="general";
            if(store.Exists(spec.Id)){
                var old=store.Message(spec.Id);
                if(old.SourceThreadId!=session.Thread||old.TargetAccountKey!=destination.AccountKey||old.Job==null||RelayPolicies.Fingerprint(old.Job)!=RelayPolicies.Fingerprint(spec))throw new InvalidOperationException("Cet identifiant de mission existe avec un autre contenu ou destinataire.");
                return old;
            }
            // Advanced scope restrictions cannot be silently bypassed by creating an automatic alias.
            var policy=RelayPolicies.Load(store);
            if(policy.Projects.Any(p=>RelayStore.SamePath(p.Workspace,source.Workspace)))throw new InvalidOperationException("Ce dossier possède une politique de délégation avancée. Utilisez list_agents puis submit_job pour respecter ses destinataires et ressources autorisés.");
            if(store.Channels().Any(c=>RelayStore.SamePath(c.Home,a.Instances.Home(destination))&&RelayStore.SamePath(c.Workspace,source.Workspace)&&policy.Agents.Any(p=>p.Channel==c.Id)))throw new InvalidOperationException("Cette instance possède un profil d'agent avancé pour ce dossier. Utilisez list_agents et submit_job pour respecter ses restrictions.");
            var target=connector==null?await Connect(store,a,destination,source.Workspace,token):await connector(destination,source.Workspace);
            spec.From=session.Channel;spec.To=target.Id;spec.Project=null;spec.Kind="general";
            return await new RelayRouter(store,transport).Submit(spec,session.Thread,token);
        }
    }
}
