using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Creezio.Switcher
{
    public sealed class SwitcherInstallationPointer
    {
        public int SchemaVersion {get;set;}
        public string Version {get;set;}
        public string Directory {get;set;}
        public string ManifestSha256 {get;set;}
        public string Transaction {get;set;}
    }
    internal static class SwitcherInstallation
    {
        private sealed class AdoptionOwner
        {public string Kind {get;set;}public string Root {get;set;}public string Version {get;set;}public string ManifestSha256 {get;set;}}
        internal const string RunName="Creezio Codex Account Switcher";
        internal static string Root {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Creezio","CodexAccountSwitcher");}}
        internal static string Launcher {get{return Path.Combine(Root,"Switcher.exe");}}
        internal static RelayStore Store(){return new RelayStore(RelayStore.DefaultRoot);}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern bool CreateHardLink(string path,string existing,IntPtr attributes);
        internal static string Digest(string path)
        {
            SafeFiles.RejectLinks(path);
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
        }
        internal static string VersionDirectory(string root,string version)
        {
            SwitcherUpdatePackages.RequireVersion(version);
            string path=Path.Combine(Path.GetFullPath(root),"versions",version);SafeFiles.RejectLinks(path);return path;
        }
        internal static SwitcherInstallationPointer Read(string root,bool verify)
        {
            string path=Path.Combine(root,"current.json");SafeFiles.RejectLinks(path);
            if(!File.Exists(path))return null;
            var value=Json.Read<SwitcherInstallationPointer>(SafeFiles.ReadText(path));
            if(value==null||value.SchemaVersion!=1||!RelayStore.SamePath(value.Directory,VersionDirectory(root,value.Version)))throw new InvalidOperationException("L'installation de Switcher doit être vérifiée.");
            if(verify&&!String.Equals(value.ManifestSha256,SwitcherUpdatePackages.ValidateDirectory(value.Directory,value.Version),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("L'application installée a changé depuis sa validation.");
            return value;
        }
        internal static void Write(string root,SwitcherInstallationPointer pointer)
        {
            if(pointer.SchemaVersion!=1||!RelayStore.SamePath(pointer.Directory,VersionDirectory(root,pointer.Version)))throw new InvalidOperationException("Destination d'installation invalide.");
            SafeFiles.AtomicWrite(Path.Combine(root,"current.json"),Encoding.UTF8.GetBytes(Json.Write(pointer)));
        }
        // First installation adopts the already validated portable payload. Only
        // immutable runtime files may share storage; application files stay private.
        internal static SwitcherInstallationPointer Adopt(string root,string source,string version)
        {
            string sourceHash=SwitcherUpdatePackages.ValidateDirectory(source,version);
            SafeFiles.PrivateDirectory(root);SafeFiles.PrivateDirectory(Path.Combine(root,"versions"));
            string target=VersionDirectory(root,version);
            if(!Directory.Exists(target)){
                string stage=Path.Combine(root,".adopt-stage-"+version),ownerPath=Path.Combine(root,".adopt-stage.json");
                SafeFiles.RejectLinks(stage);SafeFiles.RejectLinks(ownerPath);
                var owner=new AdoptionOwner{Kind="switcher-adoption-v1",Root=Path.GetFullPath(root),Version=version,ManifestSha256=sourceHash};
                if(File.Exists(ownerPath)){
                    var previousOwner=Json.Read<AdoptionOwner>(SafeFiles.ReadText(ownerPath));
                    if(previousOwner==null||Json.Write(previousOwner)!=Json.Write(owner))throw new IOException("Une installation précédente attend sa vérification.");
                }else{
                    if(Directory.Exists(stage))throw new IOException("Le dossier de préparation existe sans preuve d'origine. Il est conservé.");
                    SafeFiles.AtomicWrite(ownerPath,Encoding.UTF8.GetBytes(Json.Write(owner)));
                }
                var manifest=Json.Read<object>(SafeFiles.ReadText(Path.Combine(source,"switcher-update.json")));
                var immutable=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string runtimeMarker=Path.Combine(source,"RUNTIME-SHARED.json");
                if(File.Exists(runtimeMarker)){
                    var runtime=Json.Read<object>(SafeFiles.ReadText(runtimeMarker));
                    if(Object.Equals(Json.Get(runtime,"ImmutableRuntimeOnly"),true))foreach(var item in RelayEngine.Rows(Json.Get(runtime,"Files")))immutable.Add(Json.Str(Json.Get(item,"RelativePath")));
                }
                var drive=new DriveInfo(Path.GetPathRoot(root));if(drive.AvailableFreeSpace<20L*1024*1024*1024)throw new IOException("Libérez de l'espace disque avant l'installation (20 Gio requis).");
                SafeFiles.PrivateDirectory(stage);
                foreach(var item in RelayEngine.Rows(Json.Get(manifest,"files"))){
                    string relative=Json.Str(Json.Get(item,"path"));string from=Path.GetFullPath(Path.Combine(source,relative));string to=Path.GetFullPath(Path.Combine(stage,relative));
                    if(!from.StartsWith(Path.GetFullPath(source).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||!to.StartsWith(stage+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Fichier hors du paquet.");
                    SafeFiles.RejectLinks(from);SafeFiles.RejectLinks(to);Directory.CreateDirectory(Path.GetDirectoryName(to));
                    if(File.Exists(to)){
                        if(new FileInfo(to).Length==(long)Json.Number(Json.Get(item,"length")).Value&&Digest(to)==Json.Str(Json.Get(item,"sha256")))continue;
                        // Unlink an owned incomplete file; never truncate a hard link.
                        File.Delete(to);
                    }
                    bool shared=immutable.Contains(relative.Replace('/','\\'))&&relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&!relative.StartsWith("plugins/",StringComparison.OrdinalIgnoreCase)&&!relative.Equals("CodexAccountSwitcher.dll",StringComparison.OrdinalIgnoreCase)&&FileVersionInfo.GetVersionInfo(from).CompanyName=="Microsoft Corporation";
                    if(!shared||!CreateHardLink(to,from,IntPtr.Zero))File.Copy(from,to,false);
                }
                string stagedManifest=Path.Combine(stage,"switcher-update.json");
                if(File.Exists(stagedManifest)&&Digest(stagedManifest)!=sourceHash)File.Delete(stagedManifest);
                if(!File.Exists(stagedManifest))File.Copy(Path.Combine(source,"switcher-update.json"),stagedManifest,false);
                if(SwitcherUpdatePackages.ValidateDirectory(stage,version)!=sourceHash)throw new IOException("La préparation de l'application doit être vérifiée.");
                Directory.Move(stage,target);File.Delete(ownerPath);
            }
            string targetHash=SwitcherUpdatePackages.ValidateDirectory(target,version);
            if(targetHash!=sourceHash)throw new InvalidOperationException("Cette version existe déjà avec un contenu différent. Elle est conservée pour vérification.");
            string completedOwner=Path.Combine(root,".adopt-stage.json");SafeFiles.RejectLinks(completedOwner);
            if(File.Exists(completedOwner)&&!Directory.Exists(Path.Combine(root,".adopt-stage-"+version))){
                var owner=Json.Read<AdoptionOwner>(SafeFiles.ReadText(completedOwner));
                if(owner!=null&&owner.Kind=="switcher-adoption-v1"&&owner.Root==Path.GetFullPath(root)&&owner.Version==version&&owner.ManifestSha256==sourceHash)File.Delete(completedOwner);
            }
            string launcherSource=Path.Combine(target,"SwitcherLauncher.exe");if(!File.Exists(launcherSource))throw new IOException("Le lanceur stable manque dans le paquet.");
            if(!File.Exists(Path.Combine(root,"Switcher.exe")))SafeFiles.AtomicWrite(Path.Combine(root,"Switcher.exe"),File.ReadAllBytes(launcherSource));
            string launcherConfig=Path.Combine(root,"Switcher.exe.config");
            if(!File.Exists(launcherConfig))SafeFiles.AtomicWrite(launcherConfig,File.ReadAllBytes(Path.Combine(target,"SwitcherLauncher.exe.config")));
            var pointer=new SwitcherInstallationPointer{SchemaVersion=1,Version=version,Directory=target,ManifestSha256=targetHash};
            if(Read(root,false)==null)Write(root,pointer);
            return pointer;
        }
        internal static bool StartsWithWindows()
        {
            using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key!=null&&String.Equals(key.GetValue(RunName) as string,RelayWorker.Quote(Launcher)+" --startup",StringComparison.OrdinalIgnoreCase);
        }
        internal static void SetStartup(bool enabled)
        {
            if(enabled&&!File.Exists(Launcher))throw new InvalidOperationException("Installez d'abord l'entrée stable de Switcher.");
            using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){
                if(enabled)key.SetValue(RunName,RelayWorker.Quote(Launcher)+" --startup",RegistryValueKind.String);
                else key.DeleteValue(RunName,false);
            }
        }
        internal static void CreateShortcut()
        {
            string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"CodexAccountSwitcher.lnk");
            SafeFiles.RejectLinks(path);
            Type type=Type.GetTypeFromProgID("WScript.Shell");object shell=null,link=null;
            try{
                shell=Activator.CreateInstance(type);link=type.InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{path});
                var linkType=link.GetType();linkType.InvokeMember("TargetPath",System.Reflection.BindingFlags.SetProperty,null,link,new object[]{Launcher});
                linkType.InvokeMember("WorkingDirectory",System.Reflection.BindingFlags.SetProperty,null,link,new object[]{Root});
                linkType.InvokeMember("Description",System.Reflection.BindingFlags.SetProperty,null,link,new object[]{"Switcher — mises à jour automatiques"});
                linkType.InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,link,new object[0]);
            }finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
        }
    }
}
