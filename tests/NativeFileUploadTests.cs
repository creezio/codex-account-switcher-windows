using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;
using Microsoft.Win32.SafeHandles;

internal static class NativeFileUploadTests
{
    private const string Secret = "SECRET_MUST_NEVER_APPEAR";
    private const string Blob = "https://fixture.blob.core.windows.net/upload?sig=" + Secret;
    private static int checks;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string link, string target, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputLength, IntPtr output, int outputLength, out int bytesReturned, IntPtr overlapped);
    private static void Assert(bool value, string why) { if (!value) throw new Exception(why); }
    private static string Encode(object value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    private static AuthIdentity Identity(string account = "account-a")
    { return new AuthIdentity { Key = account + ":user-a", AccountId = account, AccessToken = "e30." + Encode(new Dictionary<string, object> { { "exp", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 }, { "https://api.openai.com/auth", new { chatgpt_account_id = account, chatgpt_user_id = "user-a" } } }) + "." + Secret }; }
    private sealed class Request
    { internal string Method, Uri, Authorization, Account, BlobType, Body, ClientId; internal long? Length; }
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<Request> Requests = new List<Request>();
        internal Func<Request, CancellationToken, Task<HttpResponseMessage>> Reply;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            var request = new Request { Method = message.Method.Method, Uri = message.RequestUri.AbsoluteUri, Authorization = Header(message, "Authorization"), Account = Header(message, "ChatGPT-Account-Id"), BlobType = Header(message, "x-ms-blob-type"), ClientId = Header(message, "x-ms-client-request-id"), Length = message.Content == null ? null : message.Content.Headers.ContentLength, Body = message.Content == null ? null : await message.Content.ReadAsStringAsync() };
            Requests.Add(request); return await Reply(request, token);
        }
        private static string Header(HttpRequestMessage request, string name) { IEnumerable<string> values; return request.Headers.TryGetValues(name, out values) ? String.Join(",", values) : null; }
    }
    private sealed class Fixture : IDisposable
    {
        internal AuthIdentity Current = Identity();
        internal readonly Handler Handler = new Handler();
        internal readonly NativeFileUpload Uploader;
        internal readonly string Path;
        internal string UploadUrl = Blob;
        internal int Finalizations;
        private readonly HttpClient client;
        internal Fixture(string root)
        {
            Path = System.IO.Path.Combine(root, "native-file-upload-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(Path, "hello", new UTF8Encoding(false)); client = new HttpClient(Handler);
            Uploader = new NativeFileUpload(client, () => Current, Current.Key);
            Uploader.ResolveAddresses = (host, token) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
            Uploader.Delay = (delay, token) => Task.FromResult(true);
            Handler.Reply = (request, token) => Task.FromResult(DefaultReply(request));
        }
        internal HttpResponseMessage DefaultReply(Request request)
        {
            if (request.Method == "PUT") return Reply(new { });
            if (request.Uri.EndsWith("/uploaded")) { Finalizations++; return Reply(new { status = "success", download_url = "https://files.example.com/file-1?sig=" + Secret }); }
            return Reply(new { file_id = "file-1", upload_url = UploadUrl });
        }
        internal object Upload(CancellationToken token = default(CancellationToken)) { return Uploader.Upload(Path, token).GetAwaiter().GetResult(); }
        public void Dispose()
        {
            Uploader.Dispose(); client.Dispose();
            // Only this fixture's exact file is owned; never recursively remove a directory.
            File.Delete(Path);
        }
    }
    private static HttpResponseMessage Reply(object body, HttpStatusCode status = HttpStatusCode.OK) { return new HttpResponseMessage(status) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") }; }
    private static NativeFileUploadException Refuse(Action action, string code = null)
    {
        try { action(); }
        catch (NativeFileUploadException error)
        {
            Assert(!error.ToString().Contains(Secret) && error.InnerException == null && !error.ToString().Contains("sig="), "sensitive diagnostic leaked");
            if (code != null) Assert(error.Code == code, "wrong refusal: " + error.Code); return error;
        }
        throw new Exception("Expected safe upload refusal");
    }
    private static void Check(string name, Action action) { action(); checks++; Console.WriteLine("PASS " + name); }
    internal static void RunAll(string root)
    {
        Check("real file streams, bound backend auth, uncredentialed blob, exact create/finalize protocol", () => {
            using (var f = new Fixture(root))
            {
                bool locked = false;
                f.Handler.Reply = (request, token) => {
                    try { using (var writer = new FileStream(f.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
                    catch (IOException) { locked = true; }
                    return Task.FromResult(f.DefaultReply(request));
                };
                var result = Json.Obj(Json.Read<object>(Json.Write(f.Upload()))); Assert(result.Count == 2 && Json.Str(result["file_id"]) == "file-1", "ephemeral payload altered");
                Assert(f.Handler.Requests.Count == 3 && locked, "file was not immutable or wrong request count");
                var create = f.Handler.Requests[0]; var body = Json.Obj(Json.Read<object>(create.Body));
                Assert(create.Uri == "https://chatgpt.com/backend-api/files" && create.Method == "POST" && create.Account == f.Current.AccountId && create.Authorization == "Bearer " + f.Current.AccessToken, "create account changed");
                Assert(body.Count == 3 && Json.Str(body["file_name"]) == System.IO.Path.GetFileName(f.Path) && Json.Number(body["file_size"]) == 5 && Json.Str(body["use_case"]) == "codex", "create protocol changed");
                var put = f.Handler.Requests[1]; Guid clientId;
                Assert(put.Uri == Blob && put.Method == "PUT" && put.Authorization == null && put.Account == null && put.Length == 5 && put.Body == "hello" && put.BlobType == "BlockBlob" && Guid.TryParse(put.ClientId, out clientId), "blob upload leaked auth or lost stream");
                var final = f.Handler.Requests[2]; Assert(final.Uri.EndsWith("/files/file-1/uploaded") && final.Body == "{}" && final.Account == f.Current.AccountId && final.Authorization == create.Authorization, "finalize protocol changed");
            }
        });
        Check("pending finalization reuses exact file ID without another create or PUT", () => {
            using (var f = new Fixture(root))
            {
                int final = 0; f.Handler.Reply = (request, token) => Task.FromResult(request.Uri.EndsWith("/uploaded") && ++final < 3 ? Reply(new { status = "retry" }) : f.DefaultReply(request));
                f.Upload(); Assert(f.Handler.Requests.Count == 5 && f.Handler.Requests.Count(r => r.Method == "PUT") == 1 && f.Handler.Requests.Count(r => r.Uri.EndsWith("/files")) == 1, "record was created twice");
            }
        });
        Check("pending timeout preserves file ID and cannot loop indefinitely", () => {
            using (var f = new Fixture(root))
            {
                f.Uploader.FinalizeTimeout = TimeSpan.Zero; f.Handler.Reply = (request, token) => Task.FromResult(request.Uri.EndsWith("/uploaded") ? Reply(new { status = "retry" }) : f.DefaultReply(request));
                var error = Refuse(() => f.Upload(), "not-ready"); Assert(error.FileId == "file-1" && f.Handler.Requests.Count == 3, "timeout lost identity or retried");
            }
        });
        Check("HTTP failures never create or retry a second ID", () => {
            foreach (int step in new[] { 1, 2, 3 }) using (var f = new Fixture(root))
            {
                f.Handler.Reply = (request, token) => Task.FromResult(f.Handler.Requests.Count == step ? Reply(new { error = Secret }, HttpStatusCode.Forbidden) : f.DefaultReply(request));
                var error = Refuse(() => f.Upload(), "http"); Assert(error.HttpStatus == 403 && f.Handler.Requests.Count == step && (step == 1 || error.FileId == "file-1"), "failure silently retried");
            }
        });
        Check("refuses HTTP, localhost, private address, credentials, fragments and nonstandard ports", () => {
            foreach (string url in new[] { "http://files.example.com/?sig=" + Secret, "https://localhost/file", "https://127.0.0.1/file", "https://10.0.0.1/file", "https://169.254.169.254/file", "https://192.168.1.1/file", "https://100.64.0.1/file", "https://[::1]/file", "https://user:password@example.com/file", "https://example.com/file#fragment", "https://example.com:8443/file", "https://machine.local/file", "https://machine/file" })
                using (var f = new Fixture(root)) { f.UploadUrl = url; Refuse(() => f.Upload(), "unsafe-url"); Assert(f.Handler.Requests.Count == 1, "unsafe upload reached blob"); }
        });
        Check("refuses public host resolving to private address or mixed DNS addresses", () => {
            using (var f = new Fixture(root))
            {
                f.Uploader.ResolveAddresses = (host, token) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("172.16.2.3") });
                Refuse(() => f.Upload(), "unsafe-url"); Assert(f.Handler.Requests.Count == 1, "private DNS destination reached blob");
            }
        });
        Check("refuses all redirects, preserving created identity without retry", () => {
            foreach (int step in new[] { 1, 2, 3 }) using (var f = new Fixture(root))
            {
                f.Handler.Reply = (request, token) => { if (f.Handler.Requests.Count != step) return Task.FromResult(f.DefaultReply(request)); var response = Reply(new { detail = Secret }, HttpStatusCode.TemporaryRedirect); response.Headers.Location = new Uri("https://evil.example/?sig=" + Secret); return Task.FromResult(response); };
                Refuse(() => f.Upload(), "redirect"); Assert(f.Handler.Requests.Count == step, "redirect was followed or request retried");
            }
        });
        Check("detects injected transport that changed final destination", () => {
            using (var f = new Fixture(root))
            {
                f.Handler.Reply = (request, token) => { var response = f.DefaultReply(request); response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://evil.example/"); return Task.FromResult(response); };
                Refuse(() => f.Upload(), "redirect"); Assert(f.Handler.Requests.Count == 1, "changed destination accepted");
            }
        });
        Check("refuses account change before create and during each response", () => {
            using (var f = new Fixture(root)) { f.Current = Identity("account-b"); Refuse(() => f.Upload(), "authentication"); Assert(f.Handler.Requests.Count == 0, "wrong initial account reached backend"); }
            foreach (int step in new[] { 1, 2, 3 }) using (var f = new Fixture(root))
            {
                f.Handler.Reply = (request, token) => { var response = f.DefaultReply(request); if (f.Handler.Requests.Count == step) f.Current = Identity("account-b"); return Task.FromResult(response); };
                Refuse(() => f.Upload(), "authentication"); Assert(f.Handler.Requests.Count == step, "account change ignored");
            }
        });
        Check("rejects relative, UNC and alternate stream paths before network", () => {
            using (var f = new Fixture(root)) foreach (string path in new[] { "relative.tar", "\\\\server\\share\\archive.tar", f.Path + ":stream" })
            { Refuse(() => f.Uploader.Upload(path, CancellationToken.None).GetAwaiter().GetResult(), "invalid-path"); Assert(f.Handler.Requests.Count == 0, "unsafe path allocated file"); }
        });
        Check("rejects over-512-MiB sparse file before allocating a remote record", () => {
            using (var f = new Fixture(root))
            {
                using (var file = new FileStream(f.Path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    int returned; Assert(DeviceIoControl(file.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero), "test sparse file unavailable");
                    file.SetLength(NativeFileUpload.LimitBytes + 1);
                }
                Refuse(() => f.Upload(), "too-large"); Assert(f.Handler.Requests.Count == 0, "oversize file allocated remote record");
            }
        });
        Check("rejects symlink path without opening or allocating its target", () => {
            using (var f = new Fixture(root))
            {
                string link = f.Path + ".link"; bool created = CreateSymbolicLink(link, f.Path, 2);
                if (!created && Marshal.GetLastWin32Error() == 87) created = CreateSymbolicLink(link, f.Path, 0);
                Assert(created, "test symlink unavailable");
                try { Refuse(() => f.Uploader.Upload(link, CancellationToken.None).GetAwaiter().GetResult()); Assert(f.Handler.Requests.Count == 0, "symlink allocated record"); }
                finally { File.Delete(link); }
            }
        });
        Check("pins account ID even if a faulty identity reader reuses the key", () => {
            using (var f = new Fixture(root))
            {
                var changed = Identity("account-b"); changed.Key = f.Current.Key; f.Current = changed;
                Refuse(() => f.Upload(), "authentication"); Assert(f.Handler.Requests.Count == 0, "changed account with reused key reached backend");
            }
        });
        Check("malformed IDs, reservations and finalization failures never escape diagnostics", () => {
            foreach (object body in new object[] { new { file_id = "../bad?" + Secret, upload_url = Blob }, new { file_id = "file-1", upload_url = Blob, pdf_c2pa_reservation = true } })
                using (var f = new Fixture(root)) { f.Handler.Reply = (request, token) => Task.FromResult(Reply(body)); Refuse(() => f.Upload()); Assert(f.Handler.Requests.Count == 1, "malformed create progressed"); }
            using (var f = new Fixture(root)) { f.Handler.Reply = (request, token) => Task.FromResult(request.Uri.EndsWith("/uploaded") ? Reply(new { status = "failure", error_message = Secret }) : f.DefaultReply(request)); Refuse(() => f.Upload(), "finalize-rejected"); }
        });
        Check("transport exceptions redact signed URL, token and inner exceptions", () => {
            using (var f = new Fixture(root)) { f.Handler.Reply = (request, token) => { throw new HttpRequestException(Secret + Blob); }; Refuse(() => f.Upload(), "transport"); Assert(f.Handler.Requests.Count == 1, "transport exception retried create"); }
        });
        Check("cancellation stops pending transport and does not retry", () => {
            using (var f = new Fixture(root)) using (var cancel = new CancellationTokenSource())
            {
                f.Handler.Reply = async (request, token) => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return Reply(new { }); };
                try { f.Upload(cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
                Assert(f.Handler.Requests.Count == 1, "canceled create was retried");
            }
        });
        Check("default Authorization cannot leak to signed upload", () => {
            using (var handler = new Handler()) using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Secret);
                Refuse(() => new NativeFileUpload(client, () => Identity(), Identity().Key), "transport-credentials"); Assert(handler.Requests.Count == 0, "credential-bearing client used");
            }
        });
    }
    public static int Main(string[] args)
    {
        try { string root = System.IO.Path.GetFullPath(args.Length == 0 ? "work/test-bin" : args[0]); Directory.CreateDirectory(root); RunAll(root); Console.WriteLine(checks + " native file upload tests passed; no real network or Site mutation."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }
}
