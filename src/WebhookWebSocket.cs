using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Creezio.Switcher
{
    internal sealed class WebhookWebSocketException:Exception
    {
        internal readonly ushort CloseCode;
        internal WebhookWebSocketException(ushort code,string message):base(message){CloseCode=code;}
    }
    internal sealed class WebhookWebSocketFrame
    {
        internal byte Opcode;
        internal byte[] Payload;
    }
    // This transport only observes an authenticated, webhook-created chat.
    // Mutations still pass through the bounded, idempotent HTTP admission path.
    internal static class WebhookWebSocket
    {
        internal const int MaximumFrameBytes=65536,MaximumMessageBytes=1048576;
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static bool IsEventsPath(string path,out string slug,out string chatId)
        {
            string instanceId;if(IsPortalEventsPath(path,out slug,out instanceId,out chatId))return true;
            var match=Regex.Match(path??"",@"\A/v1/hooks/([a-z0-9][a-z0-9-]{1,62})/chats/([a-f0-9]{32})/events\z");
            slug=match.Groups[1].Value;chatId=match.Groups[2].Value;return match.Success;
        }
        internal static bool IsPortalEventsPath(string path,out string slug,out string instanceId,out string chatId)
        {
            var match=Regex.Match(path??"",@"\A/v1/hooks/([a-z0-9][a-z0-9-]{1,62})/instances/(local|[a-f0-9]{32})/chats/([a-f0-9]{32}|[a-f0-9]{8}-[a-f0-9]{4}-7[a-f0-9]{3}-[a-f0-9]{4}-[a-f0-9]{12})/events\z");
            slug=match.Groups[1].Value;instanceId=match.Groups[2].Value;chatId=match.Groups[3].Value;return match.Success;
        }
        private static bool HasToken(string value,string token)
        {foreach(string part in (value??"").Split(','))if(String.Equals(part.Trim(),token,StringComparison.OrdinalIgnoreCase))return true;return false;}
        internal static string AcceptKey(WebhookHttpRequest request)
        {
            string slug,id,upgrade,connection,version,key;
            if(request.Method!="GET"||!IsEventsPath(request.Path,out slug,out id))throw new WebhookException(400,"Adresse WebSocket invalide.");
            request.Headers.TryGetValue("Upgrade",out upgrade);request.Headers.TryGetValue("Connection",out connection);
            request.Headers.TryGetValue("Sec-WebSocket-Version",out version);request.Headers.TryGetValue("Sec-WebSocket-Key",out key);
            if(!String.Equals(upgrade,"websocket",StringComparison.OrdinalIgnoreCase)||!HasToken(connection,"Upgrade")||version!="13"||String.IsNullOrEmpty(key))throw new WebhookException(400,"Negociation WebSocket invalide.");
            byte[] decoded;try{decoded=Convert.FromBase64String(key);}catch(FormatException){throw new WebhookException(400,"Cle WebSocket invalide.");}
            if(decoded.Length!=16||Convert.ToBase64String(decoded)!=key)throw new WebhookException(400,"Cle WebSocket invalide.");
            using(var sha=SHA1.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        }
        internal static void WriteHandshake(Stream stream,string accept)
        {
            byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\nCache-Control: no-store\r\n\r\n");
            stream.Write(response,0,response.Length);stream.Flush();
        }
        private static int Byte(Stream stream)
        {int value=stream.ReadByte();if(value<0)throw new EndOfStreamException();return value;}
        private static byte[] Exactly(Stream stream,int length)
        {var data=new byte[length];int offset=0;while(offset<length){int count=stream.Read(data,offset,length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}return data;}
        internal static WebhookWebSocketFrame ReadControlFrame(Stream stream)
        {
            int first=Byte(stream),second=Byte(stream);byte opcode=(byte)(first&15);bool final=(first&128)!=0;
            if((first&112)!=0||(second&128)==0)throw new WebhookWebSocketException(1002,"Trame WebSocket invalide.");
            ulong length=(ulong)(second&127);
            if(length==126){length=(ulong)(Byte(stream)<<8|Byte(stream));if(length<126)throw new WebhookWebSocketException(1002,"Longueur WebSocket invalide.");}
            else if(length==127){length=0;for(int i=0;i<8;i++){int value=Byte(stream);if(i==0&&(value&128)!=0)throw new WebhookWebSocketException(1002,"Longueur WebSocket invalide.");length=(length<<8)|(uint)value;}if(length<65536)throw new WebhookWebSocketException(1002,"Longueur WebSocket invalide.");}
            if(length>MaximumFrameBytes)throw new WebhookWebSocketException(1009,"Trame WebSocket trop longue.");
            if(opcode!=8&&opcode!=9&&opcode!=10)throw new WebhookWebSocketException(1003,"Les messages passent par le formulaire HTTP.");
            if(!final||length>125)throw new WebhookWebSocketException(1002,"Trame de controle invalide.");
            byte[] mask=Exactly(stream,4),payload=Exactly(stream,(int)length);for(int i=0;i<payload.Length;i++)payload[i]^=mask[i%4];
            if(opcode==8){
                if(payload.Length==1)throw new WebhookWebSocketException(1002,"Fermeture WebSocket invalide.");
                if(payload.Length>=2){
                    int code=(payload[0]<<8)|payload[1];if(!ValidCloseCode(code))throw new WebhookWebSocketException(1002,"Code de fermeture invalide.");
                    try{Utf8.GetString(payload,2,payload.Length-2);}catch(DecoderFallbackException){throw new WebhookWebSocketException(1007,"Texte de fermeture invalide.");}
                }
            }
            return new WebhookWebSocketFrame{Opcode=opcode,Payload=payload};
        }
        private static bool ValidCloseCode(int code)
        {return (code>=1000&&code<=1014&&code!=1004&&code!=1005&&code!=1006)||(code>=3000&&code<=4999);}
        internal static byte[] EncodeFrame(byte opcode,byte[] payload,bool final)
        {
            payload=payload??new byte[0];if(payload.Length>MaximumFrameBytes)throw new ArgumentOutOfRangeException("payload");
            if(opcode>=8&&(!final||payload.Length>125))throw new ArgumentException("Trame de controle invalide.");
            using(var output=new MemoryStream()){
                output.WriteByte((byte)((final?128:0)|opcode));
                if(payload.Length<126)output.WriteByte((byte)payload.Length);
                else if(payload.Length<=65535){output.WriteByte(126);output.WriteByte((byte)(payload.Length>>8));output.WriteByte((byte)payload.Length);}
                else {output.WriteByte(127);for(int shift=56;shift>=0;shift-=8)output.WriteByte((byte)(((ulong)payload.Length)>>shift));}
                output.Write(payload,0,payload.Length);return output.ToArray();
            }
        }
        private static void Frame(Stream stream,byte opcode,byte[] payload,bool final)
        {byte[] frame=EncodeFrame(opcode,payload,final);stream.Write(frame,0,frame.Length);stream.Flush();}
        internal static void WriteText(Stream stream,string text)
        {
            int length=Utf8.GetByteCount(text);if(length>MaximumMessageBytes)throw new WebhookWebSocketException(1009,"La discussion depasse la taille du flux.");
            byte[] bytes=Utf8.GetBytes(text);int offset=0;
            do{int count=Math.Min(MaximumFrameBytes,bytes.Length-offset);var part=new byte[count];Buffer.BlockCopy(bytes,offset,part,0,count);Frame(stream,(byte)(offset==0?1:0),part,offset+count==bytes.Length);offset+=count;}while(offset<bytes.Length);
        }
        private static void Close(Stream stream,ushort code,string reason)
        {
            byte[] reasonBytes=Utf8.GetBytes(reason??"");if(reasonBytes.Length>123)reasonBytes=Encoding.ASCII.GetBytes("Connexion terminee.");
            var bytes=new byte[reasonBytes.Length+2];bytes[0]=(byte)(code>>8);bytes[1]=(byte)code;Buffer.BlockCopy(reasonBytes,0,bytes,2,reasonBytes.Length);Frame(stream,8,bytes,true);
        }
        private static bool Equal(byte[] first,byte[] second)
        {if(first==null||second==null||first.Length!=second.Length)return false;for(int i=0;i<first.Length;i++)if(first[i]!=second[i])return false;return true;}
        internal static void Run(TcpClient client,Stream stream,RelayStore store,string slug,string bearer,string chatId,object initial,CancellationToken token)
        { Run(client,stream,()=>{Webhooks.ValidateAuthorization(store,slug,bearer);return WebhookChats.Snapshot(store,slug,bearer,chatId);},initial,token); }
        internal static void Run(TcpClient client,Stream stream,Func<object> readSnapshot,object initial,CancellationToken token)
        {
            DateTime began=DateTime.UtcNow,nextSnapshot=began,nextPing=began.AddSeconds(15),pingSent=began;byte[] ping=null;
            string previous=null;object snapshot=initial;
            try{
                while(!token.IsCancellationRequested){
                    DateTime now=DateTime.UtcNow;
                    if((now-began).TotalMinutes>=10){Close(stream,1001,"Reconnectez le suivi de la discussion.");return;}
                    if(now>=nextSnapshot){
                        // Rotation, disabling, route scope and chat ownership are checked
                        // throughout the connection, not only at the HTTP handshake.
                        // Even the initial payload is reauthorized immediately
                        // before emission. Revocation also closes existing streams.
                        object authorized=readSnapshot();
                        if(snapshot==null)snapshot=authorized;
                        string current=Json.Write(new{type="snapshot",data=snapshot});snapshot=null;
                        if(current!=previous){WriteText(stream,current);previous=current;}
                        nextSnapshot=DateTime.UtcNow.AddSeconds(1);
                    }
                    if(ping!=null&&(now-pingSent).TotalSeconds>=45){Close(stream,1001,"Le client ne repond plus.");return;}
                    if(ping==null&&now>=nextPing){ping=Encoding.ASCII.GetBytes(now.Ticks.ToString(CultureInfo.InvariantCulture));Frame(stream,9,ping,true);pingSent=now;nextPing=now.AddSeconds(15);}
                    if(client.Available>0){
                        // A partial frame shares a short absolute deadline. Its header
                        // and payload total at most 131 bytes for permitted opcodes.
                        var bounded=new DeadlineStream(stream,DateTime.UtcNow.AddSeconds(2));var frame=ReadControlFrame(bounded);
                        if(frame.Opcode==8){Frame(stream,8,frame.Payload,true);return;}
                        if(frame.Opcode==9)Frame(stream,10,frame.Payload,true);
                        else if(frame.Opcode==10&&Equal(frame.Payload,ping)){ping=null;nextPing=now.AddSeconds(15);}
                    }
                    token.WaitHandle.WaitOne(100);
                }
            }catch(WebhookException ex){
                try{WriteText(stream,Json.Write(new{type="error",error=ex.Message}));Close(stream,1008,"Acces a la discussion interrompu.");}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}
            }catch(WebhookWebSocketException ex){try{Close(stream,ex.CloseCode,ex.Message);}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}}
            catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}
            catch(Exception){try{Close(stream,1011,"Le suivi de la discussion est momentanement indisponible.");}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}}
        }
        private sealed class DeadlineStream:Stream
        {
            private readonly Stream inner;private readonly DateTime deadline;
            internal DeadlineStream(Stream inner,DateTime deadline){this.inner=inner;this.deadline=deadline;}
            private void Bound(){double left=(deadline-DateTime.UtcNow).TotalMilliseconds;if(left<=0)throw new IOException("Delai de trame expire.");if(inner.CanTimeout)inner.ReadTimeout=Math.Max(1,(int)Math.Ceiling(left));}
            public override int Read(byte[] buffer,int offset,int count){Bound();return inner.Read(buffer,offset,count);}
            public override int ReadByte(){Bound();return inner.ReadByte();}
            public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        }
    }
}
