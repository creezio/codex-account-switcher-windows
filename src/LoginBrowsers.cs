using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Creezio.Switcher
{
    internal sealed class LoginBrowserProfile
    {
        public string Directory { get; set; }
        public string Label { get; set; }
    }
    internal sealed class LoginBrowser
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Executable { get; set; }
        public string DataDirectory { get; set; }
        public LoginBrowserProfile[] Profiles { get; set; }
        public LoginBrowser() { Profiles = new LoginBrowserProfile[0]; }
    }
    // A submission snapshot: never holds a WPF control or an OAuth URL.
    internal sealed class LoginBrowserTarget
    {
        internal readonly string Id, Executable, DataDirectory, ProfileDirectory;
        internal LoginBrowserTarget(LoginBrowser browser, string profile)
        { Id=browser.Id; Executable=browser.Executable; DataDirectory=browser.DataDirectory; ProfileDirectory=profile; }
        internal static void ValidateUrl(string url)
        {
            Uri uri;
            if (String.IsNullOrEmpty(url) || url.Any(Char.IsControl) || !Uri.TryCreate(url,UriKind.Absolute,out uri) || uri.Scheme!="https" || uri.Host!="auth.openai.com" || !uri.IsDefaultPort || uri.UserInfo.Length!=0)
                throw new InvalidOperationException("Le lien de connexion Codex n’est pas valide. Relancez la connexion.");
        }
        internal ProcessStartInfo StartInfo(string url)
        {
            ValidateUrl(url);
            if(Id=="manual") return null;
            if(Id=="system") return new ProcessStartInfo(url) { UseShellExecute=true };
            if(String.IsNullOrEmpty(Executable) || !File.Exists(Executable))
                throw new InvalidOperationException("Ce navigateur n’est plus disponible. Copiez le lien ou choisissez un autre navigateur après annulation.");
            var args=new List<string>();
            if(!String.IsNullOrEmpty(ProfileDirectory)) {
                if(!LoginBrowsers.ValidProfileDirectory(ProfileDirectory) || String.IsNullOrEmpty(DataDirectory) || !Directory.Exists(Path.Combine(DataDirectory,ProfileDirectory)))
                    throw new InvalidOperationException("Ce profil n’est plus disponible. Copiez le lien ou choisissez un autre profil après annulation.");
                args.Add("--user-data-dir="+DataDirectory);
                args.Add("--profile-directory="+ProfileDirectory);
            }
            args.Add(url);
            return new ProcessStartInfo(Executable,String.Join(" ",args.Select(Quote))) { UseShellExecute=false };
        }
        internal void Open(string url)
        {
            var start=StartInfo(url);
            if(start!=null) using(var process=Process.Start(start)) { }
        }
        // Windows CommandLineToArgvW/CRT quoting, with no shell interpretation.
        internal static string Quote(string value)
        {
            var result=new StringBuilder("\""); int slashes=0;
            foreach(char c in value) {
                if(c=='\\') { slashes++; continue; }
                if(c=='\"') result.Append('\\',slashes*2+1).Append(c);
                else result.Append('\\',slashes).Append(c);
                slashes=0;
            }
            return result.Append('\\',slashes*2).Append('"').ToString();
        }
    }
    internal static class LoginBrowsers
    {
        internal static LoginBrowser[] Basic()
        { return new[]{new LoginBrowser{Id="system",Label="Navigateur par défaut de Windows"},new LoginBrowser{Id="manual",Label="Copier le lien · sans ouverture automatique"}}; }
        internal static bool ValidProfileDirectory(string name)
        { return !String.IsNullOrWhiteSpace(name) && name!="." && name!=".." && name.IndexOfAny(Path.GetInvalidFileNameChars())<0 && name.IndexOfAny(new[]{'/', '\\'})<0 && !name.Any(Char.IsControl); }
        internal static LoginBrowserProfile[] ReadProfiles(string root)
        {
            var profiles=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var excluded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                string state=Path.Combine(root,"Local State");
                if(File.Exists(state) && new FileInfo(state).Length<=4194304) {
                    var cache=Json.Obj(Json.Get(Json.Get(Json.Read<object>(SafeFiles.ReadText(state)),"profile"),"info_cache"));
                    foreach(var pair in cache) {
                        if(Object.Equals(Json.Get(pair.Value,"is_ephemeral"),true)) { excluded.Add(pair.Key); continue; }
                        if(!ValidProfileDirectory(pair.Key) || !Directory.Exists(Path.Combine(root,pair.Key))) continue;
                        string name=Json.Str(Json.Get(pair.Value,"name"));
                        profiles[pair.Key]=String.IsNullOrWhiteSpace(name)?"Profil principal":new string(name.Where(c=>!Char.IsControl(c)).Take(100).ToArray());
                    }
                }
            } catch(IOException) { } catch(UnauthorizedAccessException) { } catch(ArgumentException) { } catch(InvalidOperationException) { } catch(System.Runtime.Serialization.SerializationException) { }
#if NETCOREAPP
            catch(System.Text.Json.JsonException) { }
#endif
            // Metadata may be unavailable during a browser update. Only existing profile
            // folders are offered; never create a new profile by guessing an absent path.
            try {
                if(Directory.Exists(root)) foreach(var dir in Directory.GetDirectories(root)) {
                    string name=Path.GetFileName(dir);
                    if((name=="Default" || name.StartsWith("Profile ",StringComparison.Ordinal)) && File.Exists(Path.Combine(dir,"Preferences")) && !profiles.ContainsKey(name) && !excluded.Contains(name)) profiles[name]=name=="Default"?"Profil principal":name;
                }
            } catch(IOException) { } catch(UnauthorizedAccessException) { }
            return profiles.Select(p=>new LoginBrowserProfile{Directory=p.Key,Label=p.Value+" · "+p.Key}).OrderBy(p=>p.Label,StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        internal static LoginBrowser[] Discover()
        {
            var result=new List<LoginBrowser>(Basic());
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Add(result,"chrome","Google Chrome","chrome.exe",Path.Combine(local,@"Google\Chrome\User Data"), @"Google\Chrome\Application\chrome.exe");
            Add(result,"edge","Microsoft Edge","msedge.exe",Path.Combine(local,@"Microsoft\Edge\User Data"), @"Microsoft\Edge\Application\msedge.exe");
            Add(result,"brave","Brave","brave.exe",Path.Combine(local,@"BraveSoftware\Brave-Browser\User Data"), @"BraveSoftware\Brave-Browser\Application\brave.exe");
            Add(result,"vivaldi","Vivaldi","vivaldi.exe",Path.Combine(local,@"Vivaldi\User Data"), @"Vivaldi\Application\vivaldi.exe");
            Add(result,"opera","Opera","opera.exe",Path.Combine(roaming,@"Opera Software\Opera Stable"), @"Opera\opera.exe", @"Programs\Opera\opera.exe");
            Add(result,"opera-gx","Opera GX",null,Path.Combine(roaming,@"Opera Software\Opera GX Stable"), @"Opera GX\opera.exe", @"Programs\Opera GX\opera.exe");
            Add(result,"opera-air","Opera Air",null,Path.Combine(roaming,@"Opera Software\Opera Air Stable"), @"Opera Air\opera.exe", @"Programs\Opera Air\opera.exe");
            return result.ToArray();
        }
        private static void Add(List<LoginBrowser> result,string id,string label,string appPath,string data,params string[] relativePaths)
        {
            var candidates=new List<string>();
            // Prefer the known installation for this channel. App Paths may point
            // at another edition (for example Opera GX also registers opera.exe).
            foreach(var folder in new[]{Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86})
                foreach(string relative in relativePaths) candidates.Add(Path.Combine(Environment.GetFolderPath(folder),relative));
            if(appPath!=null) foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine}) {
                try { using(var key=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\"+appPath)) { string path=key==null?null:key.GetValue(null) as string; if(path!=null&&(id!="opera"||String.Equals(Path.GetFileName(Path.GetDirectoryName(path.Trim('"'))),"Opera",StringComparison.OrdinalIgnoreCase)))candidates.Add(path.Trim('"')); } }
                catch(System.Security.SecurityException) { } catch(UnauthorizedAccessException) { }
            }
            string exe=candidates.FirstOrDefault(File.Exists);
            if(exe!=null) result.Add(new LoginBrowser{Id=id,Label=label,Executable=exe,DataDirectory=data,Profiles=ReadProfiles(data)});
        }
    }
}
