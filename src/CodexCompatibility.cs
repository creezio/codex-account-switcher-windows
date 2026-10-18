using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class CodexCompatibilityCheck
    {
        public string InstanceId { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string Detail { get; set; }
        public string RunningVersion { get; set; }
        public string CheckedUtc { get; set; }
    }
    internal sealed class CodexCompatibilityReport
    {
        public string Fingerprint { get; set; }
        public string InstalledVersion { get; set; }
        public string InstalledPath { get; set; }
        public string CliPath { get; set; }
        public string CliVersion { get; set; }
        public string DetectedUtc { get; set; }
        public string CheckedUtc { get; set; }
        public string State { get; set; }
        public string Detail { get; set; }
        public string EpisodeId { get; set; }
        public string NotifiedUtc { get; set; }
        public bool NeedsAttention { get; set; }
        public List<CodexCompatibilityCheck> Checks { get; set; }
        public CodexCompatibilityReport() { Checks = new List<CodexCompatibilityCheck>(); }
    }
    internal sealed class CodexCompatibilityInventory
    {
        internal string Fingerprint, InstalledVersion, InstalledPath, CliPath;
        internal List<CodexCompatibilityTarget> Targets = new List<CodexCompatibilityTarget>();
    }
    internal sealed class CodexCompatibilityTarget
    {
        internal string Id, Name, Version;
        internal bool Running;
        internal int Pid;
        internal long Started;
        internal AccountService Accounts;
        internal DesktopInstance Instance;
    }

    // These checks read local process identities and existing native chats. They
    // never start Codex, run a model, approve a request or exercise a mutation.
    internal sealed class CodexCompatibility
    {
        internal const string Record = "codex-compatibility-v1.dpapi";
        private readonly RelayStore store;
        private readonly Func<CancellationToken, Task<CodexCompatibilityInventory>> inventory;
        private readonly Func<CodexCompatibilityTarget, CancellationToken, Task<CodexCompatibilityCheck>> probe;
        private readonly Func<string, CancellationToken, Task<string>> cliVersion;
        private readonly Func<DateTime> now;
        internal TimeSpan ProbeTimeout { get; set; }
        internal CodexCompatibility(RelayStore relay) : this(relay, null, null, null, null) { }
        internal CodexCompatibility(RelayStore relay,
            Func<CancellationToken, Task<CodexCompatibilityInventory>> detect,
            Func<CodexCompatibilityTarget, CancellationToken, Task<CodexCompatibilityCheck>> check,
            Func<string, CancellationToken, Task<string>> version, Func<DateTime> clock)
        {
            store = relay; inventory = detect ?? Detect; probe = check ?? Probe;
            cliVersion = version ?? ReadCliVersion; now = clock ?? (() => DateTime.UtcNow);
            ProbeTimeout = TimeSpan.FromSeconds(15);
        }
        internal CodexCompatibilityReport Read() { return store.ReadRecord<CodexCompatibilityReport>(Record); }
        internal void MarkNotified(string episode)
        {
            using (store.Lease("codex-compatibility-save-v1"))
            {
                var report = Read();
                if (report.NeedsAttention && report.EpisodeId == episode && String.IsNullOrEmpty(report.NotifiedUtc))
                { report.NotifiedUtc = now().ToString("o"); store.WriteRecord(Record, report); }
            }
        }
        internal async Task<CodexCompatibilityReport> Refresh(CancellationToken token, bool force = false)
        {
            FileStream lease;
            try { lease = store.Lease("codex-compatibility-run-v1"); }
            catch (IOException) { return Read(); }
            using (lease)
            {
                var previous = Read(); var stamp = now();
                CodexCompatibilityInventory detected;
                try { detected = await Bounded(inventory, TimeSpan.FromSeconds(30), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return SaveFailure(previous, stamp); }
                catch (Exception) { return SaveFailure(previous, stamp); }
                DateTime checkedAt;
                bool retryDue = previous.NeedsAttention && DateTime.TryParse(previous.CheckedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out checkedAt) && stamp - checkedAt.ToUniversalTime() >= TimeSpan.FromMinutes(5);
                if (!force && !retryDue && detected.Fingerprint == previous.Fingerprint) return previous;
                var report = new CodexCompatibilityReport
                {
                    Fingerprint = detected.Fingerprint, InstalledVersion = detected.InstalledVersion,
                    InstalledPath = detected.InstalledPath, CliPath = detected.CliPath,
                    DetectedUtc = detected.Fingerprint == previous.Fingerprint ? previous.DetectedUtc : stamp.ToString("o"),
                    CheckedUtc = stamp.ToString("o"), EpisodeId = Guid.NewGuid().ToString("N")
                };
                foreach (var target in detected.Targets)
                {
                    token.ThrowIfCancellationRequested();
                    CodexCompatibilityCheck check;
                    if (!target.Running) check = new CodexCompatibilityCheck { State = "not-tested", Detail = "Instance fermée : aucun lancement effectué. Vérification automatique à sa prochaine ouverture." };
                    else
                    {
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            deadline.CancelAfter(TimeSpan.FromSeconds(15));
                            try { check = await Bounded(t => probe(target, t), ProbeTimeout, deadline.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); check = new CodexCompatibilityCheck { State = "unavailable", Detail = "Le contrôle natif a dépassé 15 secondes. Compatibilité non confirmée ; aucune mission n’a été envoyée." }; }
                            catch (Exception) { check = new CodexCompatibilityCheck { State = "unavailable", Detail = "La connexion native ou l’identité du compte n’a pas pu être vérifiée. Le Switcher peut nécessiter une mise à jour si ce problème persiste." }; }
                        }
                    }
                    check.InstanceId = target.Id; check.Name = target.Name; check.RunningVersion = target.Version;
                    check.CheckedUtc = now().ToString("o"); report.Checks.Add(check);
                }
                try { report.CliVersion = await Bounded(t => cliVersion(detected.CliPath, t), TimeSpan.FromSeconds(7), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { report.Checks.Add(new CodexCompatibilityCheck { Name = "Codex CLI", State = "unavailable", Detail = "Version du CLI non vérifiable ; le mode sans fenêtre doit être contrôlé.", CheckedUtc = now().ToString("o") }); }
                report.NeedsAttention = report.Checks.Any(c => c.State == "incompatible" || c.State == "unavailable");
                bool tested = report.Checks.Any(c => c.State == "verified");
                report.State = report.NeedsAttention ? "attention" : tested ? "verified" : "not-tested";
                report.Detail = report.NeedsAttention
                    ? "Un contrôle a échoué après détection de Codex. Consultez le détail avant de déléguer ; une mise à jour du Switcher peut être nécessaire."
                    : tested ? "Connexion, lecture d’un chat existant et commandes annoncées vérifiées. Les opérations qui modifient ou publient ne sont pas exécutées par ce contrôle."
                    : "Version détectée. Les connexions seront vérifiées à l’ouverture d’une instance ; aucune instance n’est lancée pour les tester.";
                // A restart/retry for the same failed version must not spam the tray.
                if (report.NeedsAttention && previous.NeedsAttention && previous.Fingerprint == report.Fingerprint &&
                    Json.Write(previous.Checks.Select(c => new { c.InstanceId, c.State })) == Json.Write(report.Checks.Select(c => new { c.InstanceId, c.State })))
                { report.EpisodeId = previous.EpisodeId; report.NotifiedUtc = previous.NotifiedUtc; }
                using (store.Lease("codex-compatibility-save-v1"))
                {
                    var latest = Read();
                    if (latest.EpisodeId == report.EpisodeId && !String.IsNullOrEmpty(latest.NotifiedUtc))
                        report.NotifiedUtc = latest.NotifiedUtc;
                    store.WriteRecord(Record, report);
                }
                return report;
            }
        }
        private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken token)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException("timeout");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(timeout);
                var operation = Task.Run(() => action(deadline.Token), deadline.Token);
                if (await Task.WhenAny(operation, Task.Delay(timeout, token)).ConfigureAwait(false) != operation)
                {
                    deadline.Cancel();
                    ObserveFailure(operation);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("La vérification n’a pas répondu dans le délai prévu.");
                }
                return await operation.ConfigureAwait(false);
            }
        }
        private static void ObserveFailure(Task operation)
        { operation.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        private CodexCompatibilityReport SaveFailure(CodexCompatibilityReport previous, DateTime stamp)
        {
            previous.State = "attention"; previous.NeedsAttention = true;
            previous.Detail = "Impossible de détecter la version de Codex ; le contrôle de compatibilité n’est pas confirmé.";
            previous.CheckedUtc = stamp.ToString("o");
            if (String.IsNullOrEmpty(previous.EpisodeId)) previous.EpisodeId = Guid.NewGuid().ToString("N");
            // A failed inventory must be retried, even if the next version is unchanged.
            previous.Fingerprint = null;
            using (store.Lease("codex-compatibility-save-v1")) store.WriteRecord(Record, previous);
            return previous;
        }
        internal static string PackageVersion(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable)) return "Non détectée";
            string directory = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(executable ?? "")));
            var match = System.Text.RegularExpressions.Regex.Match(directory ?? "", @"\AOpenAI\.Codex_(?<version>[0-9]+(?:\.[0-9]+){2,3})_");
            return match.Success ? match.Groups["version"].Value : "Installation personnalisée";
        }
        private async Task<CodexCompatibilityInventory> Detect(CancellationToken token)
        {
            var accounts = new AccountService(Path.GetDirectoryName(store.Root));
            var package = await DesktopRuntime.FindPackage(token).ConfigureAwait(false);
            var result = new CodexCompatibilityInventory { InstalledPath = package.Executable, InstalledVersion = PackageVersion(package.Executable), CliPath = accounts.Settings.CodexExecutable };
            foreach (var instance in accounts.Data.Instances.Where(i => !i.Archived).OrderBy(i => i.Id, StringComparer.Ordinal))
            {
                var state = accounts.Instances.Runtime.Probe(instance);
                string version = null;
                if (state.Running && DesktopRuntime.SameProcess(state.DesktopPid, state.DesktopStartTicks))
                using (var process = Process.GetProcessById(state.DesktopPid)) version = PackageVersion(process.MainModule.FileName);
                result.Targets.Add(new CodexCompatibilityTarget { Id = instance.Id, Name = instance.Name, Running = state.Running, Pid = state.DesktopPid, Started = state.DesktopStartTicks, Version = version, Accounts = accounts, Instance = instance });
            }
            string cliIdentity = File.Exists(result.CliPath) ? new FileInfo(result.CliPath).Length + ":" + File.GetLastWriteTimeUtc(result.CliPath).Ticks : "missing";
            result.Fingerprint = RemotePeers.Hash(result.InstalledPath + "|" + result.InstalledVersion + "|" + result.CliPath + "|" + cliIdentity + "|" +
                String.Join("|", result.Targets.Select(t => t.Id + ":" + t.Instance.AccountKey + ":" + t.Name + ":" + t.Pid + ":" + t.Started + ":" + t.Running)));
            return result;
        }
        internal static string[] MissingCommands(object catalog)
        {
            var names = RelayEngine.Rows(Json.Get(catalog, "tools")).Where(t => Json.Str(Json.Get(t, "namespace")) == "codex_app").Select(t => Json.Str(Json.Get(t, "name"))).ToArray();
            return new[] { "read_thread", "list_threads", "list_projects", "create_thread", "send_message_to_thread", "navigate_to_codex_page" }.Where(n => !names.Contains(n)).ToArray();
        }
        private async Task<CodexCompatibilityCheck> Probe(CodexCompatibilityTarget target, CancellationToken token)
        {
            var accounts = target.Accounts; var instance = target.Instance;
            NamedInstances.VerifyAccount(accounts, instance); string pipe;
            using (var client = NamedInstances.OpenDesktop(store, accounts, instance, token, out pipe))
            {
                var catalog = await client.Request("tools/list", new { threadStartKind = "all" }, token).ConfigureAwait(false);
                string[] missing = MissingCommands(catalog);
                if (missing.Length > 0) return new CodexCompatibilityCheck { State = "incompatible", Detail = "Commandes natives absentes : " + String.Join(", ", missing) + ". Mise à jour du Switcher à étudier ; aucune mission de test n’a été créée." };
                var channel = store.Channels().FirstOrDefault(c => c.Enabled && c.AccountKey == instance.AccountKey &&
                    RelayStore.SamePath(c.Home, accounts.Instances.Home(instance)) && c.ServerPid == client.ServerPid && c.ServerStartTicks == client.ServerStartTicks && !String.IsNullOrEmpty(c.AnchorThreadId));
                if (channel == null) return new CodexCompatibilityCheck { State = "catalog-only", Detail = "Canal et commandes disponibles. Aucun chat de connexion existant : lecture non testée, aucune conversation créée." };
                var snapshot = await client.Call(channel.AnchorThreadId, "read_thread", new { threadId = channel.AnchorThreadId, turnLimit = 1, includeOutputs = false, maxOutputCharsPerItem = 100 }, token).ConfigureAwait(false);
                DirectMessages.RequireThread(snapshot, channel.AnchorThreadId);
                NamedInstances.VerifyAccount(accounts, instance);
                return new CodexCompatibilityCheck { State = "verified", Detail = "Identité de l’instance, canal Windows, catalogue des commandes et lecture native d’un chat vérifiés. Aucun prompt envoyé." };
            }
        }
        private static async Task<string> ReadCliVersion(string executable, CancellationToken token)
        {
            if (String.IsNullOrEmpty(executable) || !File.Exists(executable)) throw new FileNotFoundException();
            var start = new ProcessStartInfo(executable, "--version") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                try
                {
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (!process.HasExited) { token.ThrowIfCancellationRequested(); if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(50, token).ConfigureAwait(false); }
                    string value = (await output.ConfigureAwait(false)).Trim(); await error.ConfigureAwait(false);
                    if (process.ExitCode != 0 || value.Length > 200 || !System.Text.RegularExpressions.Regex.IsMatch(value,
                        @"\Acodex(?:-cli)? [0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.+-]+)?\z")) throw new InvalidDataException();
                    return value;
                }
                finally { if (!process.HasExited) { try { process.Kill(); } catch (InvalidOperationException) { } } }
            }
        }
    }
}
