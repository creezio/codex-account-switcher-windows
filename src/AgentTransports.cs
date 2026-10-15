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
        public static bool Codex(RelayChannel channel){return Kind(channel)=="codex"||Kind(channel)=="codex-headless";}
        public static string Binding(RelayChannel c){string value=Kind(c)+"|"+c.AccountKey+"|"+c.Home+"|"+c.Workspace+"|"+c.PeerId+"|"+c.RemoteChannel+"|"+c.RemoteWorkspace+"|"+c.RemoteAccount+"|"+c.RemoteDevice;if(Kind(c)=="codex-headless")value+="|"+c.Executable+"|"+c.HeadlessSandbox+"|"+c.HeadlessApprovalPolicy;return RemotePeers.Hash(value);}
        public static void Pin(RelayMessage m,RelayChannel c){m.TargetProvider=Kind(c);m.TargetBinding=Binding(c);m.TargetExecutionWorkspace=Codex(c)?c.Workspace:c.RemoteWorkspace;m.TargetDevice=Codex(c)?"local":c.RemoteDevice;}
        public static string Caption(RelayChannel c){if(Kind(c)=="remote")return "PC distant · connexion à vérifier";if(Kind(c)=="codex-headless")return "Agent sans fenêtre · démarrage à la demande";try{return DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks)?"Connecté":"À reconnecter";}catch(System.ComponentModel.Win32Exception){return "À vérifier";}}
        public static bool? Online(RelayStore store,RelayChannel c){if(Kind(c)=="codex-headless"){try{HeadlessRelayTransport.Validate(store,c);return true;}catch(InvalidOperationException){return false;}}try{return Kind(c)=="codex"?(bool?)DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks):null;}catch(System.ComponentModel.Win32Exception){return null;}}
        public static IRelayTransport Create(RelayStore store){return new AgentTransport(store);}
    }
    internal sealed class AgentTransport : IRelayTransport, IRelayPermissionTransport
    {
        private readonly IRelayTransport desktop=new DesktopRelayTransport();
        private readonly RemoteTransport remote;
        private readonly HeadlessRelayTransport headless;
        public AgentTransport(RelayStore store){remote=new RemoteTransport(store);headless=new HeadlessRelayTransport(store);}
        private IRelayTransport For(RelayChannel c)
        {
            switch(AgentProviders.Kind(c)){
                case "codex": return desktop;
                case "codex-headless": return headless;
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
