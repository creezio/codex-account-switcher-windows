using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class HeadlessApprovalTests
{
    private static void Assert(bool value,string why){if(!value)throw new Exception(why);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected approval rejection");}
    private static object Obj(object value){return HeadlessTransportFixture.Obj(value);}
    private static object Params(HeadlessTransportFixture f){return Obj(new{threadId=f.ThreadId,turnId=f.TurnId,itemId="fixture-file-change",command="echo Bearer PRIVATE_SECRET_VALUE",reason="api_key=PRIVATE_KEY_VALUE",cwd=f.Channel.Workspace});}
    internal static void RunAll(Action<string,Action> check,string root,string auth)
    {
        int number=1000;
        check("headless native approval waits for a human and exposes a redacted encrypted request",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth))using(var cancel=new CancellationTokenSource()){
                f.Own();var approvals=new HeadlessApprovals(f.Store);var waiting=approvals.Wait(f.Channel,"item/commandExecution/requestApproval",Params(f),cancel.Token);var pending=approvals.Pending().Single();
                Assert(!waiting.IsCompleted&&pending.ThreadId==f.ThreadId&&pending.AccountKey==f.Channel.AccountKey&&!pending.Summary.Contains("PRIVATE_SECRET_VALUE")&&!pending.Summary.Contains("PRIVATE_KEY_VALUE"),"native request was approved or leaked secrets");
                string bytes=System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,"headless-approval-"+pending.Id+".dpapi")));Assert(!bytes.Contains(f.Channel.Workspace)&&!bytes.Contains("PRIVATE_KEY_VALUE"),"native request was stored in cleartext");
                approvals.Respond(pending.Id,"decline");Assert(Json.Str(Json.Get(waiting.GetAwaiter().GetResult(),"decision"))=="decline"&&approvals.Pending().Length==0,"human rejection was not relayed");
            }
        });
        check("headless command and file approvals support exactly one explicit accept, decline or cancel",()=>{
            foreach(string method in new[]{"item/commandExecution/requestApproval","item/fileChange/requestApproval"})foreach(string decision in new[]{"accept","decline","cancel"})using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own();var approvals=new HeadlessApprovals(f.Store);var waiting=approvals.Wait(f.Channel,method,Params(f),CancellationToken.None,null,method=="item/fileChange/requestApproval"?"fixture-file.txt\n+explicit fictitious patch":null);var id=approvals.Pending().Single().Id;Reject(()=>approvals.Respond(id,"acceptForSession"));approvals.Respond(id,decision);Assert(Json.Str(Json.Get(waiting.GetAwaiter().GetResult(),"decision"))==decision,"native decision changed scope or outcome");Reject(()=>approvals.Respond(id,"accept"));
            }
        });
        check("headless permission grants copy the requested permissions exactly and remain limited to one turn",()=>{
            foreach(string decision in new[]{"accept","decline"})using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own();var approvals=new HeadlessApprovals(f.Store);var permissions=Obj(new{fileSystem=new{write=new[]{f.Channel.Workspace}},network=new{enabled=false}});var args=Obj(new{threadId=f.ThreadId,turnId=f.TurnId,permissions=permissions});var expected=Json.Write(Json.Get(args,"permissions"));
                var waiting=approvals.Wait(f.Channel,"item/permissions/requestApproval",args,CancellationToken.None);Json.Obj(Json.Get(args,"permissions"))["network"]=Obj(new{enabled=true});approvals.Respond(approvals.Pending().Single().Id,decision);var response=waiting.GetAwaiter().GetResult();
                Assert(Json.Str(Json.Get(response,"scope"))=="turn"&&Json.Write(Json.Get(response,"permissions"))==(decision=="accept"?expected:"{}"),"permission response widened authority or retained a mutable request");
            }
        });
        check("headless user input validates every exact question and emits the native answers envelope",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own();var approvals=new HeadlessApprovals(f.Store);var waiting=approvals.Wait(f.Channel,"item/tool/requestUserInput",Obj(new{threadId=f.ThreadId,turnId=f.TurnId,questions=new[]{new{id="question-a",header="Fixture",question="Choose a value",isSecret=false}}}),CancellationToken.None);var id=approvals.Pending().Single().Id;
                Reject(()=>approvals.Respond(id,"accept",new Dictionary<string,string[]>{{"different",new[]{"answer"}}}));Reject(()=>approvals.Respond(id,"accept",new Dictionary<string,string[]>{{"question-a",new string[0]}}));
                approvals.Respond(id,"accept",new Dictionary<string,string[]>{{"question-a",new[]{"Fixture answer"}}});var response=waiting.GetAwaiter().GetResult();Assert(RelayEngine.Rows(Json.Get(Json.Get(Json.Get(response,"answers"),"question-a"),"answers")).Select(Json.Str).Single()=="Fixture answer","user reply did not follow the native question ID and response schema");
            }
        });
        check("headless native interaction refuses secret questions and unsupported methods before journaling",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own();var approvals=new HeadlessApprovals(f.Store);Reject(()=>approvals.Wait(f.Channel,"unknown/requestApproval",Params(f),CancellationToken.None).GetAwaiter().GetResult());
                Reject(()=>approvals.Wait(f.Channel,"item/tool/requestUserInput",Obj(new{threadId=f.ThreadId,questions=new[]{new{id="secret",isSecret=true,question="Password"}}}),CancellationToken.None).GetAwaiter().GetResult());Assert(approvals.Pending().Length==0&&Directory.GetFiles(f.Store.Root,"headless-approval-*.dpapi").Length==0,"unsafe native request was persisted");
            }
        });
        check("headless native approval expires to a durable refusal without any automatic grant",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own();var approvals=new HeadlessApprovals(f.Store,TimeSpan.FromSeconds(1));var waiting=approvals.Wait(f.Channel,"item/commandExecution/requestApproval",Params(f),CancellationToken.None);var pending=approvals.Pending().Single();var response=waiting.GetAwaiter().GetResult();
                Assert(Json.Str(Json.Get(response,"decision"))=="decline"&&f.Store.ReadRecord<HeadlessApproval>("headless-approval-"+pending.Id+".dpapi").State=="expired","expired request did not decline");Reject(()=>approvals.Respond(pending.Id,"accept"));
            }
        });
        check("headless cancelled approval cannot be approved afterwards",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth))using(var cancel=new CancellationTokenSource()){
                f.Own();var approvals=new HeadlessApprovals(f.Store);var waiting=approvals.Wait(f.Channel,"item/fileChange/requestApproval",Params(f),cancel.Token);var id=approvals.Pending().Single().Id;cancel.Cancel();Assert(Reject(()=>waiting.GetAwaiter().GetResult()) is OperationCanceledException,"cancelled wait did not terminate");
                var record=f.Store.ReadRecord<HeadlessApproval>("headless-approval-"+id+".dpapi");Assert(record.State=="cancelled"&&record.Decision=="decline","cancelled wait retained an active grant");Reject(()=>approvals.Respond(id,"accept"));
            }
        });
        check("headless approval rechecks persisted channel and instance opt-out before consuming the response",()=>{
            foreach(bool changeInstance in new[]{false,true})using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own();var approvals=new HeadlessApprovals(f.Store);var waiting=approvals.Wait(f.Channel,"item/commandExecution/requestApproval",Params(f),CancellationToken.None);var id=approvals.Pending().Single().Id;
                if(changeInstance){f.Instance.HeadlessEnabled=false;f.Accounts.Save();}else f.Store.SetEnabled(f.Channel.Id,false);
                Reject(()=>approvals.Respond(id,"accept"));Reject(()=>waiting.GetAwaiter().GetResult());var record=f.Store.ReadRecord<HeadlessApproval>("headless-approval-"+id+".dpapi");Assert(record.State=="cancelled"&&record.Decision=="decline","revoked request was granted or left actionable");
            }
        });
        check("headless approvals cannot target a foreign conversation or write through an invalid request ID",()=>{
            using(var f=new HeadlessTransportFixture(root,++number,auth)){
                f.Own();var approvals=new HeadlessApprovals(f.Store);Reject(()=>approvals.Wait(f.Channel,"item/permissions/requestApproval",Obj(new{threadId=Guid.NewGuid().ToString(),permissions=new{}}),CancellationToken.None).GetAwaiter().GetResult());Reject(()=>approvals.Respond("../outside","accept"));Assert(approvals.Pending().Length==0,"foreign conversation gained a native permission request");
            }
        });
    }
}
