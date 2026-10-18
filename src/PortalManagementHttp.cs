using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    // Same signed administration identity for the Site and trusted API clients.
    // Ordinary chat/webhook bearer keys never become machine administrators.
    internal static class PortalManagementHttp
    {
        private static readonly Regex Endpoint=new Regex(@"\A/v1/admin/([a-f0-9]{32})/instances/(?<instance>local|[a-f0-9]{32})/(?:(?<kind>skills)(?:/(?<skill>[a-f0-9]{64}|import|deploy))?|chats/(?<chat>[a-f0-9]{32}|[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/(?:(?<operation>management|goal)|(?<operation>schedules)(?:/(?<schedule>[a-f0-9]{32})/delete)?))\z");
        internal static Match Path(string path){return Endpoint.Match(path??"");}
        internal static bool IsArchiveImport(string path){var match=Path(path);return match.Success&&match.Groups["kind"].Value=="skills"&&match.Groups["skill"].Value=="import";}
        internal static int MaximumBody(string path){var match=Path(path);if(!match.Success)return PortalAdministration.MaximumBody;return match.Groups["kind"].Value=="skills"?(match.Groups["skill"].Value=="import"?3*1024*1024:128*1024):(match.Groups["operation"].Value=="goal"?32*1024:128*1024);}
        private static WebhookHttpResponse Result(int status,object value){return new WebhookHttpResponse{Status=status,Body=Json.Write(value)};}
        internal static WebhookHttpResponse Handle(RelayStore store,WebhookHttpRequest request,PortalAdministrationIdentity identity,PortalSkillService fixtureSkills=null,PortalConversationManagement fixtureManagement=null)
        {
            var path=Path(request.Path);if(!path.Success)throw new WebhookException(404,"Adresse de gestion introuvable.");
            string instance=path.Groups["instance"].Value,key;request.Headers.TryGetValue("Idempotency-Key",out key);
            if(path.Groups["kind"].Value=="skills"){
                var service=fixtureSkills??new PortalSkillService(store);string operation=path.Groups["skill"].Value;
                if(request.Method=="GET"&&operation.Length==0)return Result(200,service.Catalog(identity.Route,identity.Actor,instance));
                if(request.Method=="GET"&&Regex.IsMatch(operation,@"\A[a-f0-9]{64}\z"))return Result(200,service.Read(identity.Route,identity.Actor,instance,operation));
                if(request.Method!="POST")throw new WebhookException(405,"Methode non autorisee.");
                if(operation.Length==0)return Result(200,service.Save(identity.Route,identity.Actor,instance,key,Fields.Read<PortalSkillSaveRequest>(request.Body,MaximumBody(request.Path),"name","document","expectedRevision","sourceSkillId")));
                if(operation=="import")return Result(200,service.Import(identity.Route,identity.Actor,instance,key,Fields.Read<PortalSkillImportRequest>(request.Body,MaximumBody(request.Path),"archiveBase64","name","expectedRevision")));
                if(operation=="deploy")return Result(200,service.Deploy(identity.Route,identity.Actor,instance,key,Fields.Read<PortalSkillDeployRequest>(request.Body,MaximumBody(request.Path),"sourceInstanceId","skillId","sourceRevision","name","expectedRevision")));
                throw new WebhookException(405,"Methode non autorisee.");
            }
            var management=fixtureManagement??new PortalConversationManagement(store);
            string chat=path.Groups["chat"].Value,action=path.Groups["operation"].Value,schedule=path.Groups["schedule"].Value;
            if(request.Method=="GET"&&action=="management")return Result(200,management.Read(identity.Route,identity.Actor,instance,chat));
            if(request.Method!="POST")throw new WebhookException(405,"Methode non autorisee.");
            if(action=="goal")return Result(200,management.Goal(identity.Route,identity.Actor,instance,chat,key,Fields.Parse(request.Body,MaximumBody(request.Path),"action","expectedVersion","objective","status","tokenBudget")));
            if(action=="schedules"&&schedule.Length==0)return Result(200,management.SaveSchedule(identity.Route,identity.Actor,instance,chat,key,Fields.Parse(request.Body,MaximumBody(request.Path),"id","revision","name","prompt","status","frequency","intervalMinutes","localTime","daysOfWeek","timeZoneId")));
            if(action=="schedules"&&schedule.Length>0)return Result(200,management.DeleteSchedule(identity.Route,identity.Actor,instance,chat,schedule,key,Fields.Parse(request.Body,MaximumBody(request.Path),"revision")));
            throw new WebhookException(405,"Methode non autorisee.");
        }
        // Deliberately bounded flat JSON. Duplicate decoded keys are rejected on
        // both Framework and .NET before DTO deserialization can hide them.
        internal static class Fields
        {
            internal static WebhookException Bad(){return new WebhookException(400,"Corps JSON de gestion invalide : champs attendus sans doublons, entiers et listes de texte uniquement.");}
            internal static Dictionary<string,object> Parse(string text,int maximum,params string[] allowed){return new Reader(text,maximum,allowed).Read();}
            internal static T Read<T>(string text,int maximum,params string[] allowed){var fields=Parse(text,maximum,allowed);foreach(var field in fields){var property=typeof(T).GetProperties().SingleOrDefault(p=>p.Name.Equals(field.Key,StringComparison.OrdinalIgnoreCase));if(property==null)throw Bad();Type declared=property.PropertyType,type=Nullable.GetUnderlyingType(declared)??declared;object value=field.Value;if(value==null){if(declared.IsValueType&&Nullable.GetUnderlyingType(declared)==null)throw Bad();continue;}if(type==typeof(string)&&!(value is string)||type==typeof(bool)&&!(value is bool)||(type==typeof(long)||type==typeof(int))&&!(value is long)||typeof(IEnumerable<string>).IsAssignableFrom(type)&&!(value is string[]))throw Bad();}try{return Json.Read<T>(Json.Write(fields));}catch(WebhookException){throw;}catch(Exception){throw Bad();}}
            private sealed class Reader
            {
                private readonly string text;private readonly HashSet<string> allowed;private int at;
                internal Reader(string input,int maximum,string[] names){text=input??"";allowed=new HashSet<string>(names,StringComparer.Ordinal);if(text.Length>maximum)throw Bad();}
                private void Space(){while(at<text.Length&&" \r\n\t".IndexOf(text[at])>=0)at++;}
                private bool Peek(char value){Space();return at<text.Length&&text[at]==value;}
                private void Take(char value){Space();if(at>=text.Length||text[at++]!=value)throw Bad();}
                private string String(){Space();int start=at;Take('"');bool ended=false;while(at<text.Length){char value=text[at++];if(value=='"'){ended=true;break;}if(value<32)throw Bad();if(value=='\\'){if(at>=text.Length)throw Bad();char escape=text[at++];if(escape=='u'){for(int i=0;i<4;i++)if(at>=text.Length||!Uri.IsHexDigit(text[at++]))throw Bad();}else if("\"\\/bfnrt".IndexOf(escape)<0)throw Bad();}}if(!ended)throw Bad();try{return Json.Read<string>(text.Substring(start,at-start));}catch(Exception){throw Bad();}}
                private object Value(){
                    Space();if(Peek('"'))return String();
                    if(Peek('[')){at++;var values=new List<string>();if(Peek(']')){at++;return values.ToArray();}while(true){if(values.Count>=32||!Peek('"'))throw Bad();values.Add(String());if(Peek(']')){at++;return values.ToArray();}Take(',');}}
                    foreach(string literal in new[]{"true","false","null"})if(at+literal.Length<=text.Length&&text.Substring(at,literal.Length)==literal){at+=literal.Length;return literal=="null"?null:(object)(literal=="true");}
                    int start=at;if(at<text.Length&&text[at]=='-')at++;if(at>=text.Length||text[at]<'0'||text[at]>'9')throw Bad();if(text[at]=='0')at++;else while(at<text.Length&&text[at]>='0'&&text[at]<='9')at++;
                    long integer;if(!Int64.TryParse(text.Substring(start,at-start),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out integer))throw Bad();return integer;
                }
                internal Dictionary<string,object> Read(){var result=new Dictionary<string,object>(StringComparer.Ordinal);Take('{');if(Peek('}'))at++;else while(true){string key=String();if(!allowed.Contains(key)||result.ContainsKey(key))throw Bad();Take(':');result.Add(key,Value());Space();if(at>=text.Length)throw Bad();char next=text[at++];if(next=='}')break;if(next!=',')throw Bad();}Space();if(at!=text.Length)throw Bad();return result;}
            }
        }
    }
}
