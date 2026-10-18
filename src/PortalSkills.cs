using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    public sealed class PortalSkillSaveRequest
    {public string Name {get;set;}public string Document {get;set;}public string ExpectedRevision {get;set;}public string SourceSkillId {get;set;}}
    public sealed class PortalSkillImportRequest
    {public string ArchiveBase64 {get;set;}public string Name {get;set;}public string ExpectedRevision {get;set;}}
    public sealed class PortalSkillDeployRequest
    {public string SourceInstanceId {get;set;}public string SkillId {get;set;}public string SourceRevision {get;set;}public string Name {get;set;}public string ExpectedRevision {get;set;}}
    public sealed class PortalSkillTarget
    {public string InstanceId {get;set;}public string AccountKey {get;set;}public string Home {get;set;}public long GrantRevision {get;set;}public long PolicyRevision {get;set;}public string SiteOrigin {get;set;}}
    public sealed class PortalSkillOperation
    {
        public string Id {get;set;}public string Route {get;set;}public string Actor {get;set;}public PortalSkillTarget Target {get;set;}
        public string RequestHash {get;set;}public string Name {get;set;}public string ExpectedRevision {get;set;}public string DesiredRevision {get;set;}
        public string SourceId {get;set;}public string CustomizedFrom {get;set;}public string State {get;set;}public string UpdatedUtc {get;set;}
        public PortalSkillTarget SourceTarget {get;set;}public string SourceRevision {get;set;}
    }
    internal sealed class PortalSkillDependencies
    {
        internal Func<string,string,string,PortalSkillTarget> Resolve=null;
        internal string SharedSkillsRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".agents","skills");
        internal string BuiltinSkillsRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugins","creezio-relay","skills");
        internal Action BeforeCommit=null;
    }
    // Pure file/catalog operations: no model, chat, CLI, app-server or plugin
    // execution. Native caches are sources only; writes always target HOME/skills.
    internal sealed class PortalSkillService
    {
        internal const int MaximumDocument=65536,MaximumReadDocument=262144,MaximumArchive=2097152,MaximumExpanded=8388608,MaximumFile=1048576,MaximumFiles=128;
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        private readonly RelayStore store;private readonly string root;private readonly Action<string,string> require;private readonly PortalSkillDependencies dependencies;
        [StructLayout(LayoutKind.Sequential)]private struct FileIdentity{public uint Attributes,CreateLow,CreateHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileIdentity info);
        private sealed class Skill
        {internal string Id,Name,Description,Path,Origin,Scope,Plugin,Revision,Issue,Overrides;internal bool Installed,Enabled,Editable;}
        private sealed class Package
        {internal string Name,Description,Revision,SourceRevision,CustomizedFrom;internal PortalSkillTarget SourceTarget;internal Dictionary<string,byte[]> Files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);}
        internal PortalSkillService(RelayStore value,Action<string,string> authorize=null,PortalSkillDependencies adapters=null)
        {store=value;root=Path.GetDirectoryName(value.Root);require=authorize??((route,actor)=>PortalAdministration.RequireActor(store,route,actor));dependencies=adapters??new PortalSkillDependencies();}
        private static WebhookException Bad(){return new WebhookException(400,"Skill invalide : verifiez le nom, le frontmatter name/description et les limites du paquet.");}
        private static WebhookException Conflict(){return new WebhookException(409,"Le skill, le compte ou ses autorisations ont change. Actualisez avant de reprendre.");}
        private static bool ValidInstance(string id){return id=="local"||Regex.IsMatch(id??"",@"\A[a-f0-9]{32}\z");}
        private static bool ValidName(string name){return name!=null&&name.Length<=64&&Regex.IsMatch(name,@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")&&!Regex.IsMatch(name,@"\A(?:con|prn|aux|nul|com[1-9]|lpt[1-9])\z",RegexOptions.IgnoreCase);}
        private static bool HashValue(string value){return Regex.IsMatch(value??"",@"\A[a-f0-9]{64}\z");}
        private static bool Within(string path,string directory){return Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\','/')+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
        private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        private static string Id(string name,string path){return ToolTunnel.Hash(new{kind="skill",name=name,path=Path.GetFullPath(path)});}
        private PortalSkillTarget Resolve(string route,string actor,string instance,bool local)
        {
            if(!ValidInstance(instance))throw Bad();if(!local)require(route,actor);
            if(dependencies.Resolve!=null){var supplied=dependencies.Resolve(route,actor,instance);if(supplied==null||supplied.InstanceId!=instance||String.IsNullOrEmpty(supplied.AccountKey)||!Path.IsPathRooted(supplied.Home))throw Conflict();SafeFiles.RejectLinks(supplied.Home);return supplied;}
            var accounts=new AccountService(root);var selected=NamedInstances.Resolve(accounts,instance);NamedInstances.VerifyAccount(accounts,selected);
            string home=accounts.Instances.Home(selected);SafeFiles.RejectLinks(home);if(!Directory.Exists(home))throw Conflict();
            var result=new PortalSkillTarget{InstanceId=selected.Id,AccountKey=selected.AccountKey,Home=Path.GetFullPath(home)};
            if(!local){var policy=PortalAdministration.Policy(store);var config=Portal.Config(store,route);var grant=config.Instances.SingleOrDefault(g=>g.InstanceId==selected.Id);
                if(grant==null)throw new WebhookException(404,"Cette instance n'est pas autorisee sur le portail.");
                if(!selected.IsLocal&&!selected.AccountLocked||grant.AccountKey!=selected.AccountKey||!RelayStore.SamePath(grant.Home,home))throw Conflict();
                result.GrantRevision=grant.AuthorizationRevision;result.PolicyRevision=policy.Revision;result.SiteOrigin=policy.SiteOrigin;}
            return result;
        }
        private static bool Same(PortalSkillTarget a,PortalSkillTarget b){return a!=null&&b!=null&&a.InstanceId==b.InstanceId&&a.AccountKey==b.AccountKey&&RelayStore.SamePath(a.Home,b.Home)&&a.GrantRevision==b.GrantRevision&&a.PolicyRevision==b.PolicyRevision&&a.SiteOrigin==b.SiteOrigin;}
        private void Verify(string route,string actor,PortalSkillTarget target,bool local){if(!Same(target,Resolve(route,actor,target.InstanceId,local)))throw Conflict();}
        private static string Scalar(string value)
        {
            value=value.Trim();if(value.Length==0)throw Bad();
            if(value[0]=='\''){if(value.Length<2||value[value.Length-1]!='\''||value.Substring(1,value.Length-2).Replace("''","").Contains("'"))throw Bad();return value.Substring(1,value.Length-2).Replace("''","'");}
            if(value[0]=='"'){try{return Json.Read<string>(value);}catch(Exception){throw Bad();}}
            value=Regex.Replace(value,@"\s+#.*$","").Trim();
            if(value.Length==0||"!&*[{".IndexOf(value[0])>=0||value.Contains(": ")||new[]{"true","false","null","~"}.Contains(value,StringComparer.OrdinalIgnoreCase))throw Bad();return value;
        }
        private static KeyValuePair<string,string> Metadata(string document,bool strict)
        {
            if(document==null||document.IndexOf('\0')>=0||strict&&document.Any(c=>Char.IsControl(c)&&c!='\r'&&c!='\n'&&c!='\t'))throw Bad();try{if(Utf8.GetByteCount(document)>(strict?MaximumDocument:MaximumReadDocument))throw Bad();}catch(EncoderFallbackException){throw Bad();}
            string normalized=document.TrimStart('\uFEFF').Replace("\r\n","\n");var lines=normalized.Split('\n');if(lines.Length<4||lines[0]!="---")throw Bad();
            int end=Array.IndexOf(lines,"---",1);if(end<2||end>160)throw Bad();if(strict)PortableFrontmatter(lines,end);var fields=new Dictionary<string,string>(StringComparer.Ordinal);var all=new HashSet<string>(StringComparer.Ordinal);
            for(int i=1;i<end;i++){
                string line=lines[i];if(line.Trim().Length==0||line.TrimStart().StartsWith("#",StringComparison.Ordinal))continue;
                if(Char.IsWhiteSpace(line[0])){if(line[0]=='\t')throw Bad();continue;}
                var match=Regex.Match(line,@"\A([a-zA-Z][a-zA-Z0-9_-]*):(?:[ \t]+(.*)|[ \t]*)\z");if(!match.Success||!all.Add(match.Groups[1].Value))throw Bad();
                string key=match.Groups[1].Value,value=match.Groups[2].Value;
                if(strict&&!new[]{"name","description","license","allowed-tools","metadata","compatibility"}.Contains(key))throw Bad();
                if(key!="name"&&key!="description")continue;
                if(Regex.IsMatch(value,@"\A[|>][+-]?\z")){
                    if(key=="name")throw Bad();var parts=new List<string>();while(i+1<end&&(lines[i+1].Length==0||Char.IsWhiteSpace(lines[i+1][0]))){i++;if(lines[i].Contains("\t"))throw Bad();parts.Add(lines[i].Trim());}value=String.Join(" ",parts).Trim();
                }else value=Scalar(value);
                fields.Add(key,value);
            }
            string name,description;if(!fields.TryGetValue("name",out name)||!fields.TryGetValue("description",out description)||String.IsNullOrWhiteSpace(name)||name.Length>160||name.Any(Char.IsControl)||strict&&!ValidName(name)||String.IsNullOrWhiteSpace(description)||description.Length>(strict?1024:6000)||strict&&(description.Contains("<")||description.Contains(">")||description.StartsWith("[TODO:",StringComparison.Ordinal))||String.IsNullOrWhiteSpace(String.Join("\n",lines.Skip(end+1))))throw Bad();
            return new KeyValuePair<string,string>(name,description);
        }
        private static void PortableFrontmatter(string[] lines,int end)
        {
            // Validate the portable YAML forms we can preserve without loading
            // an executable parser or adding a runtime dependency. Unknown YAML
            // constructs fail before deployment instead of creating a broken skill.
            bool metadata=false,nest=false;int previous=0,literal=-1;var names=new Dictionary<int,HashSet<string>>();
            for(int i=1;i<end;i++){
                string line=lines[i];if(String.IsNullOrWhiteSpace(line)||line.TrimStart().StartsWith("#",StringComparison.Ordinal))continue;if(line.Contains("\t"))throw Bad();int indent=line.TakeWhile(c=>c==' ').Count();
                if(literal>=0&&indent>literal)continue;literal=-1;var pair=Regex.Match(line.Substring(indent),@"\A([A-Za-z0-9_-]+):(?:[ ]+(.*)|[ ]*)\z");if(!pair.Success)throw Bad();string key=pair.Groups[1].Value,value=pair.Groups[2].Value.Trim();
                if(indent==0){metadata=key=="metadata";previous=0;nest=metadata&&value.Length==0;names.Clear();
                    if(metadata){if(value.Length>0&&value!="{}")throw new WebhookException(400,"Le frontmatter metadata doit etre un dictionnaire YAML indente. Les syntaxes YAML avancees ne sont pas prises en charge.");continue;}
                    if(Regex.IsMatch(value,@"\A[|>][+-]?\z")){literal=0;continue;}
                    if(key=="allowed-tools"&&value.StartsWith("[",StringComparison.Ordinal)){try{var tools=Json.Read<string[]>(value);if(tools==null||tools.Length>128||tools.Any(t=>String.IsNullOrWhiteSpace(t)))throw Bad();}catch(Exception){throw new WebhookException(400,"Utilisez une liste JSON de textes pour allowed-tools, ou une valeur YAML simple.");}continue;}
                    Scalar(value);continue;
                }
                if(!metadata||indent>16||indent%2!=0||indent>previous&&(!nest||indent!=previous+2))throw new WebhookException(400,"Le frontmatter contient une indentation ou une syntaxe YAML non prise en charge. Utilisez des champs simples et metadata indente de deux espaces.");
                if(indent>previous)names[indent]=new HashSet<string>(StringComparer.Ordinal);else foreach(int level in names.Keys.Where(n=>n>indent).ToArray())names.Remove(level);
                HashSet<string> siblings;if(!names.TryGetValue(indent,out siblings)||!siblings.Add(key))throw Bad();previous=indent;nest=value.Length==0;
                if(nest)continue;if(Regex.IsMatch(value,@"\A[|>][+-]?\z")){literal=indent;continue;}
                if(!Regex.IsMatch(value,@"\A(?:true|false|null|~|[-+]?[0-9]+(?:\.[0-9]+)?)\z",RegexOptions.IgnoreCase))Scalar(value);
            }
        }
        private static string FileName(string path)
        {
            if(String.IsNullOrEmpty(path)||path.Length>200||path.Contains("\\")||path.StartsWith("/",StringComparison.Ordinal)||path.Any(c=>c<32)||path.IndexOfAny(new[]{':','*','?','"','<','>','|'})>=0)throw Bad();
            foreach(var part in path.Split('/'))if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||Regex.IsMatch(part,@"\A(?:CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|\z)",RegexOptions.IgnoreCase)||part.StartsWith(".env.",StringComparison.OrdinalIgnoreCase)||new[]{".git",".ssh",".env","auth.json","credentials.json","secrets.json","accounts.dpapi","config.toml"}.Contains(part,StringComparer.OrdinalIgnoreCase))throw Bad();return path;
        }
        private static byte[] ReadBytes(string path,int maximum)
        {
            SafeFiles.RejectLinks(path);using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
                FileIdentity identity;if(!GetFileInformationByHandle(input.SafeFileHandle,out identity)||identity.Links!=1||(identity.Attributes&0x400)!=0)throw new IOException("Identite du fichier de skill non verifiable.");
                if(input.Length>maximum)throw Bad();var bytes=new byte[(int)input.Length];int at=0,count;while(at<bytes.Length&&(count=input.Read(bytes,at,bytes.Length-at))>0)at+=count;if(at!=bytes.Length||input.ReadByte()!=-1)throw Conflict();return bytes;
            }
        }
        private static string ReadDocument(string path){return Utf8.GetString(ReadBytes(path,MaximumReadDocument));}
        private static string Revision(Dictionary<string,byte[]> files)
        {return Webhooks.Hash(String.Join("\n",files.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"\n"+p.Value.Length+"\n"+Hash(p.Value))));}
        private static Package Complete(Dictionary<string,byte[]> files,string name=null)
        {
            byte[] doc;if(files.Count==0||files.Count>MaximumFiles||!files.TryGetValue("SKILL.md",out doc)||!files.Keys.Contains("SKILL.md",StringComparer.Ordinal)||files.Values.Sum(b=>(long)b.Length)>MaximumExpanded||files.Values.Any(b=>b.Length>MaximumFile))throw Bad();
            foreach(string path in files.Keys){FileName(path);if(files.Keys.Any(other=>other!=path&&other.StartsWith(path+"/",StringComparison.OrdinalIgnoreCase)))throw Bad();}
            string document;try{document=Utf8.GetString(doc);}catch(DecoderFallbackException){throw Bad();}var metadata=Metadata(document,true);if(name!=null&&name!=metadata.Key)throw Bad();
            return new Package{Name=metadata.Key,Description=metadata.Value,Files=files,Revision=Revision(files)};
        }
        private static Package ReadPackage(string folder,bool strict=true)
        {
            SafeFiles.RejectLinks(folder);if(!Directory.Exists(folder))throw Conflict();var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);long total=0;
            var pending=new Stack<string>();pending.Push(folder);int entries=0;
            while(pending.Count>0){string directory=pending.Pop();foreach(string path in Directory.EnumerateFileSystemEntries(directory)){
                if(++entries>MaximumFiles*3)throw Bad();SafeFiles.RejectLinks(path);string relative=path.Substring(folder.TrimEnd('\\','/').Length+1).Replace('\\','/');FileName(relative);
                if(Directory.Exists(path)){pending.Push(path);continue;}var info=new FileInfo(path);if(info.Length>MaximumFile||(total+=info.Length)>MaximumExpanded||files.Count>=MaximumFiles)throw Bad();
                byte[] value=ReadBytes(path,MaximumFile);if(value.Length!=info.Length)throw Conflict();
                if(files.ContainsKey(relative))throw Bad();files.Add(relative,value);
            }}
            if(strict)return Complete(files);byte[] document;if(!files.TryGetValue("SKILL.md",out document))throw Bad();var metadata=Metadata(Utf8.GetString(document),false);return new Package{Name=metadata.Key,Description=metadata.Value,Files=files,Revision=Revision(files)};
        }
        private List<Skill> Scan(PortalSkillTarget target,List<string> warnings)
        {
            var result=new List<Skill>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int visited=0;
            Action<string,string,string,string,bool,bool> scan=(folder,origin,scope,plugin,installed,enabled)=>{
                if(String.IsNullOrEmpty(folder)||!Directory.Exists(folder))return;
                try{SafeFiles.RejectLinks(folder);var pending=new Stack<KeyValuePair<string,int>>();pending.Push(new KeyValuePair<string,int>(folder,0));
                    while(pending.Count>0){var current=pending.Pop();if(++visited>12000||result.Count>=2000)throw new IOException("skill-catalog-limit");SafeFiles.RejectLinks(current.Key);string path=Path.Combine(current.Key,"SKILL.md");
                        if(File.Exists(path)){
                            if(!seen.Add(Path.GetFullPath(path)))continue;var row=new Skill{Path=path,Origin=origin,Scope=scope,Plugin=plugin,Installed=installed,Enabled=enabled,Editable=origin=="user"&&scope=="instance"};
                            try{var meta=Metadata(ReadDocument(path),false);row.Name=meta.Key;row.Description=meta.Value;row.Id=Id(row.Name,path);try{row.Revision=ReadPackage(current.Key,false).Revision;}catch(IOException){row.Issue="Le paquet ne peut pas etre personnalise en toute securite.";}catch(WebhookException){row.Issue="Ce paquet depasse les limites d'edition ou contient un fichier non importable.";}}
                            catch(IOException){row.Name=Path.GetFileName(current.Key);row.Description="Skill inaccessible";row.Id=Id(row.Name,path);row.Issue="Fichier du skill indisponible.";}
                            catch(WebhookException){row.Name=Path.GetFileName(current.Key);row.Description="Frontmatter a verifier";row.Id=Id(row.Name,path);row.Issue="Format du skill non pris en charge pour l'edition.";}
                            catch(DecoderFallbackException){row.Name=Path.GetFileName(current.Key);row.Description="Encodage a verifier";row.Id=Id(row.Name,path);row.Issue="Le document n'est pas un fichier UTF-8 valide.";}
                            result.Add(row);continue;
                        }
                        if(current.Value>=7)continue;foreach(string child in Directory.EnumerateDirectories(current.Key).Take(2001)){
                            if(new[]{"scripts","assets","references","node_modules",".git"}.Contains(Path.GetFileName(child)))continue;pending.Push(new KeyValuePair<string,int>(child,current.Value+1));}
                    }
                }catch(IOException){warnings.Add("Une source de skills est inaccessible ou depasse les limites du catalogue.");}catch(UnauthorizedAccessException){warnings.Add("Windows refuse la lecture d'une source de skills.");}
            };
            string user=Path.Combine(target.Home,"skills");if(Directory.Exists(user)){SafeFiles.RejectLinks(user);foreach(string dir in Directory.EnumerateDirectories(user).Take(2001))scan(dir,Path.GetFileName(dir)==".system"?"system":"user","instance",null,true,true);}
            scan(dependencies.SharedSkillsRoot,"user","shared",null,true,true);
            string cache=Path.Combine(target.Home,"plugins","cache");bool relayFound=false;
            string relayVersion=store.ReadRecord<RelayIntegrationState>("integration-"+RelayIntegration.HomeKey(target.Home)+".dpapi").InstalledVersion;
            if(Directory.Exists(cache))try{SafeFiles.RejectLinks(cache);foreach(string market in Directory.EnumerateDirectories(cache).Take(64)){
                SafeFiles.RejectLinks(market);foreach(string plugin in Directory.EnumerateDirectories(market).Take(512)){
                    SafeFiles.RejectLinks(plugin);string pluginName=Path.GetFileName(plugin),pluginId=pluginName+"@"+Path.GetFileName(market);var versions=new DirectoryInfo(plugin).EnumerateDirectories().Take(64).ToArray();
                    var selected=versions.FirstOrDefault(v=>pluginName=="creezio-relay"&&v.Name==relayVersion)??versions.OrderByDescending(v=>v.Name=="latest").ThenByDescending(v=>v.LastWriteTimeUtc).FirstOrDefault();if(selected==null)continue;
                    bool? configured=PluginEnabled(target.Home,pluginId);bool installed=configured.HasValue&&(versions.Length==1||selected.Name=="latest")||pluginName=="creezio-relay"&&selected.Name==relayVersion;
                    scan(Path.Combine(selected.FullName,"skills"),"plugin",installed?"instance":"cache",pluginId,installed,installed&&configured!=false);
                    if(!installed)foreach(var row in result.Where(r=>r.Plugin==pluginId&&r.Issue==null))row.Issue="Copie en cache ; installation active non confirmee.";
                    if(pluginName=="creezio-relay"&&result.Any(r=>r.Plugin==pluginId))relayFound=true;
                }}
            }catch(IOException){warnings.Add("Le cache de plugins est partiellement inaccessible.");}catch(UnauthorizedAccessException){warnings.Add("Windows refuse la lecture du cache de plugins.");}
            if(!relayFound)scan(dependencies.BuiltinSkillsRoot,"plugin","bundled","creezio-relay@creezio-switcher",false,false);
            foreach(var row in result.Where(r=>r.Editable)){var binding=store.ReadRecord<PortalSkillOperation>("portal-skill-binding-"+Webhooks.Hash(target.Home.ToUpperInvariant()+"\n"+row.Name).Substring(0,32)+".dpapi");if(binding.Target!=null&&binding.Target.InstanceId==target.InstanceId&&binding.Target.AccountKey==target.AccountKey&&RelayStore.SamePath(binding.Target.Home,target.Home))row.Overrides=binding.CustomizedFrom??(binding.SourceId!=row.Id?binding.SourceId:null);}
            return result.OrderBy(r=>r.Name,StringComparer.OrdinalIgnoreCase).ThenBy(r=>r.Origin).ToList();
        }
        private static bool? PluginEnabled(string home,string plugin)
        {
            string file=Path.Combine(home,"config.toml");if(!File.Exists(file))return null;bool found=false;try{string section=null;foreach(string line in SafeFiles.ReadText(file).Replace("\r","").Split('\n')){
                var header=Regex.Match(line.Trim(),@"\A\[plugins\.""([^""]+)""\]\z");if(line.TrimStart().StartsWith("[",StringComparison.Ordinal))section=header.Success?header.Groups[1].Value:null;
                if(section==plugin)found=true;
                if(section==plugin&&Regex.IsMatch(line,@"\A\s*enabled\s*=\s*false\s*(?:#.*)?\z"))return false;
            }}catch(IOException){return null;}return found?(bool?)true:null;
        }
        private static object View(Skill row){return new{id=row.Id,name=row.Name,description=row.Description,origin=row.Origin,scope=row.Scope,pluginId=row.Plugin,revision=row.Revision,editable=row.Editable&&row.Revision!=null,canCustomize=row.Revision!=null,installed=row.Installed,enabled=row.Enabled,overridesId=row.Overrides,issue=row.Issue};}
        private Skill Find(PortalSkillTarget target,string id)
        {if(!HashValue(id))throw Bad();var rows=Scan(target,new List<string>());var row=rows.SingleOrDefault(r=>r.Id==id);if(row==null)throw new WebhookException(404,"Skill introuvable dans le catalogue de cette instance.");return row;}
        private object CatalogCore(string route,string actor,string instance,bool local)
        {var target=Resolve(route,actor,instance,local);var warnings=new List<string>();var rows=Scan(target,warnings);Verify(route,actor,target,local);return new{instanceId=target.InstanceId,skills=rows.Select(View).ToArray(),warnings=warnings.Distinct().ToArray(),limits=new{documentBytes=MaximumDocument,readDocumentBytes=MaximumReadDocument,archiveBytes=MaximumArchive,expandedBytes=MaximumExpanded,files=MaximumFiles},refreshRequired=false};}
        internal object Catalog(string route,string actor,string instance){return CatalogCore(route,actor,instance,false);}
        internal object CatalogLocal(string instance){return CatalogCore("desktop","desktop",instance,true);}
        private object ReadCore(string route,string actor,string instance,string id,bool local)
        {var target=Resolve(route,actor,instance,local);var row=Find(target,id);string document;object[] files;bool truncated=false;
            if(row.Revision!=null){var package=ReadPackage(Path.GetDirectoryName(row.Path),false);if(package.Revision!=row.Revision)throw Conflict();document=Utf8.GetString(package.Files["SKILL.md"]);files=package.Files.OrderBy(p=>p.Key).Select(p=>(object)new{path=p.Key,bytes=(long)p.Value.Length}).ToArray();}
            else{document=ReadDocument(row.Path);var metadata=new List<object>();var pending=new Stack<string>();string folder=Path.GetDirectoryName(row.Path);pending.Push(folder);int visited=0;
                while(pending.Count>0&&metadata.Count<MaximumFiles){foreach(string entry in Directory.EnumerateFileSystemEntries(pending.Pop())){if(++visited>MaximumFiles*3){truncated=true;break;}SafeFiles.RejectLinks(entry);if(Directory.Exists(entry)){pending.Push(entry);continue;}metadata.Add(new{path=entry.Substring(folder.Length+1).Replace('\\','/'),bytes=new FileInfo(entry).Length});if(metadata.Count>=MaximumFiles){truncated=true;break;}}if(visited>MaximumFiles*3)break;}files=metadata.ToArray();}
            Verify(route,actor,target,local);return new{instanceId=instance,skill=View(row),document=document,files=files,filesTruncated=truncated};}
        internal object Read(string route,string actor,string instance,string id){return ReadCore(route,actor,instance,id,false);}
        internal object ReadLocal(string instance,string id){return ReadCore("desktop","desktop",instance,id,true);}
        private static string OperationId(string route,string actor,string instance,string key)
        {if(String.IsNullOrEmpty(key)||key.Length>128||key.Any(c=>c<33||c>126))throw Bad();return Webhooks.Hash("skill-v1\n"+route+"\n"+actor+"\n"+instance+"\n"+key).Substring(0,32);}
        private static string OpName(string id){return "portal-skill-operation-"+id+".dpapi";}
        private object Mutate(string route,string actor,string instance,string key,object request,bool local,Func<PortalSkillTarget,Package> build,string expected,string sourceId,Func<PortalSkillTarget,string> expectedTarget=null)
        {
            string id=OperationId(route,actor,instance,key),requestHash=ToolTunnel.Hash(request);var initial=Resolve(route,actor,instance,local);
            using(VaultMutation.Acquire(root,CancellationToken.None))using(store.Lease("portal-skill-home-"+RelayIntegration.HomeKey(initial.Home))){
                Verify(route,actor,initial,local);var operation=store.ReadRecord<PortalSkillOperation>(OpName(id));
                if(operation.Id!=null){if(operation.Id!=id||operation.Route!=route||operation.Actor!=actor||operation.RequestHash!=requestHash||!Same(operation.Target,initial))throw Conflict();return Commit(route,actor,operation,local);}
                var package=build(initial);if(expectedTarget!=null)expected=expectedTarget(initial);if(!ValidName(package.Name))throw Bad();string target=Path.Combine(initial.Home,"skills",package.Name);SafeFiles.RejectLinks(target);
                string actual=Directory.Exists(target)?ReadPackage(target,false).Revision:null;if(File.Exists(target)||(String.IsNullOrEmpty(expected)?actual!=null:actual!=expected))throw Conflict();
                string temporary=Path.Combine(initial.Home,".switcher-skill-stage");SafeFiles.PrivateDirectory(temporary);string stage=Path.Combine(temporary,id);if(Directory.Exists(stage)||File.Exists(stage)||Directory.Exists(stage+"-backup")||File.Exists(stage+"-backup"))throw new WebhookException(409,"Une preparation de skill doit etre verifiee avant de reprendre.");
                operation=new PortalSkillOperation{Id=id,Route=route,Actor=actor,Target=initial,RequestHash=requestHash,Name=package.Name,ExpectedRevision=expected,DesiredRevision=package.Revision,SourceId=sourceId,CustomizedFrom=package.CustomizedFrom,SourceTarget=package.SourceTarget,SourceRevision=package.SourceRevision,State="preparing",UpdatedUtc=DateTime.UtcNow.ToString("o")};store.WriteRecord(OpName(id),operation);
                SafeFiles.PrivateDirectory(stage);foreach(var file in package.Files){string destination=Path.Combine(stage,file.Key.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(destination));SafeFiles.AtomicWrite(destination,file.Value);}
                if(ReadPackage(stage).Revision!=package.Revision)throw Conflict();operation.State="prepared";store.WriteRecord(OpName(id),operation);return Commit(route,actor,operation,local);
            }
        }
        private object Commit(string route,string actor,PortalSkillOperation operation,bool local)
        {
            Verify(route,actor,operation.Target,local);string parent=Path.Combine(operation.Target.Home,"skills"),target=Path.Combine(parent,operation.Name),temporary=Path.Combine(operation.Target.Home,".switcher-skill-stage"),stage=Path.Combine(temporary,operation.Id),backup=stage+"-backup";
            foreach(string path in new[]{parent,target,temporary,stage,backup})SafeFiles.RejectLinks(path);
            if(operation.State=="preparing")throw new WebhookException(409,"Preparation interrompue : aucun skill existant n'a ete remplace. Reprenez avec une nouvelle cle apres verification locale.");
            string actual=Directory.Exists(target)?ReadPackage(target,false).Revision:null;
            if(actual!=operation.DesiredRevision){
                if(operation.State=="deployed"||!Directory.Exists(stage)||ReadPackage(stage).Revision!=operation.DesiredRevision)throw Conflict();
                if(Directory.Exists(backup)){if(actual!=null||String.IsNullOrEmpty(operation.ExpectedRevision)||ReadPackage(backup,false).Revision!=operation.ExpectedRevision)throw Conflict();}
                else if(String.IsNullOrEmpty(operation.ExpectedRevision)?actual!=null:actual!=operation.ExpectedRevision)throw Conflict();
                if(dependencies.BeforeCommit!=null)dependencies.BeforeCommit();Verify(route,actor,operation.Target,local);
                if(operation.SourceTarget!=null){Verify(route,actor,operation.SourceTarget,local);
                    bool sourceInBackup=Same(operation.SourceTarget,operation.Target)&&operation.SourceId==Id(operation.Name,Path.Combine(target,"SKILL.md"))&&actual==null&&Directory.Exists(backup)&&ReadPackage(backup,false).Revision==operation.SourceRevision;
                    if(!sourceInBackup){var source=Find(operation.SourceTarget,operation.SourceId);if(source.Revision!=operation.SourceRevision)throw Conflict();}}
                if((Directory.Exists(target)?ReadPackage(target,false).Revision:null)!=actual)throw Conflict();
                Directory.CreateDirectory(parent);
                if(actual!=null)Directory.Move(target,backup);
                try{Directory.Move(stage,target);}catch{if(!Directory.Exists(target)&&Directory.Exists(backup))Directory.Move(backup,target);throw;}
                if(ReadPackage(target).Revision!=operation.DesiredRevision)throw Conflict();
            }
            if(Directory.Exists(stage)){if(ReadPackage(stage).Revision!=operation.DesiredRevision)throw Conflict();SafeFiles.DeleteOwnedTree(temporary,stage);}
            if(Directory.Exists(backup)){if(String.IsNullOrEmpty(operation.ExpectedRevision)||ReadPackage(backup,false).Revision!=operation.ExpectedRevision)throw Conflict();SafeFiles.DeleteOwnedTree(temporary,backup);}
            operation.State="deployed";operation.UpdatedUtc=DateTime.UtcNow.ToString("o");store.WriteRecord(OpName(operation.Id),operation);store.WriteRecord("portal-skill-binding-"+Webhooks.Hash(operation.Target.Home.ToUpperInvariant()+"\n"+operation.Name).Substring(0,32)+".dpapi",operation);
            var row=Find(operation.Target,Id(operation.Name,Path.Combine(target,"SKILL.md")));bool copied=operation.SourceId!=null&&!(Same(operation.SourceTarget,operation.Target)&&operation.SourceId==row.Id&&!String.IsNullOrEmpty(operation.ExpectedRevision));
            return new{operationId=operation.Id,state="deployed",skill=View(row),refreshRequired=true,message=copied?"Copie du skill installee dans cette instance. Selectionnez explicitement cette copie dans le compositeur d'une nouvelle conversation ; le skill d'origine du plugin ou du profil source reste conserve.":"Skill deploye dans cette instance. Selectionnez-le dans une nouvelle conversation pour utiliser cette version ; les conversations ouvertes sont conservees."};
        }
        private object SaveCore(string route,string actor,string instance,string key,PortalSkillSaveRequest request,bool local)
        {
            if(request==null||!ValidName(request.Name)||request.Document==null||request.ExpectedRevision!=null&&request.ExpectedRevision.Length>0&&!HashValue(request.ExpectedRevision))throw Bad();Metadata(request.Document,true);
            return Mutate(route,actor,instance,key,new{kind="save",value=request},local,target=>{
                var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
                Skill source=null;if(!String.IsNullOrEmpty(request.SourceSkillId)){source=Find(target,request.SourceSkillId);if(source.Revision==null||source.Revision!=request.ExpectedRevision)throw Conflict();files=ReadPackage(Path.GetDirectoryName(source.Path),false).Files;if(Revision(files)!=source.Revision)throw Conflict();}
                files["SKILL.md"]=Utf8.GetBytes(request.Document);var package=Complete(files,request.Name);if(source!=null){package.SourceTarget=target;package.SourceRevision=request.ExpectedRevision;package.CustomizedFrom=source.Editable&&source.Name==request.Name?source.Overrides:source.Id;}return package;
            },request.ExpectedRevision,request.SourceSkillId,target=>TargetExpected(target,request));
        }
        private string TargetExpected(PortalSkillTarget target,PortalSkillSaveRequest request)
        {
            if(String.IsNullOrEmpty(request.SourceSkillId))return request.ExpectedRevision;var source=Find(target,request.SourceSkillId);
            // Editing user files keeps the optimistic revision. Customizing an
            // immutable/global source creates a separate instance skill.
            return source.Editable&&source.Name==request.Name?request.ExpectedRevision:null;
        }
        internal object Save(string route,string actor,string instance,string key,PortalSkillSaveRequest request){return SaveCore(route,actor,instance,key,request,false);}
        internal object SaveLocal(string instance,string key,PortalSkillSaveRequest request){return SaveCore("desktop","desktop",instance,key,request,true);}
        private static Package Archive(PortalSkillImportRequest request)
        {
            if(request==null||String.IsNullOrEmpty(request.ArchiveBase64)||request.ArchiveBase64.Length>((MaximumArchive+2)/3)*4||request.Name!=null&&!ValidName(request.Name))throw Bad();byte[] bytes;try{bytes=Convert.FromBase64String(request.ArchiveBase64);}catch(FormatException){throw Bad();}if(bytes.Length>MaximumArchive)throw Bad();
            CheckZipDirectory(bytes);var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);try{using(var memory=new MemoryStream(bytes,false))using(var zip=new ZipArchive(memory,ZipArchiveMode.Read,false)){
                if(zip.Entries.Count==0||zip.Entries.Count>MaximumFiles*2)throw Bad();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
                foreach(var entry in zip.Entries){string name=entry.FullName;bool directory=name.EndsWith("/",StringComparison.Ordinal);FileName(directory?name.TrimEnd('/'):name);if(!names.Add(name.TrimEnd('/'))||((entry.ExternalAttributes>>16)&0xF000)==0xA000||(entry.ExternalAttributes&(int)FileAttributes.ReparsePoint)!=0)throw Bad();if(directory){if(entry.Length!=0)throw Bad();continue;}
                    if(entry.Length>MaximumFile||(total+=entry.Length)>MaximumExpanded||files.Count>=MaximumFiles)throw Bad();using(var input=entry.Open())using(var output=new MemoryStream()){byte[] buffer=new byte[8192];int count;while((count=input.Read(buffer,0,buffer.Length))>0){if(output.Length+count>entry.Length||output.Length+count>MaximumFile)throw Bad();output.Write(buffer,0,count);}if(output.Length!=entry.Length)throw Bad();files.Add(name,output.ToArray());}
                }
            }}catch(InvalidDataException){throw Bad();}
            // Accept a direct package or exactly one conventional outer folder.
            if(!files.ContainsKey("SKILL.md")){var first=files.Keys.Select(p=>p.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(first.Length!=1||files.Keys.Any(p=>!p.StartsWith(first[0]+"/",StringComparison.Ordinal)))throw Bad();files=files.ToDictionary(p=>p.Key.Substring(first[0].Length+1),p=>p.Value,StringComparer.OrdinalIgnoreCase);}
            return Complete(files,request.Name);
        }
        private static void CheckZipDirectory(byte[] bytes)
        {
            for(int at=bytes.Length-22;at>=Math.Max(0,bytes.Length-65557);at--){
                if(BitConverter.ToUInt32(bytes,at)!=0x06054b50u||at+22+BitConverter.ToUInt16(bytes,at+20)!=bytes.Length)continue;
                int count=BitConverter.ToUInt16(bytes,at+10);long size=BitConverter.ToUInt32(bytes,at+12),offset=BitConverter.ToUInt32(bytes,at+16);
                if(BitConverter.ToUInt16(bytes,at+4)!=0||BitConverter.ToUInt16(bytes,at+6)!=0||BitConverter.ToUInt16(bytes,at+8)!=count||count==0||count>MaximumFiles*2||size>MaximumArchive||offset+size!=at)throw Bad();
                int cursor=(int)offset;for(int index=0;index<count;index++){
                    if(cursor<0||cursor+46>at||BitConverter.ToUInt32(bytes,cursor)!=0x02014b50u||(BitConverter.ToUInt16(bytes,cursor+8)&1)!=0)throw Bad();
                    int length=BitConverter.ToUInt16(bytes,cursor+28),extra=BitConverter.ToUInt16(bytes,cursor+30),comment=BitConverter.ToUInt16(bytes,cursor+32);
                    if(length==0||length>800||BitConverter.ToUInt16(bytes,cursor+34)!=0||BitConverter.ToUInt32(bytes,cursor+20)==UInt32.MaxValue||BitConverter.ToUInt32(bytes,cursor+24)==UInt32.MaxValue||BitConverter.ToUInt32(bytes,cursor+42)>=offset||cursor+46+length+extra+comment>at)throw Bad();cursor+=46+length+extra+comment;
                }if(cursor!=at)throw Bad();return;
            }throw Bad();
        }
        private object ImportCore(string route,string actor,string instance,string key,PortalSkillImportRequest request,bool local)
        {if(request==null||!String.IsNullOrEmpty(request.ExpectedRevision)&&!HashValue(request.ExpectedRevision))throw Bad();return Mutate(route,actor,instance,key,new{kind="import",value=request},local,target=>Archive(request),request.ExpectedRevision,null);}
        internal object Import(string route,string actor,string instance,string key,PortalSkillImportRequest request){return ImportCore(route,actor,instance,key,request,false);}
        internal object ImportLocal(string instance,string key,PortalSkillImportRequest request){return ImportCore("desktop","desktop",instance,key,request,true);}
        private object DeployCore(string route,string actor,string instance,string key,PortalSkillDeployRequest request,bool local)
        {
            if(request==null||!ValidInstance(request.SourceInstanceId)||!HashValue(request.SkillId)||!HashValue(request.SourceRevision)||request.Name!=null&&!ValidName(request.Name)||!String.IsNullOrEmpty(request.ExpectedRevision)&&!HashValue(request.ExpectedRevision))throw Bad();
            return Mutate(route,actor,instance,key,new{kind="deploy",value=request},local,target=>{
                var source=Resolve(route,actor,request.SourceInstanceId,local);var row=Find(source,request.SkillId);if(row.Revision!=request.SourceRevision)throw Conflict();var package=ReadPackage(Path.GetDirectoryName(row.Path),false);if(package.Revision!=request.SourceRevision)throw Conflict();Verify(route,actor,source,local);
                // A rename must be explicit in the document editor first.
                if(request.Name!=null&&request.Name!=package.Name)throw Bad();package=Complete(package.Files,request.Name);package.SourceTarget=source;package.SourceRevision=request.SourceRevision;package.CustomizedFrom=Same(source,target)&&row.Editable?row.Overrides:row.Id;return package;
            },request.ExpectedRevision,request.SkillId);
        }
        internal object Deploy(string route,string actor,string instance,string key,PortalSkillDeployRequest request){return DeployCore(route,actor,instance,key,request,false);}
        internal object DeployLocal(string instance,string key,PortalSkillDeployRequest request){return DeployCore("desktop","desktop",instance,key,request,true);}
    }
}
