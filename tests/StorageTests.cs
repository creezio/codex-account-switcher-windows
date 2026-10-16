using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Creezio.Switcher;
using Microsoft.Win32.SafeHandles;

internal static class StorageTests
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateHardLink(string path,string existing,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputSize,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Throws(Action action){try{action();}catch(OperationCanceledException){return;}throw new Exception("Cancellation was not observed");}
    private sealed class Fixture
    {
        internal readonly string Root,Folder,Marker,File;
        internal RuntimeOwner Owner;
        internal Fixture(string root,int number,bool temporary=false)
        {
            Root=Path.Combine(root,"storage-"+number);Folder=Path.Combine(Root,temporary?"temporary":"runtime",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Folder);
            Marker=Path.Combine(Folder,temporary?"switcher-temporary-owner.json":"switcher-runtime-owner.json");File=Path.Combine(Folder,"payload.tmp");System.IO.File.WriteAllBytes(File,new byte[17001]);
            Owner=new RuntimeOwner{Kind=temporary?"switcher-owned-temp-v1":"codex-rpc-scratch-v1",Directory=Folder,Pid=Int32.MaxValue,Started=1};WriteMarker();
        }
        internal void WriteMarker(){System.IO.File.WriteAllText(Marker,Json.Write(Owner),new UTF8Encoding(false));}
        internal StorageCleanupResult Clean(Func<int,long,StorageOwnerStatus> owner=null,Func<string,int,bool> referenced=null,Action<string> before=null,CancellationToken token=default(CancellationToken))
        {return StorageMaintenance.CleanCore(Root,new[]{Folder},token,owner??((pid,started)=>StorageOwnerStatus.Dead),referenced??((path,pid)=>false),null,before).Single();}
    }
    private static void Junction(string path,string target)
    {
        Directory.CreateDirectory(path);string substitute=@"\??\"+Path.GetFullPath(target),print=Path.GetFullPath(target);
        byte[] sub=Encoding.Unicode.GetBytes(substitute),display=Encoding.Unicode.GetBytes(print);int dataLength=8+sub.Length+2+display.Length+2;var data=new byte[8+dataLength];
        Array.Copy(BitConverter.GetBytes(0xA0000003u),0,data,0,4);Array.Copy(BitConverter.GetBytes((ushort)dataLength),0,data,4,2);Array.Copy(BitConverter.GetBytes((ushort)sub.Length),0,data,10,2);Array.Copy(BitConverter.GetBytes((ushort)(sub.Length+2)),0,data,12,2);Array.Copy(BitConverter.GetBytes((ushort)display.Length),0,data,14,2);Array.Copy(sub,0,data,16,sub.Length);Array.Copy(display,0,data,18+sub.Length,display.Length);
        using(var handle=CreateFile(path,0x40000000,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero)){int returned;if(handle.IsInvalid||!DeviceIoControl(handle,0x000900A4,data,data.Length,IntPtr.Zero,0,out returned,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        int number=0;
        check("storage scan reads metadata, classifies histories and never offers protected data for cleanup",()=>{
            var f=new Fixture(root,++number);string history=Path.Combine(f.Root,"sessions"),cache=Path.Combine(f.Root,"cache");Directory.CreateDirectory(history);Directory.CreateDirectory(cache);File.WriteAllBytes(Path.Combine(history,"rollout.jsonl"),new byte[13]);File.WriteAllBytes(Path.Combine(cache,"data.bin"),new byte[7]);
            var snapshot=StorageMaintenance.Scan(new[]{new StorageScope{Id="protected",Path=f.Root,Kind="data",Protected=true}},CancellationToken.None);
            Assert(snapshot.Entries.Single(e=>e.Path==history).Kind=="history"&&snapshot.Entries.Single(e=>e.Path==cache).Kind=="cache"&&snapshot.Entries.All(e=>!e.CanClean),"protected profile or history became disposable");Assert(snapshot.FreeBytes>0&&snapshot.TotalBytes>=snapshot.FreeBytes&&!snapshot.Partial,"metadata snapshot or disk counters invalid");
        });
        check("storage scan deduplicates actual NTFS hardlinks but never distinct equal-content files",()=>{
            string folder=Path.Combine(root,"storage-"+(++number));Directory.CreateDirectory(folder);string first=Path.Combine(folder,"a.bin");File.WriteAllBytes(first,new byte[19]);File.WriteAllBytes(Path.Combine(folder,"c.bin"),new byte[19]);if(!CreateHardLink(Path.Combine(folder,"b.bin"),first,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
            var snapshot=StorageMaintenance.Scan(new[]{new StorageScope{Id="files",Path=folder}},CancellationToken.None);Assert(snapshot.Entries.Sum(e=>e.Bytes)==38&&snapshot.Entries.Sum(e=>e.Files)==3&&!snapshot.Partial,"links were added twice or equal contents treated as one file");Assert(snapshot.Entries.Any(e=>(e.Reason??"").Contains("Liens physiques")),"shared allocation was hidden");
        });
        check("storage scan deduplicates overlapping scopes and supports explicitly scoped individual files",()=>{
            string folder=Path.Combine(root,"storage-"+(++number));Directory.CreateDirectory(folder);string file=Path.Combine(folder,"only.bin");File.WriteAllBytes(file,new byte[23]);var snapshot=StorageMaintenance.Scan(new[]{new StorageScope{Id="tree",Path=folder},new StorageScope{Id="file",Path=file,Protected=true}},CancellationToken.None);
            Assert(snapshot.Entries.Sum(e=>e.Bytes)==23&&snapshot.Entries.Count(e=>e.ScopeId=="file")==1&&snapshot.Entries.All(e=>!e.CanClean),"overlapping scan doubled storage or lost file scope");
        });
        check("storage scan exposes unavailable paths, enforces its traversal bound and honors cancellation",()=>{
            var f=new Fixture(root,++number);var missing=StorageMaintenance.Scan(new[]{new StorageScope{Id="missing",Path=Path.Combine(f.Root,"absent")}},CancellationToken.None);Assert(missing.Partial&&missing.Entries.Any(e=>e.Errors>0),"missing path appeared complete");
            var limited=StorageMaintenance.ScanCore(new[]{new StorageScope{Id="limited",Path=f.Root}},CancellationToken.None,1);Assert(limited.Partial&&limited.Entries.Any(e=>(e.Reason??"").Contains("bornée")),"scan ignored its safety bound");using(var cancel=new CancellationTokenSource()){cancel.Cancel();Throws(()=>StorageMaintenance.Scan(new[]{new StorageScope{Path=f.Root}},cancel.Token));}Assert(File.Exists(f.File),"read-only scan changed storage");
            var bounded=StorageMaintenance.CleanRuntimeBounded(f.Root,new[]{f.Folder},CancellationToken.None,2).Single();Assert(bounded.Code=="limit"&&!bounded.Removed&&File.Exists(f.File)&&File.Exists(f.Marker),"bounded cleanup exceeded metadata item allowance or deleted a partial plan");
        });
        check("storage cleanup is offered only for a verified owned scratch under its exact whitelist root",()=>{
            var f=new Fixture(root,++number);var snapshot=StorageMaintenance.Scan(new[]{new StorageScope{Id="runtime",Path=Path.GetDirectoryName(f.Folder),Kind="runtime"}},CancellationToken.None);Assert(snapshot.Entries.Single().CanClean,"dead exact owned scratch not recognized");
            var arbitrary=StorageMaintenance.Scan(new[]{new StorageScope{Id="other",Path=f.Root,Kind="cache"}},CancellationToken.None);Assert(arbitrary.Entries.All(e=>!e.CanClean),"folder name alone authorized cleanup");
        });
        check("storage cleanup rejects root, outside paths and a whole mixed unsafe selection before deleting anything",()=>{
            var f=new Fixture(root,++number);string outside=Path.Combine(f.Root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outside);
            var results=StorageMaintenance.CleanCore(f.Root,new[]{f.Folder,outside,f.Root},CancellationToken.None,(pid,start)=>StorageOwnerStatus.Dead,(path,pid)=>false,null);
            Assert(results.Count(r=>r.Code=="outside-root")==2&&results[0].Code=="batch-refused"&&File.Exists(f.File)&&File.Exists(f.Marker),"unsafe batch partly deleted a valid sibling");
        });
        check("storage cleanup refuses absent, wrong-kind and wrong-directory ownership markers",()=>{
            foreach(string change in new[]{"missing","kind","directory"}){var f=new Fixture(root,++number);if(change=="missing")File.Delete(f.Marker);else{if(change=="kind")f.Owner.Kind="another-application";else f.Owner.Directory=f.Root;f.WriteMarker();}var result=f.Clean();Assert(!result.Removed&&File.Exists(f.File),"invalid marker authorized deletion");}
        });
        check("storage cleanup keeps exact live, reused and inaccessible process identities",()=>{
            foreach(var state in new[]{StorageOwnerStatus.Alive,StorageOwnerStatus.Reused,StorageOwnerStatus.Unknown}){var f=new Fixture(root,++number);var result=f.Clean((pid,start)=>state);Assert(!result.Removed&&result.Code=="owner-"+(state==StorageOwnerStatus.Alive?"running":state==StorageOwnerStatus.Reused?"reused":"unknown")&&File.Exists(f.File),"unconfirmed owner was considered dead");}
        });
        check("storage cleanup revalidates owner and live path references before its first deletion",()=>{
            var f=new Fixture(root,++number);int reads=0;var changed=f.Clean((pid,start)=>++reads==1?StorageOwnerStatus.Dead:StorageOwnerStatus.Alive);Assert(changed.Code=="owner-changed"&&File.Exists(f.File),"owner change after preflight was ignored");var inUse=f.Clean(null,(path,pid)=>true);Assert(inUse.Code=="in-use"&&File.Exists(f.Marker),"referenced scratch was deleted");
            string second=Path.Combine(Path.GetDirectoryName(f.Folder),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(second);File.WriteAllText(Path.Combine(second,"payload.tmp"),"fixture");File.WriteAllText(Path.Combine(second,"switcher-runtime-owner.json"),Json.Write(new RuntimeOwner{Kind=f.Owner.Kind,Directory=second,Pid=Int32.MaxValue-1,Started=1}));int secondReads=0;
            var batch=StorageMaintenance.CleanCore(f.Root,new[]{f.Folder,second},CancellationToken.None,(pid,start)=>pid==Int32.MaxValue-1&&++secondReads>=3?StorageOwnerStatus.Alive:StorageOwnerStatus.Dead,(path,pid)=>false,null);Assert(batch[0].Removed&&!batch[1].Removed&&batch[1].Code=="owner-changed"&&File.Exists(Path.Combine(second,"payload.tmp")),"later plan skipped its immediate owner recheck");
        });
        check("storage cleanup never deletes protected histories, secrets, databases, dependencies or source under a forged marker",()=>{
            foreach(string name in new[]{"auth.json","auth.json.tmp","state.sqlite-wal","conversation.jsonl","project.cs","package.json",".env","notes.md","payload.cs.tmp"}){var f=new Fixture(root,++number);string protectedFile=Path.Combine(f.Folder,name);File.WriteAllText(protectedFile,"keep fixture");var result=f.Clean();Assert(!result.Removed&&File.Exists(protectedFile)&&File.Exists(f.File)&&File.Exists(f.Marker),"protected or unclassified file was removed: "+name);}
            foreach(string name in new[]{".git","src","sessions","node_modules"}){var f=new Fixture(root,++number);Directory.CreateDirectory(Path.Combine(f.Folder,name));Assert(f.Clean().Code=="protected-data"&&File.Exists(f.File),"protected directory accepted");}
        });
        check("storage scan and cleanup reject junctions before descent without touching their target",()=>{
            var f=new Fixture(root,++number);long expectedBytes=new FileInfo(f.File).Length+new FileInfo(f.Marker).Length;string target=Path.Combine(f.Root,"foreign-data");Directory.CreateDirectory(target);string keep=Path.Combine(target,"keep.bin");File.WriteAllBytes(keep,new byte[701]);string link=Path.Combine(f.Folder,"linked");Junction(link,target);
            try{var snapshot=StorageMaintenance.Scan(new[]{new StorageScope{Id="root",Path=Path.GetDirectoryName(f.Folder),Kind="runtime"}},CancellationToken.None);Assert(snapshot.Partial&&snapshot.Entries.Single().Errors>0&&!snapshot.Entries.Single().CanClean&&snapshot.Entries.Single().Bytes==expectedBytes&&snapshot.Entries.Single().Files==2,"junction target counted or offered for deletion");var result=f.Clean();Assert(result.Code=="reparse-point"&&File.Exists(keep)&&File.Exists(f.File)&&File.Exists(f.Marker),"junction target or safe sibling deleted");}
            finally{Directory.Delete(link);}
        });
        check("storage cleanup reports an actual Windows sharing lock without retries, ACL changes or partial deletion",()=>{
            var f=new Fixture(root,++number);using(var held=new FileStream(f.File,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){var result=f.Clean();Assert(!result.Removed&&(result.Code=="win32-32"||result.Code=="win32-33")&&File.Exists(f.Marker),"sharing lock was hidden or bypassed");}Assert(File.Exists(f.File),"locked file changed");
        });
        check("storage cleanup removes only its verified dead disposable scratch and records a conservative allocation receipt",()=>{
            var f=new Fixture(root,++number);string nested=Path.Combine(f.Folder,"child");Directory.CreateDirectory(nested);File.WriteAllBytes(Path.Combine(nested,"work.log"),new byte[37]);var result=f.Clean();Assert(result.Removed&&result.Code=="removed"&&!Directory.Exists(f.Folder)&&result.ReclaimedBytes>0,"owned disposable scratch was not removed or had no receipt");
        });
        check("storage cleanup preserves outside hardlinks and never claims their data as reclaimed",()=>{
            var f=new Fixture(root,++number);string outside=Path.Combine(f.Root,"outside.bin");if(!CreateHardLink(outside,f.File,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());var result=f.Clean();Assert(result.Removed&&File.Exists(outside)&&new FileInfo(outside).Length==17001&&result.ReclaimedBytes<17001,"outside link deleted or its shared data counted as freed");
        });
        check("storage cleanup has no fallback after a policy rejection and retains its marker after partial Windows failure",()=>{
            var f=new Fixture(root,++number);int attempts=0;var policy=f.Clean(null,null,path=>{attempts++;throw new IOException("blocked by policy");});Assert(policy.Code=="policy-blocked"&&attempts==1&&File.Exists(f.Marker)&&File.Exists(f.File),"policy rejection was retried or bypassed");
            string sibling=Path.Combine(Path.GetDirectoryName(f.Folder),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(sibling);File.WriteAllText(Path.Combine(sibling,"payload.tmp"),"fixture");File.WriteAllText(Path.Combine(sibling,"switcher-runtime-owner.json"),Json.Write(new RuntimeOwner{Kind=f.Owner.Kind,Directory=sibling,Pid=Int32.MaxValue,Started=1}));attempts=0;
            var refused=StorageMaintenance.CleanCore(f.Root,new[]{f.Folder,sibling},CancellationToken.None,(pid,start)=>StorageOwnerStatus.Dead,(path,pid)=>false,null,path=>{attempts++;throw new IOException("blocked by policy");});Assert(attempts==1&&refused[0].Code=="policy-blocked"&&refused[1].Code=="batch-refused"&&File.Exists(Path.Combine(sibling,"payload.tmp")),"policy rejection continued with another candidate");
            string second=Path.Combine(f.Folder,"second.tmp");File.WriteAllBytes(second,new byte[5]);attempts=0;var partial=f.Clean(null,null,path=>{if(++attempts==2)throw new Win32Exception(32);});Assert(!partial.Removed&&partial.Code=="partial-win32-32"&&File.Exists(f.Marker)&&Directory.Exists(f.Folder),"partial failure was presented as success or removed proof");
        });
        check("storage cleanup cancellation returns honest receipts and never starts another deletion afterwards",()=>{
            var f=new Fixture(root,++number);using(var cancel=new CancellationTokenSource()){cancel.Cancel();var result=f.Clean(null,null,null,cancel.Token);Assert(result.Code=="cancelled"&&!result.Removed&&File.Exists(f.File),"cancelled batch changed files");}
            File.WriteAllBytes(Path.Combine(f.Folder,"second.tmp"),new byte[5]);using(var cancel=new CancellationTokenSource()){int attempts=0;var result=f.Clean(null,null,path=>{attempts++;cancel.Cancel();},cancel.Token);Assert(result.Code=="partial-cancelled"&&!result.Removed&&attempts==1&&File.Exists(f.Marker),"cancellation invented completion or continued deleting");}
        });
        check("storage own temporary release requires exact current controller identity and never permits a legacy profile",()=>{
            var f=new Fixture(root,++number,true);using(var process=Process.GetCurrentProcess()){f.Owner.Pid=process.Id;f.Owner.Started=process.StartTime.ToUniversalTime().Ticks;f.WriteMarker();Assert(StorageMaintenance.CleanRuntime(f.Root,new[]{f.Folder},CancellationToken.None).Single().Code=="owner-running","live controller treated as orphan");var wrong=new RuntimeOwner{Kind=f.Owner.Kind,Directory=f.Owner.Directory,Pid=f.Owner.Pid,Started=f.Owner.Started-1};Assert(StorageMaintenance.ClearOwnedTemporary(f.Root,f.Folder,wrong,CancellationToken.None).Code=="invalid-owner"&&File.Exists(f.File),"different controller released temporary");var released=StorageMaintenance.ClearOwnedTemporary(f.Root,f.Folder,f.Owner,CancellationToken.None);Assert(released.Removed&&!Directory.Exists(f.Folder),"exact own temporary was not released");}
            var legacy=new Fixture(root,++number);using(var process=Process.GetCurrentProcess()){legacy.Owner.Pid=process.Id;legacy.Owner.Started=process.StartTime.ToUniversalTime().Ticks;legacy.WriteMarker();Assert(StorageMaintenance.ClearOwnedTemporary(legacy.Root,legacy.Folder,legacy.Owner,CancellationToken.None).Code=="invalid-owner"&&File.Exists(legacy.File),"own release bypassed legacy scratch protection");}
        });
    }
}
