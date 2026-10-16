using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Creezio.Switcher
{
    // Current Codex drops obsolete executed-tool annotations and adds empty
    // retained-context defaults. Adapt only the private import snapshot, and
    // only when every removed annotation exactly duplicates a real tool call.
    internal sealed class LegacyHistoryCompatibility
    {
        private readonly Dictionary<string,HashSet<string>> calls=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        internal int DuplicateAnnotations,EmptyDefaults;
        internal LegacyHistoryCompatibility(string path,CancellationToken token)
        {
            using(var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))){
                string line;while((line=reader.ReadLine())!=null){token.ThrowIfCancellationRequested();if(line.IndexOf("\"function_call\"",StringComparison.Ordinal)<0)continue;
                    object entry;try{entry=RolloutSnapshot.ReadRollout(line);}catch{throw new InvalidOperationException("Le journal est incomplet ou son format n’est pas reconnu. Aucun import n’est créé.");}var item=Json.Get(entry,"payload");
                    if(Json.Str(Json.Get(entry,"type"))!="response_item"||Json.Str(Json.Get(item,"type"))!="function_call")continue;
                    object arguments;try{arguments=RolloutSnapshot.ReadRollout(Json.Str(Json.Get(item,"arguments")));}catch{continue;}
                    string name=Json.Str(Json.Get(item,"name")),space=Json.Str(Json.Get(item,"namespace")),id=Json.Str(Json.Get(item,"call_id"));if(id.Length==0)continue;HashSet<string> signatures;if(!calls.TryGetValue(id,out signatures))calls[id]=signatures=new HashSet<string>(StringComparer.Ordinal);signatures.Add(Signature(name,arguments));if(space.Length>0)signatures.Add(Signature(space+"__"+name,arguments));
                }
            }
        }
        private static string Signature(string name,object args){return name+"\n"+Value(args);}
        private static string Value(object value)
        {
            var obj=value as IDictionary<string,object>;if(obj!=null)return "{"+String.Join(",",obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>Json.Write(p.Key)+":"+Value(p.Value)))+"}";
            var list=value as System.Collections.IEnumerable;if(list!=null&&!(value is string))return "["+String.Join(",",list.Cast<object>().Select(Value))+"]";
            return Json.Write(value);
        }
        internal void Apply(object entry)
        {
            string type=Json.Str(Json.Get(entry,"type"));var payload=Json.Get(entry,"payload");
            if(type=="response_item")Message(payload);
            if(type=="compacted"){
                var retained=Json.Get(payload,"retained_context");if(retained is Dictionary<string,object>){var obj=Json.Obj(retained);if(!obj.ContainsKey("assistant_messages")){obj["assistant_messages"]=new object[0];EmptyDefaults++;}if(!obj.ContainsKey("assistant_messages_incomplete")){obj["assistant_messages_incomplete"]=false;EmptyDefaults++;}}
                foreach(string field in new[]{"guardian_history","replacement_history"})foreach(var item in RelayEngine.Rows(Json.Get(payload,field)))Message(item);
            }
        }
        private void Message(object item)
        {
            if(Json.Str(Json.Get(item,"type"))!="function_call_output")return;
            var metadata=Json.Get(item,"internal_chat_message_metadata_passthrough") as Dictionary<string,object>;if(metadata==null||(!metadata.ContainsKey("executed_tool_calls")&&!metadata.ContainsKey("tool_calls_complete")))return;
            var annotations=metadata.ContainsKey("executed_tool_calls")?metadata["executed_tool_calls"] as System.Collections.IEnumerable:null;
            if(annotations==null||annotations is string||!Equals(Json.Get(metadata,"tool_calls_complete"),true))throw new InvalidOperationException("Anciennes annotations d’outils non vérifiables ; l’original reste conservé.");
            foreach(var call in annotations){var obj=call as Dictionary<string,object>;HashSet<string> signatures;if(obj==null||obj.Count!=2||!obj.ContainsKey("name")||!obj.ContainsKey("arguments")||!calls.TryGetValue(Json.Str(Json.Get(item,"call_id")),out signatures)||!signatures.Contains(Signature(Json.Str(Json.Get(obj,"name")),Json.Get(obj,"arguments"))))throw new InvalidOperationException("Une ancienne annotation d’outil n’a pas d’appel identique dans l’historique ; aucun import incomplet n’est créé.");DuplicateAnnotations++;}
            metadata.Remove("executed_tool_calls");metadata.Remove("tool_calls_complete");
        }
    }
}
