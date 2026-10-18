using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;
using Microsoft.Win32.SafeHandles;

// All HTTP is in-memory, all files belong to the test sandbox. No installer,
// native Codex engine, model, source credential, or live application is invoked.
internal static class SwitcherUpdateTests
{
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static void Reject(Action action){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}catch(IOException){rejected=true;}Assert(rejected,"Unsafe update was accepted");}
    private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    private static string HashFile(string path){return Hash(File.ReadAllBytes(path));}
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation{internal uint Attributes;internal System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;internal uint Volume,High,Low,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation information);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateHardLink(string destination,string source,IntPtr security);
    private static string FileId(string path){using(var file=File.OpenRead(path)){FileInformation info;if(!GetFileInformationByHandle(file.SafeFileHandle,out info))throw new Win32Exception(Marshal.GetLastWin32Error());return info.Volume+":"+info.IndexHigh+":"+info.IndexLow;}}
    private sealed class Http : HttpMessageHandler
    {
        internal readonly Dictionary<string,byte[]> Bodies=new Dictionary<string,byte[]>();internal readonly Dictionary<string,string> Redirects=new Dictionary<string,string>();internal readonly List<string> Requests=new List<string>();internal readonly List<string> Accepts=new List<string>();internal Action<string> Before;internal Func<string,HttpContent> Content=null;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();string url=request.RequestUri.AbsoluteUri;Requests.Add(url);string accept=String.Join(",",request.Headers.Accept.Select(value=>value.MediaType));Accepts.Add(accept);string expected=request.RequestUri.Host=="api.github.com"?"application/vnd.github+json":"application/octet-stream";if(accept!=expected)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType));if(Before!=null)Before(url);string redirect;if(Redirects.TryGetValue(url,out redirect)){var moved=new HttpResponseMessage(HttpStatusCode.Redirect);moved.Headers.Location=new Uri(redirect,UriKind.RelativeOrAbsolute);return Task.FromResult(moved);}byte[] bytes;if(!Bodies.TryGetValue(url,out bytes))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=Content==null?new ByteArrayContent(bytes):Content(url)});
        }
    }
    private sealed class UnknownLength : HttpContent
    {
        private readonly byte[] bytes;internal UnknownLength(byte[] bytes){this.bytes=bytes;}
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext context){return stream.WriteAsync(bytes,0,bytes.Length);}
        protected override Task<Stream> CreateContentReadStreamAsync(){return Task.FromResult<Stream>(new MemoryStream(bytes,false));}
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root,Install,Version,ArchiveName,ArchiveUrl,SumsUrl,Current;internal readonly Http Handler=new Http();internal readonly HttpClient Client;internal Dictionary<string,byte[]> Files;internal SwitcherUpdatePackages.Manifest Manifest;internal byte[] Archive;internal SwitcherUpdateRelease Release;
        internal Fixture(string sandbox)
        {
            Root=Path.Combine(sandbox,"u"+Guid.NewGuid().ToString("N").Substring(0,5));Install=Path.Combine(Root,"install");Current=Path.Combine(Root,"current");Directory.CreateDirectory(Current);Version=FileVersionInfo.GetVersionInfo(typeof(SwitcherUpdateTests).Assembly.Location).ProductVersion;ArchiveName="CodexAccountSwitcher-"+Version+"-windows-x64.zip";ArchiveUrl="https://github.com/creezio/codex-account-switcher-windows/releases/download/v"+Version+"/"+ArchiveName;SumsUrl="https://github.com/creezio/codex-account-switcher-windows/releases/download/v"+Version+"/SHA256SUMS-"+Version+".txt";Client=new HttpClient(Handler,false);
            byte[] product=File.ReadAllBytes(typeof(SwitcherUpdateTests).Assembly.Location);Files=new Dictionary<string,byte[]>{{"CodexAccountSwitcher.exe",Encoding.UTF8.GetBytes("Fictional native apphost never executed")},{"CodexAccountSwitcher.dll",product},{"CodexAccountSwitcher.deps.json",Encoding.UTF8.GetBytes("{}")},{"CodexAccountSwitcher.runtimeconfig.json",Encoding.UTF8.GetBytes("{}")},{"CreezioRelay.exe",product},{"CreezioRelay.exe.config",Encoding.UTF8.GetBytes("<configuration/>")},{"coreclr.dll",new byte[]{1,2,3}},{"hostfxr.dll",new byte[]{4,5,6}},{"hostpolicy.dll",new byte[]{7,8,9}},{"PresentationFramework.dll",new byte[]{10,11,12}},{"plugins/creezio-relay/.codex-plugin/plugin.json",Encoding.UTF8.GetBytes("{}")},{"plugins/creezio-relay/.mcp.json",Encoding.UTF8.GetBytes("{}")},{"plugins/creezio-relay/ui/viewer.html",Encoding.UTF8.GetBytes("<html>Fictional</html>")}};
            Files["SwitcherLauncher.exe"]=Encoding.UTF8.GetBytes("Fictional stable launcher never executed");Files["SwitcherLauncher.exe.config"]=Encoding.UTF8.GetBytes("<configuration/>");Files["SwitcherCodexBridge.exe"]=product;Files["SwitcherCodexBridge.exe.config"]=Encoding.UTF8.GetBytes("<configuration/>");Build();
        }
        internal void Build(Action<ZipArchive> extra=null,Action<SwitcherUpdatePackages.Manifest> changeManifest=null,string manifestText=null,bool includeManifest=true)
        {
            Manifest=new SwitcherUpdatePackages.Manifest{schemaVersion=1,version=Version,platform="windows-x64",healthProtocol=1,entrypoint="CodexAccountSwitcher.exe",files=Files.Select(pair=>new SwitcherUpdatePackages.ManifestFile{path=pair.Key,length=pair.Value.Length,sha256=Hash(pair.Value)}).ToList()};if(changeManifest!=null)changeManifest(Manifest);
            using(var memory=new MemoryStream()){using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){foreach(var pair in Files)Write(zip,pair.Key,pair.Value);if(includeManifest)Write(zip,"switcher-update.json",Encoding.UTF8.GetBytes(manifestText??Json.Write(Manifest)));if(extra!=null)extra(zip);}Archive=memory.ToArray();}
            Release=new SwitcherUpdateRelease{Version=Version,ArchiveUrl=ArchiveUrl,ArchiveBytes=Archive.Length,ArchiveSha256=Hash(Archive),ReleasePage="https://github.com/creezio/codex-account-switcher-windows/releases/tag/v"+Version};Handler.Bodies[ArchiveUrl]=Archive;byte[] sums=Encoding.ASCII.GetBytes(Release.ArchiveSha256+"  "+ArchiveName+"\n");Handler.Bodies[SumsUrl]=sums;SetCatalogue(new[]{Row(Version,ArchiveUrl,Release.ArchiveSha256,sums)});
        }
        internal static void Write(ZipArchive zip,string path,byte[] bytes){using(var stream=zip.CreateEntry(path,CompressionLevel.Fastest).Open())stream.Write(bytes,0,bytes.Length);}
        internal object Row(string version,string archiveUrl,string digest,byte[] sums,bool draft=false)
        {return new{tag_name="v"+version,draft=draft,prerelease=true,assets=new[]{new{name="CodexAccountSwitcher-"+version+"-windows-x64.zip",browser_download_url=archiveUrl,size=Archive.Length,digest="sha256:"+digest},new{name="SHA256SUMS-"+version+".txt",browser_download_url=SumsUrl,size=sums.Length,digest="sha256:"+Hash(sums)}}};}
        internal void SetCatalogue(object value){Handler.Bodies[SwitcherUpdatePackages.Catalogue]=Encoding.UTF8.GetBytes(Json.Write(value));}
        internal SwitcherUpdateRelease Find(string version="0.1.0"){return SwitcherUpdatePackages.Find(version,CancellationToken.None,Client).GetAwaiter().GetResult();}
        internal string Stage(CancellationToken token=default(CancellationToken)){return SwitcherUpdatePackages.Stage(Release,Install,Current,token,Client).GetAwaiter().GetResult();}
        public void Dispose(){Client.Dispose();Handler.Dispose();}
    }
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("updater catalogue selects only newer canonical versions and verifies GitHub plus checksum digests",()=>{
            using(var f=new Fixture(sandbox)){var value=f.Find();Assert(value.Version==f.Version&&value.ArchiveSha256==Hash(f.Archive)&&value.ArchiveBytes==f.Archive.Length,"Release identity differs from pinned catalogue");Assert(f.Find(f.Version)==null&&f.Find("99.0.0")==null,"Current or older release was offered as an update");Assert(SwitcherUpdatePackages.CompareVersions("0.28.0-beta.10","0.28.0-beta.7")>0&&SwitcherUpdatePackages.CompareVersions("0.28.0","0.28.0-beta.10")>0,"Semantic release order changed");Reject(()=>SwitcherUpdatePackages.CompareVersions("01.2.3","1.2.3"));Reject(()=>SwitcherUpdatePackages.CompareVersions("1.2.3-beta.01","1.2.3"));}
            using(var f=new Fixture(sandbox)){using(var wrong=new HttpRequestMessage(HttpMethod.Get,SwitcherUpdatePackages.Catalogue)){wrong.Headers.Accept.ParseAdd("application/octet-stream");using(var rejected=f.Client.SendAsync(wrong).GetAwaiter().GetResult())Assert(rejected.StatusCode==HttpStatusCode.UnsupportedMediaType,"HTTP fixture did not reproduce GitHub's 415 rejection");}f.Handler.Requests.Clear();f.Handler.Accepts.Clear();Assert(f.Find()!=null&&f.Handler.Accepts.SequenceEqual(new[]{"application/vnd.github+json","application/octet-stream"}),"Catalogue API or checksum download sent the wrong Accept media type");}
        });
        check("updater catalogue rejects checksum disagreement duplicate matching sums and absent GitHub digests",()=>{
            foreach(string error in new[]{"wrong-sum","duplicate-sum","missing-digest","foreign-asset"})using(var f=new Fixture(sandbox)){
                byte[] sums=Encoding.ASCII.GetBytes((error=="wrong-sum"?new string('0',64):f.Release.ArchiveSha256)+"  "+f.ArchiveName+"\n"+(error=="duplicate-sum"?f.Release.ArchiveSha256+"  "+f.ArchiveName+"\n":""));f.Handler.Bodies[f.SumsUrl]=sums;f.SetCatalogue(new[]{f.Row(f.Version,error=="foreign-asset"?"https://example.invalid/"+f.ArchiveName:f.ArchiveUrl,error=="missing-digest"?"":f.Release.ArchiveSha256,sums)});Reject(()=>f.Find());Assert(!Directory.Exists(f.Install),"Catalogue read created an installation");
            }
        });
        check("updater follows only approved HTTPS asset redirects and bounds metadata bodies",()=>{
            foreach(string url in new[]{"http://github.com/unsafe","https://github.com.evil.invalid/x","https://127.0.0.1/x","https://user@github.com/x","https://github.com:444/x","https://objects.githubusercontent.com/x#fragment"})using(var f=new Fixture(sandbox)){f.Handler.Redirects[SwitcherUpdatePackages.Catalogue]=url;Reject(()=>f.Find());Assert(f.Handler.Requests.Count==1,"Rejected redirect reached another server");}
            using(var f=new Fixture(sandbox)){string mirror="https://release-assets.githubusercontent.com/github-production-release-asset/fixture";f.Handler.Redirects[f.ArchiveUrl]=mirror;f.Handler.Bodies[mirror]=f.Archive;Assert(Directory.Exists(f.Stage())&&f.Handler.Requests.Contains(mirror),"Official asset redirect was refused");}
            using(var f=new Fixture(sandbox)){f.Handler.Bodies[SwitcherUpdatePackages.Catalogue]=new byte[SwitcherUpdatePackages.MaximumManifest+1];Reject(()=>f.Find());}
            using(var f=new Fixture(sandbox)){f.Handler.Content=url=>new UnknownLength(new byte[SwitcherUpdatePackages.MaximumManifest+1]);Reject(()=>f.Find());}
            using(var f=new Fixture(sandbox)){f.Handler.Redirects[SwitcherUpdatePackages.Catalogue]=SwitcherUpdatePackages.Catalogue;Reject(()=>f.Find());Assert(f.Handler.Requests.Count==5,"Redirect cycle was not bounded");}
        });
        check("updater stages a complete package separately and reuses the validated receipt without another download",()=>{
            using(var f=new Fixture(sandbox)){string source=Path.Combine(f.Current,"keep.txt");File.WriteAllText(source,"Existing application is untouched");string before=HashFile(source);string target=f.Stage();Assert(target==Path.Combine(f.Install,"versions",f.Version)&&SwitcherUpdatePackages.ValidateDirectory(target,f.Version)==Hash(Encoding.UTF8.GetBytes(Json.Write(f.Manifest))),"Staged manifest identity is incorrect");int requests=f.Handler.Requests.Count;Assert(f.Stage()==target&&f.Handler.Requests.Count==requests,"Valid completed stage was downloaded twice");Assert(HashFile(source)==before&&!File.Exists(Path.Combine(f.Install,".update-download.zip"))&&!Directory.Exists(Path.Combine(f.Install,".update-stage")),"Source changed or completed temporary cache remained");Assert(!File.Exists(Path.Combine(f.Install,"current.json")),"Staging activated an application");}
        });
        check("updater rejects old missing manifests wrong products and incomplete runtime packages",()=>{
            foreach(string error in new[]{"missing","protocol","version","platform","entrypoint","required","pe-version","manifest-self"})using(var f=new Fixture(sandbox)){
                f.Build(null,m=>{if(error=="protocol")m.healthProtocol=99;if(error=="version")m.version="99.0.0";if(error=="platform")m.platform="windows-arm64";if(error=="entrypoint")m.entrypoint="CreezioRelay.exe";if(error=="required")m.files.RemoveAll(file=>file.path=="coreclr.dll");if(error=="pe-version"){m.version="99.0.0";}if(error=="manifest-self")m.files.Add(new SwitcherUpdatePackages.ManifestFile{path="switcher-update.json",length=0,sha256=new string('0',64)});},null,error!="missing");if(error=="pe-version"){f.Release.Version="99.0.0";string url="https://github.com/creezio/codex-account-switcher-windows/releases/download/v99.0.0/CodexAccountSwitcher-99.0.0-windows-x64.zip";f.Release.ArchiveUrl=url;f.Handler.Bodies[url]=f.Archive;}
                Reject(()=>f.Stage());Assert(!Directory.Exists(Path.Combine(f.Install,"versions",f.Release.Version)),"Invalid package was exposed as a version");
            }
            foreach(string missing in new[]{"SwitcherLauncher.exe","SwitcherLauncher.exe.config"})using(var f=new Fixture(sandbox)){f.Files.Remove(missing);f.Build();Reject(()=>f.Stage());Assert(!Directory.Exists(Path.Combine(f.Install,"versions",f.Version)),"Missing stable launcher file was accepted");}
        });
        check("updater preserves legacy metadata but requires the Codex bridge from beta three",()=>{
            using(var f=new Fixture(sandbox)){
                var legacy=Json.Read<SwitcherUpdatePackages.Manifest>(Json.Write(f.Manifest));legacy.files.RemoveAll(file=>file.path=="SwitcherCodexBridge.exe"||file.path=="SwitcherCodexBridge.exe.config");legacy.version="0.29.0-beta.2";
                var accepted=SwitcherUpdatePackages.ReadManifest(Encoding.UTF8.GetBytes(Json.Write(legacy)),legacy.version);Assert(accepted.healthProtocol==1&&!accepted.files.Any(file=>file.path=="SwitcherCodexBridge.exe"),"A legacy health-protocol-one package now requires the new bridge");
                foreach(string version in new[]{"0.29.0-beta.3","0.29.0-beta.10","0.29.0","0.30.0-alpha.1"}){legacy.version=version;Reject(()=>SwitcherUpdatePackages.ReadManifest(Encoding.UTF8.GetBytes(Json.Write(legacy)),version));}
            }
        });
        check("updater rejects missing or incorrectly versioned bridges before exposing a new version",()=>{
            foreach(string missing in new[]{"SwitcherCodexBridge.exe","SwitcherCodexBridge.exe.config"})using(var f=new Fixture(sandbox)){f.Files.Remove(missing);f.Build();Reject(()=>f.Stage());Assert(!Directory.Exists(Path.Combine(f.Install,"versions",f.Version))&&!File.Exists(Path.Combine(f.Install,"current.json")),"Incomplete bridge package became available or active");}
            using(var f=new Fixture(sandbox)){f.Files["SwitcherCodexBridge.exe"]=File.ReadAllBytes(typeof(Enumerable).Assembly.Location);f.Build();Reject(()=>f.Stage());Assert(!Directory.Exists(Path.Combine(f.Install,"versions",f.Version)),"A valid hash concealed a bridge from another product version");}
            using(var f=new Fixture(sandbox)){string target=f.Stage();Assert(File.Exists(Path.Combine(target,"SwitcherCodexBridge.exe.config"))&&FileVersionInfo.GetVersionInfo(Path.Combine(target,"SwitcherCodexBridge.exe")).ProductVersion==f.Version,"The validated package omitted its correctly versioned bridge");}
        });
        check("updater rejects zip traversal alternate streams aliases duplicate entries and links",()=>{
            foreach(string entry in new[]{"../escape.txt","/absolute.txt","C:/outside.txt","plugins/a:stream","plugins/../escape","plugins/NUL.txt","plugins/trailing. ","PLUGINS/creezio-relay/ui/viewer.html","plugins\\bad.txt","plugins/creezio-relay/ui/viewer.html"})using(var f=new Fixture(sandbox)){f.Build(zip=>Fixture.Write(zip,entry,new byte[]{0}));Reject(()=>f.Stage());Assert(!File.Exists(Path.Combine(f.Root,"escape.txt")),"ZIP escaped staging root");}
            using(var f=new Fixture(sandbox)){f.Build(zip=>{var entry=zip.CreateEntry("plugins/linked");entry.ExternalAttributes=unchecked((int)0xa1ff0000);});Reject(()=>f.Stage());}
            using(var f=new Fixture(sandbox)){f.Build(zip=>zip.CreateEntry("unexpected-empty/"));Reject(()=>f.Stage());}
        });
        check("updater rejects ambiguous JSON duplicate manifest paths and edited staged files",()=>{
            using(var f=new Fixture(sandbox)){string text=Json.Write(f.Manifest).Replace("\"schemaVersion\":1","\"schemaVersion\":1,\"schemaVersion\":1");f.Build(null,null,text);Reject(()=>f.Stage());}
            using(var f=new Fixture(sandbox)){f.Build(null,m=>m.files.Add(new SwitcherUpdatePackages.ManifestFile{path="CORECLR.dll",length=3,sha256=Hash(new byte[]{1,2,3})}));Reject(()=>f.Stage());}
            using(var f=new Fixture(sandbox)){string target=f.Stage();File.WriteAllText(Path.Combine(target,"coreclr.dll"),"tampered");Reject(()=>SwitcherUpdatePackages.ValidateDirectory(target,f.Version));Reject(()=>f.Stage());}
            using(var f=new Fixture(sandbox)){string target=f.Stage();File.WriteAllText(Path.Combine(target,"extra.dll"),"unlisted");Reject(()=>SwitcherUpdatePackages.ValidateDirectory(target,f.Version));}
        });
        check("updater bounds ZIP central directory counts names and encryption before materializing entries",()=>{
            foreach(string error in new[]{"count","name","encrypted","offset"})using(var f=new Fixture(sandbox)){
                int end=f.Archive.Length-22,central=(int)BitConverter.ToUInt32(f.Archive,end+16);
                if(error=="count"){Array.Copy(BitConverter.GetBytes((ushort)65535),0,f.Archive,end+8,2);Array.Copy(BitConverter.GetBytes((ushort)65535),0,f.Archive,end+10,2);}if(error=="name")Array.Copy(BitConverter.GetBytes((ushort)32767),0,f.Archive,central+28,2);if(error=="encrypted")f.Archive[central+8]|=1;if(error=="offset")Array.Copy(BitConverter.GetBytes((uint)0),0,f.Archive,end+16,4);
                f.Release.ArchiveSha256=Hash(f.Archive);Reject(()=>f.Stage());Assert(!Directory.Exists(Path.Combine(f.Install,"versions",f.Version)),"Malformed ZIP became an installed version");
            }
        });
        check("updater interrupted owned caches resume and unowned directories remain untouched",()=>{
            using(var f=new Fixture(sandbox))using(var cancel=new CancellationTokenSource()){
                f.Handler.Before=url=>{if(url==f.ArchiveUrl)cancel.Cancel();};bool stopped=false;try{f.Stage(cancel.Token);}catch(OperationCanceledException){stopped=true;}Assert(stopped&&!Directory.Exists(Path.Combine(f.Install,"versions",f.Version)),"Cancelled download exposed a version");f.Handler.Before=null;Assert(Directory.Exists(f.Stage()),"Owned interrupted download did not recover");
            }
            using(var f=new Fixture(sandbox)){string stage=Path.Combine(f.Install,".update-stage");Directory.CreateDirectory(stage);string keep=Path.Combine(stage,"keep.txt");File.WriteAllText(keep,"foreign");Reject(()=>f.Stage());Assert(File.ReadAllText(keep)=="foreign"&&f.Handler.Requests.Count==0,"Unowned staging tree was removed or reused");}
            using(var f=new Fixture(sandbox)){Directory.CreateDirectory(f.Install);string keep=Path.Combine(f.Install,".update-download.json");File.WriteAllText(keep,"Unrecognized owner without an archive");string before=HashFile(keep);Reject(()=>f.Stage());Assert(HashFile(keep)==before&&f.Handler.Requests.Count==0,"Foreign cache marker was overwritten without a matching archive");}
            using(var f=new Fixture(sandbox)){string target=Path.Combine(f.Install,"versions",f.Version);Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"keep.txt"),"foreign");Reject(()=>f.Stage());Assert(File.ReadAllText(Path.Combine(target,"keep.txt"))=="foreign","Existing version was overwritten");}
        });
        check("updater reuses only identical Microsoft runtime hardlinks and never links application payloads",()=>{
            using(var f=new Fixture(sandbox)){byte[] runtime=File.ReadAllBytes(typeof(Enumerable).Assembly.Location);f.Files["System.FictionalRuntime.dll"]=runtime;f.Build();File.WriteAllBytes(Path.Combine(f.Current,"System.FictionalRuntime.dll"),runtime);File.WriteAllBytes(Path.Combine(f.Current,"CodexAccountSwitcher.dll"),f.Files["CodexAccountSwitcher.dll"]);string before=HashFile(Path.Combine(f.Current,"System.FictionalRuntime.dll"));string target=f.Stage();Assert(FileId(Path.Combine(f.Current,"System.FictionalRuntime.dll"))==FileId(Path.Combine(target,"System.FictionalRuntime.dll")),"Identical official Microsoft runtime was copied instead of reused");Assert(FileId(Path.Combine(f.Current,"CodexAccountSwitcher.dll"))!=FileId(Path.Combine(target,"CodexAccountSwitcher.dll")),"Application DLL was hardlinked");Assert(HashFile(Path.Combine(f.Current,"System.FictionalRuntime.dll"))==before,"Shared runtime source was rewritten");}
        });
        check("updater never truncates a hardlinked cache when recovering a partial download",()=>{
            using(var f=new Fixture(sandbox))using(var cancel=new CancellationTokenSource()){
                f.Handler.Before=url=>{if(url==f.ArchiveUrl)cancel.Cancel();};try{f.Stage(cancel.Token);}catch(OperationCanceledException){}f.Handler.Before=null;string source=Path.Combine(f.Root,"protected-source.txt");File.WriteAllText(source,"Protected original bytes");string archive=Path.Combine(f.Install,".update-download.zip");if(File.Exists(archive))File.Delete(archive);Assert(CreateHardLink(archive,source,IntPtr.Zero),"Could not prepare cache hardlink fixture");string before=HashFile(source);Assert(Directory.Exists(f.Stage())&&HashFile(source)==before,"Retry truncated the linked source");
            }
        });
    }
}
