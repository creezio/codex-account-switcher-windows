using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;
internal static class SitesArchiveTests
{
    static int count;
    static void Assert(bool ok,string text){if(!ok)throw new Exception(text);}
    static void Check(string name,Action test){test();count++;Console.WriteLine("PASS "+name);}
    sealed class Fake:IToolTunnelConnection,IToolTunnelArchiveConnection {
        internal TunnelTool Tool;internal int Uploads,Saves;internal Action DuringUpload;internal bool Locked;
        public Task<TunnelTool[]> Inventory(CancellationToken t){return Task.FromResult(new[]{Tool});}
        public Task<object> Invoke(string s,string n,object a,CancellationToken t){Saves++;return Task.FromResult((object)new{isError=false,structuredContent=new{id="saved-verified"}});}
        public Task<object> InvokeArchive(string s,string n,object a,Action validate,CancellationToken t){Uploads++;try{using(var ignored=File.OpenWrite(Json.Str(Json.Get(a,"archive")))){}}catch(IOException){Locked=true;}if(DuringUpload!=null)DuringUpload();validate();return Invoke(s,n,a,t);}
        public void Dispose(){}
    }
    sealed class Fixture {
        internal RelayStore Store;internal RelaySession Session;internal ToolTunnel Tunnel;internal TunnelGrant Grant;internal Fake Connection;internal string Archive,Context;
        internal Fixture(string root){
            string folder=Path.Combine(root,"case-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);Store=new RelayStore(Path.Combine(folder,"relay"));
            string thread="01a148d5-cd63-7651-b6a6-b152793a4e2e",home=Path.Combine(folder,"source");
            Session=new RelaySession{Home=home,Thread=thread,Account="source",Channel="source"};string sessionFolder=Path.Combine(home,"sessions","2026","10","17");Directory.CreateDirectory(sessionFolder);Context=Path.Combine(sessionFolder,"rollout-"+thread+".jsonl");SetPermission(true);
            Archive=Path.Combine(folder,"site.tar.gz");File.WriteAllBytes(Archive,new byte[]{0x1f,0x8b,1,2,3});
            Connection=new Fake{Tool=new TunnelTool{Server="codex_apps",Name="sites.save_site_version",Description="Save",ReadOnly=false,Schema=Json.Read<object>("{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"commit_sha\":{\"type\":\"string\"},\"archive\":{\"type\":\"string\",\"description\":\"omit it only when local packaging cannot complete and remote build fallback is required. This parameter expects an absolute local file path.\"}},\"required\":[\"project_id\",\"commit_sha\"]}")}};
            var owner=new TunnelOwner{Id="owner",Home=Path.Combine(folder,"owner"),Account="owner-key"};Tunnel=new ToolTunnel(Store,id=>owner,o=>Connection,s=>{});
            Grant=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Name="Sites archive test",Instance="owner",Enabled=true,ResourceField="project_id",ResourceValue="site",Sources=new Dictionary<string,string>{{home,"source"}},Tools=new List<TunnelTool>{Connection.Tool}};Tunnel.SaveGrant(Grant);
        }
        internal void SetPermission(bool full){File.WriteAllText(Context,Json.Write(new{type="turn_context",payload=new{sandbox_policy=new{type=full?"danger-full-access":"workspace-write"},approval_policy=full?"never":"on-request"}})+"\n");}
        internal TunnelCall Call(string id=null,string path=null){return Tunnel.Invoke(Session,id??Guid.NewGuid().ToString("N"),Grant.Id,"codex_apps","sites.save_site_version",Json.Read<object>(Json.Write(new{project_id="site",commit_sha=new string('a',40),archive=path??Archive})),CancellationToken.None).GetAwaiter().GetResult();}
    }
    internal static void RunAll(Action<string,Action> check,string root){
        check("Sites archive locks file, records hash and invokes exactly once",()=>{var f=new Fixture(root);string id=Guid.NewGuid().ToString("N");var call=f.Call(id);Assert(call.State=="completed"&&call.ArchiveHash.Length==64&&call.ArchiveBytes==5&&f.Connection.Locked&&f.Connection.Uploads==1&&f.Connection.Saves==1,"archive not safely dispatched");f.Call(id);Assert(f.Connection.Uploads==1&&f.Connection.Saves==1,"archive duplicated");});
        check("Sites archive requires source full access",()=>{var f=new Fixture(root);f.SetPermission(false);Assert(f.Call().State=="blocked"&&f.Connection.Uploads==0,"read bypass");});
        check("Sites archive rejects nonexistent local file",()=>{var f=new Fixture(root);Assert(f.Call(null,Path.Combine(root,"missing.tar.gz")).State=="blocked"&&f.Connection.Uploads==0,"missing upload");});
        check("Sites archive rejects UNC before reading a network file",()=>{var f=new Fixture(root);Assert(f.Call(null,@"\\fixture.invalid\share\site.tar.gz").State=="blocked"&&f.Connection.Uploads==0,"network archive accepted");});
        check("Sites archive rejects non-gzip file",()=>{var f=new Fixture(root);File.WriteAllText(f.Archive,"not an archive");Assert(f.Call().State=="blocked"&&f.Connection.Uploads==0,"invalid upload");});
        check("Sites archive revocation during upload prevents saving",()=>{var f=new Fixture(root);f.Connection.DuringUpload=()=>f.Tunnel.Disable(f.Grant.Id);var c=f.Call();Assert(c.State=="uncertain"&&f.Connection.Uploads==1&&f.Connection.Saves==0,"revoked write");});
        check("Sites archive permission change during upload prevents saving",()=>{var f=new Fixture(root);f.Connection.DuringUpload=()=>f.SetPermission(false);var c=f.Call();Assert(c.State=="uncertain"&&f.Connection.Saves==0,"permission bypass");});
    }
    static int Main(string[] args){try{
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);ToolTunnelTests.RunAll(Check,root);RunAll(Check,root);
        Console.WriteLine(count+" tests passed.");return 0;
    }catch(Exception e){Console.WriteLine(e.GetType().Name+": "+e.Message);return 1;}}
}
