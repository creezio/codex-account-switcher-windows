using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    public sealed class PortalAttachmentRef
    {
        public string Id {get;set;} public string Name {get;set;} public string RelativePath {get;set;}
        public long Size {get;set;} public string MimeType {get;set;} public string Path {get;set;} public string Sha256 {get;set;}
    }
    internal sealed class PortalAttachmentRecord
    {
        public string Id {get;set;} public string RouteId {get;set;} public string InstanceId {get;set;}
        public string AccountKey {get;set;} public string Home {get;set;} public string Fingerprint {get;set;}
        public string Name {get;set;} public string RelativePath {get;set;} public string MimeType {get;set;} public long Size {get;set;}
        public string Sha256 {get;set;} public string CreatedUtc {get;set;} public string State {get;set;}
        public bool Used {get;set;} public bool PayloadRemoved {get;set;} public string CleanupError {get;set;}
        public int OwnerPid {get;set;} public long OwnerStarted {get;set;}
    }
    internal sealed class PortalAttachmentCleanupCursor { public string LastId {get;set;} }
    internal static class PortalAttachments
    {
        internal const int MaximumFileBytes=8*1024*1024,MaximumFiles=10,MaximumTurnBytes=24*1024*1024,MaximumUploadBytes=12*1024*1024;
        internal const long MaximumStoredBytes=1024L*1024*1024,MaximumStagedRouteBytes=256L*1024*1024,MinimumFreeBytes=20L*1024*1024*1024;
        private const int MaximumRecords=10000;
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        private static readonly System.Threading.SemaphoreSlim UploadGate=new System.Threading.SemaphoreSlim(1,1);
        [StructLayout(LayoutKind.Sequential)]private struct HeldFileInfo {public uint Attributes,CreateLow,CreateHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
        [StructLayout(LayoutKind.Sequential)]private struct DeleteDisposition {public int DeleteFile;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out HeldFileInfo info);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref DeleteDisposition disposition,uint size);
        private static SafeFileHandle Hold(string path,bool directory)
        {
            SafeFiles.RejectLinks(path);var handle=CreateFile(path,directory?0x80u:0x80010000u,directory?3u:0u,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            try{if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());HeldFileInfo info;if(!GetFileInformationByHandle(handle,out info))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());if((info.Attributes&0x400)!=0||((info.Attributes&0x10)!=0)!=directory||!directory&&info.Links!=1)throw new IOException("Identite du fichier ou de son dossier non verifiable.");return handle;}catch{handle.Dispose();throw;}
        }
        private static void HoldParents(string directory,List<SafeFileHandle> held)
        {var ancestors=new Stack<string>();for(string path=directory;path!=null;path=System.IO.Path.GetDirectoryName(path))ancestors.Push(path);foreach(string path in ancestors)held.Add(Hold(path,true));}
        internal static string RecordName(string id){Id(id);return "portal-attachment-"+id+".dpapi";}
        private static void Id(string id){if(!Regex.IsMatch(id??"","\\A[a-f0-9]{32}\\z"))throw new WebhookException(404,"Piece jointe introuvable.");}
        private static string DirectoryFor(RelayStore store,string id)
        {
            Id(id);string parent=System.IO.Path.Combine(store.Root,"portal-attachments"),directory=System.IO.Path.Combine(parent,id);
            SafeFiles.RejectLinks(parent);SafeFiles.RejectLinks(directory);return directory;
        }
        private static string BlobName(string mime){return "blob"+(mime=="image/png"?".png":mime=="image/jpeg"?".jpg":mime=="image/webp"?".webp":mime=="image/gif"?".gif":".bin");}
        private static string PayloadFor(RelayStore store,string id){var record=store.ReadRecord<PortalAttachmentRecord>(RecordName(id));string path=System.IO.Path.Combine(DirectoryFor(store,id),BlobName(record.MimeType));SafeFiles.RejectLinks(path);return path;}
        private static FileStream Lease(RelayStore store)
        {
            DateTime until=DateTime.UtcNow.AddSeconds(5);while(true)try{return store.Lease("portal-attachments");}catch(IOException){if(DateTime.UtcNow>=until)throw new WebhookException(429,"Les pieces jointes sont occupees. Reessayez avec la meme cle d'idempotence.");System.Threading.Thread.Sleep(15);}
        }
        private static void ValidateScope(RelayStore store,WebhookRoute supplied,PortalInstanceGrant grant)
        {
            if(supplied==null||grant==null)throw new WebhookException(403,"Association de la piece jointe absente.");
            var route=Webhooks.Route(store,supplied.Id);if(!Webhooks.Config(store).Enabled||!route.Enabled||route.Revision!=supplied.Revision)throw new WebhookException(403,"La route a change pendant le transfert.");
            bool direct=route.InstanceId==grant.InstanceId&&route.AccountKey==grant.AccountKey&&RelayStore.SamePath(route.Home,grant.Home);
            var config=Portal.Config(store,route.Id);bool portal=config.Enabled&&config.RootRouteId==route.Id&&config.Instances.Any(i=>i.InstanceId==grant.InstanceId&&i.AccountKey==grant.AccountKey&&RelayStore.SamePath(i.Home,grant.Home));
            if(!direct&&!portal)throw new WebhookException(404,"Instance non partagee avec cette route.");
            if(grant.InstanceId!="local")Id(grant.InstanceId);if(String.IsNullOrWhiteSpace(grant.AccountKey)||String.IsNullOrWhiteSpace(grant.Home)||!System.IO.Path.IsPathRooted(grant.Home))throw new WebhookException(403,"Association de compte invalide.");
        }
        internal static object Upload(RelayStore store,WebhookRoute route,PortalInstanceGrant grant,string idempotency,string json)
        {return UploadCore(store,route,grant,idempotency,json,()=>new DriveInfo(System.IO.Path.GetPathRoot(store.Root)).AvailableFreeSpace,DateTime.UtcNow);}
        internal static object UploadCore(RelayStore store,WebhookRoute route,PortalInstanceGrant grant,string idempotency,string json,Func<long> freeBytes,DateTime now)
        {
            if(!UploadGate.Wait(5000))throw new WebhookException(429,"Un fichier est deja en cours de transfert. Reessayez avec la meme cle d'idempotence.");
            try{return UploadLocked(store,route,grant,idempotency,json,freeBytes,now);}finally{UploadGate.Release();}
        }
        private static object UploadLocked(RelayStore store,WebhookRoute route,PortalInstanceGrant grant,string idempotency,string json,Func<long> freeBytes,DateTime now)
        {
            ValidateScope(store,route,grant);
            if(String.IsNullOrEmpty(idempotency)||idempotency.Length>128||idempotency.Any(c=>c<33||c>126))throw new WebhookException(400,"Idempotency-Key est obligatoire pour le fichier.");
            var values=new UploadJson(json).Read();string name=Name(values["name"]),mime=Mime(values["mimeType"]),relative=Relative(values.ContainsKey("relativePath")?values["relativePath"]:name,name);
            byte[] bytes=Decode(values["contentBase64"]);try{
                CheckImage(mime,bytes);string hash=Digest(bytes),fingerprint=Webhooks.Hash(Json.Write(new[]{name,relative,mime,hash}));
                string id=Webhooks.Hash(route.Id+"\n"+grant.InstanceId+"\n"+grant.AccountKey+"\n"+System.IO.Path.GetFullPath(grant.Home).ToUpperInvariant()+"\n"+idempotency).Substring(0,32);
                using(Lease(store)){
                    ValidateScope(store,route,grant);string recordFile=System.IO.Path.Combine(store.Root,RecordName(id));
                    if(File.Exists(recordFile)){
                        var prior=store.ReadRecord<PortalAttachmentRecord>(RecordName(id));if(prior.Id!=id)throw new WebhookException(409,"Le recu de ce fichier ne correspond plus a son identifiant.");Binding(prior,route.Id,grant);
                        if(prior.Fingerprint!=fingerprint)throw new WebhookException(409,"Cette cle d'idempotence designe un autre fichier.");
                        if(prior.State=="expired")throw new WebhookException(410,"Ce fichier non envoye a expire. Choisissez-le de nouveau avec une nouvelle cle.");
                        // A crash after the final rename can be reconciled without
                        // writing a second payload or changing the selected file.
                        if(prior.State!="ready"&&File.Exists(PayloadFor(store,id))&&VerifyPayload(store,prior)){prior.State="ready";prior.CleanupError=null;store.WriteRecord(RecordName(id),prior);}
                        if(prior.State!="ready"||!VerifyPayload(store,prior))throw new WebhookException(409,"Le premier transfert est incomplet ou a change. Aucun nouveau fichier cree.");
                        return Receipt(prior,true);
                    }
                    var records=Records(store);if(records.Length>=MaximumRecords)throw new WebhookException(507,"Le registre des pieces jointes est plein. Liberez les transferts inutilises dans le Switcher.");
                    long stored=records.Where(r=>!r.PayloadRemoved).Sum(r=>r.Size),staged=records.Where(r=>r.RouteId==route.Id&&!r.Used&&!r.PayloadRemoved).Sum(r=>r.Size);
                    if(stored+bytes.LongLength>MaximumStoredBytes||staged+bytes.LongLength>MaximumStagedRouteBytes)throw new WebhookException(507,"Le stockage des pieces jointes a atteint sa limite. Les fichiers envoyes sont conserves.");
                    long free=freeBytes();if(free<0||free-bytes.LongLength<MinimumFreeBytes)throw new WebhookException(507,"Moins de 20 Gio resteraient libres. Liberez de l'espace avant d'envoyer ce fichier.");
                    string directory=DirectoryFor(store,id);SafeFiles.PrivateDirectory(System.IO.Path.GetDirectoryName(directory));SafeFiles.PrivateDirectory(directory);
                    var record=new PortalAttachmentRecord{Id=id,RouteId=route.Id,InstanceId=grant.InstanceId,AccountKey=grant.AccountKey,Home=System.IO.Path.GetFullPath(grant.Home),Fingerprint=fingerprint,Name=name,RelativePath=relative,MimeType=mime,Size=bytes.LongLength,Sha256=hash,CreatedUtc=now.ToUniversalTime().ToString("o"),State="staging"};
                    using(var self=Process.GetCurrentProcess()){record.OwnerPid=self.Id;record.OwnerStarted=self.StartTime.ToUniversalTime().Ticks;}
                    store.WriteRecord(RecordName(id),record);string part=System.IO.Path.Combine(directory,"upload.part");SafeFiles.RejectLinks(part);
                    try{using(var output=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}SafeFiles.RejectLinks(directory);File.Move(part,PayloadFor(store,id));record.State="ready";store.WriteRecord(RecordName(id),record);}
                    catch{record.State="abandoned";record.CleanupError="upload-interrupted";store.WriteRecord(RecordName(id),record);throw new WebhookException(500,"Le transfert du fichier a ete interrompu. Aucun message envoye.");}
                    return Receipt(record,false);
                }
            }finally{Array.Clear(bytes,0,bytes.Length);}
        }
        private static object Receipt(PortalAttachmentRecord r,bool replayed)
        {return new{attachment=new{id=r.Id,name=r.Name,relativePath=r.RelativePath,size=r.Size,mimeType=r.MimeType},replayed=replayed};}
        internal static PortalAttachmentRef[] Require(RelayStore store,string routeId,PortalInstanceGrant grant,IEnumerable<string> ids)
        {using(Lease(store))return RequireLocked(store,routeId,grant,ids);}
        internal static object[] PublicMetadata(RelayStore store,string routeId,PortalInstanceGrant grant,IEnumerable<string> ids)
        {
            var selected=(ids??new string[0]).Take(MaximumFiles+1).ToArray();if(selected.Length>MaximumFiles||selected.Distinct(StringComparer.Ordinal).Count()!=selected.Length)throw new WebhookException(400,"Liste de pieces jointes invalide.");
            return selected.Select(id=>{Id(id);var r=store.ReadRecord<PortalAttachmentRecord>(RecordName(id));if(r.Id!=id)throw new WebhookException(404,"Piece jointe introuvable.");Binding(r,routeId,grant);if(r.State=="expired")throw new WebhookException(410,"Une piece jointe a expire.");return (object)new{id=r.Id,name=r.Name,relativePath=r.RelativePath,size=r.Size,mimeType=r.MimeType};}).ToArray();
        }
        private static PortalAttachmentRef[] RequireLocked(RelayStore store,string routeId,PortalInstanceGrant grant,IEnumerable<string> ids)
        {
            var selected=(ids??new string[0]).Take(MaximumFiles+1).ToArray();if(selected.Length>MaximumFiles||selected.Distinct(StringComparer.Ordinal).Count()!=selected.Length)throw new WebhookException(400,"Choisissez au plus 10 pieces jointes distinctes.");
            var output=new List<PortalAttachmentRef>();long total=0;foreach(string id in selected){Id(id);var record=store.ReadRecord<PortalAttachmentRecord>(RecordName(id));if(record.Id!=id)throw new WebhookException(404,"Piece jointe introuvable.");Binding(record,routeId,grant);if(record.State=="expired")throw new WebhookException(410,"Une piece jointe non envoyee a expire.");if(record.State!="ready"||!VerifyPayload(store,record))throw new WebhookException(409,"Une piece jointe est incomplete ou a change.");total=checked(total+record.Size);if(total>MaximumTurnBytes)throw new WebhookException(413,"La somme des pieces jointes depasse 24 Mio pour ce message.");output.Add(new PortalAttachmentRef{Id=record.Id,Name=record.Name,RelativePath=record.RelativePath,Size=record.Size,MimeType=record.MimeType,Path=PayloadFor(store,record.Id),Sha256=record.Sha256});}return output.ToArray();
        }
        // Claim before publishing the queued job. A failed job commit may retain
        // bytes, but cannot leave a real conversation pointing to deleted files.
        internal static void Claim(RelayStore store,string routeId,PortalInstanceGrant grant,IEnumerable<string> ids)
        {using(Lease(store)){var refs=RequireLocked(store,routeId,grant,ids);foreach(var item in refs){var record=store.ReadRecord<PortalAttachmentRecord>(RecordName(item.Id));record.Used=true;store.WriteRecord(RecordName(item.Id),record);}}}
        private static void Binding(PortalAttachmentRecord r,string routeId,PortalInstanceGrant grant)
        {if(r==null||grant==null||r.RouteId!=routeId||r.InstanceId!=grant.InstanceId||r.AccountKey!=grant.AccountKey||String.IsNullOrWhiteSpace(r.Home)||!RelayStore.SamePath(r.Home,grant.Home))throw new WebhookException(404,"Piece jointe introuvable pour cette instance.");Id(r.Id);if(r.Size<0||r.Size>MaximumFileBytes||!Regex.IsMatch(r.Sha256??"","\\A[a-f0-9]{64}\\z"))throw new WebhookException(409,"Metadonnees du fichier invalides.");}
        private static bool VerifyPayload(RelayStore store,PortalAttachmentRecord record)
        {
            string path=PayloadFor(store,record.Id);if(!File.Exists(path))return false;SafeFiles.RejectLinks(path);
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){if(input.Length!=record.Size)return false;using(var sha=SHA256.Create())return Hex(sha.ComputeHash(input))==record.Sha256;}
        }
        private static PortalAttachmentRecord[] Records(RelayStore store)
        {
            var names=Directory.EnumerateFiles(store.Root,"portal-attachment-*.dpapi",SearchOption.TopDirectoryOnly).Where(p=>Regex.IsMatch(System.IO.Path.GetFileName(p),"\\Aportal-attachment-[a-f0-9]{32}\\.dpapi\\z")).Take(MaximumRecords+1).ToArray();if(names.Length>MaximumRecords)throw new WebhookException(507,"Le registre des pieces jointes depasse sa limite.");
            return names.Select(p=>{SafeFiles.RejectLinks(p);var r=store.ReadRecord<PortalAttachmentRecord>(System.IO.Path.GetFileName(p));if(r==null||RecordName(r.Id)!=System.IO.Path.GetFileName(p)||r.Size<0||r.Size>MaximumFileBytes)throw new WebhookException(409,"Un transfert incomplet demande une verification locale.");return r;}).ToArray();
        }
        internal static object Cleanup(RelayStore store){return CleanupCore(store,DateTime.UtcNow,ProcessAlive);}
        internal static object CleanupCore(RelayStore store,DateTime now,Func<int,long,bool?> ownerAlive)
        {
            int examined=0,expired=0,retained=0,errors=0;long removed=0;
            using(Lease(store)){
                var cursor=store.ReadRecord<PortalAttachmentCleanupCursor>("portal-attachment-cleanup-v1.dpapi");
                foreach(var r in Records(store).OrderBy(r=>String.CompareOrdinal(r.Id,cursor.LastId)>0?0:1).ThenBy(r=>r.Id,StringComparer.Ordinal).Take(50)){
                    examined++;cursor.LastId=r.Id;DateTime created;
                    if(r.Used||r.State=="expired"||!DateTime.TryParse(r.CreatedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out created)||now.ToUniversalTime()-created.ToUniversalTime()<TimeSpan.FromHours(24)){retained++;continue;}
                    if(r.State=="staging"&&ownerAlive(r.OwnerPid,r.OwnerStarted)!=false){retained++;continue;}
                    if(!new[]{"ready","staging","abandoned"}.Contains(r.State)){retained++;continue;}
                    string directory=DirectoryFor(store,r.Id);var held=new List<SafeFileHandle>();var lengths=new List<long>();var parents=new List<SafeFileHandle>();long deletedBytes=0;
                    try{
                        if(Directory.Exists(directory))HoldParents(directory,parents);
                        var paths=Directory.Exists(directory)?Directory.GetFileSystemEntries(directory):new string[0];
                        if(paths.Any(p=>System.IO.Path.GetFileName(p)!=BlobName(r.MimeType)&&System.IO.Path.GetFileName(p)!="upload.part")){retained++;continue;}
                        foreach(string path in paths){var handle=Hold(path,false);held.Add(handle);using(var borrowed=new SafeFileHandle(handle.DangerousGetHandle(),false))using(var input=new FileStream(borrowed,FileAccess.Read)){lengths.Add(input.Length);if(System.IO.Path.GetFileName(path)==BlobName(r.MimeType)){if(input.Length!=r.Size)throw new IOException("Le contenu du fichier a change.");using(var sha=SHA256.Create())if(Hex(sha.ComputeHash(input))!=r.Sha256)throw new IOException("Le contenu du fichier a change.");}}}
                        // Persist the tombstone first. Even an interrupted cleanup
                        // cannot turn the same upload retry into a second file.
                        r.State="expired";r.CleanupError=null;store.WriteRecord(RecordName(r.Id),r);
                        for(int i=0;i<held.Count;i++){var deletion=new DeleteDisposition{DeleteFile=1};if(!SetFileInformationByHandle(held[i],4,ref deletion,(uint)Marshal.SizeOf(typeof(DeleteDisposition))))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());deletedBytes+=lengths[i];}foreach(var handle in held)handle.Dispose();held.Clear();
                        foreach(var handle in parents.AsEnumerable().Reverse())handle.Dispose();parents.Clear();
                        if(Directory.Exists(directory)&&Directory.GetFileSystemEntries(directory).Length==0)Directory.Delete(directory,false);
                        r.PayloadRemoved=true;store.WriteRecord(RecordName(r.Id),r);expired++;
                    }catch(Exception e){errors++;r.CleanupError=e is UnauthorizedAccessException?"cleanup-denied":"cleanup-unavailable";store.WriteRecord(RecordName(r.Id),r);}
                    finally{foreach(var handle in held)handle.Dispose();foreach(var handle in parents.AsEnumerable().Reverse())handle.Dispose();removed+=deletedBytes;}
                }
                store.WriteRecord("portal-attachment-cleanup-v1.dpapi",cursor);
            }
            return new{examined=examined,expired=expired,retained=retained,errors=errors,bytesRemoved=removed};
        }
        private static bool? ProcessAlive(int pid,long ticks)
        {if(pid<=0||ticks<=0)return null;try{using(var p=Process.GetProcessById(pid))return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==ticks;}catch(ArgumentException){return false;}catch(System.ComponentModel.Win32Exception){return null;}catch(InvalidOperationException){return false;}}
        private static string Digest(byte[] bytes){using(var sha=SHA256.Create())return Hex(sha.ComputeHash(bytes));}
        private static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        private static string Name(string name)
        {
            if(String.IsNullOrWhiteSpace(name)||name.Length>180||name=="."||name==".."||name.EndsWith(".",StringComparison.Ordinal)||name.EndsWith(" ",StringComparison.Ordinal)||Regex.IsMatch(name,"[<>:\"/\\\\|?*\\x00-\\x1f]")||Regex.IsMatch(name,"\\A(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\..*)?\\z",RegexOptions.IgnoreCase))throw new WebhookException(400,"Nom de fichier invalide. Aucun chemin local n'est accepte.");try{Utf8.GetByteCount(name);}catch(EncoderFallbackException){throw new WebhookException(400,"Encodage du nom de fichier invalide.");}return name;
        }
        private static string Relative(string relative,string name)
        {if(String.IsNullOrEmpty(relative)||relative.Length>1024)throw new WebhookException(400,"Chemin relatif du fichier invalide.");relative=relative.Replace('\\','/');var segments=relative.Split('/');foreach(string segment in segments)Name(segment);if(segments.Last()!=name)throw new WebhookException(400,"Le nom ne correspond pas au chemin relatif du fichier.");return String.Join("/",segments);}
        private static string Mime(string mime)
        {if(String.IsNullOrEmpty(mime)||mime.Length>120||!Regex.IsMatch(mime,"\\A[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]*/[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]*\\z"))throw new WebhookException(400,"Type MIME invalide.");return mime.ToLowerInvariant();}
        private static byte[] Decode(string text)
        {
            if(text==null||text.Length>4*((MaximumFileBytes+2)/3))throw new WebhookException(413,"Le fichier depasse 8 Mio.");
            if(text.Length%4!=0)throw new WebhookException(400,"Le fichier doit utiliser un base64 standard complet.");
            int pad=text.EndsWith("==",StringComparison.Ordinal)?2:text.EndsWith("=",StringComparison.Ordinal)?1:0;
            for(int i=0;i<text.Length-pad;i++)if(!(text[i]>='A'&&text[i]<='Z'||text[i]>='a'&&text[i]<='z'||text[i]>='0'&&text[i]<='9'||text[i]=='+'||text[i]=='/'))throw new WebhookException(400,"Contenu base64 invalide.");
            if(text.Length>0&&pad>0){const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";int last=alphabet.IndexOf(text[text.Length-pad-1]);if(last<0||(pad==2&&(last&15)!=0)||(pad==1&&(last&3)!=0))throw new WebhookException(400,"Base64 non canonique.");}
            byte[] bytes;try{bytes=Convert.FromBase64String(text);}catch(FormatException){throw new WebhookException(400,"Contenu base64 invalide.");}if(bytes.Length>MaximumFileBytes){Array.Clear(bytes,0,bytes.Length);throw new WebhookException(413,"Le fichier depasse 8 Mio.");}return bytes;
        }
        private static void CheckImage(string mime,byte[] b)
        {
            if(!mime.StartsWith("image/",StringComparison.Ordinal))return;
            bool ok=mime=="image/png"&&b.Length>=8&&b.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||mime=="image/jpeg"&&b.Length>=3&&b[0]==255&&b[1]==216&&b[2]==255||mime=="image/gif"&&b.Length>=6&&(Encoding.ASCII.GetString(b,0,6)=="GIF87a"||Encoding.ASCII.GetString(b,0,6)=="GIF89a")||mime=="image/webp"&&b.Length>=12&&Encoding.ASCII.GetString(b,0,4)=="RIFF"&&Encoding.ASCII.GetString(b,8,4)=="WEBP";
            if(!ok)throw new WebhookException(415,"Image invalide ou format non pris en charge. Utilisez PNG, JPEG, GIF ou WebP.");
        }
        // Only this bounded upload parser accepts large strings. General Codex,
        // webhook and configuration JSON limits remain unchanged.
        private sealed class UploadJson
        {
            private readonly string text;private int at;
            internal UploadJson(string value){try{if(value==null||Utf8.GetByteCount(value)>MaximumUploadBytes)throw new WebhookException(413,"Le transfert JSON depasse 12 Mio.");}catch(EncoderFallbackException){throw new WebhookException(400,"Encodage du fichier invalide.");}text=value;}
            private void Space(){while(at<text.Length&&" \t\r\n".IndexOf(text[at])>=0)at++;}
            private WebhookException Bad(){return new WebhookException(400,"Le fichier accepte uniquement name, mimeType, contentBase64 et relativePath optionnel (textes distincts).");}
            private void Take(char c){Space();if(at>=text.Length||text[at++]!=c)throw Bad();}
            private string ReadString()
            {
                Take('"');var result=new StringBuilder();bool done=false;while(at<text.Length){char c=text[at++];if(c=='"'){done=true;break;}if(c<32)throw Bad();if(c=='\\'){if(at>=text.Length)throw Bad();char e=text[at++];if(e=='u'){if(at+4>text.Length)throw Bad();int value=0;for(int i=0;i<4;i++){char h=text[at++];if(!Uri.IsHexDigit(h))throw Bad();value=value*16+(h<='9'?h-'0':Char.ToLowerInvariant(h)-'a'+10);}c=(char)value;}else{int pos="\"\\/bfnrt".IndexOf(e);if(pos<0)throw Bad();c="\"\\/\b\f\n\r\t"[pos];}}result.Append(c);}if(!done)throw Bad();return result.ToString();
            }
            internal Dictionary<string,string> Read()
            {
                var output=new Dictionary<string,string>(StringComparer.Ordinal);Take('{');Space();if(at<text.Length&&text[at]=='}')at++;else while(true){string key=ReadString();if(!new[]{"name","mimeType","contentBase64","relativePath"}.Contains(key)||output.ContainsKey(key))throw Bad();Take(':');output[key]=ReadString();Space();if(at>=text.Length)throw Bad();char c=text[at++];if(c=='}')break;if(c!=',')throw Bad();}Space();if(at!=text.Length||!output.ContainsKey("name")||!output.ContainsKey("mimeType")||!output.ContainsKey("contentBase64"))throw Bad();return output;
            }
        }
    }
}
