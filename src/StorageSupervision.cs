using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Creezio.Switcher
{
    public sealed class StorageSettings
    {
        public bool AutoCleanOwnedTemps {get;set;}
        public StorageSettings(){AutoCleanOwnedTemps=true;}
    }
    public sealed class StorageMaintenanceReport
    {
        public string CheckedUtc {get;set;}
        public long FreeBytes {get;set;}
        public string LastCandidate {get;set;}
        public StorageCleanupResult[] Results {get;set;}
    }
    internal static class StorageSupervision
    {
        internal static StorageMaintenanceReport Run(RelayStore store,string appRoot,CancellationToken token)
        {
            var previous=store.ReadRecord<StorageMaintenanceReport>("storage-maintenance.dpapi");
            var report=new StorageMaintenanceReport{CheckedUtc=DateTime.UtcNow.ToString("o"),FreeBytes=-1,Results=new StorageCleanupResult[0],LastCandidate=previous.LastCandidate};
            var results=new List<StorageCleanupResult>();
            try{
                report.FreeBytes=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(appRoot))).AvailableFreeSpace;
                if(store.ReadRecord<StorageSettings>("storage-settings.dpapi").AutoCleanOwnedTemps){
                    string parent=Path.Combine(appRoot,"temporary");SafeFiles.RejectLinks(parent);
                    if(Directory.Exists(parent)){
                        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                            // Cooperative deadline: a blocking Windows metadata
                            // operation may return later. No strict wall-clock claim.
                            deadline.CancelAfter(TimeSpan.FromSeconds(3));
                            try{
                                int examined=0,names=0;bool complete=true;var candidates=new List<string>();
                                foreach(string folder in Directory.EnumerateDirectories(parent)){
                                    deadline.Token.ThrowIfCancellationRequested();
                                    if(++names>=20000){complete=false;break;}
                                    if(InstanceRules.ValidId(Path.GetFileName(folder)))candidates.Add(folder);
                                }
                                if(!complete)results.Add(new StorageCleanupResult{Path=parent,Code="directory-limit",Message="Borne de 20 000 noms atteinte dans le dossier temporaire. Exhaustivité non confirmée : aucun nettoyage de ce passage, curseur conservé."});
                                else foreach(string folder in candidates.OrderBy(f=>String.CompareOrdinal(Path.GetFileName(f),previous.LastCandidate)>0?0:1).ThenBy(f=>Path.GetFileName(f),StringComparer.Ordinal)){
                                    deadline.Token.ThrowIfCancellationRequested();if(examined++>=10)break;
                                    // At most ten owned candidates, each bounded
                                    // to 2,000 metadata items including its root.
                                    // Never legacy profiles, projects or age rules.
                                    report.LastCandidate=Path.GetFileName(folder);
                                    SafeFiles.RejectLinks(folder);
                                    string marker=Path.Combine(folder,"switcher-temporary-owner.json");
                                    if(!File.Exists(marker))continue;
                                    results.AddRange(StorageMaintenance.CleanRuntimeBounded(appRoot,new[]{folder},deadline.Token,2000));
                                }
                            }catch(OperationCanceledException){token.ThrowIfCancellationRequested();results.Add(new StorageCleanupResult{Path=parent,Code="deadline",Message="Échéance coopérative atteinte ; aucune nouvelle suppression tentée. Les résultats précédents restent détaillés."});}
                        }
                    }
                }
            }catch(OperationCanceledException){throw;}
            catch(Exception error){results.Add(new StorageCleanupResult{Path=appRoot,Code="maintenance-error",Message=Program.SafeError(error)});}
            report.Results=results.ToArray();
            try{report.FreeBytes=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(appRoot))).AvailableFreeSpace;}catch(IOException){}
            store.WriteRecord("storage-maintenance.dpapi",report);
            return report;
        }
    }
}
