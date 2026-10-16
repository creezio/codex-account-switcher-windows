using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class HeadlessRuntimeLostException : InvalidOperationException
    {
        internal string Code{get;private set;}internal string Method{get;private set;}internal string Diagnostic{get;private set;}
        internal HeadlessRuntimeLostException():base("Le moteur qui exécutait ce mandat s'est arrêté. Son historique reste lisible ; aucun renvoi automatique n'a été effectué. Relisez son résultat ou classez cette mission avant d'en créer une autre.") {}
        internal HeadlessRuntimeLostException(string reason):base("Le moteur sans fenêtre a été interrompu après modification de sa configuration. Aucun succès ni arrêt du mandat n'est confirmé. "+reason) {}
        internal HeadlessRuntimeLostException(HeadlessRpcException error):base("Le moteur sans fenêtre a interrompu l'opération "+(error.Method??"native")+" ("+error.Code+(String.IsNullOrEmpty(error.Diagnostic)?"":" · "+error.Diagnostic)+"). Aucun renvoi automatique ; résultat à vérifier.",error){Code=error.Code;Method=error.Method;Diagnostic=error.Diagnostic;}
    }
    public sealed class HeadlessThreadReceipt
    {
        public string ThreadId {get;set;} public string TurnId {get;set;} public string ChannelId {get;set;}
        public string AccountKey {get;set;} public string Home {get;set;} public string Workspace {get;set;}
        public string Sandbox {get;set;} public string ApprovalPolicy {get;set;} public string Title {get;set;}
        public string State {get;set;} public string PromptHash {get;set;} public string WirePromptHash {get;set;} public string UpdatedUtc {get;set;} public string RuntimeId {get;set;} public string ClientUserMessageId {get;set;}
        public bool InterruptSent {get;set;}
    }
    public sealed class HeadlessRuntimeState
    {
        public string AccountKey {get;set;} public string Home {get;set;} public string RuntimeId {get;set;} public string BrokerPipe {get;set;}
        public int WorkerPid {get;set;} public long WorkerStarted {get;set;} public int ProcessId {get;set;} public long ProcessStartedTicks {get;set;} public string UpdatedUtc {get;set;} public bool Running {get;set;} public bool Revoked {get;set;}public string Diagnostic {get;set;}
    }
    // This adapter exposes only conversations created by this service. It never resumes
    // an arbitrary imported or user chat, and retains a native server while a turn runs.
    internal sealed class HeadlessRelayTransport : IRelayTransport,IRelayPermissionTransport
    {
        private sealed class Runtime : IDisposable
        {
            internal IHeadlessRpc Rpc; internal FileStream Lease; internal IDisposable Broker;internal string Home,Id,Account; internal int Users;
            internal readonly SemaphoreSlim Gate=new SemaphoreSlim(1,1);
            internal readonly HashSet<string> Loaded=new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Active=new HashSet<string>(StringComparer.Ordinal);
            internal readonly ConcurrentDictionary<string,string> Changes=new ConcurrentDictionary<string,string>(StringComparer.Ordinal);
            public void Dispose(){try{if(Broker!=null)Broker.Dispose();Rpc.Dispose();}finally{Lease.Dispose();}}
        }
        private static readonly object RuntimeSync=new object();
        private static readonly Dictionary<string,Runtime> Runtimes=new Dictionary<string,Runtime>(StringComparer.Ordinal);
        private readonly RelayStore store; private readonly Func<RelayChannel,IHeadlessRpc> factory;private readonly Action<RelayChannel,CancellationToken> prepare;
        internal HeadlessRelayTransport(RelayStore store,Func<RelayChannel,IHeadlessRpc> factory=null,Action<RelayChannel,CancellationToken> prepare=null){this.store=store;this.factory=factory??(channel=>new HeadlessRpc(channel.Executable,channel.Home,channel.AccountKey));this.prepare=prepare??(factory==null?(Action<RelayChannel,CancellationToken>)((channel,token)=>PrepareDefault(store,channel,token)):((channel,token)=>{}));}
        private static void PrepareDefault(RelayStore store,RelayChannel channel,CancellationToken token)
        {
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=Validate(store,channel);HeadlessAccounts.Read(accounts,instance,false,token);
            if(accounts.Settings.MaintainIntegration)IntegrationMaintenance.Check(store,channel.Home,accounts.Settings,true,false,token).GetAwaiter().GetResult();
            Validate(store,channel);
        }
        internal static string ReceiptName(string thread){Guid parsed;if(!Guid.TryParse(thread,out parsed))throw new InvalidOperationException("Identifiant natif de conversation invalide.");return "headless-thread-"+parsed.ToString("N")+".dpapi";}
        internal static string RuntimeName(string account){return "headless-runtime-"+RelayReturns.Key(account)+".dpapi";}
        private static object Shape(object value){return Json.Read<object>(Json.Write(value));}
        internal static DesktopInstance Validate(RelayStore store,RelayChannel channel)
        {
            try{return ValidateCore(store,channel);}catch(Exception error){
                bool revoked=Invalidate(store,channel);if(!revoked){try{revoked=store.ReadRecord<HeadlessRuntimeState>(RuntimeName(channel.AccountKey)).Revoked;}catch(IOException){}}
                if(revoked)throw new HeadlessRuntimeLostException(Program.SafeError(error));throw;
            }
        }
        private static DesktopInstance ValidateCore(RelayStore store,RelayChannel channel)
        {
            if(!channel.Enabled||AgentProviders.Kind(channel)!="codex-headless")throw new InvalidOperationException("Canal sans interface désactivé ou incompatible.");
            var current=store.Channel(channel.Id);if(!current.Enabled||AgentProviders.Binding(current)!=AgentProviders.Binding(channel)||current.InstanceId!=channel.InstanceId||current.RequireFullAccess!=channel.RequireFullAccess)throw new InvalidOperationException("La connexion ou ses permissions ont changé dans le Switcher. Aucun nouvel envoi effectué.");
            var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=accounts.Data.Instances.SingleOrDefault(value=>!value.Archived&&value.Id==channel.InstanceId);
            if(instance==null||!instance.HeadlessEnabled||instance.AccountKey!=channel.AccountKey||!RelayStore.SamePath(accounts.Instances.Home(instance),channel.Home))throw new InvalidOperationException("Le compte ou l'instance du moteur sans interface a changé.");
            NamedInstances.VerifyAccount(accounts,instance);CodexEnvironment.CheckFileStorage(channel.Home);RelayStore.WorkspacePath(channel.Workspace);
            if(instance.HeadlessSandbox!=channel.HeadlessSandbox||instance.HeadlessApprovalPolicy!=channel.HeadlessApprovalPolicy||!new[]{"read-only","workspace-write","danger-full-access"}.Contains(channel.HeadlessSandbox)||!new[]{"on-request","never"}.Contains(channel.HeadlessApprovalPolicy))throw new InvalidOperationException("Les permissions du moteur ont changé. Préparez une nouvelle mission avec les réglages actuels.");
            if(String.IsNullOrEmpty(channel.Executable)||!Path.IsPathRooted(channel.Executable)||!File.Exists(channel.Executable)||!RelayStore.SamePath(channel.Executable,accounts.Settings.CodexExecutable))throw new InvalidOperationException("L'exécutable Codex de cette mission a changé.");
            return instance;
        }
        internal static HeadlessThreadReceipt RequireOwned(RelayStore store,RelayChannel channel,string thread)
        {
            var receipt=store.ReadRecord<HeadlessThreadReceipt>(ReceiptName(thread));
            if(receipt.ThreadId!=thread||receipt.ChannelId!=channel.Id||receipt.AccountKey!=channel.AccountKey||!RelayStore.SamePath(receipt.Home??"",channel.Home)||!RelayStore.SamePath(receipt.Workspace??"",channel.Workspace)||receipt.Sandbox!=channel.HeadlessSandbox||receipt.ApprovalPolicy!=channel.HeadlessApprovalPolicy)throw new InvalidOperationException("Cette conversation n'a pas été créée par ce moteur pour ce compte et ce dossier. Aucun envoi effectué.");
            return receipt;
        }
        private void Save(HeadlessThreadReceipt receipt){receipt.UpdatedUtc=DateTime.UtcNow.ToString("o");store.WriteRecord(ReceiptName(receipt.ThreadId),receipt);}
        public void Verify(RelayChannel channel){Validate(store,channel);}
        public void VerifySend(RelayChannel channel,string thread)
        {
            Verify(channel);if(channel.RequireFullAccess&&Permissions(channel,thread)!="full-access")throw new InvalidOperationException("Ce canal exige Accès complet. Choisissez explicitement les permissions correspondantes dans les réglages de l'instance avant une nouvelle mission.");
            if(!String.IsNullOrEmpty(thread))RequireOwned(store,channel,thread);
        }
        public string Permissions(RelayChannel channel,string thread)
        {Validate(store,channel);if(!String.IsNullOrEmpty(thread))RequireOwned(store,channel,thread);return channel.HeadlessSandbox=="danger-full-access"&&channel.HeadlessApprovalPolicy=="never"?"full-access":channel.HeadlessSandbox+" / "+channel.HeadlessApprovalPolicy;}
        private Runtime Connection(RelayChannel channel,CancellationToken token)
        {
            string key=store.Root+"|"+channel.AccountKey;
            lock(RuntimeSync){Runtime active;if(Runtimes.TryGetValue(key,out active)){if(!RelayStore.SamePath(active.Home,channel.Home))throw new InvalidOperationException("Ce compte travaille déjà dans un autre profil sans interface. Attendez sa fin.");active.Users++;return active;}}
            // Authentication refresh takes the profile coordinator's leases before
            // the inference server exists. Never hold the global runtime lock here.
            prepare(channel,token);token.ThrowIfCancellationRequested();Verify(channel);
            lock(RuntimeSync){
                Runtime runtime;if(Runtimes.TryGetValue(key,out runtime)){if(!RelayStore.SamePath(runtime.Home,channel.Home))throw new InvalidOperationException("Ce compte travaille déjà dans un autre profil sans interface. Attendez sa fin.");runtime.Users++;return runtime;}
                var lease=store.Lease("headless-owner-"+RelayReturns.Key(channel.AccountKey));
                runtime=null;try{
                    runtime=new Runtime{Rpc=factory(channel),Lease=lease,Home=channel.Home,Account=channel.AccountKey,Id=Guid.NewGuid().ToString("N"),Users=1};
                    runtime.Rpc.Notification+=(method,args)=>{if(method!="item/started"&&method!="item/completed")return;var item=Json.Get(args,"item");string summary=HeadlessApprovals.FileChangeSummary(item);if(summary==null)return;string threadId=Json.Str(Json.Get(args,"threadId")),turnId=Json.Str(Json.Get(args,"turnId")),itemId=Json.Str(Json.Get(item,"id"));if(threadId.Length>128||turnId.Length>128||itemId.Length>256)return;string keyItem=HeadlessApprovals.ChangeKey(threadId,turnId,itemId);if(runtime.Changes.Count>=32){string ignored;foreach(string previous in runtime.Changes.Keys.Take(Math.Max(1,runtime.Changes.Count-31)).ToArray())runtime.Changes.TryRemove(previous,out ignored);}runtime.Changes[keyItem]=summary;};
                    runtime.Rpc.RequestHandler=(method,args,interactionToken)=>{
                        var receipt=store.ReadRecord<HeadlessThreadReceipt>(ReceiptName(Json.Str(Json.Get(args,"threadId"))));
                        if(receipt.AccountKey!=runtime.Account||!RelayStore.SamePath(receipt.Home??"",runtime.Home))throw new InvalidOperationException("Intervention hors du compte contrôlé.");
                        string changes;runtime.Changes.TryGetValue(HeadlessApprovals.ChangeKey(receipt.ThreadId,Json.Str(Json.Get(args,"turnId")),Json.Str(Json.Get(args,"itemId"))),out changes);return new HeadlessApprovals(store).Wait(store.Channel(receipt.ChannelId),method,args,interactionToken,runtime.Id,changes);
                    };
                    Runtimes.Add(key,runtime);using(var process=Process.GetCurrentProcess()){
                        var state=new HeadlessRuntimeState{AccountKey=channel.AccountKey,Home=channel.Home,RuntimeId=runtime.Id,WorkerPid=process.Id,WorkerStarted=process.StartTime.ToUniversalTime().Ticks,ProcessId=runtime.Rpc.ProcessId,ProcessStartedTicks=runtime.Rpc.ProcessStartedTicks,Running=true,UpdatedUtc=DateTime.UtcNow.ToString("o")};
                        if(state.ProcessId>0){var broker=new HeadlessSourceBroker(store,state,(source,threadId,readToken)=>Call(source,"read_thread",new{threadId=threadId},readToken));runtime.Broker=broker;state.BrokerPipe=broker.Name;}
                        store.WriteRecord(RuntimeName(channel.AccountKey),state);
                    }
                    return runtime;
                }catch{Runtimes.Remove(key);if(runtime!=null)runtime.Dispose();else lease.Dispose();throw;}
            }
        }
        internal static void Shutdown(RelayStore store)
        {
            lock(RuntimeSync){foreach(var pair in Runtimes.Where(pair=>pair.Key.StartsWith(store.Root+"|",StringComparison.Ordinal)).ToArray()){
                Runtimes.Remove(pair.Key);var runtime=pair.Value;
                try{runtime.Dispose();}finally{try{new HeadlessApprovals(store).CancelOwned(runtime.Account,runtime.Home);store.WriteRecord(RuntimeName(runtime.Account),new HeadlessRuntimeState{AccountKey=runtime.Account,Home=runtime.Home,RuntimeId=runtime.Id,Running=false,UpdatedUtc=DateTime.UtcNow.ToString("o")});}catch(IOException){}}
            }}
        }
        internal static bool HasOwnedWork(RelayStore store)
        {lock(RuntimeSync)return Runtimes.Any(pair=>pair.Key.StartsWith(store.Root+"|",StringComparison.Ordinal)&&(pair.Value.Active.Count>0||pair.Value.Users>0));}
        internal static async Task PumpOwned(RelayStore store,CancellationToken token)
        {
            string[] active;lock(RuntimeSync)active=Runtimes.Where(pair=>pair.Key.StartsWith(store.Root+"|",StringComparison.Ordinal)).SelectMany(pair=>pair.Value.Active.ToArray()).Distinct().ToArray();
            foreach(string thread in active){token.ThrowIfCancellationRequested();var receipt=store.ReadRecord<HeadlessThreadReceipt>(ReceiptName(thread));
                try{await new HeadlessRelayTransport(store).Call(store.Channel(receipt.ChannelId),"read_thread",new{threadId=thread},token).ConfigureAwait(false);}
                catch(OperationCanceledException){throw;}catch(Exception error){var runtime=store.ReadRecord<HeadlessRuntimeState>(RuntimeName(receipt.AccountKey));if(runtime.RuntimeId==receipt.RuntimeId){runtime.Diagnostic=Program.SafeError(error);runtime.UpdatedUtc=DateTime.UtcNow.ToString("o");store.WriteRecord(RuntimeName(receipt.AccountKey),runtime);}}
            }
        }
        private static bool Invalidate(RelayStore store,RelayChannel channel)
        {
            lock(RuntimeSync){Runtime runtime;string key=store.Root+"|"+channel.AccountKey;
                if(!Runtimes.TryGetValue(key,out runtime)||!RelayStore.SamePath(runtime.Home,channel.Home))return false;
                Runtimes.Remove(key);try{runtime.Dispose();}finally{
                    foreach(string path in Directory.GetFiles(store.Root,"headless-thread-*.dpapi")){var receipt=store.ReadRecord<HeadlessThreadReceipt>(Path.GetFileName(path));if(receipt.AccountKey==runtime.Account&&RelayStore.SamePath(receipt.Home??"",runtime.Home)&&new[]{"sending","running","uncertain"}.Contains(receipt.State)){receipt.State="uncertain";receipt.UpdatedUtc=DateTime.UtcNow.ToString("o");store.WriteRecord(Path.GetFileName(path),receipt);}}
                    try{new HeadlessApprovals(store).CancelOwned(runtime.Account,runtime.Home);}catch(IOException){}
                    store.WriteRecord(RuntimeName(runtime.Account),new HeadlessRuntimeState{AccountKey=runtime.Account,Home=runtime.Home,RuntimeId=runtime.Id,Running=false,Revoked=true,UpdatedUtc=DateTime.UtcNow.ToString("o")});
                }return true;
            }
        }
        private void Release(RelayChannel channel,Runtime runtime)
        {
            lock(RuntimeSync){runtime.Users--;if(runtime.Users>0||runtime.Active.Count>0)return;Runtime current;string key=store.Root+"|"+channel.AccountKey;if(Runtimes.TryGetValue(key,out current)&&ReferenceEquals(current,runtime)){Runtimes.Remove(key);runtime.Dispose();store.WriteRecord(RuntimeName(channel.AccountKey),new HeadlessRuntimeState{AccountKey=channel.AccountKey,Home=channel.Home,RuntimeId=runtime.Id,Running=false,UpdatedUtc=DateTime.UtcNow.ToString("o")});}}
        }
        private static string Sandbox(object result)
        {string kind=Json.Str(Json.Get(Json.Get(result,"sandbox"),"type"));switch(kind){case "workspaceWrite":return "workspace-write";case "readOnly":return "read-only";case "dangerFullAccess":return "danger-full-access";default:return kind;}}
        private static void VerifyContext(RelayChannel channel,object result)
        {if(Json.Str(Json.Get(result,"approvalPolicy"))!=channel.HeadlessApprovalPolicy||Sandbox(result)!=channel.HeadlessSandbox||!RelayStore.SamePath(Json.Str(Json.Get(result,"cwd")),channel.Workspace))throw new InvalidOperationException("Codex n'a pas confirmé les permissions ou le dossier demandés. Aucun mandat transmis.");}
        private async Task<object> Read(RelayChannel channel,Runtime runtime,HeadlessThreadReceipt receipt,object args,CancellationToken token)
        {
            var read=await runtime.Rpc.Call("thread/read",new{threadId=receipt.ThreadId,includeTurns=true},token).ConfigureAwait(false);
            Verify(channel);
            var thread=Json.Get(read,"thread");if(Json.Str(Json.Get(thread,"id"))!=receipt.ThreadId)throw new InvalidOperationException("Conversation native non confirmée.");
            var turns=RelayEngine.Rows(Json.Get(thread,"turns")).Select(value=>Json.Obj(value)).ToArray();
            foreach(var turn in turns){var items=RelayEngine.Rows(Json.Get(turn,"items")).Select(value=>Json.Obj(value)).ToArray();var last=items.LastOrDefault(value=>Json.Str(Json.Get(value,"type"))=="agentMessage");if(last!=null&&Json.Str(Json.Get(last,"phase"))==""&&Json.Str(Json.Get(turn,"status"))=="completed")last["phase"]="final_answer";turn["items"]=items;}
            if(String.IsNullOrEmpty(receipt.TurnId)&&!String.IsNullOrEmpty(receipt.PromptHash)){
                var matches=turns.Where(turn=>RelayEngine.Rows(Json.Get(turn,"items")).Any(item=>Json.Str(Json.Get(item,"type"))=="userMessage"&&RemotePeers.Hash(String.Join("",RelayEngine.Rows(Json.Get(item,"content")).Where(part=>Json.Str(Json.Get(part,"type"))=="text").Select(part=>Json.Str(Json.Get(part,"text")))))==(receipt.WirePromptHash??receipt.PromptHash))).ToArray();
                if(matches.Length==1){receipt.TurnId=Json.Str(Json.Get(matches[0],"id"));Save(receipt);}
            }
            var latest=turns.LastOrDefault(turn=>Json.Str(Json.Get(turn,"id"))==receipt.TurnId);string status=latest==null?null:Json.Str(Json.Get(latest,"status"));
            if(new[]{"completed","failed","interrupted"}.Contains(status)){receipt.State=status;lock(RuntimeSync)runtime.Active.Remove(receipt.ThreadId);Save(receipt);}
            else if(receipt.RuntimeId!=runtime.Id&&new[]{"sending","running","uncertain"}.Contains(receipt.State)){receipt.State="uncertain";Save(receipt);throw new HeadlessRuntimeLostException();}
            var pending=new HeadlessApprovals(store).Pending().Where(value=>value.ThreadId==receipt.ThreadId&&value.AccountKey==channel.AccountKey).ToArray();
            if(pending.Any(value=>value.Method=="item/fileChange/requestApproval"&&!value.DetailsVerified))foreach(var turn in turns)foreach(var item in RelayEngine.Rows(Json.Get(turn,"items")))new HeadlessApprovals(store).ObserveFileChange(receipt.ThreadId,Json.Str(Json.Get(turn,"id")),item);
            object nativeStatus=Json.Get(thread,"status");string nativeType=Json.Str(Json.Get(nativeStatus,"type"));if(String.IsNullOrEmpty(nativeType))nativeType=new[]{"created","completed","failed","interrupted"}.Contains(receipt.State)?"idle":"active";
            string[] flags=pending.Select(value=>value.Method=="item/tool/requestUserInput"?"waitingOnUserInput":"waitingOnApproval").Distinct().ToArray();
            flags=flags.Concat(RelayEngine.Rows(Json.Get(nativeStatus,"activeFlags")).Select(Json.Str)).Distinct().ToArray();var info=Json.Obj(thread);info.Remove("turns");info["status"]=new{type=nativeType,activeFlags=flags};
            return Shape(new{thread=info,turns=turns,page=new{nextCursor=(string)null}});
        }
        public async Task<object> Call(RelayChannel channel,string tool,object arguments,CancellationToken token)
        {
            Verify(channel);var args=Json.Read<object>(Json.Write(arguments));
            if(tool=="list_threads")return Shape(new{threads=Directory.GetFiles(store.Root,"headless-thread-*.dpapi").Select(path=>store.ReadRecord<HeadlessThreadReceipt>(Path.GetFileName(path))).Where(value=>value.ChannelId==channel.Id&&value.AccountKey==channel.AccountKey).Select(value=>new{threadId=value.ThreadId,title=value.Title}).ToArray(),pinnedThreads=new object[0]});
            if(!new[]{"create_thread","send_message_to_thread","read_thread","interrupt_thread"}.Contains(tool))throw new InvalidOperationException("Cette opération exige l'interface Codex ou n'est pas disponible sur le moteur autonome.");
            string thread=Json.Str(Json.Get(args,"threadId"));HeadlessThreadReceipt receipt=tool=="create_thread"?null:RequireOwned(store,channel,thread);
            if(tool=="read_thread"){
                var owner=store.ReadRecord<HeadlessRuntimeState>(RuntimeName(channel.AccountKey));using(var current=Process.GetCurrentProcess())if(owner.Running&&owner.WorkerPid!=current.Id)return await HeadlessSourceBroker.Read(store,channel,thread,token).ConfigureAwait(false);
            }
            if(tool=="create_thread"||tool=="send_message_to_thread")VerifySend(channel,tool=="create_thread"?null:thread);
            var runtime=Connection(channel,token);bool entered=false;
            try{
                await runtime.Gate.WaitAsync(token).ConfigureAwait(false);entered=true;
                Verify(channel);
                if(tool=="read_thread")return await Read(channel,runtime,receipt,args,token).ConfigureAwait(false);
                if(tool=="interrupt_thread"){
                    if(receipt.RuntimeId!=runtime.Id&&new[]{"sending","running","uncertain"}.Contains(receipt.State))throw new HeadlessRuntimeLostException();
                    string expected=Json.Str(Json.Get(args,"turnId"));if(String.IsNullOrEmpty(receipt.TurnId)||(expected.Length>0&&expected!=receipt.TurnId))throw new InvalidOperationException("Le tour à interrompre n'est pas celui de cette mission.");
                    if(!receipt.InterruptSent){await runtime.Rpc.Call("turn/interrupt",new{threadId=receipt.ThreadId,turnId=receipt.TurnId},token).ConfigureAwait(false);receipt.InterruptSent=true;Save(receipt);}
                    return Shape(new{requested=true,threadId=receipt.ThreadId,turnId=receipt.TurnId});
                }
                if(tool=="create_thread"){
                    var start=new Dictionary<string,object>{{"cwd",channel.Workspace},{"sandbox",channel.HeadlessSandbox},{"approvalPolicy",channel.HeadlessApprovalPolicy},{"historyMode","legacy"},{"ephemeral",false}};string model=Json.Str(Json.Get(args,"model"));if(model.Length>0)start["model"]=model;
                    var created=await runtime.Rpc.Call("thread/start",start,token).ConfigureAwait(false);VerifyContext(channel,created);thread=Json.Str(Json.Get(Json.Get(created,"thread"),"id"));ReceiptName(thread);
                    receipt=new HeadlessThreadReceipt{ThreadId=thread,ChannelId=channel.Id,AccountKey=channel.AccountKey,Home=channel.Home,Workspace=channel.Workspace,Sandbox=channel.HeadlessSandbox,ApprovalPolicy=channel.HeadlessApprovalPolicy,Title=Json.Str(Json.Get(args,"title")),State="created",RuntimeId=runtime.Id};Save(receipt);runtime.Loaded.Add(thread);
                }else{
                    if(new[]{"sending","running","uncertain"}.Contains(receipt.State))throw new InvalidOperationException("Le mandat est déjà envoyé ou son résultat reste à vérifier. Aucun renvoi automatique.");
                    if(!runtime.Loaded.Contains(thread)){var resumed=await runtime.Rpc.Call("thread/resume",new{threadId=thread,cwd=channel.Workspace,sandbox=channel.HeadlessSandbox,approvalPolicy=channel.HeadlessApprovalPolicy,excludeTurns=true},token).ConfigureAwait(false);VerifyContext(channel,resumed);if(Json.Str(Json.Get(Json.Get(resumed,"thread"),"id"))!=thread)throw new InvalidOperationException("La reprise n'a pas confirmé sa conversation.");runtime.Loaded.Add(thread);}
                }
                VerifySend(channel,receipt.ThreadId);string prompt=Json.Str(Json.Get(args,"prompt"));if(String.IsNullOrWhiteSpace(prompt))throw new InvalidOperationException("Mandat vide.");
                var integration=RelayIntegration.Status(store,channel.Home);bool integrationCurrent=integration.Healthy&&!String.IsNullOrEmpty(integration.InstalledVersion)&&integration.InstalledVersion.StartsWith(RelayWorker.Version+"+codex.",StringComparison.Ordinal);string integrationNote=integrationCurrent?"Si tu utilises report_result, un outil partagé ou une sous-délégation et que l'outil MCP bind_headless_chat est disponible, appelle-le d'abord avec threadId="+thread+", puis utilise la session retournée.":"L'intégration MCP du relais n'a pas pu être vérifiée pour cette version. Ne l'installe pas toi-même : rapporte ce blocage si tu en as besoin et rends ton résultat final dans ce chat ; il sera récupéré. Diagnostic : "+HeadlessApprovals.SafeSummary(integration.Status??"non vérifiée");
                string wirePrompt=prompt+"\n\n[CREEZIO_HEADLESS_CHAT:"+thread+"]\nContexte technique du relais pour CE chat créé par Account Switcher. "+integrationNote+" Aucune commande shell de connexion ni élévation n'est nécessaire. Cet identifiant seul ne donne aucun droit ; le relais vérifie le moteur, le compte et le chat natif. Ne copie pas une session d'un autre chat.";
                receipt.State="sending";receipt.RuntimeId=runtime.Id;receipt.TurnId=null;receipt.ClientUserMessageId=Guid.NewGuid().ToString("N");receipt.PromptHash=RemotePeers.Hash(prompt);receipt.WirePromptHash=RemotePeers.Hash(wirePrompt);receipt.InterruptSent=false;Save(receipt);lock(RuntimeSync)runtime.Active.Add(thread);
                try{var started=await runtime.Rpc.Call("turn/start",new{threadId=thread,input=new[]{new{type="text",text=wirePrompt}},approvalPolicy=channel.HeadlessApprovalPolicy,clientUserMessageId=receipt.ClientUserMessageId},token).ConfigureAwait(false);receipt.TurnId=Json.Str(Json.Get(Json.Get(started,"turn"),"id"));if(String.IsNullOrEmpty(receipt.TurnId))throw new InvalidOperationException("Le service n'a pas confirmé le tour.");receipt.State="running";Save(receipt);Verify(channel);return Shape(new{threadId=thread,turnId=receipt.TurnId});}
                catch{receipt.State="uncertain";Save(receipt);throw;}
            }catch(HeadlessRpcException error){Invalidate(store,channel);var lost=new HeadlessRuntimeLostException(error);var state=store.ReadRecord<HeadlessRuntimeState>(RuntimeName(channel.AccountKey));if(state.RuntimeId==runtime.Id){state.Diagnostic=Program.SafeError(lost);store.WriteRecord(RuntimeName(channel.AccountKey),state);}throw lost;}
            catch(ObjectDisposedException){Invalidate(store,channel);throw new HeadlessRuntimeLostException();}
            finally{if(entered)runtime.Gate.Release();Release(channel,runtime);}
        }
    }
}
