using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
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
            socket.Options.SetRequestHeader("User-Agent", "creezio-switcher-cloud-prototype/0.17.2-beta.1");
#endif
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
#if !NETCOREAPP
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            Stage = "connect";
            await socket.ConnectAsync(new Uri("wss://codex-cloud-backend.chatgpt.com/"), token).ConfigureAwait(false);
            Stage = "connected";
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
                    if (result.MessageType != WebSocketMessageType.Text || buffer.Length + result.Count > 4194304) throw new IOException("Invalid Cloud WebSocket frame.");
                    buffer.Write(bytes, 0, result.Count);
                } while (!result.EndOfMessage);
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }
        public void Abort() { var old = socket; socket = null; if (old != null) { old.Abort(); old.Dispose(); } }
        public void Dispose() { Abort(); }
    }

    // Native desktop backend adapter; no CLI cloud, browser automation, or protected IPC.
    internal sealed class CloudTasksBackend : ICloudTasksConnection, ICloudOnboardingConnection, ICloudTasksInventoryConnection
    {
        public const string ProtocolVersion = CloudBackend.ProtocolVersion;
        private readonly ICloudTasksTransport transport;
        private readonly Func<AuthIdentity> readIdentity;
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
        public static CloudTasksBackend Create(Func<AuthIdentity> readIdentity, CloudBackendBinding binding)
        {
            var captured = new CloudBackendBinding { AccountKey = binding.AccountKey, AccountId = binding.AccountId, UserId = binding.UserId };
            return new CloudTasksBackend(new CloudTaskWebSocket(), readIdentity, captured, async (id, token) => { using (var client = CloudBackend.Create(readIdentity, captured)) return await client.Get(id, token).ConfigureAwait(false); });
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
        { return StartCore(configId, cwd, false, token); }
        public Task<CloudTaskThread> StartOnboarding(string configId, CancellationToken token)
        { return StartCore(configId, "/workspace", true, token); }
        private async Task<CloudTaskThread> StartCore(string configId, string cwd, bool onboarding, CancellationToken token)
        {
            Id(configId);
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
            var environment = new Dictionary<string, object> { { onboarding ? "onboardingConfigId" : "environmentConfigId", configId } };
            if (!onboarding && cwd != null) environment["cwd"] = cwd;
            var body = new { serviceName = "codex_cloud", ephemeral = false, threadSource = "user", environments = new[] { environment }, deferredEnvironment = true, pluginsMcp = new { productSku = "codex" } };
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
                var items = await Rpc("thread/items/list", new { threadId = threadId, turnId = turn.Id, limit = 100, sortDirection = "desc", cursor = (string)null }, false, token).ConfigureAwait(false);
                var rows = Rows(Json.Get(items, "data")).ToArray();
                var text = new List<string>(); bool truncated = false;
                foreach (var row in rows.Reverse())
                {
                    var item = Json.Get(row, "item") ?? row;
                    if (Json.Get(row, "item") != null && Optional(row, "turnId") != turn.Id) throw Incompatible();
                    if (Optional(item, "type") != "agentMessage" || !new[] { "final", "final_answer" }.Contains(Optional(item, "phase"))) continue;
                    string part = Optional(item, "text");
                    if (part != null) text.Add(part);
                    if (Object.Equals(Json.Get(item, "truncated"), true)) truncated = true;
                }
                string final = String.Join("\n\n", text);
                if (final.Length > 250000) { final = final.Substring(0, 250000); truncated = true; }
                turn.Text = Redact(final);
                turn.TextComplete = !truncated && Json.Get(items, "nextCursor") == null;
                thread.LatestTurn = turn;
            }
            if (!nativeAttention && attentionSequence == attentionBefore && thread.Status == "idle" && thread.LatestTurn != null && new[] { "completed", "failed", "interrupted" }.Contains(thread.LatestTurn.Status))
            { pendingApprovals.Remove(thread.Id); pendingInputs.Remove(thread.Id); thread.AwaitingApproval = false; thread.AwaitingInput = false; }
            observed[thread.Id] = thread;
            return thread;
        }
        public Task<CloudTaskTurn> StartTurn(string threadId, string prompt, CancellationToken token)
        { return StartTurn(threadId, prompt, Guid.NewGuid().ToString("N"), token); }
        public Task<CloudTaskTurn> StartTurn(string threadId, string prompt, string clientUserMessageId, CancellationToken token)
        { return SendTurn(threadId, prompt, clientUserMessageId, null, false, token); }
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, CancellationToken token)
        { return StartOnboardingTurn(threadId, prompt, Guid.NewGuid().ToString("N"), null, token); }
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, string clientUserMessageId, CancellationToken token)
        { return StartOnboardingTurn(threadId, prompt, clientUserMessageId, null, token); }
        public Task<CloudTaskTurn> StartOnboardingTurn(string threadId, string prompt, string clientUserMessageId, IDictionary<string, string> repositoryNames, CancellationToken token)
        { return SendTurn(threadId, prompt, clientUserMessageId, repositoryNames, true, token); }
        private async Task<CloudTaskTurn> SendTurn(string threadId, string prompt, string clientUserMessageId, IDictionary<string, string> repositoryNames, bool onboarding, CancellationToken token)
        {
            Id(threadId);
            if (String.IsNullOrWhiteSpace(prompt) || prompt.Length > 250000 || prompt.IndexOf('\0') >= 0) throw Invalid("Mission Cloud vide ou trop longue.");
            if (clientUserMessageId == null || clientUserMessageId.Length > 128 || !Regex.IsMatch(clientUserMessageId, "^[A-Za-z0-9_-]+$")) throw Invalid("Identifiant du message Cloud invalide.");
            if (onboarding && repositoryNames == null) throw Invalid("Les noms GitHub vérifiés des dépôts sont requis pour préparer cet environnement.");
            var thread = await Read(threadId, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(thread.VerifiedEnvironmentConfigId)) throw Invalid("L'environnement de cette conversation Cloud n'est pas encore confirmé par le serveur. Relisez son état ; aucun prompt n'a été envoyé.");
            if (thread.AwaitingApproval || thread.AwaitingInput || thread.Status != "idle" || (thread.LatestTurn != null && new[] { "inProgress", "running" }.Contains(thread.LatestTurn.Status)))
                throw Invalid("Cette conversation Cloud travaille déjà ou attend une intervention dans Codex.");
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
            if (onboarding) { parameters["turnTrigger"] = "environment_onboarding"; parameters["cwd"] = "/workspace"; parameters["runtimeWorkspaceRoots"] = new[] { "/workspace", "/tmp" }; }
            var result = await Rpc("turn/start", parameters, true, token).ConfigureAwait(false);
            try { return ParseTurn(Json.Get(result, "turn")); }
            catch (CloudBackendException) { throw new CloudBackendUncertainException("message envoyé sans confirmation du tour"); }
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
                await transport.Connect(identity.AccessToken, binding.AccountId, token).ConfigureAwait(false); Current();
                string id = NextId();
                await transport.Send(Json.Write(new { id = id, method = "initialize", @params = new { clientInfo = new { name = "creezio_account_switcher", title = "Creezio Account Switcher", version = "0.17.2-beta.1" }, capabilities = new { experimentalApi = true, requestAttestation = false } } }), token).ConfigureAwait(false);
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
            await gate.WaitAsync(token).ConfigureAwait(false); bool sent = false;
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
                if (mutation && sent && (e.Code == "authentication" || e.Code == "invalid-response")) { transport.Abort(); initialized = false; throw new CloudBackendUncertainException(e.Code); }
                throw;
            }
            catch (OperationCanceledException)
            {
                transport.Abort(); initialized = false;
                if (mutation && sent) throw new CloudBackendUncertainException("envoi interrompu");
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                throw new CloudBackendException("timeout", "Le service natif Codex Cloud n'a pas répondu dans le délai prévu.");
            }
            catch
            {
                transport.Abort(); initialized = false;
                if (mutation && sent) throw new CloudBackendUncertainException("connexion interrompue");
                throw new CloudBackendException("unavailable", "La lecture de la mission Cloud a été interrompue. Vous pouvez relire son état.");
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
