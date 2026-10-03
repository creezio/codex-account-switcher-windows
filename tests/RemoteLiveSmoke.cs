using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Creezio.Switcher;

internal static class RemoteLiveSmoke
{
    private static RelayMessage Finish(RelayStore store,RelayMessage m)
    {
        var engine=new RelayEngine(store);DateTime until=DateTime.UtcNow.AddSeconds(45);
        do{engine.Process(m.Id,CancellationToken.None).GetAwaiter().GetResult();m=store.Message(m.Id);if(m.State=="completed"||m.State=="uncertain"||m.BlockReason=="permissions")break;Thread.Sleep(1000);}while(DateTime.UtcNow<until);return m;
    }
    public static int Main(string[] args)
    {
        try{
            if(args.Length!=3)throw new InvalidOperationException("root liveChannel testThread");var root=Path.GetFullPath(args[0]);if(RelayStore.SamePath(root,RelayStore.DefaultRoot))throw new InvalidOperationException("Isolated store required");
            var owner=new RelayStore(Path.Combine(root,"owner"));var client=new RelayStore(Path.Combine(root,"client"));var channel=new RelayStore(RelayStore.DefaultRoot).Channel(args[1]);new DesktopRelayTransport().Verify(channel);owner.Register(channel);
            var finder=new TcpListener(IPAddress.Loopback,0);finder.Start();int port=((IPEndPoint)finder.LocalEndpoint).Port;finder.Stop();RemotePeers.SaveConfig(owner,new RemoteConfig{Enabled=true,Port=port});
            var invite=RemotePeers.Invite(owner,"Acceptance loopback","127.0.0.1",new[]{channel.Id},true,true,true,1,new System.Collections.Generic.Dictionary<string,string>{{args[2],channel.Id}});var peer=RemotePeers.Import(client,invite);
            using(var stop=new CancellationTokenSource()){
                var listener=new RemoteGateway(owner).Listen(RemotePeers.Config(owner),stop.Token);
                try{
                    var remote=RemotePeers.Connect(client,peer,new SharedChannel{Id=channel.Id,Name="Acceptance native Codex",Account=channel.AccountKey,Workspace=channel.Workspace,Anchor=args[2],RequireFullAccess=channel.RequireFullAccess},"remote-live",channel.Workspace);
                    var m=new DirectMessages(client).Submit(new RelayJobSpec{To=remote.Id,Title="Switcher 0.8 · TLS acceptance",Access="read",Prompt="Test autorisé de messagerie entre deux extrémités Switcher. Sans outil, fichier ni délégation, réponds SWITCHER_TLS_OK puis CREEZIO_OUTCOME {\"status\":\"succeeded\"}."},args[2],CancellationToken.None).GetAwaiter().GetResult();m=Finish(client,m);bool direct=m.State=="completed"&&(m.Result??"").Contains("SWITCHER_TLS_OK");Console.WriteLine(Json.Write(new{test="TLS to native Codex",m.Id,m.TargetThreadId,m.State,m.Error,expected=direct}));if(!direct)return 2;
                    var assistance=new Assistance(owner);var ticket=assistance.Request(Guid.NewGuid().ToString("N"),channel.Id,args[2],"Test assistance native","Test autorisé ; aucune opération sur fichier ou service.","Vérifier le trajet réponse du responsable vers le chat.",true,CancellationToken.None).GetAwaiter().GetResult();
                    RelayWorker.Stop(owner);
                    RemoteClient.Call(peer,"assistance_reply",channel.Id,channel.AccountKey,channel.Workspace,new{id=ticket.Id,answer="Sans outil ni délégation, réponds uniquement SWITCHER_ASSISTANCE_OK puis CREEZIO_OUTCOME {\"status\":\"succeeded\"}.",access="read"},CancellationToken.None).GetAwaiter().GetResult();
                    assistance.Pump(CancellationToken.None).GetAwaiter().GetResult();ticket=assistance.Read(ticket.Id);var reply=Finish(owner,owner.Message(ticket.ReplyJob));assistance.Pump(CancellationToken.None).GetAwaiter().GetResult();ticket=assistance.Read(ticket.Id);bool answered=ticket.State=="delivered"&&(reply.Result??"").Contains("SWITCHER_ASSISTANCE_OK");Console.WriteLine(Json.Write(new{test="Remote assistance response to native Codex",ticket.Id,ticket.State,expected=answered}));return answered?0:2;
                }finally{stop.Cancel();try{listener.GetAwaiter().GetResult();}catch(OperationCanceledException){}RemotePeers.Revoke(owner,peer.Id);var config=RemotePeers.Config(owner);config.Enabled=false;RemotePeers.SaveConfig(owner,config);}
            }
        }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
    }
}
