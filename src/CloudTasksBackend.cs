using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class CloudTaskEnvironment
    {
        public string EnvironmentId { get; set; }
        public string EnvironmentConfigId { get; set; }
        public string Cwd { get; set; }
    }
    internal sealed class CloudTaskTurn
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Text { get; set; }
        public string ErrorCode { get; set; }
        public bool TextComplete { get; set; }
    }
    internal sealed class CloudTaskThread
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string VerifiedEnvironmentConfigId { get; set; }
        public string AccountKey { get; set; }
        public string Cwd { get; set; }
        public string Model { get; set; }
        public List<CloudTaskEnvironment> Environments { get; set; }
        public CloudTaskTurn LatestTurn { get; set; }
        public bool AwaitingApproval { get; set; }
        public bool AwaitingInput { get; set; }
    }
    internal sealed class CloudTaskPage
    {
        public List<CloudTaskThread> Data { get; set; }
        public string NextCursor { get; set; }
    }
    internal sealed class CloudTaskTool
    {
        public string Server { get; set; }
        public string ToolName { get; set; }
        public bool? ReadOnly { get; set; }
        public string Description { get; set; }
        public string[] ArgumentNames { get; set; }
        public string[] RequiredArguments { get; set; }
    }
    internal sealed class CloudToolInventory
    {
        public string AccountKey { get; set; }
        public string ThreadId { get; set; }
        public string ConfigurationId { get; set; }
        public List<CloudTaskTool> Tools { get; set; }
    }
    internal interface ICloudTasksInventoryConnection
    {
        Task<CloudToolInventory> InventoryTools(string threadId, CancellationToken token);
    }
    internal interface ICloudTaskTurnConnection
    {
        Task<CloudTaskTurn> ReadTurn(string threadId, string turnId, CancellationToken token);
    }
    internal sealed class CloudTaskReconciliation
    {
        public string AccountKey { get; set; }
        public string ThreadId { get; set; }
        public string ConfigurationId { get; set; }
        public CloudTaskTurn Turn { get; set; }
        public bool HistoryComplete { get; set; }
        public string Evidence { get; set; }
    }
    internal interface ICloudTaskReconciliationConnection
    {
        Task<CloudTaskReconciliation> FindSubmittedTurn(string threadId,string configurationId,string clientUserMessageId,string promptSha256,DateTime attemptedUtc,CancellationToken token);
    }
    // Only this transport can prove that its turn/start RPC was never entered.
    // Do not infer this fact from an empty history or a server-side refusal.
    internal sealed class CloudTaskNotDispatchedException : CloudBackendException
    {
        private static string ErrorCode(Exception error){var backend=error as CloudBackendException;return backend!=null&&Regex.IsMatch(backend.Code??"","^[a-z][a-z0-9-]{0,79}$")?backend.Code:"dispatch-not-sent";}
        public CloudTaskNotDispatchedException(Exception error) : base(ErrorCode(error),"Le contrôle préalable a échoué avant l'envoi de turn/start. Aucun prompt métier n'a été transmis par cette tentative.") { }
    }
    internal interface ICloudTasksConnection : IDisposable
    {
        Task Connect(CancellationToken token);
        Task<CloudTaskPage> List(string cursor, int limit, CancellationToken token);
        Task<CloudTaskThread> Start(string configId, string cwd, CancellationToken token);
        Task<CloudTaskThread> StartOnboarding(string configId, CancellationToken token);
        Task<CloudTaskThread> Read(string threadId, CancellationToken token);
        Task<CloudTaskTurn> StartTurn(string threadId, string prompt, CancellationToken token);
        Task<CloudTaskTurn> StartTurn(string threadId, string prompt, string clientUserMessageId, CancellationToken token);
        Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, CancellationToken token);
        Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, string clientUserMessageId, CancellationToken token);
    }
    internal interface ICloudOnboardingConnection : IDisposable
    {
        Task<CloudTaskThread> StartOnboarding(string configId, CancellationToken token);
        Task<CloudTaskThread> Read(string threadId, CancellationToken token);
        Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, string clientUserMessageId, IDictionary<string, string> repositoryNames, CancellationToken token);
    }
    internal interface ICloudTasksTransport : IDisposable
    {
        bool IsOpen { get; }
        Task Connect(string accessToken, string accountId, CancellationToken token);
        Task Send(string message, CancellationToken token);
        Task<string> Receive(CancellationToken token);
        void Abort();
    }
    internal sealed class CloudTaskWebSocket : ICloudTasksTransport
    {
        private ClientWebSocket socket;
        internal string Stage { get; private set; }
        public bool IsOpen { get { return socket != null && socket.State == WebSocketState.Open; } }
        public async Task Connect(string accessToken, string accountId, CancellationToken token)
        {
            Abort();
            Stage = "create";
            socket = new ClientWebSocket();
            Stage = "protocols";
            socket.Options.AddSubProtocol("codex-app-server");
            socket.Options.AddSubProtocol("codex-client.creezio-switcher");
            socket.Options.AddSubProtocol("openai-bearer." + accessToken);
            Stage = "header-account"; socket.Options.SetRequestHeader("ChatGPT-Account-Id", accountId);
            Stage = "header-product";
            socket.Options.SetRequestHeader("X-OpenAI-Product-Sku", "codex");
            Stage = "header-originator";
            socket.Options.SetRequestHeader("originator", "creezio_account_switcher");
#if NETCOREAPP
            Stage = "header-user-agent";
            socket.Options.SetRequestHeader("User-Agent", "creezio-switcher-cloud-prototype/0.18.0-beta.1");
#endif
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
#if !NETCOREAPP
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            Stage = "connect";
            try{await socket.ConnectAsync(new Uri("wss://codex-cloud-backend.chatgpt.com/"), token).ConfigureAwait(false);}
            catch(Exception error){if(Unauthorized(error))throw new CloudBackendException("connection-authentication","La connexion Cloud a refusé les identifiants avant tout appel métier.");throw;}
            Stage = "connected";
        }
        private static bool Unauthorized(Exception error)
        {
            for(int index=0;error!=null&&index<5;index++,error=error.InnerException){
                var web=error as System.Net.WebException;var response=web==null?null:web.Response as System.Net.HttpWebResponse;
                if(response!=null&&(int)response.StatusCode==401)return true;
                var property=error.GetType().GetProperty("StatusCode");
                if(property!=null){try{var value=property.GetValue(error,null);if(value!=null&&Convert.ToInt32(value,System.Globalization.CultureInfo.InvariantCulture)==401)return true;}catch{}}
            }
            return false;
        }
        public async Task Send(string message, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
        public async Task<string> Receive(CancellationToken token)
        {
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[8192]; WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token).ConfigureAwait(false);
                    if(result.MessageType!=WebSocketMessageType.Text){Stage=result.MessageType==WebSocketMessageType.Close?"frame-close":"frame-nontext";throw new IOException("Invalid Cloud WebSocket frame.");}
                    if(buffer.Length+result.Count>4194304){Stage="frame-too-large";throw new IOException("Invalid Cloud WebSocket frame.");}
                    buffer.Write(bytes, 0, result.Count);
                } while (!result.EndOfMessage);
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }
        public void Abort() { var old = socket; socket = null; if (old != null) { old.Abort(); old.Dispose(); } }
        public void Dispose() { Abort(); }
    }

    // Native desktop backend adapter; no CLI cloud, browser automation, or protected IPC.
    internal sealed class CloudTasksBackend : ICloudTasksConnection, ICloudOnboardingConnection, ICloudTasksInventoryConnection, ICloudTasksExecutionConnection, ICloudTaskTurnConnection, ICloudTaskReconciliationConnection
    {
        public const string ProtocolVersion = CloudBackend.ProtocolVersion;
        private readonly ICloudTasksTransport transport;
        private readonly Func<AuthIdentity> readIdentity;
        internal Action<CancellationToken> RefreshConnectionAuthentication {get;set;}
        private readonly CloudBackendBinding binding;
        private readonly Func<string, CancellationToken, Task<CloudConfiguration>> readConfiguration;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, CloudTaskThread> observed = new Dictionary<string, CloudTaskThread>(StringComparer.Ordinal);
        private readonly HashSet<string> pendingApprovals = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> pendingInputs = new HashSet<string>(StringComparer.Ordinal);
        private bool initialized, disposed;
        private int sequence;
        private long attentionSequence;

        public CloudTasksBackend(ICloudTasksTransport transport, Func<AuthIdentity> readIdentity, CloudBackendBinding binding, Func<string, CancellationToken, Task<CloudConfiguration>> readConfiguration)
        {
            if (transport == null || readIdentity == null || binding == null || String.IsNullOrWhiteSpace(binding.AccountKey) || String.IsNullOrWhiteSpace(binding.AccountId) || String.IsNullOrWhiteSpace(binding.UserId)) throw new ArgumentException("Association Cloud incomplète.");
            this.transport = transport; this.readIdentity = readIdentity; this.readConfiguration = readConfiguration;
            this.binding = new CloudBackendBinding { AccountKey = binding.AccountKey, AccountId = binding.AccountId, UserId = binding.UserId };
        }
        public static CloudTasksBackend Create(Func<AuthIdentity> readIdentity, CloudBackendBinding binding,Action<CancellationToken> refreshAuthentication=null)
        {
            var captured = new CloudBackendBinding { AccountKey = binding.AccountKey, AccountId = binding.AccountId, UserId = binding.UserId };
            return new CloudTasksBackend(new CloudTaskWebSocket(), readIdentity, captured, async (id, token) => { using (var client = CloudBackend.Create(readIdentity, captured)){client.RefreshReadAuthentication=refreshAuthentication;return await client.Get(id, token).ConfigureAwait(false);} }){RefreshConnectionAuthentication=refreshAuthentication};
        }
        public async Task Connect(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { using (var timeout = Deadline(token, 30)) await EnsureConnected(timeout.Token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        public async Task<CloudTaskPage> List(string cursor, int limit, CancellationToken token)
        {
            if (limit < 1 || limit > 100 || (cursor != null && (cursor.Length == 0 || cursor.Length > 4096 || cursor.Any(Char.IsControl)))) throw Invalid("Pagination Cloud invalide.");
            var result = await Rpc("thread/list", new { cursor = cursor, limit = limit }, false, token).ConfigureAwait(false);
            var page = new CloudTaskPage { Data = Rows(Json.Get(result, "data")).Select(ParseThread).ToList(), NextCursor = Optional(result, "nextCursor") };
            if (page.Data.Count > limit) throw Incompatible();
            return page;
        }
        public async Task<CloudToolInventory> InventoryTools(string threadId, CancellationToken token)
        {
            Id(threadId);
            var thread = await Read(threadId, token).ConfigureAwait(false);
            if (thread.Id != threadId || thread.AccountKey != binding.AccountKey || String.IsNullOrEmpty(thread.VerifiedEnvironmentConfigId)) throw Incompatible();
            var result = new CloudToolInventory { AccountKey = binding.AccountKey, ThreadId = threadId, ConfigurationId = thread.VerifiedEnvironmentConfigId, Tools = new List<CloudTaskTool>() };
            var cursors = new HashSet<string>(StringComparer.Ordinal); var toolKeys = new HashSet<string>(StringComparer.Ordinal); string cursor = null;
            do
            {
                var page = await Rpc("mcpServerStatus/list", new { cursor = cursor, limit = 100, detail = "toolsAndAuthOnly", threadId = threadId }, false, token).ConfigureAwait(false);
                var servers = Rows(Json.Get(page, "data")).ToArray();
                if (servers.Length > 100) throw Incompatible();
                foreach (var server in servers)
                {
                    string name = MetadataName(Required(server, "name"));
                    foreach (var entry in Json.Obj(Json.Get(server, "tools")))
                    {
                        string tool = MetadataName(entry.Key);
                        if (!toolKeys.Add(name + "\n" + tool) || result.Tools.Count >= 10000) throw Incompatible();
                        var description = Optional(entry.Value, "description");
                        if (description != null && description.Length > 1000) description = description.Substring(0, 1000);
                        var hint = Json.Get(Json.Get(entry.Value, "annotations"), "readOnlyHint");
                        if (hint != null && !(hint is bool)) throw Incompatible();
                        var schema = Json.Get(entry.Value, "inputSchema");
                        var required = Json.Get(schema, "required");
                        var requiredNames = required == null ? new string[0] : Rows(required).Select(value => MetadataName(value as string)).ToArray();
                        var arguments = Json.Obj(Json.Get(schema, "properties")).Keys.Select(MetadataName).ToArray();
                        if (arguments.Length > 256 || requiredNames.Length > 256 || requiredNames.Any(value => !arguments.Contains(value))) throw Incompatible();
                        result.Tools.Add(new CloudTaskTool { Server = name, ToolName = tool, ReadOnly = hint as bool?, Description = Redact(description), ArgumentNames = arguments, RequiredArguments = requiredNames });
                    }
                }
                cursor = Optional(page, "nextCursor");
                if (cursor != null && (cursor.Length == 0 || cursor.Length > 4096 || cursor.Any(Char.IsControl) || !cursors.Add(cursor) || cursors.Count > 50)) throw Incompatible();
            } while (cursor != null);
            Current(); return result;
        }
        public Task<CloudTaskThread> Start(string configId, string cwd, CancellationToken token)
        { return StartCore(configId, cwd, false, null, token); }
        public Task<CloudTaskThread> Start(string configId, string cwd, CloudTaskExecutionOptions options, CancellationToken token)
        { return StartCore(configId, cwd, false, options, token); }
        public Task<CloudTaskThread> StartOnboarding(string configId, CancellationToken token)
        { return StartCore(configId, "/workspace", true, null, token); }
        private async Task<CloudTaskThread> StartCore(string configId, string cwd, bool onboarding, CloudTaskExecutionOptions options, CancellationToken token)
        {
            Id(configId);
            options = CloudTaskExecutionOptions.Validate(options);
            if (cwd != null && (cwd.Length > 4096 || !cwd.StartsWith("/", StringComparison.Ordinal) || cwd.Any(Char.IsControl))) throw Invalid("Dossier Cloud invalide.");
            Current();
            if (readConfiguration == null) throw Invalid("La vérification de l'environnement Cloud n'est pas configurée.");
            var config = await readConfiguration(configId, token).ConfigureAwait(false); Current();
            if (config == null || config.Id != configId || !config.VersionRevision.HasValue) throw Invalid("Configuration Cloud non confirmée.");
            if (onboarding)
            {
                if (config.VersionRevision.Value != 1 || config.IsOwner != true) throw Invalid("La préparation initiale exige une nouvelle configuration Cloud appartenant à ce compte.");
            }
            else if (config.VersionRevision.Value <= 1 || String.IsNullOrWhiteSpace(config.LatestReadyVersionId) || !new[] { "ready", "pending", "failed" }.Contains(config.Status))
                throw Invalid("Publiez et vérifiez cet environnement Cloud dans Codex avant de lancer une mission.");
            if (options.Required) await ValidateExecutionModel(options, null, token).ConfigureAwait(false);
            var environment = new Dictionary<string, object> { { onboarding ? "onboardingConfigId" : "environmentConfigId", configId } };
            if (!onboarding && cwd != null) environment["cwd"] = cwd;
            var body = new Dictionary<string, object> { { "serviceName", "codex_cloud" }, { "ephemeral", false }, { "threadSource", "user" }, { "environments", new[] { environment } }, { "deferredEnvironment", true }, { "pluginsMcp", new { productSku = "codex" } } };
            ApplyExecution(body, options, false);
            var result = await Rpc("thread/start", body, true, token).ConfigureAwait(false);
            try
            {
                var thread = ParseThread(Json.Get(result, "thread"));
                if (thread.VerifiedEnvironmentConfigId != null && thread.VerifiedEnvironmentConfigId != configId) throw Incompatible();
                return thread;
            }
            catch (CloudBackendException) { throw new CloudBackendUncertainException("conversation créée sans confirmation de son environnement"); }
        }
        public async Task<CloudTaskThread> Read(string threadId, CancellationToken token)
        {
            Id(threadId);
            long attentionBefore = attentionSequence;
            var result = await Rpc("thread/read", new { threadId = threadId, includeTurns = false }, false, token).ConfigureAwait(false);
            var nativeFlags = Json.Get(Json.Get(Json.Get(result, "thread"), "status"), "activeFlags");
            bool nativeAttention = nativeFlags != null && Rows(nativeFlags).Any(flag => Object.Equals(flag, "waitingOnApproval") || Object.Equals(flag, "waitingOnUserInput"));
            var thread = ParseThread(Json.Get(result, "thread"));
            if (thread.Id != threadId) throw Incompatible();
            var turns = await Rpc("thread/turns/list", new { threadId = threadId, cursor = (string)null, limit = 1, itemsView = "notLoaded", sortDirection = "desc" }, false, token).ConfigureAwait(false);
            var latest = Rows(Json.Get(turns, "data")).FirstOrDefault();
            if (latest != null)
            {
                var turn = ParseTurn(latest);
                await ReadTurnText(threadId,turn,token).ConfigureAwait(false);
                thread.LatestTurn = turn;
            }
            if (!nativeAttention && attentionSequence == attentionBefore && thread.Status == "idle" && thread.LatestTurn != null && new[] { "completed", "failed", "interrupted" }.Contains(thread.LatestTurn.Status))
            { pendingApprovals.Remove(thread.Id); pendingInputs.Remove(thread.Id); thread.AwaitingApproval = false; thread.AwaitingInput = false; }
            observed[thread.Id] = thread;
            return thread;
        }
        public async Task<CloudTaskTurn> ReadTurn(string threadId, string turnId, CancellationToken token)
        {
            Id(threadId); Id(turnId);
            CloudTaskThread previous; observed.TryGetValue(threadId, out previous);
            string expectedConfiguration = previous == null ? null : previous.VerifiedEnvironmentConfigId;
            try
            {
                var context = await ReadTurnContext(threadId, expectedConfiguration, token).ConfigureAwait(false);
                expectedConfiguration = context.VerifiedEnvironmentConfigId;
                // A later follow-up must never replace the result of the admitted
                // turn. Search only bounded native history, without resuming work.
                string cursor = null; var cursors = new HashSet<string>(StringComparer.Ordinal);
                var ids = new HashSet<string>(StringComparer.Ordinal); CloudTaskTurn target = null;
                for (int pageIndex = 0; pageIndex < 4 && target == null; pageIndex++)
                {
                    var page = await Rpc("thread/turns/list", new { threadId = threadId, cursor = cursor, limit = 25, itemsView = "notLoaded", sortDirection = "desc" }, false, token).ConfigureAwait(false);
                    var turns = Rows(Json.Get(page, "data")).ToArray();
                    if (turns.Length > 25) throw Incompatible();
                    foreach (var row in turns)
                    {
                        var turn = ParseTurn(row);
                        if (!ids.Add(turn.Id)) throw Incompatible();
                        if (turn.Id == turnId) target = turn;
                    }
                    if (target != null) break;
                    cursor = Optional(page, "nextCursor");
                    if (cursor == null) break;
                    if (cursor.Length == 0 || cursor.Length > 4096 || cursor.Any(Char.IsControl) || !cursors.Add(cursor)) throw Incompatible();
                }
                if (target != null) await ReadTurnText(threadId, target, token).ConfigureAwait(false);
                await ReadTurnContext(threadId, expectedConfiguration, token).ConfigureAwait(false);
                return target;
            }
            catch
            {
                if (previous != null) observed[threadId] = previous; else observed.Remove(threadId);
                throw;
            }
        }
        private async Task<CloudTaskThread> ReadTurnContext(string threadId, string expectedConfiguration, CancellationToken token)
        {
            var result = await Rpc("thread/read", new { threadId = threadId, includeTurns = false }, false, token).ConfigureAwait(false);
            var thread = ParseThread(Json.Get(result, "thread"));
            if (thread.Id != threadId || thread.AccountKey != binding.AccountKey || String.IsNullOrEmpty(thread.VerifiedEnvironmentConfigId) || expectedConfiguration != null && thread.VerifiedEnvironmentConfigId != expectedConfiguration) throw Incompatible();
            return thread;
        }
        internal static string PromptSha256(string text)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text??""))).Replace("-","").ToLowerInvariant();}
        public async Task<CloudTaskReconciliation> FindSubmittedTurn(string threadId,string configurationId,string clientUserMessageId,string promptSha256,DateTime attemptedUtc,CancellationToken token)
        {
            Id(threadId);Id(configurationId);
            if(clientUserMessageId==null||!Regex.IsMatch(clientUserMessageId,"^[A-Za-z0-9_-]{1,128}$")||promptSha256==null||!Regex.IsMatch(promptSha256,"^[0-9a-f]{64}$"))throw Invalid("Preuve du message Cloud invalide.");
            var before=await ReadTurnContext(threadId,configurationId,token).ConfigureAwait(false);
            var result=new CloudTaskReconciliation{AccountKey=before.AccountKey,ThreadId=threadId,ConfigurationId=configurationId};
            string cursor=null;bool complete=false;int itemPages=0;var cursors=new HashSet<string>(StringComparer.Ordinal);var ids=new HashSet<string>(StringComparer.Ordinal);var matches=new List<CloudTaskTurn>();string evidence=null;
            // A bounded or empty history never proves non-delivery. Compare
            // only native user input, in memory; never assistant/tool quotations.
            for(int pageIndex=0;pageIndex<4;pageIndex++){
                var page=await Rpc("thread/turns/list",new{threadId=threadId,cursor=cursor,limit=25,itemsView="notLoaded",sortDirection="desc"},false,token).ConfigureAwait(false);
                var turns=Rows(Json.Get(page,"data")).ToArray();if(turns.Length>25)throw Incompatible();
                foreach(var row in turns){
                    var turn=ParseTurn(row);if(!ids.Add(turn.Id))throw Incompatible();
                    var started=Json.Number(Json.Get(row,"startedAt"));
                    if(started.HasValue){var earliest=(attemptedUtc.ToUniversalTime().AddMinutes(-5)-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;if(started.Value<earliest)continue;}
                    string itemCursor=null;var itemCursors=new HashSet<string>(StringComparer.Ordinal);var itemIds=new HashSet<string>(StringComparer.Ordinal);bool itemsComplete=false;bool matched=false;string matchEvidence=null;
                    for(int itemPage=0;itemPage<10&&itemPages<40;itemPage++){
                        itemPages++;
                        var items=await ReadItemsPage(threadId,turn.Id,itemCursor,token).ConfigureAwait(false);
                        var rows=Rows(Json.Get(items,"data")).ToArray();if(rows.Length>10)throw Incompatible();
                        foreach(var itemRow in rows){
                            var item=Json.Get(itemRow,"item")??itemRow;string rowTurn=Optional(itemRow,"turnId");
                            if((Json.Get(itemRow,"item")!=null||rowTurn!=null)&&rowTurn!=turn.Id)throw Incompatible();
                            string itemId=Optional(item,"id");if(itemId!=null&&!itemIds.Add(itemId))throw Incompatible();
                            if(Optional(item,"type")!="userMessage")continue;
                            string clientId=Optional(item,"clientId");var content=Rows(Json.Get(item,"content")).ToArray();
                            if(content.Length!=1||Optional(content[0],"type")!="text"||Object.Equals(Json.Get(item,"truncated"),true))continue;
                            string text=Optional(content[0],"text");bool sameHash=text!=null&&PromptSha256(text)==promptSha256;
                            if(clientId==clientUserMessageId&&!sameHash)throw new CloudBackendException("reconciliation-conflict","L'identifiant du message existe avec un contenu différent. Aucun rapprochement ni nouvel envoi autorisé.");
                            if(sameHash&&(clientId==null||clientId==clientUserMessageId)){if(matched)throw Incompatible();matched=true;matchEvidence=clientId==null?"exact-prompt-sha256":"client-id-and-prompt-sha256";}
                        }
                        itemCursor=Optional(items,"nextCursor");if(itemCursor==null){itemsComplete=true;break;}
                        if(itemCursor.Length==0||itemCursor.Length>4096||itemCursor.Any(Char.IsControl)||!itemCursors.Add(itemCursor))throw Incompatible();
                    }
                    if(!itemsComplete){await ReadTurnContext(threadId,configurationId,token).ConfigureAwait(false);return result;}
                    if(matched){matches.Add(turn);evidence=matchEvidence;if(matches.Count>1)throw new CloudBackendException("reconciliation-conflict","Plusieurs tours portent le même message. Aucun rapprochement ni nouvel envoi autorisé.");}
                }
                cursor=Optional(page,"nextCursor");if(cursor==null){complete=true;break;}
                if(cursor.Length==0||cursor.Length>4096||cursor.Any(Char.IsControl)||!cursors.Add(cursor))throw Incompatible();
            }
            await ReadTurnContext(threadId,configurationId,token).ConfigureAwait(false);
            result.HistoryComplete=complete;if(complete&&matches.Count==1){result.Turn=matches[0];result.Evidence=evidence;}
            return result;
        }
        private async Task<object> ReadItemsPage(string threadId,string turnId,string cursor,CancellationToken token)
        {
            try{return await Rpc("thread/items/list",new{threadId=threadId,turnId=turnId,limit=10,sortDirection="desc",cursor=cursor},false,token).ConfigureAwait(false);}
            catch(CloudBackendException error){
                if(error.Code!="response-too-large")throw;
            }
            // A read can safely retry the same cursor with one item. Never
            // increase the frame limit or replay a business mutation.
            return await Rpc("thread/items/list",new{threadId=threadId,turnId=turnId,limit=1,sortDirection="desc",cursor=cursor},false,token).ConfigureAwait(false);
        }
        private async Task ReadTurnText(string threadId, CloudTaskTurn turn, CancellationToken token)
        {
            string cursor = null; var cursors = new HashSet<string>(StringComparer.Ordinal);
            var itemIds = new HashSet<string>(StringComparer.Ordinal); var parts = new List<string>();
            int length = 0; bool truncated = false, complete = false;
            for (int pageIndex = 0; pageIndex < 100; pageIndex++)
            {
                var page = await ReadItemsPage(threadId,turn.Id,cursor,token).ConfigureAwait(false);
                var rows = Rows(Json.Get(page, "data")).ToArray();
                if (rows.Length > 10) throw Incompatible();
                foreach (var row in rows)
                {
                    var item = Json.Get(row, "item") ?? row;
                    string rowTurn = Optional(row, "turnId");
                    if ((Json.Get(row, "item") != null || rowTurn != null) && rowTurn != turn.Id) throw Incompatible();
                    string itemId = Optional(item, "id");
                    if (itemId != null && !itemIds.Add(itemId)) throw Incompatible();
                    if (Optional(item, "type") != "agentMessage" || !new[] { "final", "final_answer" }.Contains(Optional(item, "phase"))) continue;
                    string text = Optional(item, "text");
                    if (text != null) { parts.Add(text); length += text.Length; }
                    if (Object.Equals(Json.Get(item, "truncated"), true)) truncated = true;
                }
                cursor = Optional(page, "nextCursor");
                if (cursor == null) { complete = true; break; }
                if (cursor.Length == 0 || cursor.Length > 4096 || cursor.Any(Char.IsControl) || !cursors.Add(cursor)) throw Incompatible();
                if (length > 250000) { truncated = true; break; }
            }
            parts.Reverse(); string final = String.Join("\n\n", parts);
            if (final.Length > 250000) { final = final.Substring(0, 250000); truncated = true; }
            turn.Text = Redact(final); turn.TextComplete = complete && !truncated;
        }
        public Task<CloudTaskTurn> StartTurn(string threadId, string prompt, CancellationToken token)
        { return StartTurn(threadId, prompt, Guid.NewGuid().ToString("N"), token); }
        public Task<CloudTaskTurn> StartTurn(string threadId, string prompt, string clientUserMessageId, CancellationToken token)
        { return SendTurn(threadId, prompt, clientUserMessageId, null, false, null, token); }
        public Task<CloudTaskTurn> StartTurn(string threadId, string prompt, string clientUserMessageId, CloudTaskExecutionOptions options, CancellationToken token)
        { return SendTurn(threadId, prompt, clientUserMessageId, null, false, options, token); }
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, CancellationToken token)
        { return StartOnboardingTurn(threadId, prompt, Guid.NewGuid().ToString("N"), null, token); }
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, string clientUserMessageId, CancellationToken token)
        { return StartOnboardingTurn(threadId, prompt, clientUserMessageId, null, token); }
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, string clientUserMessageId, IDictionary<string, string> repositoryNames, CancellationToken token)
        { return SendTurn(threadId, prompt, clientUserMessageId, repositoryNames, true, null, token); }
        private async Task<CloudTaskTurn> SendTurn(string threadId, string prompt, string clientUserMessageId, IDictionary<string, string> repositoryNames, bool onboarding, CloudTaskExecutionOptions options, CancellationToken token)
        {
            bool rpcEntered=false;
            try{
            Id(threadId);
            options = CloudTaskExecutionOptions.Validate(options);
            if (String.IsNullOrWhiteSpace(prompt) || prompt.Length > 250000 || prompt.IndexOf('\0') >= 0) throw Invalid("Mission Cloud vide ou trop longue.");
            if (clientUserMessageId == null || clientUserMessageId.Length > 128 || !Regex.IsMatch(clientUserMessageId, "^[A-Za-z0-9_-]+$")) throw Invalid("Identifiant du message Cloud invalide.");
            if (onboarding && repositoryNames == null) throw Invalid("Les noms GitHub vérifiés des dépôts sont requis pour préparer cet environnement.");
            var thread = await Read(threadId, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(thread.VerifiedEnvironmentConfigId)) throw Invalid("L'environnement de cette conversation Cloud n'est pas encore confirmé par le serveur. Relisez son état ; aucun prompt n'a été envoyé.");
            if (thread.AwaitingApproval || thread.AwaitingInput || thread.Status != "idle" || (thread.LatestTurn != null && new[] { "inProgress", "running" }.Contains(thread.LatestTurn.Status)))
                throw Invalid("Cette conversation Cloud travaille déjà ou attend une intervention dans Codex.");
            if (options.ReasoningEffort != null && options.Model == null && String.IsNullOrWhiteSpace(thread.Model))
                throw new CloudBackendException("execution-model-unverified", "Le modèle courant de ce chat Cloud n'est pas confirmé. Indiquez explicitement le modèle avant de choisir son raisonnement.");
            if (options.Required) await ValidateExecutionModel(options, thread.Model, token).ConfigureAwait(false);
            var inputs = new List<object>();
            if (onboarding)
            {
                if (readConfiguration == null) throw Invalid("Vérification de la configuration initiale manquante.");
                var config = await readConfiguration(thread.VerifiedEnvironmentConfigId, token).ConfigureAwait(false); Current();
                if (config == null || config.Id != thread.VerifiedEnvironmentConfigId || config.VersionRevision != 1 || config.IsOwner != true || config.Repositories == null || repositoryNames.Count != config.Repositories.Count)
                    throw Invalid("La configuration initiale ou le périmètre GitHub a changé. Relisez son état.");
                var repositories = config.Repositories.Select(repository =>
                {
                    string name;
                    if (!repositoryNames.TryGetValue(repository.RepositoryId, out name) || name == null || !Regex.IsMatch(name, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw Invalid("Nom GitHub du dépôt non vérifié.");
                    return new { name = name, @ref = repository.Ref };
                }).ToArray();
                inputs.Add(new { type = "text", text = Json.Write(new { repositories = repositories }) + "\n\n## My request:\n" + prompt, text_elements = new object[0] });
                inputs.Add(new { type = "text", text = "Use $cloud-environment-onboarding:setup to set up this cloud environment", text_elements = new object[0] });
            }
            else inputs.Add(new { type = "text", text = prompt, text_elements = new object[0] });
            var parameters = new Dictionary<string, object> { { "threadId", threadId }, { "clientUserMessageId", clientUserMessageId }, { "input", inputs.ToArray() } };
            ApplyExecution(parameters, options, true);
            if (onboarding) { parameters["turnTrigger"] = "environment_onboarding"; parameters["cwd"] = "/workspace"; parameters["runtimeWorkspaceRoots"] = new[] { "/workspace", "/tmp" }; }
            rpcEntered=true;var result = await Rpc("turn/start", parameters, true, token).ConfigureAwait(false);
            try { return ParseTurn(Json.Get(result, "turn")); }
            catch (CloudBackendException) { throw new CloudBackendUncertainException("message envoyé sans confirmation du tour"); }
            }catch(Exception error){if(!rpcEntered)throw new CloudTaskNotDispatchedException(error);throw;}
        }

        private static void ApplyExecution(IDictionary<string, object> parameters, CloudTaskExecutionOptions options, bool turn)
        {
            if (options.Model != null) parameters["model"] = options.Model;
            if (options.ReasoningEffort != null)
            {
                if (turn) parameters["effort"] = options.ReasoningEffort;
                else parameters["config"] = new Dictionary<string, object> { { "model_reasoning_effort", options.ReasoningEffort } };
            }
            // The deployed Cloud server supports this native legacy builder.
            // Custom profiles retain their exact profile ID and are never widened.
            if (options.PermissionProfile == ":danger-full-access")
            {
                if (turn) parameters["sandboxPolicy"] = new { type = "dangerFullAccess" };
                else parameters["sandbox"] = "danger-full-access";
                parameters["approvalPolicy"] = "never"; parameters["approvalsReviewer"] = "user";
            }
            else if (options.PermissionProfile != null) parameters["permissions"] = options.PermissionProfile;
        }
        private async Task ValidateExecutionModel(CloudTaskExecutionOptions options, string currentModel, CancellationToken token)
        {
            if (options.Model == null && options.ReasoningEffort == null) return;
            string requested = options.Model ?? currentModel, cursor = null;
            object selected = null; var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await Rpc("model/list", new { includeHidden = true, cursor = cursor, limit = 100 }, false, token).ConfigureAwait(false);
                var models = Rows(Json.Get(page, "data")).ToArray();
                if (models.Length > 100) throw Incompatible();
                foreach (var model in models)
                {
                    bool matches = requested == null ? Object.Equals(Json.Get(model, "isDefault"), true) : Optional(model, "model") == requested;
                    if (matches) { if (selected != null) throw Incompatible(); selected = model; }
                }
                cursor = Optional(page, "nextCursor");
                if (cursor != null && (cursor.Length == 0 || cursor.Length > 4096 || cursor.Any(Char.IsControl) || !cursors.Add(cursor) || cursors.Count > 50)) throw Incompatible();
            } while (cursor != null);
            if (selected == null) throw new CloudBackendException("execution-model-unavailable", "Le modèle demandé n'est pas disponible sur ce compte Cloud. Aucun autre modèle n'a été choisi.");
            if (options.ReasoningEffort != null)
            {
                var efforts = Json.Get(selected, "supportedReasoningEfforts");
                if (efforts == null || !Rows(efforts).Any(value => Optional(value, "reasoningEffort") == options.ReasoningEffort))
                    throw new CloudBackendException("execution-effort-unavailable", "Ce modèle Cloud ne propose pas le niveau de raisonnement demandé. Aucun niveau de remplacement n'a été choisi.");
            }
        }
        public async Task<CloudTaskExecutionObservation> ReadExecution(string threadId, CancellationToken token)
        {
            Id(threadId);
            CloudTaskThread prior;
            string expectedConfiguration = observed.TryGetValue(threadId, out prior) ? prior.VerifiedEnvironmentConfigId : null;
            // No requested setting is replayed here. A native resume supplies the
            // server's persisted settings, while deferred goals cannot start work.
            var result = await Rpc("thread/resume", new { threadId = threadId, excludeTurns = true, deferGoalContinuation = true }, true, token).ConfigureAwait(false);
            return ParseExecution(threadId, result, expectedConfiguration, prior);
        }
        public async Task<CloudTaskExecutionObservation> ConfigureExecution(string threadId, CloudTaskExecutionOptions options, CancellationToken token)
        {
            Id(threadId); options = CloudTaskExecutionOptions.Validate(options);
            if (!options.Required) throw Invalid("Aucun réglage Cloud explicite à appliquer.");
            var prior = await Read(threadId, token).ConfigureAwait(false);
            RequireUnstartedExecutionThread(prior);
            string configurationId = prior.VerifiedEnvironmentConfigId;
            if (readConfiguration == null) throw Invalid("La vérification de l'environnement Cloud n'est pas configurée.");
            var config = await readConfiguration(configurationId, token).ConfigureAwait(false); Current();
            if (config == null || config.Id != configurationId || !config.VersionRevision.HasValue || config.VersionRevision.Value <= 1 || String.IsNullOrEmpty(config.VersionId) || String.IsNullOrEmpty(config.LatestReadyVersionId) || config.Status != "ready" || config.Draft != null)
                throw Invalid("L'environnement de ce nouveau chat Cloud doit être publié, prêt et sans brouillon avant ses réglages d'exécution.");
            await ValidateExecutionModel(options, prior.Model, token).ConfigureAwait(false);
            // Discovery can take time. Recheck the untouched native thread rather
            // than changing settings on a chat that someone has started meanwhile.
            var fresh = await Read(threadId, token).ConfigureAwait(false);
            RequireUnstartedExecutionThread(fresh);
            if (fresh.VerifiedEnvironmentConfigId != configurationId) throw new CloudBackendException("execution-environment-changed", "L'environnement de ce nouveau chat Cloud a changé. Aucun réglage n'a été appliqué.");
            var parameters = new Dictionary<string, object> { { "threadId", threadId }, { "excludeTurns", true }, { "deferGoalContinuation", true } };
            ApplyExecution(parameters, options, false);
            var result = await Rpc("thread/resume", parameters, true, token).ConfigureAwait(false);
            return ParseExecution(threadId, result, configurationId, fresh);
        }
        private static void RequireUnstartedExecutionThread(CloudTaskThread thread)
        {
            if (thread == null || String.IsNullOrEmpty(thread.VerifiedEnvironmentConfigId) || thread.Status != "idle" || thread.AwaitingApproval || thread.AwaitingInput || thread.LatestTurn != null)
                throw Invalid("Les réglages initiaux exigent un nouveau chat Cloud au repos, sans aucun tour ni intervention native. Aucun chat déjà utilisé n'est modifié.");
        }
        private CloudTaskExecutionObservation ParseExecution(string threadId, object result, string expectedConfiguration, CloudTaskThread prior)
        {
            var thread = ParseThread(Json.Get(result, "thread"));
            if (thread.Id != threadId || String.IsNullOrEmpty(thread.VerifiedEnvironmentConfigId)) throw Incompatible();
            if (expectedConfiguration != null && thread.VerifiedEnvironmentConfigId != expectedConfiguration)
            {
                observed[threadId] = prior;
                throw new CloudBackendException("execution-environment-changed", "L'environnement de ce chat Cloud a changé pendant la relecture de ses réglages. Aucun nouveau prompt n'est autorisé.");
            }
            string model = Optional(result, "model"), effort = Optional(result, "reasoningEffort");
            var policyValue = Json.Get(result, "approvalPolicy");
            string policy = policyValue as string, reviewer = Optional(result, "approvalsReviewer");
            if (policyValue != null && policy == null && Json.Obj(policyValue).ContainsKey("granular")) policy = "granular";
            string profile = Optional(Json.Get(result, "activePermissionProfile"), "id");
            bool fullAccess = Optional(Json.Get(result, "sandbox"), "type") == "dangerFullAccess" && policy == "never" && reviewer == "user";
            if (profile == ":danger-full-access" && !fullAccess) profile = null;
            if (profile == null && fullAccess) profile = ":danger-full-access";
            return new CloudTaskExecutionObservation { Model = model, ReasoningEffort = effort, PermissionProfile = profile, ApprovalPolicy = policy, ApprovalsReviewer = reviewer, Verified = !String.IsNullOrWhiteSpace(model) };
        }

        private AuthIdentity Current()
        {
            try
            {
                var identity = readIdentity(); var current = CloudBackend.Capture(identity);
                if (current.AccountKey != binding.AccountKey || current.AccountId != binding.AccountId || current.UserId != binding.UserId) throw new InvalidOperationException();
                return identity;
            }
            catch { throw new CloudBackendException("authentication", "Le compte Cloud actif a changé ou sa connexion a expiré."); }
        }
        private async Task EnsureConnected(CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException("CloudTasksBackend");
            Current(); if (initialized && transport.IsOpen) return;
            initialized = false;
            try
            {
                var identity = Current();
                bool refreshConnection=false;
                try{await transport.Connect(identity.AccessToken, binding.AccountId, token).ConfigureAwait(false);}
                catch(CloudBackendException error){
                    if(error.Code!="connection-authentication"||RefreshConnectionAuthentication==null)throw;
                    refreshConnection=true;
                }
                if(refreshConnection){transport.Abort();RefreshConnectionAuthentication(token);identity=Current();await transport.Connect(identity.AccessToken,binding.AccountId,token).ConfigureAwait(false);}
                Current();
                string id = NextId();
                await transport.Send(Json.Write(new { id = id, method = "initialize", @params = new { clientInfo = new { name = "creezio_account_switcher", title = "Creezio Account Switcher", version = "0.18.0-beta.1" }, capabilities = new { experimentalApi = true, requestAttestation = false } } }), token).ConfigureAwait(false);
                var result = await Receive(id, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(Optional(result, "userAgent"))) throw Incompatible();
                await transport.Send(Json.Write(new { method = "initialized", @params = new { } }), token).ConfigureAwait(false); Current();
                initialized = true;
            }
            catch (CloudBackendException) { transport.Abort(); throw; }
            catch (OperationCanceledException) { transport.Abort(); throw; }
            catch (Exception error) { transport.Abort(); throw new CloudBackendException("unavailable", "Connexion au service natif Codex Cloud impossible. " + SafeDiagnostic(error, transport as CloudTaskWebSocket)); }
        }
        private async Task<object> Rpc(string method, object parameters, bool mutation, CancellationToken token)
        {
            try{await gate.WaitAsync(token).ConfigureAwait(false);}catch(Exception error){if(mutation&&method=="turn/start")throw new CloudTaskNotDispatchedException(error);throw;} bool sent = false;
            try
            {
                using (var timeout = Deadline(token, mutation ? 120 : 35))
                {
                    await EnsureConnected(timeout.Token).ConfigureAwait(false); Current(); timeout.Token.ThrowIfCancellationRequested();
                    string id = NextId(); sent = true;
                    await transport.Send(Json.Write(new { id = id, method = method, @params = parameters }), timeout.Token).ConfigureAwait(false);
                    var result = await Receive(id, timeout.Token).ConfigureAwait(false); Current(); return result;
                }
            }
            catch (CloudBackendUncertainException) { transport.Abort(); initialized = false; throw; }
            catch (CloudBackendException e)
            {
                if(mutation&&!sent&&method=="turn/start")throw new CloudTaskNotDispatchedException(e);
                if (mutation && sent && (e.Code == "authentication" || e.Code == "invalid-response")) { transport.Abort(); initialized = false; throw new CloudBackendUncertainException(e.Code); }
                throw;
            }
            catch (OperationCanceledException)
            {
                transport.Abort(); initialized = false;
                if (mutation && sent) throw new CloudBackendUncertainException("envoi interrompu");
                if(mutation&&method=="turn/start")throw new CloudTaskNotDispatchedException(new OperationCanceledException());
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                throw new CloudBackendException("timeout", "Le service natif Codex Cloud n'a pas répondu dans le délai prévu.");
            }
            catch(Exception error)
            {
                transport.Abort(); initialized = false;
                if (mutation && sent) throw new CloudBackendUncertainException("connexion interrompue");
                if(mutation&&method=="turn/start")throw new CloudTaskNotDispatchedException(error);
                var webSocket=transport as CloudTaskWebSocket;if(webSocket!=null&&webSocket.Stage=="frame-too-large")throw new CloudBackendException("response-too-large","La page native dépasse la limite mémoire de 4 Mio ; aucune donnée tronquée n'est considérée complète. méthode="+method+".");
                throw new CloudBackendException("unavailable", "La lecture de la mission Cloud a été interrompue. Vous pouvez relire son état. méthode="+method+" · "+SafeDiagnostic(error,transport as CloudTaskWebSocket));
            }
            finally { gate.Release(); }
        }
        private async Task<object> Receive(string expectedId, CancellationToken token)
        {
            for (int count = 0; count < 2000; count++)
            {
                string text = await transport.Receive(token).ConfigureAwait(false); Current();
                object message;
                try { message = Json.Read<object>(text); } catch { throw Incompatible(); }
                var id = Json.Get(message, "id"); string method = Optional(message, "method");
                if (method != null) { Observe(method, Json.Get(message, "params")); continue; }
                if (id == null || Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture) != expectedId) continue;
                var error = Json.Get(message, "error");
                if (error != null)
                {
                    string code = Convert.ToString(Json.Get(error, "code"), System.Globalization.CultureInfo.InvariantCulture);
                    if (code == null || !Regex.IsMatch(code, "^[A-Za-z0-9_.-]{1,64}$")) code = "unknown";
                    throw new CloudBackendException("rpc-" + code, "Le service natif Codex Cloud a refusé cette demande (code " + code + ").");
                }
                if (!Json.Obj(message).ContainsKey("result")) throw Incompatible();
                return Json.Get(message, "result");
            }
            throw Incompatible();
        }
        private void Observe(string method, object parameters)
        {
            string threadId = Optional(parameters, "threadId");
            if (threadId == null) return;
            if (method.EndsWith("requestApproval", StringComparison.Ordinal) && pendingApprovals.Count < 1000) { pendingApprovals.Add(threadId); attentionSequence++; }
            if (method.EndsWith("requestUserInput", StringComparison.Ordinal) && pendingInputs.Count < 1000) { pendingInputs.Add(threadId); attentionSequence++; }
            CloudTaskThread thread;
            if (!observed.TryGetValue(threadId, out thread)) return;
            // Requests for approval or user input remain unanswered. The Switcher
            // never grants native Codex permissions on behalf of the user.
            if (method.EndsWith("requestApproval", StringComparison.Ordinal)) thread.AwaitingApproval = true;
            if (method.EndsWith("requestUserInput", StringComparison.Ordinal)) thread.AwaitingInput = true;
            if (method == "thread/status/changed") ApplyStatus(thread, Json.Get(parameters, "status"));
        }
        private CloudTaskThread ParseThread(object value)
        {
            var environments = Json.Get(value, "environments");
            var result = new CloudTaskThread { Id = Id(Required(value, "id")), Name = Redact(Optional(value, "name")), Cwd = Optional(value, "cwd"), Model = Optional(value, "model"), AccountKey = binding.AccountKey, Environments = environments == null ? new List<CloudTaskEnvironment>() : Rows(environments).Select(o => new CloudTaskEnvironment { EnvironmentId = Optional(o, "environmentId"), EnvironmentConfigId = Optional(o, "environmentConfigId"), Cwd = Optional(o, "cwd") }).ToList() };
            var configs = result.Environments.Select(e => e.EnvironmentConfigId).Where(e => !String.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal).ToArray();
            if (configs.Length == 1 && result.Environments.Count == 1) result.VerifiedEnvironmentConfigId = Id(configs[0]);
            ApplyStatus(result, Json.Get(value, "status"));
            result.AwaitingApproval = result.AwaitingApproval || pendingApprovals.Contains(result.Id);
            result.AwaitingInput = result.AwaitingInput || pendingInputs.Contains(result.Id);
            var turns = Json.Get(value, "turns");
            if (turns != null) { var latest = Rows(turns).LastOrDefault(); if (latest != null) result.LatestTurn = ParseTurn(latest); }
            observed[result.Id] = result;
            return result;
        }
        private static void ApplyStatus(CloudTaskThread thread, object status)
        {
            thread.Status = status is string ? (string)status : Optional(status, "type");
            var flags = Json.Get(status, "activeFlags");
            if (flags != null)
            {
                var values = Rows(flags).Select(o => o as string).ToArray();
                thread.AwaitingApproval = values.Contains("waitingOnApproval"); thread.AwaitingInput = values.Contains("waitingOnUserInput");
            }
        }
        private static CloudTaskTurn ParseTurn(object value)
        {
            var error = Json.Get(value, "error");
            var errorInfo = error == null ? null : Json.Get(error, "codexErrorInfo");
            string code = errorInfo as string;
            if (code == null && Json.Obj(errorInfo).Count == 1) code = Json.Obj(errorInfo).Keys.First();
            return new CloudTaskTurn { Id = Id(Required(value, "id")), Status = Required(value, "status"), ErrorCode = code != null && Regex.IsMatch(code, "^[A-Za-z0-9_.-]{1,64}$") ? code : null, Text = "", TextComplete = false };
        }
        private string NextId() { return "creezio-cloud-" + Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture); }
        private static CancellationTokenSource Deadline(CancellationToken token, int seconds) { var source = CancellationTokenSource.CreateLinkedTokenSource(token); source.CancelAfter(TimeSpan.FromSeconds(seconds)); return source; }
        private static string Id(string value) { if (value == null || value.Length > 512 || !Regex.IsMatch(value, "^[A-Za-z0-9_~-]+$")) throw Invalid("Identifiant de mission Cloud invalide."); return value; }
        private static string MetadataName(string value) { if (String.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(Char.IsControl)) throw Incompatible(); return value; }
        private static string Required(object value, string key) { string text = Optional(value, key); if (String.IsNullOrWhiteSpace(text)) throw Incompatible(); return text; }
        private static string Optional(object value, string key) { var field = Json.Get(value, key); if (field == null) return null; var text = field as string; if (text == null) throw Incompatible(); return text; }
        private static IEnumerable<object> Rows(object value) { var result = value as IEnumerable<object>; if (result == null) throw Incompatible(); return result; }
        private static string Redact(string text)
        {
            if (text == null) return null;
            text = Regex.Replace(text, "(?i)\\bBearer\\s+[^\\s,;]+", "Bearer [redacted]");
            return Regex.Replace(text, "\\b(?:sk-[A-Za-z0-9_-]{8,}|gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+|tskey-[A-Za-z0-9_-]{8,}|eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+)\\b", "[redacted]");
        }
        private static CloudBackendException Invalid(string message) { return new CloudBackendException("invalid-request", message); }
        private static CloudBackendException Incompatible() { return new CloudBackendException("invalid-response", "Réponse du service natif Codex Cloud incompatible avec le protocole " + ProtocolVersion + "."); }
        private static string SafeDiagnostic(Exception error, CloudTaskWebSocket webSocket)
        {
            var parts = new List<string>();
            if (webSocket != null) parts.Add("phase=" + webSocket.Stage);
            for (int count = 0; error != null && count < 3; count++, error = error.InnerException)
            {
                parts.Add(error.GetType().Name + "/0x" + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                var ws = error as WebSocketException;
                if (ws != null) parts.Add("ws=" + ws.WebSocketErrorCode + "/native=" + ws.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var socket = error as System.Net.Sockets.SocketException;
                if (socket != null) parts.Add("socket=" + socket.SocketErrorCode);
                var web = error as System.Net.WebException;
                if (web != null) parts.Add("http=" + web.Status);
            }
            return String.Join(" · ", parts) + ".";
        }
        public void Dispose() { if (disposed) return; disposed = true; initialized = false; transport.Dispose(); }
    }
}
