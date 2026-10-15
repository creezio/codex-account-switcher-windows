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
    internal sealed class CloudEnvironmentUpdateResult
    {
        public string Id { get; set; }
        public string State { get; set; }
        public string Stage { get; set; }
        public string ConfigurationId { get; set; }
        public string DraftId { get; set; }
        public string RemoteOperationId { get; set; }
        public CloudConfiguration Configuration { get; set; }
        public string FailureCode { get; set; }
        public int? HttpStatus { get; set; }
        public string RequestId { get; set; }
    }

    // Persist only identifiers, revisions and request hashes. Installation commands and
    // startup instructions can contain secrets; they must never enter this journal.
    internal sealed class CloudEnvironmentUpdateRecord
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public string InstanceId { get; set; }
        public string AccountKey { get; set; }
        public string ConfigurationId { get; set; }
        public string Fingerprint { get; set; }
        public string BaseVersionId { get; set; }
        public string BaseReadyVersionId { get; set; }
        public string RepositoriesFingerprint { get; set; }
        public string DraftId { get; set; }
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

    internal static class CloudEnvironmentUpdate
    {
        internal static CloudEnvironmentUpdateResult[] Recent(AccountService accounts, DesktopInstance instance)
        {
            string directory = Path.Combine(Path.GetFullPath(accounts.Vault.Root), "cloud-updates-v1");
            if (!Directory.Exists(directory)) return new CloudEnvironmentUpdateResult[0];
            SafeFiles.RejectLinks(directory);
            var records = new List<CloudEnvironmentUpdateRecord>();
            foreach (string file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!Regex.IsMatch(Path.GetFileNameWithoutExtension(file), "^[a-f0-9]{32}$")) continue;
                var record = Json.Read<CloudEnvironmentUpdateRecord>(SafeFiles.ReadText(file));
                if (record != null && record.SchemaVersion == 1 && record.InstanceId == instance.Id && record.AccountKey == instance.AccountKey) records.Add(record);
            }
            return records.OrderByDescending(value => value.CreatedUtc, StringComparer.Ordinal).Take(100).Select(value => Result(value, null)).ToArray();
        }

        internal static async Task<CloudEnvironmentUpdateResult> Update(AccountService accounts, DesktopInstance instance, string configurationId, CloudDraftPatch patch, string operationId, CancellationToken token)
        {
            using (var client = CloudInstanceConnection.Open(accounts, instance))
                return await Update(accounts.Vault.Root, instance.Id, instance.AccountKey, client, configurationId, patch, operationId, token);
        }

        internal static async Task<CloudEnvironmentUpdateResult> Follow(AccountService accounts, DesktopInstance instance, string operationId, CancellationToken token)
        {
            using (var client = CloudInstanceConnection.Open(accounts, instance))
                return await Follow(accounts.Vault.Root, instance.Id, instance.AccountKey, client, operationId, token);
        }

        internal static async Task<CloudEnvironmentUpdateResult> Update(string vaultRoot, string instanceId, string accountKey, CloudBackend client, string configurationId, CloudDraftPatch patch, string operationId, CancellationToken token)
        {
            if (patch == null || String.IsNullOrEmpty(patch.BaseVersionId) || patch.ExpectedRevision != 0)
                throw new InvalidOperationException("Cette modification attend une version précise sans brouillon existant (ExpectedRevision = 0).");
            if (patch.Repositories == null && patch.NetworkPolicy == null && patch.InstallScript == null && patch.Cwd == null && patch.StartSkill == null)
                throw new InvalidOperationException("Aucune modification de l'environnement Cloud n'a été demandée.");
            // Freeze the caller's intent before any await. UI changes cannot alter a request mid-flight.
            patch = Json.Read<CloudDraftPatch>(Json.Write(patch));
            string file = FilePath(vaultRoot, operationId), fingerprint = Hash(new { configurationId = configurationId, patch = patch });
            using (var lease = Lock(file))
            {
                if (File.Exists(file))
                {
                    var existing = Read(file, instanceId, accountKey, operationId);
                    if (existing.ConfigurationId != configurationId || existing.Fingerprint != fingerprint)
                        throw new InvalidOperationException("Cet identifiant Cloud appartient à une autre demande de modification.");
                    return await Advance(client, file, existing, token);
                }
                var record = new CloudEnvironmentUpdateRecord { SchemaVersion = 1, Id = operationId, InstanceId = instanceId, AccountKey = accountKey, ConfigurationId = configurationId, Fingerprint = fingerprint, BaseVersionId = patch.BaseVersionId, State = "preparing", Stage = "reading", CreatedUtc = DateTime.UtcNow.ToString("o") };
                if (String.IsNullOrEmpty(instanceId) || String.IsNullOrEmpty(accountKey)) throw new InvalidOperationException("Association Cloud incomplète.");
                Save(file, record);
                try
                {
                    var current = await client.Get(configurationId, token);
                    if (current.IsOwner != true || current.VersionId != patch.BaseVersionId || current.Draft != null)
                        throw Conflict("La version de cet environnement a changé ou un brouillon existe déjà. Actualisez avant toute modification ; aucun brouillon existant ne sera remplacé.");
                    record.BaseReadyVersionId = current.LatestReadyVersionId;
                    record.RepositoriesFingerprint = RepositoryHash(patch.Repositories ?? current.Repositories);
                    Sending(file, record, "creating-draft");
                    var created = await client.CreateDraft(configurationId, false, token);
                    record.DraftId = created.DraftId;
                    record.EnvironmentId = created.EnvironmentId;
                    record.State = "preparing"; record.Stage = "draft-created"; Save(file, record);
                    var draft = await client.Get(configurationId, record.DraftId, token);
                    RequireDraft(draft, record, null);
                    record.DraftRevision = draft.Draft.Revision;
                    var change = Json.Read<CloudDraftPatch>(Json.Write(patch));
                    change.ExpectedRevision = record.DraftRevision;
                    Sending(file, record, "patching-draft");
                    var changed = await client.PatchDraft(configurationId, record.DraftId, change, token);
                    try { RequireDraft(changed, record, null); }
                    catch (CloudBackendException) { throw new CloudBackendUncertainException("version du brouillon modifié non confirmée"); }
                    if (changed.Draft.Revision <= record.DraftRevision || !Matches(change, changed.Draft))
                        throw new CloudBackendUncertainException("brouillon modifié sans confirmation des valeurs attendues");
                    record.DraftRevision = changed.Draft.Revision;
                    record.State = "preparing"; record.Stage = "draft-patched"; Save(file, record);
                    // A fresh revision prevents publishing somebody else's concurrent edits.
                    var fresh = await client.Get(configurationId, record.DraftId, token);
                    RequireDraft(fresh, record, record.DraftRevision);
                    if (!Matches(change, fresh.Draft)) throw Conflict("Le brouillon Cloud a été modifié après son enregistrement. Sa publication a été arrêtée.");
                    Sending(file, record, "beginning-approval");
                    var approval = await client.BeginApproval(configurationId, record.DraftId, record.DraftRevision, record.Id, token);
                    RequireOperation(approval, record, false);
                    record.RemoteOperationId = approval.Id;
                    record.State = "publishing"; record.Stage = "approval-pending"; Save(file, record);
                    return Result(record, fresh);
                }
                catch (Exception error) { Failure(file, record, error); throw; }
            }
        }

        internal static async Task<CloudEnvironmentUpdateResult> Follow(string vaultRoot, string instanceId, string accountKey, CloudBackend client, string operationId, CancellationToken token)
        {
            string file = FilePath(vaultRoot, operationId);
            using (var lease = Lock(file))
            {
                if (!File.Exists(file)) throw new InvalidOperationException("Cette opération de modification Cloud est introuvable.");
                return await Advance(client, file, Read(file, instanceId, accountKey, operationId), token);
            }
        }

        private static async Task<CloudEnvironmentUpdateResult> Advance(CloudBackend client, string file, CloudEnvironmentUpdateRecord record, CancellationToken token)
        {
            if (record.State == "failed" && record.FailureCode == "conflict" && record.Stage == "approval-pending" && !String.IsNullOrEmpty(record.RemoteOperationId))
            { record.State = "publishing"; record.FailureCode = null; record.HttpStatus = null; record.RequestId = null; Save(file, record); }
            // A process may have crashed between sending a write and recording its response.
            // Readback is safe, but never recreate or resend that write automatically.
            if (record.State == "sending" || record.State == "preparing")
            {
                record.State = "uncertain"; record.FailureCode = "interrupted-workflow"; Save(file, record);
            }
            if (record.State == "completed" || record.State == "failed" || record.State == "uncertain")
                return Result(record, await client.Get(record.ConfigurationId, token));
            if (record.State == "verifying") return await Verify(client, file, record, token);
            if (record.State != "publishing" || String.IsNullOrEmpty(record.RemoteOperationId))
                throw new InvalidOperationException("État de suivi Cloud incompatible ; aucun nouvel envoi n'a été effectué.");
            try
            {
                var operation = await client.ReadOperation(record.RemoteOperationId, token);
                RequireOperation(operation, record, true);
                if (operation.State == "FAILED")
                {
                    record.State = "failed"; record.FailureCode = Code(operation.FailureCode ?? "approval-failed"); Save(file, record);
                    return Result(record, await client.Get(record.ConfigurationId, token));
                }
                if (operation.State != "SUCCEEDED") return Result(record, null);
                var fresh = await client.Get(record.ConfigurationId, token);
                if (fresh.VersionId == record.BaseVersionId) fresh = await client.Get(record.ConfigurationId, record.DraftId, token);
                RequireCompletionConfiguration(fresh, record);
                Sending(file, record, "completing-approval");
                // No setup conversation was created, therefore no unrelated thread ID is supplied.
                var published = await client.CompleteApproval(record.ConfigurationId, record.DraftId, record.RemoteOperationId, null, token);
                if (record.ExpectedPublishedVersionId != null && published.VersionId != record.ExpectedPublishedVersionId) throw new CloudBackendUncertainException("la version publiée ne correspond pas à la publication confirmée");
                if (record.ExpectedPublishedVersionId == null) record.ExpectedPublishedVersionId = published.VersionId != record.BaseVersionId ? published.VersionId : null;
                record.State = "verifying"; record.Stage = "verifying"; Save(file, record);
                return await Verify(client, file, record, token);
            }
            catch (Exception error)
            {
                // Read failures preserve a known pending/verifying operation for later follow-up.
                if (record.State == "sending" || error is CloudBackendUncertainException || (error is CloudBackendException && ((CloudBackendException)error).Code == "conflict")) Failure(file, record, error);
                throw;
            }
        }

        private static async Task<CloudEnvironmentUpdateResult> Verify(CloudBackend client, string file, CloudEnvironmentUpdateRecord record, CancellationToken token)
        {
            var current = await client.Get(record.ConfigurationId, token);
            bool ready = current.Status == "ready" && current.VersionRevision.HasValue && current.VersionRevision.Value > 1 && !String.IsNullOrEmpty(current.LatestReadyVersionId) && current.LatestReadyVersionId != record.BaseReadyVersionId && current.LatestReadyVersionId != record.BaseVersionId;
            if (!String.IsNullOrEmpty(record.ExpectedPublishedVersionId)) ready = ready && current.LatestReadyVersionId == record.ExpectedPublishedVersionId;
            if (ready) { record.State = "completed"; record.Stage = "completed"; Save(file, record); }
            return Result(record, current);
        }

        private static void RequireDraft(CloudConfiguration config, CloudEnvironmentUpdateRecord record, int? revision)
        {
            if (config.Id != record.ConfigurationId || config.VersionId != record.BaseVersionId || config.Draft == null || config.Draft.Id != record.DraftId || config.Draft.BaseVersionId != record.BaseVersionId || (revision.HasValue && config.Draft.Revision != revision.Value))
                throw Conflict("Le brouillon ou sa version de base a changé. Aucun remplacement automatique n'a été effectué.");
        }
        private static void RequireOperation(CloudOperation operation, CloudEnvironmentUpdateRecord record, bool existing)
        {
            if (operation == null || operation.Kind != "APPROVE_ENVIRONMENT_CONFIG_DRAFT" || operation.EnvironmentId != record.EnvironmentId || (existing && operation.Id != record.RemoteOperationId))
                throw new CloudBackendUncertainException("identité de l'opération distante non confirmée");
        }
        private static void RequireCompletionConfiguration(CloudConfiguration config, CloudEnvironmentUpdateRecord record)
        {
            if (config == null || config.Id != record.ConfigurationId || config.IsOwner != true || config.EnvironmentId != record.EnvironmentId || String.IsNullOrEmpty(record.RepositoriesFingerprint)) throw Conflict("Le compte ou l'environnement ne correspondent pas à la publication confirmée.");
            if (config.VersionId == record.BaseVersionId)
            {
                RequireDraft(config, record, record.DraftRevision);
                if (RepositoryHash(config.Draft.Repositories) != record.RepositoriesFingerprint) throw Conflict("Les dépôts du brouillon ne correspondent pas à la publication confirmée.");
                return;
            }
            if (RepositoryHash(config.Repositories) != record.RepositoriesFingerprint) throw Conflict("Les dépôts de la nouvelle version ne correspondent pas à la publication confirmée.");
            if (config.Status != "ready" || !config.VersionRevision.HasValue || config.VersionRevision.Value <= 1 || config.VersionId != config.LatestReadyVersionId || config.VersionId == record.BaseReadyVersionId) throw Conflict("La nouvelle version de la publication n'est pas confirmée.");
            record.ExpectedPublishedVersionId = config.VersionId;
        }
        private static bool Matches(CloudDraftPatch expected, CloudConfigurationDraft actual)
        {
            if (expected.InstallScript != null && expected.InstallScript != actual.InstallScript) return false;
            if (expected.Cwd != null && expected.Cwd != actual.Cwd) return false;
            if (expected.StartSkill != null && expected.StartSkill != actual.StartSkill) return false;
            if (expected.NetworkPolicy != null && !SameNetwork(expected.NetworkPolicy, actual.NetworkPolicy)) return false;
            if (expected.Repositories != null && (actual.Repositories == null || expected.Repositories.Count != actual.Repositories.Count || expected.Repositories.Any(w => !actual.Repositories.Any(a => a.RepositoryId == w.RepositoryId && a.Ref == w.Ref && (!w.Depth.HasValue || w.Depth == a.Depth))))) return false;
            return true;
        }
        private static bool SameNetwork(CloudNetworkPolicy expected, CloudNetworkPolicy actual)
        {
            if (actual == null || expected.Type != actual.Type) return false;
            return Sequence(expected.Presets).SequenceEqual(Sequence(actual.Presets)) && Sequence(expected.EgressHosts).SequenceEqual(Sequence(actual.EgressHosts));
        }
        private static IEnumerable<string> Sequence(IEnumerable<string> values) { return (values ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal); }
        private static string RepositoryHash(IEnumerable<CloudRepository> values) { return Hash((values ?? new CloudRepository[0]).OrderBy(value => value.RepositoryId, StringComparer.Ordinal).ThenBy(value => value.Ref, StringComparer.Ordinal).Select(value => new { id = value.RepositoryId, reference = value.Ref }).ToArray()); }
        private static CloudEnvironmentUpdateResult Result(CloudEnvironmentUpdateRecord record, CloudConfiguration config)
        { return new CloudEnvironmentUpdateResult { Id = record.Id, State = record.State, Stage = record.Stage, ConfigurationId = record.ConfigurationId, DraftId = record.DraftId, RemoteOperationId = record.RemoteOperationId, Configuration = config, FailureCode = record.FailureCode, HttpStatus = record.HttpStatus, RequestId = record.RequestId }; }
        private static CloudBackendException Conflict(string message) { return new CloudBackendException("conflict", message, 409); }
        private static string Code(string value) { return Regex.IsMatch(value ?? "", "^[A-Za-z0-9_.-]{1,100}$") ? value : "cloud-error"; }
        private static void Failure(string file, CloudEnvironmentUpdateRecord record, Exception error)
        {
            var native = error as CloudBackendException;
            bool uncertain = error is CloudBackendUncertainException || (record.State == "sending" && native == null);
            record.State = uncertain ? "uncertain" : "failed";
            record.FailureCode = native == null ? "local-workflow-error" : Code(native.Code);
            record.HttpStatus = native == null ? null : native.HttpStatus;
            record.RequestId = native == null ? null : native.RequestId;
            Save(file, record);
        }
        private static string Hash(object value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(value)))).Replace("-", "").ToLowerInvariant(); }
        private static string FilePath(string root, string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) throw new InvalidOperationException("Identifiant d'opération Cloud invalide.");
            string directory = Path.Combine(Path.GetFullPath(root), "cloud-updates-v1"); SafeFiles.PrivateDirectory(directory);
            string file = Path.Combine(directory, id + ".json"); SafeFiles.RejectLinks(file); SafeFiles.RejectLinks(file + ".lock"); return file;
        }
        private static FileStream Lock(string file) { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        private static CloudEnvironmentUpdateRecord Read(string file, string instance, string account, string id)
        {
            var record = Json.Read<CloudEnvironmentUpdateRecord>(SafeFiles.ReadText(file));
            if (record == null || record.SchemaVersion != 1 || record.Id != id || record.InstanceId != instance || record.AccountKey != account)
                throw new InvalidOperationException("Cette opération Cloud appartient à une autre instance ou à un autre compte.");
            return record;
        }
        private static void Sending(string file, CloudEnvironmentUpdateRecord record, string stage)
        { record.State = "sending"; record.Stage = stage; Save(file, record); }
        private static void Save(string file, CloudEnvironmentUpdateRecord record)
        { record.UpdatedUtc = DateTime.UtcNow.ToString("o"); SafeFiles.AtomicWrite(file, Encoding.UTF8.GetBytes(Json.Write(record))); }
    }
}
