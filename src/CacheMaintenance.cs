using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Creezio.Switcher
{
    public sealed class RuntimeOwner
    {
        public string Kind {get;set;}
        public string Directory {get;set;}
        public int Pid {get;set;}
        public long Started {get;set;}
    }
    internal static class CacheMaintenance
    {
        public static void Mark(string folder,int pid,long started){SafeFiles.AtomicWrite(Path.Combine(folder,"switcher-runtime-owner.json"),Encoding.UTF8.GetBytes(Json.Write(new RuntimeOwner{Kind="codex-rpc-scratch-v1",Directory=Path.GetFullPath(folder),Pid=pid,Started=started})));}
        public static string[] Candidates(string appRoot)
        {
            string root=Path.Combine(Path.GetFullPath(appRoot),"runtime");if(!Directory.Exists(root))return new string[0];
            var snapshot=StorageMaintenance.Scan(new[]{new StorageScope{Id="runtime",Path=root,Kind="runtime"}},CancellationToken.None);
            if(snapshot.Partial){var reasons=snapshot.Entries.Where(e=>e.Errors>0).Select(e=>e.Reason).Distinct().Take(8).ToArray();throw new IOException("Analyse des temporaires incomplète ; aucun nettoyage proposé. "+(reasons.Length>0?String.Join(" · ",reasons):"Mesure indisponible ou borne de parcours atteinte."));}
            return snapshot.Entries.Where(e=>e.CanClean).Select(e=>e.Path).ToArray();
        }
        public static StorageCleanupResult[] CleanResults(string appRoot,IEnumerable<string> approved)
        {return StorageMaintenance.CleanRuntime(appRoot,approved,CancellationToken.None);}
        public static int Clean(string appRoot,IEnumerable<string> approved)
        {
            return CleanResults(appRoot,approved).Count(r=>r.Removed);
        }
        public static string DescribeResults(StorageCleanupResult[] results)
        {return results.Count(r=>r.Removed)+" dossier(s) supprimé(s).\n\n"+String.Join("\n\n",results.Select(r=>r.Path+"\n["+r.Code+"] "+r.Message));}
    }
}
