using System;
using System.IO;

namespace Creezio.Switcher
{
    public sealed class ToolAccessPolicyState
    {
        public bool Enabled {get;set;}
        public string Revision {get;set;}
        public string UpdatedUtc {get;set;}
        public ToolAccessPolicyState(){Revision="initial";}
    }

    // This opt-in policy supplements explicit grants; it never rewrites them.
    // Keeping it separate also makes disabling the mode restore the exact
    // individual sharing configuration, including grants created while enabled.
    internal static class ToolAccessPolicy
    {
        internal const string FileName="tool-access-policy.dpapi";
        public static ToolAccessPolicyState Read(RelayStore store)
        {
            string path=Path.Combine(store.Root,FileName);SafeFiles.RejectLinks(path);
            var state=RelayStore.ReadFile<ToolAccessPolicyState>(path,true,false);
            Guid revision;
            if(state==null||String.IsNullOrEmpty(state.Revision)||
                (state.Revision!="initial"&&(!Guid.TryParseExact(state.Revision,"N",out revision)))||
                (state.Enabled&&state.Revision=="initial"))
                throw new InvalidDataException("La configuration de l'accès complet des ressources est invalide. Aucun accès global accordé.");
            return state;
        }
        public static ToolAccessPolicyState Save(RelayStore store,bool enabled,string expectedRevision)
        {
            using(store.Lease("tool-access-policy")){
                var current=Read(store);
                if(String.IsNullOrEmpty(expectedRevision)||current.Revision!=expectedRevision)
                    throw new InvalidOperationException("Le mode d'accès complet a changé. Actualisez son état avant d'enregistrer.");
                if(current.Enabled==enabled)return current;
                var changed=new ToolAccessPolicyState{Enabled=enabled,Revision=Guid.NewGuid().ToString("N"),UpdatedUtc=DateTime.UtcNow.ToString("o")};
                store.WriteRecord(FileName,changed);return changed;
            }
        }
    }
}
