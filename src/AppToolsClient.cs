using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Creezio.Switcher
{
    // Version-sensitive desktop adapter. Never fall back to UI clicks or an unauthenticated remote listener.
    internal sealed class AppToolsClient : IDisposable
    {
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
        private readonly NamedPipeClientStream pipe;
        private int sequence;
        public readonly int ServerPid;
        public readonly long ServerStartTicks;
        public AppToolsClient(string path,int expectedPid=0,long expectedTicks=0)
        {
            const string prefix="\\\\.\\pipe\\";
            if(String.IsNullOrWhiteSpace(path) || !path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || path.Length>240 || path.Substring(prefix.Length).IndexOfAny(new[]{'\\','/'})>=0)
                throw new InvalidOperationException("Le relais exige un canal Windows local fourni par Codex.");
            pipe=new NamedPipeClientStream(".",path.Substring(prefix.Length),PipeDirection.InOut,PipeOptions.Asynchronous);
            try {
                pipe.Connect(3000);
                uint pid;
                if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid)) throw new IOException("Pipe identity unavailable.");
                using(var process=Process.GetProcessById((int)pid)) {
                    ServerPid=(int)pid;ServerStartTicks=process.StartTime.ToUniversalTime().Ticks;
                    string name=Path.GetFileName(process.MainModule.FileName);
                    if(!name.Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase) && !name.Equals("Codex.exe",StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected pipe owner.");
                }
                if(expectedPid!=0 && (expectedPid!=ServerPid || expectedTicks!=ServerStartTicks))
                    throw new InvalidOperationException("Codex a redémarré. Reconnectez ce canal depuis sa conversation.");
            } catch {pipe.Dispose();throw;}
        }
        private async Task ReadExact(byte[] buffer,CancellationToken token)
        {
            int offset=0;
            while(offset<buffer.Length) {int read=await pipe.ReadAsync(buffer,offset,buffer.Length-offset,token);if(read==0)throw new EndOfStreamException();offset+=read;}
        }
        public async Task<object> Request(string method,object parameters,CancellationToken token)
        {
            int id=++sequence;
            byte[] bytes=Encoding.UTF8.GetBytes(Json.Write(new {jsonrpc="2.0",id=id,method=method,@params=parameters}));
            if(bytes.Length>4000000)throw new InvalidOperationException("Message de relais trop volumineux.");
            // The desktop's native pipe uses a 4-byte little-endian length prefix.
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                deadline.CancelAfter(TimeSpan.FromSeconds(55));
                using(deadline.Token.Register(()=>pipe.Dispose())) {
                    byte[] header=BitConverter.GetBytes(bytes.Length);
                    await pipe.WriteAsync(header,0,header.Length,deadline.Token);
                    await pipe.WriteAsync(bytes,0,bytes.Length,deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                    await ReadExact(header,deadline.Token);
                    int size=BitConverter.ToInt32(header,0);
                    if(size<=0 || size>8000000)throw new IOException("Invalid desktop frame.");
                    bytes=new byte[size];await ReadExact(bytes,deadline.Token);
                    var reply=Json.Read<object>(Encoding.UTF8.GetString(bytes));
                    if(Convert.ToString(Json.Get(reply,"id"))!=id.ToString())throw new IOException("Unexpected desktop response.");
                    if(Json.Get(reply,"error")!=null)throw new InvalidOperationException("Codex a refusé la demande de relais. Vérifiez le canal et sa conversation.");
                    return Json.Get(reply,"result");
                }
            }
        }
        public async Task<object> Call(string caller,string tool,object arguments,CancellationToken token)
        {
            var result=await Request("tools/call",new {callerSource="codex",callId="creezio-"+Guid.NewGuid().ToString("N"),@namespace="codex_app",threadId=caller,tool=tool,turnId="creezio-"+Guid.NewGuid().ToString("N"),arguments=arguments},token);
            if(!Object.Equals(Json.Get(result,"success"),true)) {
                string detail=String.Join(" ",RelayEngine.Rows(Json.Get(result,"contentItems")).Select(x=>Json.Str(Json.Get(x,"text"))));
                if(detail.Length>1800)detail=detail.Substring(0,1800);
                throw new InvalidOperationException("Codex a refusé la demande : "+detail);
            }
            var items=Json.Get(result,"contentItems") as IEnumerable;
            if(items!=null)foreach(object item in items) {
                string text=Json.Str(Json.Get(item,"text"));
                if(text.Length==0)continue;
                try{return Json.Read<object>(text);}catch(ArgumentException){}catch(InvalidOperationException){}
            }
            throw new InvalidOperationException("Réponse Codex non reconnue. Le relais conserve la demande pour vérification.");
        }
        public void Dispose(){pipe.Dispose();}
    }
}
