using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class CloudEnvironmentUpdateTests
{
    private const string ConfigId="configuration_fixture~asenvcfg_test";
    private const string OriginalVersion="version_original~cecfgver_test";
    private const string PublishedVersion="version_published~cecfgver_test";
    private const string DraftId="draft_fixture~draft_test";
    private const string RemoteOperationId="operation_fixture";
    private const string Secret="PRIVATE_PATCH_SCRIPT_MUST_NEVER_ENTER_UPDATE_RECEIPTS";
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected update to be rejected");}
    private static string Encode(object value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(value))).TrimEnd('=').Replace('+','-').Replace('/','_');}
    private static AuthIdentity Identity(string account="account-a")
    {
        string access="e30."+Encode(new Dictionary<string,object>{{"exp",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600},{"https://api.openai.com/auth",new{chatgpt_account_id=account,chatgpt_user_id="user-a"}}})+".fixture";
        return new AuthIdentity{Key=account+":user-a",AccountId=account,AccessToken=access};
    }
    private static string RecordFile(string root,string id){return Path.Combine(root,"cloud-updates-v1",id+".json");}
    private static object Record(string root,string id){return Json.Read<object>(File.ReadAllText(RecordFile(root,id)));}
    private static CloudDraftPatch Patch(){return new CloudDraftPatch{BaseVersionId=OriginalVersion,ExpectedRevision=0,InstallScript=Secret};}
    private static HttpResponseMessage Response(object value)
    {return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Json.Write(value),Encoding.UTF8,"application/json")};}
    private sealed class Handler:HttpMessageHandler
    {
        internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> OnRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){return OnRequest(request,token);}
    }
    private sealed class Fixture:IDisposable
    {
        internal readonly string Root,Id=Guid.NewGuid().ToString("N");
        internal AuthIdentity Current=Identity();
        internal readonly CloudBackend Backend;
        private readonly HttpClient client;
        internal readonly List<string> Requests=new List<string>(),MutationStages=new List<string>();
        internal readonly List<object> MutationBodies=new List<object>();
        internal bool ExistingDraft,VersionChanged,DraftBaseChanged,Published,FinalNotReady,TamperFreshDraft,PublishBeforeComplete;
        internal string OperationState="RUNNING",OperationEnvironment="environment_fixture",TimeoutStage;
        internal int Creates,Patches,Begins,Completes,DraftReads;
        internal Fixture(string root,int number)
        {
            Root=Path.Combine(root,"cloud-update-"+number);Directory.CreateDirectory(Root);
            var handler=new Handler{OnRequest=Respond};client=new HttpClient(handler);Backend=new CloudBackend(client,()=>Current,CloudBackend.Capture(Current));
        }
        private object Configuration(bool draft=false)
        {
            var value=new Dictionary<string,object>{{"id",ConfigId},{"name","Fixture"},{"is_owner",true},{"share_settings","private"},{"status",FinalNotReady&&Published?"starting":"ready"},{"version_id",Published?PublishedVersion:VersionChanged?"version_other":OriginalVersion},{"version_revision",Published?2:1},{"latest_ready_version_id",FinalNotReady&&Published?null:Published?PublishedVersion:OriginalVersion},{"repositories",new[]{new{repository_id="github/123",@ref="main",depth=1}}},{"network_policy",new{type="disabled"}},{"environment_id","environment_fixture"},{"install_script",Published?Secret:"old"}};
            if((draft||ExistingDraft)&&!(Published&&PublishBeforeComplete))value["draft"]=new{id=DraftId,base_version_id=DraftBaseChanged?"version_other":OriginalVersion,revision=TamperFreshDraft&&DraftReads>1?2:Patches==0?0:1,repositories=new[]{new{repository_id="github/123",@ref="main",depth=1}},network_policy=new{type="disabled"},install_script=Patches==0?"old":Secret};
            return value;
        }
        private object Operation(){return new{id=RemoteOperationId,kind="APPROVE_ENVIRONMENT_CONFIG_DRAFT",environment_id=OperationEnvironment,state=OperationState,failure=OperationState=="FAILED"?new{code="BOOT_FAILED",message=Secret}:null};}
        private Task<HttpResponseMessage> Respond(HttpRequestMessage request,CancellationToken token)
        {
            string method=request.Method.Method,path=request.RequestUri.AbsolutePath;Requests.Add(method+" "+path);
            bool mutation=method!="GET";
            if(mutation){
                var record=Record(Root,Id);string stage=Json.Str(Json.Get(record,"Stage"));MutationStages.Add(stage);
                Assert(Json.Str(Json.Get(record,"State"))=="sending","mutation reached backend before durable sending state");
                MutationBodies.Add(Json.Read<object>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
                if(TimeoutStage==stage)throw new TaskCanceledException("private transport "+Secret);
            }
            if(method=="GET"&&path=="/v1/environment-configs/"+ConfigId)return Task.FromResult(Response(Configuration()));
            if(method=="POST"&&path.EndsWith("/drafts",StringComparison.Ordinal)){Creates++;return Task.FromResult(Response(new{draft_id=DraftId,environment_id="environment_fixture",thread_id=(string)null}));}
            if(method=="GET"&&path.EndsWith("/drafts/"+DraftId,StringComparison.Ordinal)){DraftReads++;return Task.FromResult(Response(Configuration(true)));}
            if(method=="PATCH"&&path.EndsWith("/drafts/"+DraftId,StringComparison.Ordinal)){Patches++;return Task.FromResult(Response(Configuration(true)));}
            if(method=="POST"&&path.EndsWith("/approve/begin",StringComparison.Ordinal)){Begins++;return Task.FromResult(Response(Operation()));}
            if(method=="GET"&&path=="/v1/environment-operations/"+RemoteOperationId){if(PublishBeforeComplete&&OperationState=="SUCCEEDED")Published=true;return Task.FromResult(Response(Operation()));}
            if(method=="POST"&&path.EndsWith("/approve/complete",StringComparison.Ordinal)){Completes++;Published=true;return Task.FromResult(Response(Configuration(true)));}
            throw new InvalidOperationException("Unexpected fixture request "+method+" "+path);
        }
        internal CloudEnvironmentUpdateResult Update(CloudDraftPatch patch=null,string id=null,string account=null,string instance="instance-a")
        {return CloudEnvironmentUpdate.Update(Root,instance,account??"account-a:user-a",Backend,ConfigId,patch??Patch(),id??Id,CancellationToken.None).GetAwaiter().GetResult();}
        internal CloudEnvironmentUpdateResult Follow(string account=null,string instance="instance-a")
        {return CloudEnvironmentUpdate.Follow(Root,instance,account??"account-a:user-a",Backend,Id,CancellationToken.None).GetAwaiter().GetResult();}
        public void Dispose(){Backend.Dispose();client.Dispose();}
    }
    public static void RunAll(Action<string,Action> check,string root)
    {
        int number=0;
        check("cloud environment update persists each mutation stage and uses the draft revision",()=>{
            using(var f=new Fixture(root,++number)){
                var result=f.Update();
                Assert(result.State=="publishing"&&f.Creates==1&&f.Patches==1&&f.Begins==1&&f.Completes==0,"update did not stop at pending publication");
                Assert(f.MutationStages.SequenceEqual(new[]{"creating-draft","patching-draft","beginning-approval"}),"mutation stages missing or reordered");
                Assert(Json.Get(f.MutationBodies[0],"start_thread") is bool&&!(bool)Json.Get(f.MutationBodies[0],"start_thread"),"update created an unnecessary conversation");
                Assert(Json.Number(Json.Get(f.MutationBodies[1],"expected_revision"))==0&&Json.Str(Json.Get(f.MutationBodies[1],"base_version_id"))==OriginalVersion,"draft base or revision changed");
                Assert(Json.Str(Json.Get(Record(f.Root,f.Id),"State"))=="publishing","pending publication not durable");
            }
        });
        check("cloud environment update refuses changed version and existing user draft before mutation",()=>{
            using(var f=new Fixture(root,++number)){
                f.VersionChanged=true;Reject(()=>f.Update());Assert(f.Creates==0&&f.Patches==0&&f.Begins==0,"changed version overwritten");
            }
            using(var f=new Fixture(root,++number)){
                f.ExistingDraft=true;Reject(()=>f.Update());Assert(f.Creates==0&&f.Patches==0&&f.Begins==0,"existing user draft overwritten");
            }
        });
        check("cloud environment update rejects a draft created against another published base",()=>{
            using(var f=new Fixture(root,++number)){
                f.DraftBaseChanged=true;Reject(()=>f.Update());Assert(f.Creates==1&&f.Patches==0&&f.Begins==0,"foreign base draft patched or approved");
            }
        });
        check("cloud environment update follows pending publication after restart without replaying mutations",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();var pending=f.Follow();Assert(pending.State=="publishing"&&f.Creates==1&&f.Patches==1&&f.Begins==1&&f.Completes==0,"pending follow resubmitted update");
                f.OperationState="SUCCEEDED";var completed=f.Follow();
                Assert(completed.State=="completed"&&completed.Configuration.VersionId==PublishedVersion&&f.Creates==1&&f.Patches==1&&f.Begins==1&&f.Completes==1,"successful follow did not verify the new version");
                var repeat=f.Follow();Assert(repeat.State=="completed"&&f.Completes==1,"completed follow replayed publication");
            }
        });
        check("cloud environment update journal stores no script, patch body or returned configuration",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();string pending=File.ReadAllText(RecordFile(f.Root,f.Id));
                Assert(!pending.Contains(Secret)&&!pending.Contains("install_script")&&!pending.Contains("InstallScript")&&!pending.Contains("NetworkPolicy"),"pending journal stored patch data");
                f.OperationState="SUCCEEDED";f.Follow();string completed=File.ReadAllText(RecordFile(f.Root,f.Id));
                Assert(!completed.Contains(Secret)&&!completed.Contains("install_script")&&!completed.Contains("InstallScript"),"completed journal stored configuration data");
            }
        });
        check("cloud environment update uncertain draft creation or approval is never replayed",()=>{
            foreach(var stage in new[]{"creating-draft","beginning-approval"})using(var f=new Fixture(root,++number)){
                f.TimeoutStage=stage;var error=Reject(()=>f.Update());Assert(error is CloudBackendUncertainException,"write uncertainty lost");
                int requests=f.Requests.Count;var first=f.Follow();var second=f.Update();
                Assert(first.State=="uncertain"&&second.State=="uncertain"&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET ",StringComparison.Ordinal))&&Json.Str(Json.Get(Record(f.Root,f.Id),"State"))=="uncertain","uncertain update resent or marked completed without proof");
            }
        });
        check("cloud environment update refuses account drift before sending and on follow",()=>{
            using(var f=new Fixture(root,++number)){
                f.Current=Identity("account-b");Reject(()=>f.Update());Assert(f.Requests.Count==0,"wrong account reached backend");
            }
            using(var f=new Fixture(root,++number)){
                f.Update();int requests=f.Requests.Count;f.Current=Identity("account-b");Reject(()=>f.Follow());Assert(f.Requests.Count==requests&&f.Completes==0,"changed account published pending update");
            }
        });
        check("cloud environment update binds operation IDs to the exact account, instance and patch",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();int requests=f.Requests.Count;
                Reject(()=>f.Update(new CloudDraftPatch{BaseVersionId=OriginalVersion,ExpectedRevision=0,InstallScript="different"}));
                Reject(()=>f.Follow(account:"account-other"));Reject(()=>f.Follow(instance:"instance-other"));
                Assert(f.Requests.Count==requests&&f.Creates==1&&f.Patches==1&&f.Begins==1,"conflicting operation touched backend");
            }
        });
        check("cloud environment update reports failed approval without completing or leaking backend text",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();f.OperationState="FAILED";var result=f.Follow();
                Assert(result.State=="failed"&&result.FailureCode=="BOOT_FAILED"&&f.Completes==0,"failed approval treated as complete");
                Assert(!Json.Write(result).Contains(Secret)&&!File.ReadAllText(RecordFile(f.Root,f.Id)).Contains(Secret),"failure exposes backend text or patch secret");
            }
        });
        check("cloud environment update does not claim completion before the new version is ready",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();f.OperationState="SUCCEEDED";f.FinalNotReady=true;
                var pending=f.Follow();Assert(pending.State=="verifying"&&Json.Str(Json.Get(Record(f.Root,f.Id),"State"))=="verifying"&&f.Completes==1,"unready final state marked completed");
                int requests=f.Requests.Count;f.FinalNotReady=false;var ready=f.Follow();
                Assert(ready.State=="completed"&&f.Completes==1&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET ",StringComparison.Ordinal)),"verification replayed completion rather than reading final state");
            }
        });
        check("cloud environment update does not replay a lost completion acknowledgement",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();f.OperationState="SUCCEEDED";f.TimeoutStage="completing-approval";
                Assert(Reject(()=>f.Follow()) is CloudBackendUncertainException,"completion uncertainty lost");
                int requests=f.Requests.Count;var result=f.Follow();Assert(result.State=="uncertain"&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET ",StringComparison.Ordinal))&&Json.Str(Json.Get(Record(f.Root,f.Id),"State"))=="uncertain","uncertain completion replayed");
            }
        });
        check("cloud environment update treats persisted interrupted sending as uncertain without resubmission",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();var record=Json.Obj(Record(f.Root,f.Id));record["State"]="sending";record["Stage"]="patching-draft";File.WriteAllText(RecordFile(f.Root,f.Id),Json.Write(record));
                int requests=f.Requests.Count;var recovered=f.Follow();
                Assert(recovered.State=="uncertain"&&recovered.FailureCode=="interrupted-workflow"&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET ",StringComparison.Ordinal)),"crashed sending stage was replayed");
            }
        });
        check("cloud environment update rejects draft edits occurring between patch and publication",()=>{
            using(var f=new Fixture(root,++number)){
                f.TamperFreshDraft=true;var error=Reject(()=>f.Update()) as CloudBackendException;
                Assert(error!=null&&error.Code=="conflict"&&f.Creates==1&&f.Patches==1&&f.Begins==0&&f.Completes==0,"concurrently edited draft published");
            }
        });
        check("cloud environment update refuses an approval operation belonging to another environment",()=>{
            using(var f=new Fixture(root,++number)){
                f.OperationEnvironment="environment_other";
                Assert(Reject(()=>f.Update()) is CloudBackendUncertainException&&Json.Str(Json.Get(Record(f.Root,f.Id),"State"))=="uncertain"&&f.Completes==0,"foreign operation accepted after approval dispatch");
            }
            using(var f=new Fixture(root,++number)){
                f.Update();f.OperationEnvironment="environment_other";f.OperationState="SUCCEEDED";
                Assert(Reject(()=>f.Follow()) is CloudBackendUncertainException&&Json.Str(Json.Get(Record(f.Root,f.Id),"State"))=="uncertain"&&f.Completes==0,"foreign operation completed pending approval");
            }
        });
        check("cloud environment update accepts a version already published before completion acknowledgment",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();f.PublishBeforeComplete=true;f.OperationState="SUCCEEDED";var result=f.Follow();
                Assert(result.State=="completed"&&result.Configuration.VersionId==PublishedVersion&&result.Configuration.Draft==null&&f.Creates==1&&f.Patches==1&&f.Begins==1&&f.Completes==1,"early published version rejected or mutation replayed");
                f.Follow();Assert(f.Completes==1,"early publication acknowledgment repeated");
            }
        });
        check("cloud environment update recovers the precise historical approval conflict by readback only",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();var record=Json.Obj(Record(f.Root,f.Id));record["State"]="failed";record["Stage"]="approval-pending";record["FailureCode"]="conflict";record["HttpStatus"]=409;File.WriteAllText(RecordFile(f.Root,f.Id),Json.Write(record));
                f.PublishBeforeComplete=true;f.OperationState="SUCCEEDED";var result=f.Follow();
                Assert(result.State=="completed"&&result.FailureCode==null&&f.Creates==1&&f.Patches==1&&f.Begins==1&&f.Completes==1,"historical false conflict not reconciled through its existing operation");
            }
        });
        check("cloud environment update never revives a conflict from an unrelated workflow stage",()=>{
            using(var f=new Fixture(root,++number)){
                f.Update();var record=Json.Obj(Record(f.Root,f.Id));record["State"]="failed";record["Stage"]="patching-draft";record["FailureCode"]="conflict";File.WriteAllText(RecordFile(f.Root,f.Id),Json.Write(record));
                f.PublishBeforeComplete=true;f.OperationState="SUCCEEDED";int requests=f.Requests.Count;var result=f.Follow();
                Assert(result.State=="failed"&&f.Completes==0&&f.Requests.Skip(requests).All(r=>r.StartsWith("GET /v1/environment-configs/",StringComparison.Ordinal)),"unrelated conflict resumed publication");
            }
        });
    }
}
