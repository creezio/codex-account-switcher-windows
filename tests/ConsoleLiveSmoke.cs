using System;
using System.IO;
using System.Threading;
using Creezio.Switcher;

internal static class ConsoleLiveSmoke
{
    public static int Main(string[] args)
    {
        try{
            if(args.Length<3)throw new InvalidOperationException("root channel new|thread [job]");
            var root=Path.GetFullPath(args[0]);if(RelayStore.SamePath(root,RelayStore.DefaultRoot))throw new InvalidOperationException("Use an isolated acceptance store");
            var store=new RelayStore(root);var live=new RelayStore(RelayStore.DefaultRoot);var channel=live.Channel(args[1]);store.Register(channel);
            var transport=new DesktopRelayTransport();transport.Verify(channel);
            RelayMessage message;
            if(args.Length==4)message=store.Message(args[3]);else message=new DirectMessages(store,transport).Submit(new RelayJobSpec{To=channel.Id,Title="Switcher 0.8 · direct message acceptance",Prompt="Test explicite du transport depuis Account Switcher. Sans outil, fichier ni délégation, réponds uniquement SWITCHER_DIRECT_OK puis CREEZIO_OUTCOME {\"status\":\"succeeded\"}.",Access="read"},args[2]=="new"?null:args[2],CancellationToken.None).GetAwaiter().GetResult();
            var engine=new RelayEngine(store);DateTime until=DateTime.UtcNow.AddSeconds(35);
            do{engine.Process(message.Id,CancellationToken.None).GetAwaiter().GetResult();message=store.Message(message.Id);if(message.State=="completed"||message.State=="uncertain"||message.BlockReason=="permissions")break;Thread.Sleep(1000);}while(DateTime.UtcNow<until);
            Console.WriteLine(Json.Write(new{message.Id,message.State,message.BlockReason,message.Error,message.TargetThreadId,hasExpectedResult=(message.Result??"").Contains("SWITCHER_DIRECT_OK"),message.OperatorOrigin,message.ReturnState}));
            return message.State=="completed"?0:2;
        }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
    }
}
