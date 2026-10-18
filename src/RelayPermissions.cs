using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace Creezio.Switcher
{
    internal sealed class RelayPermissionException : InvalidOperationException
    {
        internal RelayPermissionException(string message) : base(message) { }
    }
    // Permission changes are bounded explicit user selections for one chat.
    // This component never writes global configuration or approves commands.
    internal static class RelayPermissions
    {
        internal static bool IsExplicitProfile(string profile)
        {return profile!=null&&profile!="inherit"&&new[]{"read-only","workspace-write","full-access"}.Contains(profile);}
        internal static Dictionary<string,object> NativeProfile(string profile,string workspace)
        {
            var result=new Dictionary<string,object>();if(profile==null||profile=="inherit")return result;if(!IsExplicitProfile(profile))throw new InvalidOperationException("Profil de permissions non reconnu.");
            result["approvalPolicy"]=profile=="full-access"?"never":"on-request";
            result["sandboxPolicy"]=profile=="full-access"?(object)new{type="dangerFullAccess"}:profile=="read-only"?(object)new{type="readOnly",networkAccess=false}:new{type="workspaceWrite",writableRoots=new[]{RelayStore.WorkspacePath(workspace)},networkAccess=false};return result;
        }
        internal static bool MatchesProfile(object context,string profile)
        {
            if(profile==null||profile=="inherit")return true;if(!IsExplicitProfile(profile))return false;object sandbox=Json.Get(context,"sandbox_policy")??Json.Get(context,"sandboxPolicy")??Json.Get(context,"sandbox");string kind=Json.Str(Json.Get(sandbox,"type")),approval=Json.Str(Json.Get(context,"approval_policy")??Json.Get(context,"approvalPolicy"));
            if(profile=="full-access")return new[]{"danger-full-access","dangerFullAccess"}.Contains(kind)&&approval=="never";
            object networkValue=Json.Get(sandbox,"network_access")??Json.Get(sandbox,"networkAccess");if(networkValue!=null&&!(networkValue is bool))return false;bool network=Object.Equals(networkValue,true);return !network&&approval=="on-request"&&(profile=="read-only"?new[]{"read-only","readOnly"}.Contains(kind):new[]{"workspace-write","workspaceWrite"}.Contains(kind));
        }
        internal static string Profile(object context)
        {foreach(string profile in new[]{"full-access","read-only","workspace-write"})if(MatchesProfile(context,profile))return profile;return "unknown";}
        internal static string Describe(object context)
        {
            string sandbox=Json.Str(Json.Get(Json.Get(context,"sandbox_policy"),"type"));
            string approval=Json.Str(Json.Get(context,"approval_policy"));
            if(sandbox=="danger-full-access" && approval=="never")return "full-access";
            return sandbox.Length==0?"unknown":sandbox+" / "+approval;
        }
        internal static string Read(string home,string threadId)
        {var context=ReadContext(home,threadId,null);return context==null?"unknown":Describe(context);}
        internal static string ReadProfile(string home,string threadId,string turnId)
        {var context=ReadContext(home,threadId,turnId);return context==null?"unknown":Profile(context);}
        private static object ReadContext(string home,string threadId,string turnId)
        {
            if(!Regex.IsMatch(threadId??"","^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-7[a-fA-F0-9]{3}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$"))return null;
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
                if(files.Length!=1)return null;SafeFiles.RejectLinks(files[0]);
                using(var stream=new FileStream(files[0],FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    if(turnId!=null){byte[] first=new byte[(int)Math.Min(stream.Length,1048576)];int got=stream.Read(first,0,first.Length);int newline=Array.IndexOf(first,(byte)10,0,got);if(newline<0)return null;string line=Encoding.UTF8.GetString(first,0,newline).TrimStart('\uFEFF');object metadata;try{metadata=Json.Read<object>(line);}catch(ArgumentException){return null;}if(Json.Str(Json.Get(metadata,"type"))!="session_meta"||Json.Str(Json.Get(Json.Get(metadata,"payload"),"id"))!=threadId)return null;}
                    // Avoid loading large conversation histories or exposing any tool output.
                    long end=stream.Length,minimum=Math.Max(0,end-67108864);
                    while(end>minimum) {
                        long offset=Math.Max(minimum,end-2097152);stream.Position=offset;
                        byte[] bytes=new byte[(int)(end-offset)];int count=0;
                        while(count<bytes.Length){int n=stream.Read(bytes,count,bytes.Length-count);if(n==0)break;count+=n;}
                        using(var reader=new StringReader(Encoding.UTF8.GetString(bytes,0,count))) {
                            if(offset>0)reader.ReadLine();string line;object result=null;
                            while((line=reader.ReadLine())!=null) {
                                if(line.Length>500000 || !line.Contains("\"turn_context\""))continue;
                                try {var item=Json.Read<object>(line);var payload=Json.Get(item,"payload");if(Json.Str(Json.Get(item,"type"))=="turn_context"&&(turnId==null||Json.Str(Json.Get(payload,"turn_id"))==turnId))result=payload;}catch(ArgumentException){}
                            }
                            if(result!=null)return result;
                        }
                        if(offset==minimum)break;
                        end=offset+500000; // overlap for a context split across block boundaries
                    }
                    return null;
                }
            }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}
        }
        internal static void Require(RelayChannel channel,string threadId)
        {
            if(channel.RequireFullAccess && Read(channel.Home,threadId)!="full-access")
                throw new RelayPermissionException("Le canal « "+channel.Id+" » exige Accès complet, mais cette conversation ne l'a pas confirmé. Dans cette conversation Codex, sélectionnez Accès complet, envoyez un message puis reconnectez le canal. Le relais ne change jamais les permissions automatiquement.");
        }
        internal static void RequireFull(RelayChannel channel,string threadId)
        {
            if(Read(channel.Home,threadId)!="full-access")throw new RelayPermissionException("Ce profil exige un accès complet confirmé dans la conversation destinataire.");
        }
    }
}
