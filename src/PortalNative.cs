using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace Creezio.Switcher
{
    internal sealed class PortalNativeDependencies
    {
        internal Func<AccountService,DesktopInstance,InstanceState> Runtime=(accounts,instance)=>accounts.Instances.Runtime.Probe(instance);
        internal Func<RelayStore,IRelayTransport> Headless=value=>new HeadlessRelayTransport(value);
        internal Func<RelayStore,AccountService,DesktopInstance,InstanceState,string,CancellationToken,object> DesktopRead=null;
    }
    // Desktop metadata uses its verified native endpoint. Headless metadata is
    // restricted to conversations owned by our existing headless transport.
    // Neither path starts a model turn, resumes or creates a chat.
    internal sealed class PortalNative : IPortalNative,IPortalNativeAdmission
    {
        internal const int ThreadLimit=AppToolsClient.ThreadListLimit;
        private readonly RelayStore store;
        private readonly PortalNativeDependencies dependencies;
        internal PortalNative(RelayStore value,PortalNativeDependencies adapters=null){store=value;dependencies=adapters??new PortalNativeDependencies();}
        private DesktopInstance Resolve(AccountService accounts,PortalInstanceGrant grant)
        {
            var instance=NamedInstances.Resolve(accounts,grant.InstanceId);
            if(instance==null||instance.AccountKey!=grant.AccountKey||!RelayStore.SamePath(accounts.Instances.Home(instance),grant.Home))throw new InvalidOperationException("L'association permanente de cette instance a change. L'acces Web est suspendu.");
            NamedInstances.VerifyAccount(accounts,instance);RelayStore.WorkspacePath(grant.Workspace);
            if(grant.Access!="desktop"&&grant.Access!="headless")throw new InvalidOperationException("Mode d'acces de cette instance non reconnu.");return instance;
        }
        public void Verify(PortalInstanceGrant grant)
        {var accounts=new AccountService(Path.GetDirectoryName(store.Root));Resolve(accounts,grant);}
        public void VerifySend(PortalInstanceGrant grant)
        {if(grant.Access=="headless")RequireHeadless(new AccountService(Path.GetDirectoryName(store.Root)),grant);else Verify(grant);}
        public object Describe(PortalInstanceGrant grant)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=Resolve(accounts,grant);var state=dependencies.Runtime(accounts,instance);var profile=accounts.Data.Profiles.FirstOrDefault(p=>p.Key==grant.AccountKey);
            string issue=grant.Access=="headless"?HeadlessIssue(accounts,instance,state):state.Running?(string)null:"Ouvrez cette instance Codex pour consulter ses discussions natives.";
            bool available=grant.Access=="headless"?issue==null:state.Running;
            return new{id=instance.Id,name=WebhookChats.Safe(instance.Name,100),account=WebhookChats.Safe(profile==null?"":profile.Email??profile.Label,160),online=available,desktopOpen=state.Running,headlessAvailable=grant.Access=="headless"&&available,executionMode=available?grant.Access:"unavailable",issue=issue};
        }
        private static string HeadlessIssue(AccountService accounts,DesktopInstance instance,InstanceState state)
        {
            if(instance.IsLocal)return "Le mode sans fenetre de ce portail exige une instance isolee du Switcher.";
            if(!instance.HeadlessEnabled)return "Le moteur sans fenetre est desactive pour cette instance.";
            if(state==null||state.Phase=="unknown")return "L'etat de cette instance n'est pas verifiable. Aucun autre moteur ne sera demarre.";
            if(state.Running||state.Phase=="starting"||state.Phase=="stopping")return "Cette instance est ouverte ou en cours de lancement. Le portail ne demarre pas un second moteur.";
            if(String.IsNullOrEmpty(accounts.Settings.CodexExecutable)||!Path.IsPathRooted(accounts.Settings.CodexExecutable)||!File.Exists(accounts.Settings.CodexExecutable))return "L'executable Codex sans fenetre n'est pas disponible sur ce PC.";
            return null;
        }
        private DesktopInstance RequireHeadless(AccountService accounts,PortalInstanceGrant grant)
        {
            var instance=Resolve(accounts,grant);string issue=HeadlessIssue(accounts,instance,dependencies.Runtime(accounts,instance));
            if(issue!=null)throw new InvalidOperationException(issue);CodexEnvironment.CheckFileStorage(grant.Home);return instance;
        }
        private RelayChannel HeadlessChannel(AccountService accounts,DesktopInstance instance,PortalInstanceGrant grant,bool required)
        {
            string id=NamedInstances.HeadlessChannelId(grant.Home,grant.Workspace);var channel=store.Channels().SingleOrDefault(value=>value.Id==id);
            if(channel==null){if(required)throw new InvalidOperationException("Aucune discussion sans fenetre n'a encore ete creee pour ce compte et ce dossier.");return null;}
            if(!channel.Enabled||AgentProviders.Kind(channel)!="codex-headless"||channel.InstanceId!=instance.Id||channel.AccountKey!=grant.AccountKey||!RelayStore.SamePath(channel.Home??"",grant.Home)||!RelayStore.SamePath(channel.Workspace??"",grant.Workspace)||!RelayStore.SamePath(channel.Executable??"",accounts.Settings.CodexExecutable)||channel.HeadlessSandbox!=instance.HeadlessSandbox||channel.HeadlessApprovalPolicy!=instance.HeadlessApprovalPolicy)throw new InvalidOperationException("La connexion sans fenetre ne correspond plus au compte, au dossier ou aux reglages de cette instance.");
            return channel;
        }
        private static bool DesktopMetadataOwner(InstanceState state)
        {return state!=null&&state.Running&&state.DesktopPid>0&&state.DesktopStartTicks>0&&state.HostPid>0&&state.HostStartTicks>0&&!String.IsNullOrEmpty(state.LaunchId)&&state.Phase!="unknown"&&state.Phase!="starting"&&state.Phase!="stopping"&&state.Phase!="error";}
        private InstanceState RequireDesktopMetadata(AccountService accounts,DesktopInstance instance,PortalInstanceGrant grant,InstanceState expected=null)
        {
            if(instance.IsLocal||!instance.HeadlessEnabled)throw new InvalidOperationException("La lecture des discussions sans fenetre exige la meme instance isolee et autorisee.");
            var current=dependencies.Runtime(accounts,instance);
            if(!DesktopMetadataOwner(current)||expected!=null&&(current.LaunchId!=expected.LaunchId||current.HostPid!=expected.HostPid||current.HostStartTicks!=expected.HostStartTicks||current.DesktopPid!=expected.DesktopPid||current.DesktopStartTicks!=expected.DesktopStartTicks))throw new InvalidOperationException("Le proprietaire natif de cette instance a change pendant la lecture. Aucun autre moteur n'a ete demarre.");
            CodexEnvironment.CheckFileStorage(grant.Home);return Json.Read<InstanceState>(Json.Write(current));
        }
        private List<HeadlessThreadReceipt> OwnedReceipts(RelayChannel channel)
        {
            var rows=new List<HeadlessThreadReceipt>();if(channel==null)return rows;
            foreach(string path in BoundedReceiptPaths(Directory.EnumerateFiles(store.Root,"headless-thread-*.dpapi",SearchOption.TopDirectoryOnly))){
                var candidate=store.ReadRecord<HeadlessThreadReceipt>(Path.GetFileName(path));Guid parsed;
                if(candidate.ChannelId!=channel.Id||candidate.AccountKey!=channel.AccountKey||!Guid.TryParse(candidate.ThreadId,out parsed)||Path.GetFileName(path)!=HeadlessRelayTransport.ReceiptName(candidate.ThreadId))continue;
                try{rows.Add(HeadlessRelayTransport.RequireOwned(store,channel,candidate.ThreadId));}catch(InvalidOperationException){}
            }
            return rows;
        }
        internal static string[] BoundedReceiptPaths(IEnumerable<string> source)
        {var paths=source.Take(4097).ToArray();if(paths.Length>4096)throw new InvalidOperationException("L'historique sans fenetre depasse la limite de lecture de ce portail.");return paths;}
        private static object ReceiptList(IEnumerable<HeadlessThreadReceipt> rows)
        {return Json.Read<object>(Json.Write(new{pinnedThreads=new object[0],threads=rows.OrderByDescending(row=>row.UpdatedUtc,StringComparer.Ordinal).Take(ThreadLimit).Select(row=>new{threadId=row.ThreadId,title=WebhookChats.Safe(row.Title,120),status=row.State,updatedAt=row.UpdatedUtc}).ToArray()}));}
        private object HeadlessDesktopList(AccountService accounts,DesktopInstance instance,PortalInstanceGrant grant)
        {
            var pin=RequireDesktopMetadata(accounts,instance,grant);var channel=HeadlessChannel(accounts,instance,grant,false);var rows=OwnedReceipts(channel);
            accounts=new AccountService(Path.GetDirectoryName(store.Root));instance=Resolve(accounts,grant);RequireDesktopMetadata(accounts,instance,grant,pin);HeadlessChannel(accounts,instance,grant,channel!=null);
            return ReceiptList(rows);
        }
        private object HeadlessDesktopRead(AccountService accounts,DesktopInstance instance,PortalInstanceGrant grant,string thread)
        {
            var pin=RequireDesktopMetadata(accounts,instance,grant);var channel=HeadlessChannel(accounts,instance,grant,true);HeadlessRelayTransport.RequireOwned(store,channel,thread);
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15))){
                object result;
                if(dependencies.DesktopRead!=null)result=dependencies.DesktopRead(store,accounts,instance,pin,thread,deadline.Token);
                else{
                    string pipe;using(var client=NamedInstances.OpenDesktop(store,accounts,instance,deadline.Token,out pipe)){
                        if(client.ServerPid!=pin.DesktopPid||client.ServerStartTicks!=pin.DesktopStartTicks)throw new InvalidOperationException("Le canal natif ne correspond plus au proprietaire de cette instance.");
                        result=client.Call(thread,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},deadline.Token).GetAwaiter().GetResult();
                    }
                }
                DirectMessages.RequireThread(result,thread);string cwd=Json.Str(Json.Get(Json.Get(result,"thread"),"cwd"));if(cwd.Length==0||!RelayStore.SamePath(RelayStore.WorkspacePath(cwd),grant.Workspace))throw new InvalidOperationException("Le dossier de cette discussion ne correspond plus au dossier autorise.");
                accounts=new AccountService(Path.GetDirectoryName(store.Root));instance=Resolve(accounts,grant);RequireDesktopMetadata(accounts,instance,grant,pin);HeadlessChannel(accounts,instance,grant,true);HeadlessRelayTransport.RequireOwned(store,channel,thread);return result;
            }
        }
        private object HeadlessList(PortalInstanceGrant grant)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=Resolve(accounts,grant);
            if(DesktopMetadataOwner(dependencies.Runtime(accounts,instance)))return HeadlessDesktopList(accounts,instance,grant);
            instance=RequireHeadless(accounts,grant);
            using(new InstanceLease(InstancePaths.Folder(accounts.Vault.Root,instance.Id))){
                accounts=new AccountService(Path.GetDirectoryName(store.Root));instance=RequireHeadless(accounts,grant);var channel=HeadlessChannel(accounts,instance,grant,false);var rows=OwnedReceipts(channel);
                RequireHeadless(new AccountService(Path.GetDirectoryName(store.Root)),grant);
                return ReceiptList(rows);
            }
        }
        private object HeadlessRead(PortalInstanceGrant grant,string thread)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=Resolve(accounts,grant);
            if(DesktopMetadataOwner(dependencies.Runtime(accounts,instance)))return HeadlessDesktopRead(accounts,instance,grant,thread);
            instance=RequireHeadless(accounts,grant);
            using(new InstanceLease(InstancePaths.Folder(accounts.Vault.Root,instance.Id)))using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15))){
                accounts=new AccountService(Path.GetDirectoryName(store.Root));instance=RequireHeadless(accounts,grant);var channel=HeadlessChannel(accounts,instance,grant,true);
                HeadlessRelayTransport.RequireOwned(store,channel,thread);
                object result=dependencies.Headless(store).Call(channel,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},deadline.Token).GetAwaiter().GetResult();
                DirectMessages.RequireThread(result,thread);string cwd=Json.Str(Json.Get(Json.Get(result,"thread"),"cwd"));if(cwd.Length==0||!RelayStore.SamePath(RelayStore.WorkspacePath(cwd),grant.Workspace))throw new InvalidOperationException("Le dossier de cette discussion ne correspond plus au dossier autorise.");
                accounts=new AccountService(Path.GetDirectoryName(store.Root));instance=RequireHeadless(accounts,grant);HeadlessChannel(accounts,instance,grant,true);HeadlessRelayTransport.RequireOwned(store,channel,thread);return result;
            }
        }
        private string Anchor(PortalInstanceGrant grant)
        {
            var registered=store.Channels().Where(c=>c.Enabled&&AgentProviders.Kind(c)=="codex"&&c.AccountKey==grant.AccountKey&&RelayStore.SamePath(c.Home,grant.Home)&&!String.IsNullOrEmpty(c.AnchorThreadId)).Select(c=>c.AnchorThreadId).FirstOrDefault();if(registered!=null)return registered;
            string index=Path.Combine(grant.Home,"session_index.jsonl");if(!File.Exists(index))throw new InvalidOperationException("Aucune discussion native n'est encore disponible dans cette instance.");SafeFiles.RejectLinks(index);var ids=new List<string>();
            using(var stream=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                stream.Position=Math.Max(0,stream.Length-65536);using(var reader=new StreamReader(stream)){if(stream.Position>0)reader.ReadLine();string line;while((line=reader.ReadLine())!=null){if(line.Length>8192)continue;try{string id=Json.Str(Json.Get(Json.Read<object>(line),"id"));Guid parsed;if(Guid.TryParse(id,out parsed))ids.Add(id);}catch(ArgumentException){}}}
            }
            if(ids.Count==0)throw new InvalidOperationException("Aucune discussion native verifiable n'est encore disponible.");return ids.Last();
        }
        private object Call(PortalInstanceGrant grant,string thread,string tool,object args)
        {
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15))){
                var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=Resolve(accounts,grant);string pipe;
                using(var client=NamedInstances.OpenDesktop(store,accounts,instance,deadline.Token,out pipe)){
                    object result=client.Call(thread,tool,args,deadline.Token).GetAwaiter().GetResult();Resolve(new AccountService(Path.GetDirectoryName(store.Root)),grant);return result;
                }
            }
        }
        public object List(PortalInstanceGrant grant)
        {return grant.Access=="headless"?HeadlessList(grant):Call(grant,Anchor(grant),"list_threads",new{limit=ThreadLimit});}
        public object Read(PortalInstanceGrant grant,string thread)
        {return grant.Access=="headless"?HeadlessRead(grant,thread):Call(grant,thread,"read_thread",new{threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000});}
    }
}
