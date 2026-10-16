using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Creezio.Switcher
{
    public sealed class CatalogHeader
    {
        public string Generation {get;set;}
        public List<string> Buckets {get;set;}
        public List<string> Active {get;set;}
        public int Count {get;set;}
        public CatalogHeader(){Buckets=new List<string>();Active=new List<string>();}
    }
    public sealed class CatalogBucket
    {
        public List<RelayMessage> Items {get;set;}
        public CatalogBucket(){Items=new List<RelayMessage>();}
    }
    internal sealed partial class RelayStore
    {
        private sealed class BucketCache {public string Generation;public CatalogBucket Value;}
        private readonly ConcurrentDictionary<string,BucketCache> bucketCache=new ConcurrentDictionary<string,BucketCache>();
        private string BucketPath(string key){if(key.Length!=2||!System.Text.RegularExpressions.Regex.IsMatch(key,"^[a-f0-9]{2}$"))throw new InvalidOperationException("Index invalide.");return Path.Combine(Root,"catalog-v2",key+".dpapi");}
        private static RelayMessage Summary(RelayMessage message){var m=Json.Read<RelayMessage>(Json.Write(message));m.Prompt=null;m.Result=null;m.Events=null;m.Continuation=null;if(m.Job!=null){m.Job.Prompt=null;m.Job.Files=null;}return m;}
        private static bool IndexedActive(RelayMessage m){return RelayRouter.Active(m)||RelayEngine.PendingReturn(m)||m.ReturnState=="sending"||m.ReturnState=="uncertain";}
        private CatalogHeader EnsureCatalog()
        {
            if(File.Exists(Path.Combine(Root,"catalog-header-v2.dpapi"))&&!File.Exists(Path.Combine(Root,"catalog-dirty-v2.dpapi")))return ReadRecord<CatalogHeader>("catalog-header-v2.dpapi");
            var header=new CatalogHeader{Generation=Guid.NewGuid().ToString("N")};
            var paths=new[]{"jobs-v4","jobs-v3","jobs","messages"}.SelectMany(folder=>Directory.GetFiles(Path.Combine(Root,folder),"*.dpapi")).GroupBy(Path.GetFileName).Select(g=>g.First());
            var rows=paths.Select(path=>Summary(Read<RelayMessage>(path))).ToList();
            foreach(var group in rows.GroupBy(m=>m.Id.Substring(0,2))){Write(BucketPath(group.Key),new CatalogBucket{Items=group.ToList()});header.Buckets.Add(group.Key);}
            header.Active=rows.Where(IndexedActive).Select(m=>m.Id).ToList();header.Count=rows.Count;WriteRecord("catalog-header-v2.dpapi",header);bucketCache.Clear();
            if(File.Exists(Path.Combine(Root,"catalog-dirty-v2.dpapi")))File.Delete(Path.Combine(Root,"catalog-dirty-v2.dpapi"));return header;
        }
        private void UpdateCatalog(CatalogHeader header,RelayMessage m)
        {
            string key=m.Id.Substring(0,2);var bucket=Read<CatalogBucket>(BucketPath(key));bool existed=bucket.Items.Any(x=>x.Id==m.Id);bucket.Items.RemoveAll(x=>x.Id==m.Id);bucket.Items.Add(Summary(m));Write(BucketPath(key),bucket);
            if(!header.Buckets.Contains(key))header.Buckets.Add(key);if(!existed)header.Count++;
            header.Active.Remove(m.Id);if(IndexedActive(m))header.Active.Add(m.Id);header.Generation=Guid.NewGuid().ToString("N");WriteRecord("catalog-header-v2.dpapi",header);
            BucketCache ignored;bucketCache.TryRemove(key,out ignored);
        }
        private CatalogHeader Catalog()
        {
            if(File.Exists(Path.Combine(Root,"catalog-header-v2.dpapi"))&&!File.Exists(Path.Combine(Root,"catalog-dirty-v2.dpapi")))return ReadRecord<CatalogHeader>("catalog-header-v2.dpapi");
            // A dirty marker may belong to an in-flight writer, not a crashed one.
            // Give its short transaction time to finish before rebuilding.
            using(CatalogLease())return EnsureCatalog();
        }
        private FileStream CatalogLease(){for(int attempt=0;attempt<100;attempt++){try{return Lease("catalog");}catch(IOException){if(attempt==99)throw;System.Threading.Thread.Sleep(20);}}throw new IOException("Index occupé.");}
        private CatalogBucket Bucket(string key,string generation)
        {
            BucketCache cached;string path=BucketPath(key);var info=new FileInfo(path);string stamp=info.LastWriteTimeUtc.Ticks+":"+info.Length;
            if(!bucketCache.TryGetValue(key,out cached)||cached.Generation!=stamp){cached=new BucketCache{Generation=stamp,Value=Read<CatalogBucket>(path)};bucketCache[key]=cached;}
            return cached.Value;
        }
        public int Count(){return Catalog().Count;}
        public List<RelayMessage> Query(Func<RelayMessage,bool> predicate,int offset,int limit)
        {
            if(offset<0||limit<0)throw new InvalidOperationException("Page invalide.");using(CatalogLease()){var header=EnsureCatalog();var rows=header.Buckets.SelectMany(key=>Bucket(key,header.Generation).Items);
            if(predicate!=null)rows=rows.Where(predicate);
            return rows.OrderByDescending(m=>m.CreatedUtc,StringComparer.Ordinal).ThenBy(m=>m.Id,StringComparer.Ordinal).Skip(offset).Take(limit).Select(m=>Json.Read<RelayMessage>(Json.Write(m))).ToList();}
        }
        public List<RelayMessage> ActiveMessages()
        {
            using(CatalogLease()){var header=EnsureCatalog();var ids=new HashSet<string>(header.Active);
            return ids.Select(id=>id.Substring(0,2)).Distinct().SelectMany(key=>Bucket(key,header.Generation).Items).Where(m=>ids.Contains(m.Id)).Select(m=>Json.Read<RelayMessage>(Json.Write(m))).ToList();}
        }
        public void RebuildCatalog(){using(CatalogLease()){WriteRecord("catalog-dirty-v2.dpapi",new CatalogHeader());EnsureCatalog();}}
    }
}
