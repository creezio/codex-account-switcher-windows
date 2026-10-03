using System;
using System.IO;
using Creezio.Switcher;
internal static class CompatibilitySmoke
{
    public static int Main(string[] args){var service=new AccountService(Path.GetFullPath(args[0]));if(service.Data.Profiles.Count!=4)throw new Exception("Account count mismatch");var store=new RelayStore(Path.Combine(service.Vault.Root,"relay"));var policy=RelayPolicies.Load(store);if(policy.Projects.Count!=1||store.Messages().Count!=57)throw new Exception("Policy or jobs mismatch");service.Data.Profiles[0].Label="Framework → .NET 10";policy.Projects[0].Name="Framework compatible";service.Save();RelayPolicies.Save(store,policy);Console.WriteLine("PASS Framework reads and rewrites .NET 10 vault, policy and 57 jobs");return 0;}
}
