using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Creezio.Switcher;

// Only fixture directories are passed to Adopt/Read/Write. No launcher is
// executed, no global installation is used, and no registry or shortcut is read.
internal static class SwitcherInstallationTests
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateHardLink(string destination,string source,IntPtr security);
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static void Reject(Action action){bool rejected=false;try{action();}catch(IOException){rejected=true;}catch(InvalidOperationException){rejected=true;}Assert(rejected,"Unowned or different installation was accepted");}
    private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    private static string HashFile(string path){return Hash(File.ReadAllBytes(path));}
    private static string Fingerprint(string folder){return String.Join("|",Directory.GetFiles(folder,"*",SearchOption.AllDirectories).OrderBy(path=>path,StringComparer.Ordinal).Select(path=>path.Substring(folder.Length)+":"+HashFile(path)));}
    private sealed class Fixture
    {
        internal readonly string Root,Source,Version=RelayWorker.Version,Target,Stage,OwnerPath,ManifestHash;
        internal Fixture(string sandbox)
        {
            string folder=Path.Combine(sandbox,"i"+Guid.NewGuid().ToString("N").Substring(0,5));Root=Path.Combine(folder,"install");Source=Path.Combine(folder,"source");Target=SwitcherInstallation.VersionDirectory(Root,Version);Stage=Path.Combine(Root,".adopt-stage-"+Version);OwnerPath=Path.Combine(Root,".adopt-stage.json");Directory.CreateDirectory(Source);
            byte[] product=File.ReadAllBytes(typeof(TestRunner).Assembly.Location);var files=new Dictionary<string,byte[]>{
                {"CodexAccountSwitcher.exe",Encoding.UTF8.GetBytes("Fictional apphost never executed")},{"CodexAccountSwitcher.dll",product},{"CodexAccountSwitcher.deps.json",Encoding.UTF8.GetBytes("{}")},{"CodexAccountSwitcher.runtimeconfig.json",Encoding.UTF8.GetBytes("{}")},{"CreezioRelay.exe",product},{"CreezioRelay.exe.config",Encoding.UTF8.GetBytes("<configuration/>")},{"SwitcherLauncher.exe",Encoding.UTF8.GetBytes("Fictional stable launcher never executed")},{"SwitcherLauncher.exe.config",Encoding.UTF8.GetBytes("<configuration/>")},
                {"coreclr.dll",new byte[]{1,2,3}},{"hostfxr.dll",new byte[]{4,5,6}},{"hostpolicy.dll",new byte[]{7,8,9}},{"PresentationFramework.dll",new byte[]{10,11,12}},{"plugins/creezio-relay/.codex-plugin/plugin.json",Encoding.UTF8.GetBytes("{}")},{"plugins/creezio-relay/.mcp.json",Encoding.UTF8.GetBytes("{}")},{"plugins/creezio-relay/ui/viewer.html",Encoding.UTF8.GetBytes("<html>Fictional fixture</html>")}};
            files["SwitcherCodexBridge.exe"]=product;files["SwitcherCodexBridge.exe.config"]=Encoding.UTF8.GetBytes("<configuration/>");
            foreach(var file in files){string path=Path.Combine(Source,file.Key.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,file.Value);}WriteManifest(Source);ManifestHash=SwitcherUpdatePackages.ValidateDirectory(Source,Version);
        }
        internal void WriteManifest(string directory)
        {
            var manifest=new SwitcherUpdatePackages.Manifest{schemaVersion=1,version=Version,platform="windows-x64",healthProtocol=1,entrypoint="CodexAccountSwitcher.exe",files=Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Where(path=>Path.GetFileName(path)!="switcher-update.json").Select(path=>new SwitcherUpdatePackages.ManifestFile{path=path.Substring(directory.Length+1).Replace('\\','/'),length=new FileInfo(path).Length,sha256=HashFile(path)}).OrderBy(file=>file.path,StringComparer.Ordinal).ToList()};File.WriteAllText(Path.Combine(directory,"switcher-update.json"),Json.Write(manifest),new UTF8Encoding(false));
        }
        internal void Owner(bool matching=true){Directory.CreateDirectory(Root);File.WriteAllText(OwnerPath,Json.Write(new{Kind="switcher-adoption-v1",Root=Path.GetFullPath(Root),Version=Version,ManifestSha256=matching?ManifestHash:new string('0',64)}),new UTF8Encoding(false));}
        internal void CopyPayload(string destination)
        {Directory.CreateDirectory(destination);foreach(string file in Directory.GetFiles(Source,"*",SearchOption.AllDirectories)){string to=Path.Combine(destination,file.Substring(Source.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(to));File.Copy(file,to,false);}}
        internal SwitcherInstallationPointer Adopt(){return SwitcherInstallation.Adopt(Root,Source,Version);}
    }
    internal static void RunAll(Action<string,Action> check,string sandbox)
    {
        check("stable installation adopts a validated local payload without modifying its source",()=>{
            var f=new Fixture(sandbox);string before=Fingerprint(f.Source);Directory.CreateDirectory(f.Root);string stable=Path.Combine(f.Root,"Switcher.exe"),orphan=AtomicFiles.Temporary(stable,Int32.MaxValue,1,"feedcafe"),reservation=stable+".switcher-tmp";File.WriteAllText(orphan,"Owned interrupted write");File.WriteAllText(reservation,AtomicFiles.Reservation(Int32.MaxValue,1,"feedcafe"));var pointer=f.Adopt();var saved=SwitcherInstallation.Read(f.Root,true);
            Assert(pointer.Directory==f.Target&&saved.Directory==f.Target&&saved.Version==f.Version&&saved.SchemaVersion==1&&saved.ManifestSha256==f.ManifestHash,"Initial stable pointer was not validated");Assert(HashFile(stable)==HashFile(Path.Combine(f.Source,"SwitcherLauncher.exe")),"Stable launcher was not copied from its validated payload");Assert(HashFile(Path.Combine(f.Root,"Switcher.exe.config"))==HashFile(Path.Combine(f.Source,"SwitcherLauncher.exe.config")),"Stable launcher configuration was not published from the validated payload");Assert(!File.Exists(orphan)&&!File.Exists(reservation),"Owned atomic launcher temporaries were not recovered");Assert(before==Fingerprint(f.Source)&&!Directory.Exists(f.Stage)&&!File.Exists(f.OwnerPath),"Adoption changed source files or left completed staging");
        });
        check("stable installation resumes owned partial files and never truncates a linked source",()=>{
            var f=new Fixture(sandbox);string sourceBefore=Fingerprint(f.Source);f.Owner();Directory.CreateDirectory(f.Stage);string core=Path.Combine(f.Stage,"coreclr.dll");File.Copy(Path.Combine(f.Source,"coreclr.dll"),core);var preservedTime=new DateTime(2001,2,3,4,5,6,DateTimeKind.Utc);File.SetLastWriteTimeUtc(core,preservedTime);
            string protectedFile=Path.Combine(Path.GetDirectoryName(f.Root),"protected.txt");File.WriteAllText(protectedFile,"Original independent bytes");Assert(CreateHardLink(Path.Combine(f.Stage,"CreezioRelay.exe"),protectedFile,IntPtr.Zero),"Could not create linked partial fixture");string protectedBefore=HashFile(protectedFile);File.WriteAllText(Path.Combine(f.Stage,"switcher-update.json"),"Incomplete old manifest");
            var pointer=f.Adopt();Assert(pointer.ManifestSha256==f.ManifestHash&&SwitcherInstallation.Read(f.Root,true).Directory==f.Target,"Owned interrupted adoption did not finish");Assert(File.GetLastWriteTimeUtc(Path.Combine(f.Target,"coreclr.dll"))==preservedTime,"Already verified partial file was copied again");Assert(HashFile(protectedFile)==protectedBefore&&Fingerprint(f.Source)==sourceBefore,"Partial recovery wrote through a hard link or changed source files");Assert(!File.Exists(f.OwnerPath)&&!Directory.Exists(f.Stage),"Completed partial stage remained");
        });
        check("stable installation preserves unknown partial stages and mismatched ownership proofs",()=>{
            foreach(bool owner in new[]{false,true}){
                var f=new Fixture(sandbox);Directory.CreateDirectory(f.Stage);string keep=Path.Combine(f.Stage,"unknown.txt");File.WriteAllText(keep,"Not owned by this adoption");if(owner)f.Owner(false);string before=Fingerprint(f.Stage),sourceBefore=Fingerprint(f.Source),ownerBefore=owner?HashFile(f.OwnerPath):null;
                Reject(()=>f.Adopt());Assert(before==Fingerprint(f.Stage)&&sourceBefore==Fingerprint(f.Source)&&!Directory.Exists(f.Target)&&!File.Exists(Path.Combine(f.Root,"current.json")),"Unknown stage was changed or exposed as installed");if(owner)Assert(HashFile(f.OwnerPath)==ownerBefore,"Mismatched ownership proof was replaced");
            }
        });
        check("stable installation never replaces an occupied version with a different valid payload",()=>{
            var f=new Fixture(sandbox);f.CopyPayload(f.Target);File.WriteAllText(Path.Combine(f.Target,"plugins","creezio-relay","ui","viewer.html"),"Different previously validated payload");f.WriteManifest(f.Target);string different=SwitcherUpdatePackages.ValidateDirectory(f.Target,f.Version);Assert(different!=f.ManifestHash,"Occupied version fixture has identical manifest");SwitcherInstallation.Write(f.Root,new SwitcherInstallationPointer{SchemaVersion=1,Version=f.Version,Directory=f.Target,ManifestSha256=different});string before=Fingerprint(f.Target),pointerBefore=HashFile(Path.Combine(f.Root,"current.json")),sourceBefore=Fingerprint(f.Source);
            Reject(()=>f.Adopt());Assert(before==Fingerprint(f.Target)&&pointerBefore==HashFile(Path.Combine(f.Root,"current.json"))&&sourceBefore==Fingerprint(f.Source),"Occupied version pointer or payload was replaced");
        });
        check("stable installation recovers a completed move and removes only its matching stale owner",()=>{
            var f=new Fixture(sandbox);f.CopyPayload(f.Target);f.Owner();string sourceBefore=Fingerprint(f.Source),targetBefore=Fingerprint(f.Target);var pointer=f.Adopt();Assert(pointer.ManifestSha256==f.ManifestHash&&SwitcherInstallation.Read(f.Root,true).Directory==f.Target,"Completed move did not recover its stable pointer");Assert(!File.Exists(f.OwnerPath)&&targetBefore==Fingerprint(f.Target)&&sourceBefore==Fingerprint(f.Source),"Matching stale owner remained or validated payload was rewritten");
            f.Owner(false);string foreign=HashFile(f.OwnerPath);try{f.Adopt();}catch(IOException){}Assert(File.Exists(f.OwnerPath)&&HashFile(f.OwnerPath)==foreign,"Unrelated stale owner was removed");
        });
    }
}
