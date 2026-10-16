using System;
using System.IO;
using System.Threading;
using Creezio.Switcher;

// Real detached process, no Codex account, network call or live conversation.
internal static class WorkerSmoke
{
    private static void Until(Func<bool> condition,string failure){DateTime end=DateTime.UtcNow.AddSeconds(15);while(DateTime.UtcNow<end){if(condition())return;Thread.Sleep(150);}throw new Exception(failure);}
    public static int Main(string[] args)
    {
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var store=new RelayStore(Path.Combine(root,"relay"));
        try{
            foreach(string id in new[]{"source","target"})store.Register(new RelayChannel{Id=id,Name=id,Home=root,Workspace=root,AccountKey=id,Enabled=true,AnchorThreadId=id+"-chat"});
            var m=store.Enqueue("source","source-chat","target","Offline persistence","Never execute: disconnected fixture",null,false,null,null,x=>{x.SchemaVersion=3;});
            RelayWorker.Resume(store);Until(()=>RelayWorker.Running(store),"worker did not start");int first=RelayWorker.Status(store).Pid;
            Until(()=>store.Message(m.Id).Error!=null,"offline work was not observed");
            RelayWorker.Ensure(store);Thread.Sleep(500);if(RelayWorker.Status(store).Pid!=first)throw new Exception("duplicate worker");
            RelayWorker.Stop(store);Until(()=>!RelayWorker.Running(store),"worker did not stop");RelayWorker.Ensure(store);if(RelayWorker.Running(store))throw new Exception("manual stop ignored");
            if(new RelayStore(store.Root).Message(m.Id).State!="queued")throw new Exception("queued work lost");
            RelayWorker.Resume(store);Until(()=>RelayWorker.Running(store),"worker did not resume");if(RelayWorker.Status(store).Pid==first)throw new Exception("stale process identity");
            RelayWorker.Stop(store);Until(()=>!RelayWorker.Running(store),"resumed worker did not stop");store.RequestCancel(m.Id);
            new Vault(root).SaveSettings(new Settings{CodexHome=root,AutoRefresh=false,AutoResetCredits=false});
            UsageCoordinator.SavePolicies(store,new UsagePolicies{Background=true});UsageCoordinator.Ensure(store);Until(()=>UsageCoordinator.Running(store),"usage worker did not start");int usage=store.ReadRecord<RelayWorkerState>("usage-worker.dpapi").Pid;
            UsageCoordinator.Ensure(store);Thread.Sleep(500);if(store.ReadRecord<RelayWorkerState>("usage-worker.dpapi").Pid!=usage)throw new Exception("duplicate usage worker");
            UsageCoordinator.SavePolicies(store,new UsagePolicies{Background=false});Until(()=>!UsageCoordinator.Running(store),"usage worker did not observe disable");
            Console.WriteLine("PASS independent quota supervisor start, singleton and cooperative stop; no account loaded");
            Console.WriteLine("PASS detached worker persistence, singleton, manual pause, restart; no Codex process used");return 0;
        }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
        finally{RelayWorker.Stop(store);UsageCoordinator.SavePolicies(store,new UsagePolicies{Background=false});}
    }
}
