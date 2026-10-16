using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Creezio.Switcher;

internal static class AtomicReplaceTests
{
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected atomic replacement failure");}
    private sealed class Fixture
    {
        internal readonly string Source,Destination;
        internal Fixture(string root,int number)
        {string folder=Path.Combine(root,"atomic-replace-"+number);Directory.CreateDirectory(folder);Source=Path.Combine(folder,"owned-temp");Destination=Path.Combine(folder,"destination");File.WriteAllText(Source,"replacement");File.WriteAllText(Destination,"original");}
    }
    public static void RunAll(Action<string,Action> check,string root)
    {
        int number=0;
        check("atomic replacement retries Windows 1175 only while source and destination stay identical",()=>{
            var f=new Fixture(root,++number);int attempts=0;var waits=new List<int>();
            AtomicFiles.ReplaceVerified(f.Source,f.Destination,(source,destination)=>{attempts++;if(attempts<3)throw new IOException("simulated Windows replacement contention",unchecked((int)0x80070497));File.Replace(source,destination,null);},waits.Add);
            Assert(attempts==3&&waits.SequenceEqual(new[]{20,50})&&File.ReadAllText(f.Destination)=="replacement"&&!File.Exists(f.Source),"bounded retry did not preserve atomic replacement");
        });
        check("atomic replacement stops after its bounded two retries without deleting either file",()=>{
            var f=new Fixture(root,++number);int attempts=0,waits=0;
            var error=Reject(()=>AtomicFiles.ReplaceVerified(f.Source,f.Destination,(source,destination)=>{attempts++;throw new IOException("simulated 1175",unchecked((int)0x80070497));},ms=>waits++));
            Assert(error is IOException&&error.HResult==unchecked((int)0x80070497)&&attempts==3&&waits==2&&File.ReadAllText(f.Destination)=="original"&&File.ReadAllText(f.Source)=="replacement","retry exceeded bound or removed source/destination");
        });
        check("atomic replacement never retries permission or ambiguous replacement errors",()=>{
            foreach(int code in new[]{5,1176,1177}){
                var f=new Fixture(root,++number);int attempts=0,waits=0;
                Reject(()=>AtomicFiles.ReplaceVerified(f.Source,f.Destination,(source,destination)=>{attempts++;throw new IOException("nonretryable failure",unchecked((int)(0x80070000u+(uint)code)));},ms=>waits++));
                Assert(attempts==1&&waits==0&&File.ReadAllText(f.Destination)=="original"&&File.Exists(f.Source),"nonretryable native failure was retried");
            }
        });
        check("atomic replacement refuses a destination changed during transient contention",()=>{
            var f=new Fixture(root,++number);int attempts=0;
            Reject(()=>AtomicFiles.ReplaceVerified(f.Source,f.Destination,(source,destination)=>{attempts++;throw new IOException("simulated sharing",unchecked((int)0x80070020));},ms=>File.WriteAllText(f.Destination,"foreign writer")));
            Assert(attempts==1&&File.ReadAllText(f.Destination)=="foreign writer"&&File.ReadAllText(f.Source)=="replacement","retry overwrote another writer");
        });
        check("atomic replacement refuses a temporary changed during transient contention",()=>{
            var f=new Fixture(root,++number);int attempts=0;
            Reject(()=>AtomicFiles.ReplaceVerified(f.Source,f.Destination,(source,destination)=>{attempts++;throw new IOException("simulated sharing",unchecked((int)0x80070020));},ms=>File.WriteAllText(f.Source,"foreign replacement")));
            Assert(attempts==1&&File.ReadAllText(f.Destination)=="original"&&File.ReadAllText(f.Source)=="foreign replacement","retry promoted a changed temporary");
        });
        check("atomic replacement never recreates a destination removed after failure",()=>{
            var f=new Fixture(root,++number);int attempts=0;
            Reject(()=>AtomicFiles.ReplaceVerified(f.Source,f.Destination,(source,destination)=>{attempts++;throw new IOException("simulated 1175",unchecked((int)0x80070497));},ms=>File.Delete(f.Destination)));
            Assert(attempts==1&&!File.Exists(f.Destination)&&File.ReadAllText(f.Source)=="replacement","retry silently moved a temporary into a removed destination");
        });
        check("atomic replacement handles an actual Windows sharing lock without a delete-move fallback",()=>{
            var f=new Fixture(root,++number);int attempts=0,waits=0;var held=new FileStream(f.Destination,FileMode.Open,FileAccess.Read,FileShare.Read);
            try{
                AtomicFiles.ReplaceVerified(f.Source,f.Destination,(source,destination)=>{attempts++;File.Replace(source,destination,null);},ms=>{waits++;held.Dispose();});
                Assert(attempts==2&&waits==1&&File.ReadAllText(f.Destination)=="replacement"&&!File.Exists(f.Source),"real sharing violation not recovered atomically");
            }finally{held.Dispose();}
        });
    }
}
