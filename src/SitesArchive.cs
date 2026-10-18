using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // Only the declared Sites archive parameter is supported. Other connector
    // file parameters continue to fail closed in ToolTunnel.CheckTransport.
    internal interface IToolTunnelArchiveConnection
    {
        Task<object> InvokeArchive(string server,string tool,object arguments,Action validateBeforeSave,CancellationToken token);
    }
    internal sealed class SitesArchive : IDisposable
    {
        private readonly FileStream stream;
        internal readonly string Path,Hash;
        internal readonly long Bytes;
        private SitesArchive(string path,FileStream file,string hash)
        {Path=path;stream=file;Hash=hash;Bytes=file.Length;}
        internal static bool Supplied(TunnelTool tool,object args)
        {return tool!=null&&tool.Server=="codex_apps"&&(tool.Name=="sites.save_site_version"||tool.Name=="sites.save_version_and_deploy_private")&&Json.Obj(args).ContainsKey("archive");}
        internal static SitesArchive Open(TunnelTool tool,object args,RelaySession source)
        {
            var values=Json.Obj(args);
            if(!ToolTunnel.SupportsSitesRemoteBuild(tool)||values.Count!=3||!(Json.Get(args,"project_id") is string)||!Regex.IsMatch(Json.Str(Json.Get(args,"commit_sha")),"^[0-9a-f]{40}$"))
                throw new InvalidOperationException("L’archive Sites exige le contrat natif autorisé, le project_id exact et le SHA poussé complet.");
            if(RelayPermissions.Read(source.Home,source.Thread)!="full-access")
                throw new RelayPermissionException("Le transfert d’une archive exige Accès complet confirmé dans le chat appelant. Aucune permission n’a été modifiée.");
            string path=Json.Get(args,"archive") as string;
            if(String.IsNullOrWhiteSpace(path)||!System.IO.Path.IsPathRooted(path)||path.StartsWith("\\\\",StringComparison.Ordinal)||path.StartsWith("//",StringComparison.Ordinal)||!path.EndsWith(".tar.gz",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fournissez le chemin absolu de l’archive .tar.gz produite par le workflow Sites.");
            path=System.IO.Path.GetFullPath(path);SafeFiles.RejectLinks(path);
            if(path.Length<3||path[1]!=':'||path[2]!='\\'||path.Substring(2).Contains(":"))throw new InvalidOperationException("Le transfert exige une archive locale sur ce PC.");
            FileStream file=null;
            try {
                // Deny writes/deletion until the provider has returned. No copy of
                // the archive is made, and the hash binds the diagnostic receipt.
                file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(file.Length<=0||file.Length>512L*1024*1024)throw new InvalidOperationException("Archive Sites vide ou supérieure à 512 Mio.");
                if(file.ReadByte()!=0x1f||file.ReadByte()!=0x8b)throw new InvalidOperationException("Le fichier n’est pas une archive gzip Sites.");
                file.Position=0;string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
                return new SitesArchive(path,file,hash);
            } catch {if(file!=null)file.Dispose();throw;}
        }
        public void Dispose(){stream.Dispose();}
    }
}
