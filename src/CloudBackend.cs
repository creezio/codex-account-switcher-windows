using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // This is the desktop 26.1007.2314.0 backend protocol, not a public OpenAI API.
    // Keep it separate from the legacy Codex CLI cloud provider.
    internal sealed class CloudBackendBinding
    {
        public string AccountKey { get; set; }
        public string AccountId { get; set; }
        public string UserId { get; set; }
    }
    internal sealed class CloudRepository
    {
        public string RepositoryId { get; set; }
        public string Ref { get; set; }
        public int? Depth { get; set; }
    }
    internal sealed class CloudNetworkPolicy
    {
        public string Type { get; set; }
        public List<string> Presets { get; set; }
        public List<string> EgressHosts { get; set; }
    }
    internal sealed class CloudConfigurationDraft
    {
        public string Id { get; set; }
        public string BaseVersionId { get; set; }
        public int Revision { get; set; }
        public List<CloudRepository> Repositories { get; set; }
        public CloudNetworkPolicy NetworkPolicy { get; set; }
        public string InstallScript { get; set; }
        public string Cwd { get; set; }
        public string StartSkill { get; set; }
    }
    internal sealed class CloudConfiguration
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool? IsOwner { get; set; }
        public string ShareSettings { get; set; }
        public string Status { get; set; }
        public string VersionId { get; set; }
        public int? VersionRevision { get; set; }
        public string LatestReadyVersionId { get; set; }
        public string EnvironmentId { get; set; }
        public string ThreadId { get; set; }
        public List<CloudRepository> Repositories { get; set; }
        public CloudNetworkPolicy NetworkPolicy { get; set; }
        public string InstallScript { get; set; }
        public string Cwd { get; set; }
        public string StartSkill { get; set; }
        public CloudConfigurationDraft Draft { get; set; }
    }
    internal sealed class CloudConfigurationPage
    {
        public List<CloudConfiguration> Data { get; set; }
        public string NextCursor { get; set; }
    }
    internal sealed class CloudConfigurationCreate
    {
        public string Name { get; set; }
        public List<CloudRepository> Repositories { get; set; }
        public CloudNetworkPolicy NetworkPolicy { get; set; }
        public string ShareSettings { get; set; }
        public string InstallScript { get; set; }
        public string Cwd { get; set; }
        public string StartSkill { get; set; }
        public bool StartOnboarding { get; set; }
        public CloudConfigurationCreate() { ShareSettings = "private"; }
    }
    internal sealed class CloudDraftPatch
    {
        public string BaseVersionId { get; set; }
        public int ExpectedRevision { get; set; }
        public List<CloudRepository> Repositories { get; set; }
        public CloudNetworkPolicy NetworkPolicy { get; set; }
        public string InstallScript { get; set; }
        public string Cwd { get; set; }
        public string StartSkill { get; set; }
    }
    internal sealed class CloudDraft
    {
        public string DraftId { get; set; }
        public string EnvironmentId { get; set; }
        public string ThreadId { get; set; }
    }
    internal sealed class CloudOperation
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string EnvironmentId { get; set; }
        public string State { get; set; }
        public string FailureCode { get; set; }
        public string FailureMessage { get; set; }
    }
    internal class CloudBackendException : InvalidOperationException
    {
        public string Code { get; private set; }
        public int? HttpStatus { get; private set; }
        public string RequestId { get; private set; }
        public string ResponseMediaType { get; private set; }
        public bool EdgeChallenge { get; private set; }
        public CloudBackendException(string code, string message, int? status = null, string requestId = null, string responseMediaType = null, bool edgeChallenge = false)
            : base(message + (status.HasValue ? " (HTTP " + status.Value.ToString(CultureInfo.InvariantCulture) + ")" : "") + (edgeChallenge ? " · vérification interactive signalée par le service" : "") + (requestId == null ? "" : " · requête " + requestId))
        { Code = code; HttpStatus = status; RequestId = requestId; ResponseMediaType = responseMediaType; EdgeChallenge = edgeChallenge; }
    }
    internal sealed class CloudBackendUncertainException : CloudBackendException
    {
        public CloudBackendUncertainException(string reason, int? status = null, string requestId = null)
            : base("uncertain", "Le résultat de la modification Cloud est incertain (" + reason + "). Relisez son état avant toute nouvelle tentative.", status, requestId) { }
    }

    internal sealed class CloudBackend : IDisposable
    {
        public const string ProtocolVersion = "26.1007.2314.0";
        public const string Origin = "https://codex-cloud-backend.chatgpt.com";
        private const int MaximumResponseBytes = 2097152;
        private readonly HttpClient client;
        private readonly Func<AuthIdentity> readIdentity;
        internal Action<CancellationToken> RefreshReadAuthentication {get;set;}
        private readonly CloudBackendBinding binding;
        private bool ownsClient;
        private bool disposed;

        // Injection is for tests; production must use Create, which disables redirects.
        public CloudBackend(HttpClient client, Func<AuthIdentity> readIdentity, CloudBackendBinding binding)
        {
            if (client == null || readIdentity == null || binding == null) throw new ArgumentNullException();
            if (String.IsNullOrWhiteSpace(binding.AccountKey) || String.IsNullOrWhiteSpace(binding.AccountId) || String.IsNullOrWhiteSpace(binding.UserId)) throw Invalid("Identité Cloud attendue manquante.");
            this.client = client; this.readIdentity = readIdentity;
            this.binding = new CloudBackendBinding { AccountKey = binding.AccountKey, AccountId = binding.AccountId, UserId = binding.UserId };
        }
        public static CloudBackend Create(Func<AuthIdentity> readIdentity, CloudBackendBinding binding)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
#if !NETCOREAPP
            // csc's Framework executable has no recent TargetFrameworkAttribute. Its
            // legacy TLS defaults can otherwise refuse the Cloud HTTPS endpoint.
            // Scope TLS 1.2 to this handler when the installed Framework exposes it.
            var sslProtocols = typeof(HttpClientHandler).GetProperty("SslProtocols");
            bool configured = false;
            if (sslProtocols != null && sslProtocols.CanWrite)
            {
                try { sslProtocols.SetValue(handler, System.Security.Authentication.SslProtocols.Tls12, null); configured = true; }
                catch (System.Reflection.TargetInvocationException) { }
                catch (NotSupportedException) { }
            }
            if (!configured) System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            var http = new HttpClient(handler); http.Timeout = Timeout.InfiniteTimeSpan;
            try { var value = new CloudBackend(http, readIdentity, binding); value.ownsClient = true; return value; }
            catch { http.Dispose(); throw; }
        }
        public static CloudBackendBinding Capture(AuthIdentity identity)
        {
            if (identity == null || String.IsNullOrWhiteSpace(identity.Key) || String.IsNullOrWhiteSpace(identity.AccountId)) throw Authentication();
            var claims = AccessClaims(identity.AccessToken);
            var auth = Json.Get(claims, "https://api.openai.com/auth");
            string account = Required(auth, "chatgpt_account_id"), user = Required(auth, "chatgpt_user_id");
            double? expiry = Json.Number(Json.Get(claims, "exp"));
            if (account != identity.AccountId || !expiry.HasValue || expiry.Value <= (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds) throw Authentication();
            return new CloudBackendBinding { AccountKey = identity.Key, AccountId = account, UserId = user };
        }
        public Task<CloudConfigurationPage> List(string scope, string cursor, CancellationToken token)
        {
            if (scope != "user" && scope != "workspace") throw Invalid("Périmètre Cloud invalide.");
            if (cursor != null && (cursor.Length == 0 || cursor.Length > 4096 || cursor.Any(Char.IsControl))) throw Invalid("Curseur Cloud invalide.");
            return Request(HttpMethod.Get, "/v1/environment-configs?scope=" + scope + "&limit=100&omitDraft=true" + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), null, ParsePage, token);
        }
        public Task<CloudConfiguration> Get(string configId, CancellationToken token) { return Get(configId, null, token); }
        public Task<CloudConfiguration> Get(string configId, string draftId, CancellationToken token)
        { return Request(HttpMethod.Get, ConfigPath(configId, draftId), null, o => ParseConfiguration(o, false, configId, draftId), token); }
        public Task<CloudConfiguration> Create(CloudConfigurationCreate value, CancellationToken token)
        {
            if (value == null) throw Invalid("Configuration Cloud manquante.");
            string name = Name(value.Name), sharing = Sharing(value.ShareSettings);
            var repositories = Json.Write(RepositoriesBody(value.Repositories));
            var body = new Dictionary<string, object> { { "name", name }, { "repositories", Json.Read<object>(repositories) }, { "network_policy", NetworkBody(value.NetworkPolicy) }, { "share_settings", sharing }, { "start_onboarding", value.StartOnboarding } };
            Optional(body, "install_script", value.InstallScript, 262144); Optional(body, "cwd", WorkingDirectory(value.Cwd), 4096); Optional(body, "start_skill", value.StartSkill, 262144);
            return Request(HttpMethod.Post, "/v1/environment-configs", body, o => { var result = ParseConfiguration(o, false, null, null); if (result.Name != name || result.ShareSettings != sharing || !SameRepositories(Json.Read<object>(repositories), result.Repositories)) throw InvalidResponse(); return result; }, token);
        }
        public Task<CloudConfiguration> Rename(string configId, string name, CancellationToken token)
        { string expected = Name(name); return Request(new HttpMethod("PATCH"), ConfigPath(configId, null), new { name = expected }, o => { var result = ParseConfiguration(o, false, configId, null); if (result.Name != expected) throw InvalidResponse(); return result; }, token); }
        public Task<CloudDraft> CreateDraft(string configId, bool startThread, CancellationToken token)
        {
            return Request(HttpMethod.Post, ConfigPath(configId, null) + "/drafts", new { start_thread = startThread }, o => new CloudDraft { DraftId = Id(Required(o, "draft_id")), EnvironmentId = Required(o, "environment_id"), ThreadId = OptionalString(o, "thread_id") }, token);
        }
        public Task<CloudConfiguration> PatchDraft(string configId, string draftId, CloudDraftPatch value, CancellationToken token)
        {
            if (draftId == null || value == null || value.ExpectedRevision < 0) throw Invalid("Version du brouillon Cloud manquante.");
            var body = new Dictionary<string, object> { { "base_version_id", Nonempty(value.BaseVersionId, 512) }, { "expected_revision", value.ExpectedRevision } };
            if (value.Repositories != null) body["repositories"] = RepositoriesBody(value.Repositories);
            if (value.NetworkPolicy != null) body["network_policy"] = NetworkBody(value.NetworkPolicy);
            Optional(body, "install_script", value.InstallScript, 262144); Optional(body, "cwd", WorkingDirectory(value.Cwd), 4096); Optional(body, "start_skill", value.StartSkill, 262144);
            if (body.Count == 2) throw Invalid("Aucune modification du brouillon Cloud.");
            return Request(new HttpMethod("PATCH"), ConfigPath(configId, draftId), body, o => ParseConfiguration(o, false, configId, draftId), token);
        }
        public Task<CloudConfiguration> BootstrapInitialDraft(string configId, string baseVersionId, List<CloudRepository> repositories, CancellationToken token)
        {
            // Native first-publication flow uses the implicit /draft, not /drafts.
            // Its optimistic base is pinned; the response supplies the revision for approval.
            var body = new { base_version_id = Nonempty(baseVersionId, 512), repositories = RepositoriesBody(repositories) };
            return Request(new HttpMethod("PATCH"), ConfigPath(configId, null) + "/draft", body, o => ParseConfiguration(o, false, configId, null), token);
        }
        public Task<CloudOperation> BeginApproval(string configId, string draftId, int expectedRevision, string idempotencyKey, CancellationToken token)
        {
            if (expectedRevision < 0) throw Invalid("Révision Cloud invalide.");
            string path = ApprovalPath(configId, draftId) + "/begin";
            return Request(HttpMethod.Post, path, new { expected_revision = expectedRevision, idempotency_key = Nonempty(idempotencyKey, 128) }, ParseOperation, token);
        }
        public Task<CloudOperation> ReadOperation(string operationId, CancellationToken token)
        {
            return Request(HttpMethod.Get, "/v1/environment-operations/" + Id(operationId), null, o => { var result = ParseOperation(o); if (result.Id != operationId) throw InvalidResponse(); return result; }, token);
        }
        public Task<CloudConfiguration> CompleteApproval(string configId, string draftId, string operationId, string threadId, CancellationToken token)
        {
            var body = new Dictionary<string, object> { { "operation_id", Id(operationId) } };
            if (threadId != null) body["thread_id"] = Nonempty(threadId, 512);
            return Request(HttpMethod.Post, ApprovalPath(configId, draftId) + "/complete", body, o => ParseConfiguration(o, false, configId, null), token);
        }

        private AuthIdentity Current()
        {
            AuthIdentity identity; CloudBackendBinding observed;
            try { identity = readIdentity(); observed = Capture(identity); }
            catch { throw Authentication(); }
            if (observed.AccountKey != binding.AccountKey || observed.AccountId != binding.AccountId || observed.UserId != binding.UserId) throw Authentication();
            return identity;
        }
        private Task<T> Request<T>(HttpMethod method, string path, object body, Func<object, T> parse, CancellationToken token)
        {return RequestCore(method,path,body,parse,token,true);}
        private async Task<T> RequestCore<T>(HttpMethod method, string path, object body, Func<object, T> parse, CancellationToken token,bool mayRefresh)
        {
            if (disposed) throw new ObjectDisposedException("CloudBackend");
            bool mutation = method != HttpMethod.Get, sent = false; int? status = null; string requestId = null;
            token.ThrowIfCancellationRequested();
            var auth = Current();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(method, new Uri(Origin + path)))
            {
                // Native approval completion and draft preparation can take up to
                // ten minutes. The caller can still cancel sooner; never retry.
                bool preparing = method == HttpMethod.Post && path.StartsWith("/v1/environment-configs/", StringComparison.Ordinal) && !path.EndsWith("/begin", StringComparison.Ordinal);
                timeout.CancelAfter(TimeSpan.FromSeconds(preparing ? 600 : 30));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
                request.Headers.Add("ChatGPT-Account-Id", binding.AccountId);
                request.Headers.Add("X-OpenAI-Product-Sku", "codex");
                request.Headers.Add("originator", "creezio_account_switcher");
                request.Headers.UserAgent.ParseAdd("creezio-switcher-cloud-prototype/0.18.0-beta.1");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body != null) request.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                try
                {
                    Current(); token.ThrowIfCancellationRequested(); sent = true;
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        status = (int)response.StatusCode; requestId = SafeRequestId(response);
                        Current();
                        if (status >= 300 && status < 400) throw new CloudBackendException("redirect", "La redirection du service Cloud a été refusée.", status, requestId);
                        if (response.RequestMessage != null && response.RequestMessage.RequestUri != request.RequestUri) throw new CloudBackendException("redirect", "La destination de la réponse Cloud a changé.", status, requestId);
                        if (!response.IsSuccessStatusCode) {
                            if(status==401&&!mutation&&mayRefresh&&RefreshReadAuthentication!=null){RefreshReadAuthentication(token);return await RequestCore(method,path,body,parse,token,false).ConfigureAwait(false);}
                            string media=MediaType(response); bool challenge=IsEdgeChallenge(response);
                            if(status==403&&media=="text/html")throw new CloudBackendException("unexpected-html", "Le service Cloud a refusé cet appel et renvoyé une page HTML. Le motif concernant les droits du compte n'est pas confirmé ; aucun renvoi automatique.",status,requestId,media,challenge);
                            throw HttpFailure(status.Value, requestId, media, challenge);
                        }
                        object data;
                        try
                        {
                            string text = await ReadBody(response, timeout.Token).ConfigureAwait(false);
                            data = Json.Read<object>(text);
                            Current();
                            return parse(data);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (CloudBackendException e) { if (e.Code == "authentication" || e.Code == "invalid-response") throw; throw InvalidResponse(); }
                        catch { throw InvalidResponse(); }
                    }
                }
                catch (CloudBackendUncertainException) { throw; }
                catch (CloudBackendException e)
                {
                    if (mutation && sent && ((e.Code == "authentication" && !e.HttpStatus.HasValue) || e.Code == "invalid-response" || e.HttpStatus == 408 || e.HttpStatus >= 500)) throw new CloudBackendUncertainException(e.Code, status, requestId);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    if (mutation && sent) throw new CloudBackendUncertainException("requête interrompue", status, requestId);
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    throw new CloudBackendException("timeout", "Le service Cloud n'a pas répondu dans le délai prévu.");
                }
                catch (Exception)
                {
                    if (mutation && sent) throw new CloudBackendUncertainException("connexion interrompue", status, requestId);
                    throw new CloudBackendException("unavailable", "Connexion au service Cloud impossible.");
                }
            }
        }
        private static async Task<string> ReadBody(HttpResponseMessage response, CancellationToken token)
        {
            if (response.Content == null || response.Content.Headers.ContentLength > MaximumResponseBytes) throw InvalidResponse();
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var bytes = new byte[8192]; int count;
                while ((count = await input.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false)) > 0)
                { if (output.Length + count > MaximumResponseBytes) throw InvalidResponse(); output.Write(bytes, 0, count); }
                token.ThrowIfCancellationRequested(); return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }
        private static CloudConfigurationPage ParsePage(object value)
        {
            if (!Json.Obj(value).ContainsKey("next_cursor")) throw InvalidResponse();
            var data = Array(Json.Get(value, "data")).Select(o => ParseConfiguration(o, true, null, null)).ToList();
            if (data.Count > 100) throw InvalidResponse();
            string cursor = OptionalString(value, "next_cursor");
            if (cursor != null && (cursor.Length == 0 || cursor.Length > 4096 || cursor.Any(Char.IsControl))) throw InvalidResponse();
            return new CloudConfigurationPage { Data = data, NextCursor = cursor };
        }
        private static CloudConfiguration ParseConfiguration(object value, bool list, string expectedId, string expectedDraftId)
        {
            string id = Id(Required(value, "id"));
            if (expectedId != null && id != expectedId) throw InvalidResponse();
            var result = new CloudConfiguration {
                Id = id, Name = Required(value, "name"), IsOwner = OptionalBool(value, "is_owner"), ShareSettings = Sharing(Required(value, "share_settings")),
                VersionId = Required(value, "version_id"), VersionRevision = OptionalInt(value, "version_revision", 1),
                // The list schema has no runtime/publication status; only Get can prove it.
                Status = list ? null : Required(value, "status"), LatestReadyVersionId = OptionalString(value, "latest_ready_version_id"),
                EnvironmentId = OptionalString(value, "environment_id"), ThreadId = OptionalString(value, "thread_id"),
                Repositories = ParseRepositories(Json.Get(value, "repositories")),
                NetworkPolicy = list ? null : ParseNetwork(Json.Get(value, "network_policy")),
                InstallScript = OptionalString(value, "install_script"), Cwd = OptionalString(value, "cwd"), StartSkill = OptionalString(value, "start_skill")
            };
            var draft = Json.Get(value, "draft");
            if (draft != null)
            {
                result.Draft = new CloudConfigurationDraft { Id = Id(Required(draft, "id")), BaseVersionId = Required(draft, "base_version_id"), Revision = RequiredInt(draft, "revision", 0), Repositories = ParseRepositories(Json.Get(draft, "repositories")), NetworkPolicy = ParseNetwork(Json.Get(draft, "network_policy")), InstallScript = OptionalString(draft, "install_script"), Cwd = OptionalString(draft, "cwd"), StartSkill = OptionalString(draft, "start_skill") };
            }
            if (expectedDraftId != null && (result.Draft == null || result.Draft.Id != expectedDraftId)) throw InvalidResponse();
            return result;
        }
        private static CloudOperation ParseOperation(object value)
        {
            var result = new CloudOperation { Id = Id(Required(value, "id")), Kind = Required(value, "kind"), EnvironmentId = Required(value, "environment_id"), State = Required(value, "state") };
            if (result.Kind != "APPROVE_ENVIRONMENT_CONFIG_DRAFT" || !new[] { "PENDING", "RUNNING", "SUCCEEDED", "FAILED" }.Contains(result.State)) throw InvalidResponse();
            var failure = Json.Get(value, "failure");
            string code = failure == null ? null : OptionalString(failure, "code");
            result.FailureCode = code != null && Regex.IsMatch(code, "^[A-Z_]{1,64}$") ? code : null;
            // Backend failure messages may contain commands, URLs or secrets. Never retain them.
            result.FailureMessage = failure == null ? null : "Le service Cloud a signalé un échec" + (result.FailureCode == null ? "." : " : " + result.FailureCode + ".");
            return result;
        }
        private static List<CloudRepository> ParseRepositories(object value)
        { return Array(value).Select(o => new CloudRepository { RepositoryId = Required(o, "repository_id"), Ref = Required(o, "ref"), Depth = OptionalInt(o, "depth", 1) }).ToList(); }
        private static CloudNetworkPolicy ParseNetwork(object value)
        {
            string type = Required(value, "type");
            if (!new[] { "disabled", "unrestricted", "restricted" }.Contains(type)) throw InvalidResponse();
            return new CloudNetworkPolicy { Type = type, Presets = OptionalArray(value, "presets").Select(StringValue).ToList(), EgressHosts = OptionalArray(value, "egress_rules").Select(o => Required(o, "host")).ToList() };
        }
        private static object RepositoriesBody(List<CloudRepository> values)
        {
            if (values == null || values.Count > 100 || values.Any(v => v == null)) throw Invalid("Dépôts Cloud invalides.");
            if (values.Select(v => v.RepositoryId).Distinct(StringComparer.Ordinal).Count() != values.Count) throw Invalid("Dépôt Cloud présent plusieurs fois.");
            return values.Select(v => { var item = new Dictionary<string, object> { { "repository_id", Nonempty(v.RepositoryId, 512) }, { "ref", Nonempty(v.Ref, 1024) } }; if (v.Depth.HasValue) { if (v.Depth < 1) throw Invalid("Profondeur Git invalide."); item["depth"] = v.Depth.Value; } return item; }).ToArray();
        }
        private static bool SameRepositories(object expected, List<CloudRepository> actual)
        {
            var wanted = ParseRepositories(expected);
            return wanted.Count == actual.Count && wanted.All(w => actual.Any(a => a.RepositoryId == w.RepositoryId && a.Ref == w.Ref && (!w.Depth.HasValue || a.Depth == w.Depth)));
        }
        private static object NetworkBody(CloudNetworkPolicy value)
        {
            if (value == null || !new[] { "disabled", "unrestricted", "restricted" }.Contains(value.Type)) throw Invalid("Règle réseau Cloud invalide.");
            var body = new Dictionary<string, object> { { "type", value.Type } };
            if (value.Type == "restricted")
            {
                if (value.Presets != null) { if (value.Presets.Any(p => p != "package_managers")) throw Invalid("Préréglage réseau Cloud invalide."); body["presets"] = value.Presets.ToArray(); }
                if (value.EgressHosts == null || value.EgressHosts.Count > 1000) throw Invalid("Destinations réseau Cloud manquantes.");
                body["egress_rules"] = value.EgressHosts.Select(h => new { host = Nonempty(h, 512) }).ToArray();
            }
            else if ((value.Presets != null && value.Presets.Count > 0) || (value.EgressHosts != null && value.EgressHosts.Count > 0)) throw Invalid("Destinations inattendues pour cette règle réseau.");
            return body;
        }
        private static object AccessClaims(string token)
        {
            try
            {
                if (String.IsNullOrEmpty(token) || token.Length > 65535 || !Regex.IsMatch(token, "^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$")) throw Authentication();
                string[] parts = token.Split('.'); if (parts.Length != 3) throw Authentication();
                string part = parts[1].Replace('-', '+').Replace('_', '/'); part = part.PadRight((part.Length + 3) / 4 * 4, '=');
                // Claims pin the intended account; the HTTPS backend validates the token signature.
                return Json.Read<object>(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(part)));
            }
            catch { throw Authentication(); }
        }
        private static string ConfigPath(string configId, string draftId)
        { return "/v1/environment-configs/" + Id(configId) + (draftId == null ? "" : "/drafts/" + Id(draftId)); }
        private static string ApprovalPath(string configId, string draftId)
        { return ConfigPath(configId, draftId) + (draftId == null ? "/draft/approve" : "/approve"); }
        private static string Id(string value)
        { if (value == null || value.Length > 512 || !Regex.IsMatch(value, "^[A-Za-z0-9_~-]+$")) throw Invalid("Identifiant Cloud invalide."); return value; }
        private static string Name(string value) { return Nonempty(value == null ? null : value.Trim(), 256); }
        private static string Sharing(string value)
        { if (value != "private" && value != "workspace") throw Invalid("Visibilité Cloud invalide."); return value; }
        private static string WorkingDirectory(string value)
        { if (value != null && (!value.StartsWith("/", StringComparison.Ordinal) || value.IndexOf('\0') >= 0)) throw Invalid("Dossier Cloud invalide."); return value; }
        private static string Nonempty(string value, int maximum)
        { if (String.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(Char.IsControl)) throw Invalid("Valeur Cloud invalide."); return value; }
        private static void Optional(Dictionary<string, object> body, string key, string value, int maximum)
        { if (value == null) return; if (value.Length > maximum || value.IndexOf('\0') >= 0) throw Invalid("Valeur Cloud trop longue ou invalide."); body[key] = value; }
        private static string StringValue(object value) { var text = value as string; if (text == null) throw InvalidResponse(); return text; }
        private static string Required(object value, string key) { var text = Json.Get(value, key) as string; if (String.IsNullOrEmpty(text)) throw InvalidResponse(); return text; }
        private static string OptionalString(object value, string key) { var field = Json.Get(value, key); if (field == null) return null; return StringValue(field); }
        private static bool? OptionalBool(object value, string key) { var field = Json.Get(value, key); if (field == null) return null; if (!(field is bool)) throw InvalidResponse(); return (bool)field; }
        private static int RequiredInt(object value, string key, int minimum) { var number = OptionalInt(value, key, minimum); if (!number.HasValue) throw InvalidResponse(); return number.Value; }
        private static int? OptionalInt(object value, string key, int minimum)
        { var field = Json.Get(value, key); if (field == null) return null; var number = Json.Number(field); if (!number.HasValue || number.Value < minimum || number.Value > Int32.MaxValue || number.Value != Math.Truncate(number.Value)) throw InvalidResponse(); return (int)number.Value; }
        private static IEnumerable<object> Array(object value) { var values = value as IEnumerable<object>; if (values == null) throw InvalidResponse(); return values; }
        private static IEnumerable<object> OptionalArray(object value, string key) { var field = Json.Get(value, key); return field == null ? new object[0] : Array(field); }
        private static string SafeRequestId(HttpResponseMessage response)
        { foreach (string header in new[] { "x-request-id", "x-openai-request-id", "traceparent", "cf-ray" }) { IEnumerable<string> values; if (response.Headers.TryGetValues(header, out values)) { string value = values.FirstOrDefault(); if (value != null && Regex.IsMatch(value, "^[A-Za-z0-9_.:-]{1,128}$")) return value; } } return null; }
        private static string MediaType(HttpResponseMessage response)
        { string type = response.Content == null || response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType; if(type!=null)type=type.ToLowerInvariant(); return new[] { "text/html", "application/json", "application/problem+json" }.Contains(type) ? type : type == null ? null : "other"; }
        private static bool IsEdgeChallenge(HttpResponseMessage response)
        { IEnumerable<string> values; return response.Headers.TryGetValues("cf-mitigated", out values) && values.Any(value => String.Equals(value, "challenge", StringComparison.OrdinalIgnoreCase)); }
        private static CloudBackendException HttpFailure(int status, string requestId, string responseMediaType, bool edgeChallenge)
        {
            string code = status == 401 ? "authentication" : status == 403 ? "forbidden" : status == 404 ? "not-found" : status == 409 ? "conflict" : status == 429 ? "rate-limit" : "http";
            string text = status == 401 ? "La connexion Cloud a expiré. Reconnectez ce compte dans Codex." : status == 403 ? "Ce compte n'est pas autorisé à effectuer cette opération Cloud." : status == 404 ? "Cette configuration Cloud est introuvable pour ce compte." : status == 409 ? "La configuration Cloud a changé. Relisez son état avant de modifier à nouveau." : status == 429 ? "La limite de requêtes Cloud est atteinte." : "Le service Cloud a refusé la requête.";
            return new CloudBackendException(code, text, status, requestId, responseMediaType, edgeChallenge);
        }
        private static CloudBackendException Authentication() { return new CloudBackendException("authentication", "Le compte Cloud actif ne correspond plus à l'instance sélectionnée, ou sa connexion a expiré."); }
        private static CloudBackendException Invalid(string message) { return new CloudBackendException("invalid-request", message); }
        private static CloudBackendException InvalidResponse() { return new CloudBackendException("invalid-response", "Réponse du service Cloud incompatible avec le protocole " + ProtocolVersion + "."); }
        public void Dispose() { if (disposed) return; disposed = true; if (ownsClient) client.Dispose(); }
    }
}
