using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Text;
using Creezio.Switcher;

internal static class IntegrationSmoke
{
    private static object Rpc(Process process,int id,string method,object parameters)
    {
        process.StandardInput.WriteLine(Json.Write(new{id=id,method=method,@params=parameters}));process.StandardInput.Flush();
        DateTime until=DateTime.UtcNow.AddSeconds(40);
        while(DateTime.UtcNow<until){var read=process.StandardOutput.ReadLineAsync();if(!read.Wait(40000))throw new Exception("Codex read-only probe timed out");string line=read.Result;if(line==null)throw new Exception("Codex closed probe");var value=Json.Read<object>(line);if(Convert.ToString(Json.Get(value,"id"))==id.ToString()&&Json.Get(value,"method")==null){if(Json.Get(value,"error")!=null)throw new Exception("Codex RPC rejected "+method+": "+Json.Write(Json.Get(value,"error")));return Json.Get(value,"result");}}
        throw new Exception("Probe timed out");
    }
    public static int Main(string[] args)
    {
        try{
            string root=Path.GetFullPath(args[0]);SafeFiles.PrivateDirectory(root);string home=Path.Combine(root,"codex-home");SafeFiles.PrivateDirectory(home);
            if(!File.Exists(Path.Combine(home,"config.toml")))File.WriteAllText(Path.Combine(home,"config.toml"),"cli_auth_credentials_store = \"file\"\n[analytics]\nenabled = false\n");
            var store=new RelayStore(Path.Combine(root,"relay"));string executable=CodexEnvironment.FindExecutable();
            var first=RelayIntegration.Install(store,home,executable,true,CancellationToken.None).GetAwaiter().GetResult();
            var second=RelayIntegration.Install(store,home,executable,false,CancellationToken.None).GetAwaiter().GetResult();
            if(first.Fingerprint!=second.Fingerprint)throw new Exception("Installation not idempotent");
            if(!second.Healthy||first.InstalledVersion!=second.InstalledVersion)throw new Exception("Healthy verification rewrote plugin");
            string installedSkill=Path.Combine(home,"plugins","cache",RelayIntegration.Market,RelayIntegration.Name,second.InstalledVersion,"skills","delegate-task","SKILL.md");
            File.WriteAllText(installedSkill,"damaged test skill");
            var repaired=RelayIntegration.Install(store,home,executable,false,CancellationToken.None).GetAwaiter().GetResult();
            if(!repaired.Healthy||repaired.InstalledVersion==second.InstalledVersion)throw new Exception("Missing cache repair");
            Console.WriteLine("PASS damaged cached skill repaired without modifying cache used by existing chats");
            string configPath=Path.Combine(home,"config.toml"),config=File.ReadAllText(configPath);
            string disabled=System.Text.RegularExpressions.Regex.Replace(config,"(\\[plugins\\.\"creezio-relay@creezio-switcher\"\\]\\s*enabled\\s*=\\s*)true","${1}false");
            if(disabled==config)throw new Exception("Fixture plugin configuration not found");File.WriteAllText(configPath,disabled);
            var preserved=RelayIntegration.Install(store,home,executable,false,CancellationToken.None).GetAwaiter().GetResult();
            var off=RelayIntegration.FindPlugin(RelayIntegration.Cli(executable,home,"plugin list --marketplace "+RelayIntegration.Market+" --json",CancellationToken.None).GetAwaiter().GetResult());
            if(!Object.Equals(Json.Get(off,"enabled"),false))throw new Exception("Automatic install re-enabled disabled plugin");
            RelayIntegration.Install(store,home,executable,true,CancellationToken.None).GetAwaiter().GetResult();
            var on=RelayIntegration.FindPlugin(RelayIntegration.Cli(executable,home,"plugin list --marketplace "+RelayIntegration.Market+" --json",CancellationToken.None).GetAwaiter().GetResult());
            if(!Object.Equals(Json.Get(on,"enabled"),true))throw new Exception("Explicit reinstall did not enable plugin");
            Console.WriteLine("PASS disabled plugin respected; explicit reinstall re-enabled it");
            Console.WriteLine(Json.Write(new{ok=true,status=second.Status,marketplace=second.Marketplace}));
            var inventory=RelayIntegration.Inventory(executable,home,CancellationToken.None).GetAwaiter().GetResult();Console.WriteLine(Json.Write(inventory));
            var start=new ProcessStartInfo(executable,"app-server --listen stdio://"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=root};start.EnvironmentVariables["CODEX_HOME"]=home;start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;
            foreach(string key in new[]{"CODEX_THREAD_ID","CODEX_APP_TOOLS_PIPE_PATH","OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_IDENTITY_TOKEN","OPENAI_IDENTITY_TOKEN_FILE","OPENAI_IDENTITY_PROVIDER_ID","OPENAI_ORGANIZATION","CHATGPT_BASE_URL","CODEX_CHATGPT_BASE_URL"})start.EnvironmentVariables.Remove(key);
            using(var process=Process.Start(start)){
                process.ErrorDataReceived+=delegate{};process.BeginErrorReadLine();
                try{
                    Rpc(process,1,"initialize",new{clientInfo=new{name="creezio_integration_test",version=RelayWorker.Version},capabilities=new{experimentalApi=true}});
                    process.StandardInput.WriteLine(Json.Write(new{method="initialized"}));process.StandardInput.Flush();
                    var skills=Rpc(process,2,"skills/list",new{cwds=new[]{root},forceReload=true});
                    var names=RelayIntegration.Objects(skills).Select(x=>Json.Str(Json.Get(x,"name"))).Where(x=>new[]{"delegate-task","execute-relay-task","request-assistance","use-shared-tools"}.Any(n=>x.EndsWith(n))).Distinct().ToArray();
                    Console.WriteLine(Json.Write(new{skills=names}));if(names.Length!=4)throw new Exception("All four relay skills must load in Codex");
                    var servers=Rpc(process,3,"mcpServerStatus/list",new{detail="toolsAndAuthOnly"});
                    var expected=RelayMcp.Tools().Select(t=>Json.Str(Json.Get(Json.Read<object>(Json.Write(t)),"name"))).ToArray();
                    var tools=RelayIntegration.Objects(servers).Select(x=>Json.Str(Json.Get(x,"name"))).Where(x=>expected.Contains(x)).Distinct().ToArray();
                    Console.WriteLine(Json.Write(new{loadedTools=tools}));if(tools.Length!=20||expected.Except(tools).Any())throw new Exception("All twenty relay MCP tools must load in Codex");
                }finally{try{process.StandardInput.Close();if(!process.WaitForExit(3000))process.Kill();}catch{}}
            }
            return 0;
        }catch(Exception e){Console.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
}
