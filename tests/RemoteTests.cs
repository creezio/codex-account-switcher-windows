using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class RemoteTests
{
    private static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    private static void Denied(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected denied request");}
    internal sealed class Fixture
    {
        public RelayStore Store;public ConsoleTests.Fake Fake=new ConsoleTests.Fake();public RemotePeer Peer;public RemoteGateway Gateway;public RelayChannel Channel;
        public Fixture(string root,int n,bool send=true,bool create=true)
        {
            string path=Path.Combine(root,"remote-"+n);Directory.CreateDirectory(path);Store=new RelayStore(Path.Combine(path,"relay"));
            Channel=new RelayChannel{Id="shared",Name="Shared",AccountKey="account",Home=path,Workspace=path,AnchorThreadId="existing",Enabled=true};Store.Register(Channel);
            RemotePeers.SaveConfig(Store,new RemoteConfig{Enabled=true});Peer=Json.Read<RemotePeer>(RemotePeers.Invite(Store,"Reviewer","127.0.0.1",new[]{Channel.Id},send,create,false,1));Gateway=new RemoteGateway(Store,Fake);
        }
        public RemoteRequest Request(string op,object args){return new RemoteRequest{Version=1,Id=Guid.NewGuid().ToString("N"),Grant=Peer.Id,Token=Peer.Token,Operation=op,Channel=Channel.Id,Account=Channel.AccountKey,Workspace=Channel.Workspace,Arguments=Json.Write(args)};}
        public object Call(RemoteRequest r){return Gateway.Handle(r,CancellationToken.None).GetAwaiter().GetResult();}
    }
    public static void RunAll(Action<string,Action> check,string root)
    {
        int n=0;
        check("remote invitation contains no account credential and is stored encrypted",()=>{var f=new Fixture(root,++n);Assert(!Json.Write(f.Peer).Contains("auth.json")&&RemotePeers.Grants(f.Store)[0].TokenHash!=f.Peer.Token,"credential material exported");RemotePeers.Import(f.Store,Json.Write(f.Peer));Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Store.Root,"remote-peers.dpapi"))).Contains(f.Peer.Token),"plain token");});
        check("remote wrong secret, expired and revoked grants are refused",()=>{var f=new Fixture(root,++n);var r=f.Request("channels",new{});r.Token="incorrect";Denied(()=>f.Call(r));r.Token=f.Peer.Token;RemotePeers.Revoke(f.Store,f.Peer.Id);Denied(()=>f.Call(r));f.Peer.Expires=DateTime.UtcNow.AddMinutes(-1).ToString("o");Denied(()=>RemotePeers.Import(f.Store,Json.Write(f.Peer)));});
        check("remote read only grant cannot send or create",()=>{var f=new Fixture(root,++n,false,false);Denied(()=>f.Call(f.Request("send_message_to_thread",new{threadId="existing",prompt="hi"})));Denied(()=>f.Call(f.Request("create_thread",new{title="hi",prompt="hi"})));Assert(f.Fake.Sends==0&&f.Fake.Creates==0,"read-only grant executed");});
        check("remote selected conversation boundary excludes other chats",()=>{var f=new Fixture(root,++n);Denied(()=>f.Call(f.Request("read_thread",new{threadId="private"})));Denied(()=>f.Call(f.Request("send_message_to_thread",new{threadId="private",prompt="hi"})));Assert(f.Fake.Sends==0,"private chat touched");});
        check("remote account or workspace change requires new share",()=>{var f=new Fixture(root,++n);var r=f.Request("read_thread",new{threadId="existing"});r.Account="other";Denied(()=>f.Call(r));r.Account=f.Channel.AccountKey;r.Workspace=root;Denied(()=>f.Call(r));});
        check("remote protocol cannot execute arbitrary tools",()=>{var f=new Fixture(root,++n);Denied(()=>f.Call(f.Request("exec_command",new{cmd="echo forbidden"})));Denied(()=>f.Call(f.Request("tools/call",new{})));});
        check("remote acknowledged message is deduplicated by persisted receipt",()=>{var f=new Fixture(root,++n);var r=f.Request("send_message_to_thread",new{threadId="existing",prompt="hi"});f.Call(r);new RemoteGateway(new RelayStore(f.Store.Root),f.Fake).Handle(r,CancellationToken.None).GetAwaiter().GetResult();Assert(f.Fake.Sends==1,"duplicate send after restart");r.Arguments=Json.Write(new{threadId="existing",prompt="changed"});Denied(()=>f.Call(r));});
        check("remote uncertain send never replays and receipt cannot cross grants",()=>{var f=new Fixture(root,++n);f.Fake.UnknownSend=true;var r=f.Request("send_message_to_thread",new{threadId="existing",prompt="hi"});try{f.Call(r);}catch(IOException){}Denied(()=>f.Call(r));Assert(f.Fake.Sends==1,"uncertain replay");var second=Json.Read<RemotePeer>(RemotePeers.Invite(f.Store,"Other","127.0.0.1",new[]{"shared"},true,true,false,1));r.Grant=second.Id;r.Token=second.Token;Denied(()=>f.Call(r));});
        check("remote created chat is added only to its creator grant",()=>{var f=new Fixture(root,++n);var second=Json.Read<RemotePeer>(RemotePeers.Invite(f.Store,"Other","127.0.0.1",new[]{"shared"},true,true,false,1));f.Call(f.Request("create_thread",new{title="New",prompt="hi"}));Assert(RemotePeers.Grants(f.Store).Single(g=>g.Id==f.Peer.Id).Threads.ContainsKey("created"),"new thread not shared");var r=f.Request("read_thread",new{threadId="created"});r.Grant=second.Id;r.Token=second.Token;Denied(()=>f.Call(r));});
        check("remote native busy chat is not interrupted",()=>{var f=new Fixture(root,++n);f.Fake.Occupied=true;Denied(()=>f.Call(f.Request("send_message_to_thread",new{threadId="existing",prompt="hi"})));Assert(f.Fake.Sends==0,"interrupted");});
        check("remote disable takes effect before another request",()=>{var f=new Fixture(root,++n);var config=RemotePeers.Config(f.Store);config.Enabled=false;RemotePeers.SaveConfig(f.Store,config);Denied(()=>f.Call(f.Request("channels",new{})));});
        check("remote strips arbitrary tool outputs from shared transcripts",()=>{var input=Json.Read<object>(Json.Write(new{thread=new{id="a"},turns=new[]{new{id="b",status="completed",items=new object[]{new{type="functionCallOutput",name="exec_command",output=new{text="PRIVATE_SECRET"}},new{type="agentMessage",text="Public answer"}}}}}));var clean=Json.Write(RemoteGateway.ConversationOnly(input));Assert(!clean.Contains("PRIVATE_SECRET")&&clean.Contains("Public answer"),"tool output leaked");});
        check("remote oversized frame is rejected before allocation",()=>{using(var stream=new MemoryStream(BitConverter.GetBytes(Int32.MaxValue))){try{RemoteWire.Read(stream,CancellationToken.None).GetAwaiter().GetResult();throw new Exception("oversize accepted");}catch(IOException){}}});
        check("remote real TLS roundtrip validates pin and token",()=>{
            var f=new Fixture(root,++n);var finder=new TcpListener(IPAddress.Loopback,0);finder.Start();int port=((IPEndPoint)finder.LocalEndpoint).Port;finder.Stop();var config=RemotePeers.Config(f.Store);config.Port=port;f.Peer.Port=port;
            using(var stop=new CancellationTokenSource()){
                var listener=f.Gateway.Listen(config,stop.Token);Thread.Sleep(100);
                try{var result=RemoteClient.Call(f.Peer,"channels",null,null,null,new{},CancellationToken.None).GetAwaiter().GetResult();Assert(RelayEngine.Rows(result).Count()==1,"TLS response missing");var bad=Json.Read<RemotePeer>(Json.Write(f.Peer));bad.Pin=new string('0',64);bool rejected=false;try{RemoteClient.Call(bad,"channels",null,null,null,new{},CancellationToken.None).GetAwaiter().GetResult();}catch{rejected=true;}Assert(rejected,"untrusted certificate accepted");bad=Json.Read<RemotePeer>(Json.Write(f.Peer));bad.Token=new string('0',64);Denied(()=>RemoteClient.Call(bad,"channels",null,null,null,new{},CancellationToken.None).GetAwaiter().GetResult());}
                finally{stop.Cancel();try{listener.GetAwaiter().GetResult();}catch(OperationCanceledException){}}
            }
        });
    }
}
