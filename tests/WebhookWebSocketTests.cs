using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class WebhookWebSocketTests
{
    private const string ClientKey="dGhlIHNhbXBsZSBub25jZQ==";
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle,out uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void HttpReject(int status,Action action){try{action();}catch(WebhookException e){Assert(e.StatusCode==status,"unexpected HTTP rejection "+e.StatusCode);return;}throw new Exception("Expected HTTP rejection "+status);}
    private static void FrameReject(int code,byte[] bytes){try{using(var stream=new MemoryStream(bytes))WebhookWebSocket.ReadControlFrame(stream);}catch(WebhookWebSocketException e){Assert(e.CloseCode==code,"unexpected close code "+e.CloseCode);return;}throw new Exception("Expected frame rejection "+code);}
    private sealed class Fixture
    {
        internal readonly RelayStore Store;internal readonly WebhookRoute Route;internal readonly string Key;internal readonly WebhookJob Job;
        internal Fixture(string root,string name)
        {
            string folder=System.IO.Path.Combine(root,"webhook-websocket-tests",name),home=System.IO.Path.Combine(folder,"home"),workspace=System.IO.Path.Combine(folder,"workspace");Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);Store=new RelayStore(System.IO.Path.Combine(folder,"relay"));
            Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true,Port=47831});
            var route=Webhooks.SaveRoute(Store,new WebhookRoute{Name="Fictional live chat",Slug="fixture-hook",InstanceId=new string('a',32),AccountKey="fictional-websocket-account",Home=home,Workspace=workspace,Access="desktop",Enabled=true});
            Key=Webhooks.RotateKey(Store,route.Id);Route=Webhooks.Route(Store,route.Id);Job=Webhooks.Submit(Store,Route.Slug,Key,"ws-root","{\"title\":\"Live fixture\",\"prompt\":\"Return a fictional receipt only.\"}");
        }
        internal string Path{get{return "/v1/hooks/fixture-hook/chats/"+Job.Id+"/events";}}
        internal string Headers(string bearer=null)
        {return "GET "+Path+" HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer "+(bearer??Key)+"\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: "+ClientKey+"\r\n\r\n";}
    }
    private static WebhookHttpRequest Parse(string text)
    {using(var stream=new MemoryStream(Encoding.ASCII.GetBytes(text)))return WebhookHttp.ReadRequest(stream);}
    private static byte[] Masked(byte opcode,byte[] payload,bool final=true)
    {
        using(var result=new MemoryStream()){
            result.WriteByte((byte)((final?128:0)|opcode));
            if(payload.Length<126)result.WriteByte((byte)(128|payload.Length));
            else {result.WriteByte(254);result.WriteByte((byte)(payload.Length>>8));result.WriteByte((byte)payload.Length);}
            byte[] mask={3,7,9,13};result.Write(mask,0,mask.Length);for(int i=0;i<payload.Length;i++)result.WriteByte((byte)(payload[i]^mask[i%4]));return result.ToArray();
        }
    }
    private static string Header(Stream stream)
    {
        var text=new StringBuilder();while(text.Length<16384){int value=stream.ReadByte();if(value<0)break;text.Append((char)value);if(text.Length>=4&&text.ToString(text.Length-4,4)=="\r\n\r\n")return text.ToString();}throw new Exception("Missing bounded HTTP response");
    }
    private sealed class ServerFrame{internal int Opcode;internal bool Final;internal byte[] Data;}
    private static int ReadByte(Stream stream){int value=stream.ReadByte();if(value<0)throw new EndOfStreamException();return value;}
    private static ServerFrame ReadServer(Stream stream)
    {
        int first=ReadByte(stream),second=ReadByte(stream);Assert((second&128)==0,"server frame was masked");long length=second&127;
        if(length==126)length=(ReadByte(stream)<<8)|ReadByte(stream);
        else if(length==127){length=0;for(int i=0;i<8;i++)length=(length<<8)|((uint)ReadByte(stream));}
        Assert(length<=WebhookWebSocket.MaximumFrameBytes,"server frame exceeded bound");var data=new byte[(int)length];int offset=0;while(offset<data.Length){int count=stream.Read(data,offset,data.Length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}
        return new ServerFrame{Opcode=first&15,Final=(first&128)!=0,Data=data};
    }
    private static TcpClient Connect(int port,string headers,out NetworkStream stream,out string response)
    {
        var client=new TcpClient();client.Connect(IPAddress.Loopback,port);client.ReceiveTimeout=5000;client.SendTimeout=5000;stream=client.GetStream();byte[] bytes=Encoding.ASCII.GetBytes(headers);stream.Write(bytes,0,bytes.Length);response=Header(stream);return client;
    }
    private static string Exchange(int port,string text)
    {NetworkStream stream;string header;using(var client=Connect(port,text,out stream,out header)){using(var reader=new StreamReader(stream,Encoding.UTF8))return header+reader.ReadToEnd();}}
    public static int Main(string[] args)
    {
        if(args.Length!=1)return 2;string root=System.IO.Path.GetFullPath(args[0]);if(Directory.Exists(root)&&Directory.GetFileSystemEntries(root).Length!=0)return 2;Directory.CreateDirectory(root);int count=0;
        try{RunAll((name,test)=>{test();count++;Console.WriteLine("PASS "+name);},root);Console.WriteLine(count+" WebSocket tests passed.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("websocket actual listener and authenticated sockets are non-inheritable",()=>{
            var f=new Fixture(root,"handle-flags");using(var server=new WebhookHttp(f.Store,0)){
                var listener=(TcpListener)typeof(WebhookHttp).GetField("listener",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);uint flags;
                Assert(GetHandleInformation(listener.Server.Handle,out flags)&&(flags&1)==0,"constructor left inheritable listening socket");
                Assert(SetHandleInformation(listener.Server.Handle,1,1),"fixture could not emulate Framework inherited flag");server.Start();Assert(GetHandleInformation(listener.Server.Handle,out flags)&&(flags&1)==0,"Start retained inheritable listening socket");
                NetworkStream stream;string header;using(var client=Connect(server.Port,f.Headers(),out stream,out header)){
                    ReadServer(stream);var sockets=(HashSet<TcpClient>)typeof(WebhookHttp).GetField("sockets",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);lock(sockets){Assert(sockets.Count==1,"fixture accepted socket not active");foreach(var accepted in sockets)Assert(GetHandleInformation(accepted.Client.Handle,out flags)&&(flags&1)==0,"authenticated accepted socket can leak into relay child");}
                }
            }
        });
        check("websocket redirected child cannot retain listening port after server disposal",()=>{
            var f=new Fixture(root,"child-inheritance");Process child=null;var server=new WebhookHttp(f.Store,0);int port=0;
            try{
                server.Start();port=server.Port;var info=new ProcessStartInfo{FileName=System.IO.Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"),Arguments="-NoLogo -NoProfile -NonInteractive -Command \"[Console]::Out.WriteLine('READY'); Start-Sleep -Seconds 20\"",UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
                child=Process.Start(info);Assert(child!=null,"fixture child did not start");var ready=Task.Run(()=>child.StandardOutput.ReadLine());Assert(ready.Wait(5000)&&ready.Result=="READY"&&!child.HasExited,"fixture child did not remain alive");
                server.Dispose();server=null;var replacement=new TcpListener(IPAddress.Loopback,port);try{replacement.Start();Assert(!child.HasExited,"child ended before inherited port could be checked");}finally{replacement.Stop();}
            }finally{if(server!=null)server.Dispose();if(child!=null){if(!child.HasExited){child.Kill();child.WaitForExit(3000);}child.Dispose();}}
        });
        check("websocket handshake matches RFC6455 accept vector on exact chat route",()=>{
            var f=new Fixture(root,"handshake");var request=Parse(f.Headers());Assert(WebhookWebSocket.AcceptKey(request)=="s3pPLMBiTxaQ9kYGzzhZRbK+xOo=","RFC6455 accept mismatch");string slug,id;Assert(WebhookWebSocket.IsEventsPath(f.Path,out slug,out id)&&slug==f.Route.Slug&&id==f.Job.Id,"events path lost immutable chat root");
        });
        check("websocket handshake rejects version key token method and wrong endpoint",()=>{
            var f=new Fixture(root,"bad-handshake");foreach(string changed in new[]{f.Headers().Replace("Version: 13","Version: 12"),f.Headers().Replace(ClientKey,"not-base64"),f.Headers().Replace(ClientKey,Convert.ToBase64String(new byte[15])),f.Headers().Replace("keep-alive, Upgrade","keep-alive"),f.Headers().Replace("Upgrade: websocket","Upgrade: h2c")})HttpReject(400,()=>WebhookWebSocket.AcceptKey(Parse(changed)));
            var request=Parse(f.Headers());request.Method="POST";HttpReject(400,()=>WebhookWebSocket.AcceptKey(request));request.Method="GET";request.Path="/v1/hooks/fixture-hook/jobs";HttpReject(400,()=>WebhookWebSocket.AcceptKey(request));
        });
        check("websocket upgrade remains forbidden on jobs health and encoded endpoints",()=>{
            var f=new Fixture(root,"upgrade-scope");foreach(string path in new[]{"/health","/v1/hooks/fixture-hook/jobs",f.Path+"?token=secret",f.Path.Replace("events","%65vents")})HttpReject(400,()=>Parse(f.Headers().Replace(f.Path,path)));
        });
        check("websocket reads only bounded masked control frames",()=>{
            byte[] body=Encoding.UTF8.GetBytes("ping-test");foreach(byte opcode in new byte[]{9,10})using(var stream=new MemoryStream(Masked(opcode,body))){var frame=WebhookWebSocket.ReadControlFrame(stream);Assert(frame.Opcode==opcode&&frame.Payload.SequenceEqual(body),"masked payload did not round-trip");}
            using(var stream=new MemoryStream(Masked(8,new byte[0])))Assert(WebhookWebSocket.ReadControlFrame(stream).Opcode==8,"empty close rejected");
            FrameReject(1002,new byte[]{137,0});FrameReject(1002,Masked(9,new byte[0],false));FrameReject(1002,Masked(9,new byte[126]));FrameReject(1003,Masked(1,Encoding.UTF8.GetBytes("No side channel")));FrameReject(1002,new byte[]{201,128});
        });
        check("websocket rejects excessive noncanonical and truncated frames before allocation",()=>{
            FrameReject(1009,new byte[]{137,255,0,0,0,0,0,1,0,1});FrameReject(1002,new byte[]{137,254,0,1});FrameReject(1002,new byte[]{137,255,128,0,0,0,0,0,0,0});
            bool ended=false;try{using(var stream=new MemoryStream(new byte[]{137,128,0,0}))WebhookWebSocket.ReadControlFrame(stream);}catch(EndOfStreamException){ended=true;}Assert(ended,"truncated mask was accepted");
        });
        check("websocket validates close status and strict UTF8",()=>{
            FrameReject(1002,Masked(8,new byte[]{0}));FrameReject(1002,Masked(8,new byte[]{3,237}));FrameReject(1007,Masked(8,new byte[]{3,232,255}));using(var stream=new MemoryStream(Masked(8,new byte[]{3,232})))Assert(WebhookWebSocket.ReadControlFrame(stream).Payload.Length==2,"normal close rejected");
        });
        check("websocket fragments bounded UTF8 snapshots and never masks server frames",()=>{
            string text=new string('é',40000)+"終";using(var output=new MemoryStream()){
                WebhookWebSocket.WriteText(output,text);output.Position=0;var first=ReadServer(output);var second=ReadServer(output);Assert(first.Opcode==1&&!first.Final&&first.Data.Length==65536&&second.Opcode==0&&second.Final,"fragment sequence invalid");Assert(Encoding.UTF8.GetString(first.Data.Concat(second.Data).ToArray())==text,"UTF8 message changed across fragments");Assert(output.Position==output.Length,"unexpected extra frame");
            }
            bool bounded=false;try{using(var stream=new MemoryStream())WebhookWebSocket.WriteText(stream,new string('x',WebhookWebSocket.MaximumMessageBytes+1));}catch(WebhookWebSocketException e){bounded=e.CloseCode==1009;}Assert(bounded,"oversized snapshot allocated unbounded output");
        });
        check("websocket HTTP chat list snapshot and upgrade requirement preserve bearer scope",()=>{
            var f=new Fixture(root,"http-chat");var request=Parse("GET /v1/hooks/fixture-hook/chats HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer "+f.Key+"\r\n\r\n");Assert(WebhookHttp.Handle(f.Store,request).Body.Contains(f.Job.Id),"chat list omitted webhook root");request.Path="/v1/hooks/fixture-hook/chats/"+f.Job.Id;var snapshot=WebhookHttp.Handle(f.Store,request);Assert(snapshot.Status==200&&snapshot.Body.Contains("Return a fictional receipt"),"snapshot missing admitted user prompt");request.Path=f.Path;Assert(WebhookHttp.Handle(f.Store,request).Status==426,"non-upgrade events request was accepted");request.Headers["Authorization"]="Bearer wrong";Assert(WebhookHttp.Handle(f.Store,request).Status==401,"bad token learned upgrade status");
        });
        check("websocket real TCP authenticates before upgrade and streams one actual snapshot",()=>{
            var f=new Fixture(root,"tcp-stream");using(var server=new WebhookHttp(f.Store,0)){
                server.Start();Assert(Exchange(server.Port,f.Headers("wrong")).StartsWith("HTTP/1.1 401",StringComparison.Ordinal),"unauthorized upgrade was accepted");
                NetworkStream stream;string header;using(var client=Connect(server.Port,f.Headers(),out stream,out header)){
                    Assert(header.StartsWith("HTTP/1.1 101",StringComparison.Ordinal)&&header.Contains("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=")&&!header.Contains("Access-Control-Allow-Origin"),"upgrade contract invalid");
                    var first=ReadServer(stream);string message=Encoding.UTF8.GetString(first.Data);Assert(first.Opcode==1&&message.Contains("\"type\":\"snapshot\"")&&message.Contains(f.Job.Id)&&!message.Contains(f.Key)&&!message.Contains(f.Route.AccountKey),"snapshot wrong or leaked binding");
                    byte[] body=Encoding.ASCII.GetBytes("client-ping"),ping=Masked(9,body);stream.Write(ping,0,ping.Length);var pong=ReadServer(stream);Assert(pong.Opcode==10&&pong.Data.SequenceEqual(body),"control ping was not answered");
                }
            }
        });
        check("websocket observes persisted changes then terminates when key rotates",()=>{
            var f=new Fixture(root,"revoke");using(var server=new WebhookHttp(f.Store,0)){
                server.Start();NetworkStream stream;string header;using(var client=Connect(server.Port,f.Headers(),out stream,out header)){
                    Assert(header.StartsWith("HTTP/1.1 101",StringComparison.Ordinal),"upgrade failed");ReadServer(stream);var job=Webhooks.Job(f.Store,f.Job.Id);job.State="completed";job.Result="Fictional websocket completion";Webhooks.SaveJob(f.Store,job);
                    var update=ReadServer(stream);Assert(update.Opcode==1&&Encoding.UTF8.GetString(update.Data).Contains("completed"),"persisted status did not reach stream");Webhooks.RotateKey(f.Store,f.Route.Id);var error=ReadServer(stream);Assert(error.Opcode==1&&Encoding.UTF8.GetString(error.Data).Contains("\"type\":\"error\""),"revocation did not notify client");Assert(ReadServer(stream).Opcode==8,"revocation did not close stream");
                }
            }
        });
        check("websocket disabling broker revokes an already upgraded connection",()=>{
            var f=new Fixture(root,"disable");using(var server=new WebhookHttp(f.Store,0)){
                server.Start();NetworkStream stream;string header;using(var client=Connect(server.Port,f.Headers(),out stream,out header)){
                    Assert(header.StartsWith("HTTP/1.1 101",StringComparison.Ordinal),"upgrade failed");ReadServer(stream);Webhooks.SaveConfig(f.Store,new WebhookConfig{Enabled=false,Port=47831});
                    var error=ReadServer(stream);Assert(error.Opcode==1&&Encoding.UTF8.GetString(error.Data).Contains("\"type\":\"error\""),"broker disable did not revoke stream");Assert(ReadServer(stream).Opcode==8,"disabled broker retained stream");
                }
            }
        });
        check("websocket stream capacity does not starve ordinary HTTP requests",()=>{
            var f=new Fixture(root,"capacity");var connections=new List<TcpClient>();using(var server=new WebhookHttp(f.Store,0)){
                try{
                    server.Start();for(int i=0;i<4;i++){NetworkStream stream;string header;var client=Connect(server.Port,f.Headers(),out stream,out header);connections.Add(client);Assert(header.StartsWith("HTTP/1.1 101",StringComparison.Ordinal),"stream admission failed");ReadServer(stream);}
                    Assert(Exchange(server.Port,f.Headers()).StartsWith("HTTP/1.1 429",StringComparison.Ordinal),"stream capacity not bounded");Assert(Exchange(server.Port,"GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n").StartsWith("HTTP/1.1 200",StringComparison.Ordinal),"HTTP starved by long-lived streams");
                }finally{foreach(var connection in connections)connection.Close();}
            }
        });
        check("websocket server disposal closes active streams without waiting for lifetime",()=>{
            var f=new Fixture(root,"dispose");var server=new WebhookHttp(f.Store,0);server.Start();NetworkStream stream;string header;using(var client=Connect(server.Port,f.Headers(),out stream,out header)){
                ReadServer(stream);var elapsed=Stopwatch.StartNew();server.Dispose();Assert(elapsed.ElapsedMilliseconds<3000,"server shutdown waited for stream lifetime");bool closed=false;try{closed=stream.ReadByte()<0;}catch(IOException){closed=true;}Assert(closed,"active stream survived shutdown");
            }
        });
    }
}
