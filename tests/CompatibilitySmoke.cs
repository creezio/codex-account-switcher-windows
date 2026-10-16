using System;
using System.IO;
using System.Collections.Generic;
using Creezio.Switcher;
internal static class CompatibilitySmoke
{
    internal static TunnelTool Contract()
    {return new TunnelTool{Server="codex_apps",Name="chatgpt_space.edit_page",ConnectorId="pages",Description="Éditer l’accès > <page> & \"texte\" + emoji 📝",Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"page_id\":{\"type\":\"string\",\"description\":\"Identifiant — obligatoire\"}}}"),Annotations=new Dictionary<string,object>{{"readOnlyHint",false}},ReadOnly=false};}
    internal static void CheckContract(RelayStore store)
    {
        var saved=store.ReadRecord<TunnelTool>("compat-contract.dpapi");var live=Contract();
        if(!ToolTunnel.SameContract(saved,live))throw new Exception("Cross-runtime identical contract refused");
        if(ToolTunnel.Signature(live)==saved.Signature)throw new Exception("Fixture must reproduce different runtime serialization hashes");
        live.Description+=" Changed behavior";if(ToolTunnel.SameContract(saved,live))throw new Exception("Changed contract accepted");
        var rewrite=Contract();rewrite.Signature=ToolTunnel.Signature(rewrite);store.WriteRecord("compat-contract.dpapi",rewrite);
    }
    public static int Main(string[] args){var service=new AccountService(Path.GetFullPath(args[0]));if(service.Data.Profiles.Count!=4)throw new Exception("Account count mismatch");var store=new RelayStore(Path.Combine(service.Vault.Root,"relay"));CheckContract(store);Console.WriteLine("PASS Framework accepts identical .NET 10 tool contract despite different JSON hash; rejects real drift");var policy=RelayPolicies.Load(store);if(policy.Projects.Count!=1||store.Messages().Count!=57)throw new Exception("Policy or jobs mismatch");service.Data.Profiles[0].Label="Framework → .NET 10";policy.Projects[0].Name="Framework compatible";service.Save();RelayPolicies.Save(store,policy);Console.WriteLine("PASS Framework reads and rewrites .NET 10 vault, policy and 57 jobs");return 0;}
}
