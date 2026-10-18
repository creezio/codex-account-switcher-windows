using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class PortalAttachmentTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(int code,Action action){try{action();}catch(WebhookException e){Assert(e.StatusCode==code,"Expected "+code+", got "+e.StatusCode);return;}throw new Exception("Expected rejection "+code);}
    private sealed class Fixture
    {
        internal RelayStore Store;internal WebhookRoute Route;internal PortalInstanceGrant Grant;internal DateTime Now=new DateTime(2026,10,17,12,0,0,DateTimeKind.Utc);
        internal Fixture(string root,string name){string folder=Path.Combine(root,"portal-attachment-tests",name+"-"+Guid.NewGuid().ToString("N").Substring(0,8)),home=Path.Combine(folder,"home"),workspace=Path.Combine(folder,"workspace");Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);Store=new RelayStore(Path.Combine(folder,"relay"));Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true});Route=Webhooks.SaveRoute(Store,new WebhookRoute{Name="Attachment fixture",Slug="attachment-fixture",InstanceId=new string('a',32),AccountKey="fixture-account",Home=home,Workspace=workspace,Enabled=true});Grant=new PortalInstanceGrant{InstanceId=Route.InstanceId,AccountKey=Route.AccountKey,Home=home,Workspace=workspace,Access="desktop"};}
        internal object Upload(string key,string name,byte[] bytes,string relative=null,string mime="text/plain"){return PortalAttachments.UploadCore(Store,Route,Grant,key,Body(name,bytes,relative,mime),()=>100L*1024*1024*1024,Now);}
        internal static string Body(string name,byte[] bytes,string relative=null,string mime="text/plain"){return "{\"name\":"+Json.Write(name)+",\"mimeType\":"+Json.Write(mime)+",\"contentBase64\":\""+Convert.ToBase64String(bytes)+"\""+(relative==null?"":",\"relativePath\":"+Json.Write(relative))+"}";}
        internal static string UploadedId(object receipt){return Json.Str(Json.Get(Json.Get(Json.Read<object>(Json.Write(receipt)),"attachment"),"id"));}
        internal string Add(string key="file",string name="note.txt",byte[] bytes=null){return UploadedId(Upload(key,name,bytes??Encoding.UTF8.GetBytes("fictional fixture only")));}
        internal PortalAttachmentRecord Record(string id){return Store.ReadRecord<PortalAttachmentRecord>(PortalAttachments.RecordName(id));}
        internal void Save(PortalAttachmentRecord r){Store.WriteRecord(PortalAttachments.RecordName(r.Id),r);}
        internal object Clean(Func<int,long,bool?> owner=null){return Json.Read<object>(Json.Write(PortalAttachments.CleanupCore(Store,Now.AddHours(25),owner??((pid,ticks)=>(bool?)false))));}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("portal attachment upload is private opaque scoped and not a client path",()=>{
            var f=new Fixture(root,"upload");var receipt=f.Upload("one","rapport.txt",Encoding.UTF8.GetBytes("fixture"),"dossier/rapport.txt");string id=Fixture.UploadedId(receipt);var r=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single();Assert(r.Name=="rapport.txt"&&r.RelativePath=="dossier/rapport.txt"&&r.Size==7&&File.ReadAllText(r.Path)=="fixture"&&r.Path.StartsWith(f.Store.Root,StringComparison.OrdinalIgnoreCase),"Wrong byte or metadata binding");Assert(!Json.Write(receipt).Contains(f.Store.Root)&&!Json.Write(receipt).Contains("fixture-account"),"Public receipt leaked internal binding");
        });
        check("portal attachment retry reconciles one immutable payload",()=>{
            var f=new Fixture(root,"idempotence");string id=f.Add();var again=f.Upload("file","note.txt",Encoding.UTF8.GetBytes("fictional fixture only"));Assert(Fixture.UploadedId(again)==id&&Object.Equals(Json.Get(Json.Read<object>(Json.Write(again)),"replayed"),true),"Retry duplicated payload");Reject(409,()=>f.Upload("file","note.txt",Encoding.UTF8.GetBytes("changed")));Assert(Directory.GetFiles(Path.Combine(f.Store.Root,"portal-attachments"),"blob.bin",SearchOption.AllDirectories).Length==1,"Retry wrote another blob");
        });
        check("portal attachment concurrent retries create one file and one record",()=>{
            var f=new Fixture(root,"concurrency");var ids=new System.Collections.Concurrent.ConcurrentBag<string>();Parallel.For(0,8,i=>ids.Add(f.Add()));Assert(ids.Distinct().Count()==1&&Directory.GetFiles(f.Store.Root,"portal-attachment-*.dpapi").Length==1,"Concurrent retry duplicated file");
        });
        check("portal attachment strict JSON rejects duplicate escaped unknown or nonstring fields",()=>{
            var f=new Fixture(root,"strict-json");foreach(string body in new[]{"null","[]","{}","{\"name\":\"a.txt\",\"mimeType\":\"text/plain\",\"contentBase64\":\"YQ==\",\"n\\u0061me\":\"b.txt\"}","{\"name\":\"a.txt\",\"mimeType\":\"text/plain\",\"contentBase64\":\"YQ==\",\"path\":\"C:/private\"}","{\"name\":\"a.txt\",\"mimeType\":\"text/plain\",\"contentBase64\":1}","{\"name\":\"a.txt\",\"mimeType\":\"text/plain\",\"contentBase64\":\"YQ==\"} trailing"})Reject(400,()=>PortalAttachments.UploadCore(f.Store,f.Route,f.Grant,"strict",body,()=>100L<<30,f.Now));Assert(Directory.GetFiles(f.Store.Root,"portal-attachment-*.dpapi").Length==0,"Invalid payload persisted data");
        });
        check("portal attachment Unicode names and escaped keys use canonical metadata",()=>{
            var f=new Fixture(root,"unicode");var receipt=f.Upload("unicode","résumé.txt",new byte[]{65},"équipe/résumé.txt");var r=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{Fixture.UploadedId(receipt)}).Single();Assert(r.RelativePath=="équipe/résumé.txt"&&r.Name=="résumé.txt","Unicode filename lost");
        });
        check("portal attachment rejects traversal ADS UNC reserved and dot names",()=>{
            var f=new Fixture(root,"names");foreach(string name in new[]{"..",".","../auth.json","C:\\secret.txt","a:stream","CON","nul.txt","COM1.log","LPT9","name.","name ","a/b.txt","a\\b.txt"})Reject(400,()=>f.Upload(Guid.NewGuid().ToString("N"),name,new byte[]{65}));foreach(string relative in new[]{"../note.txt","/note.txt","C:/note.txt","//server/note.txt","folder/../note.txt","folder//note.txt","folder/CON/note.txt","folder/not-note.txt"})Reject(400,()=>f.Upload(Guid.NewGuid().ToString("N"),"note.txt",new byte[]{65},relative));
        });
        check("portal attachment base64 is canonical bounded and never fetches a URL",()=>{
            var f=new Fixture(root,"base64");foreach(string encoded in new[]{"YQ","YQ=","YQ==\n","Y Q==","YR==","http://private/file","YQ--","=AAA"}){string body="{\"name\":\"a.txt\",\"mimeType\":\"text/plain\",\"contentBase64\":"+Json.Write(encoded)+"}";Reject(400,()=>PortalAttachments.UploadCore(f.Store,f.Route,f.Grant,"base64",body,()=>100L<<30,f.Now));}
        });
        check("portal attachment MIME images require supported matching magic",()=>{
            var f=new Fixture(root,"image-type");Reject(415,()=>f.Upload("fake","image.png",Encoding.UTF8.GetBytes("not PNG"),null,"image/png"));Reject(415,()=>f.Upload("svg","image.svg",Encoding.UTF8.GetBytes("<svg/>"),null,"image/svg+xml"));var good=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");string id=Fixture.UploadedId(f.Upload("png","image.png",good,null,"image/png"));Assert(id.Length==32&&PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path.EndsWith(".png",StringComparison.Ordinal),"Supported PNG or server extension refused");
        });
        check("portal attachment another route instance account or home cannot read or claim bytes",()=>{
            var f=new Fixture(root,"scope");string id=f.Add();foreach(string property in new[]{"route","instance","account","home"}){var grant=Json.Read<PortalInstanceGrant>(Json.Write(f.Grant));string route=f.Route.Id;if(property=="route")route=new string('f',32);if(property=="instance")grant.InstanceId=new string('b',32);if(property=="account")grant.AccountKey="another";if(property=="home")grant.Home=Path.Combine(f.Store.Root,"another-home");Reject(404,()=>PortalAttachments.Require(f.Store,route,grant,new[]{id}));Reject(404,()=>PortalAttachments.Claim(f.Store,route,grant,new[]{id}));}Assert(!f.Record(id).Used,"Foreign claim changed history retention");
        });
        check("portal attachment turn file count duplicates and aggregate size are atomic",()=>{
            var f=new Fixture(root,"turn-limits");string id=f.Add();Reject(400,()=>PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id,id}));Reject(400,()=>PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,Enumerable.Repeat(id,11)));Reject(400,()=>PortalAttachments.Claim(f.Store,f.Route.Id,f.Grant,new[]{id,id}));Assert(!f.Record(id).Used,"Rejected turn partially claimed bytes");
        });
        check("portal attachment accepts exactly 8 MiB and enforces 24 MiB per turn",()=>{
            var f=new Fixture(root,"byte-limits");var bytes=new byte[PortalAttachments.MaximumFileBytes];var ids=Enumerable.Range(0,3).Select(i=>f.Add("large-"+i,"fixture.bin",bytes)).ToList();Assert(PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,ids).Sum(r=>r.Size)==PortalAttachments.MaximumTurnBytes,"Exact turn boundary refused");ids.Add(f.Add("extra","extra.txt",new byte[]{1}));Reject(413,()=>PortalAttachments.Claim(f.Store,f.Route.Id,f.Grant,ids));Assert(ids.All(id=>!f.Record(id).Used),"Oversized turn partially claimed");Reject(413,()=>f.Add("too-large","oversize.bin",new byte[PortalAttachments.MaximumFileBytes+1]));
        });
        check("portal attachment JSON upload has a dedicated 12 MiB parser only",()=>{
            var f=new Fixture(root,"upload-json-limit");Reject(413,()=>PortalAttachments.UploadCore(f.Store,f.Route,f.Grant,"huge",new string(' ',PortalAttachments.MaximumUploadBytes+1),()=>100L<<30,f.Now));bool generalRejected=false;try{Json.Read<object>("\""+new string('A',4194305)+"\"");}catch{generalRejected=true;}Assert(generalRejected,"General JSON bound was increased");
        });
        check("portal attachment low disk and storage budgets fail before writing payload",()=>{
            var f=new Fixture(root,"budget");Reject(507,()=>PortalAttachments.UploadCore(f.Store,f.Route,f.Grant,"disk",Fixture.Body("a.txt",new byte[]{65}),()=>PortalAttachments.MinimumFreeBytes,f.Now));Assert(!Directory.Exists(Path.Combine(f.Store.Root,"portal-attachments")),"Low disk created payload");
        });
        check("portal attachment aggregate staged storage reservation is bounded",()=>{
            var f=new Fixture(root,"staged-budget");string id=f.Add();var original=f.Record(id);for(int i=0;i<32;i++){var record=Json.Read<PortalAttachmentRecord>(Json.Write(original));record.Id=(i+100).ToString("x32");record.Size=PortalAttachments.MaximumFileBytes;record.State="staging";f.Save(record);}Reject(507,()=>f.Add("over-budget"));
        });
        check("portal attachment modified or missing stored bytes cannot be sent",()=>{
            var f=new Fixture(root,"integrity");string id=f.Add();string path=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path;File.WriteAllText(path,"changed");Reject(409,()=>PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}));Assert(!f.Record(id).Used,"Changed bytes claimed");
        });
        check("portal attachment public metadata never reads or rehashes stored content",()=>{
            var f=new Fixture(root,"metadata-only");string id=f.Add();string path=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path;using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)){var metadata=PortalAttachments.PublicMetadata(f.Store,f.Route.Id,f.Grant,new[]{id});Assert(metadata.Length==1&&!Json.Write(metadata).Contains(f.Store.Root),"Metadata endpoint read locked bytes or leaked path");}
        });
        check("portal attachment corrupted metadata cannot alias another file ID",()=>{
            var f=new Fixture(root,"record-alias");string first=f.Add("first"),second=f.Add("second");var record=f.Record(second);f.Store.WriteRecord(PortalAttachments.RecordName(first),record);Reject(404,()=>PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{first}));Reject(404,()=>PortalAttachments.PublicMetadata(f.Store,f.Route.Id,f.Grant,new[]{first}));
        });
        check("portal attachment cleanup expires only unclaimed uploads and retains replay tombstone",()=>{
            var f=new Fixture(root,"cleanup");string id=f.Add();string path=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path;var result=f.Clean();Assert(Convert.ToInt32(Json.Get(result,"expired"))==1&&!File.Exists(path)&&f.Record(id).State=="expired","Unclaimed expired payload not cleaned");Assert(Convert.ToInt64(Json.Get(result,"bytesRemoved"))==f.Record(id).Size,"Cleanup count did not match removed payload");Reject(410,()=>f.Add());Reject(410,()=>PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}));
        });
        check("portal attachment cleanup never counts absent bytes as reclaimed storage",()=>{
            var f=new Fixture(root,"cleanup-missing");string id=f.Add();string path=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path;File.Delete(path);Directory.Delete(Path.GetDirectoryName(path),false);var result=f.Clean();Assert(Convert.ToInt32(Json.Get(result,"expired"))==1&&Convert.ToInt64(Json.Get(result,"bytesRemoved"))==0&&f.Record(id).PayloadRemoved,"Missing payload counted reserved bytes as disk reclaimed");
        });
        check("portal attachment cleanup counts the actual partial scratch length",()=>{
            var f=new Fixture(root,"cleanup-partial");string id=f.Add();string path=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path,part=Path.Combine(Path.GetDirectoryName(path),"upload.part");File.Move(path,part);using(var output=new FileStream(part,FileMode.Open,FileAccess.Write,FileShare.None))output.SetLength(3);var record=f.Record(id);record.State="abandoned";f.Save(record);var result=f.Clean();Assert(Convert.ToInt32(Json.Get(result,"expired"))==1&&Convert.ToInt64(Json.Get(result,"bytesRemoved"))==3&&!File.Exists(part),"Partial upload counted its original reservation instead of removed bytes");
        });
        check("portal attachment claimed history and fresh staged uploads never auto expire",()=>{
            var f=new Fixture(root,"retain-history");string id=f.Add();PortalAttachments.Claim(f.Store,f.Route.Id,f.Grant,new[]{id});f.Clean();Assert(f.Record(id).Used&&PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Length==1,"Historical attachment deleted");var g=new Fixture(root,"retain-fresh");string fresh=g.Add();PortalAttachments.CleanupCore(g.Store,g.Now.AddHours(23),(pid,ticks)=>(bool?)false);Assert(g.Record(fresh).State=="ready","Fresh upload expired");
        });
        check("portal attachment cleanup preserves unknown files active staging and inaccessible files",()=>{
            var f=new Fixture(root,"retain-unknown");string id=f.Add();var r=f.Record(id);string directory=Path.GetDirectoryName(PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path);File.WriteAllText(Path.Combine(directory,"user-original.txt"),"must remain");f.Clean();Assert(f.Record(id).State=="ready"&&File.Exists(Path.Combine(directory,"user-original.txt")),"Unknown user file cleaned");var g=new Fixture(root,"retain-active");string active=g.Add();var staged=g.Record(active);staged.State="staging";g.Save(staged);g.Clean((pid,ticks)=>(bool?)true);Assert(g.Record(active).State=="staging","Active owner staging cleaned");
        });
        check("portal attachment cleanup retains changed bytes and files held by an active reader",()=>{
            var f=new Fixture(root,"cleanup-changed");string id=f.Add();string path=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new[]{id}).Single().Path;File.WriteAllText(path,"changed original must remain");f.Clean();Assert(File.Exists(path)&&f.Record(id).State=="ready","Changed file was cleaned");var g=new Fixture(root,"cleanup-reader");string read=g.Add();string active=PortalAttachments.Require(g.Store,g.Route.Id,g.Grant,new[]{read}).Single().Path;using(var handle=new FileStream(active,FileMode.Open,FileAccess.Read,FileShare.Read)){g.Clean();Assert(File.Exists(active)&&g.Record(read).State=="ready","Active reader payload deleted");}
        });
        check("portal attachment crash after final rename reconciles without rewriting bytes",()=>{
            var f=new Fixture(root,"crash-reconcile");string id=f.Add();var record=f.Record(id);record.State="staging";f.Save(record);string path=PortalAttachments.Require(f.Store,f.Route.Id,f.Grant,new string[0]).Length==0?Path.Combine(f.Store.Root,"portal-attachments",id,"blob.bin"):null;var stamp=File.GetLastWriteTimeUtc(path);var receipt=f.Upload("file","note.txt",Encoding.UTF8.GetBytes("fictional fixture only"));Assert(Fixture.UploadedId(receipt)==id&&f.Record(id).State=="ready"&&File.GetLastWriteTimeUtc(path)==stamp,"Crash recovery rewrote or duplicated payload");
        });
    }
    public static int Main(string[] args){int count=0;try{RunAll((name,test)=>{test();count++;Console.WriteLine("PASS "+name);},Path.GetFullPath(args.Length==0?"work/portal-composer-tests":args[0]));Console.WriteLine(count+" attachment tests passed");return 0;}catch(Exception e){Console.WriteLine(e.GetType().Name+": "+e.Message);return 1;}}
}
