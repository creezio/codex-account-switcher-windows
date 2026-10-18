using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Creezio.Switcher
{
    // A file lease has no thread affinity and can safely span awaited GUI work.
    // The short write lease is deliberately separate: Save also protects callers
    // which have not adopted the whole read/mutate/save transaction yet.
    internal static class VaultMutation
    {
        internal static IDisposable Acquire(string root,CancellationToken token)
        {return Acquire(root,token,TimeSpan.FromSeconds(30));}
        internal static IDisposable Acquire(string root,CancellationToken token,TimeSpan timeout)
        {return AcquireCore(root,"accounts-mutation.lock",token,timeout);}
        internal static IDisposable AcquireWrite(string root)
        {return AcquireCore(root,"accounts-write.lock",CancellationToken.None,TimeSpan.FromSeconds(5));}
        private static IDisposable AcquireCore(string root,string name,CancellationToken token,TimeSpan timeout)
        {
            token.ThrowIfCancellationRequested();if(timeout<TimeSpan.Zero||timeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException("timeout");
            root=Path.GetFullPath(root);SafeFiles.RejectLinks(root);if(!Directory.Exists(root))SafeFiles.PrivateDirectory(root);string path=Path.Combine(root,name);var elapsed=Stopwatch.StartNew();
            while(true){
                token.ThrowIfCancellationRequested();SafeFiles.RejectLinks(path);
                try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException error){int code=error.HResult&65535;if(code!=32&&code!=33)throw;}
                if(elapsed.Elapsed>=timeout)throw new IOException("Une autre modification du coffre est en cours. Actualisez puis reessayez.");
                int wait=Math.Max(1,(int)Math.Min(25,(timeout-elapsed.Elapsed).TotalMilliseconds));if(token.WaitHandle.WaitOne(wait))token.ThrowIfCancellationRequested();
            }
        }
    }
}
