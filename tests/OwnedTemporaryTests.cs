using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Creezio.Switcher;

internal static class OwnedTemporaryTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("storage inventory exposes individual history databases without reading or offering deletion",delegate{
            string root=Path.Combine(sandbox,"storage-history-file");Directory.CreateDirectory(root);
            string file=Path.Combine(root,"thread_history_1.sqlite");File.WriteAllText(file,"fictional database fixture");
            var before=File.ReadAllBytes(file);var report=StorageMaintenance.Scan(new[]{new StorageScope{Id="profile",Name="Fixture",Path=root,Kind="instance",Protected=true}},CancellationToken.None);
            var entry=report.Entries.Single(r=>r.Path==file);Assert(entry.Kind=="history"&&entry.Bytes==before.Length&&!entry.CanClean&&before.SequenceEqual(File.ReadAllBytes(file)),"history database hidden, read as disposable or changed");
        });
        check("owned helper temp does not change host TEMP and is deleted after release",delegate{
            string root=Path.Combine(sandbox,"owned-helper"),before=Environment.GetEnvironmentVariable("TEMP");
            var temp=new OwnedTemporary(root);string folder=temp.DirectoryPath;
            var start=new ProcessStartInfo("unused.exe");temp.Apply(start);
            Assert(start.EnvironmentVariables["TEMP"]==folder&&start.EnvironmentVariables["TMP"]==folder,"helper temp not scoped");
            Assert(Environment.GetEnvironmentVariable("TEMP")==before,"changed host environment");
            File.WriteAllText(Path.Combine(folder,"cache.tmp"),"regenerable fixture");
            var active=StorageMaintenance.CleanRuntime(root,new[]{folder},CancellationToken.None);
            Assert(!active.Single().Removed&&Directory.Exists(folder),"removed active lease");
            temp.Dispose();Assert(temp.Result.Removed&&!Directory.Exists(folder),"owned temporary not released: "+temp.Result.Code+" "+temp.Result.Message);
            temp.Dispose();
        });
        check("owned helper temp preserves unexpected source or credential files",delegate{
            string root=Path.Combine(sandbox,"owned-protected");var temp=new OwnedTemporary(root);
            string source=Path.Combine(temp.DirectoryPath,"source.cs");File.WriteAllText(source,"fixture source");
            temp.Dispose();Assert(!temp.Result.Removed&&temp.Result.Code=="protected-data"&&File.Exists(source),"deleted unclassified source");
        });
        check("storage supervision respects opt out and records actual free disk",delegate{
            string root=Path.Combine(sandbox,"storage-optout");Directory.CreateDirectory(root);var store=new RelayStore(Path.Combine(root,"relay"));
            store.WriteRecord("storage-settings.dpapi",new StorageSettings{AutoCleanOwnedTemps=false});
            var report=StorageSupervision.Run(store,root,CancellationToken.None);
            Assert(report.FreeBytes>=0&&report.Results.Length==0,"disabled cleanup ran or free disk absent");
            Assert(store.ReadRecord<StorageMaintenanceReport>("storage-maintenance.dpapi").CheckedUtc==report.CheckedUtc,"maintenance receipt absent");
        });
        check("storage supervision cleans only marked dead helper temps and rotates bounded batch",delegate{
            string root=Path.Combine(sandbox,"storage-supervise");Directory.CreateDirectory(root);var store=new RelayStore(Path.Combine(root,"relay"));
            string parent=Path.Combine(root,"temporary");Directory.CreateDirectory(parent);
            for(int n=1;n<=11;n++){
                string folder=Path.Combine(parent,n.ToString("x32"));Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"switcher-temporary-owner.json"),Json.Write(new RuntimeOwner{Kind="switcher-owned-temp-v1",Directory=folder,Pid=Int32.MaxValue,Started=1}),new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder,"fixture.tmp"),"fixture");
            }
            var first=StorageSupervision.Run(store,root,CancellationToken.None);
            Assert(first.Results.Count(r=>r.Removed)<=10&&Directory.GetDirectories(parent).Length>0,"unbounded pass");
            for(int n=0;n<12&&Directory.GetDirectories(parent).Length>0;n++)StorageSupervision.Run(store,root,CancellationToken.None);
            Assert(Directory.GetDirectories(parent).Length==0,"remaining batch starved");
            string unknown=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(unknown);File.WriteAllText(Path.Combine(unknown,"keep.tmp"),"unowned");
            StorageSupervision.Run(store,root,CancellationToken.None);Assert(Directory.Exists(unknown),"deleted folder with no ownership proof");
        });
    }
}
