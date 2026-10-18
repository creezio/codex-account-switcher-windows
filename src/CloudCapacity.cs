using System;
using System.Collections.Generic;
using System.Linq;

namespace Creezio.Switcher
{
    internal sealed class CloudCapacityPreferences
    {
        public int MaxParallel { get; set; }
        public CloudCapacityPreferences() { MaxParallel = CloudCapacity.DefaultMaximum; }
    }

    internal static class CloudCapacity
    {
        internal const string Record = "cloud-capacity-preferences.dpapi";
        internal const int DefaultMaximum = 2;
        internal const int Minimum = 1;
        internal const int Maximum = 8;

        internal static int ValidateMaximum(int maximum)
        {
            if (maximum < Minimum || maximum > Maximum)
                throw new InvalidOperationException("Le nombre de missions Cloud simultanées doit être compris entre 1 et 8.");
            return maximum;
        }

        internal static CloudCapacityPreferences Read(RelayStore store)
        {
            var value = store.ReadRecord<CloudCapacityPreferences>(Record);
            if (value == null) throw new InvalidOperationException("La préférence de capacité Cloud est illisible.");
            return new CloudCapacityPreferences { MaxParallel = ValidateMaximum(value.MaxParallel) };
        }

        internal static void Save(RelayStore store, CloudCapacityPreferences value)
        {
            if (value == null) throw new InvalidOperationException("Indiquez le nombre de missions Cloud simultanées.");
            var validated = new CloudCapacityPreferences { MaxParallel = ValidateMaximum(value.MaxParallel) };
            // Share admission's short lock. A change affects future starts only;
            // it never changes, cancels or restarts an existing Cloud mission.
            using (store.Lease("cloud-capacity-v1")) store.WriteRecord(Record, validated);
        }

        internal static bool CanStart(CloudJob current, IEnumerable<CloudJob> jobs, int maximum)
        {
            ValidateMaximum(maximum);
            if (current == null) throw new ArgumentNullException("current");
            if (jobs == null) throw new ArgumentNullException("jobs");
            if (!NeedsAdmission(current)) return true;
            var inFlight = jobs.Where(job => job.Id != current.Id && ConsumesSlot(job)).ToArray();
            return inFlight.Length < maximum && !inFlight.Any(job => job.AccountKey == current.AccountKey);
        }

        internal static bool NeedsAdmission(CloudJob job)
        {
            return job.State == "queued" || job.Kind == "follow-up" && String.IsNullOrEmpty(job.TurnId) &&
                new[] { "creating", "prepared", "attention" }.Contains(job.State);
        }

        internal static bool ConsumesSlot(CloudJob job)
        {
            // A follow-up inherits its completed parent's ThreadId. That ID is
            // not evidence that this new prompt has started. Once StartTurn may
            // have been called, sending/uncertain still reserve a slot even if
            // its reply did not supply the new TurnId.
            if (job.Kind == "follow-up" && String.IsNullOrEmpty(job.TurnId) &&
                new[] { "creating", "prepared", "attention" }.Contains(job.State)) return false;
            return new[] { "creating", "prepared", "sending", "running" }.Contains(job.State) ||
                ((job.State == "attention" || job.State == "uncertain") && !String.IsNullOrEmpty(job.ThreadId));
        }
    }
}
