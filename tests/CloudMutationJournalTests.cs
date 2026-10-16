using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudMutationJournalTests
{
    private const string ConfigurationId="11111111-2222-3333-4444-555555555555~asenvcfg_journal";
    private const string Secret="SCRIPT_CONTENT_AND_SECRET_MUST_NOT_BE_JOURNALED";
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected operation to be rejected");}
    private static string NewId(){return Guid.NewGuid().ToString("N");}
    private static string DirectoryFor(string root,int number)
    {string path=Path.Combine(root,"cloud-journal-"+number);Directory.CreateDirectory(path);return path;}
    private static string RecordFile(string root,string id){return Path.Combine(root,"cloud-operations-v1",id+".json");}
    private static CloudMutationRecord Read(string root,string id){return Json.Read<CloudMutationRecord>(File.ReadAllText(RecordFile(root,id)));}
    private static CloudConfiguration Configuration(string name="Created")
    {return new CloudConfiguration{Id=ConfigurationId,Name=name,InstallScript=Secret,StartSkill=Secret,Status="ready",ShareSettings="private"};}
    private static CloudConfiguration Run(string root,string id,object request,Func<Task<CloudConfiguration>> mutate,Func<string,Task<CloudConfiguration>> reread,string account="account-a",string instance="instance-a",string action="create")
    {return CloudMutationJournal.Run(root,id,action,instance,account,request,mutate,reread).GetAwaiter().GetResult();}
    public static void RunAll(Action<string,Action> check,string root)
    {
        int number=0;
        check("cloud journal completes a mutation once and re-reads repeated requests",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0,reads=0;var request=new{name="Fixture",install_script=Secret};
            Func<Task<CloudConfiguration>> mutate=()=>{mutations++;return Task.FromResult(Configuration());};
            Func<string,Task<CloudConfiguration>> reread=configId=>{reads++;Assert(configId==ConfigurationId,"wrong configuration re-read");return Task.FromResult(Configuration("Fresh server state"));};
            Assert(Run(folder,id,request,mutate,reread).Name=="Created","initial result lost");
            Assert(Run(folder,id,request,mutate,reread).Name=="Fresh server state","completed receipt returned a stale configuration");
            Assert(mutations==1&&reads==1&&Read(folder,id).State=="completed","repeat replayed mutation or omitted current-auth read");
        });
        check("cloud journal refuses operation ID reuse with another payload, account, instance or action",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0,reads=0;var request=new{name="Fixture"};
            Func<Task<CloudConfiguration>> mutate=()=>{mutations++;return Task.FromResult(Configuration());};
            Func<string,Task<CloudConfiguration>> reread=configId=>{reads++;return Task.FromResult(Configuration());};
            Run(folder,id,request,mutate,reread);
            Reject(()=>Run(folder,id,new{name="Changed"},mutate,reread));
            Reject(()=>Run(folder,id,request,mutate,reread,account:"account-b"));
            Reject(()=>Run(folder,id,request,mutate,reread,instance:"instance-b"));
            Reject(()=>Run(folder,id,request,mutate,reread,action:"rename"));
            Assert(mutations==1&&reads==0,"conflicting receipt accessed backend");
        });
        check("cloud journal keeps only configuration identity and hashes, never scripts or configuration bodies",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();
            Run(folder,id,new{name="Request contains "+Secret,install_script=Secret,secret_token=Secret},()=>Task.FromResult(Configuration("Response contains "+Secret)),configId=>Task.FromResult(Configuration()));
            string json=File.ReadAllText(RecordFile(folder,id));var record=Read(folder,id);
            Assert(record.ConfigurationId==ConfigurationId&&record.State=="completed","configuration receipt identity missing");
            Assert(!json.Contains(Secret)&&!json.Contains("install_script")&&!json.Contains("InstallScript")&&!json.Contains("StartSkill")&&!json.Contains("Response contains"),"request or response content leaked into journal");
            Assert(!String.IsNullOrEmpty(record.Fingerprint)&&record.Fingerprint.Length==64,"request fingerprint missing");
        });
        check("cloud journal uncertain delivery survives restart and never resends",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0,reads=0;var request=new{name="Fixture"};
            Func<Task<CloudConfiguration>> mutate=()=>{mutations++;throw new CloudBackendUncertainException("transport interrompu");};
            Func<string,Task<CloudConfiguration>> reread=configId=>{reads++;return Task.FromResult(Configuration());};
            Assert(Reject(()=>Run(folder,id,request,mutate,reread)) is CloudBackendUncertainException,"initial uncertainty lost");
            Reject(()=>Run(folder,id,request,()=>{mutations++;return Task.FromResult(Configuration());},reread));
            Assert(mutations==1&&reads==0&&Read(folder,id).State=="uncertain","uncertain operation was replayed");
        });
        check("cloud journal marks local failures after dispatch uncertain without retaining exception text",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0;
            Reject(()=>Run(folder,id,new{name="Fixture"},()=>{mutations++;throw new IOException(Secret);},configId=>Task.FromResult(Configuration())));
            var record=Read(folder,id);string json=File.ReadAllText(RecordFile(folder,id));
            Assert(mutations==1&&record.State=="uncertain"&&record.ErrorCode=="local-outcome-unknown"&&!json.Contains(Secret),"local outcome misclassified or exception stored");
            Reject(()=>Run(folder,id,new{name="Fixture"},()=>{mutations++;return Task.FromResult(Configuration());},configId=>Task.FromResult(Configuration())));
            Assert(mutations==1,"local uncertain outcome resubmitted");
        });
        check("cloud journal keeps explicit backend refusal metadata and prevents blind retry",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0;
            Func<Task<CloudConfiguration>> mutate=()=>{mutations++;throw new CloudBackendException("forbidden",Secret,403);};
            Reject(()=>Run(folder,id,new{name="Fixture"},mutate,configId=>Task.FromResult(Configuration())));
            var record=Read(folder,id);Assert(record.State=="failed"&&record.ErrorCode=="forbidden"&&record.HttpStatus==403,"backend refusal metadata lost");
            Assert(!File.ReadAllText(RecordFile(folder,id)).Contains(Secret),"backend exception text stored");
            Reject(()=>Run(folder,id,new{name="Fixture"},mutate,configId=>Task.FromResult(Configuration())));Assert(mutations==1,"failed request blindly replayed");
        });
        check("cloud journal completed receipt rechecks current authentication and survives a rejected read",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0,reads=0;bool expired=false;var request=new{name="Fixture"};
            Func<Task<CloudConfiguration>> mutate=()=>{mutations++;return Task.FromResult(Configuration());};
            Func<string,Task<CloudConfiguration>> reread=configId=>{reads++;if(expired)throw new CloudBackendException("authentication","Reconnect this account",401);return Task.FromResult(Configuration("Reconnected"));};
            Run(folder,id,request,mutate,reread);expired=true;
            Assert(Reject(()=>Run(folder,id,request,mutate,reread)) is CloudBackendException,"current auth failure hidden");
            Assert(Read(folder,id).State=="completed","read failure corrupted prior completed mutation");
            expired=false;Assert(Run(folder,id,request,mutate,reread).Name=="Reconnected"&&mutations==1&&reads==2,"reauthentication replayed mutation or kept stale data");
        });
        check("cloud journal holds an exclusive OS lease until the mutation completes",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0;var request=new{name="Fixture"};
            var pending=new TaskCompletionSource<CloudConfiguration>();
            Func<string,Task<CloudConfiguration>> reread=configId=>Task.FromResult(Configuration());
            var first=CloudMutationJournal.Run(folder,id,"create","instance-a","account-a",request,()=>{mutations++;return pending.Task;},reread);
            try{
                var error=Reject(()=>Run(folder,id,request,()=>{mutations++;return Task.FromResult(Configuration());},reread));
                Assert(error is IOException&&mutations==1&&!first.IsCompleted,"concurrent writer entered open lease");
                Assert(Read(folder,id).State=="sending","in-flight state missing");
            }finally{pending.TrySetResult(Configuration());first.GetAwaiter().GetResult();}
            Run(folder,id,request,()=>{mutations++;return Task.FromResult(Configuration());},reread);
            Assert(mutations==1&&Read(folder,id).State=="completed","released lease permits duplicate mutation");
        });
        check("cloud journal rejects invalid path IDs and unsupported mutations before invoking callbacks",()=>{
            string folder=DirectoryFor(root,++number);int calls=0;
            Func<Task<CloudConfiguration>> mutate=()=>{calls++;return Task.FromResult(Configuration());};
            Func<string,Task<CloudConfiguration>> reread=configId=>{calls++;return Task.FromResult(Configuration());};
            foreach(var id in new[]{"../outside","id/elsewhere","",new string('a',31),new string('A',32)})Reject(()=>Run(folder,id,new{},mutate,reread));
            Reject(()=>Run(folder,NewId(),new{},mutate,reread,action:"delete"));
            Assert(calls==0,"invalid journal request reached backend callbacks");
        });
        check("cloud journal preserves corrupt receipts and refuses to repeat their mutation",()=>{
            string folder=DirectoryFor(root,++number),id=NewId();int mutations=0;
            string directory=Path.Combine(folder,"cloud-operations-v1");Directory.CreateDirectory(directory);string file=RecordFile(folder,id);
            byte[] original=Encoding.UTF8.GetBytes("{malformed-receipt");File.WriteAllBytes(file,original);
            Reject(()=>Run(folder,id,new{name="Fixture"},()=>{mutations++;return Task.FromResult(Configuration());},configId=>Task.FromResult(Configuration())));
            Assert(mutations==0&&Convert.ToBase64String(File.ReadAllBytes(file))==Convert.ToBase64String(original),"corrupt receipt was overwritten or replayed");
        });
    }
}
