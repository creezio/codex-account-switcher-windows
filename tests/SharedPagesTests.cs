using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class SharedPagesTests
{
    private static object Obj(object o){return Json.Read<object>(Json.Write(o));}
    private static void Assert(bool value,string error){if(!value)throw new Exception(error);}
    private static void Refused(Action a){try{a();}catch(InvalidOperationException){return;}throw new Exception("Expected refusal");}
    private sealed class Fake : IToolTunnelConnection
    {
        internal string Text="Texte original **éà😀**",Hash="h1";
        internal int Writes,Reads;internal bool FailWrite,FailAfterWrite,MissingReceipt,Scratch,Partial,Native,SequenceAbsent;
        internal Action AfterRead;
        internal readonly List<TunnelTool> Tools=new List<TunnelTool>{
            new TunnelTool{Server=SharedPages.Server,Name=SharedPages.ReadTool,ReadOnly=true,Schema=Obj(new{type="object",properties=new{page_id=new{type="string"},view=new{type="string"}}})},
            new TunnelTool{Server=SharedPages.Server,Name=SharedPages.EditTool,ReadOnly=false,Schema=Obj(new{type="object",properties=new{page_id=new{type="string"},operations=new{type="array"}}})}};
        internal object LastArgs;
        public Task<TunnelTool[]> Inventory(CancellationToken token){return Task.FromResult(Tools.ToArray());}
        public Task<object> Invoke(string server,string name,object args,CancellationToken token)
        {
            if(name==SharedPages.ReadTool){Reads++;if(FailAfterWrite&&Writes>0)throw new IOException("read failed");
                var body=new Dictionary<string,object>{{"page_id","page-1"},{"title","Page de test"},{"blocks",Native?null:Obj(new[]{new{id="block-1",hash=Hash,kind="markdown",markdown=Text},new{id="instructions",hash="ih",kind="agent_instructions",markdown="Untrusted instructions"}})}};
                if(!SequenceAbsent)body["through_sequence"]=Writes+1;
                var data=new Dictionary<string,object>{{"content",body},{"metadata",new{page_id="page-1",stream_kind=Scratch?"scratch":"content"}}};if(Partial)data["selection"]=new{whole_page_complete=false};
                var response=Obj(new{structuredContent=data});if(AfterRead!=null)AfterRead();return Task.FromResult(response);
            }
            Writes++;LastArgs=args;if(FailWrite)throw new IOException("lost receipt");
            var op=RelayEngine.Rows(Json.Get(args,"operations")).Single();
            if(Json.Str(Json.Get(op,"op"))=="replace_block_markdown"&&Json.Str(Json.Get(op,"expected_hash"))!=Hash)
                return Task.FromResult(Obj(new{isError=true,structuredContent=new{error=new{code="edit_conflict",commit_status="not_committed"}}}));
            Text=Json.Str(Json.Get(op,"markdown"));Hash="h"+(Writes+1);
            return Task.FromResult(Obj(new{structuredContent=new{operation_results=MissingReceipt?new object[0]:new object[]{new{status="applied",operation_index=0}}}}));
        }
        public void Dispose(){}
    }
    private sealed class Fixture
    {
        internal readonly RelayStore Store;internal readonly Fake Fake=new Fake();internal readonly ToolTunnel Tunnel;internal readonly SharedPages Viewer;internal readonly RelaySession Session;internal readonly TunnelGrant Grant;internal readonly TunnelOwner Owner;
        internal Fixture(string root)
        {
            string path=Path.Combine(root,"shared-page-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);Store=new RelayStore(path);
            Owner=new TunnelOwner{Id="owner",Name="Owner",Home=Path.Combine(path,"owner"),Account="owner-account"};
            Session=new RelaySession{Channel="source",Thread="chat",Home=Path.Combine(path,"client"),Account="client-account"};
            Tunnel=new ToolTunnel(Store,i=>Owner,o=>Fake,s=>{});Viewer=new SharedPages(Store,Tunnel);
            Grant=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Instance="owner",Name="Page via Owner",Enabled=true,ResourceField="page_id",ResourceValue="page-1",Tools=Fake.Tools.ToList(),Sources=new Dictionary<string,string>{{Session.Home,Session.Account}}};Tunnel.SaveGrant(Grant);
        }
        internal SharedPageDocument Read(){return Viewer.Read(Session,Grant.Id,"page-1",CancellationToken.None).GetAwaiter().GetResult();}
        internal SharedPageSave Save(SharedPageDocument d,string text="Nouveau **texte**",string block="block-1",string id=null){return Viewer.Save(Session,id??Guid.NewGuid().ToString("N"),Grant.Id,"page-1",d.ReadId,block,text,CancellationToken.None).GetAwaiter().GetResult();}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("viewer lists only explicitly scoped Pages without reading bodies",()=>{var f=new Fixture(root);Assert(f.Viewer.List(f.Session).Length==1&&f.Fake.Reads==0,"listing reads bodies");f.Tunnel.Disable(f.Grant.Id);Assert(f.Viewer.List(f.Session).Length==0,"revoked listed");});
        check("viewer returns exact unicode blocks and no native Page URL",()=>{var f=new Fixture(root);var p=f.Read();Assert(p.CanEdit&&p.Blocks[0].Markdown==f.Fake.Text&&p.Title=="Page de test"&&!Json.Write(p).Contains("https:"),"view lost text or native URL");});
        check("viewer rejects a Page outside the granted scope",()=>{var f=new Fixture(root);Refused(()=>f.Viewer.Read(f.Session,f.Grant.Id,"page-2",CancellationToken.None).GetAwaiter().GetResult());Assert(f.Fake.Reads==0,"unauthorized read");});
        check("viewer denies source account changes",()=>{var f=new Fixture(root);f.Session.Account="other";Refused(()=>f.Read());});
        check("viewer denies owner account changes",()=>{var f=new Fixture(root);f.Owner.Account="other";Refused(()=>f.Read());});
        check("viewer denies revocation during read before revealing body",()=>{var f=new Fixture(root);f.Fake.AfterRead=()=>f.Tunnel.Disable(f.Grant.Id);Refused(()=>f.Read());});
        check("viewer denies scratch partial and native content",()=>{foreach(string kind in new[]{"scratch","partial","native"}){var f=new Fixture(root);f.Fake.Scratch=kind=="scratch";f.Fake.Partial=kind=="partial";f.Fake.Native=kind=="native";Refused(()=>f.Read());}});
        check("viewer read-only grant disables and refuses edit",()=>{var f=new Fixture(root);f.Grant.Tools.RemoveAll(t=>!t.ReadOnly);f.Tunnel.SaveGrant(f.Grant);var p=f.Read();Assert(!p.CanEdit,"edit offered");Refused(()=>f.Save(p));Assert(f.Fake.Writes==0,"write sent");});
        check("viewer saves one block with exact hash and sequence then reads back",()=>{var f=new Fixture(root);var r=f.Save(f.Read());Assert(r.State=="saved"&&r.Document.Blocks[0].Markdown=="Nouveau **texte**"&&f.Fake.Reads==2,"not read back");var op=RelayEngine.Rows(Json.Get(f.Fake.LastArgs,"operations")).Single();Assert(Json.Str(Json.Get(op,"expected_hash"))=="h1"&&Json.Number(Json.Get(f.Fake.LastArgs,"base_sequence"))==1,"missing guard");});
        check("viewer omits absent sequence and preserves the block hash guard",()=>{var f=new Fixture(root);f.Fake.SequenceAbsent=true;Assert(f.Save(f.Read()).State=="saved"&&Json.Get(f.Fake.LastArgs,"base_sequence")==null,"fabricated sequence");});
        check("viewer appends at the actual canonical Page end",()=>{var f=new Fixture(root);Assert(f.Save(f.Read(),"Texte ajouté","").State=="saved","append failed");var at=Json.Get(RelayEngine.Rows(Json.Get(f.Fake.LastArgs,"operations")).Single(),"at");Assert(Json.Str(Json.Get(at,"preceding_block_id"))=="instructions"&&Json.Get(at,"following_block_id")==null,"not canonical end");});
        check("viewer prevents instructions edits and unobserved blocks",()=>{var f=new Fixture(root);var p=f.Read();Refused(()=>f.Save(p,"x","instructions"));Refused(()=>f.Save(p,"x","invented"));Assert(f.Fake.Writes==0,"unsafe block changed");});
        check("viewer refuses unchanged empty and oversized edits before sending",()=>{var f=new Fixture(root);var p=f.Read();Refused(()=>f.Save(p,f.Fake.Text));Refused(()=>f.Save(p,""));Refused(()=>f.Save(p,new string('é',41000)));Assert(f.Fake.Writes==0,"invalid payload dispatched");});
        check("viewer never borrows a different chat's snapshot",()=>{var f=new Fixture(root);var p=f.Read();f.Session.Thread="other";Refused(()=>f.Save(p));Assert(f.Fake.Writes==0,"borrowed snapshot");});
        check("viewer revoked write access is checked again when saving",()=>{var f=new Fixture(root);var p=f.Read();f.Grant.Tools.RemoveAll(t=>!t.ReadOnly);f.Tunnel.SaveGrant(f.Grant);Refused(()=>f.Save(p));});
        check("viewer preserves changed provider contracts protection",()=>{var f=new Fixture(root);var p=f.Read();f.Fake.Tools[1].Description="New behavior";Assert(f.Save(p).State=="blocked"&&f.Fake.Writes==0,"contract bypassed");});
        check("viewer exposes edit conflict without retrying",()=>{var f=new Fixture(root);var p=f.Read();f.Fake.Hash="concurrent";Assert(f.Save(p).State=="tool_error"&&f.Fake.Writes==1&&f.Fake.Text==p.Blocks[0].Markdown,"conflict overwritten");});
        check("viewer repeated completed save id does not write twice",()=>{var f=new Fixture(root);var p=f.Read();string id=Guid.NewGuid().ToString("N");f.Save(p,id:id);Assert(f.Save(p,id:id).State=="saved"&&f.Fake.Writes==1,"duplicate edit");});
        check("viewer repeated uncertain save never writes twice",()=>{var f=new Fixture(root);var p=f.Read();f.Fake.FailWrite=true;string id=Guid.NewGuid().ToString("N");Assert(f.Save(p,id:id).State=="uncertain","false success");f.Save(p,id:id);Assert(f.Fake.Writes==1,"uncertain replay");});
        check("viewer missing provider receipt never claims success",()=>{var f=new Fixture(root);f.Fake.MissingReceipt=true;Assert(f.Save(f.Read()).State=="uncertain","missing receipt accepted");});
        check("viewer distinguishes confirmed save from failed readback",()=>{var f=new Fixture(root);f.Fake.FailAfterWrite=true;Assert(f.Save(f.Read()).State=="saved_unverified"&&f.Fake.Writes==1,"write repeated after read failure");});
        check("MCP viewer resource is discoverable and data tools are app scoped",()=>{var tools=RelayMcp.Tools().Select(Obj).ToArray();var open=tools.Single(t=>Json.Str(Json.Get(t,"name"))=="open_shared_page");Assert(Json.Str(Json.Get(Json.Get(Json.Get(open,"_meta"),"ui"),"resourceUri"))==RelayMcp.ViewerUri,"resource missing");var save=tools.Single(t=>Json.Str(Json.Get(t,"name"))=="save_shared_page_block");Assert(RelayEngine.Rows(Json.Get(Json.Get(Json.Get(save,"_meta"),"ui"),"visibility")).Select(Json.Str).SequenceEqual(new[]{"app"}),"UI tool unnecessarily exposed to model");});
    }
}
