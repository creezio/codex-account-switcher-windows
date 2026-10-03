using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
            string root=Path.Combine(Path.GetFullPath(appRoot),"runtime");if(!Directory.Exists(root))return new string[0];SafeFiles.RejectLinks(root);var result=new List<string>();
            foreach(string folder in Directory.GetDirectories(root)){
                try{SafeFiles.RejectLinks(folder);if(!InstanceRules.ValidId(Path.GetFileName(folder)))continue;string marker=Path.Combine(folder,"switcher-runtime-owner.json");if(!File.Exists(marker))continue;var owner=Json.Read<RuntimeOwner>(SafeFiles.ReadText(marker));if(owner.Kind!="codex-rpc-scratch-v1"||!RelayStore.SamePath(owner.Directory,folder)||owner.Pid<=0||owner.Started<=0||DesktopRuntime.SameProcess(owner.Pid,owner.Started))continue;result.Add(folder);}catch(IOException){}
            }
            return result.ToArray();
        }
        public static int Clean(string appRoot,IEnumerable<string> approved)
        {
            int count=0;foreach(string folder in approved){if(!Candidates(appRoot).Contains(folder,StringComparer.OrdinalIgnoreCase))continue;SafeFiles.DeleteOwnedTree(Path.Combine(appRoot,"runtime"),folder);count++;}return count;
        }
    }
}
