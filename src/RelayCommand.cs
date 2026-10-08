using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class RelayCli
    {
        private static int Main(string[] args){
            if(args.Length==2&&args[0]=="--worker")return RelayWorker.Run(args[1]);
            if(args.Length==2&&args[0]=="--usage-worker")return UsageCoordinator.Run(args[1]);
            if(args.Length==2&&args[0]=="--remote-worker")return RemoteGateway.Run(args[1]);
            if(args.Length==3&&args[0]=="--assistance-hook")return AssistanceHooks.Run(args[1],args[2]);
            if(args.Length==3&&args[0]=="--mcp")return RelayMcp.Run(args[1],args[2]);
            return RelayCommand.Run();
        }
    }
    internal static class RelayCommand
    {
        private static string Required(object input,string key)
        {string value=Json.Str(Json.Get(input,key));if(String.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Champ requis : "+key);return value;}
        public static async Task<RelayChannel> Register(RelayStore store,object input,CancellationToken token)
        {
            string pipe=Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH"),thread=Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            if(String.IsNullOrWhiteSpace(pipe) || String.IsNullOrWhiteSpace(thread))throw new InvalidOperationException("Exécutez la commande de connexion dans une conversation Codex de l'instance choisie.");
            string home=CodexEnvironment.DefaultHome();CodexEnvironment.CheckFileStorage(home);
            var identity=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json")));
            string id=Required(input,"channel"),workspace=RelayStore.WorkspacePath(Required(input,"workspace"));RelayStore.ChannelId(id);
            using(var client=new AppToolsClient(pipe)) {
                var catalog=await client.Request("tools/list",new {threadStartKind="all"},token);
                var names=RelayEngine.Rows(Json.Get(catalog,"tools")).Where(t=>Json.Str(Json.Get(t,"namespace"))=="codex_app").Select(t=>Json.Str(Json.Get(t,"name"))).ToList();
                if(new[]{"read_thread","create_thread","send_message_to_thread"}.Any(n=>!names.Contains(n)))throw new InvalidOperationException("Cette version de Codex n'expose pas tous les outils nécessaires au relais.");
                var snapshot=await client.Call(thread,"read_thread",new {threadId=thread,turnLimit=1,includeOutputs=false,maxOutputCharsPerItem=1000},token);
                if(Json.Str(Json.Get(Json.Get(snapshot,"thread"),"id"))!=thread)throw new InvalidOperationException("Conversation de connexion non reconnue par cette instance.");
                var channel=new RelayChannel {Id=id,Name=String.IsNullOrWhiteSpace(Json.Str(Json.Get(input,"name")))?id:Json.Str(Json.Get(input,"name")),Home=home,AccountKey=identity.Key,Email=identity.Email,PipePath=pipe,ServerPid=client.ServerPid,ServerStartTicks=client.ServerStartTicks,AnchorThreadId=thread,Workspace=workspace,Enabled=true,ConnectedUtc=DateTime.UtcNow.ToString("o")};
                if(channel.Name.Length>100)throw new InvalidOperationException("Nom du canal trop long.");
                channel.RequireFullAccess=!Object.Equals(Json.Get(input,"requireFullAccess"),false);
                string instanceFolder=Path.GetDirectoryName(home);string instanceId=Path.GetFileName(instanceFolder);
                if(InstanceRules.ValidId(instanceId)&&RelayStore.SamePath(home,InstancePaths.Home(Path.GetDirectoryName(store.Root),instanceId)))channel.InstanceId=instanceId;
                // Only accept a project ID obtained from this instance's registered project list.
                string wanted=Json.Str(Json.Get(input,"projectId"));
                if(!String.IsNullOrEmpty(wanted)){
                    var projects=await client.Call(thread,"list_projects",new{},token);
                    var rows=RelayEngine.Rows(Json.Get(projects,"projects"));
                    var match=rows.FirstOrDefault(p=>Json.Str(Json.Get(p,"projectId"))==wanted||Json.Str(Json.Get(p,"id"))==wanted);
                    string path=Json.Str(Json.Get(match,"path"));if(String.IsNullOrEmpty(path))path=Json.Str(Json.Get(match,"cwd"));
                    if(match==null||String.IsNullOrEmpty(path)||!RelayStore.SamePath(path,workspace))throw new InvalidOperationException("Projet Codex non reconnu pour ce dossier.");channel.CodexProjectId=wanted;
                }
                RelayPermissions.Require(channel,thread);
                store.Register(channel);return channel;
            }
        }
        internal static void RequireSource(RelayChannel channel)
        {
            if(Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH")!=channel.PipePath || !RelayStore.SamePath(CodexEnvironment.DefaultHome(),channel.Home))
                throw new InvalidOperationException("Cette conversation n'appartient pas à l'instance du canal source.");
            new DesktopRelayTransport().Verify(channel);
        }
        public static object ChannelSummary(RelayChannel c)
        {return new {c.Id,c.Name,c.Email,c.Workspace,c.Enabled,c.AnchorThreadId,c.RequireFullAccess,Permissions=RelayPermissions.Read(c.Home,c.AnchorThreadId),Online=DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks)};}
        public static object MessageSummary(RelayMessage m,bool result)
        {return new {m.Id,m.Title,m.SourceChannelId,m.TargetChannelId,m.State,m.ReturnState,m.SourceThreadId,m.TargetThreadId,m.Revision,m.Error,m.Outcome,m.RoutingReason,m.BlockReason,m.DispatchPhase,m.ExpectedPermission,m.ObservedPermission,m.CancellationRequested,m.RootJobId,m.Depth,m.Job,Result=result&&m.Result!=null?m.Result.Substring(0,Math.Min(12000,m.Result.Length)):null,ResultLength=m.Result==null?0:m.Result.Length,Events=result?m.Events:null,m.CreatedUtc,m.UpdatedUtc};}
        public static async Task<object> Execute(object input,CancellationToken token)
        {
            string root=Json.Str(Json.Get(input,"root"));var store=new RelayStore(String.IsNullOrWhiteSpace(root)?RelayStore.DefaultRoot:root);
            string op=Required(input,"operation");
            if(op=="register")return ChannelSummary(await Register(store,input,token));
            if(op=="bind")return await RelaySessions.Bind(store,Json.Str(Json.Get(input,"channel")),token);
            if(op=="worker-status")return RelayWorker.Status(store);
            if(op=="worker-start"){RelayWorker.Resume(store);return new{ok=true};}
            if(op=="worker-stop"){RelayWorker.Stop(store);return new{ok=true};}
            if(op=="policy")return RelayPolicies.Load(store);
            if(op=="submit"){
                var spec=Json.Read<RelayJobSpec>(Json.Write(Json.Get(input,"job")));if(spec==null)throw new InvalidOperationException("Job requis.");
                var source=store.Channel(spec.From);RequireSource(source);
                var m=await new RelayRouter(store).Submit(spec,Environment.GetEnvironmentVariable("CODEX_THREAD_ID"),token);RelayWorker.Ensure(store);return MessageSummary(m,true);
            }
            if(op=="channels")return store.Channels().Select(ChannelSummary).ToArray();
            if(op=="list")return store.Messages().Take(100).Select(m=>MessageSummary(m,false)).ToArray();
            if(op=="get")return MessageSummary(store.Message(Required(input,"id")),true);
            if(op=="send") {
                var source=store.Channel(Required(input,"from"));RequireSource(source);
                string thread=Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
                var spec=new RelayJobSpec{From=source.Id,To=Required(input,"to"),Title=Required(input,"title"),Prompt=Required(input,"prompt"),Revision=Json.Str(Json.Get(input,"revision")),ReturnToSource=Object.Equals(Json.Get(input,"returnToSource"),true),ReplyTo=Json.Str(Json.Get(input,"replyTo")),Id=String.IsNullOrWhiteSpace(Json.Str(Json.Get(input,"id")))?null:Json.Str(Json.Get(input,"id")),ExplicitDelegation=true};
                var message=await new RelayRouter(store).Submit(spec,thread,token);RelayWorker.Ensure(store);return MessageSummary(message,true);
            }
            if(op=="pump") {RelayWorker.Ensure(store);return new {ok=true};}
            if(op=="cancel") {store.RequestCancel(Required(input,"id"));return new {ok=true};}
            if(op=="recheck") {string id=Required(input,"id");store.Recheck(id);RelayWorker.Ensure(store);return MessageSummary(store.Message(id),true);}
            if(op=="close-reviewed") {store.CloseReviewed(Required(input,"id"));return new {ok=true};}
            if(op=="wait") {
                string id=Required(input,"id");double seconds=Json.Number(Json.Get(input,"seconds"))??50;
                if(seconds<1 || seconds>3600)throw new InvalidOperationException("Délai attendu entre 1 et 3 600 secondes.");
                DateTime until=DateTime.UtcNow.AddSeconds(seconds);
                do {
                    RelayWorker.Ensure(store);var message=store.Message(id);
                    if(new[]{"failed","attention","uncertain","cancelled","closed"}.Contains(message.State) || message.ReturnState=="uncertain" || (message.State=="completed" && (!message.ReturnToSource || message.ReturnState=="delivered")))return MessageSummary(message,true);
                    await Task.Delay(2000,token);
                }while(DateTime.UtcNow<until);
                return MessageSummary(store.Message(id),true);
            }
            throw new InvalidOperationException("Opération inconnue. Utilisez register, channels, send, get, list, wait, pump ou cancel.");
        }
        public static int Run()
        {
            try {
                using(var reader=new StreamReader(Console.OpenStandardInput(),System.Text.Encoding.UTF8)) {
                    string text=reader.ReadToEnd();if(text.Length>100000)throw new InvalidOperationException("Commande trop volumineuse.");
                    object result=Execute(Json.Read<object>(text),CancellationToken.None).GetAwaiter().GetResult();
                    using(var writer=new StreamWriter(Console.OpenStandardOutput(),new System.Text.UTF8Encoding(false))){writer.WriteLine(Json.Write(result));}
                }
                return 0;
            }catch(Exception e){using(var writer=new StreamWriter(Console.OpenStandardOutput(),new System.Text.UTF8Encoding(false))){writer.WriteLine(Json.Write(new {error=Program.SafeError(e)}));}return 1;}
        }
    }
}
