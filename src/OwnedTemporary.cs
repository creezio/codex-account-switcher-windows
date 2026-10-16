using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Creezio.Switcher
{
    // Only short-lived Switcher helpers use this directory. Persistent account
    // homes, model workspaces and native Windows sandbox TEMP are unchanged.
    internal sealed class OwnedTemporary : IDisposable
    {
        private readonly string root;
        private readonly RuntimeOwner owner;
        private FileStream lease;
        private bool disposed;
        internal readonly string DirectoryPath;
        internal StorageCleanupResult Result {get;private set;}
        internal static string DefaultRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher");}}
        internal OwnedTemporary(string appRoot)
        {
            root=Path.GetFullPath(appRoot);
            string parent=Path.Combine(root,"temporary");SafeFiles.PrivateDirectory(parent);
            DirectoryPath=Path.Combine(parent,Guid.NewGuid().ToString("N"));SafeFiles.PrivateDirectory(DirectoryPath);
            using(var self=Process.GetCurrentProcess())owner=new RuntimeOwner{Kind="switcher-owned-temp-v1",Directory=DirectoryPath,Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks};
            try{
                SafeFiles.AtomicWrite(Path.Combine(DirectoryPath,"switcher-temporary-owner.json"),Encoding.UTF8.GetBytes(Json.Write(owner)));
                lease=new FileStream(Path.Combine(DirectoryPath,".switcher-temp-active.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
            }catch{if(lease!=null)lease.Dispose();throw;}
        }
        internal void Apply(ProcessStartInfo start)
        {
            if(disposed)throw new ObjectDisposedException("OwnedTemporary");
            start.EnvironmentVariables["TEMP"]=DirectoryPath;
            start.EnvironmentVariables["TMP"]=DirectoryPath;
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            if(lease!=null)lease.Dispose();
            // Caller must stop its helper process tree before releasing this
            // directory. A failed cleanup remains visible in Storage; no ACL
            // changes, alternate interpreter or forced process termination.
            Result=StorageMaintenance.ClearOwnedTemporary(root,DirectoryPath,owner,CancellationToken.None);
        }
    }
}
