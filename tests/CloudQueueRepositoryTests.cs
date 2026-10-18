using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudQueueRepositoryTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Refused(Action action){try{action();}catch(CloudBackendException error){Assert(error.Code=="queue-repository-mismatch","wrong failure");return;}throw new Exception("Wrong repository was accepted");}
    private static CloudJob Job(){return new CloudJob{Id="a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0",InstanceId="b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0",AccountKey="fictional-account",ConfigurationId="fictional-configuration",ConfigurationVersion="version-a",Repository="example/project",RepositoryIds=new[]{"github-123"},Fingerprint="fictional-fingerprint"};}
    private static RelayStore Fixture(string root,string name,CloudJob job)
    {
        var store=new RelayStore(Path.Combine(root,"cloud-queue-repository",name,"relay"));
        store.WriteRecord("cloud-prepared-mission-"+job.Id+".dpapi",new CloudPreparedMission{Id=job.Id,Repository=job.Repository,Request=new CloudJobSpec{Id=job.Id},Target=new CloudQueueTarget{InstanceId=job.InstanceId,ExpectedAccountKey=job.AccountKey,ConfigurationId=job.ConfigurationId}});
        return store;
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("Prepared Cloud repository guard leaves existing unmanaged missions untouched",()=>{
            var store=new RelayStore(Path.Combine(root,"cloud-queue-repository","unmanaged","relay"));int reads=0;
            CloudQueueRepositoryBinding.Require(store,Job(),CancellationToken.None,(id,ct)=>{reads++;throw new Exception("unexpected network");}).GetAwaiter().GetResult();
            Assert(reads==0,"legacy job acquired new repository requirement");
        });
        check("Prepared Cloud repository guard rejects correct-looking label over wrong native repository",()=>{
            var job=Job();var store=Fixture(root,"wrong",job);
            Refused(()=>CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,(id,ct)=>Task.FromResult(new CloudGitHubRepository{Id=id,FullName="example/other-project"})).GetAwaiter().GetResult());
            Assert(!File.Exists(Path.Combine(store.Root,"cloud-queue-repository-"+job.Id+".dpapi")),"wrong repository left a verification receipt");
        });
        check("Prepared Cloud repository verified receipt is reused for the exact immutable job",()=>{
            var job=Job();var store=Fixture(root,"verified",job);int reads=0;
            Func<string,CancellationToken,Task<CloudGitHubRepository>> resolve=(id,ct)=>{reads++;return Task.FromResult(new CloudGitHubRepository{Id=id,FullName="EXAMPLE/Project"});};
            CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,resolve).GetAwaiter().GetResult();
            CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,resolve).GetAwaiter().GetResult();
            Assert(reads==1,"same request repeatedly fetched the native catalogue");
            job.ConfigurationVersion="version-b";
            CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,resolve).GetAwaiter().GetResult();Assert(reads==2,"receipt survived a different environment version");
        });
        check("Prepared Cloud repository refuses returned ID mismatch and absent native repository IDs",()=>{
            var job=Job();var store=Fixture(root,"identity",job);
            Refused(()=>CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,(id,ct)=>Task.FromResult(new CloudGitHubRepository{Id="github-456",FullName=job.Repository})).GetAwaiter().GetResult());
            job.RepositoryIds=new string[0];Refused(()=>CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,(id,ct)=>{throw new Exception("must not read an invented repository");}).GetAwaiter().GetResult());
        });
        check("Prepared Cloud repository does not approve a failed native catalogue request",()=>{
            var job=Job();var store=Fixture(root,"offline",job);bool failed=false;
            try{CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,(id,ct)=>{throw new IOException("fictional disconnect");}).GetAwaiter().GetResult();}catch(IOException){failed=true;}
            Assert(failed&&!File.Exists(Path.Combine(store.Root,"cloud-queue-repository-"+job.Id+".dpapi")),"offline catalogue produced approval");
        });
        check("Prepared Cloud correction still verifies its root repository before a new prompt",()=>{
            var job=Job();var store=Fixture(root,"correction",job);string rootId=job.Id;job.Id="c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0";job.Kind="follow-up";job.RootJobId=rootId;
            Refused(()=>CloudQueueRepositoryBinding.Require(store,job,CancellationToken.None,(id,ct)=>Task.FromResult(new CloudGitHubRepository{Id=id,FullName="example/wrong-project"})).GetAwaiter().GetResult());
        });
        check("Cloud cancellation can release a proven preflight blocker without cancelling a created chat",()=>{
            var job=Job();job.SchemaVersion=1;job.State="attention";job.ThreadId=null;job.TurnId=null;var store=new RelayStore(Path.Combine(root,"cloud-queue-repository","preflight-cancel","relay"));
            store.WriteRecord("cloud-job-"+job.Id+".dpapi",job);Assert(new CloudJobs(store).CancelQueued(job.Id).State=="cancelled","provably unstarted preflight could not be cancelled");
            job.State="attention";job.ThreadId="fictional-native-chat";store.WriteRecord("cloud-job-"+job.Id+".dpapi",job);bool refused=false;
            try{new CloudJobs(store).CancelQueued(job.Id);}catch(InvalidOperationException){refused=true;}Assert(refused&&new CloudJobs(store).Get(job.Id).State=="attention","existing native chat was silently cancelled");
        });
    }
}
