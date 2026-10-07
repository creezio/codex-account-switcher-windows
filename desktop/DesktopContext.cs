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
        private ResourceLinkServer resourceLinks;
        internal void StopResourceLinks(){resourceLinks?.Dispose();resourceLinks=null;}
        internal Func<DesktopInstance,InstanceState> InstanceStateFixture;
        internal Func<string,Task<string>> InstanceWindowFixture;
        internal Func<string,bool,InstanceState,Task<string>> InstanceLifecycleFixture;
        internal async Task<string> CloseOrRestartInstance(string id,bool restart,InstanceState expected)
        {
            if(Fixture)return await (InstanceLifecycleFixture?.Invoke(id,restart,expected)??Task.FromException<string>(new InvalidOperationException("Gestion non configurée dans cette recette.")));
            await gate.WaitAsync();
            try{return await DesktopWindows.CloseOrRestart(Accounts(),id,restart,expected,CancellationToken.None);}finally{gate.Release();}
        }
        internal InstanceState InstanceState(AccountService accounts,DesktopInstance instance)=>Fixture?(InstanceStateFixture?.Invoke(instance)??new InstanceState{Running=false,Phase="stopped"}):accounts.Instances.Runtime.Probe(instance);
        internal async Task<string> OpenOrShowInstance(string id)
        {
            if(Fixture)return await (InstanceWindowFixture?.Invoke(id)??Task.FromException<string>(new InvalidOperationException("Activation non configurée dans cette recette.")));
            await gate.WaitAsync();
            try{return await DesktopWindows.OpenOrShow(Accounts(),id,CancellationToken.None);}finally{gate.Release();}
        }
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private DateTime lastFullRefresh = DateTime.MinValue;
        private readonly SemaphoreSlim integrationGate = new SemaphoreSlim(1, 1);
        internal Func<string, Settings, Task> VerifyIntegrationFixture;
        internal async Task<RelayIntegrationState> VerifyIntegration(string instanceId)
        {
            await integrationGate.WaitAsync();
            try {
                var target = await Read(a => {
                    var instance = a.Data.Instances.Single(i => i.Id == instanceId);
                    if (instance.Archived) throw new InvalidOperationException("Restaurez cette instance avant de vérifier son intégration.");
                    return new { a.Settings, Home = a.Instances.Home(instance) };
                });
                if (Fixture) {
                    if (VerifyIntegrationFixture == null) throw new InvalidOperationException("Vérification non configurée dans cette recette.");
                    await VerifyIntegrationFixture(target.Home, target.Settings);
                } else await IntegrationMaintenance.Check(Store, target.Home, target.Settings, true, true, CancellationToken.None);
                // Maintenance persists failures as well as success. Completing the call alone is not success.
                return RelayIntegration.Status(Store, target.Home);
            } finally { integrationGate.Release(); }
        }
        internal async Task MaintainIntegrations(bool force = false, string instanceId = null, bool repair = false)
        {
            if (Fixture) return;
            await integrationGate.WaitAsync();
            try {
                var data = await Read(a => new { a.Settings, Homes = a.Data.Instances.Where(i => !i.Archived && (instanceId == null || i.Id == instanceId)).Select(a.Instances.Home).ToArray() });
                foreach (var home in data.Homes) await IntegrationMaintenance.Check(Store, home, data.Settings, force, repair, CancellationToken.None);
            } finally { integrationGate.Release(); }
        }
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
            await MaintainIntegrations();
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
            if(resourceLinks==null)resourceLinks=ResourceLinkServer.Start(Store);
            if (RemotePeers.Config(Store).Enabled) RemoteGateway.Start(Store);
            await Mutate(a => { a.Instances.AdoptAccounts(); var policy = RelayPolicies.Load(Store); if (policy.KeepWorkerRunning || Store.ActiveMessages().Any() || new Assistance(Store).List().Any(t=>t.State=="answered"||t.State=="delivering")) RelayWorker.Ensure(Store); return Task.CompletedTask; });
            if (await Read(a=>a.Settings.MaintainIntegration)) await MaintainIntegrations(true);
        }
    }
}
