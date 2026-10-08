using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class AssistanceHooks
    {
        private const string Label="Account Switcher assistance";
        internal static bool Matches(AssistanceRule rule,string prompt)
        {return rule!=null&&rule.Enabled&&rule.HookEnabled&&((rule.PromptLength>0&&prompt.Length>=rule.PromptLength)||RelayPolicies.Paths(rule.Keywords??"").Any(k=>prompt.IndexOf(k,StringComparison.OrdinalIgnoreCase)>=0));}
        public static string Install(RelayStore store,string home,string executable)
        {
            home=Path.GetFullPath(home);SafeFiles.RejectLinks(home);SafeFiles.RejectLinks(executable);
            if(!File.Exists(executable))throw new InvalidOperationException("Moteur d'assistance absent.");
            string path=Path.Combine(home,"hooks.json");SafeFiles.RejectLinks(path);
            using(store.Lease("assistance-hook-"+RelayIntegration.HomeKey(home))){
                var root=File.Exists(path)?Json.Obj(Json.Read<object>(SafeFiles.ReadText(path))):new Dictionary<string,object>();
                var hooks=Json.Get(root,"hooks")==null?new Dictionary<string,object>():Json.Obj(Json.Get(root,"hooks"));
                var groups=RelayEngine.Rows(Json.Get(hooks,"UserPromptSubmit")).ToList();
                // Update only our own handler, preserving all other events and handlers.
                groups=groups.Where(g=>!RelayEngine.Rows(Json.Get(g,"hooks")).Any(h=>Json.Str(Json.Get(h,"statusMessage"))==Label)||RelayEngine.Rows(Json.Get(g,"hooks")).Any(h=>Json.Str(Json.Get(h,"statusMessage"))!=Label)).ToList();
                foreach(var group in groups){var map=Json.Obj(group);map["hooks"]=RelayEngine.Rows(Json.Get(group,"hooks")).Where(h=>Json.Str(Json.Get(h,"statusMessage"))!=Label).ToArray();}
                string command="& '"+executable.Replace("'","''")+"' --assistance-hook '"+store.Root.Replace("'","''")+"' '"+home.Replace("'","''")+"'";
                groups.Add(new{hooks=new[]{new{type="command",command=command,commandWindows=command,statusMessage=Label,timeout=25}}});hooks["UserPromptSubmit"]=groups.ToArray();root["hooks"]=hooks;
                SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes(Json.Write(root)));return path;
            }
        }
        internal static async Task<object> Evaluate(RelayStore store,string home,object input,CancellationToken token,IRelayTransport adapter=null)
        {
            if(Json.Str(Json.Get(input,"hook_event_name"))!="UserPromptSubmit")return null;
            string prompt=Json.Str(Json.Get(input,"prompt")),thread=Json.Str(Json.Get(input,"session_id")),cwd=Json.Str(Json.Get(input,"cwd"));
            if(prompt.Length>64000||String.IsNullOrEmpty(thread)||String.IsNullOrEmpty(cwd))return null;
            var channels=store.Channels().Where(c=>c.Enabled&&AgentProviders.Codex(c)&&RelayStore.SamePath(c.Home,home)&&RelayStore.SamePath(c.Workspace,cwd)).ToArray();
            var rules=Assistance.Rules(store);var channel=channels.FirstOrDefault(c=>Matches(rules.FirstOrDefault(r=>r.Channel==c.Id),prompt));if(channel==null)return null;
            var rule=rules.First(r=>r.Channel==channel.Id);var assistance=new Assistance(store,adapter);
            try{
                // Only an exact persisted, currently sending assistance reply bypasses
                // this business rule. Merely typing a relay marker grants nothing.
                var match=System.Text.RegularExpressions.Regex.Match(prompt,@"^\[CREEZIO_REQUEST:([a-f0-9]{32})\]");
                if(match.Success&&store.Exists(match.Groups[1].Value)){
                    var m=store.Message(match.Groups[1].Value);
                    if(m.OperatorOrigin&&m.State=="sending"&&m.TargetChannelId==channel.Id&&m.TargetThreadId==thread&&m.TargetBinding==AgentProviders.Binding(channel)&&assistance.List().Any(t=>t.Channel==channel.Id&&t.Thread==thread&&t.ReplyJob==m.Id&&t.Answer!=null)&&prompt==RelayEngine.PreparedPrompt(m,store.Channel(m.SourceChannelId),channel,RelayPolicies.Load(store)))return null;
                }
                string id=RemotePeers.Hash(channel.Id+"\n"+thread+"\n"+Json.Str(Json.Get(input,"turn_id"))+"\n"+prompt).Substring(0,32);
                string instructions=rule.Instructions??"";
                await assistance.Request(id,channel.Id,thread,"Règle d'assistance · "+channel.Id,"Un critère configuré correspond à ce prompt. "+instructions.Substring(0,Math.Min(3500,instructions.Length)),prompt.Substring(0,Math.Min(16000,prompt.Length)),false,token);
                store.WriteRecord("assistance-hook-seen.dpapi",new{at=DateTime.UtcNow.ToString("o"),channel=channel.Id});
                string note="Demande d'assistance "+id+" enregistrée dans Account Switcher. ";
                return rule.HoldPrompt?(object)new{decision="block",reason=note+"Le responsable peut répondre depuis Assistance."}:new{systemMessage=note+"Le prompt reste autorisé par cette règle."};
            }catch(Exception){if(rule.HoldPrompt)return new{decision="block",reason="La règle d'assistance correspond, mais sa transmission a échoué. Ouvrez Account Switcher pour vérifier la connexion."};return new{systemMessage="Transmission de la demande d'assistance impossible. Vérifiez Account Switcher."};}
        }
        public static int Run(string root,string home)
        {
            try{string input;using(var reader=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8)){var buffer=new char[131073];int total=0,n;while(total<buffer.Length&&(n=reader.Read(buffer,total,buffer.Length-total))>0)total+=n;if(total==buffer.Length)throw new InvalidOperationException();input=new string(buffer,0,total);}
                using(var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20))){var result=Evaluate(new RelayStore(root),home,Json.Read<object>(input),stop.Token).GetAwaiter().GetResult();if(result!=null)Console.WriteLine(Json.Write(result));}return 0;
            }catch{Console.Error.WriteLine("Hook d'assistance impossible à vérifier. Vérifiez la configuration du switcher.");return 2;}
        }
    }
}
