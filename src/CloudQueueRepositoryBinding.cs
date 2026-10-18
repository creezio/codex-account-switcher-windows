using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class CloudQueueRepositoryReceipt
    {
        public string JobId { get; set; }
        public string Fingerprint { get; set; }
        public string AccountKey { get; set; }
        public string ConfigurationId { get; set; }
        public string ConfigurationVersion { get; set; }
        public string Repository { get; set; }
        public string[] RepositoryIds { get; set; }
        public string VerifiedUtc { get; set; }
    }

    internal static class CloudQueueRepositoryBinding
    {
        internal static async Task Require(RelayStore store, CloudJob job, CancellationToken token,
            Func<string, CancellationToken, Task<CloudGitHubRepository>> resolve = null)
        {
            RelayStore.MessageId(job.Id);
            string preparedId = job.Kind == "follow-up" ? job.RootJobId : job.Id;
            if (!InstanceRules.ValidId(preparedId)) return;
            string plannedRecord = "cloud-prepared-mission-" + preparedId + ".dpapi";
            if (!File.Exists(Path.Combine(store.Root, plannedRecord))) return;
            var mission = store.ReadRecord<CloudPreparedMission>(plannedRecord);
            if (mission.Id != preparedId || mission.Request == null || mission.Target == null ||
                mission.Target.InstanceId != job.InstanceId || mission.Target.ExpectedAccountKey != job.AccountKey ||
                mission.Target.ConfigurationId != job.ConfigurationId ||
                !String.Equals(mission.Repository, job.Repository, StringComparison.OrdinalIgnoreCase))
                throw Mismatch();
            var ids = (job.RepositoryIds ?? new string[0]).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) throw Mismatch();
            string record = "cloud-queue-repository-" + job.Id + ".dpapi";
            var prior = store.ReadRecord<CloudQueueRepositoryReceipt>(record);
            if (prior.JobId == job.Id && prior.Fingerprint == job.Fingerprint && prior.AccountKey == job.AccountKey &&
                prior.ConfigurationId == job.ConfigurationId && prior.ConfigurationVersion == job.ConfigurationVersion &&
                String.Equals(prior.Repository, job.Repository, StringComparison.OrdinalIgnoreCase) &&
                prior.RepositoryIds != null && prior.RepositoryIds.SequenceEqual(ids) && !String.IsNullOrEmpty(prior.VerifiedUtc)) return;
            bool found = false;
            if (resolve != null) found = await Contains(ids, job.Repository, resolve, token).ConfigureAwait(false);
            else
            {
                var accounts = new AccountService(Path.GetDirectoryName(store.Root));
                var instance = NamedInstances.Resolve(accounts, job.InstanceId);
                if (instance.AccountKey != job.AccountKey) throw Mismatch();
                using (var github = CloudInstanceConnection.OpenGitHub(accounts, instance))
                    found = await Contains(ids, job.Repository, (id, ct) => github.GetRepository(id, ct), token).ConfigureAwait(false);
            }
            if (!found) throw Mismatch();
            store.WriteRecord(record, new CloudQueueRepositoryReceipt {
                JobId = job.Id, Fingerprint = job.Fingerprint, AccountKey = job.AccountKey,
                ConfigurationId = job.ConfigurationId, ConfigurationVersion = job.ConfigurationVersion,
                Repository = job.Repository, RepositoryIds = ids, VerifiedUtc = DateTime.UtcNow.ToString("o")
            });
        }
        private static async Task<bool> Contains(IEnumerable<string> ids, string repository,
            Func<string, CancellationToken, Task<CloudGitHubRepository>> resolve, CancellationToken token)
        {
            foreach (string id in ids)
            {
                token.ThrowIfCancellationRequested();
                var actual = await resolve(id, token).ConfigureAwait(false);
                if (actual == null || actual.Id != id) throw Mismatch();
                if (String.Equals(actual.FullName, repository, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        private static CloudBackendException Mismatch()
        { return new CloudBackendException("queue-repository-mismatch", "Le dépôt GitHub réel de l’environnement ne correspond pas au dépôt de la mission préparée. Aucun nouveau chat ni prompt métier envoyé ; choisissez un environnement du bon dépôt."); }
    }
}
