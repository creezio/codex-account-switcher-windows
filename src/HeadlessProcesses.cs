using System;
using System.IO;
using System.Linq;

namespace Creezio.Switcher
{
    public sealed class HeadlessProcessRecord
    {
        public string AccountKey {get;set;} public string Home {get;set;} public int Pid {get;set;} public long Started {get;set;}
    }
    internal sealed class HeadlessProcesses:IDisposable
    {
        private readonly RelayStore store;private readonly string file;
        internal HeadlessProcesses(RelayStore store,IHeadlessRpc rpc,string home,string account)
        {
            this.store=store;if(store==null||rpc.ProcessId<=0||rpc.ProcessStartedTicks<=0)return;
            file="headless-tools-"+rpc.ProcessId+"-"+rpc.ProcessStartedTicks+".dpapi";
            store.WriteRecord(file,new HeadlessProcessRecord{AccountKey=account,Home=home,Pid=rpc.ProcessId,Started=rpc.ProcessStartedTicks});
        }
        internal static ResourceRoot[] Roots(RelayStore store,string home,string account)
        {
            var roots=new System.Collections.Generic.List<ResourceRoot>();
            foreach(var path in Directory.GetFiles(store.Root,"headless-tools-*.dpapi",SearchOption.TopDirectoryOnly)){
                SafeFiles.RejectLinks(path);
                var record=store.ReadRecord<HeadlessProcessRecord>(Path.GetFileName(path));
                // Only remove our exact PID/start record after its process is proven gone.
                // Access denied is not evidence that an application's process has stopped.
                if(record.Pid<=0||record.Started<=0||Path.GetFileName(path)!="headless-tools-"+record.Pid+"-"+record.Started+".dpapi")continue;
                bool alive;try{alive=DesktopRuntime.SameProcess(record.Pid,record.Started);}catch(System.ComponentModel.Win32Exception){continue;}
                if(!alive){File.Delete(path);continue;}
                if(record.AccountKey==account&&RelayStore.SamePath(record.Home??"",home))roots.Add(new ResourceRoot{Pid=record.Pid,Started=record.Started});
            }
            var runtime=store.ReadRecord<HeadlessRuntimeState>(HeadlessRelayTransport.RuntimeName(account??""));
            try{if(runtime.Running&&runtime.AccountKey==account&&RelayStore.SamePath(runtime.Home??"",home)&&DesktopRuntime.SameProcess(runtime.WorkerPid,runtime.WorkerStarted)&&DesktopRuntime.SameProcess(runtime.ProcessId,runtime.ProcessStartedTicks))roots.Add(new ResourceRoot{Pid=runtime.ProcessId,Started=runtime.ProcessStartedTicks});}catch(System.ComponentModel.Win32Exception){}
            return roots.GroupBy(root=>root.Pid+":"+root.Started).Select(group=>group.First()).ToArray();
        }
        internal static ResourceRoot[] Controllers(RelayStore store)
        {
            return new[]{"worker.dpapi","cloud-worker-v1.dpapi",CloudQueueWorker.Record,"chat-watch-worker-v1.dpapi"}.Select(name=>store.ReadRecord<RelayWorkerState>(name)).Where(state=>{
                try{return DesktopRuntime.SameProcess(state.Pid,state.Started);}catch(System.ComponentModel.Win32Exception){return false;}
            }).Select(state=>new ResourceRoot{Pid=state.Pid,Started=state.Started}).GroupBy(root=>root.Pid+":"+root.Started).Select(group=>group.First()).ToArray();
        }
        public void Dispose(){if(file==null)return;var path=Path.Combine(store.Root,file);SafeFiles.RejectLinks(path);if(File.Exists(path))File.Delete(path);}
    }
}
