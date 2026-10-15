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
    internal sealed class CloudGitHubRepository
    {
        public string Id { get; set; }
        public string FullName { get; set; }
        public string DefaultBranch { get; set; }
        public bool? CanWrite { get; set; }
        public bool? Archived { get; set; }
    }
    internal sealed class CloudGitHubRepositoryPage
    {
        public List<CloudGitHubRepository> Data { get; set; }
        public int Page { get; set; }
        // A full page has no authoritative has-next flag in the native response.
        public bool? HasNextPage { get; set; }
    }
    internal sealed class CloudGitHubInstallation
    {
        public string Id { get; set; }
        public string AccountLogin { get; set; }
        public string AccountType { get; set; }
        public bool IsMembership { get; set; }
    }
    internal sealed class CloudGitHubInstallationPage
    {
        public List<CloudGitHubInstallation> Data { get; set; }
        public bool? LikelyOnOldAccessToken { get; set; }
        public bool ConnectionVerified { get; set; }
    }
    // Read-only adapter for the GitHub catalogue actually used by Codex desktop.
    // No OAuth mutation, browser cookies, tokens, clone URLs or installation URLs
    // are returned or persisted by this service.
    internal sealed class CloudGitHubBackend : IDisposable
    {
        public const string ProtocolVersion = CloudBackend.ProtocolVersion;
        public const string DefaultConnectorId = "connector_76869538009648d5b282a4bb21c3d157";
        private const string Origin = "https://chatgpt.com";
        private readonly HttpClient client;
        private readonly Func<AuthIdentity> readIdentity;
        private readonly CloudBackendBinding binding;
        private bool ownsClient, disposed;

        public CloudGitHubBackend(HttpClient client, Func<AuthIdentity> readIdentity, CloudBackendBinding binding)
        {
            if (client == null || readIdentity == null || binding == null || String.IsNullOrWhiteSpace(binding.AccountKey) || String.IsNullOrWhiteSpace(binding.AccountId) || String.IsNullOrWhiteSpace(binding.UserId)) throw new ArgumentException("Association GitHub du compte Cloud incomplète.");
            this.client = client; this.readIdentity = readIdentity;
            this.binding = new CloudBackendBinding { AccountKey = binding.AccountKey, AccountId = binding.AccountId, UserId = binding.UserId };
        }
        public static CloudGitHubBackend Create(Func<AuthIdentity> readIdentity, CloudBackendBinding binding)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
#if !NETCOREAPP
            var property = typeof(HttpClientHandler).GetProperty("SslProtocols"); bool configured = false;
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(handler, System.Security.Authentication.SslProtocols.Tls12, null); configured = true; }
                catch (System.Reflection.TargetInvocationException) { }
                catch (NotSupportedException) { }
            }
            if (!configured) System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            var http = new HttpClient(handler); http.Timeout = Timeout.InfiniteTimeSpan;
            try { var result = new CloudGitHubBackend(http, readIdentity, binding); result.ownsClient = true; return result; }
            catch { http.Dispose(); throw; }
        }
        public Task<CloudGitHubRepositoryPage> ListRepositories(int page, CancellationToken token)
        { return ListRepositories(page, DefaultConnectorId, null, token); }
        public Task<CloudGitHubRepositoryPage> ListRepositories(int page, string connectorId, string linkId, CancellationToken token)
        {
            if (page < 1 || page > 500) throw Invalid("Numéro de page GitHub invalide.");
            string path = "/backend-api/wham/github/list-repositories?page=" + page.ToString(CultureInfo.InvariantCulture) + "&per_page=20" + ConnectionQuery(connectorId, linkId, false);
            return Read(path, value =>
            {
                var repositories = Rows(Json.Get(value, "repositories")).Select(ParseRepository).ToList();
                if (repositories.Count > 20) throw Incompatible();
                return new CloudGitHubRepositoryPage { Data = repositories, Page = page, HasNextPage = repositories.Count < 20 ? (bool?)false : null };
            }, token);
        }
        public Task<CloudGitHubRepository> GetRepository(string repositoryId, CancellationToken token)
        {
            string id = Id(repositoryId);
            return Read("/backend-api/wham/github/repositories/" + id, value => { var repository = ParseRepository(value); if (repository.Id != id) throw Incompatible(); return repository; }, token);
        }
        public Task<CloudGitHubInstallationPage> ListInstallations(CancellationToken token)
        { return ListInstallations(DefaultConnectorId, null, token); }
        public Task<CloudGitHubInstallationPage> ListInstallations(string connectorId, string linkId, CancellationToken token)
        {
            return Read("/backend-api/wham/github/installations/v2" + ConnectionQuery(connectorId, linkId, true), value =>
            {
                var entries = Rows(Json.Get(value, "memberships")).Select(item => ParseInstallation(item, true)).Concat(Rows(Json.Get(value, "other")).Select(item => ParseInstallation(item, false))).ToList();
                if (entries.Count > 10000) throw Incompatible();
                return new CloudGitHubInstallationPage { Data = entries, LikelyOnOldAccessToken = OptionalBool(value, "likely_on_old_access_token"), ConnectionVerified = true };
            }, token);
        }
        private AuthIdentity Current()
        {
            try
            {
                var identity = readIdentity(); var current = CloudBackend.Capture(identity);
                if (current.AccountKey != binding.AccountKey || current.AccountId != binding.AccountId || current.UserId != binding.UserId) throw new InvalidOperationException();
                return identity;
            }
            catch { throw new CloudBackendException("authentication", "Le compte actif ne correspond plus à cette instance, ou sa connexion a expiré."); }
        }
        private async Task<T> Read<T>(string path, Func<object, T> parse, CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException("CloudGitHubBackend");
            token.ThrowIfCancellationRequested(); var identity = Current();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Origin + path)))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AccessToken);
                request.Headers.Add("ChatGPT-Account-Id", binding.AccountId);
                request.Headers.Add("X-OpenAI-Product-Sku", "codex");
                request.Headers.Add("originator", "creezio_account_switcher");
                request.Headers.UserAgent.ParseAdd("creezio-switcher-cloud-prototype/0.17.0-beta.2");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                try
                {
                    Current();
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        Current(); int status = (int)response.StatusCode; string requestId = RequestId(response);
                        if ((status >= 300 && status < 400) || (response.RequestMessage != null && response.RequestMessage.RequestUri != request.RequestUri)) throw new CloudBackendException("redirect", "La redirection du catalogue GitHub a été refusée.", status, requestId);
                        if (!response.IsSuccessStatusCode)
                        {
                            string media = response.Content == null || response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType;
                            bool html = String.Equals(media, "text/html", StringComparison.OrdinalIgnoreCase);
                            IEnumerable<string> mitigated; bool challenge = response.Headers.TryGetValues("cf-mitigated", out mitigated) && mitigated.Any(v => String.Equals(v, "challenge", StringComparison.OrdinalIgnoreCase));
                            string code = html ? "unexpected-html" : status == 401 ? "authentication" : status == 403 ? "forbidden" : status == 404 ? "not-found" : status == 429 ? "rate-limit" : "http";
                            string message = html ? "Le catalogue GitHub a renvoyé une page HTML. Le motif du refus d'accès n'est pas confirmé." : status == 401 ? "Reconnectez ce compte dans Codex pour lire sa connexion GitHub." : status == 403 ? "La connexion GitHub de ce compte ne permet pas cette lecture. Vérifiez ses accès dans Codex." : status == 404 ? "Ce dépôt est introuvable pour la connexion GitHub de ce compte." : status == 429 ? "La limite de requêtes du catalogue GitHub est atteinte." : "Le service GitHub de Codex a refusé la lecture.";
                            throw new CloudBackendException(code, message, status, requestId, html ? "text/html" : null, challenge);
                        }
                        var raw = await ReadBody(response, deadline.Token).ConfigureAwait(false); Current();
                        try { return parse(Json.Read<object>(raw)); }
                        catch (CloudBackendException) { throw Incompatible(); }
                        catch { throw Incompatible(); }
                    }
                }
                catch (CloudBackendException) { throw; }
                catch (OperationCanceledException)
                { if (token.IsCancellationRequested) throw new OperationCanceledException(token); throw new CloudBackendException("timeout", "Le catalogue GitHub n'a pas répondu dans le délai prévu."); }
                catch { throw new CloudBackendException("unavailable", "Connexion au catalogue GitHub de Codex impossible."); }
            }
        }
        private static async Task<string> ReadBody(HttpResponseMessage response, CancellationToken token)
        {
            if (response.Content == null || response.Content.Headers.ContentLength > 2097152) throw Incompatible();
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192]; int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                { if (output.Length + count > 2097152) throw Incompatible(); output.Write(buffer, 0, count); }
                return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }
        private static CloudGitHubRepository ParseRepository(object value)
        {
            string name = Required(value, "repository_full_name"), branch = Required(value, "default_branch");
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") || branch.Length > 1024 || branch.Any(Char.IsControl)) throw Incompatible();
            return new CloudGitHubRepository { Id = Id(Required(value, "id")), FullName = name, DefaultBranch = branch, CanWrite = OptionalBool(Json.Get(value, "permissions"), "push"), Archived = OptionalBool(value, "archived") };
        }
        private static CloudGitHubInstallation ParseInstallation(object value, bool membership)
        {
            object raw = Json.Get(value, "id"); string id = raw as string;
            if (id == null) { var number = Json.Number(raw); if (!number.HasValue || number.Value < 1 || number.Value != Math.Truncate(number.Value)) throw Incompatible(); id = number.Value.ToString("0", CultureInfo.InvariantCulture); }
            return new CloudGitHubInstallation { Id = Id(id), AccountLogin = Required(value, "account_login"), AccountType = Required(value, "account_type"), IsMembership = membership };
        }
        private static string ConnectionQuery(string connectorId, string linkId, bool first)
        { return (first ? "?" : "&") + "connector_id=" + Id(connectorId) + (linkId == null ? "" : "&link_id=" + Id(linkId)); }
        private static string Id(string value)
        { if (String.IsNullOrEmpty(value) || value.Length > 512 || !Regex.IsMatch(value, "^[A-Za-z0-9_~-]+$")) throw Invalid("Identifiant GitHub de Codex invalide."); return value; }
        private static string Required(object value, string name)
        { var text = Json.Get(value, name) as string; if (String.IsNullOrEmpty(text)) throw Incompatible(); return text; }
        private static bool? OptionalBool(object value, string name)
        { var raw = Json.Get(value, name); if (raw == null) return null; if (!(raw is bool)) throw Incompatible(); return (bool)raw; }
        private static IEnumerable<object> Rows(object value) { var rows = value as IEnumerable<object>; if (rows == null) throw Incompatible(); return rows; }
        private static string RequestId(HttpResponseMessage response)
        { foreach (string key in new[] { "x-request-id", "x-openai-request-id", "cf-ray" }) { IEnumerable<string> values; if (response.Headers.TryGetValues(key, out values)) { string value = values.FirstOrDefault(); if (value != null && Regex.IsMatch(value, "^[A-Za-z0-9_.:-]{1,128}$")) return value; } } return null; }
        private static CloudBackendException Invalid(string message) { return new CloudBackendException("invalid-request", message); }
        private static CloudBackendException Incompatible() { return new CloudBackendException("invalid-response", "Réponse du catalogue GitHub incompatible avec le protocole Codex " + ProtocolVersion + "."); }
        public void Dispose() { if (disposed) return; disposed = true; if (ownsClient) client.Dispose(); }
    }
}
