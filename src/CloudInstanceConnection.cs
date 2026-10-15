using System;
using System.IO;
using System.Linq;

namespace Creezio.Switcher
{
    internal static class CloudInstanceConnection
    {
        internal static CloudBackend Open(AccountService accounts,DesktopInstance instance)
        {
            var read=Reader(accounts,instance);return CloudBackend.Create(read,CloudBackend.Capture(read()));
        }
        internal static ICloudTasksConnection OpenTasks(AccountService accounts,DesktopInstance instance)
        {
            var read=Reader(accounts,instance);return CloudTasksBackend.Create(read,CloudBackend.Capture(read()));
        }
        internal static ICloudOnboardingConnection OpenOnboarding(AccountService accounts,DesktopInstance instance)
        {
            var read=Reader(accounts,instance);return CloudTasksBackend.Create(read,CloudBackend.Capture(read()));
        }
        internal static CloudGitHubBackend OpenGitHub(AccountService accounts,DesktopInstance instance)
        {
            var read=Reader(accounts,instance);return CloudGitHubBackend.Create(read,CloudBackend.Capture(read()));
        }
        private static Func<AuthIdentity> Reader(AccountService accounts,DesktopInstance instance)
        {
            if(accounts==null||instance==null)throw new InvalidOperationException("Choisissez une instance Codex.");
            string root=accounts.Vault.Root,id=instance.Id,key=instance.AccountKey;
            string home=Path.GetFullPath(accounts.Instances.Home(instance));
            Func<AuthIdentity> read=delegate {
                var current=new AccountService(root);
                var target=current.Data.Instances.SingleOrDefault(i=>i.Id==id&&!i.Archived);
                if(target==null||String.IsNullOrEmpty(key)||target.AccountKey!=key||!RelayStore.SamePath(home,current.Instances.Home(target)))
                    throw new InvalidOperationException("L'association de cette instance a changé. Rechargez sa configuration Cloud.");
                NamedInstances.VerifyAccount(current,target);
                var identity=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json")));
                if(identity.Key!=key)throw new InvalidOperationException("Le compte connecté ne correspond pas au compte permanent de cette instance.");
                return identity;
            };
            return read;
        }
    }
}
