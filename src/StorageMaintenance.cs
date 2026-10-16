using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    public sealed class StorageScope
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Path {get;set;}
        public string Kind {get;set;}
        public bool Protected {get;set;}
    }
    public sealed class StorageEntry
    {
        public string Id {get;set;}
        public string ScopeId {get;set;}
        public string Name {get;set;}
        public string Path {get;set;}
        public string Kind {get;set;}
        // Unique logical file data, not NTFS allocation or a promise of free space.
        public long Bytes {get;set;}
        public long Files {get;set;}
        public int Errors {get;set;}
        public bool CanClean {get;set;}
        public string Reason {get;set;}
    }
    public sealed class StorageSnapshot
    {
        public StorageEntry[] Entries {get;set;}
        public long FreeBytes {get;set;}
        public long TotalBytes {get;set;}
        public string CapturedUtc {get;set;}
        public bool Partial {get;set;}
    }
    public sealed class StorageCleanupResult
    {
        public string Path {get;set;}
        public string Code {get;set;}
        public string Message {get;set;}
        public long ReclaimedBytes {get;set;}
        public bool Removed {get;set;}
    }
    internal enum StorageOwnerStatus { Dead, Alive, Reused, Unknown }

    public static class StorageMaintenance
    {
        private const uint ReadAttributes=0x80,GenericRead=0x80000000,DeleteAccess=0x10000;
        private const uint OpenExisting=3,BackupSemantics=0x02000000,OpenReparsePoint=0x00200000;
        private const string RuntimeMarker="switcher-runtime-owner.json",TemporaryMarker="switcher-temporary-owner.json";
        private const int MaximumCleanupItems=20000;
        [StructLayout(LayoutKind.Sequential)] private struct NativeInfo
        {public uint Attributes,CreatedLow,CreatedHigh,AccessLow,AccessHigh,WrittenLow,WrittenHigh,VolumeSerial,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
        [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
        {public long AllocationSize,EndOfFile;public uint NumberOfLinks;[MarshalAs(UnmanagedType.U1)]public bool DeletePending;[MarshalAs(UnmanagedType.U1)]public bool Directory;}
        [StructLayout(LayoutKind.Sequential)] private struct DispositionInfo {public int DeleteFile;}
        [StructLayout(LayoutKind.Sequential)] private struct NativeString {public ushort Length,Maximum;public IntPtr Buffer;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out NativeInfo info);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out StandardInfo info,uint size);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref DispositionInfo info,uint size);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadFile(SafeFileHandle handle,byte[] bytes,int length,out int read,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process,int kind,IntPtr value,int size,out int returned);

        private sealed class FileIdentity
        {internal string Key;internal long Length,Allocated;internal uint Links;}
        private sealed class HeldItem:IDisposable
        {internal string Path;internal bool Directory;internal FileIdentity Identity;internal SafeFileHandle Handle;public void Dispose(){if(Handle!=null)Handle.Dispose();}}
        private sealed class Plan:IDisposable
        {
            internal string Path,AppRoot,Kind,Marker;internal RuntimeOwner Owner;internal readonly List<HeldItem> Items=new List<HeldItem>();
            internal readonly List<SafeFileHandle> Guards=new List<SafeFileHandle>();
            public void Dispose(){foreach(var item in Items)item.Dispose();foreach(var guard in Guards)guard.Dispose();}
        }
        private sealed class Refusal:Exception
        {internal readonly string Code;internal Refusal(string code,string message):base(message){Code=code;}}
        private sealed class Budget
        {internal readonly Stopwatch Clock=Stopwatch.StartNew();internal int Nodes,Maximum;internal bool Limited;internal bool Take(){if(++Nodes>Maximum||Clock.Elapsed>TimeSpan.FromSeconds(90)){Limited=true;return false;}return true;}}
        private static string Full(string path){string full=System.IO.Path.GetFullPath(path);return full.Length>System.IO.Path.GetPathRoot(full).Length?full.TrimEnd(System.IO.Path.DirectorySeparatorChar):full;}
        private static bool Same(string a,string b){return String.Equals(Full(a),Full(b),StringComparison.OrdinalIgnoreCase);}
        private static string EntryId(string scope,string path)
        {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes((scope??"")+"|"+(path??"").ToUpperInvariant()))).Replace("-","").ToLowerInvariant();}
        private static void Note(StorageEntry entry,string reason)
        {if(String.IsNullOrEmpty(entry.Reason))entry.Reason=reason;else if(entry.Reason.IndexOf(reason,StringComparison.Ordinal)<0&&entry.Reason.Length<1200)entry.Reason+=" · "+reason;}
        private static void RejectReparse(string path)
        {SafeFiles.RejectLinks(path);if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new Refusal("reparse-point","Un lien ou une jonction est conservé sans être parcouru.");}
        private static SafeFileHandle Open(string path,bool directory,bool exclusive)
        {
            var handle=CreateFile(path,ReadAttributes|(exclusive?DeleteAccess|GenericRead:0),exclusive?(directory?3u:0u):7u,IntPtr.Zero,OpenExisting,OpenReparsePoint|BackupSemantics,IntPtr.Zero);
            if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}
            NativeInfo info;if(!GetFileInformationByHandle(handle,out info)){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}
            if((info.Attributes&(uint)FileAttributes.ReparsePoint)!=0){handle.Dispose();throw new Refusal("reparse-point","Un lien ou une jonction est conservé sans être parcouru.");}
            if(((info.Attributes&(uint)FileAttributes.Directory)!=0)!=directory){handle.Dispose();throw new Refusal("changed","Le type du chemin a changé ; opération refusée.");}
            return handle;
        }
        private static FileIdentity Identify(SafeFileHandle handle)
        {
            NativeInfo info;if(!GetFileInformationByHandle(handle,out info))throw new Win32Exception(Marshal.GetLastWin32Error());
            var text=new StringBuilder(32768);uint count=GetFinalPathNameByHandle(handle,text,(uint)text.Capacity,1);
            if(count==0||count>=text.Capacity)throw new Win32Exception(Marshal.GetLastWin32Error());
            string final=text.ToString();int end=final.IndexOf('}');
            if(!final.StartsWith(@"\\?\Volume{",StringComparison.OrdinalIgnoreCase)||end<0)throw new Refusal("identity-unavailable","Identité du volume indisponible ; taille estimée sans déduplication complète.");
            StandardInfo standard;long allocated=-1;
            if(GetFileInformationByHandleEx(handle,1,out standard,(uint)Marshal.SizeOf(typeof(StandardInfo))))allocated=standard.AllocationSize;
            return new FileIdentity{Key=final.Substring(0,end+1)+":"+info.VolumeSerial+":"+info.IndexHigh+":"+info.IndexLow,Length=((long)info.SizeHigh<<32)|info.SizeLow,Allocated=allocated,Links=info.Links};
        }
        private static string Classify(string path,string fallback)
        {
            string name=(System.IO.Path.GetFileName(path??"")??"").ToLowerInvariant();
            if(new[]{"sessions","archived_sessions","history","thread_history","rollout-migrations"}.Contains(name))return "history";
            if(name.StartsWith("thread_history",StringComparison.Ordinal)||name=="session_index.jsonl"||name.StartsWith("rollout-",StringComparison.Ordinal))return "history";
            if(name.IndexOf(".sqlite",StringComparison.Ordinal)>=0||name.EndsWith(".db",StringComparison.Ordinal)||name.EndsWith(".dpapi",StringComparison.Ordinal)||name=="auth.json")return "data";
            if(new[]{".git","src","sources","source","docs","scripts"}.Contains(name))return "sources";
            if(new[]{"node_modules","dependencies","vendor","sdk","packs","plugins","skills",".plugin-appserver",".sandbox-bin","node_repl"}.Contains(name))return "dependencies";
            if(new[]{"cache","caches",".cache",".tmp","tmp","temp","component_crx_cache","grshadercache","shadercache","gpucache"}.Contains(name))return name.IndexOf("tmp",StringComparison.Ordinal)>=0||name=="temp"?"temporary":"cache";
            if(new[]{"bin","obj","build","outputs"}.Contains(name))return "builds";
            if(name=="runtime"||name=="temporary")return name;
            if(new[]{"instances","codex-home","electron-profile","worktrees","attachments","generated_images","visualizations","sqlite"}.Contains(name))return "data";
            return new[]{"history","data","cache","temporary","dependencies","builds","sources","runtime"}.Contains(fallback)?fallback:"unknown";
        }
        public static StorageSnapshot Scan(IEnumerable<StorageScope> scopes,CancellationToken token)
        {return ScanCore(scopes,token,1000000);}
        internal static StorageSnapshot ScanCore(IEnumerable<StorageScope> scopes,CancellationToken token,int maximumNodes)
        {
            var entries=new List<StorageEntry>();var identities=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var budget=new Budget{Maximum=Math.Max(1,maximumNodes)};
            var snapshot=new StorageSnapshot{CapturedUtc=DateTime.UtcNow.ToString("o")};bool driveRead=false;
            foreach(var scope in scopes??new StorageScope[0]){
                token.ThrowIfCancellationRequested();if(scope==null)continue;string root=null;
                try{
                    root=Full(scope.Path);RejectReparse(root);
                    if(!driveRead){try{var drive=new DriveInfo(System.IO.Path.GetPathRoot(root));snapshot.FreeBytes=drive.AvailableFreeSpace;snapshot.TotalBytes=drive.TotalSize;driveRead=true;}catch(IOException){snapshot.Partial=true;}}
                    if(File.Exists(root)){var fileEntry=NewEntry(scope,scope.Name??System.IO.Path.GetFileName(root),root);entries.Add(fileEntry);Measure(new FileInfo(root),fileEntry,identities,token);Note(fileEntry,scope.Protected?"Emplacement protégé ; aucune suppression autorisée.":"Fichier conservé ; aucune suppression sur son seul nom.");continue;}
                    if(!Directory.Exists(root))throw new DirectoryNotFoundException();
                    var direct=new List<FileInfo>();
                    foreach(var item in new DirectoryInfo(root).EnumerateFileSystemInfos()){
                        token.ThrowIfCancellationRequested();if(!budget.Take())break;if((item.Attributes&FileAttributes.Directory)==0&&(item.Attributes&FileAttributes.ReparsePoint)==0){direct.Add((FileInfo)item);continue;}
                        var entry=NewEntry(scope,item.Name,item.FullName);entries.Add(entry);
                        Walk(item.FullName,entry,identities,visited,budget,token);
                        if(scope.Protected)Note(entry,"Emplacement protégé ; aucune suppression autorisée.");else OfferCleanup(scope,entry,token);if(budget.Limited)break;
                    }
                    if(direct.Count>0){
                        var grouped=new List<FileInfo>();int displayed=0;
                        foreach(var file in direct.OrderByDescending(f=>f.Length)){
                            token.ThrowIfCancellationRequested();string kind=Classify(file.FullName,scope.Kind);
                            if(displayed<100&&(file.Length>=1024*1024||kind=="history"||kind=="data")){
                                if(!budget.Take())break;var item=NewEntry(scope,file.Name,file.FullName);entries.Add(item);Measure(file,item,identities,token);displayed++;Note(item,"Fichier conservé ; aucune suppression sur son seul nom.");
                            }else grouped.Add(file);
                        }
                        if(grouped.Count>0){var entry=NewEntry(scope,"Autres fichiers à la racine",root);entries.Add(entry);foreach(var file in grouped){if(!budget.Take())break;Measure(file,entry,identities,token);}Note(entry,scope.Protected?"Emplacement protégé ; aucune suppression autorisée.":"Fichiers conservés ; aucune suppression sur leur seul nom.");}
                    }
                }catch(OperationCanceledException){throw;}catch(Exception error){var entry=NewEntry(scope,scope.Name??scope.Id??"Emplacement",root??scope.Path);entry.Errors++;Note(entry,Describe(error));entries.Add(entry);snapshot.Partial=true;}
                if(budget.Limited){snapshot.Partial=true;foreach(var entry in entries)Note(entry,"Analyse bornée interrompue ; tailles partielles.");break;}
            }
            snapshot.Entries=entries.ToArray();snapshot.Partial=snapshot.Partial||entries.Any(e=>e.Errors>0);return snapshot;
        }
        private static StorageEntry NewEntry(StorageScope scope,string name,string path)
        {return new StorageEntry{Id=EntryId(scope.Id,path),ScopeId=scope.Id,Name=name,Path=path,Kind=Classify(path,scope.Kind),Reason=scope.Protected?"Emplacement protégé ; aucune suppression autorisée.":null};}
        private static void Walk(string root,StorageEntry entry,HashSet<string> identities,HashSet<string> visited,Budget budget,CancellationToken token)
        {
            var pending=new Stack<string>();pending.Push(root);
            while(pending.Count>0){token.ThrowIfCancellationRequested();if(!budget.Take())return;string path=pending.Pop();
                try{RejectReparse(path);if(!visited.Add(Full(path))){Note(entry,"Chemin déjà compté dans cette analyse.");continue;}
                    foreach(var item in new DirectoryInfo(path).EnumerateFileSystemInfos()){
                        token.ThrowIfCancellationRequested();if(!budget.Take())return;if((item.Attributes&FileAttributes.ReparsePoint)!=0){entry.Errors++;Note(entry,"Lien ou jonction ignoré avant descente.");continue;}
                        if((item.Attributes&FileAttributes.Directory)!=0)pending.Push(item.FullName);else Measure((FileInfo)item,entry,identities,token);
                    }
                }catch(OperationCanceledException){throw;}catch(Exception error){entry.Errors++;Note(entry,Describe(error));}
            }
        }
        private static void Measure(FileInfo file,StorageEntry entry,HashSet<string> identities,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();entry.Files++;
            try{RejectReparse(file.FullName);using(var handle=Open(file.FullName,false,false)){var id=Identify(handle);if(identities.Add(id.Key))entry.Bytes=checked(entry.Bytes+id.Length);else Note(entry,"Liens physiques partagés comptés une seule fois dans l’analyse.");}}
            catch(Exception error){if(error is OperationCanceledException)throw;entry.Errors++;try{entry.Bytes=checked(entry.Bytes+file.Length);}catch(IOException){}Note(entry,Describe(error)+" Taille estimée ; déduplication incomplète.");}
        }
        private static void OfferCleanup(StorageScope scope,StorageEntry entry,CancellationToken token)
        {
            string name=System.IO.Path.GetFileName(scope.Path??"");if(!new[]{"runtime","temporary"}.Contains(name,StringComparer.OrdinalIgnoreCase))return;
            try{using(var plan=Prepare(System.IO.Path.GetDirectoryName(Full(scope.Path)),entry.Path,token,OwnerStatus,Referenced,null)){entry.CanClean=entry.Errors==0;Note(entry,"Temporaire du Switcher identifié ; propriétaire arrêté. Confirmation et nouvelle vérification requises.");}}
            catch(OperationCanceledException){throw;}
            catch(Exception error){entry.CanClean=false;Note(entry,Describe(error));}
        }
        private static StorageOwnerStatus OwnerStatus(int pid,long started)
        {
            try{using(var process=Process.GetProcessById(pid)){if(process.HasExited)return StorageOwnerStatus.Dead;return process.StartTime.ToUniversalTime().Ticks==started?StorageOwnerStatus.Alive:StorageOwnerStatus.Reused;}}
            catch(ArgumentException){return StorageOwnerStatus.Dead;}catch(InvalidOperationException){return StorageOwnerStatus.Dead;}catch(Win32Exception){return StorageOwnerStatus.Unknown;}catch(UnauthorizedAccessException){return StorageOwnerStatus.Unknown;}
        }
        private static string ProcessCommandLine(int pid)
        {
            IntPtr handle=OpenProcess(0x1000,false,pid);if(handle==IntPtr.Zero)return null;
            try{int length;NtQueryInformationProcess(handle,60,IntPtr.Zero,0,out length);if(length<=0||length>262144)return null;IntPtr buffer=Marshal.AllocHGlobal(length);
                try{if(NtQueryInformationProcess(handle,60,buffer,length,out length)!=0)return null;var value=(NativeString)Marshal.PtrToStructure(buffer,typeof(NativeString));long offset=value.Buffer.ToInt64()-buffer.ToInt64();if(offset<0||offset+value.Length>length||value.Length%2!=0)return null;return Marshal.PtrToStringUni(value.Buffer,value.Length/2);}finally{Marshal.FreeHGlobal(buffer);}}
            finally{CloseHandle(handle);}
        }
        private static bool Referenced(string path,int exceptPid)
        {
            foreach(var process in Process.GetProcesses())using(process){if(process.Id==exceptPid)continue;try{string command=ProcessCommandLine(process.Id);if(command!=null&&command.Replace("\\\\","\\").IndexOf(path,StringComparison.OrdinalIgnoreCase)>=0)return true;}catch(Win32Exception){}catch(InvalidOperationException){}}
            return false;
        }
        private static bool ProtectedName(string name,bool directory)
        {
            name=name.ToLowerInvariant();
            if(directory)return new[]{".git","src","source","sources","projects","sessions","archived_sessions","history","worktrees","profiles","instances",".ssh",".aws",".azure",".config","plugins","skills","node_modules","dependencies","packages","attachments","generated_images","visualizations","documents","secrets","credentials"}.Contains(name);
            if(new[]{".env","auth.json","accounts.dpapi","credentials.json","cookies","login data","web data"}.Contains(name)||name.StartsWith("auth.",StringComparison.Ordinal)||name.IndexOf("secret",StringComparison.Ordinal)>=0||name.IndexOf("credential",StringComparison.Ordinal)>=0||name.IndexOf("token",StringComparison.Ordinal)>=0)return true;
            string ext=System.IO.Path.GetExtension(name);if(new[]{".sqlite",".sqlite3",".db",".jsonl",".dpapi",".pem",".key",".pfx",".cs",".csproj",".js",".mjs",".cjs",".ts",".tsx",".jsx",".py",".rs",".go",".c",".cpp",".h",".java",".md",".tex",".git"}.Contains(ext))return true;
            return name.IndexOf(".sqlite",StringComparison.Ordinal)>=0||name.IndexOf(".db-",StringComparison.Ordinal)>=0||name.StartsWith(".env.",StringComparison.Ordinal)||System.Text.RegularExpressions.Regex.IsMatch(name,@"\.(cs|js|ts|py|rs|go|md|jsonl)\.(tmp|bak)$");
        }
        private static bool DisposableFile(string name,string marker,string kind)
        {
            if(String.Equals(name,marker,StringComparison.OrdinalIgnoreCase))return true;
            if(kind=="codex-rpc-scratch-v1"&&String.Equals(name,"config.toml",StringComparison.OrdinalIgnoreCase))return true;
            return new[]{".tmp",".temp",".log",".cache",".dmp",".part",".lock"}.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());
        }
        private static RuntimeOwner ReadMarker(HeldItem item)
        {
            if(item.Identity.Length<=0||item.Identity.Length>8192)throw new Refusal("invalid-marker","Marqueur de propriété absent ou invalide ; conservation.");
            var bytes=new byte[(int)item.Identity.Length];int read;if(!ReadFile(item.Handle,bytes,bytes.Length,out read,IntPtr.Zero)||read!=bytes.Length)throw new Refusal("invalid-marker","Marqueur de propriété illisible ; conservation.");
            try{return Json.Read<RuntimeOwner>(new UTF8Encoding(false,true).GetString(bytes));}catch{throw new Refusal("invalid-marker","Marqueur de propriété invalide ; conservation.");}
        }
        private static Plan Prepare(string appRoot,string candidate,CancellationToken token,Func<int,long,StorageOwnerStatus> ownerStatus,Func<string,int,bool> referenced,RuntimeOwner currentOwner,int maximumItems=MaximumCleanupItems)
        {
            token.ThrowIfCancellationRequested();var plan=new Plan();
            try{
                plan.AppRoot=Full(appRoot);plan.Path=Full(candidate);string parent=System.IO.Path.GetDirectoryName(plan.Path),name=System.IO.Path.GetFileName(plan.Path);
                if(!System.Text.RegularExpressions.Regex.IsMatch(name??"","^[a-f0-9]{32}$")||(!Same(parent,System.IO.Path.Combine(plan.AppRoot,"runtime"))&&!Same(parent,System.IO.Path.Combine(plan.AppRoot,"temporary"))))throw new Refusal("outside-root","Seul un sous-dossier GUID directement détenu par le Switcher peut être nettoyé.");
                plan.Kind=Same(parent,System.IO.Path.Combine(plan.AppRoot,"runtime"))?"codex-rpc-scratch-v1":"switcher-owned-temp-v1";plan.Marker=plan.Kind=="codex-rpc-scratch-v1"?RuntimeMarker:TemporaryMarker;
                RejectReparse(plan.AppRoot);RejectReparse(parent);RejectReparse(plan.Path);
                plan.Guards.Add(Open(plan.AppRoot,true,false));plan.Guards.Add(Open(parent,true,false));
                int inspected=1;maximumItems=Math.Max(1,Math.Min(maximumItems,MaximumCleanupItems));var dirs=new Stack<string>();dirs.Push(plan.Path);
                while(dirs.Count>0){token.ThrowIfCancellationRequested();string path=dirs.Pop();
                    RejectReparse(path);var dir=new HeldItem{Path=path,Directory=true,Handle=Open(path,true,true)};plan.Items.Add(dir);
                    foreach(var child in new DirectoryInfo(path).EnumerateFileSystemInfos()){
                        token.ThrowIfCancellationRequested();if(++inspected>maximumItems)throw new Refusal("limit","Trop d’éléments pour ce passage borné ; contenu conservé.");
                        if((child.Attributes&FileAttributes.ReparsePoint)!=0)throw new Refusal("reparse-point","Un lien ou une jonction est présent ; aucun élément de ce lot n’est supprimé.");
                        bool directory=(child.Attributes&FileAttributes.Directory)!=0;
                        if(ProtectedName(child.Name,directory))throw new Refusal("protected-data","Sources, identifiants, bases ou historiques détectés ; contenu conservé.");
                        if(directory){dirs.Push(child.FullName);continue;}
                        if(!DisposableFile(child.Name,plan.Marker,plan.Kind))throw new Refusal("unclassified-content","Contenu non identifié comme temporaire jetable ; conservation.");
                        var held=new HeldItem{Path=child.FullName,Handle=Open(child.FullName,false,true)};plan.Items.Add(held);held.Identity=Identify(held.Handle);
                    }
                }
                string markerPath=System.IO.Path.Combine(plan.Path,plan.Marker);var marker=plan.Items.SingleOrDefault(i=>!i.Directory&&Same(i.Path,markerPath));
                if(marker==null)throw new Refusal("missing-marker","Aucun marqueur de propriété du Switcher ; conservation.");
                var owner=ReadMarker(marker);if(owner==null||owner.Kind!=plan.Kind||String.IsNullOrWhiteSpace(owner.Directory)||!Same(owner.Directory,plan.Path)||owner.Pid<=0||owner.Started<=0)throw new Refusal("invalid-marker","Le marqueur ne prouve pas la propriété exacte de ce temporaire.");plan.Owner=owner;
                int except=0;
                if(currentOwner!=null){using(var process=Process.GetCurrentProcess())if(plan.Kind!="switcher-owned-temp-v1"||currentOwner.Kind!=owner.Kind||!Same(currentOwner.Directory,owner.Directory)||currentOwner.Pid!=owner.Pid||currentOwner.Started!=owner.Started||owner.Pid!=process.Id||owner.Started!=process.StartTime.ToUniversalTime().Ticks)throw new Refusal("invalid-owner","Seul le contrôleur actuel exact peut libérer son propre temporaire.");except=owner.Pid;}
                else{var status=ownerStatus(owner.Pid,owner.Started);if(status!=StorageOwnerStatus.Dead)throw new Refusal(status==StorageOwnerStatus.Alive?"owner-running":status==StorageOwnerStatus.Reused?"owner-reused":"owner-unknown",status==StorageOwnerStatus.Alive?"Le propriétaire de ce temporaire fonctionne encore.":"L’arrêt du propriétaire exact ne peut pas être confirmé ; conservation.");}
                if(referenced(plan.Path,except))throw new Refusal("in-use","Un processus référence encore ce temporaire ; conservation.");
                return plan;
            }catch{plan.Dispose();throw;}
        }
        public static StorageCleanupResult[] CleanRuntime(string appRoot,IEnumerable<string> approved,CancellationToken token)
        {return CleanCore(appRoot,approved,token,OwnerStatus,Referenced,null);}
        internal static StorageCleanupResult[] CleanRuntimeBounded(string appRoot,IEnumerable<string> approved,CancellationToken token,int maximumItems)
        {return CleanCore(appRoot,approved,token,OwnerStatus,Referenced,null,null,maximumItems);}
        public static StorageCleanupResult ClearOwnedTemporary(string appRoot,string path,RuntimeOwner expected,CancellationToken token)
        {if(expected==null)return Result(path,new Refusal("invalid-owner","Propriétaire contrôleur exact requis."));return CleanCore(appRoot,new[]{path},token,OwnerStatus,Referenced,expected).Single();}
        internal static StorageCleanupResult[] CleanCore(string appRoot,IEnumerable<string> approved,CancellationToken token,Func<int,long,StorageOwnerStatus> owner,Func<string,int,bool> referenced,RuntimeOwner currentOwner,Action<string> beforeDelete=null,int maximumItems=MaximumCleanupItems)
        {
            var paths=(approved??new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).Take(129).ToArray();var plans=new List<Plan>();var results=new List<StorageCleanupResult>();bool tooMany=paths.Length>128,rejected=tooMany;
            foreach(string path in paths){try{if(tooMany)throw new Refusal("limit","Sélection trop importante ; aucun nettoyage effectué.");var plan=Prepare(appRoot,path,token,owner,referenced,currentOwner,maximumItems);plans.Add(plan);results.Add(new StorageCleanupResult{Path=plan.Path,Code="prepared",Message="Préparation vérifiée."});}catch(Exception error){rejected=true;results.Add(Result(path,error));}}
            try{
                if(rejected){foreach(var result in results.Where(r=>r.Code=="prepared")){result.Code="batch-refused";result.Message="Un élément de la sélection n’est pas sûr ; aucun élément de ce lot n’a été supprimé.";}return results.ToArray();}
                // Revalidate all owners before the first irreversible operation.
                foreach(var plan in plans){token.ThrowIfCancellationRequested();RejectReparse(plan.Path);if(currentOwner==null&&owner(plan.Owner.Pid,plan.Owner.Started)!=StorageOwnerStatus.Dead)throw new Refusal("owner-changed","Le propriétaire a changé avant suppression ; lot conservé.");if(referenced(plan.Path,currentOwner==null?0:currentOwner.Pid))throw new Refusal("in-use","Un processus utilise le temporaire ; lot conservé.");}
                for(int index=0;index<plans.Count;index++){
                    var plan=plans[index];var result=results[index];long reclaimed=0;bool any=false;
                    try{
                        token.ThrowIfCancellationRequested();RejectReparse(plan.Path);
                        if(currentOwner==null&&owner(plan.Owner.Pid,plan.Owner.Started)!=StorageOwnerStatus.Dead)throw new Refusal("owner-changed","Le propriétaire a changé avant suppression ; contenu conservé.");
                        if(referenced(plan.Path,currentOwner==null?0:currentOwner.Pid))throw new Refusal("in-use","Un processus utilise le temporaire ; contenu conservé.");
                        string markerPath=System.IO.Path.Combine(plan.Path,plan.Marker);
                        foreach(var item in plan.Items.OrderBy(i=>i.Directory?2:Same(i.Path,markerPath)?1:0).ThenByDescending(i=>i.Path.Length)){
                            token.ThrowIfCancellationRequested();if(beforeDelete!=null)beforeDelete(item.Path);var deletion=new DispositionInfo{DeleteFile=1};
                            if(!SetFileInformationByHandle(item.Handle,4,ref deletion,(uint)Marshal.SizeOf(typeof(DispositionInfo))))throw new Win32Exception(Marshal.GetLastWin32Error());
                            item.Dispose();any=true;if(!item.Directory&&item.Identity.Links==1&&item.Identity.Allocated>0)reclaimed=checked(reclaimed+item.Identity.Allocated);
                        }
                        result.Code="removed";result.Removed=true;result.Message="Temporaire du Switcher supprimé ; seuls les fichiers vérifiés ont été retirés.";
                    }catch(Exception error){var failure=Result(plan.Path,error);result.Code=(any?"partial-":"")+failure.Code;result.Message=(any?"Nettoyage partiel. ":"")+failure.Message;}
                    result.ReclaimedBytes=reclaimed;
                    if(result.Code=="policy-blocked"||result.Code=="partial-policy-blocked"){
                        foreach(var remaining in results.Skip(index+1).Where(r=>r.Code=="prepared")){remaining.Code="batch-refused";remaining.Message="Lot interrompu après le refus du contrôle automatique ; aucune autre suppression tentée.";}
                        break;
                    }
                }
                return results.ToArray();
            }catch(Exception error){foreach(var result in results.Where(r=>r.Code=="prepared")){var failure=Result(result.Path,error);result.Code=failure.Code;result.Message=failure.Message;}return results.ToArray();}
            finally{foreach(var plan in plans)plan.Dispose();}
        }
        private static StorageCleanupResult Result(string path,Exception error)
        {
            var refusal=error as Refusal;string code=refusal==null?null:refusal.Code;
            if(error is OperationCanceledException)code="cancelled";
            if(error.Message.IndexOf("blocked by policy",StringComparison.OrdinalIgnoreCase)>=0)code="policy-blocked";
            var native=error as Win32Exception;if(code==null&&native!=null)code="win32-"+native.NativeErrorCode;
            if(code==null&&error is UnauthorizedAccessException)code="win32-5";
            if(code==null&&(error is IOException))code="win32-"+(error.HResult&0xffff);
            return new StorageCleanupResult{Path=path,Code=code??"unavailable",Message=Describe(error)};
        }
        private static string Describe(Exception error)
        {
            if(error is Refusal)return error.Message;
            if(error is OperationCanceledException)return "Opération annulée ; aucun nouvel élément n’est supprimé.";
            if(error.Message.IndexOf("blocked by policy",StringComparison.OrdinalIgnoreCase)>=0)return "Le contrôle automatique a refusé l’opération (blocked by policy). Aucun contournement n’est effectué.";
            var native=error as Win32Exception;int code=native!=null?native.NativeErrorCode:error is UnauthorizedAccessException?5:error is IOException?(error.HResult&0xffff):0;
            if(code==5)return "Accès refusé par Windows (Win32 5) ; contenu conservé, sans modification des droits.";
            if(code==32||code==33)return "Fichier verrouillé par Windows (Win32 "+code+") ; contenu conservé.";
            if(code==2||code==3)return "Chemin absent ou déplacé (Win32 "+code+").";
            if(code!=0)return "Erreur Windows "+code+" ; résultat incomplet, sans nouvelle tentative automatique.";
            return "État indisponible ("+error.GetType().Name+") ; contenu conservé.";
        }
    }
}
