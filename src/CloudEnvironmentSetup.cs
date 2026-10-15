using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class CloudEnvironmentSetupResult
    {
        public string Id { get; set; }
        public string State { get; set; }
        public string Stage { get; set; }
        public string ConfigurationId { get; set; }
        public string ThreadId { get; set; }
        public string TurnId { get; set; }
        public string RemoteOperationId { get; set; }
        public CloudConfiguration Configuration { get; set; }
        public string FailureCode { get; set; }
        public int? HttpStatus { get; set; }
        public string RequestId { get; set; }
    }

    internal sealed class CloudEnvironmentSetupRecord
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public string InstanceId { get; set; }
        public string AccountKey { get; set; }
        public string ConfigurationId { get; set; }
        public string BaseVersionId { get; set; }
        public string BaseReadyVersionId { get; set; }
        public string RepositoriesFingerprint { get; set; }
        public string Fingerprint { get; set; }
        public string ThreadId { get; set; }
        public string TurnId { get; set; }
        public string EnvironmentId { get; set; }
        public int DraftRevision { get; set; }
        public string RemoteOperationId { get; set; }
        public string ExpectedPublishedVersionId { get; set; }
        public string State { get; set; }
        public string Stage { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
        public string FailureCode { get; set; }
        public int? HttpStatus { get; set; }
        public string RequestId { get; set; }
    }

    internal static class CloudEnvironmentSetup
    {
        private const string Preparation = "Prepare this private Cloud environment for the selected repository. Inspect the existing project and verify the minimal test command already available. Do not edit repository files, create commits, push branches, open pull requests, change sharing, add secrets, or publish the environment. Keep its current network policy. When preparation is ready, report that publication remains a separate action.";

        internal static CloudEnvironmentSetupResult[] Recent(AccountService accounts, DesktopInstance instance)
        {
            string directory = Path.Combine(Path.GetFullPath(accounts.Vault.Root), "cloud-setups-v1");
            if (!Directory.Exists(directory)) return new CloudEnvironmentSetupResult[0];
            SafeFiles.RejectLinks(directory);
            var records = new List<CloudEnvironmentSetupRecord>();
            foreach (string file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!Regex.IsMatch(Path.GetFileNameWithoutExtension(file), "^[a-f0-9]{32}$")) continue;
                var record = Json.Read<CloudEnvironmentSetupRecord>(SafeFiles.ReadText(file));
                if (record != null && record.SchemaVersion == 1 && record.InstanceId == instance.Id && record.AccountKey == instance.AccountKey) records.Add(record);
            }
            return records.OrderByDescending(value => value.CreatedUtc, StringComparer.Ordinal).Take(100).Select(value => Result(value, null)).ToArray();
        }

        internal static async Task<CloudEnvironmentSetupResult> Start(AccountService accounts, DesktopInstance instance, string configId, IDictionary<string, string> repositoryNames, string operationId, CancellationToken token)
        {
            using (var client = CloudInstanceConnection.Open(accounts, instance))
            using (var tasks = CloudInstanceConnection.OpenOnboarding(accounts, instance))
                return await Start(accounts.Vault.Root, instance.Id, instance.AccountKey, client, tasks, configId, repositoryNames, operationId, token);
        }
        internal static async Task<CloudEnvironmentSetupResult> Follow(AccountService accounts, DesktopInstance instance, string operationId, CancellationToken token)
        {
            using (var client = CloudInstanceConnection.Open(accounts, instance))
            using (var tasks = CloudInstanceConnection.OpenOnboarding(accounts, instance))
                return await Follow(accounts.Vault.Root, instance.Id, instance.AccountKey, client, tasks, operationId, token);
        }
        internal static async Task<CloudEnvironmentSetupResult> Publish(AccountService accounts, DesktopInstance instance, string operationId, CancellationToken token)
        {
            using (var client = CloudInstanceConnection.Open(accounts, instance))
            using (var tasks = CloudInstanceConnection.OpenOnboarding(accounts, instance))
                return await Publish(accounts.Vault.Root, instance.Id, instance.AccountKey, client, tasks, operationId, token);
        }
        internal static async Task<CloudEnvironmentSetupResult> Start(string vaultRoot, string instanceId, string accountKey, CloudBackend client, ICloudOnboardingConnection tasks, string configId, IDictionary<string, string> repositoryNames, string operationId, CancellationToken token)
        {
            if (repositoryNames == null || repositoryNames.Count == 0) throw new InvalidOperationException("Les noms GitHub vérifiés des dépôts sont requis pour préparer l'environnement Cloud.");
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in repositoryNames)
            {
                if (String.IsNullOrEmpty(pair.Key) || pair.Key.Length > 512 || !Regex.IsMatch(pair.Value ?? "", "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new InvalidOperationException("Nom de dépôt GitHub invalide.");
                names.Add(pair.Key, pair.Value);
            }
            string file = FilePath(vaultRoot, operationId), fingerprint = Hash(new { configurationId = configId, repositoryNames = names });
            using (var lease = Lock(file))
            {
                if (File.Exists(file))
                {
                    var existing = Read(file, instanceId, accountKey, operationId);
                    if (existing.ConfigurationId != configId || existing.Fingerprint != fingerprint) throw new InvalidOperationException("Cet identifiant appartient à une autre préparation Cloud.");
                    return await FollowCore(client, tasks, file, existing, token);
                }
                if (String.IsNullOrEmpty(instanceId) || String.IsNullOrEmpty(accountKey)) throw new InvalidOperationException("Association Cloud incomplète.");
                var record = new CloudEnvironmentSetupRecord { SchemaVersion = 1, Id = operationId, InstanceId = instanceId, AccountKey = accountKey, ConfigurationId = configId, Fingerprint = fingerprint, State = "preparing", Stage = "reading", CreatedUtc = DateTime.UtcNow.ToString("o") };
                Save(file, record);
                try
                {
                    var config = await client.Get(configId, token);
                    if (config.IsOwner != true || !config.VersionRevision.HasValue || config.VersionRevision.Value != 1 || config.Draft != null || !String.IsNullOrEmpty(config.ThreadId))
                        throw Conflict("Cet environnement possède déjà une préparation ou une version publiée. Aucune nouvelle conversation n'a été créée.");
                    var repositories = config.Repositories ?? new List<CloudRepository>();
                    if (repositories.Count == 0 || repositories.Count != names.Count || repositories.Any(value => !names.ContainsKey(value.RepositoryId))) throw new InvalidOperationException("Les noms GitHub fournis ne correspondent pas exactement aux dépôts de cet environnement.");
                    record.BaseVersionId = config.VersionId; record.BaseReadyVersionId = config.LatestReadyVersionId; record.RepositoriesFingerprint = RepositoryHash(repositories);
                    Sending(file, record, "creating-thread");
                    var thread = await tasks.StartOnboarding(configId, token);
                    RequireThread(thread, record, false);
                    record.ThreadId = thread.Id;
                    record.State = "preparing"; record.Stage = "thread-created"; Save(file, record);
                    Sending(file, record, "starting-setup");
                    var turn = await tasks.StartOnboardingTurn(record.ThreadId, Preparation, record.Id, names, token);
                    if (turn == null || String.IsNullOrEmpty(turn.Id)) throw new CloudBackendUncertainException("tour de préparation non confirmé");
                    record.TurnId = turn.Id; record.State = "running"; record.Stage = "setup-running"; Save(file, record);
                    return Result(record, config);
                }
                catch (Exception error) { Failure(file, record, error); throw; }
            }
        }
        internal static async Task<CloudEnvironmentSetupResult> Follow(string vaultRoot, string instanceId, string accountKey, CloudBackend client, ICloudOnboardingConnection tasks, string operationId, CancellationToken token)
        {
            string file = FilePath(vaultRoot, operationId);
            using (var lease = Lock(file)) return await FollowCore(client, tasks, file, Read(file, instanceId, accountKey, operationId), token);
        }
        internal static async Task<CloudEnvironmentSetupResult> Publish(string vaultRoot, string instanceId, string accountKey, CloudBackend client, ICloudOnboardingConnection tasks, string operationId, CancellationToken token)
        {
            string file = FilePath(vaultRoot, operationId);
            using (var lease = Lock(file))
            {
                var record = Read(file, instanceId, accountKey, operationId);
                var followed = await FollowCore(client, tasks, file, record, token);
                if (record.State == "publishing" || record.State == "verifying" || record.State == "completed") return followed;
                if (record.State != "ready-to-publish") throw new InvalidOperationException("La préparation n'est pas prête pour publication. Vérifiez son suivi ; aucun nouvel envoi n'a été effectué.");
                try
                {
                    var config = await client.Get(record.ConfigurationId, token);
                    RequireBase(config, record);
                    if (config.Draft == null)
                    {
                        Sending(file, record, "bootstrapping-draft");
                        config = await client.BootstrapInitialDraft(record.ConfigurationId, record.BaseVersionId, config.Repositories, token);
                        record.State = "preparing-publication"; record.Stage = "draft-prepared"; Save(file, record);
                    }
                    RequireInitialDraft(config, record);
                    record.DraftRevision = config.Draft.Revision;
                    record.EnvironmentId = config.EnvironmentId;
                    if (String.IsNullOrEmpty(record.EnvironmentId)) throw Conflict("L'environnement de préparation n'est pas encore confirmé. Aucun début de publication n'a été envoyé.");
                    // Recheck the draft and base immediately before approval.
                    var fresh = await client.Get(record.ConfigurationId, token);
                    RequireInitialDraft(fresh, record);
                    if (fresh.Draft.Revision != record.DraftRevision || fresh.EnvironmentId != record.EnvironmentId) throw Conflict("Le brouillon initial a changé. Actualisez avant de publier.");
                    Sending(file, record, "beginning-approval");
                    var operation = await client.BeginApproval(record.ConfigurationId, null, record.DraftRevision, record.Id, token);
                    RequireOperation(operation, record, false);
                    record.RemoteOperationId = operation.Id; record.State = "publishing"; record.Stage = "approval-pending"; Save(file, record);
                    return Result(record, fresh);
                }
                catch (Exception error) { Failure(file, record, error); throw; }
            }
        }
        private static async Task<CloudEnvironmentSetupResult> FollowCore(CloudBackend client, ICloudOnboardingConnection tasks, string file, CloudEnvironmentSetupRecord record, CancellationToken token)
        {
            if (record.State == "failed" && record.FailureCode == "conflict" && record.Stage == "approval-pending" && !String.IsNullOrEmpty(record.RemoteOperationId))
            { record.State = "publishing"; record.FailureCode = null; record.HttpStatus = null; record.RequestId = null; Save(file, record); }
            if (record.State == "sending" || record.State == "preparing" || record.State == "preparing-publication")
            { record.State = "uncertain"; record.FailureCode = "interrupted-workflow"; Save(file, record); }
            if (record.State == "completed" || record.State == "failed" || record.State == "uncertain") return Result(record, await client.Get(record.ConfigurationId, token));
            if (record.State == "verifying") return await Verify(client, file, record, token);
            if (record.State == "publishing") return await FollowPublication(client, file, record, token);
            var thread = await tasks.Read(record.ThreadId, token);
            try { RequireThread(thread, record, true); }
            catch (CloudBackendUncertainException error) { Failure(file, record, error); throw; }
            if (thread.LatestTurn == null || thread.LatestTurn.Id != record.TurnId)
            { record.State = "uncertain"; record.FailureCode = "setup-turn-changed"; Save(file, record); return Result(record, null); }
            if (thread.AwaitingApproval) record.State = "awaiting-approval";
            else if (thread.AwaitingInput) record.State = "awaiting-input";
            else if (thread.LatestTurn.Status == "failed" || thread.LatestTurn.Status == "interrupted") { record.State = "failed"; record.FailureCode = "setup-" + thread.LatestTurn.Status; }
            else if (thread.Status == "idle" && thread.LatestTurn.Status == "completed") { record.State = "ready-to-publish"; record.Stage = "setup-completed"; }
            else record.State = "running";
            Save(file, record);
            return Result(record, await client.Get(record.ConfigurationId, token));
        }
        private static async Task<CloudEnvironmentSetupResult> FollowPublication(CloudBackend client, string file, CloudEnvironmentSetupRecord record, CancellationToken token)
        {
            try
            {
                var operation = await client.ReadOperation(record.RemoteOperationId, token);
                RequireOperation(operation, record, true);
                if (operation.State == "FAILED")
                { record.State = "failed"; record.FailureCode = Code(operation.FailureCode ?? "approval-failed"); Save(file, record); return Result(record, await client.Get(record.ConfigurationId, token)); }
                if (operation.State != "SUCCEEDED") return Result(record, null);
                var config = await client.Get(record.ConfigurationId, token);
                RequireCompletionConfiguration(config, record);
                Sending(file, record, "completing-approval");
                var published = await client.CompleteApproval(record.ConfigurationId, null, record.RemoteOperationId, null, token);
                if (record.ExpectedPublishedVersionId != null && published.VersionId != record.ExpectedPublishedVersionId) throw new CloudBackendUncertainException("la version publiée ne correspond pas à la publication confirmée");
                if (record.ExpectedPublishedVersionId == null) record.ExpectedPublishedVersionId = published.VersionId != record.BaseVersionId ? published.VersionId : null;
                record.State = "verifying"; record.Stage = "verifying"; Save(file, record);
                return await Verify(client, file, record, token);
            }
            catch (Exception error)
            {
                if (record.State == "sending" || error is CloudBackendUncertainException || (error is CloudBackendException && ((CloudBackendException)error).Code == "conflict")) Failure(file, record, error);
                throw;
            }
        }
        private static async Task<CloudEnvironmentSetupResult> Verify(CloudBackend client, string file, CloudEnvironmentSetupRecord record, CancellationToken token)
        {
            var config = await client.Get(record.ConfigurationId, token);
            bool ready = config.Status == "ready" && config.VersionRevision.HasValue && config.VersionRevision.Value > 1 && !String.IsNullOrEmpty(config.LatestReadyVersionId) && config.LatestReadyVersionId != record.BaseReadyVersionId && config.LatestReadyVersionId != record.BaseVersionId;
            if (record.ExpectedPublishedVersionId != null) ready = ready && config.LatestReadyVersionId == record.ExpectedPublishedVersionId;
            if (ready) { record.State = "completed"; record.Stage = "completed"; Save(file, record); }
            return Result(record, config);
        }
        private static void RequireThread(CloudTaskThread thread, CloudEnvironmentSetupRecord record, bool existing)
        {
            if (thread == null || String.IsNullOrEmpty(thread.Id) || thread.VerifiedEnvironmentConfigId != record.ConfigurationId || thread.AccountKey != record.AccountKey || (existing && thread.Id != record.ThreadId)) throw new CloudBackendUncertainException("identité de la conversation de préparation non confirmée");
        }
        private static void RequireBase(CloudConfiguration config, CloudEnvironmentSetupRecord record)
        {
            if (config == null || config.Id != record.ConfigurationId || config.IsOwner != true || config.VersionId != record.BaseVersionId || !RepositoryMatches(record.RepositoriesFingerprint, config.Repositories) || (!String.IsNullOrEmpty(config.ThreadId) && config.ThreadId != record.ThreadId)) throw Conflict("La configuration initiale ou son compte a changé. Aucun remplacement automatique n'a été effectué.");
        }
        private static void RequireInitialDraft(CloudConfiguration config, CloudEnvironmentSetupRecord record)
        {
            RequireBase(config, record);
            if (config.Draft == null || config.Draft.BaseVersionId != record.BaseVersionId || !RepositoryMatches(record.RepositoriesFingerprint, config.Draft.Repositories)) throw Conflict("Le brouillon initial ne correspond plus à la version ou aux dépôts préparés.");
        }
        private static void RequireCompletionConfiguration(CloudConfiguration config, CloudEnvironmentSetupRecord record)
        {
            if (config == null || config.Id != record.ConfigurationId || config.IsOwner != true || config.EnvironmentId != record.EnvironmentId || !RepositoryMatches(record.RepositoriesFingerprint, config.Repositories) || (!String.IsNullOrEmpty(config.ThreadId) && config.ThreadId != record.ThreadId)) throw Conflict("Le compte, les dépôts ou l'environnement ne correspondent pas à la publication confirmée.");
            if (config.VersionId == record.BaseVersionId)
            {
                RequireInitialDraft(config, record);
                if (config.Draft.Revision != record.DraftRevision) throw Conflict("Le brouillon initial a changé depuis le début de publication.");
                return;
            }
            if (config.Status != "ready" || !config.VersionRevision.HasValue || config.VersionRevision.Value <= 1 || config.VersionId != config.LatestReadyVersionId || config.VersionId == record.BaseReadyVersionId) throw Conflict("La nouvelle version de la publication initiale n'est pas confirmée.");
            record.ExpectedPublishedVersionId = config.VersionId;
        }
        private static void RequireOperation(CloudOperation operation, CloudEnvironmentSetupRecord record, bool existing)
        {
            if (operation == null || operation.Kind != "APPROVE_ENVIRONMENT_CONFIG_DRAFT" || operation.EnvironmentId != record.EnvironmentId || (existing && operation.Id != record.RemoteOperationId)) throw new CloudBackendUncertainException("identité de la publication initiale non confirmée");
        }
        private static CloudEnvironmentSetupResult Result(CloudEnvironmentSetupRecord record, CloudConfiguration config)
        { return new CloudEnvironmentSetupResult { Id = record.Id, State = record.State, Stage = record.Stage, ConfigurationId = record.ConfigurationId, ThreadId = record.ThreadId, TurnId = record.TurnId, RemoteOperationId = record.RemoteOperationId, Configuration = config, FailureCode = record.FailureCode, HttpStatus = record.HttpStatus, RequestId = record.RequestId }; }
        private static string FilePath(string root, string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) throw new InvalidOperationException("Identifiant de préparation Cloud invalide.");
            string directory = Path.Combine(Path.GetFullPath(root), "cloud-setups-v1"); SafeFiles.PrivateDirectory(directory);
            string file = Path.Combine(directory, id + ".json"); SafeFiles.RejectLinks(file); SafeFiles.RejectLinks(file + ".lock"); return file;
        }
        private static FileStream Lock(string file) { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        private static CloudEnvironmentSetupRecord Read(string file, string instance, string account, string id)
        {
            if (!File.Exists(file)) throw new InvalidOperationException("Préparation Cloud introuvable.");
            var record = Json.Read<CloudEnvironmentSetupRecord>(SafeFiles.ReadText(file));
            if (record == null || record.SchemaVersion != 1 || record.Id != id || record.InstanceId != instance || record.AccountKey != account) throw new InvalidOperationException("Cette préparation appartient à une autre instance ou à un autre compte.");
            return record;
        }
        private static CloudBackendException Conflict(string message) { return new CloudBackendException("conflict", message, 409); }
        private static string Code(string value) { return Regex.IsMatch(value ?? "", "^[A-Za-z0-9_.-]{1,100}$") ? value : "cloud-error"; }
        private static void Failure(string file, CloudEnvironmentSetupRecord record, Exception error)
        {
            var native = error as CloudBackendException;
            record.State = error is CloudBackendUncertainException || (record.State == "sending" && native == null) ? "uncertain" : "failed";
            record.FailureCode = native == null ? "local-setup-error" : Code(native.Code); record.HttpStatus = native == null ? null : native.HttpStatus; record.RequestId = native == null ? null : native.RequestId; Save(file, record);
        }
        private static void Sending(string file, CloudEnvironmentSetupRecord record, string stage) { record.State = "sending"; record.Stage = stage; Save(file, record); }
        private static void Save(string file, CloudEnvironmentSetupRecord record) { record.UpdatedUtc = DateTime.UtcNow.ToString("o"); SafeFiles.AtomicWrite(file, Encoding.UTF8.GetBytes(Json.Write(record))); }
        private static string Hash(object value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(value)))).Replace("-", "").ToLowerInvariant(); }
        private static string RepositoryHash(IEnumerable<CloudRepository> values) { return Hash((values ?? new CloudRepository[0]).OrderBy(value => value.RepositoryId, StringComparer.Ordinal).ThenBy(value => value.Ref, StringComparer.Ordinal).Select(value => new { id = value.RepositoryId, reference = value.Ref }).ToArray()); }
        private static bool RepositoryMatches(string fingerprint, IEnumerable<CloudRepository> values)
        {
            if (String.IsNullOrEmpty(fingerprint)) return false;
            // The first prototype hashed the complete DTO. Accept that exact hash for
            // existing receipts; new records always use the canonical repository scope.
            return RepositoryHash(values) == fingerprint || Hash(values == null ? null : values.ToList()) == fingerprint;
        }
    }
}
