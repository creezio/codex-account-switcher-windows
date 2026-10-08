using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    // Read only the recorded permission context. Never edit Codex policy or approve commands.
    internal static class RelayPermissions
    {
        internal static string Describe(object context)
        {
            string sandbox=Json.Str(Json.Get(Json.Get(context,"sandbox_policy"),"type"));
            string approval=Json.Str(Json.Get(context,"approval_policy"));
            if(sandbox=="danger-full-access" && approval=="never")return "full-access";
            return sandbox.Length==0?"unknown":sandbox+" / "+approval;
        }
        internal static string Read(string home,string threadId)
        {
            if(!Regex.IsMatch(threadId??"","^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-7[a-fA-F0-9]{3}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$"))return "unknown";
            try {
                long ms=Convert.ToInt64(threadId.Replace("-","").Substring(0,12),16);
                var day=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ms);
                // UUID time is UTC; Codex groups rollout files by local calendar date.
                // Check adjacent dates too (including a changed timezone), not the whole history.
                var candidates=new System.Collections.Generic.List<string>();
                foreach(int shift in new[]{0,-1,1}){
                    var date=day.AddDays(shift);string directory=Path.Combine(home,"sessions",date.ToString("yyyy"),date.ToString("MM"),date.ToString("dd"));
                    SafeFiles.RejectLinks(directory);if(Directory.Exists(directory))candidates.AddRange(Directory.GetFiles(directory,"rollout-*"+threadId+".jsonl"));
                }
                string[] files=candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if(files.Length!=1)return "unknown";SafeFiles.RejectLinks(files[0]);
                using(var stream=new FileStream(files[0],FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    // Avoid loading large conversation histories or exposing any tool output.
                    long end=stream.Length,minimum=Math.Max(0,end-67108864);
                    while(end>minimum) {
                        long offset=Math.Max(minimum,end-2097152);stream.Position=offset;
                        byte[] bytes=new byte[(int)(end-offset)];int count=0;
                        while(count<bytes.Length){int n=stream.Read(bytes,count,bytes.Length-count);if(n==0)break;count+=n;}
                        using(var reader=new StringReader(Encoding.UTF8.GetString(bytes,0,count))) {
                            if(offset>0)reader.ReadLine();string line,result=null;
                            while((line=reader.ReadLine())!=null) {
                                if(line.Length>500000 || !line.Contains("\"turn_context\""))continue;
                                try {var item=Json.Read<object>(line);if(Json.Str(Json.Get(item,"type"))=="turn_context")result=Describe(Json.Get(item,"payload"));}catch(ArgumentException){}
                            }
                            if(result!=null)return result;
                        }
                        if(offset==minimum)break;
                        end=offset+500000; // overlap for a context split across block boundaries
                    }
                    return "unknown";
                }
            }catch(IOException){return "unknown";}catch(UnauthorizedAccessException){return "unknown";}catch(ArgumentException){return "unknown";}
        }
        internal static void Require(RelayChannel channel,string threadId)
        {
            if(channel.RequireFullAccess && Read(channel.Home,threadId)!="full-access")
                throw new InvalidOperationException("Le canal « "+channel.Id+" » exige Accès complet, mais cette conversation ne l'a pas confirmé. Dans cette conversation Codex, sélectionnez Accès complet, envoyez un message puis reconnectez le canal. Le relais ne change jamais les permissions automatiquement.");
        }
        internal static void RequireFull(RelayChannel channel,string threadId)
        {
            if(Read(channel.Home,threadId)!="full-access")throw new InvalidOperationException("Ce profil exige un accès complet confirmé dans la conversation destinataire.");
        }
    }
}
