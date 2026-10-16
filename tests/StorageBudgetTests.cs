using System;
using System.IO;
using System.Linq;
using System.Threading;
using Creezio.Switcher;

internal static class StorageBudgetTests
{
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static Exception Reject(Action action){try{action();}catch(Exception error){return error;}throw new Exception("Expected storage admission refusal");}
    private static StorageBudgetVolume Volume(long? bytes){return new StorageBudgetVolume{Root="D:\\",AvailableBytes=bytes};}
    private sealed class VirtualOutput:Stream
    {
        internal long Cursor,Size;internal int Writes;internal bool Disposed;
        public override bool CanRead{get{return false;}}public override bool CanWrite{get{return !Disposed;}}public override bool CanSeek{get{return true;}}
        public override long Length{get{return Size;}}public override long Position{get{return Cursor;}set{Cursor=value;}}
        public override void Write(byte[] buffer,int offset,int count){Writes++;Cursor=checked(Cursor+count);Size=Math.Max(Size,Cursor);}
        public override void Flush(){}public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){Size=value;}
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        const long m=StorageBudget.MiB,g=StorageBudget.GiB;
        check("storage units and strict large-operation boundary use binary MiB and GiB",()=>{
            Assert(m==1048576&&g==1073741824,"binary units changed");
            var small=StorageBudget.Decide("D:\\",2*g,50*m,0);var large=StorageBudget.Decide("D:\\",2*g,50*m+1,0);
            Assert(small.Allowed&&!small.Large&&!large.Allowed&&large.Large&&large.RequiredBytes==20*g,"50 MiB boundary was rounded or the 20 GiB floor was omitted");
        });
        check("storage accepts exactly twenty GiB before a large operation and refuses one byte below",()=>{
            Assert(StorageBudget.Decide("D:\\",20*g,51*m,0).Allowed,"exact admission boundary refused");
            Assert(!StorageBudget.Decide("D:\\",20*g-1,51*m,0).Allowed,"large operation admitted below the floor");
        });
        check("storage adds known write cost and margin instead of checking only the floor",()=>{
            var decision=StorageBudget.Decide("D:\\",25*g,24*g,2*g);
            Assert(!decision.Allowed&&decision.RequiredBytes==26*g&&StorageBudget.Decide("D:\\",26*g,24*g,2*g).Allowed,"known peak write and margin were ignored");
        });
        check("storage does not impose twenty GiB on small known writes",()=>{
            var decision=StorageBudget.Decide("D:\\",2*g,1024,g);Assert(decision.Allowed&&!decision.Large&&decision.RequiredBytes==g+1024,"small copy was rejected merely because the disk is below 20 GiB");
        });
        check("storage fails closed for unknown estimates and unknown free space",()=>{
            Assert(StorageBudget.Decide("D:\\",100*g,null).Reason=="estimate-unknown","unknown size assumed small");
            Assert(StorageBudget.Decide("D:\\",null,51*m).Reason=="space-unknown","unknown free space assumed sufficient");
        });
        check("storage rejects negative sizes and arithmetic overflow without wrapping",()=>{
            Assert(Reject(()=>StorageBudget.Decide("D:\\",g,-1)) is ArgumentOutOfRangeException,"negative write accepted");
            Assert(Reject(()=>StorageBudget.Decide("D:\\",-1,1)) is ArgumentOutOfRangeException,"negative free space accepted");
            Assert(Reject(()=>StorageBudget.Decide("D:\\",g,1,-1)) is ArgumentOutOfRangeException,"negative margin accepted");
            var overflow=StorageBudget.Decide("D:\\",Int64.MaxValue,Int64.MaxValue,1);Assert(!overflow.Allowed&&overflow.Reason=="estimate-overflow"&&overflow.RequiredBytes==Int64.MaxValue,"cost overflow admitted work");
            Assert(!StorageBudget.Cost(Int64.MaxValue,2).HasValue&&StorageBudget.Cost(123,3)==369,"copy multiplication wrapped");
        });
        check("storage probes the destination disk rather than the application or source disk",()=>{
            string called=null;var decision=StorageBudget.Inspect("D:\\fixture-new\\project",51*m,0,path=>{called=path;return Volume(30*g);});
            Assert(called=="D:\\fixture-new\\project"&&decision.Volume=="D:\\"&&decision.Allowed,"another disk's free space was used");
        });
        check("storage refusal names the disk, available bytes, requirement and Storage destination",()=>{
            var error=Reject(()=>StorageBudget.Require("D:\\fixture-new",51*m,"Copie du projet",g,path=>Volume(3*g))) as StorageBudgetException;
            Assert(error!=null&&error.Message.Contains("D:\\")&&error.Message.Contains("3,00 Gio")&&error.Message.Contains("20,00 Gio")&&error.Message.Contains("Stockage")&&error.Message.Contains("dossier commun"),"refusal does not explain the concrete recovery path");
        });
        check("storage admission alone creates no folder or reservation file",()=>{
            string target=Path.Combine(root,"budget-no-side-effects","destination");Reject(()=>StorageBudget.Require(target,51*m,"Copie",0,path=>Volume(0)));
            Assert(!Directory.Exists(Path.GetDirectoryName(target)),"budget refusal mutated destination storage");
        });
        check("storage rejects relative destinations before querying any disk",()=>{
            bool queried=false;Reject(()=>StorageBudget.Inspect("relative\\project",1,0,path=>{queried=true;return Volume(g);}));Assert(!queried,"relative destination reached the disk probe");
        });
        check("project copy budget is checked before any destination mutation or checkpoint",()=>{
            string source=Path.Combine(root,"budget-project-source"),destination=source+"-copy";Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"note.txt"),"source kept");
            var plan=ProjectFiles.Plan(source,destination,CancellationToken.None,path=>Volume(30*g));bool checkpoint=false;
            Reject(()=>ProjectFiles.Copy(plan,()=>checkpoint=true,CancellationToken.None,path=>Volume(0)));
            Assert(!checkpoint&&!plan.Completed&&!Directory.Exists(destination)&&File.ReadAllText(Path.Combine(source,"note.txt"))=="source kept","storage refusal wrote a marker, copied a file or changed source");
        });
        check("project copy plan refusal creates no destination and keeps dependency files in its requested scope",()=>{
            string source=Path.Combine(root,"budget-dependencies"),destination=source+"-copy";Directory.CreateDirectory(Path.Combine(source,"node_modules","fixture"));Directory.CreateDirectory(Path.Combine(source,"dist"));File.WriteAllText(Path.Combine(source,"node_modules","fixture","keep.js"),"dependency");File.WriteAllText(Path.Combine(source,"dist","keep.txt"),"requested artifact");
            Reject(()=>ProjectFiles.Plan(source,destination,CancellationToken.None,path=>Volume(0)));Assert(!Directory.Exists(destination),"refused plan created destination");
            var plan=ProjectFiles.Plan(source,destination,CancellationToken.None,path=>Volume(30*g));Assert(plan.Files.Any(file=>file.Relative==Path.Combine("node_modules","fixture","keep.js"))&&plan.Files.Any(file=>file.Relative==Path.Combine("dist","keep.txt")),"independent-copy semantics silently excluded supplied files");
        });
        check("project copy succeeds with a small injected budget and preserves Unicode contents",()=>{
            string source=Path.Combine(root,"budget-small-source"),destination=source+"-copy";Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"été.txt"),"français日本語");
            var plan=ProjectFiles.Plan(source,destination,CancellationToken.None,path=>Volume(2*g));ProjectFiles.Copy(plan,()=>{},CancellationToken.None,path=>Volume(2*g));
            Assert(plan.Completed&&File.ReadAllText(Path.Combine(destination,"été.txt"))=="français日本語"&&!File.Exists(Path.Combine(destination,".account-switcher-copy")),"small admitted copy was incomplete");
        });
        check("worktree budget derives file sizes from the selected commit including symlinks and names with tabs",()=>{
            string tree="100644 blob fixture1 123\tfile with spaces\0"+"120000 blob fixture2 17\tlink\0"+"160000 commit fixture3 -\tsubmodule\0"+"100644 blob fixture4 5\tname\twithtab\0";
            Assert(WorkspaceService.CommittedBytes(tree)==145,"commit tree sizes were confused with the current working folder");
        });
        check("worktree budget refuses unparseable or overflowing blob estimates",()=>{
            Assert(!WorkspaceService.CommittedBytes("100644 blob fixture -\tfile\0").HasValue,"unknown blob length treated as zero");
            Assert(!WorkspaceService.CommittedBytes("malformed").HasValue,"malformed tree admitted");
            Assert(!WorkspaceService.CommittedBytes("100644 blob a 9223372036854775807\ta\0"+"100644 blob b 1\tb\0").HasValue,"tree sum wrapped");
            Assert(WorkspaceService.CommittedBytes("")==0,"empty committed tree cannot be estimated");
        });
        check("history preparation checks budget before the write that crosses the large-operation boundary",()=>{
            var sink=new VirtualOutput{Cursor=25*m-1,Size=25*m-1};using(var writer=StorageBudget.GuardWrites(sink,"D:\\fixture-history","Historique",2,path=>Volume(2*g))){writer.Write(new byte[1],0,1);long before=sink.Cursor;var error=Reject(()=>writer.Write(new byte[1],0,1));Assert(error is StorageBudgetException&&sink.Writes==1&&sink.Cursor==before,"large history write reached output before admission");}
            Assert(sink.Disposed,"owned preparation stream was not closed");
        });
        check("history preparation periodically rechecks a growing temporary and never writes after refusal",()=>{
            int probes=0;var sink=new VirtualOutput{Cursor=30*m,Size=30*m};using(var writer=StorageBudget.GuardWrites(sink,"D:\\fixture-history","Historique",2,path=>Volume(++probes==1?30*g:19*g))){writer.Write(new byte[1],0,1);sink.Cursor+=5*m;sink.Size=sink.Cursor;long before=sink.Cursor;Reject(()=>writer.Write(new byte[1],0,1));Assert(probes==2&&sink.Writes==1&&sink.Cursor==before,"grown snapshot reused a stale space check");}
        });
        check("history preparation fails closed before any output when its cost overflows",()=>{
            var sink=new VirtualOutput{Cursor=Int64.MaxValue-1,Size=Int64.MaxValue-1};using(var writer=StorageBudget.GuardWrites(sink,"D:\\fixture-history","Historique",3,path=>Volume(Int64.MaxValue))){Reject(()=>writer.Write(new byte[1],0,1));Assert(sink.Writes==0,"overflowing snapshot cost reached output");}
        });
    }
}
