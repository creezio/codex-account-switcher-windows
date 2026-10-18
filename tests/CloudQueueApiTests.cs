using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Creezio.Switcher;

internal static class CloudQueueApiTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Refused(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected Cloud queue API refusal");}
    private static object Obj(object value){return Json.Read<object>(Json.Write(value));}
    private static Dictionary<string,object> Team()
    {
        return Json.Obj(Obj(new{id="11111111111111111111111111111111",name="Fictional project team",repository="example/project",mandate="Develop the explicitly reserved lots for this project; review remains with the source.",explicitDelegation=true,targets=new[]{new{instanceId="fictional-instance",expectedAccountKey="fictional-account",configurationId="fictional-configuration"}}}));
    }
    private static Dictionary<string,object> Mission()
    {
        return Json.Obj(Obj(new{id="22222222222222222222222222222222",teamId="11111111111111111111111111111111",title="Bounded fictional work",prompt="Develop the reserved reader and prove its owner isolation. Do not publish.",repository="example/project",feature="bounded-reader",lot="3-01",specification="docs/specification.md#lot-3",explicitDelegation=true,reservedFiles=new[]{"lib/reader.ts"},eligibleInstanceIds=new[]{"fictional-instance"},dependencies=new[]{new{missionId="33333333333333333333333333333333"}}}));
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("Cloud queue public schemas expose five source-bound tools and no internal identity fields",()=>{
            string[] names={"configure_cloud_team","prepare_cloud_mission","list_cloud_queue","review_cloud_dependency","control_cloud_queue"};
            var tools=RelayMcp.Tools().Select(Obj).Where(tool=>names.Contains(Json.Str(Json.Get(tool,"name")))).ToArray();Assert(tools.Length==5,"queue tool set changed");
            foreach(var tool in tools){var schema=Json.Get(tool,"inputSchema");Assert(RelayEngine.Rows(Json.Get(schema,"required")).Select(Json.Str).Contains("session"),"source session optional");Assert(Object.Equals(Json.Get(schema,"additionalProperties"),false),"open public schema");}
            var missionTool=tools.Single(tool=>Json.Str(Json.Get(tool,"name"))=="prepare_cloud_mission");var mission=Json.Get(Json.Get(Json.Get(missionTool,"inputSchema"),"properties"),"mission");
            var fields=Json.Obj(Json.Get(mission,"properties"));Assert(!fields.ContainsKey("Source")&&!fields.ContainsKey("source")&&!fields.ContainsKey("state")&&!fields.ContainsKey("observedJobId"),"caller may set identity/state");
            Assert(RelayEngine.Rows(Json.Get(mission,"required")).Select(Json.Str).Contains("reservedFiles"),"scope reservation optional");
            Assert(Object.Equals(Json.Get(mission,"additionalProperties"),false),"open mission schema");
            var control=tools.Single(tool=>Json.Str(Json.Get(tool,"name"))=="control_cloud_queue");var controls=Json.Get(Json.Get(control,"inputSchema"),"properties");Assert(RelayEngine.Rows(Json.Get(Json.Get(controls,"action"),"enum")).Select(Json.Str).Contains("reject"),"reviewed failure release missing");Assert(RelayEngine.Rows(Json.Get(Json.Get(controls,"threadId"),"type")).Select(Json.Str).Contains("null"),"pre-chat terminal failure cannot be reviewed");
        });
        check("Cloud queue team parsing requires scoped actual mandate and rejects impersonated source",()=>{
            var input=Team();var team=CloudCommands.QueueTeam(input);Assert(team.Repository=="example/project"&&team.MaximumParallel==2&&team.ExplicitDelegation&&team.Targets.Single().ExpectedAccountKey=="fictional-account","team configuration lost");
            input["explicitDelegation"]=false;Refused(()=>CloudCommands.QueueTeam(input));input=Team();input["mandate"]=" ";Refused(()=>CloudCommands.QueueTeam(input));
            foreach(string field in new[]{"Source","source","AuthorizedUtc","Paused"}){input=Team();input[field]="forged";Refused(()=>CloudCommands.QueueTeam(input));}
            input=Team();input["targets"]=new[]{new{instanceId="fictional-instance",expectedAccountKey="fictional-account",configurationId="fictional-configuration",source="forged"}};Refused(()=>CloudCommands.QueueTeam(Obj(input)));
        });
        check("Cloud queue mission parsing defaults dependency to accepted and preserves explicit completed gate",()=>{
            var input=Mission();var mission=CloudCommands.QueueMission(input);Assert(mission.Dependencies.Single().Mode=="accepted"&&mission.ReservedFiles.Single()=="lib/reader.ts"&&mission.EligibleInstanceIds.Single()=="fictional-instance","prepared mandate changed");
            input["dependencies"]=new[]{new{missionId="33333333333333333333333333333333",mode="completed"}};Assert(CloudCommands.QueueMission(Obj(input)).Dependencies.Single().Mode=="completed","explicit weaker gate lost");
            input["dependencies"]=new[]{new{missionId="33333333333333333333333333333333",mode="returned"}};Refused(()=>CloudCommands.QueueMission(Obj(input)));
        });
        check("Cloud queue API rejects injected receipts, source, targets and internal prepared state",()=>{
            foreach(string field in new[]{"Source","source","Request","Target","EligibleTargets","State","state","ObservedJobId","AcceptedUtc"}){var input=Mission();input[field]="forged";Refused(()=>CloudCommands.QueueMission(input));}
            var missing=Mission();missing["explicitDelegation"]=false;Refused(()=>CloudCommands.QueueMission(missing));missing=Mission();missing.Remove("specification");Refused(()=>CloudCommands.QueueMission(missing));
            foreach(string key in new[]{"reservedFiles","eligibleInstanceIds","dependencies"}){var malformed=Mission();malformed[key]="not-an-array";Refused(()=>CloudCommands.QueueMission(malformed));}
            foreach(string key in new[]{"reservedFiles","eligibleInstanceIds"}){var empty=Mission();empty[key]=new string[0];Refused(()=>CloudCommands.QueueMission(Obj(empty)));}
        });
        check("Cloud queue API rejects fractional capacity, invalid flags and priority without coercion",()=>{
            foreach(object number in new object[]{0,9,1.5}){var input=Team();input["maximumParallel"]=number;Refused(()=>CloudCommands.QueueTeam(input));}
            var invalid=Team();invalid["enabled"]="true";Refused(()=>CloudCommands.QueueTeam(invalid));
            foreach(object number in new object[]{-1001,1001,2.5}){var input=Mission();input["priority"]=number;Refused(()=>CloudCommands.QueueMission(input));}
        });
        check("Cloud queue snapshot excludes full mandate, prompt and source session",()=>{
            var team=CloudCommands.QueueTeam(Team());team.Source=new RelaySession{Token="PRIVATE_SESSION_NOT_FOR_OUTPUT",Thread="fictional-source",Home=root,Account="fictional-source-account",Channel="source"};
            var mission=CloudCommands.QueueMission(Mission());mission.Prompt="PRIVATE_FULL_MANDATE_NOT_FOR_OUTPUT";
            string json=Json.Write(CloudCommands.QueueSnapshot(new CloudQueueSnapshot{Teams=new List<CloudQueueTeam>{team},Missions=new List<CloudPreparedMission>{mission}}));
            Assert(!json.Contains("PRIVATE_SESSION_NOT_FOR_OUTPUT")&&!json.Contains("PRIVATE_FULL_MANDATE_NOT_FOR_OUTPUT")&&!json.Contains(team.Mandate),"private planning/session echoed");Assert(json.Contains("bounded-reader")&&json.Contains("reservedFiles"),"compact scheduling evidence missing");
        });
        check("Cloud queue CLI and MCP refuse missing or foreign-home session before queue access",()=>{
            string fixture=Path.Combine(root,"cloud-queue-api");Directory.CreateDirectory(fixture);var store=new RelayStore(Path.Combine(fixture,"relay"));
            Refused(()=>CloudCommands.ExecuteQueue(store,Obj(new{operation="cloud-queue-list"}),null));
            Refused(()=>CloudCommands.Execute(store,Obj(new{operation="cloud-queue-list"}),CancellationToken.None).GetAwaiter().GetResult());
            string session=new string('a',64);store.WriteRecord("session-"+session+".dpapi",new RelaySession{Token=session,Thread="fictional-source",Home=Path.Combine(fixture,"different-home"),Account="fixture",Channel="source",Expires=DateTime.UtcNow.AddHours(1).ToString("o")});
            Refused(()=>CloudCommands.Execute(store,Obj(new{operation="cloud-queue-list",session=session}),CancellationToken.None).GetAwaiter().GetResult());
            var mcp=new RelayMcp(store,fixture);mcp.Handle(Obj(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2025-11-25"}}),CancellationToken.None).GetAwaiter().GetResult();
            var reply=mcp.Handle(Obj(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="list_cloud_queue",arguments=new{session=session}}}),CancellationToken.None).GetAwaiter().GetResult();
            Assert(Object.Equals(Json.Get(Json.Get(Obj(reply),"result"),"isError"),true),"MCP accepted other profile session");Assert(!Directory.EnumerateFiles(store.Root,"cloud-prepared-*.dpapi").Any(),"denied request created queue state");
            var rejected=mcp.Handle(Obj(new{jsonrpc="2.0",id=3,method="tools/call",@params=new{name="control_cloud_queue",arguments=new{session=session,action="reject",id="22222222222222222222222222222222",jobId="22222222222222222222222222222222",threadId=(string)null,turnId=(string)null}}}),CancellationToken.None).GetAwaiter().GetResult();Assert(Object.Equals(Json.Get(Json.Get(Obj(rejected),"result"),"isError"),true),"failed delivery released by another profile");
        });
    }
}
