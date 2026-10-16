using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Creezio.Switcher
{
    public sealed class ProjectFileEntry
    {public string Relative {get;set;} public string Digest {get;set;} public long Length {get;set;}}
    public sealed class ProjectFileManifest
    {
        public string Source {get;set;} public string Destination {get;set;} public string Marker {get;set;}
        public ProjectFileEntry[] Files {get;set;} public string[] Directories {get;set;} public long Bytes {get;set;} public bool Completed {get;set;}
    }
    internal static class ProjectFiles
    {
        private static string Hash(string path){using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(file)).Replace("-","");}
        private static string Root(string path){if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))throw new InvalidOperationException("Choisissez un chemin absolu.");path=Path.GetFullPath(RolloutSnapshot.NativePath(path)).TrimEnd('\\');SafeFiles.RejectLinks(path);if(path.Length<=3||path.StartsWith(@"\\",StringComparison.Ordinal))throw new InvalidOperationException("Choisissez un dossier local, pas une racine de disque ou un partage réseau.");return path;}
        internal static string Map(ProjectFileManifest manifest,string source)
        {source=Root(source);if(RelayStore.SamePath(source,manifest.Source))return manifest.Destination;if(!RolloutSnapshot.Under(source,manifest.Source))throw new InvalidOperationException("Une conversation travaille hors du dossier du projet. Utilisez le dossier commun ou transférez cette conversation séparément.");return Inside(manifest.Destination,source.Substring(manifest.Source.Length+1));}
        private static string Inside(string root,string relative)
        {if(String.IsNullOrEmpty(relative)||Path.IsPathRooted(relative)||relative.Split('\\','/').Any(p=>p==".."||p==".")||relative.IndexOf(':')>=0)throw new InvalidOperationException("Chemin relatif de projet invalide.");string path=Path.GetFullPath(Path.Combine(root,relative));if(!RolloutSnapshot.Under(path,root))throw new InvalidOperationException("Chemin en dehors du dossier choisi.");SafeFiles.RejectLinks(path);return path;}
        private static ProjectFileManifest Scan(string source,CancellationToken token)
        {
            var files=new List<ProjectFileEntry>();var dirs=new List<string>();var pending=new Stack<string>();pending.Push(source);long bytes=0;
            while(pending.Count>0){token.ThrowIfCancellationRequested();string folder=pending.Pop();SafeFiles.RejectLinks(folder);
                foreach(string entry in Directory.EnumerateFileSystemEntries(folder)){
                    token.ThrowIfCancellationRequested();SafeFiles.RejectLinks(entry);string relative=entry.Substring(source.Length+1);
                    if(relative==".account-switcher-copy")throw new InvalidOperationException("Ce dossier contient déjà une copie de projet inachevée.");
                    if((File.GetAttributes(entry)&FileAttributes.Directory)!=0){dirs.Add(relative);pending.Push(entry);}
                    else {if(files.Count>=100000)throw new InvalidOperationException("Ce dossier contient plus de 100 000 fichiers. Utilisez le dossier commun ou préparez un clone Git séparément.");
                        if(String.Equals(relative,".git",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Ce dossier est un worktree Git. Préparez un clone Git indépendant avant d’importer ses conversations.");
                        var info=new FileInfo(entry);long length=info.Length;DateTime date=info.LastWriteTimeUtc;string hash=Hash(entry);info.Refresh();if(length!=info.Length||date!=info.LastWriteTimeUtc)throw new InvalidOperationException("Un fichier change pendant la préparation.");files.Add(new ProjectFileEntry{Relative=relative,Length=length,Digest=hash});bytes=checked(bytes+length);
                    }
                }
            }
            return new ProjectFileManifest{Source=source,Files=files.OrderBy(f=>f.Relative,StringComparer.OrdinalIgnoreCase).ToArray(),Directories=dirs.OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToArray(),Bytes=bytes};
        }
        internal static ProjectFileManifest Plan(string source,string destination,CancellationToken token)
        {return Plan(source,destination,token,null);}
        internal static ProjectFileManifest Plan(string source,string destination,CancellationToken token,Func<string,StorageBudgetVolume> probe)
        {
            source=Root(source);destination=Root(destination);if(!Directory.Exists(source))throw new InvalidOperationException("Le dossier source n’existe pas.");
            if(RelayStore.SamePath(source,destination)||RolloutSnapshot.Under(destination,source)||RolloutSnapshot.Under(source,destination))throw new InvalidOperationException("La copie doit être dans un dossier indépendant de la source.");
            if(Directory.Exists(destination)||File.Exists(destination))throw new InvalidOperationException("Le dossier de destination doit être nouveau pour protéger les fichiers existants.");
            var result=Scan(source,token);result.Destination=destination;result.Marker=Guid.NewGuid().ToString("N");Space(result,probe);return result;
        }
        private static void Space(ProjectFileManifest m,Func<string,StorageBudgetVolume> probe)
        {StorageBudget.Require(m.Destination,m.Bytes,"Copie indépendante du projet",StorageBudget.GiB,probe);}
        private static void VerifySource(ProjectFileManifest m,CancellationToken token)
        {var current=Scan(m.Source,token);if(Json.Write(current.Files)!=Json.Write(m.Files)||!current.Directories.SequenceEqual(m.Directories))throw new InvalidOperationException("Les fichiers sources ont changé depuis l’aperçu. La copie indépendante reste suspendue ; préparez une nouvelle opération.");}
        internal static void Copy(ProjectFileManifest m,Action checkpoint,CancellationToken token)
        {Copy(m,checkpoint,token,null);}
        internal static void Copy(ProjectFileManifest m,Action checkpoint,CancellationToken token,Func<string,StorageBudgetVolume> probe)
        {
            Root(m.Source);Root(m.Destination);VerifySource(m,token);Space(m,probe);string marker=Path.Combine(m.Destination,".account-switcher-copy");SafeFiles.RejectLinks(marker);
            if(!Directory.Exists(m.Destination)){Directory.CreateDirectory(m.Destination);using(var writer=new StreamWriter(new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None)))writer.Write(m.Marker);}
            if(!File.Exists(marker)||File.ReadAllText(marker)!=m.Marker)throw new InvalidOperationException("Ce dossier de destination n’appartient pas à cette opération. Aucun fichier ne sera écrasé.");
            foreach(string directory in m.Directories){token.ThrowIfCancellationRequested();Directory.CreateDirectory(Inside(m.Destination,directory));}
            foreach(var file in m.Files){token.ThrowIfCancellationRequested();string source=Inside(m.Source,file.Relative),target=Inside(m.Destination,file.Relative);if(File.Exists(target)){if(new FileInfo(target).Length!=file.Length||Hash(target)!=file.Digest)throw new InvalidOperationException("Un fichier de destination diffère. Aucun écrasement automatique : "+file.Relative);continue;}
                string temp=target+".switcher-"+m.Marker+".partial";SafeFiles.RejectLinks(temp);
                // This exact temporary belongs to the operation's verified marker.
                // Final destination files are never overwritten during recovery.
                if(File.Exists(temp))File.Delete(temp);
                try{
                    using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){byte[] buffer=new byte[65536];int count;while((count=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,count);}}
                    if(new FileInfo(temp).Length!=file.Length||Hash(temp)!=file.Digest)throw new InvalidOperationException("Copie temporaire non conforme : "+file.Relative);File.Move(temp,target);
                }finally{if(File.Exists(temp))File.Delete(temp);}
                if(new FileInfo(target).Length!=file.Length||Hash(target)!=file.Digest)throw new InvalidOperationException("Copie non conforme : "+file.Relative);
            }
            VerifySource(m,token);foreach(var file in m.Files){token.ThrowIfCancellationRequested();string target=Inside(m.Destination,file.Relative);if(!File.Exists(target)||new FileInfo(target).Length!=file.Length||Hash(target)!=file.Digest)throw new InvalidOperationException("Un fichier de destination a changé pendant la vérification.");}m.Completed=true;checkpoint();File.Delete(marker);
        }
    }
}
