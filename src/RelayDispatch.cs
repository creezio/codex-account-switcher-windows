using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class DispatchState
    {
        public string Mode {get;set;}
        public List<string> Accepted {get;set;}
        public DispatchState(){Mode="running";Accepted=new List<string>();}
    }
    internal static class RelayDispatch
    {
        public static DispatchState Read(RelayStore store){return store.ReadRecord<DispatchState>("dispatch-state.dpapi");}
        public static bool AllowsStart(RelayStore store,RelayMessage m){var s=Read(store);return s.Mode=="running"||(s.Mode=="drain"&&(s.Accepted.Contains(m.Id)||s.Accepted.Contains(m.RootJobId??m.Id)));}
        public static void Set(RelayStore store,string mode)
        {
            if(!new[]{"running","paused","drain"}.Contains(mode))throw new InvalidOperationException("Mode de moteur invalide.");
            using(store.Lease("dispatch"))store.WriteRecord("dispatch-state.dpapi",new DispatchState{Mode=mode,Accepted=mode=="drain"?store.Messages().Where(m=>m.SchemaVersion>=3&&RelayRouter.Active(m)).Select(m=>m.RootJobId??m.Id).Distinct().ToList():new List<string>()});
        }
        public static string Caption(RelayStore store){var s=Read(store);return s.Mode=="paused"?"Départs suspendus · retours suivis":s.Mode=="drain"?"Termine les travaux acceptés puis s'arrête":"Départs autorisés";}
    }
    internal static class RelayReturns
    {
        internal static string Key(string value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant().Substring(0,24);}
        public static async Task Deliver(RelayStore store,IRelayTransport transport,RelayMessage current,RelayChannel source,CancellationToken token)
        {
            DateTime completed;if(DateTime.TryParse(current.CompletedUtc,out completed)&&DateTime.UtcNow-completed.ToUniversalTime()<TimeSpan.FromSeconds(current.ReturnDelaySeconds))return;
            if(source.AccountKey!=current.SourceAccountKey||!RelayStore.SamePath(source.Home,current.SourceHome)||!RelayStore.SamePath(source.Workspace,current.Workspace))throw new InvalidOperationException("La source a changé depuis l'admission de la demande.");
            FileStream batchLock;try{batchLock=store.Lease("return-"+Key(current.SourceHome+current.SourceThreadId));}catch(IOException){return;}
            using(batchLock){
                var leases=new List<IDisposable>();var batch=new List<RelayMessage>{current};
                try{
                    foreach(var summary in store.ActiveMessages().Where(m=>m.SchemaVersion>=3&&m.Id!=current.Id&&SameSource(m,current)&&m.ReturnMode=="batch"&&RelayEngine.PendingReturn(m)).Take(9)){
                        try{var lease=store.Lease("message-"+summary.Id);leases.Add(lease);var m=store.Message(summary.Id);if(RelayEngine.PendingReturn(m)&&SameSource(m,current))batch.Add(m);}catch(IOException){}
                    }
                    transport.VerifySend(source,current.SourceThreadId);
                    foreach(var m in batch){m.ReturnBatchId=current.Id;m.ReturnState="sending";store.Save(m);}
                    try{
                        var body=new StringBuilder(RelayEngine.ReturnMarker(current.Id)+"\nRésultats regroupés. Ce sont des résultats à examiner, pas des mandats à retransmettre.\n");
                        foreach(var m in batch){string result=m.Result??"";body.Append("\nTâche ").Append(m.Id).Append(" · ").Append(m.Title).Append(" · ").Append(m.Outcome).Append("\n").Append(result.Length>1800?result.Substring(0,1800)+"\n[Suite via read_result]":result).Append("\n");}
                        await transport.Call(source,"send_message_to_thread",new{threadId=current.SourceThreadId,prompt=body.ToString()},token);
                        foreach(var m in batch){m.ReturnState="delivered";m.Error=null;store.Save(m);}
                    }catch{foreach(var m in batch){m.ReturnState="uncertain";m.Error="Remise groupée incertaine ; vérifier sa réception avant toute reprise.";store.Save(m);}throw;}
                }finally{foreach(var lease in leases)lease.Dispose();}
            }
        }
        internal static bool SameSource(RelayMessage a,RelayMessage b){return a.SourceThreadId==b.SourceThreadId&&a.SourceChannelId==b.SourceChannelId&&a.SourceAccountKey==b.SourceAccountKey&&RelayStore.SamePath(a.SourceHome,b.SourceHome)&&RelayStore.SamePath(a.Workspace,b.Workspace);}
    }
}
