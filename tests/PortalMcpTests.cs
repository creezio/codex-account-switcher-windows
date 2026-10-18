using System;
using System.Collections.Generic;
using System.Linq;
using Creezio.Switcher;

internal static class PortalMcpTests
{
    private static object Obj(string json){return Json.Read<object>(json);}
    private static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    private static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected rejection");}
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("portal MCP skill catalog omits local paths and dependency credentials",delegate{
            var raw=Obj("{\"data\":[{\"cwd\":\"C:/private\",\"skills\":[{\"name\":\"vendor:skill\",\"description\":\"Description\",\"path\":\"C:/private/SKILL.md\",\"scope\":\"user\",\"enabled\":true,\"dependencies\":{\"token\":\"SECRET_FIXTURE\"},\"interface\":{\"displayName\":\"My skill\"}}]}]}");
            var skills=PortalMcpCapabilities.SkillRows(raw);var json=Json.Write(skills);Assert(skills.Length==1&&!json.Contains("C:/private")&&!json.Contains("SECRET_FIXTURE")&&json.Contains("My skill"),"skill private context exported");
            Assert(Json.Str(Json.Get(Json.Read<object>(Json.Write(skills[0])),"id")).Length==64,"skill opaque ID missing");
        });
        check("portal MCP plugin catalog includes installed only without private sources",delegate{
            var rows=PortalMcpCapabilities.PluginRows(Obj("{\"marketplaces\":[{\"path\":\"C:/private\",\"plugins\":[{\"id\":\"plugin@market\",\"name\":\"Plugin\",\"installed\":true,\"enabled\":false,\"source\":{\"url\":\"https://secret.example\"}},{\"id\":\"not-installed\",\"installed\":false}]}]}"));
            var json=Json.Write(rows);Assert(rows.Length==1&&!json.Contains("private")&&!json.Contains("secret.example")&&!json.Contains("not-installed"),"plugin source or uninstalled plugin exported");
        });
        check("portal MCP tool UI metadata is explicit and credentials are stripped",delegate{
            var tool=PortalMcpCapabilities.DescribeTool("codex_apps",Obj("{\"name\":\"fixture.show\",\"title\":\"Widget\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"default\":\"SECRET_FIXTURE\"}}},\"annotations\":{\"readOnlyHint\":true},\"_meta\":{\"connector_id\":\"connector\",\"connector_name\":\"Fixture\",\"link_id\":\"SECRET_FIXTURE\",\"Authorization\":\"SECRET_FIXTURE\",\"openai/outputTemplate\":\"ui://fixture/app.html\"}}"));
            var wire=Json.Read<object>(Json.Write(tool));Assert(Object.Equals(Json.Get(Json.Get(wire,"widget"),"available"),true)&&Json.Str(Json.Get(Json.Get(wire,"widget"),"protocol"))=="openai"&&!Json.Write(tool).Contains("SECRET_FIXTURE"),"widget contract or credential scrub failed");
        });
        check("portal MCP native internal widgets are not presented as external HTML",delegate{
            var tool=Json.Read<object>(Json.Write(PortalMcpCapabilities.DescribeTool("codex_apps",Obj("{\"name\":\"sites.creator\",\"_meta\":{\"ui\":{\"resourceUri\":\"internal://site-creator\"}}}"))));
            Assert(Object.Equals(Json.Get(Json.Get(tool,"widget"),"available"),false)&&Json.Str(Json.Get(Json.Get(tool,"widget"),"reason")).Contains("Codex"),"internal page exported");
        });
        check("portal MCP standard resource linkage remains separate from app visibility",delegate{
            var tool=Json.Read<object>(Json.Write(PortalMcpCapabilities.DescribeTool("fixture",Obj("{\"name\":\"show\",\"_meta\":{\"ui\":{\"resourceUri\":\"ui://fixture/app\",\"visibility\":[\"app\"]}}}"))));
            Assert(Json.Str(Json.Get(Json.Get(tool,"widget"),"protocol"))=="mcp-app"&&Object.Equals(Json.Get(Json.Get(tool,"widget"),"appAccessible"),true),"standard MCP linkage lost");
        });
        check("portal MCP resource selection never substitutes another HTML URI",delegate{
            Reject(()=>PortalMcpCapabilities.WidgetResponse("fixture","mcp-app","ui://expected",Obj("{\"contents\":[{\"uri\":\"ui://wrong\",\"mimeType\":\"text/html;profile=mcp-app\",\"text\":\"<p>wrong</p>\"}]}")));
        });
        check("portal MCP HTML base64 and declared CSP are handled without raw meta",delegate{
            var content=Obj("{\"contents\":[{\"uri\":\"ui://fixture\",\"mimeType\":\"text/html;profile=mcp-app\",\"blob\":\""+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<p>Fixture</p>"))+"\",\"_meta\":{\"secret\":\"SECRET_FIXTURE\",\"ui\":{\"csp\":{\"connectDomains\":[\"https://api.example\"],\"resourceDomains\":[\"https://static.example\"]}}}}]}");
            var result=Json.Read<object>(Json.Write(PortalMcpCapabilities.WidgetResponse("fixture","mcp-app","ui://fixture",Json.Read<object>(Json.Write(content)))));
            Assert(Json.Str(Json.Get(result,"html"))=="<p>Fixture</p>"&&!Json.Write(result).Contains("SECRET_FIXTURE")&&Json.Write(Json.Get(result,"csp")).Contains("https://api.example"),"HTML resource decoding or CSP extraction failed");
        });
        check("portal MCP resource bound and binary validation reject oversized or malformed HTML",delegate{
            Reject(()=>PortalMcpCapabilities.WidgetResponse("fixture","mcp-app","ui://fixture",new Dictionary<string,object>{{"contents",new object[]{new Dictionary<string,object>{{"uri","ui://fixture"},{"mimeType","text/html"},{"text",new string('x',1024*1024+1)}}}}}));
            Reject(()=>PortalMcpCapabilities.WidgetResponse("fixture","mcp-app","ui://fixture",Obj("{\"contents\":[{\"uri\":\"ui://fixture\",\"mimeType\":\"text/html\",\"blob\":\"invalid!\"}]}")));
        });
        check("portal MCP CSP cannot grant wildcard insecure credentialed or file origins",delegate{
            var allowed=PortalMcpCapabilities.Domains(new[]{"*","http://insecure.example","file:///private","https://user:pass@api.example","https://api.example/path","https://api.example?token=x","https://api.example","wss://stream.example","https://api.example"});
            Assert(allowed.Length==2&&allowed.Contains("https://api.example")&&allowed.Contains("wss://stream.example"),"unsafe CSP origin admitted");
        });
        check("portal MCP write hidden and credential callbacks require native workflow",delegate{
            Reject(()=>PortalMcpCapabilities.RequireReadOnly("fixture.save",false,true));Reject(()=>PortalMcpCapabilities.RequireReadOnly("fixture.hidden",true,false));Reject(()=>PortalMcpCapabilities.RequireReadOnly("fixture.get_environment_variables",true,true));Reject(()=>PortalMcpCapabilities.RequireReadOnly("fixture.access_token",true,true));PortalMcpCapabilities.RequireReadOnly("fixture.read_order",true,true);
        });
        check("portal MCP callback response strips connector authentication metadata",delegate{
            var result=PortalMcpCapabilities.PublicResult(Obj("{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"Fixture\"}],\"structuredContent\":{\"state\":\"visible\"},\"_meta\":{\"accessToken\":\"SECRET_FIXTURE\"},\"isError\":false}}"));
            Assert(!Json.Write(result).Contains("SECRET_FIXTURE")&&Json.Write(result).Contains("visible"),"raw authentication metadata exported");
        });
    }
}
