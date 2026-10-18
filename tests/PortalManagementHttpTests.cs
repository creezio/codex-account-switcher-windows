using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Creezio.Switcher;

internal static class PortalManagementHttpTests
{
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Reject(int status,Action action){try{action();}catch(WebhookException error){Assert(error.StatusCode==status,"Wrong status "+error.StatusCode);return;}throw new Exception("Expected HTTP rejection "+status);}
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        string route=new string('a',32),instance=new string('b',32),chat="01a14b48-5f60-74d1-8d48-bb28672d3afd";
        string prefix="/v1/admin/"+route+"/instances/"+instance;
        check("management HTTP route preserves root authorization and accepts primary instance",()=>{
            foreach(string suffix in new[]{"/skills","/skills/"+new string('c',64),"/skills/import","/skills/deploy","/chats/"+chat+"/management","/chats/"+chat+"/goal","/chats/"+chat+"/schedules","/chats/"+chat+"/schedules/"+new string('d',32)+"/delete"}){
                var parsed=PortalAdministration.Path(prefix+suffix);Assert(parsed.Success&&parsed.Groups[1].Value==route,"Missing route pin");Assert(PortalManagementHttp.Path(prefix.Replace(instance,"local")+suffix).Success,"Primary instance rejected");
            }
            foreach(string suffix in new[]{"/skills/../goal","/skills/import/extra","/chats/"+chat+"/goal/"+instance+"/delete","/skills?target=other"})Assert(!PortalAdministration.Path(prefix+suffix).Success,"Unbounded management path");
        });
        check("management HTTP body allocation limits remain specific to operation",()=>{
            Assert(PortalAdministration.MaximumBodyFor("/v1/admin/"+route+"/instance-enrollments")==4096,"Enrollment cap broadened");
            Assert(PortalAdministration.MaximumBodyFor(prefix+"/skills")==128*1024,"Skill document cap");
            Assert(PortalAdministration.MaximumBodyFor(prefix+"/skills/import")==3*1024*1024,"Archive cap");
            Assert(PortalAdministration.MaximumBodyFor(prefix+"/chats/"+chat+"/goal")==32*1024,"Goal cap");
            Assert(PortalAdministration.MaximumBodyFor(prefix+"/chats/"+chat+"/schedules")==128*1024,"Schedule cap");
        });
        check("management HTTP strict JSON preserves int64 budget and weekday arrays",()=>{
            var fields=PortalManagementHttp.Fields.Parse("{\"tokenBudget\":9007199254740993,\"daysOfWeek\":[\"MO\",\"FR\"]}",4096,"tokenBudget","daysOfWeek");
            Assert((long)fields["tokenBudget"]==9007199254740993L,"Budget precision lost");Assert(((string[])fields["daysOfWeek"])[1]=="FR","Weekdays lost");
        });
        check("management HTTP rejects decoded duplicate JSON fields and nested policy",()=>{
            foreach(string body in new[]{"{\"name\":\"one\",\"na\\u006de\":\"two\"}","{\"name\":{}}","{\"name\":[{}]}","{\"force\":true}","{\"name\":\"x\",}","null","{} trailing","{\"name\":truefalse}"})Reject(400,()=>PortalManagementHttp.Fields.Parse(body,4096,"name"));
        });
        check("management HTTP rejects floating exponent malformed and overflowing numbers",()=>{
            foreach(string number in new[]{"01","-01","1.0","1e2","+1","9223372036854775808","-9223372036854775809","NaN"})Reject(400,()=>PortalManagementHttp.Fields.Parse("{\"revision\":"+number+"}",4096,"revision"));
        });
        check("management HTTP does not coerce arbitrary skill document types",()=>{
            foreach(string body in new[]{"{\"name\":42}","{\"document\":true}","{\"expectedRevision\":[\"x\"]}"})Reject(400,()=>PortalManagementHttp.Fields.Read<PortalSkillSaveRequest>(body,4096,"name","document","expectedRevision"));
        });
        check("management HTTP validates signed identity before accepting extended body",()=>{
            var fixture=new PortalAdministrationTests.Fixture(sandbox);var request=fixture.Request("instances/"+instance+"/skills","POST","{\"document\":\""+new string('x',8000)+"\"}");
            PortalAdministration.ValidateHeaders(fixture.Store,request);var identity=PortalAdministration.Authorize(fixture.Store,request);Assert(identity.Route==fixture.Route.Id,"Foreign management identity");
            request=fixture.Request("instances/"+instance+"/skills/import","POST","{}");request.Headers["Authorization"]="Bearer ordinary-chat-key";Reject(403,()=>PortalAdministration.ValidateHeaders(fixture.Store,request));
        });
        check("management HTTP authenticates archive headers before waiting for bytes",()=>{
            var fixture=new PortalAdministrationTests.Fixture(sandbox);string header="POST /v1/admin/"+fixture.Route.Id+"/instances/"+instance+"/skills/import HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 2800000\r\n\r\n";
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(header)))Reject(403,()=>WebhookHttp.ReadRequest(stream,request=>{throw new WebhookException(403,"Not authorized");}));
        });
        check("management HTTP rejects archive bodies beyond cap before allocation",()=>{
            string header="POST "+prefix+"/skills/import HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 3145729\r\n\r\n";
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(header)))Reject(413,()=>WebhookHttp.ReadRequest(stream));
        });
    }
}
