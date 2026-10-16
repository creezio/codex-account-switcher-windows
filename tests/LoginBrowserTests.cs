using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Creezio.Switcher;

internal static class LoginBrowserTests
{
    [DllImport("shell32.dll",SetLastError=true)] private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string text,out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    private static string[] Args(string text)
    {
        int count;IntPtr ptr=CommandLineToArgvW("browser.exe "+text,out count);
        if(ptr==IntPtr.Zero)throw new Exception("Argument parser unavailable");
        try{return Enumerable.Range(1,count-1).Select(n=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr,n*IntPtr.Size))).ToArray();}finally{LocalFree(ptr);}
    }
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static void Refused(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected refusal");}
    internal static void RunAll(Action<string,Action> check,string root)
    {
        const string url="https://auth.openai.com/authorize?state=fixture-only&redirect_uri=http%3A%2F%2Flocalhost%3A1455";
        string data=Path.Combine(root,"browser-metadata");Directory.CreateDirectory(data);
        string state=Path.Combine(data,"Local State");
        foreach(string name in new[]{"Default","Profile 1","Profile 2","Profile 3"}){Directory.CreateDirectory(Path.Combine(data,name));File.WriteAllText(Path.Combine(data,name,"Preferences"),"{}");}
        check("login browser metadata names and existing folders only",()=>{
            File.WriteAllText(state,"{\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Personnel\"},\"Profile 1\":{\"name\":\"Studio\"},\"Profile 2\":{\"name\":\"Client\"},\"Missing\":{\"name\":\"Deleted\"},\"../escape\":{\"name\":\"Invalid\"}}}}");
            var profiles=LoginBrowsers.ReadProfiles(data);Assert(profiles.Length==4&&profiles.Single(p=>p.Directory=="Profile 1").Label=="Studio · Profile 1","wrong discovery");Assert(profiles.Any(p=>p.Directory=="Profile 3"),"fallback existing profile missing");
        });
        check("login corrupted metadata keeps existing profiles usable",()=>{File.WriteAllText(state,"{broken");Assert(LoginBrowsers.ReadProfiles(data).Length==4,"malformed state should not break login");});
        check("login default and manual choices remain explicit",()=>{
            var choices=LoginBrowsers.Basic();var manual=new LoginBrowserTarget(choices.Single(b=>b.Id=="manual"),null);Assert(manual.StartInfo(url)==null,"manual opened browser");
            var start=new LoginBrowserTarget(choices.Single(b=>b.Id=="system"),null).StartInfo(url);Assert(start.UseShellExecute&&start.FileName==url,"Windows default lost");
        });
        var browser=new LoginBrowser{Id="chrome",Executable=System.Reflection.Assembly.GetExecutingAssembly().Location,DataDirectory=data};
        check("login selected profile and full URL survive Windows argument parsing",()=>{
            var start=new LoginBrowserTarget(browser,"Profile 2").StartInfo(url);var args=Args(start.Arguments);
            Assert(!start.UseShellExecute&&args.SequenceEqual(new[]{"--user-data-dir="+data,"--profile-directory=Profile 2",url}),"profile or URL changed");
        });
        check("login Windows quoting handles spaces quotes and trailing slashes",()=>{
            var values=new[]{"",@"C:\Un dossier\", "équipe \"test\"",@"a\\\",url};Assert(Args(String.Join(" ",values.Select(LoginBrowserTarget.Quote))).SequenceEqual(values),"incorrect quoting");
        });
        check("login removed profile refuses silent default fallback",()=>Refused(()=>new LoginBrowserTarget(browser,"Deleted").StartInfo(url)));
        check("login profile traversal refuses before process creation",()=>{foreach(string name in new[]{"..",@"..\Default","a/b","a\" --flag","a\n"})Refused(()=>new LoginBrowserTarget(browser,name).StartInfo(url));});
        check("login removed browser refuses silent default fallback",()=>Refused(()=>new LoginBrowserTarget(new LoginBrowser{Id="opera",Executable=Path.Combine(root,"missing-opera.exe")},null).StartInfo(url)));
        check("login rejects foreign malformed or local authorization URL",()=>{
            foreach(string invalid in new[]{"https://auth.openai.com.evil.example/","http://auth.openai.com/","https://user@auth.openai.com/","https://auth.openai.com:444/","file:///C:/Windows/notepad.exe",url+"\n"})Refused(()=>new LoginBrowserTarget(LoginBrowsers.Basic()[0],null).StartInfo(invalid));
        });
        check("login settings compatibility and no authorization URL persistence",()=>{
            var vault=new Vault(Path.Combine(root,"browser-settings"));var settings=Json.Read<Settings>("{\"Notifications\":true}");Assert(settings.LoginBrowserId==null,"old settings changed");
            settings.LoginBrowserId="chrome";settings.LoginBrowserProfile="Profile 2";vault.SaveSettings(settings);var saved=vault.LoadSettings();Assert(saved.LoginBrowserId=="chrome"&&saved.LoginBrowserProfile=="Profile 2"&&!File.ReadAllText(Path.Combine(vault.Root,"settings.json")).Contains("auth.openai.com"),"bad preference persistence");
        });
    }
}
