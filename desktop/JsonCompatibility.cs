using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creezio.Switcher.Desktop
{
    // Untyped transport objects must remain dictionaries, as in the Framework worker.
    internal static class JsonCompatibility
    {
        internal static readonly JsonSerializerOptions Options=Create();
        private static JsonSerializerOptions Create(){var o=new JsonSerializerOptions{IncludeFields=true,PropertyNameCaseInsensitive=true,MaxDepth=100};o.Converters.Add(new UntypedConverter());return o;}
        private sealed class UntypedConverter:JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader r,Type type,JsonSerializerOptions o)
            {
                switch(r.TokenType){
                    case JsonTokenType.StartObject:
                        var d=new Dictionary<string,object>();while(r.Read()&&r.TokenType!=JsonTokenType.EndObject){string name=r.GetString();r.Read();d[name]=Read(ref r,typeof(object),o);}return d;
                    case JsonTokenType.StartArray:
                        var a=new List<object>();while(r.Read()&&r.TokenType!=JsonTokenType.EndArray)a.Add(Read(ref r,typeof(object),o));return a.ToArray();
                    case JsonTokenType.String:return r.GetString();
                    case JsonTokenType.Number:long n;return r.TryGetInt64(out n)?(object)n:r.GetDouble();
                    case JsonTokenType.True:return true;case JsonTokenType.False:return false;case JsonTokenType.Null:return null;
                    default:throw new JsonException("JSON non reconnu.");
                }
            }
            public override void Write(Utf8JsonWriter writer,object value,JsonSerializerOptions o){if(value==null)writer.WriteNullValue();else if(value.GetType()==typeof(object)){writer.WriteStartObject();writer.WriteEndObject();}else JsonSerializer.Serialize(writer,value,value.GetType(),o);}
        }
    }
}
