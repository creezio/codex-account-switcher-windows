using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    // Public request values are identifiers, never account credentials, filesystem
    // paths or arbitrary tool definitions. Permission changes use a bounded,
    // explicit native profile; omission always preserves the current policy.
    public sealed class PortalMessageOptions
    {
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string CollaborationMode {get;set;}
        public string PermissionProfile {get;set;}
        public List<string> Plugins {get;set;}
        public List<string> Skills {get;set;}
        public List<string> Attachments {get;set;}
        public PortalMessageOptions(){Plugins=new List<string>();Skills=new List<string>();Attachments=new List<string>();}
        internal bool Empty {get{return Model==null&&ReasoningEffort==null&&CollaborationMode==null&&(PermissionProfile==null||PermissionProfile=="inherit")&&Plugins.Count==0&&Skills.Count==0&&Attachments.Count==0;}}
        internal string Canonical()
        {
            // Keep the byte-for-byte historical representation for saved jobs.
            var legacy=new{model=Model,reasoningEffort=ReasoningEffort,collaborationMode=CollaborationMode,plugins=Plugins.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),skills=Skills.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),attachments=Attachments.ToArray()};
            if(PermissionProfile==null||PermissionProfile=="inherit")return Json.Write(legacy);
            var value=Json.Obj(Json.Read<object>(Json.Write(legacy)));value["permissionProfile"]=PermissionProfile;return Json.Write(value);
        }
        internal static PortalMessageOptions FromPayload(IDictionary<string,string> payload)
        {string text;if(!payload.TryGetValue("options",out text))return null;var result=Parse(text);return result.Empty?null:result;}
        internal static PortalMessageOptions Parse(string text)
        {return new Reader(text).Read();}
        private sealed class Reader
        {
            private readonly string text;private int at;
            internal Reader(string text){this.text=text??"";if(this.text.Length>20000)throw Bad();}
            private static WebhookException Bad(){return new WebhookException(400,"Options invalides : utilisez model, reasoningEffort, collaborationMode, permissionProfile, plugins, skills et attachments uniquement.");}
            private void Space(){while(at<text.Length&&" \t\r\n".IndexOf(text[at])>=0)at++;}
            private void Take(char c){Space();if(at>=text.Length||text[at++]!=c)throw Bad();}
            private bool Peek(char c){Space();return at<text.Length&&text[at]==c;}
            private string Value()
            {
                Space();int start=at;Take('"');bool ended=false;
                while(at<text.Length){char c=text[at++];if(c=='"'){ended=true;break;}if(c<32)throw Bad();if(c=='\\'){
                    if(at>=text.Length)throw Bad();char escaped=text[at++];if(escaped=='u'){for(int i=0;i<4;i++)if(at>=text.Length||!Uri.IsHexDigit(text[at++]))throw Bad();}
                    else if("\"\\/bfnrt".IndexOf(escaped)<0)throw Bad();
                }}
                if(!ended||at-start>2048)throw Bad();try{return Json.Read<string>(text.Substring(start,at-start));}catch(Exception){throw Bad();}
            }
            private List<string> Values(string key)
            {
                Take('[');var result=new List<string>();if(Peek(']')){at++;return result;}
                while(true){var value=Value();if(result.Count>=(key=="attachments"?10:20)||result.Contains(value))throw Bad();
                    if(key=="attachments"?!Regex.IsMatch(value,@"\A[a-f0-9]{32}\z"):key=="skills"?!Regex.IsMatch(value,@"\A[a-f0-9]{64}\z"):value.Length==0||value.Length>300||value.Any(Char.IsControl))throw Bad();result.Add(value);
                    Space();if(at>=text.Length)throw Bad();char next=text[at++];if(next==']')return result;if(next!=',')throw Bad();
                }
            }
            internal PortalMessageOptions Read()
            {
                var result=new PortalMessageOptions();var seen=new HashSet<string>(StringComparer.Ordinal);Take('{');
                if(Peek('}'))at++;else while(true){var key=Value();if(!seen.Add(key)||!new[]{"model","reasoningEffort","collaborationMode","permissionProfile","plugins","skills","attachments"}.Contains(key))throw Bad();Take(':');
                    if(new[]{"plugins","skills","attachments"}.Contains(key)){
                        var values=Values(key);if(key=="plugins")result.Plugins=values;else if(key=="skills")result.Skills=values;else result.Attachments=values;
                    }else{
                        string value;if(Peek('n')&&at+4<=text.Length&&text.Substring(at,4)=="null"){at+=4;value=null;}else value=Value();
                        if(key=="model"){if(value!=null&&!Regex.IsMatch(value,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,119}\z"))throw Bad();result.Model=value;}
                        else if(key=="reasoningEffort"){if(value!=null&&!new[]{"none","minimal","low","medium","high","xhigh","max","ultra"}.Contains(value))throw Bad();result.ReasoningEffort=value;}
                        else if(key=="permissionProfile"){if(value!=null&&!new[]{"inherit","read-only","workspace-write","full-access"}.Contains(value))throw Bad();result.PermissionProfile=value=="inherit"?null:value;}
                        else{if(value!=null&&!new[]{"default","plan"}.Contains(value))throw Bad();result.CollaborationMode=value;}
                    }
                    Space();if(at>=text.Length)throw Bad();char next=text[at++];if(next=='}')break;if(next!=',')throw Bad();
                }
                Space();if(at!=text.Length)throw Bad();return result;
            }
        }
    }
}
