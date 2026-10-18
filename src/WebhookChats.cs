using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    // This is a view of chats created by this webhook route, not a browser for
    // arbitrary account history. It never resumes a thread or answers approvals.
    internal static class WebhookChats
    {
        private const int MaximumLine=131072,ReadBudget=524288,InitialTail=1048576,MaximumText=100000;
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        private static readonly object Gate=new object();
        private static readonly Dictionary<string,Transcript> Cache=new Dictionary<string,Transcript>(StringComparer.OrdinalIgnoreCase);
        private sealed class PromptValue {internal string Text;internal bool Clipped;}
        private static readonly Dictionary<string,PromptValue> Prompts=new Dictionary<string,PromptValue>(StringComparer.OrdinalIgnoreCase);
        private sealed class Entry
        {
            internal string Id,Role,Text,At,Phase,Type,Name,Status;
        }
        private sealed class Transcript
        {
            internal readonly object Gate=new object();
            internal string Path,ThreadId,Permission="unknown",Updated,Turn;
            internal DateTime Accessed,Located;
            internal long Offset,Stamp,Version;
            internal bool Verified,Discard,Busy,StateKnown,Backlog,Truncated;
            internal readonly List<byte> Partial=new List<byte>();
            internal readonly List<Entry> Messages=new List<Entry>(),Activities=new List<Entry>();
        }
        internal static string Safe(string text,int maximum=4000)
        {
            text=HeadlessApprovals.SafeSummary(text??"");
            text=Regex.Replace(text,@"\b(?:whk_|cfat_)[A-Za-z0-9_-]{12,}\b","[jeton masque]");
            text=Regex.Replace(text,@"(?i)(https?://[^\s?]+\?)[^\s]+","$1[parametres masques]");
            text=Regex.Replace(text,@"(?i)\b(password|secret|token|api[_-]?key|authorization|access[_-]?token|refresh[_-]?token)(\s*[\""']?\s*:\s*[\""']?)[^\s\""',;}]+","$1$2[masque]");
            return text.Length<=maximum?text:text.Substring(0,maximum)+"…";
        }
        private static bool Same(string a,string b)
        {return !String.IsNullOrWhiteSpace(a)&&!String.IsNullOrWhiteSpace(b)&&RelayStore.SamePath(a,b);}
        internal static WebhookJob RequireRoot(RelayStore store,WebhookRoute route,string id,bool ready)
        {
            if(!Regex.IsMatch(id??"",@"\A[a-f0-9]{32}\z"))throw new WebhookException(404,"Conversation introuvable.");
            var root=Webhooks.Job(store,id);var frozen=root.RouteSnapshot;
            if(!String.IsNullOrEmpty(root.PortalInstanceId)||root.RouteId!=route.Id||!String.IsNullOrEmpty(root.ConversationId)||frozen==null||!String.IsNullOrEmpty(frozen.ThreadId)||
                frozen.AccountKey!=route.AccountKey||frozen.InstanceId!=route.InstanceId||!Same(frozen.Home,route.Home)||!Same(frozen.Workspace,route.Workspace))throw new WebhookException(404,"Conversation introuvable.");
            if(!String.IsNullOrEmpty(root.ThreadId)){
                Guid thread;
                if(!Guid.TryParse(root.ThreadId,out thread)||!store.Exists(root.Id))throw new WebhookException(409,"Le recu de creation du chat n'est pas encore confirme.");
                var native=store.Message(root.Id);
                if(!native.OperatorOrigin||!native.NewConversation||native.WebhookJobId!=root.Id||native.TargetThreadId!=root.ThreadId||native.TargetAccountKey!=frozen.AccountKey||!Same(native.TargetHome,frozen.Home)||!Same(native.TargetWorkspace??native.Workspace,frozen.Workspace))throw new WebhookException(409,"Le recu du chat ne correspond pas a cette route. Aucun acces transmis.");
            }
            if(ready){
                if(String.IsNullOrEmpty(root.ThreadId))throw new WebhookException(409,"La creation du chat n'est pas encore terminee.");
                var transcript=Read(frozen.Home,root.ThreadId);
                lock(transcript.Gate)if(!transcript.Verified||!transcript.StateKnown||transcript.Busy||transcript.Backlog||transcript.Partial.Count>0)throw new WebhookException(409,"Codex travaille encore ou son etat n'est pas confirme. Attendez avant d'envoyer un nouveau message.");
            }
            return root;
        }
        internal static object List(RelayStore store,string slug,string bearer)
        {
            var route=Webhooks.Authorize(store,slug,bearer);var jobs=Webhooks.Jobs(store).Where(j=>j.RouteId==route.Id).ToArray();
            var chats=new List<object>();
            foreach(var row in jobs.Where(j=>String.IsNullOrEmpty(j.ConversationId)).Take(100)){
                try{
                    var root=RequireRoot(store,route,row.Id,false);var latest=jobs.Where(j=>j.Id==root.Id||j.ConversationId==root.Id).OrderByDescending(j=>j.CreatedUtc,StringComparer.Ordinal).First();
                    chats.Add(new{id=root.Id,threadId=root.ThreadId,title=Safe(root.Title,120),state=latest.State,updatedUtc=latest.UpdatedUtc,error=Safe(latest.Error,1000)});
                }catch(WebhookException){/* Legacy routes targeting an existing chat are deliberately excluded. */}
            }
            return new{chats=chats.ToArray()};
        }
        internal static object Snapshot(RelayStore store,string slug,string bearer,string rootJobId)
        {
            var route=Webhooks.Authorize(store,slug,bearer);var root=RequireRoot(store,route,rootJobId,false);
            var jobs=Webhooks.Jobs(store).Where(j=>j.RouteId==route.Id&&(j.Id==root.Id||j.ConversationId==root.Id)).OrderBy(j=>j.CreatedUtc,StringComparer.Ordinal).ToArray();
            var latest=jobs.Last();var transcript=String.IsNullOrEmpty(root.ThreadId)?new Transcript():Read(root.RouteSnapshot.Home,root.ThreadId);
            lock(transcript.Gate){
                var visible=transcript.Messages.Select(e=>new Entry{Id=e.Id,Role=e.Role,Text=e.Text,At=e.At,Phase=e.Phase}).ToList();
                // Native direct messages are injected as integration outputs on
                // recent Codex builds. Their durable webhook inputs remain the
                // authoritative user text, with delivery state shown explicitly.
                bool clipped=transcript.Truncated;
                foreach(var job in jobs.Reverse().Take(50).Reverse()){var prompt=Prompt(store,job.Id,root);clipped|=prompt.Clipped;visible.Add(new Entry{Id="job-"+job.Id,Role="user",Text=prompt.Text,At=job.CreatedUtc,Phase=job.State=="queued"||job.State=="waiting"?"queued":job.State=="uncertain"?"uncertain":"submitted"});}
                visible=visible.OrderBy(e=>e.At,StringComparer.Ordinal).ToList();while(visible.Count>120){visible.RemoveAt(0);clipped=true;}
                int messageText=visible.Sum(e=>e.Text.Length);while(messageText>60000&&visible.Count>1){messageText-=visible[0].Text.Length;visible.RemoveAt(0);clipped=true;}
                var messages=visible.Select(e=>new{id=e.Id,role=e.Role,text=e.Text,createdUtc=e.At,phase=e.Phase}).ToArray();
                var activityRows=transcript.Activities.ToList();int activityText=activityRows.Sum(e=>e.Text.Length);while(activityText>40000&&activityRows.Count>1){activityText-=activityRows[0].Text.Length;activityRows.RemoveAt(0);clipped=true;}
                var activities=activityRows.Select(e=>new{id=e.Id,type=e.Type,name=e.Name,status=e.Status,text=e.Text,createdUtc=e.At}).ToList();
                if(latest.State=="attention")activities.Add(new{id="attention-"+latest.Id,type="intervention",name="Intervention dans Codex",status="waiting",text=Safe(latest.Error??"Une intervention native est requise. Aucune autorisation n'est accordee depuis ce site."),createdUtc=latest.UpdatedUtc});
                string state=transcript.Busy&&!Webhooks.IsPending(latest)?"running":!String.IsNullOrEmpty(root.ThreadId)&&!transcript.StateKnown&&!Webhooks.IsPending(latest)?"checking":latest.State;
                string cursor=Webhooks.Hash(root.Id+"\n"+latest.UpdatedUtc+"\n"+state+"\n"+transcript.Version.ToString(CultureInfo.InvariantCulture)).Substring(0,24);
                return new{id=root.Id,threadId=root.ThreadId,title=Safe(root.Title,120),state=state,updatedUtc=Latest(transcript.Updated,latest.UpdatedUtc),error=Safe(latest.Error,1000),permission=transcript.Permission,messages=messages,activities=activities.ToArray(),jobs=jobs.Reverse().Take(50).Reverse().Select(j=>new{id=j.Id,state=j.State,error=Safe(j.Error,500),createdUtc=j.CreatedUtc,updatedUtc=j.UpdatedUtc}).ToArray(),cursor=cursor,truncated=clipped,catchingUp=transcript.Backlog||!String.IsNullOrEmpty(root.ThreadId)&&!transcript.StateKnown};
            }
        }
        private static string Latest(string first,string second)
        {DateTimeOffset a,b;bool hasA=DateTimeOffset.TryParse(first,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out a),hasB=DateTimeOffset.TryParse(second,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out b);return hasA&&(!hasB||a>=b)?first:second;}
        private static PromptValue Prompt(RelayStore store,string id,WebhookJob root)
        {
            string key=store.Root+"\n"+id;PromptValue value;lock(Gate)if(Prompts.TryGetValue(key,out value))return value;
            string raw=(id==root.Id?root:Webhooks.Job(store,id)).Prompt??"";value=new PromptValue{Text=Safe(raw,4000),Clipped=raw.Length>4000};
            lock(Gate){if(Prompts.Count>=64)Prompts.Remove(Prompts.Keys.First());Prompts[key]=value;}return value;
        }
        internal static WebhookJob Send(RelayStore store,string slug,string bearer,string rootJobId,string idempotency,string jsonBody)
        {return Webhooks.SubmitConversation(store,slug,bearer,rootJobId,idempotency,jsonBody);}
        // Read-only portal projection uses the same bounded, public transcript
        // parser. No native session is resumed and hidden analysis stays omitted.
        internal static object NativeSnapshot(string home,string threadId,string id,string title)
        {
            var t=Read(home,threadId);lock(t.Gate){
                return new{id=id,threadId=threadId,title=Safe(title,120),state=t.Busy?"running":t.Verified&&t.StateKnown?"idle":"checking",updatedUtc=t.Updated,error="",permission=t.Permission,
                    messages=t.Messages.Select(e=>new{id=e.Id,role=e.Role,text=e.Text,createdUtc=e.At,phase=e.Phase}).ToArray(),
                    activities=t.Activities.Select(e=>new{id=e.Id,type=e.Type,name=e.Name,status=e.Status,text=e.Text,createdUtc=e.At}).ToArray(),
                    jobs=new object[0],cursor=Webhooks.Hash(id+"\n"+t.Version.ToString(CultureInfo.InvariantCulture)).Substring(0,24),truncated=t.Truncated,catchingUp=t.Backlog||!t.Verified||!t.StateKnown};
            }
        }
        internal static void RequireNativeReady(string home,string threadId)
        {var t=Read(home,threadId);lock(t.Gate)if(!t.Verified||!t.StateKnown||t.Busy||t.Backlog||t.Partial.Count>0)throw new WebhookException(409,"Cette discussion travaille encore ou son etat natif n'est pas confirme. Aucun message ajoute.");}
        internal static string NativeWorkspace(string home,string threadId)
        {
            string path=Locate(home,threadId);if(path==null)throw new WebhookException(409,"Le journal local de cette discussion n'est pas confirme.");SafeFiles.RejectLinks(path);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                string first=FirstLine(stream);object row=first==null?null:Json.Read<object>(first);var meta=Json.Get(row,"payload");
                if(Json.Str(Json.Get(row,"type"))!="session_meta"||Json.Str(Json.Get(meta,"id"))!=threadId)throw new WebhookException(409,"Le journal ne correspond pas a la discussion native.");
                return RelayStore.WorkspacePath(RolloutSnapshot.NativePath(Json.Str(Json.Get(meta,"cwd"))));
            }
        }
        private static Transcript Read(string home,string threadId)
        {
            string key=Path.GetFullPath(home)+"\n"+threadId;Transcript result;
            lock(Gate){
                if(!Cache.TryGetValue(key,out result)){
                    if(Cache.Count>=16)Cache.Remove(Cache.OrderBy(p=>p.Value.Accessed).First().Key);
                    result=new Transcript{ThreadId=threadId};Cache[key]=result;
                }
                result.Accessed=DateTime.UtcNow;
            }
            lock(result.Gate){
                try{
                    if(result.Path==null&&(DateTime.UtcNow-result.Located).TotalSeconds>=2){result.Located=DateTime.UtcNow;result.Path=Locate(home,threadId);}
                    if(result.Path==null)return result;
                    SafeFiles.RejectLinks(result.Path);
                    using(var stream=new FileStream(result.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                        long stamp=File.GetLastWriteTimeUtc(result.Path).Ticks;
                        if(stream.Length<result.Offset||(stream.Length==result.Offset&&result.Stamp!=0&&result.Stamp!=stamp))Reset(result);
                        if(!result.Verified){
                            string first=FirstLine(stream);object entry=first==null?null:Json.Read<object>(first);
                            if(Json.Str(Json.Get(entry,"type"))!="session_meta"||Json.Str(Json.Get(Json.Get(entry,"payload"),"id"))!=threadId)return result;
                            result.Verified=true;
                            if(stream.Length>InitialTail){result.Offset=stream.Length-InitialTail;result.Discard=true;result.Truncated=true;}
                        }
                        stream.Position=result.Offset;var bytes=new byte[65536];int remaining=ReadBudget,count;
                        while(remaining>0&&(count=stream.Read(bytes,0,Math.Min(bytes.Length,remaining)))>0){
                            remaining-=count;result.Offset+=count;
                            for(int at=0;at<count;at++){
                                byte b=bytes[at];if(b==10){
                                    if(!result.Discard&&result.Partial.Count>0){try{Parse(result,Utf8.GetString(result.Partial.ToArray()));}catch(Exception e){if(!Malformed(e))throw;}}
                                    result.Partial.Clear();result.Discard=false;
                                }else if(!result.Discard){if(result.Partial.Count>=MaximumLine){result.Partial.Clear();result.Discard=true;result.Truncated=true;}else result.Partial.Add(b);}
                            }
                        }
                        result.Backlog=result.Offset<stream.Length;result.Stamp=stamp;Trim(result);
                    }
                }catch(IOException){result.Backlog=true;}catch(UnauthorizedAccessException){result.Backlog=true;}catch(Exception e){if(!Malformed(e))throw;result.Backlog=true;}
                return result;
            }
        }
        private static bool Malformed(Exception error)
        {
            if(error is ArgumentException||error is InvalidOperationException||error is FormatException)return true;
            // Framework uses JavaScriptSerializer while Desktop uses
            // System.Text.Json; keep its typed parser errors portable without
            // masking unrelated runtime or I/O failures.
            for(Type type=error.GetType();type!=null;type=type.BaseType)if(type.FullName=="System.Text.Json.JsonException")return true;
            return false;
        }
        private static void Reset(Transcript t){t.Offset=0;t.Verified=false;t.Busy=false;t.StateKnown=false;t.Backlog=false;t.Discard=false;t.Partial.Clear();t.Messages.Clear();t.Activities.Clear();t.Version++;}
        private static string Locate(string home,string threadId)
        {
            if(!Regex.IsMatch(threadId??"",@"\A[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-7[a-fA-F0-9]{3}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\z"))return null;
            long ms=Convert.ToInt64(threadId.Replace("-","").Substring(0,12),16);var day=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ms);var paths=new List<string>();
            foreach(int shift in new[]{0,-1,1}){var date=day.AddDays(shift);string folder=Path.Combine(home,"sessions",date.ToString("yyyy",CultureInfo.InvariantCulture),date.ToString("MM",CultureInfo.InvariantCulture),date.ToString("dd",CultureInfo.InvariantCulture));SafeFiles.RejectLinks(folder);if(Directory.Exists(folder))paths.AddRange(Directory.GetFiles(folder,"rollout-*"+threadId+".jsonl",SearchOption.TopDirectoryOnly));}
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()==1?paths[0]:null;
        }
        private static string FirstLine(Stream stream)
        {stream.Position=0;var bytes=new List<byte>();for(int i=0;i<MaximumLine;i++){int b=stream.ReadByte();if(b<0)return null;if(b==10)return Utf8.GetString(bytes.ToArray());bytes.Add((byte)b);}return null;}
        private static string Content(object value)
        {
            if(value is string)return (string)value;
            return String.Join("\n",RelayEngine.Rows(value).Where(c=>new[]{"input_text","output_text","text","summary_text"}.Contains(Json.Str(Json.Get(c,"type")).ToLowerInvariant())).Select(c=>Json.Str(Json.Get(c,"text"))));
        }
        private static string Id(object payload,string fallback){string value=Json.Str(Json.Get(payload,"id"));return value.Length==0?fallback:Safe(value,160);}
        private static void Message(Transcript t,string id,string role,string text,string at,string phase)
        {
            if(String.IsNullOrWhiteSpace(text)||role!="user"&&role!="assistant"||role=="assistant"&&phase=="analysis")return;if(text.Length>6000)t.Truncated=true;text=Safe(text,6000);
            if(role=="user"&&(text.StartsWith("# AGENTS.md instructions",StringComparison.Ordinal)||text.Contains("<environment_context>")||text.Contains("Demande de l'application externe (route configuree")||text.Contains("CREEZIO_PREFLIGHT")))return;
            if(role=="assistant"&&text.Trim()=="Chat préparé.")return;
            if(role=="user")phase="message";
            var existing=t.Messages.LastOrDefault(m=>m.Id==id);
            var last=t.Messages.LastOrDefault();if(existing==null&&last!=null&&last.Role==role&&last.Text==text&&last.Phase==phase)existing=last;
            if(existing!=null)return;
            t.Messages.Add(new Entry{Id=id,Role=role,Text=text,At=at,Phase=phase});t.Version++;
        }
        private static void Activity(Transcript t,string id,string type,string name,string status,string text,string at)
        {
            var entry=t.Activities.LastOrDefault(e=>e.Id==id);if(entry==null){entry=new Entry{Id=id};t.Activities.Add(entry);}
            if((text??"").Length>2000)t.Truncated=true;entry.Type=type;entry.Name=Safe(name,160);entry.Status=status;entry.Text=Safe(text,2000);entry.At=at;t.Version++;
        }
        private static void Parse(Transcript t,string line)
        {
            var row=Json.Read<object>(line);string type=Json.Str(Json.Get(row,"type")),at=Json.Str(Json.Get(row,"timestamp"));var p=Json.Get(row,"payload");string kind=Json.Str(Json.Get(p,"type"));
            string fallback="line-"+t.Offset.ToString(CultureInfo.InvariantCulture)+"-"+Webhooks.Hash(line).Substring(0,12);
            if(type=="turn_context"){t.Permission=RelayPermissions.Describe(p);t.Version++;return;}
            if(type=="event_msg"){
                if(kind=="task_started"){t.Busy=true;t.StateKnown=true;t.Turn=Json.Str(Json.Get(p,"turn_id"));t.Version++;}
                else if(kind=="task_complete"||kind=="turn_aborted"){string ended=Json.Str(Json.Get(p,"turn_id"));if(String.IsNullOrEmpty(t.Turn)||String.IsNullOrEmpty(ended)||ended==t.Turn){t.Busy=false;t.StateKnown=true;t.Version++;}}
                else if(kind=="user_message")Message(t,fallback,"user",Json.Str(Json.Get(p,"message")),at,"message");
                else if(kind=="agent_message"&&Json.Str(Json.Get(p,"channel"))!="analysis")Message(t,fallback,"assistant",Json.Str(Json.Get(p,"message")),at,Json.Str(Json.Get(p,"phase")));
                // agent_reasoning/raw_content/analysis are private. Only explicit
                // public summary fields below are eligible for this view.
                else if(kind=="item_completed"||kind=="item_started")ParseItem(t,Json.Get(p,"item"),at,kind=="item_completed",fallback);
                return;
            }
            if(type=="response_item")ParseItem(t,p,at,true,fallback);
        }
        private static void ParseItem(Transcript t,object p,string at,bool complete,string fallback)
        {
            string kind=Json.Str(Json.Get(p,"type")),normalized=kind.Replace("_","").ToLowerInvariant(),id=Id(p,fallback);
            if(normalized=="message"||normalized=="agentmessage"){
                string role=normalized=="agentmessage"?"assistant":Json.Str(Json.Get(p,"role"));string channel=Json.Str(Json.Get(p,"channel")),phase=Json.Str(Json.Get(p,"phase"));
                if(role=="assistant"&&(channel=="analysis"||phase=="analysis"))return;
                string text=Content(Json.Get(p,"content"));if(text.Length==0&&normalized=="agentmessage")text=Json.Str(Json.Get(p,"text"));Message(t,id,role,text,at,phase);return;
            }
            // A native Plan item is an explicit public result, distinct from
            // private reasoning and its encrypted/internal payload.
            if(normalized=="plan"){Message(t,id,"assistant",Json.Str(Json.Get(p,"text")),at,"final_answer");return;}
            if(normalized=="reasoning"){
                string summary=Content(Json.Get(p,"summary"));if(summary.Length==0)summary=Json.Str(Json.Get(p,"summary_text"));
                if(summary.Length>0)Activity(t,id,"summary","Resume de raisonnement",complete?"completed":"running",summary,at);
                return;
            }
            if(normalized=="functioncall"||normalized=="customtoolcall"||normalized=="commandexecution"||normalized=="mcptoolcall"||normalized=="websearch"||normalized=="filechange"){
                string call=Json.Str(Json.Get(p,"call_id"));if(call.Length>0)id="tool-"+Safe(call,160);
                string name=Json.Str(Json.Get(p,"name"));if(name.Length==0)name=Json.Str(Json.Get(p,"tool"));if(name.Length==0)name=kind;
                string preview=Json.Str(Json.Get(p,"command"));if(preview.Length==0)preview=Json.Str(Json.Get(p,"query"));
                // Arguments and full output objects can contain credentials or
                // internal directives. Deliberately show only a bounded preview.
                string status=Json.Str(Json.Get(p,"status"));if(status.Length==0)status=normalized=="functioncall"||normalized=="customtoolcall"?"running":complete?"completed":"running";
                Activity(t,id,"tool",name,Safe(status,40),preview,at);return;
            }
            if(normalized=="functioncalloutput"||normalized=="customtoolcalloutput"){
                string call=Json.Str(Json.Get(p,"call_id"));if(call.Length>0)id="tool-"+Safe(call,160);
                var old=t.Activities.LastOrDefault(e=>e.Id==id);string name=old==null?Json.Str(Json.Get(p,"name")):old.Name;
                string output=Content(Json.Get(p,"output"));if(output.Length==0)output=Content(Json.Get(p,"aggregated_output"));
                // Codex's synthetic create_thread tool output contains internal
                // delegation metadata, not an actual user tool invocation.
                if(name=="create_thread"||output.Contains("<codex_delegation>"))return;
                Activity(t,id,"tool",name.Length==0?"Resultat d'outil":name,"completed",output,at);
            }
        }
        private static void Trim(Transcript t)
        {
            while(t.Messages.Count>120){t.Messages.RemoveAt(0);t.Truncated=true;}
            while(t.Activities.Count>180){t.Activities.RemoveAt(0);t.Truncated=true;}
            int length=t.Messages.Sum(m=>(m.Text??"").Length)+t.Activities.Sum(m=>(m.Text??"").Length);
            while(length>MaximumText&&t.Messages.Count+t.Activities.Count>1){
                bool message=t.Messages.Count>0&&(t.Activities.Count==0||String.CompareOrdinal(t.Messages[0].At,t.Activities[0].At)<=0);
                if(message){length-=t.Messages[0].Text.Length;t.Messages.RemoveAt(0);}else{length-=t.Activities[0].Text.Length;t.Activities.RemoveAt(0);}t.Truncated=true;
            }
            t.Updated=t.Messages.Select(e=>e.At).Concat(t.Activities.Select(e=>e.At)).Where(a=>!String.IsNullOrEmpty(a)).OrderByDescending(a=>a,StringComparer.Ordinal).FirstOrDefault();
        }
    }
}
