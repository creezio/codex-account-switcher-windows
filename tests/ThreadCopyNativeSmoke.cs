using System;
using System.IO;
using System.Threading;
using Creezio.Switcher;
internal static class ThreadCopyNativeSmoke
{
    static int Main(string[] args){try{Run(args).GetAwaiter().GetResult();return 0;}catch(Exception e){Console.WriteLine(e.GetType().Name+": "+e.Message);if(e is ThreadProfileRpcException)Console.WriteLine(((ThreadProfileRpcException)e).Detail);return 1;}}
    static async System.Threading.Tasks.Task Run(string[] args){
        string root=Path.GetFullPath(args.Length>2?args[2]:"work/thread-transfer-probe");Directory.CreateDirectory(root);
        foreach(string n in new[]{"source","target"}){Directory.CreateDirectory(Path.Combine(root,n));File.WriteAllText(Path.Combine(root,n,"config.toml"),"cli_auth_credentials_store = \"file\"\n[analytics]\nenabled = false\n");}
        using(var source=new ThreadProfileRpc(args[0],Path.Combine(root,"source")))using(var target=new ThreadProfileRpc(args[0],Path.Combine(root,"target"))){
            // Source is deliberately more permissive; no turn or tool runs here.
            // The fork must still use the destination's own read-only defaults.
            var created=await source.Call("thread/start",new{cwd=root,historyMode=args.Length>1?args[1]:"legacy",sandbox="danger-full-access",approvalPolicy="never"},CancellationToken.None);var thread=Json.Get(created,"thread");string id=Json.Str(Json.Get(thread,"id"));string path=Json.Str(Json.Get(thread,"path"));
            Console.WriteLine("SOURCE "+id+" mode="+Json.Str(Json.Get(thread,"historyMode")));
            await source.Call("thread/inject_items",new{threadId=id,items=new[]{new{type="message",role="user",content=new[]{new{type="input_text",text="Fixture history: the marker is ORANGE-42."}}}}},CancellationToken.None);
            await source.Call("thread/name/set",new{threadId=id,name="Thread copy fixture"},CancellationToken.None);
            await source.Call("thread/unsubscribe",new{threadId=id},CancellationToken.None);
            await System.Threading.Tasks.Task.Delay(2000);
            Console.WriteLine("PATH exists="+File.Exists(path));
            if(path.Length==0)throw new Exception("No rollout path");
            string turn=Guid.NewGuid().ToString();
            using(var writer=new StreamWriter(new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete),new System.Text.UTF8Encoding(false))){
                writer.WriteLine(Json.Write(new{timestamp=DateTime.UtcNow.ToString("o"),type="event_msg",payload=new{type="task_started",turn_id=turn,model_context_window=258400}}));
                bool modern=args.Length>1&&args[1]=="paginated";
                if(modern)writer.WriteLine(Json.Write(new{timestamp=DateTime.UtcNow.ToString("o"),type="event_msg",payload=new{type="item_completed",thread_id=id,turn_id=turn,item=new{type="UserMessage",id=Guid.NewGuid().ToString(),content=new[]{new{type="text",text="Fixture history: the marker is ORANGE-42.",text_elements=new object[0]}}}}}));
                else writer.WriteLine(Json.Write(new{timestamp=DateTime.UtcNow.ToString("o"),type="event_msg",payload=new{type="user_message",message="Fixture history: the marker is ORANGE-42.",images=new string[0],local_images=new string[0],text_elements=new object[0]}}));
                writer.WriteLine(Json.Write(new{timestamp=DateTime.UtcNow.ToString("o"),type="response_item",payload=new{type="message",role="assistant",content=new[]{new{type="output_text",text="I remember ORANGE-42."}},phase="final_answer"}}));
                if(modern)writer.WriteLine(Json.Write(new{timestamp=DateTime.UtcNow.ToString("o"),type="event_msg",payload=new{type="item_completed",thread_id=id,turn_id=turn,item=new{type="AgentMessage",id=Guid.NewGuid().ToString(),content=new[]{new{type="Text",text="I remember ORANGE-42."}},phase="final_answer"}}}));
                else writer.WriteLine(Json.Write(new{timestamp=DateTime.UtcNow.ToString("o"),type="event_msg",payload=new{type="agent_message",message="I remember ORANGE-42.",phase="final_answer"}}));
                writer.WriteLine(Json.Write(new{timestamp=DateTime.UtcNow.ToString("o"),type="event_msg",payload=new{type="task_complete",turn_id=turn,last_agent_message="I remember ORANGE-42."}}));
            }
            string snapshot=Path.Combine(root,"snapshot.jsonl");if(File.Exists(snapshot))File.Delete(snapshot);
            var before=RolloutSnapshot.Scan(path,id,snapshot);
            before=RolloutSnapshot.Scan(snapshot,id);
            string tag="creezio-copy-"+Guid.NewGuid().ToString("N");
            var defaults=await ThreadCopies.DestinationDefaults(target,root,CancellationToken.None);
            string permission=Json.Str(Json.Get(Json.Get(defaults,"activePermissionProfile"),"id"));
            var fork=await target.Call("thread/fork",new{threadId=id,path=snapshot,cwd=root,deferGoalContinuation=true,threadSource=tag,permissions=permission,approvalPolicy=Json.Get(defaults,"approvalPolicy"),approvalsReviewer=Json.Get(defaults,"approvalsReviewer"),excludeTurns=true},CancellationToken.None);
            var dest=Json.Get(fork,"thread");string destId=Json.Str(Json.Get(dest,"id")),destPath=Json.Str(Json.Get(dest,"path"));
            if(Json.Str(Json.Get(fork,"approvalPolicy"))!="on-request"||Json.Str(Json.Get(Json.Get(fork,"sandbox"),"type"))!="readOnly")throw new Exception("Source permissions leaked into destination");
            await target.Call("thread/goal/clear",new{threadId=destId},CancellationToken.None);
            var resumed=await target.Call("thread/resume",new{threadId=destId,permissions=permission,approvalPolicy=Json.Get(defaults,"approvalPolicy"),approvalsReviewer=Json.Get(defaults,"approvalsReviewer"),deferGoalContinuation=true,excludeTurns=true},CancellationToken.None);
            if(Json.Str(Json.Get(resumed,"approvalPolicy"))!="on-request"||Json.Str(Json.Get(Json.Get(resumed,"sandbox"),"type"))!="readOnly")throw new Exception("Resumed permissions changed");
            await target.Call("thread/name/set",new{threadId=destId,name="Copie · été 👋"},CancellationToken.None);
            var after=RolloutSnapshot.Scan(destPath,destId);
            Console.WriteLine("Native fork: newId="+(id!=destId)+" equal="+(before.Digest==after.Digest)+" items="+before.Items+"/"+after.Items+" marker="+Json.Str(Json.Get(dest,"threadSource"))+" approvals="+Json.Write(Json.Get(fork,"approvalPolicy"))+" sandbox="+Json.Write(Json.Get(fork,"sandbox")));
            await target.Call("thread/unsubscribe",new{threadId=destId},CancellationToken.None);
            await System.Threading.Tasks.Task.Delay(1000);
            target.Dispose();
            await ThreadCopyMigration.Run(args[0],Path.Combine(root,"target"),destId,CancellationToken.None);
            using(var verify=new ThreadProfileRpc(args[0],Path.Combine(root,"target"))){
            var paged=await verify.Call("thread/items/list",new{threadId=destId,limit=100},CancellationToken.None);
            string pagedHistory=Json.Write(paged);
            if(!pagedHistory.Contains("ORANGE-42")||!pagedHistory.Contains("I remember"))throw new Exception("Desktop paginated history missing");
            if(RolloutSnapshot.Scan(destPath,destId).ModelDigest!=before.ModelDigest)throw new Exception("Native migration changed model history");
            await ThreadCopies.VerifyVisible(verify,destId,before.VisibleMessages,CancellationToken.None);
            Console.WriteLine("Native Desktop paginated history preserved.");
            var listed=await verify.Call("thread/list",new{limit=100,modelProviders=new string[0],useStateDbOnly=true},CancellationToken.None);

            bool found=false;foreach(var item in RelayEngine.Rows(Json.Get(listed,"data")))if(Json.Str(Json.Get(item,"id"))==destId){found=RolloutSnapshot.HasMarker(Path.Combine(root,"target"),item,tag);}
            Console.WriteLine("Recovery marker persisted="+found);
            if(before.Digest!=after.Digest||!found)throw new Exception("Native verification mismatch");
            Console.WriteLine("Native visible history preserved; no model turn started.");
            }
            File.Delete(snapshot);
        }
    }
}
