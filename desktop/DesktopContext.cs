using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher.Desktop
{
    internal sealed class DesktopContext
    {
        internal readonly string Root;
        internal readonly RelayStore Store;
        internal readonly bool Fixture;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private DateTime lastFullRefresh = DateTime.MinValue;
        internal DesktopContext(string root, bool fixture = false)
        {
            Root = root;
            Store = new RelayStore(Path.Combine(root, "relay"));
            Fixture = fixture;
        }
        internal AccountService Accounts()
        {
            var s = new AccountService(Root);
            foreach (var p in s.Data.Profiles)
                UsageCoordinator.Merge(p, Store.ReadRecord<Profile>(RelayQuota.Name(p.Key)));
            return s;
        }
        internal async Task<T> Read<T>(Func<AccountService, T> action)
        {
            await gate.WaitAsync();
            try
            {
                return await Task.Run(() => action(Accounts()));
            }
            finally { gate.Release(); }
        }
        internal async Task Mutate(Func<AccountService, Task> action)
        {
            await gate.WaitAsync();
            try
            {
                await action(Accounts());
            }
            finally { gate.Release(); }
        }
        internal async Task Supervise()
        {
            if (Fixture)
                return;
            await gate.WaitAsync();
            try
            {
                await Task.Run(async delegate { var s = Accounts(); if (UsageCoordinator.Policies(Store).Background) { UsageCoordinator.Ensure(Store); return; } bool full = s.Settings.AutoRefresh && DateTime.UtcNow - lastFullRefresh >= TimeSpan.FromMinutes(5); var keys = (full ? s.Data.Profiles : s.Instances.ActiveProfiles()).Select(p => p.Key).ToArray(); foreach (string key in keys) if (full || UsageCoordinator.Automatic(UsageCoordinator.For(Store, key), s.Settings.AutoResetCredits)) await UsageCoordinator.Refresh(Store, key, true, CancellationToken.None); if (full) lastFullRefresh = DateTime.UtcNow; });
            }
            finally { gate.Release(); }
        }
        internal async Task Initialize()
        {
            if (Fixture)
                return;
            await Mutate(async a => { var policy = RelayPolicies.Load(Store); if (policy.KeepWorkerRunning || Store.ActiveMessages().Any()) RelayWorker.Ensure(Store); if (policy.AutoInstallManaged) foreach (var i in a.Data.Instances.Where(i => !i.IsLocal && !i.Archived)) await RelayIntegration.Install(Store, a.Instances.Home(i), a.Settings.CodexExecutable, false, CancellationToken.None); });
        }
    }
}
