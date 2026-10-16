using System;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class IntegrationMaintenance
    {
        internal static bool Due(Settings settings,RelayIntegrationState state,DateTime now,bool force)
        {
            if(!force&&!settings.MaintainIntegration)return false;
            DateTime previous;return force||!DateTime.TryParse(state.Updated,out previous)||now-previous.ToUniversalTime()>=TimeSpan.FromMinutes(Math.Max(1,Math.Min(120,settings.IntegrationCheckMinutes)));
        }
        internal static async Task Check(RelayStore store,string home,Settings settings,bool force,bool repair,CancellationToken token)
        {
            if(!Due(settings,RelayIntegration.Status(store,home),DateTime.UtcNow,force))return;
            try{await RelayIntegration.Install(store,home,settings.CodexExecutable,repair,token);}
            catch(OperationCanceledException){throw;}
            catch(Exception e){var state=RelayIntegration.Status(store,home);state.Home=home;state.Healthy=false;state.Status=Program.SafeError(e);state.Updated=DateTime.UtcNow.ToString("o");store.WriteRecord("integration-"+RelayIntegration.HomeKey(home)+".dpapi",state);}
        }
    }
}
