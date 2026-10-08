using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class ToolTunnelTests
{
    private static void Assert(bool value,string error){if(!value)throw new Exception(error);}
    private static void Refused(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected refusal");}
    private sealed class Fake : IToolTunnelConnection
    {
        internal TunnelTool Tool=new TunnelTool{Server="codex_apps",Name="chatgpt_space.edit_page",Group="Pages",Description="Edit a page",Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"page_id\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}}}"),ReadOnly=false};
        internal int Calls;
        internal bool Throw,ToolError,Disposed;
        internal Action InventoryAction,CallAction;
        internal object Result;
        public Task<TunnelTool[]> Inventory(CancellationToken token){if(InventoryAction!=null)InventoryAction();return Task.FromResult(new[]{Tool});}
        public Task<object> Invoke(string server,string name,object args,CancellationToken token){Calls++;if(CallAction!=null)CallAction();if(Throw)throw new IOException("connection lost");return Task.FromResult(Result??Json.Read<object>(Json.Write(new{isError=ToolError,content=new[]{new{type="text",text="private receipt"}},structuredContent=new{receipt="receipt-1"}})));}
        public void Dispose(){Disposed=true;}
    }
    private sealed class Fixture
    {
        internal readonly RelayStore Store;
        internal readonly Fake Fake=new Fake();
        internal readonly ToolTunnel Tunnel;
        internal readonly RelaySession Session;
        internal readonly TunnelGrant Grant;
        internal readonly TunnelOwner Owner;
        internal bool Online=true,Source=true;
        internal Fixture(string root)
        {
            string folder=Path.Combine(root,"tunnel-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);Store=new RelayStore(folder);
            Owner=new TunnelOwner{Id="owner",Name="Owner",Home=Path.Combine(folder,"owner"),Account="account-b"};
            Session=new RelaySession{Home=Path.Combine(folder,"source"),Account="account-a",Thread="chat-a",Channel="source"};
            Tunnel=new ToolTunnel(Store,id=>{if(!Online)throw new InvalidOperationException("offline");return Owner;},o=>Fake,s=>{if(!Source)throw new InvalidOperationException("source changed");});
            Grant=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Instance=Owner.Id,Name="Pages via Owner",Enabled=true,ResourceField="page_id",ResourceValue="page-1",Tools=new List<TunnelTool>{Fake.Tool},Sources=new Dictionary<string,string>{{Session.Home,Session.Account}}};Tunnel.SaveGrant(Grant);
        }
        internal TunnelCall Call(string id=null,object args=null){return Tunnel.Invoke(Session,id??Guid.NewGuid().ToString("N"),Grant.Id,Fake.Tool.Server,Fake.Tool.Name,args??Json.Read<object>("{\"page_id\":\"page-1\",\"content\":\"hello\"}"),CancellationToken.None).GetAwaiter().GetResult();}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("tunnel invokes tool directly and returns encrypted original result",delegate{var f=new Fixture(root);var c=f.Call();Assert(c.State=="completed"&&f.Fake.Calls==1&&f.Fake.Disposed,"call failed");Assert(f.Tunnel.Read(f.Session,c.Id).Result!=null,"result missing");Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,"tool-call-"+c.Id+".dpapi"))).Contains("private receipt"),"plaintext result");});
        check("tunnel replay of a completed write never invokes twice",delegate{var f=new Fixture(root);string id=Guid.NewGuid().ToString("N");f.Call(id);f.Online=false;Assert(f.Call(id).State=="completed"&&f.Fake.Calls==1,"duplicate write");});
        check("tunnel canonical fingerprints tolerate reordered arguments",delegate{var f=new Fixture(root);string id=Guid.NewGuid().ToString("N");f.Call(id);f.Call(id,Json.Read<object>("{\"content\":\"hello\",\"page_id\":\"page-1\"}"));Assert(f.Fake.Calls==1,"order changed identity");});
        check("tunnel idempotency key cannot be reused for another payload",delegate{var f=new Fixture(root);string id=Guid.NewGuid().ToString("N");f.Call(id);Refused(()=>f.Call(id,Json.Read<object>("{\"page_id\":\"page-1\",\"content\":\"different\"}")));});
        check("tunnel write timeout stays uncertain and is never resent",delegate{var f=new Fixture(root);f.Fake.Throw=true;var c=f.Call();Assert(c.State=="uncertain","false failure/success");f.Fake.Throw=false;f.Call(c.Id);Assert(f.Fake.Calls==1,"uncertain write repeated");});
        check("tunnel rejects a resource outside the configured scope",delegate{var f=new Fixture(root);Refused(()=>f.Call(null,Json.Read<object>("{\"page_id\":\"page-2\"}")));Assert(f.Fake.Calls==0,"escaped resource");});
        check("tunnel redacts credentials from structured and textual copies",delegate{bool sensitive=false;string payload="{\"structuredContent\":{\"id\":\"site-1\",\"credential\":{\"token\":\"do-not-store\"}},\"content\":[{\"type\":\"text\",\"text\":\"{\\\"token\\\":\\\"do-not-store\\\"}\"}]}";string redacted=Json.Write(ToolTunnel.Redact(Json.Read<object>(payload),ref sensitive));Assert(sensitive&&!redacted.Contains("do-not-store")&&redacted.Contains("site-1"),"secret persisted or resource lost");});
        check("tunnel recent history reads metadata without private result bodies",delegate{var f=new Fixture(root);var c=f.Call();var recent=f.Tunnel.Recent().Single();Assert(recent.Id==c.Id&&recent.HasResult&&recent.Result==null,"UI loads private payload");});
        check("tunnel returns credentials once without persisting them",delegate{var f=new Fixture(root);f.Fake.Result=Json.Read<object>("{\"structuredContent\":{\"token\":\"one-time-value\",\"id\":\"resource\"}}");var c=f.Call();Assert(Json.Write(ToolTunnel.Reply(c)).Contains("one-time-value"),"first response lost credential");Assert(!Json.Write(f.Tunnel.Read(f.Session,c.Id)).Contains("one-time-value"),"credential retained on disk");Assert(!Json.Write(ToolTunnel.Reply(f.Call(c.Id))).Contains("one-time-value")&&f.Fake.Calls==1,"credential replayed");});
        check("tunnel blocks native file uploads before dispatch",delegate{var f=new Fixture(root);f.Fake.Tool.Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"page_id\":{\"type\":\"string\"},\"archive\":{\"type\":\"string\",\"description\":\"This parameter expects an absolute local file path.\"}}}");f.Tunnel.SaveGrant(f.Grant);var c=f.Call(null,Json.Read<object>("{\"page_id\":\"page-1\",\"archive\":\"C:/test.zip\"}"));Assert(c.State=="blocked"&&f.Fake.Calls==0,"unsupported upload sent");});
        check("tunnel rejects a new Sites version even with archive omitted",delegate{var f=new Fixture(root);f.Fake.Tool.Name="sites.save_site_version";f.Tunnel.SaveGrant(f.Grant);Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"source-only silent fallback");});
        check("tunnel rejects absent nested numeric and null resource identifiers",delegate{var f=new Fixture(root);foreach(string s in new[]{"{}","{\"page_id\":null}","{\"page_id\":1}","{\"nested\":{\"page_id\":\"page-1\"}}","[]"})Refused(()=>f.Call(null,Json.Read<object>(s)));});
        check("tunnel disabled share refuses both calls and result access",delegate{var f=new Fixture(root);var c=f.Call();f.Tunnel.Disable(f.Grant.Id);Refused(()=>f.Call());Refused(()=>f.Tunnel.Read(f.Session,c.Id));});
        check("tunnel client account drift does not inherit an old grant",delegate{var f=new Fixture(root);f.Session.Account="another-account";Refused(()=>f.Call());});
        check("tunnel source must remain verified even for replay",delegate{var f=new Fixture(root);var c=f.Call();f.Source=false;Refused(()=>f.Call(c.Id));});
        check("tunnel owner account drift blocks before invocation",delegate{var f=new Fixture(root);f.Owner.Account="changed-owner";Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"owner drift ignored");});
        check("tunnel offline owner does not launch a tool",delegate{var f=new Fixture(root);f.Online=false;Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"offline invoked");});
        check("tunnel schema drift requires user to update the share",delegate{var f=new Fixture(root);f.Fake.Tool.Description="New behavior";Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"changed contract executed");});
        check("tunnel compares saved contract rather than serializer-specific cached signature",delegate{var f=new Fixture(root);var saved=f.Tunnel.Grants();saved[0].Tools[0].Signature="hash-from-another-runtime";f.Store.WriteRecord("tool-grants.dpapi",saved);Assert(f.Call().State=="completed"&&f.Fake.Calls==1,"identical contract falsely blocked");});
        check("tool descriptions use the current instance name after rename",delegate{var f=new Fixture(Path.Combine(root,"renamed-contract"));f.Owner.Id=Guid.NewGuid().ToString("N");f.Grant.Instance=f.Owner.Id;f.Tunnel.SaveGrant(f.Grant);var vault=new Vault(Path.GetDirectoryName(f.Store.Root));var data=vault.Load();data.Instances.Add(new DesktopInstance{Id=f.Owner.Id,Name="Owner renamed"});vault.Save(data);var description=Json.Read<object>(Json.Write(f.Tunnel.Describe(f.Session,f.Grant.Id,f.Fake.Tool.Server,f.Fake.Tool.Name)));Assert(Json.Str(Json.Get(description,"instanceName"))=="Owner renamed","stale owner name");});
        check("tunnel input schema drift remains blocked before sending",delegate{var f=new Fixture(root);Json.Obj(Json.Get(f.Fake.Tool.Schema,"properties"))["new_scope"]=Json.Read<object>("{\"type\":\"string\"}");Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"schema change accepted");});
        check("tunnel annotation and connector changes remain blocked",delegate{var f=new Fixture(root);f.Fake.Tool.Annotations=new Dictionary<string,object>{{"destructiveHint",true}};Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"changed risk accepted");f=new Fixture(root);f.Fake.Tool.ConnectorId="other-provider";Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"changed provider accepted");});
        check("tunnel revocation during discovery prevents dispatch",delegate{var f=new Fixture(root);f.Fake.InventoryAction=()=>f.Tunnel.Disable(f.Grant.Id);Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"revocation race");});
        check("tunnel result belongs to the exact originating chat",delegate{var f=new Fixture(root);var c=f.Call();f.Session.Thread="chat-b";Refused(()=>f.Tunnel.Read(f.Session,c.Id));Refused(()=>f.Call(c.Id));});
        check("tunnel provider errors are not reported as completed",delegate{var f=new Fixture(root);f.Fake.ToolError=true;Assert(f.Call().State=="tool_error","provider error ignored");});
        check("tunnel does not silently grant native interaction requests",delegate{var f=new Fixture(root);f.Fake.CallAction=()=>{throw new TunnelInteractionException();};var c=f.Call();Assert(c.State=="uncertain"&&c.Error.Contains("Aucune approbation"),"approval swallowed");});
        check("tunnel stored executing call after crash is not retried",delegate{var f=new Fixture(root);var c=f.Call();c.State="executing";c.Result=null;f.Store.WriteRecord("tool-call-"+c.Id+".dpapi",c);Assert(f.Call(c.Id).State=="executing"&&f.Fake.Calls==1,"crash repeated");});
        check("tunnel result pagination rejects invalid bounds",delegate{var f=new Fixture(root);var c=f.Call();Refused(()=>f.Tunnel.Result(f.Session,c.Id,-1,20));Refused(()=>f.Tunnel.Result(f.Session,c.Id,0,24001));var value=Json.Read<object>(Json.Write(f.Tunnel.Result(f.Session,c.Id,0,5)));Assert(Object.Equals(Json.Get(value,"hasMore"),true)&&Json.Str(Json.Get(value,"json")).Length==5,"pagination");});
        check("tunnel scope cannot include tools without the scope argument",delegate{var f=new Fixture(root);f.Grant.Tools.Add(new TunnelTool{Server="codex_apps",Name="list_pages",Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{}}")});Refused(()=>f.Tunnel.SaveGrant(f.Grant));});
        check("tunnel cannot export itself or desktop shell tools",delegate{foreach(string name in new[]{"creezio-relay","codex_app","cua_repl","node_repl"})Assert(!ToolTunnel.AllowedServer(name),"recursive export");Assert(ToolTunnel.AllowedServer("codex_apps"),"apps missing");});
        check("tunnel detects owner drift after response without leaking result",delegate{var f=new Fixture(root);f.Fake.CallAction=()=>f.Owner.Account="different";var c=f.Call();Assert(c.State=="uncertain"&&c.Result==null,"drift leaked result");});
        check("tunnel cannot send an operation missing from the grant",delegate{var f=new Fixture(root);Refused(()=>f.Tunnel.Invoke(f.Session,Guid.NewGuid().ToString("N"),f.Grant.Id,"codex_apps","chatgpt_space.delete_page",new Dictionary<string,object>{{"page_id","page-1"}},CancellationToken.None).GetAwaiter().GetResult());});
        check("tunnel owner serialization blocks concurrent writes",delegate{var f=new Fixture(root);using(f.Store.Lease("tool-owner-"+ToolTunnel.Hash(f.Owner.Account).Substring(0,32))){Assert(f.Call().State=="blocked"&&f.Fake.Calls==0,"parallel owner write");}});
        check("MCP exposes direct tool schemas with stable request id",delegate{var tools=RelayMcp.Tools().Select(t=>Json.Read<object>(Json.Write(t))).ToArray();var tool=tools.Single(t=>Json.Str(Json.Get(t,"name"))=="call_shared_tool");Assert(RelayEngine.Rows(Json.Get(Json.Get(tool,"inputSchema"),"required")).Select(Json.Str).Contains("id"),"missing id");Assert(tools.Any(t=>Json.Str(Json.Get(t,"name"))=="read_shared_tool_result"),"missing result reader");});
    }
}
