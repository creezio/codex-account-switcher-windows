using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    internal sealed class NativeFileUploadException : InvalidOperationException
    {
        public readonly string Code, Phase, FileId;
        public readonly int? HttpStatus;
        internal NativeFileUploadException(string code, string phase, string fileId = null, int? status = null)
            : base("Transfert de fichier interrompu (" + code + ", étape " + phase + (status.HasValue ? ", HTTP " + status.Value : "") + "). Aucun nouvel envoi automatique.")
        { Code = code; Phase = phase; FileId = fileId; HttpStatus = status; }
    }

    // Implements the OpenAI Codex file protocol, independently of model turns.
    // The injected HttpClient must not follow redirects or add default credentials.
    // Create() supplies that transport. Upload results are ephemeral: never log or persist them.
    internal sealed class NativeFileUpload : IDisposable
    {
        internal const long LimitBytes = 512L * 1024 * 1024;
        private const string Origin = "https://chatgpt.com/backend-api/files";
        private readonly HttpClient client;
        private readonly Func<AuthIdentity> readIdentity;
        private readonly string expectedKey;
        private readonly string boundAccount, boundUser;
        private bool ownsClient, disposed;
        internal Func<string, CancellationToken, Task<IPAddress[]>> ResolveAddresses = Resolve;
        internal Func<TimeSpan, CancellationToken, Task> Delay = (delay, token) => Task.Delay(delay, token);
        internal TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(30);

        internal NativeFileUpload(HttpClient client, Func<AuthIdentity> readIdentity, string expectedKey)
        {
            if (client == null || readIdentity == null || String.IsNullOrWhiteSpace(expectedKey)) throw new ArgumentException("Association de compte absente.");
            this.client = client; this.readIdentity = readIdentity; this.expectedKey = expectedKey;
            NoDefaultCredentials();
            var initial = CloudBackend.Capture(Current("validation", null));
            boundAccount = initial.AccountId; boundUser = initial.UserId;
        }
        internal static NativeFileUpload Create(Func<AuthIdentity> readIdentity, string expectedKey)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
#if !NETCOREAPP
            var property = typeof(HttpClientHandler).GetProperty("SslProtocols"); bool configured = false;
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(handler, System.Security.Authentication.SslProtocols.Tls12, null); configured = true; }
                catch (System.Reflection.TargetInvocationException) { }
                catch (NotSupportedException) { }
            }
            if (!configured) ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            try { var value = new NativeFileUpload(http, readIdentity, expectedKey); value.ownsClient = true; return value; }
            catch { http.Dispose(); throw; }
        }
        private void NoDefaultCredentials()
        {
            if (client.DefaultRequestHeaders.Authorization != null || client.DefaultRequestHeaders.Contains("Cookie") || client.DefaultRequestHeaders.Contains("ChatGPT-Account-Id") || client.DefaultRequestHeaders.Contains("Proxy-Authorization"))
                throw new NativeFileUploadException("transport-credentials", "validation");
        }
        private AuthIdentity Current(string phase, string fileId)
        {
            try
            {
                var identity = readIdentity(); var binding = CloudBackend.Capture(identity);
                if (binding.AccountKey != expectedKey || (boundAccount != null && binding.AccountId != boundAccount) || (boundUser != null && binding.UserId != boundUser)) throw new InvalidOperationException();
                return identity;
            }
            catch { throw new NativeFileUploadException("authentication", phase, fileId); }
        }
        internal async Task<object> Upload(string absolutePath, CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException("NativeFileUpload");
            string phase = "validation", fileId = null;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    deadline.Token.ThrowIfCancellationRequested(); Current(phase, null); NoDefaultCredentials();
                    string path = LocalPath(absolutePath);
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        SafeFiles.RejectLinks(path); VerifyOpenedPath(file, path);
                        long length = file.Length;
                        if (length > LimitBytes) throw new NativeFileUploadException("too-large", phase);
                        phase = "create";
                        var createBody = new { file_name = Path.GetFileName(path), file_size = length, use_case = "codex" };
                        object created = await Backend(Origin, createBody, phase, null, deadline.Token).ConfigureAwait(false);
                        fileId = Json.Get(created, "file_id") as string;
                        if (fileId == null || !Regex.IsMatch(fileId, "^[A-Za-z0-9_-]{1,200}$")) throw new NativeFileUploadException("invalid-response", phase);
                        // This uploader handles normal archives, never PDF provenance reservations.
                        if (Object.Equals(Json.Get(created, "pdf_c2pa_reservation"), true)) throw new NativeFileUploadException("unsupported-reservation", phase, fileId);
                        phase = "upload";
                        Uri uploadUri = await PublicUri(Json.Get(created, "upload_url") as string, phase, fileId, deadline.Token).ConfigureAwait(false);
                        Current(phase, fileId); NoDefaultCredentials();
                        using (var request = new HttpRequestMessage(HttpMethod.Put, uploadUri))
                        {
                            request.Headers.Add("x-ms-blob-type", "BlockBlob");
                            request.Headers.Add("x-ms-client-request-id", Guid.NewGuid().ToString());
                            request.Content = new StreamContent(new BorrowedStream(file), 81920);
                            request.Content.Headers.ContentLength = length;
                            using (var response = await Send(request, phase, fileId, deadline.Token).ConfigureAwait(false)) { }
                        }
                        Current(phase, fileId);
                        if (file.Length != length || file.Position != length) throw new NativeFileUploadException("file-changed", phase, fileId);
                        phase = "finalize"; var clock = Stopwatch.StartNew(); int attempts = 0;
                        while (true)
                        {
                            object result = await Backend(Origin + "/" + fileId + "/uploaded", new Dictionary<string, object>(), phase, fileId, deadline.Token).ConfigureAwait(false);
                            string status = Json.Get(result, "status") as string;
                            if (status == "success")
                            {
                                Uri download = await PublicUri(Json.Get(result, "download_url") as string, phase, fileId, deadline.Token).ConfigureAwait(false);
                                Current(phase, fileId);
                                return new { file_id = fileId, download_url = download.AbsoluteUri };
                            }
                            // The official protocol returns "retry" while finalization is pending.
                            if (status != "retry") throw new NativeFileUploadException("finalize-rejected", phase, fileId);
                            if (++attempts >= 120 || clock.Elapsed >= FinalizeTimeout) throw new NativeFileUploadException("not-ready", phase, fileId);
                            await Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (NativeFileUploadException) { throw; }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException("Transfert annulé. Aucun nouvel envoi automatique.", token);
                    throw new NativeFileUploadException("timeout", phase, fileId);
                }
                // Never retain raw HTTP errors, provider bodies, tokens, signed URLs or paths.
                catch { throw new NativeFileUploadException("transport", phase, fileId); }
            }
        }
        private async Task<object> Backend(string url, object body, string phase, string fileId, CancellationToken token)
        {
            var identity = Current(phase, fileId); NoDefaultCredentials();
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AccessToken);
                request.Headers.Add("ChatGPT-Account-Id", identity.AccountId);
                request.Headers.Add("originator", "creezio_account_switcher");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                using (var response = await Send(request, phase, fileId, token).ConfigureAwait(false))
                {
                    if (response.Content == null) throw new NativeFileUploadException("invalid-response", phase, fileId);
                    if (response.Content.Headers.ContentLength > 65536) throw new NativeFileUploadException("invalid-response", phase, fileId);
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        byte[] bytes = new byte[4096]; int count;
                        while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false)) > 0)
                        { if (buffer.Length + count > 65536) throw new NativeFileUploadException("invalid-response", phase, fileId); buffer.Write(bytes, 0, count); }
                        Current(phase, fileId);
                        try { return Json.Read<object>(Encoding.UTF8.GetString(buffer.ToArray())); }
                        catch { throw new NativeFileUploadException("invalid-response", phase, fileId); }
                    }
                }
            }
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, string phase, string fileId, CancellationToken token)
        {
            Current(phase, fileId); token.ThrowIfCancellationRequested();
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                Current(phase, fileId); int status = (int)response.StatusCode;
                if ((status >= 300 && status < 400) || (response.RequestMessage != null && response.RequestMessage.RequestUri != request.RequestUri)) throw new NativeFileUploadException("redirect", phase, fileId, status);
                if (!response.IsSuccessStatusCode) throw new NativeFileUploadException("http", phase, fileId, status);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        private static string LocalPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "^[A-Za-z]:[\\\\/]") || value.IndexOf(':', 2) >= 0 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new NativeFileUploadException("invalid-path", "validation");
            string path = Path.GetFullPath(value);
            if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || path.Substring(2).Contains(":")) throw new NativeFileUploadException("invalid-path", "validation");
            SafeFiles.RejectLinks(path); return path;
        }
        private async Task<Uri> PublicUri(string value, string phase, string fileId, CancellationToken token)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Port != 443 || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment) || uri.IsLoopback || uri.Host.IndexOf('.') < 0 || uri.Host.EndsWith(".", StringComparison.Ordinal)) throw new NativeFileUploadException("unsafe-url", phase, fileId);
            string host = uri.DnsSafeHost.ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan")) throw new NativeFileUploadException("unsafe-url", phase, fileId);
            IPAddress literal; IPAddress[] addresses;
            if (IPAddress.TryParse(host.Trim('[', ']'), out literal)) addresses = new[] { literal };
            else addresses = await ResolveAddresses(host, token).ConfigureAwait(false);
            if (addresses == null || addresses.Length == 0 || addresses.Any(PrivateAddress)) throw new NativeFileUploadException("unsafe-url", phase, fileId);
            return uri;
        }
        private static bool PrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
                return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224 ||
                    (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) || (bytes[0] == 169 && bytes[1] == 254) ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0)) ||
                    (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19));
            if (bytes.Length != 16) return true;
            return address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc || bytes.Take(12).All(b => b == 0);
        }
        private static async Task<IPAddress[]> Resolve(string host, CancellationToken token)
        {
            var lookup = Dns.GetHostAddressesAsync(host);
            var canceled = new TaskCompletionSource<bool>();
            using (token.Register(() => canceled.TrySetCanceled()))
            {
                if (await Task.WhenAny(lookup, canceled.Task).ConfigureAwait(false) != lookup) token.ThrowIfCancellationRequested();
                return await lookup.ConfigureAwait(false);
            }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
        private static void VerifyOpenedPath(FileStream file, string expected)
        {
            var text = new StringBuilder(32768); uint count = GetFinalPathNameByHandle(file.SafeFileHandle, text, (uint)text.Capacity, 0);
            if (count == 0 || count >= text.Capacity || !String.Equals(text.ToString(), "\\\\?\\" + expected, StringComparison.OrdinalIgnoreCase)) throw new NativeFileUploadException("invalid-path", "validation");
        }
        private sealed class BorrowedStream : Stream
        {
            private readonly Stream source;
            internal BorrowedStream(Stream value) { source = value; }
            public override bool CanRead { get { return source.CanRead; } }
            public override bool CanSeek { get { return source.CanSeek; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return source.Length; } }
            public override long Position { get { return source.Position; } set { source.Position = value; } }
            public override int Read(byte[] buffer, int offset, int count) { return source.Read(buffer, offset, count); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { return source.ReadAsync(buffer, offset, count, token); }
            public override long Seek(long offset, SeekOrigin origin) { return source.Seek(offset, origin); }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
        public void Dispose() { if (disposed) return; disposed = true; if (ownsClient) client.Dispose(); }
    }
}
