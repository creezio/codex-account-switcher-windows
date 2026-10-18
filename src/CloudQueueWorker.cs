using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Creezio.Switcher
{
    internal sealed class CloudQueueWorkerState
    {
        public int Pid { get; set; }
        public long Started { get; set; }
        public string Version { get; set; }
        public string UpdatedUtc { get; set; }
        public string StartRequestedUtc { get; set; }
        public string LastSuccessfulTickUtc { get; set; }
        public string Error { get; set; }
    }

    // This scheduler owns only prepared requests. Existing Cloud workers keep
    // their turns and return delivery; no desktop instance needs to be stopped.
    internal static class CloudQueueWorker
    {
        internal const string Record = "cloud-queue-worker-v1.dpapi";
        internal const string Version = "2";
        internal const int AdmissionVersion = 5;
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

        private static bool ExactCloudHost(Process process, RelayWorkerState state, string root)
        {
            using (var self = Process.GetCurrentProcess()) if (process.Id == self.Id) return false;
            if (process.StartTime.ToUniversalTime().Ticks != state.Started) return false;
            string executable = process.MainModule.FileName;
            if (!new[] { "CodexAccountSwitcher.exe", "CreezioRelay.exe" }.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase)) return false;
            string command = ProcessResources.CommandLine(process.Id);
            if (String.IsNullOrEmpty(command)) return false;
            int count; var argv = CommandLineToArgvW(command, out count);
            if (argv == IntPtr.Zero) return false;
            try
            {
                if (count != 3) return false;
                var args = new string[count];
                for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return args[1] == "--cloud-worker" && RelayStore.SamePath(args[0], executable) && RelayStore.SamePath(args[2], root);
            }
            finally { LocalFree(argv); }
        }

        internal static bool TryRetireQuiescentLegacy(RelayStore store, RelayWorkerState expected)
        {
            // A source may be busy while terminal results wait for delivery.
            // Waiting for every remote turn to finish would starve free accounts.
            // Pin its exact role/PID/start and acquire every job's run lease.
            // This proves that no local RPC or delivery is in progress. Remote
            // Cloud turns continue; the replacement monitors their same IDs.
            var leases = new List<FileStream>();
            try
            {
                leases.Add(store.Lease("cloud-capacity-v1"));
                leases.Add(store.Lease("cloud-registry-v1"));
                var jobs = new CloudJobs(store).List();
                foreach (var thread in jobs.Where(job => !String.IsNullOrEmpty(job.AccountKey) && !String.IsNullOrEmpty(job.ThreadId))
                    .Select(job => RemotePeers.Hash(job.AccountKey + "|" + job.ThreadId)).Distinct().OrderBy(value => value, StringComparer.Ordinal))
                    leases.Add(store.Lease("cloud-follow-up-" + thread));
                foreach (var job in jobs.OrderBy(job => job.Id, StringComparer.Ordinal)) leases.Add(store.Lease("cloud-run-" + job.Id));
                var current = store.ReadRecord<RelayWorkerState>("cloud-worker-v1.dpapi");
                if (current.Pid != expected.Pid || current.Started != expected.Started || current.CloudQueueAdmissionVersion.GetValueOrDefault() >= AdmissionVersion) return false;
                var latest = new CloudJobs(store).List();
                if (!jobs.Select(job => job.Id).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(latest.Select(job => job.Id).OrderBy(id => id, StringComparer.Ordinal))) return false;
                using (var process = Process.GetProcessById(current.Pid))
                {
                    if (!ExactCloudHost(process, current, store.Root)) return false;
                    process.Kill();
                    if (!process.WaitForExit(1500)) return false;
                }
                current.Pid = 0;
                current.Updated = DateTime.UtcNow.ToString("o");
                store.WriteRecord("cloud-worker-v1.dpapi", current);
                return true;
            }
            catch (IOException error)
            {
                int code = error.HResult & 65535;
                if (code == 32 || code == 33) return false;
                throw;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            finally { for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose(); }
        }

        internal static bool RuntimeReady(RelayStore store)
        {
            var runtime = store.ReadRecord<RelayWorkerState>("cloud-worker-v1.dpapi");
            if (DesktopRuntime.SameProcess(runtime.Pid, runtime.Started))
            {
                if (runtime.CloudQueueAdmissionVersion.GetValueOrDefault() >= AdmissionVersion) return true;
                if (!TryRetireQuiescentLegacy(store, runtime)) return false;
            }
            // Start the compatible host before materializing requests. Older
            // callers then reuse this same live host, including its file guard.
            CloudJobWorker.Ensure(store);
            return false;
        }

        internal static void Ensure(RelayStore store)
        {
            if (HeadlessSourceBroker.IsOwnedChild(store) || !new CloudMissionQueue(store).HasEnabledTeams()) return;
            using (store.Lease("cloud-queue-worker-start-v1"))
            {
                var state = store.ReadRecord<CloudQueueWorkerState>(Record);
                if (DesktopRuntime.SameProcess(state.Pid, state.Started)) return;
                DateTime requested;
                if (DateTime.TryParse(state.StartRequestedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out requested) &&
                    DateTime.UtcNow - requested.ToUniversalTime() < TimeSpan.FromSeconds(30)) return;
                string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexAccountSwitcher.exe");
                if (!File.Exists(executable) || !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexAccountSwitcher.dll")))
                    throw new InvalidOperationException("La file préparée nécessite la livraison complète du Switcher avec son runtime .NET 10.");
                state.StartRequestedUtc = DateTime.UtcNow.ToString("o");
                store.WriteRecord(Record, state);
                var start = new ProcessStartInfo(executable, "--cloud-queue-worker " + RelayWorker.Quote(store.Root))
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                start.EnvironmentVariables.Remove("CODEX_THREAD_ID");
                start.EnvironmentVariables.Remove("CODEX_APP_TOOLS_PIPE_PATH");
                try { using (var process = Process.Start(start)) { process.StandardInput.Close(); } }
                catch { state.StartRequestedUtc = null; store.WriteRecord(Record, state); throw; }
            }
        }

        internal static int Run(string root)
        {
            var store = new RelayStore(root);
            FileStream lease;
            try { lease = store.Lease("cloud-queue-worker-v1"); }
            catch (IOException) { return 0; }
            using (lease)
            using (var self = Process.GetCurrentProcess())
            {
                var state = new CloudQueueWorkerState { Pid = self.Id, Started = self.StartTime.ToUniversalTime().Ticks, Version = Version };
                var queue = new CloudMissionQueue(store);
                try
                {
                    while (true)
                    {
                        state.UpdatedUtc = DateTime.UtcNow.ToString("o");
                        store.WriteRecord(Record, state);
                        try
                        {
                            if (!queue.HasEnabledTeams()) break;
                            CloudQueueSnapshot snapshot;
                            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                                snapshot = queue.Tick(deadline.Token, 0).GetAwaiter().GetResult();
                            if (!CloudMissionQueue.HasReadyAdmissions(snapshot) || RuntimeReady(store))
                            {
                                if (CloudMissionQueue.HasReadyAdmissions(snapshot))
                                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                                        queue.Tick(deadline.Token).GetAwaiter().GetResult();
                                state.LastSuccessfulTickUtc = DateTime.UtcNow.ToString("o");
                                state.Error = null;
                            }
                            else state.Error = "La file attend le suivi Cloud compatible. Les missions de l’ancien suivi continuent normalement ; aucune instance Codex n’est fermée.";
                            var jobs = new CloudJobs(store);
                            if (CloudJobWorker.HasRemotePending(jobs)) CloudJobWorker.Ensure(store);
                            if (jobs.LocalReturns().Any()) RelayWorker.Ensure(store);
                        }
                        catch (Exception error) { state.Error = Program.SafeError(error); }
                        state.UpdatedUtc = DateTime.UtcNow.ToString("o");
                        store.WriteRecord(Record, state);
                        Thread.Sleep(5000);
                    }
                }
                finally
                {
                    state.Pid = 0;
                    state.StartRequestedUtc = null;
                    state.UpdatedUtc = DateTime.UtcNow.ToString("o");
                    store.WriteRecord(Record, state);
                }
            }
            return 0;
        }
    }
}
