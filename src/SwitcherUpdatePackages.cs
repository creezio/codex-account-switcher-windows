using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class SwitcherUpdateRelease
    {
        public string Version,ArchiveUrl,ArchiveSha256,ReleasePage;
        public long ArchiveBytes;
    }
    internal static class SwitcherUpdatePackages
    {
        private const string Repository="https://github.com/creezio/codex-account-switcher-windows";
        internal const string Catalogue="https://api.github.com/repos/creezio/codex-account-switcher-windows/releases?per_page=30";
        internal const long MaximumArchive=536870912,MaximumExpanded=1073741824,MaximumFile=268435456;
        internal const int MaximumManifest=2097152,MaximumFiles=5000;
        private static readonly Regex VersionPattern=new Regex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern=new Regex(@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant);
        private static readonly string[] Required={"CodexAccountSwitcher.exe","CodexAccountSwitcher.dll","CodexAccountSwitcher.deps.json","CodexAccountSwitcher.runtimeconfig.json","CreezioRelay.exe","CreezioRelay.exe.config","SwitcherLauncher.exe","SwitcherLauncher.exe.config","coreclr.dll","hostfxr.dll","hostpolicy.dll","PresentationFramework.dll","plugins/creezio-relay/.codex-plugin/plugin.json","plugins/creezio-relay/.mcp.json","plugins/creezio-relay/ui/viewer.html"};
        internal sealed class ManifestFile {public string path {get;set;} public long length {get;set;} public string sha256 {get;set;}}
        internal sealed class Manifest {public int schemaVersion {get;set;} public string version {get;set;} public string platform {get;set;} public int healthProtocol {get;set;} public string entrypoint {get;set;} public List<ManifestFile> files {get;set;}}
        private sealed class CacheOwner {public string Kind {get;set;} public string Root {get;set;} public string Version {get;set;} public string ArchiveSha256 {get;set;} public string ManifestSha256 {get;set;}}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateHardLink(string destination,string source,IntPtr security);
        private static InvalidOperationException Invalid(){return new InvalidOperationException("Le paquet de mise à jour ne correspond pas à une livraison Windows vérifiée. Aucun fichier actif n'a été remplacé.");}
        internal static void RequireVersion(string version)
        {
            if(version==null||version.Length>100||!VersionPattern.IsMatch(version))throw Invalid();
            string[] ignored;try{RelayMcpBootstrap.CompareVersion(version,version);}catch(OverflowException){throw Invalid();}
            // Numeric prerelease identifiers have their canonical SemVer spelling.
            var match=VersionPattern.Match(version);ignored=match.Groups[4].Value.Split('.');foreach(string part in ignored)if(part.Length>1&&part.All(Char.IsDigit)&&part[0]=='0')throw Invalid();
        }
        internal static int CompareVersions(string first,string second){RequireVersion(first);RequireVersion(second);return RelayMcpBootstrap.CompareVersion(first,second);}
        private static string ArchiveName(string version){return "CodexAccountSwitcher-"+version+"-windows-x64.zip";}
        private static Uri UriAllowed(string value)
        {
            Uri uri;if(value==null||value.Length>8192||!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||!new[]{"api.github.com","github.com","release-assets.githubusercontent.com","objects.githubusercontent.com"}.Contains(uri.DnsSafeHost,StringComparer.OrdinalIgnoreCase))throw Invalid();return uri;
        }
        private static void AssetUrl(string value,string tag,string name)
        {var uri=UriAllowed(value);if(uri.Host!="github.com"||uri.Query.Length!=0||Uri.UnescapeDataString(uri.AbsolutePath)!="/creezio/codex-account-switcher-windows/releases/download/"+tag+"/"+name)throw Invalid();}
        private static HttpClient Client()
        {
#if !NETCOREAPP
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
#endif
            return new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromMinutes(3)};
        }
        private static async Task<HttpResponseMessage> Response(HttpClient client,string value,CancellationToken token)
        {
            Uri uri=UriAllowed(value);
            for(int redirects=0;redirects<=4;redirects++){
                token.ThrowIfCancellationRequested();HttpResponseMessage response;
                using(var request=new HttpRequestMessage(HttpMethod.Get,uri)){
                    request.Headers.UserAgent.ParseAdd("Creezio-Switcher-Updates/1");request.Headers.Accept.ParseAdd(uri.Host=="api.github.com"?"application/vnd.github+json":"application/octet-stream");
                    response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
                }
                int code=(int)response.StatusCode;
                if(new[]{301,302,303,307,308}.Contains(code)){
                    Uri next=response.Headers.Location;response.Dispose();if(next==null||redirects==4)throw Invalid();uri=UriAllowed((next.IsAbsoluteUri?next:new Uri(uri,next)).AbsoluteUri);continue;
                }
                if(response.StatusCode!=HttpStatusCode.OK){response.Dispose();throw new InvalidOperationException("La publication GitHub n'est pas disponible. Réessayez plus tard ; l'installation actuelle est conservée.");}return response;
            }throw Invalid();
        }
        private static async Task CopyBounded(Stream source,Stream destination,long maximum,long expected,CancellationToken token)
        {
            byte[] bytes=new byte[65536];long count=0;int read;
            while((read=await source.ReadAsync(bytes,0,bytes.Length,token).ConfigureAwait(false))!=0){token.ThrowIfCancellationRequested();count+=read;if(count>maximum||expected>=0&&count>expected)throw Invalid();await destination.WriteAsync(bytes,0,read,token).ConfigureAwait(false);}
            if(expected>=0&&count!=expected)throw Invalid();
        }
        private static async Task<byte[]> RemoteBytes(HttpClient client,string url,int maximum,CancellationToken token)
        {
            using(var response=await Response(client,url,token).ConfigureAwait(false)){
                long? length=response.Content.Headers.ContentLength;if(length.HasValue&&length>maximum)throw Invalid();
                using(var source=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var memory=new MemoryStream()){await CopyBounded(source,memory,maximum,-1,token).ConfigureAwait(false);return memory.ToArray();}
            }
        }
        private static string Digest(byte[] bytes){using(var sha=SHA256.Create())return Hex(sha.ComputeHash(bytes));}
        private static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        private static string DigestFile(string path,long maximum,long expected=-1)
        {SafeFiles.RejectLinks(path);using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){if(stream.Length>maximum||expected>=0&&stream.Length!=expected)throw Invalid();using(var sha=SHA256.Create())return Hex(sha.ComputeHash(stream));}}
        private static bool Matches(string path,long length,string digest)
        {try{return DigestFile(path,MaximumFile,length)==digest;}catch(InvalidOperationException){return false;}}
        private static string AssetDigest(object asset){string value=Json.Str(Json.Get(asset,"digest"));if(!value.StartsWith("sha256:",StringComparison.Ordinal)||!HashPattern.IsMatch(value.Substring(7)))throw Invalid();return value.Substring(7);}
        private static string Utf8(byte[] bytes){try{return new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){throw Invalid();}}
        public static async Task<SwitcherUpdateRelease> Find(string installedVersion,CancellationToken token)
        {using(var client=Client())return await Find(installedVersion,token,client).ConfigureAwait(false);}
        internal static async Task<SwitcherUpdateRelease> Find(string installedVersion,CancellationToken token,HttpClient client)
        {using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){deadline.CancelAfter(TimeSpan.FromSeconds(45));return await FindCore(installedVersion,deadline.Token,client).ConfigureAwait(false);}}
        private static async Task<SwitcherUpdateRelease> FindCore(string installedVersion,CancellationToken token,HttpClient client)
        {
            RequireVersion(installedVersion);byte[] raw=await RemoteBytes(client,Catalogue,MaximumManifest,token).ConfigureAwait(false);string json=Utf8(raw);UniqueJson(json);var candidates=new List<Tuple<string,string,object,object,string>>();
            foreach(var release in RelayEngine.Rows(Json.Read<object>(json))){
                if(!Object.Equals(Json.Get(release,"draft"),false))continue;string tag=Json.Str(Json.Get(release,"tag_name")),version=tag.StartsWith("v",StringComparison.Ordinal)?tag.Substring(1):tag;
                try{if(CompareVersions(version,installedVersion)<=0)continue;}catch(InvalidOperationException){continue;}
                string archiveName=ArchiveName(version);var assets=RelayEngine.Rows(Json.Get(release,"assets")).ToArray();var archives=assets.Where(a=>Json.Str(Json.Get(a,"name"))==archiveName).ToArray();var sums=assets.Where(a=>new[]{"SHA256SUMS.txt","SHA256SUMS-"+version+".txt"}.Contains(Json.Str(Json.Get(a,"name")))).ToArray();
                if(archives.Length!=1||sums.Length!=1)continue;
                candidates.Add(Tuple.Create(version,tag,archives[0],sums[0],Repository+"/releases/tag/"+Uri.EscapeDataString(tag)));
            }
            if(candidates.Count==0)return null;var selected=candidates.OrderByDescending(c=>c.Item1,Comparer<string>.Create(CompareVersions)).First();string archiveUrl=Json.Str(Json.Get(selected.Item3,"browser_download_url")),sumsUrl=Json.Str(Json.Get(selected.Item4,"browser_download_url"));AssetUrl(archiveUrl,selected.Item2,ArchiveName(selected.Item1));AssetUrl(sumsUrl,selected.Item2,Json.Str(Json.Get(selected.Item4,"name")));
            string expected=AssetDigest(selected.Item3);double? size=Json.Number(Json.Get(selected.Item3,"size"));if(!size.HasValue||size<=0||size>MaximumArchive||size!=Math.Truncate(size.Value))throw Invalid();byte[] checksum=await RemoteBytes(client,sumsUrl,65536,token).ConfigureAwait(false);if(Digest(checksum)!=AssetDigest(selected.Item4))throw Invalid();string text=Utf8(checksum);var lines=text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);int matching=0;
            foreach(string line in lines){var match=Regex.Match(line,@"\A([0-9a-fA-F]{64})  ([A-Za-z0-9.+_-]+)\z",RegexOptions.CultureInvariant);if(!match.Success)throw Invalid();if(match.Groups[2].Value==ArchiveName(selected.Item1)){matching++;if(!String.Equals(match.Groups[1].Value,expected,StringComparison.OrdinalIgnoreCase))throw Invalid();}}
            if(matching!=1)throw Invalid();return new SwitcherUpdateRelease{Version=selected.Item1,ArchiveUrl=archiveUrl,ArchiveSha256=expected,ArchiveBytes=(long)size.Value,ReleasePage=selected.Item5};
        }
        private static string Relative(string value)
        {
            if(String.IsNullOrEmpty(value)||value.Length>220||value.IndexOf('\\')>=0||value.IndexOf(':')>=0||value.StartsWith("/",StringComparison.Ordinal))throw Invalid();
            foreach(string part in value.Split('/')){
                if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||part.IndexOfAny(new[]{'<','>','"','|','?','*'})>=0||part.Any(c=>c<32||c>126)||Regex.IsMatch(part,@"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))throw Invalid();
            }return value;
        }
        private static string Inside(string directory,string relative)
        {relative=Relative(relative);string root=Path.GetFullPath(directory).TrimEnd('\\')+"\\",path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase)||path.Length>245)throw Invalid();SafeFiles.RejectLinks(path);return path;}
        private static Manifest ReadManifest(byte[] bytes,string expectedVersion)
        {
            RequireVersion(expectedVersion);if(bytes.Length==0||bytes.Length>MaximumManifest)throw Invalid();string json=Utf8(bytes);UniqueJson(json);Manifest manifest;
            try{manifest=Json.Read<Manifest>(json);}catch(Exception error){if(error is OutOfMemoryException)throw;throw Invalid();}
            if(manifest==null||manifest.schemaVersion!=1||manifest.version!=expectedVersion||manifest.platform!="windows-x64"||manifest.healthProtocol!=1||manifest.entrypoint!="CodexAccountSwitcher.exe"||manifest.files==null||manifest.files.Count<Required.Length||manifest.files.Count>MaximumFiles)throw Invalid();
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            foreach(var file in manifest.files){if(file==null||Relative(file.path)=="switcher-update.json"||!paths.Add(file.path)||file.length<0||file.length>MaximumFile||file.sha256==null||!HashPattern.IsMatch(file.sha256))throw Invalid();total+=file.length;if(total>MaximumExpanded)throw Invalid();}
            foreach(string required in Required)if(!manifest.files.Any(file=>file.path==required))throw Invalid();
            foreach(string path in paths){string parent=path;while(parent.IndexOf('/')>=0){parent=parent.Substring(0,parent.LastIndexOf('/'));if(paths.Contains(parent))throw Invalid();}}
            return manifest;
        }
        private static IEnumerable<string> Files(string directory)
        {
            SafeFiles.RejectLinks(directory);var pending=new Stack<string>();pending.Push(directory);int count=0;
            while(pending.Count>0){string current=pending.Pop();foreach(string child in Directory.GetFileSystemEntries(current)){SafeFiles.RejectLinks(child);if(++count>MaximumFiles*3)throw Invalid();if(Directory.Exists(child))pending.Push(child);else yield return child;}}
        }
        public static string ValidateDirectory(string directory,string expectedVersion)
        {
            SafeFiles.RejectLinks(directory);string manifestPath=Inside(directory,"switcher-update.json");byte[] bytes;
            using(var input=new FileStream(manifestPath,FileMode.Open,FileAccess.Read,FileShare.Read)){if(input.Length>MaximumManifest)throw Invalid();using(var output=new MemoryStream()){input.CopyTo(output);bytes=output.ToArray();}}
            var manifest=ReadManifest(bytes,expectedVersion);var files=Files(directory).ToArray();if(files.Length!=manifest.files.Count+1)throw Invalid();var found=new HashSet<string>(files.Select(p=>p.Substring(Path.GetFullPath(directory).TrimEnd('\\').Length+1).Replace('\\','/')),StringComparer.OrdinalIgnoreCase);
            foreach(var file in manifest.files)if(!found.Contains(file.path)||DigestFile(Inside(directory,file.path),MaximumFile,file.length)!=file.sha256)throw Invalid();
            foreach(string name in new[]{"CodexAccountSwitcher.dll","CreezioRelay.exe"}){string version=FileVersionInfo.GetVersionInfo(Inside(directory,name)).ProductVersion;if(version!=expectedVersion)throw Invalid();}
            return Digest(bytes);
        }
        private static string Root(string directory)
        {string root=Path.GetFullPath(directory).TrimEnd('\\');if(root.Length<5||root.StartsWith("\\\\",StringComparison.Ordinal))throw Invalid();SafeFiles.RejectLinks(root);return root;}
        private static CacheOwner Owner(string root,SwitcherUpdateRelease release,string kind){return new CacheOwner{Kind=kind,Root=root,Version=release.Version,ArchiveSha256=release.ArchiveSha256};}
        private static bool SameOwner(string path,CacheOwner expected)
        {if(!File.Exists(path))return false;SafeFiles.RejectLinks(path);CacheOwner value;try{value=Json.Read<CacheOwner>(SafeFiles.ReadText(path));}catch{return false;}return value!=null&&value.Kind==expected.Kind&&value.Root==expected.Root&&value.Version==expected.Version&&value.ArchiveSha256==expected.ArchiveSha256;}
        private static void WriteOwner(string path,CacheOwner value){SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes(Json.Write(value)));}
        private static void RemoveFile(string path){SafeFiles.RejectLinks(path);if(File.Exists(path))using(new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose)){};}
        public static async Task<string> Stage(SwitcherUpdateRelease release,string installationRoot,string currentDirectory,CancellationToken token)
        {using(var client=Client())return await Stage(release,installationRoot,currentDirectory,token,client).ConfigureAwait(false);}
        internal static async Task<string> Stage(SwitcherUpdateRelease release,string installationRoot,string currentDirectory,CancellationToken token,HttpClient client)
        {using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){deadline.CancelAfter(TimeSpan.FromMinutes(5));return await StageCore(release,installationRoot,currentDirectory,deadline.Token,client).ConfigureAwait(false);}}
        private static async Task<string> StageCore(SwitcherUpdateRelease release,string installationRoot,string currentDirectory,CancellationToken token,HttpClient client)
        {
            if(release==null)throw Invalid();RequireVersion(release.Version);if(release.ArchiveSha256==null||!HashPattern.IsMatch(release.ArchiveSha256)||release.ArchiveBytes<=0||release.ArchiveBytes>MaximumArchive)throw Invalid();
            var source=UriAllowed(release.ArchiveUrl);string canonical=Repository+"/releases/download/";if(source.Host!="github.com"||source.Query.Length!=0||!source.AbsoluteUri.StartsWith(canonical,StringComparison.Ordinal))throw Invalid();string tag=Uri.UnescapeDataString(source.AbsolutePath.Split('/')[5]);if(tag!="v"+release.Version&&tag!=release.Version)throw Invalid();AssetUrl(release.ArchiveUrl,tag,ArchiveName(release.Version));
            string root=Root(installationRoot),versions=Inside(root,"versions"),target=Inside(versions,release.Version),stage=Inside(root,".update-stage"),stageOwner=Inside(root,".update-stage.json"),archivePath=Inside(root,".update-download.zip"),downloadOwner=Inside(root,".update-download.json"),proof=Inside(root,".update-version-"+release.Version+".json"),lease=Inside(root,".update-package.lock");
            token.ThrowIfCancellationRequested();Directory.CreateDirectory(root);SafeFiles.RejectLinks(root);
            using(new FileStream(lease,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
                var expected=Owner(root,release,"switcher-update-stage-v1");
                if(Directory.Exists(target)){
                    string digest=ValidateDirectory(target,release.Version);string ownerPath=SameOwner(proof,expected)?proof:stageOwner;if(!SameOwner(ownerPath,expected)||Json.Read<CacheOwner>(SafeFiles.ReadText(ownerPath)).ManifestSha256!=digest)throw Invalid();WriteOwner(proof,new CacheOwner{Kind=expected.Kind,Root=root,Version=release.Version,ArchiveSha256=release.ArchiveSha256,ManifestSha256=digest});if(SameOwner(stageOwner,expected)&&!Directory.Exists(stage))RemoveFile(stageOwner);if(SameOwner(downloadOwner,Owner(root,release,"switcher-update-download-v1"))){RemoveFile(archivePath);RemoveFile(downloadOwner);}return target;
                }
                if((Directory.Exists(stage)||File.Exists(stageOwner))&&!SameOwner(stageOwner,expected))throw new InvalidOperationException("Une préparation précédente doit être vérifiée avant de télécharger une autre mise à jour. L'installation actuelle est conservée.");
                long free=new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace;if(free<20L*1024*1024*1024)throw new InvalidOperationException("Au moins 20 Gio libres sont nécessaires pour préparer une mise à jour. Aucune application n'a été remplacée.");
                var download=Owner(root,release,"switcher-update-download-v1");if((File.Exists(archivePath)||File.Exists(downloadOwner))&&!SameOwner(downloadOwner,download))throw Invalid();WriteOwner(downloadOwner,download);
                bool cached=false;if(File.Exists(archivePath))try{cached=DigestFile(archivePath,MaximumArchive,release.ArchiveBytes)==release.ArchiveSha256;}catch(IOException){}catch(InvalidOperationException){}
                if(!cached){
                    using(var response=await Response(client,release.ArchiveUrl,token).ConfigureAwait(false)){
                        long? length=response.Content.Headers.ContentLength;if(length.HasValue&&length!=release.ArchiveBytes)throw Invalid();
                        // Unlink an owned partial cache first; never truncate a hard link.
                        RemoveFile(archivePath);using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(archivePath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){await CopyBounded(input,output,MaximumArchive,release.ArchiveBytes,token).ConfigureAwait(false);output.Flush(true);}
                    }
                    if(DigestFile(archivePath,MaximumArchive,release.ArchiveBytes)!=release.ArchiveSha256)throw Invalid();
                }
                token.ThrowIfCancellationRequested();if(!Directory.Exists(stage)){WriteOwner(stageOwner,expected);Directory.CreateDirectory(stage);}
                using(var input=new FileStream(archivePath,FileMode.Open,FileAccess.Read,FileShare.Read)){ZipDirectory(input);using(var archive=new ZipArchive(input,ZipArchiveMode.Read,false)){
                    if(archive.Entries.Count>MaximumFiles*2)throw Invalid();var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
                    foreach(var entry in archive.Entries){
                        bool directory=entry.FullName.EndsWith("/",StringComparison.Ordinal);string path=Relative(directory?entry.FullName.TrimEnd('/'):entry.FullName);Inside(stage,path);int mode=(entry.ExternalAttributes>>16)&0xf000;if(mode==0xa000||(entry.ExternalAttributes&(int)FileAttributes.ReparsePoint)!=0)throw Invalid();
                        if(directory){if(entry.Length!=0||!directories.Add(path)||entries.ContainsKey(path))throw Invalid();continue;}if(entries.ContainsKey(path)||directories.Contains(path)||entry.Length>MaximumFile||entry.Length<0)throw Invalid();entries.Add(path,entry);total+=entry.Length;if(total>MaximumExpanded)throw Invalid();
                    }
                    ZipArchiveEntry manifestEntry;if(!entries.TryGetValue("switcher-update.json",out manifestEntry)||manifestEntry.FullName!="switcher-update.json"||manifestEntry.Length>MaximumManifest)throw Invalid();byte[] manifestBytes;
                    using(var value=manifestEntry.Open())using(var output=new MemoryStream()){await CopyBounded(value,output,MaximumManifest,manifestEntry.Length,token).ConfigureAwait(false);manifestBytes=output.ToArray();}
                    var manifest=ReadManifest(manifestBytes,release.Version);if(entries.Count!=manifest.files.Count+1)throw Invalid();foreach(var directory in directories)if(!manifest.files.Any(file=>file.path.StartsWith(directory+"/",StringComparison.Ordinal)))throw Invalid();
                    foreach(var file in manifest.files){ZipArchiveEntry entry;if(!entries.TryGetValue(file.path,out entry)||entry.FullName!=file.path||entry.Length!=file.length)throw Invalid();}
                    foreach(string extra in Files(stage)){string path=extra.Substring(stage.Length+1).Replace('\\','/');if(path!="switcher-update.json"&&!manifest.files.Any(file=>file.path==path))throw Invalid();}
                    foreach(var file in manifest.files){token.ThrowIfCancellationRequested();string destination=Inside(stage,file.path);Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        if(File.Exists(destination)){if(Matches(destination,file.length,file.sha256))continue;RemoveFile(destination);}
                        bool linked=false;if(currentDirectory!=null&&RuntimeFile(file.path)){string previous=Inside(currentDirectory,file.path);if(File.Exists(previous)&&Matches(previous,file.length,file.sha256)&&String.Equals(FileVersionInfo.GetVersionInfo(previous).CompanyName,"Microsoft Corporation",StringComparison.Ordinal))linked=CreateHardLink(destination,previous,IntPtr.Zero);}
                        if(!linked)using(var value=entries[file.path].Open())using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None)){await CopyBounded(value,output,MaximumFile,file.length,token).ConfigureAwait(false);}
                        if(DigestFile(destination,MaximumFile,file.length)!=file.sha256)throw Invalid();
                    }
                    string stagedManifest=Inside(stage,"switcher-update.json");if(File.Exists(stagedManifest)){if(DigestFile(stagedManifest,MaximumManifest)!=Digest(manifestBytes))throw Invalid();}else using(var output=new FileStream(stagedManifest,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(manifestBytes,0,manifestBytes.Length);
                }}
                token.ThrowIfCancellationRequested();expected.ManifestSha256=ValidateDirectory(stage,release.Version);WriteOwner(stageOwner,expected);Directory.CreateDirectory(versions);SafeFiles.RejectLinks(versions);Directory.Move(stage,target);WriteOwner(proof,expected);RemoveFile(stageOwner);RemoveFile(archivePath);RemoveFile(downloadOwner);return target;
            }
        }
        private static bool RuntimeFile(string relative)
        {string name=Path.GetFileName(relative);return relative.IndexOf('/')<0&&name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&!name.StartsWith("CodexAccountSwitcher",StringComparison.OrdinalIgnoreCase)&&!name.StartsWith("Creezio",StringComparison.OrdinalIgnoreCase);}
        private static void ZipDirectory(Stream stream)
        {
            // Bound the central directory before ZipArchive materializes its entries.
            // Our sub-1-GiB Windows payloads never require ZIP64 or multi-disk ZIPs.
            if(stream.Length<22||stream.Length>MaximumArchive)throw Invalid();byte[] tail=new byte[(int)Math.Min(stream.Length,65557)];stream.Position=stream.Length-tail.Length;int read=0;
            while(read<tail.Length){int size=stream.Read(tail,read,tail.Length-read);if(size==0)throw Invalid();read+=size;}
            int end=-1;for(int at=tail.Length-22;at>=0;at--)if(BitConverter.ToUInt32(tail,at)==0x06054b50&&at+22+BitConverter.ToUInt16(tail,at+20)==tail.Length){end=at;break;}if(end<0)throw Invalid();
            int count=BitConverter.ToUInt16(tail,end+10);long offset=BitConverter.ToUInt32(tail,end+16),length=BitConverter.ToUInt32(tail,end+12),endAt=stream.Length-tail.Length+end;
            if(BitConverter.ToUInt16(tail,end+4)!=0||BitConverter.ToUInt16(tail,end+6)!=0||BitConverter.ToUInt16(tail,end+8)!=count||count<1||count>MaximumFiles*2||length>16777216||offset+length!=endAt)throw Invalid();
            stream.Position=offset;using(var reader=new BinaryReader(stream,Encoding.UTF8,true)){
                for(int index=0;index<count;index++){
                    if(stream.Position+46>endAt||reader.ReadUInt32()!=0x02014b50)throw Invalid();byte[] row=reader.ReadBytes(42);if(row.Length!=42)throw Invalid();int name=BitConverter.ToUInt16(row,24),extra=BitConverter.ToUInt16(row,26),comment=BitConverter.ToUInt16(row,28),method=BitConverter.ToUInt16(row,6);
                    if(name<1||name>221||extra>4096||comment>4096||BitConverter.ToUInt16(row,30)!=0||(BitConverter.ToUInt16(row,4)&1)!=0||method!=0&&method!=8||BitConverter.ToUInt32(row,20)>MaximumFile||stream.Position+name+extra+comment>endAt)throw Invalid();stream.Position+=name+extra+comment;
                }
                if(stream.Position!=endAt)throw Invalid();
            }stream.Position=0;
        }
        // Both JSON implementations otherwise accept duplicate object properties.
        // A bounded structural pass rejects ambiguous manifest/catalogue keys.
        private sealed class JsonKeys
        {
            private readonly string text;private int at,nodes;
            internal JsonKeys(string text){this.text=text;}
            private void White(){while(at<text.Length&&Char.IsWhiteSpace(text[at]))at++;}
            private string String(){int start=at;if(at>=text.Length||text[at++]!='"')throw Invalid();bool escaped=false;while(at<text.Length){char c=text[at++];if(escaped){escaped=false;continue;}if(c=='\\'){escaped=true;continue;}if(c=='"')return Json.Read<string>(text.Substring(start,at-start));}throw Invalid();}
            private void Value(int depth){White();if(depth>40||++nodes>100000||at>=text.Length)throw Invalid();char c=text[at];if(c=='{'){at++;White();var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);if(at<text.Length&&text[at]=='}'){at++;return;}while(true){White();if(!keys.Add(String()))throw Invalid();White();if(at>=text.Length||text[at++]!=':')throw Invalid();Value(depth+1);White();if(at>=text.Length)throw Invalid();char end=text[at++];if(end=='}')return;if(end!=',')throw Invalid();}}if(c=='['){at++;White();if(at<text.Length&&text[at]==']'){at++;return;}while(true){Value(depth+1);White();if(at>=text.Length)throw Invalid();char end=text[at++];if(end==']')return;if(end!=',')throw Invalid();}}if(c=='"'){String();return;}int start=at;while(at<text.Length&&!Char.IsWhiteSpace(text[at])&&text[at]!=','&&text[at]!=']'&&text[at]!='}')at++;if(start==at)throw Invalid();string scalar=text.Substring(start,at-start);if(scalar!="true"&&scalar!="false"&&scalar!="null"&&!Regex.IsMatch(scalar,@"\A-?(0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z",RegexOptions.CultureInvariant))throw Invalid();}
            internal void Check(){Value(0);White();if(at!=text.Length)throw Invalid();}
        }
        private static void UniqueJson(string text){try{new JsonKeys(text).Check();}catch(Exception error){if(error is OutOfMemoryException)throw;throw Invalid();}}
    }
}
