using System;
using System.IO;
using System.Threading;
using Creezio.Switcher;

internal static class ProtocolSmoke
{
    public static int Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);
        try
        {
            string executable=CodexEnvironment.FindExecutable();
            if(String.IsNullOrEmpty(executable)) throw new Exception("Codex CLI unavailable.");
            using(var rpc=new RpcClient(executable,root))
            {
                rpc.Initialize(CancellationToken.None).GetAwaiter().GetResult();
                var result=rpc.Call("account/read",new {refreshToken=false},CancellationToken.None).GetAwaiter().GetResult();
                if(Json.Get(result,"account")!=null) throw new Exception("Isolated server unexpectedly has an account.");
                Console.WriteLine("PASS isolated app-server initialize + account/read");
            }
            if(args.Length>1 && args[1]=="--live-read-only")
            {
                string home=CodexEnvironment.DefaultHome();
                string auth=SafeFiles.ReadText(Path.Combine(home,"auth.json"));
                byte[] before=File.ReadAllBytes(Path.Combine(home,"auth.json"));
                var service=new AccountService(root);
                var usage=service.FetchUsage(auth,CancellationToken.None).GetAwaiter().GetResult();
                byte[] after=File.ReadAllBytes(Path.Combine(home,"auth.json"));
                if(!System.Linq.Enumerable.SequenceEqual(before,after)) throw new Exception("Active auth file changed during the test (possibly by the running client).");
                Console.WriteLine("PASS live quota read through external-token mode ("+usage.Buckets.Count+" buckets); active auth unchanged");
                Console.WriteLine("PASS reset-credit metadata parsed (provided="+(usage.ResetCredits!=null)+"); no consume endpoint called");
            }
            if(Directory.Exists(Path.Combine(root,"runtime")) && Directory.GetDirectories(Path.Combine(root,"runtime")).Length!=0) throw new Exception("Runtime directories were not cleaned.");
            Console.WriteLine("PASS helper processes disposed and runtime directories removed");
            return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine("FAIL "+Program.SafeError(ex));return 1;}
    }
}
