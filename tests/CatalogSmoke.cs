using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Creezio.Switcher;

internal static class CatalogSmoke
{
    public static int Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);var store=new RelayStore(root);
        try{
            var clock=Stopwatch.StartNew();
            for(int i=store.Count();i<2500;i++){string id=i.ToString("x32");store.Save(new RelayMessage{SchemaVersion=3,Id=id,Title="Fixture "+i,State=i%100==0?"queued":"completed",CreatedUtc=DateTime.UtcNow.ToString("o"),Prompt=new String('x',12000),Result=new String('y',12000)});}
            long build=clock.ElapsedMilliseconds;clock.Restart();var cold=new RelayStore(root);var page=cold.Query(null,1100,100);long first=clock.ElapsedMilliseconds;clock.Restart();var warm=cold.Query(null,1200,100);long next=clock.ElapsedMilliseconds;
            if(page.Count!=100||warm.Count!=100||cold.Count()!=2500||cold.ActiveMessages().Count!=25||page.Any(m=>m.Prompt!=null||m.Result!=null))throw new Exception("Incorrect indexed history");
            Console.WriteLine(Json.Write(new{passed=true,records=2500,active=25,pageSize=100,creationMs=build,coldPageMs=first,warmPageMs=next}));return 0;
        }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
        finally{SafeFiles.DeleteOwnedTree(Path.GetDirectoryName(root),root);}
    }
}
