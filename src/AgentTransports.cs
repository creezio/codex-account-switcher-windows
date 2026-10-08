using System;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal interface IRelayPermissionTransport
    {
        string Permissions(RelayChannel channel,string thread);
    }
    internal static class AgentProviders
    {
        public static string Kind(RelayChannel channel){return String.IsNullOrEmpty(channel.Provider)?"codex":channel.Provider;}
        public static bool Codex(RelayChannel channel){return Kind(channel)=="codex";}
        public static string Binding(RelayChannel c){return RemotePeers.Hash(Kind(c)+"|"+c.AccountKey+"|"+c.Home+"|"+c.Workspace+"|"+c.PeerId+"|"+c.RemoteChannel+"|"+c.RemoteWorkspace+"|"+c.RemoteAccount+"|"+c.RemoteDevice);}
        public static void Pin(RelayMessage m,RelayChannel c){m.TargetBinding=Binding(c);m.TargetExecutionWorkspace=Codex(c)?c.Workspace:c.RemoteWorkspace;m.TargetDevice=Codex(c)?"local":c.RemoteDevice;}
        public static string Caption(RelayChannel c){return Kind(c)=="remote"?"PC distant · connexion à vérifier":DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks)?"Connecté":"À reconnecter";}
        public static IRelayTransport Create(RelayStore store){return new AgentTransport(store);}
    }
    internal sealed class AgentTransport : IRelayTransport, IRelayPermissionTransport
    {
        private readonly IRelayTransport desktop=new DesktopRelayTransport();
        private readonly RemoteTransport remote;
        public AgentTransport(RelayStore store){remote=new RemoteTransport(store);}
        private IRelayTransport For(RelayChannel c)
        {
            switch(AgentProviders.Kind(c)){
                case "codex": return desktop;
                case "remote": return remote;
                default: throw new InvalidOperationException("Fournisseur non pris en charge : "+c.Provider);
            }
        }
        public void Verify(RelayChannel c){For(c).Verify(c);}
        public void VerifySend(RelayChannel c,string thread){For(c).VerifySend(c,thread);}
        public Task<object> Call(RelayChannel c,string tool,object args,CancellationToken token){return For(c).Call(c,tool,args,token);}
        public string Permissions(RelayChannel c,string thread){var p=For(c) as IRelayPermissionTransport;return p==null?RelayPermissions.Read(c.Home,thread):p.Permissions(c,thread);}
    }
}
