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
        internal static string HeadlessChannelId(string home,string workspace){return ChannelId(home,workspace)+"-headless";}
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
                var state=a.Instances.Runtime.Probe(i);bool desktopOpen=state.Running;
                bool headless=issue==null&&i.HeadlessEnabled&&!state.Running&&state.Phase!="unknown"&&File.Exists(a.Settings.CodexExecutable);
                return (object)new{id=i.Id,name=i.Name,accountKey=i.AccountKey,account=a.Data.Profiles.Where(p=>p.Key==i.AccountKey).Select(p=>p.Label).FirstOrDefault(),online=desktopOpen||headless,desktopOpen=desktopOpen,headlessAvailable=headless,executionMode=desktopOpen?"desktop":headless?"headless":"unavailable",issue=issue,integration=RelayIntegration.Status(store,a.Instances.Home(i)).Status};
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
        internal static bool AppPipeName(string value)
        {return System.Text.RegularExpressions.Regex.IsMatch(value??"",@"\A\\\\\.\\pipe\\codex-(?:browser-use|app-tools)-[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\z");}
        internal static string DiscoverPipe(InstanceState state,System.Collections.Generic.IEnumerable<string> paths,Func<string,int,long,bool> verify,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(state==null||!state.Running||state.DesktopPid<=0||state.DesktopStartTicks<=0||state.Phase=="unknown")return null;
            foreach(string path in paths.Where(AppPipeName).OrderBy(p=>p.IndexOf("codex-app-tools-",StringComparison.Ordinal)>=0?0:1).Take(64)){
                token.ThrowIfCancellationRequested();
                // The native verifier checks the Windows pipe owner PID and start
                // time before any RPC; the same proof is required for every instance.
                try{if(verify(path,state.DesktopPid,state.DesktopStartTicks))return path;}
                catch(IOException){}catch(InvalidOperationException){}catch(TimeoutException){}
            }
            return null;
        }
        private static string DesktopPipe(InstanceState state,CancellationToken token)
        {
            return DiscoverPipe(state,Directory.EnumerateFiles(@"\\.\pipe\"),(path,pid,ticks)=>{
                using(var client=new AppToolsClient(path,pid,ticks))return true;
            },token);
        }
        // Discover an existing desktop endpoint only. No channel, account,
        // permission, process or thread is created or changed by this lookup.
        internal static AppToolsClient OpenDesktop(RelayStore store,AccountService accounts,DesktopInstance instance,CancellationToken token,out string pipe)
        {
            pipe=null;token.ThrowIfCancellationRequested();VerifyAccount(accounts,instance);
            if(instance.Archived)throw new InvalidOperationException("Cette instance est archivée.");
            string home=accounts.Instances.Home(instance);var state=accounts.Instances.Runtime.Probe(instance);
            if(!state.Running||state.Phase=="unknown"||state.DesktopPid<=0||state.DesktopStartTicks<=0)throw new InvalidOperationException("L’instance Codex n’est pas ouverte ou son identité n’est pas vérifiable. Aucun lancement effectué.");
            var candidates=store.Channels().Where(c=>c.Enabled&&AgentProviders.Kind(c)=="codex"&&c.AccountKey==instance.AccountKey&&RelayStore.SamePath(c.Home,home)&&c.ServerPid==state.DesktopPid&&c.ServerStartTicks==state.DesktopStartTicks).Select(c=>c.PipePath);
            if(!instance.IsLocal&&!String.IsNullOrEmpty(state.AppToolsPipe))candidates=new[]{state.AppToolsPipe}.Concat(candidates);
            foreach(string candidate in candidates.Where(p=>!String.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)){
                token.ThrowIfCancellationRequested();AppToolsClient client=null;
                try{client=new AppToolsClient(candidate,state.DesktopPid,state.DesktopStartTicks);VerifyAccount(accounts,instance);token.ThrowIfCancellationRequested();pipe=candidate;return client;}
                catch(IOException){if(client!=null)client.Dispose();}catch(InvalidOperationException){if(client!=null)client.Dispose();}catch(TimeoutException){if(client!=null)client.Dispose();}catch{if(client!=null)client.Dispose();throw;}
            }
            {
                string candidate=DesktopPipe(state,token);token.ThrowIfCancellationRequested();
                if(candidate!=null){var client=new AppToolsClient(candidate,state.DesktopPid,state.DesktopStartTicks);try{VerifyAccount(accounts,instance);token.ThrowIfCancellationRequested();pipe=candidate;return client;}catch{client.Dispose();throw;}}
            }
            throw new InvalidOperationException("Le canal de cette instance Codex ouverte n’est pas encore disponible. Aucun autre moteur n’a été démarré.");
        }
        internal static async Task<RelayChannel> Connect(RelayStore store,AccountService a,DesktopInstance instance,string workspace,CancellationToken token,string preferredThread=null,bool allowHeadless=true)
        {
            VerifyAccount(a,instance);workspace=RelayStore.WorkspacePath(workspace);string home=a.Instances.Home(instance),id=ChannelId(home,workspace);
            var channels=store.Channels().Where(c=>RelayStore.SamePath(c.Home,home)&&c.AccountKey==instance.AccountKey&&AgentProviders.Kind(c)=="codex").ToArray();
            var old=channels.FirstOrDefault(c=>c.Id==id);
            if(old!=null&&!old.Enabled)throw new InvalidOperationException("Cette connexion a été désactivée dans les réglages avancés.");
            var state=a.Instances.Runtime.Probe(instance);
            if(allowHeadless&&!state.Running&&state.Phase!="unknown"&&instance.HeadlessEnabled){
                string headlessId=HeadlessChannelId(home,workspace);var previous=store.Channels().FirstOrDefault(c=>c.Id==headlessId);
                if(previous!=null&&!previous.Enabled)throw new InvalidOperationException("Cette connexion sans interface a été désactivée dans les réglages avancés.");
                var channel=new RelayChannel{Id=headlessId,Provider="codex-headless",Name=instance.Name,Home=home,AccountKey=instance.AccountKey,Email=a.Data.Profiles.Where(p=>p.Key==instance.AccountKey).Select(p=>p.Email).FirstOrDefault(),Executable=a.Settings.CodexExecutable,HeadlessSandbox=instance.HeadlessSandbox,HeadlessApprovalPolicy=instance.HeadlessApprovalPolicy,Workspace=workspace,Enabled=true,InstanceId=instance.Id,RequireFullAccess=previous!=null?previous.RequireFullAccess:old!=null&&old.RequireFullAccess,ConnectedUtc=DateTime.UtcNow.ToString("o")};
                VerifyAccount(a,instance);store.Register(channel);HeadlessRelayTransport.Validate(store,channel);return channel;
            }
            string pipe;
            using(var client=OpenDesktop(store,a,instance,token,out pipe)){
                var anchors=new[]{preferredThread}.Concat(Anchors(home)).Concat(channels.Where(c=>c.Enabled).Select(c=>c.AnchorThreadId)).Where(t=>!String.IsNullOrEmpty(t)).Distinct().Take(12);
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
                if(old.Job!=null)spec.To=old.Job.To;
                if(old.SourceThreadId!=session.Thread||old.TargetAccountKey!=destination.AccountKey||String.IsNullOrEmpty(old.TargetHome)||!RelayStore.SamePath(old.TargetHome,a.Instances.Home(destination))||old.Job==null||RelayPolicies.Fingerprint(old.Job)!=RelayPolicies.Fingerprint(spec))throw new InvalidOperationException("Cet identifiant de mission existe avec un autre contenu ou destinataire.");
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
