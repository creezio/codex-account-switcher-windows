using System;
using System.IO;
using System.Linq;
using System.Threading;
using Creezio.Switcher;

// Explicit acceptance harness: two visible test chats, no files or external actions.
internal static class NamedLiveSmoke
{
    public static int Main(string[] args)
    {
        try{
            var store=new RelayStore(RelayStore.DefaultRoot);var a=new AccountService(Path.GetDirectoryName(store.Root));
            var destination=NamedInstances.Resolve(a,args[0]);var token=CancellationToken.None;
            var source=NamedInstances.BindSource(store,token).GetAwaiter().GetResult();
            var transport=new DesktopRelayTransport();
            if(destination.AccountKey==source.AccountKey)throw new InvalidOperationException("Acceptance requires two distinct accounts.");
            var target=NamedInstances.Connect(store,a,destination,source.Workspace,token).GetAwaiter().GetResult();
            Console.WriteLine(Json.Write(new{connected=destination.Name,targetPermission=RelayPermissions.Read(target.Home,target.AnchorThreadId)}));
            var created=transport.Call(source,"create_thread",new{title="Switcher 0.9 · retour de délégation par nom",prompt="Test explicite du relais entre mes instances. Réponds uniquement SOURCE_READY sans aucun outil. Un résultat de test CREEZIO_RESULT te sera ensuite transmis ; à réception, réponds uniquement SWITCHER_RETURN_OK. Ne délègue rien et ne modifie aucun fichier.",target=new{type="projectless"}},token).GetAwaiter().GetResult();
            string thread=Json.Str(Json.Get(created,"threadId"));if(String.IsNullOrEmpty(thread))throw new Exception("No source test thread");
            var session=new RelaySession{Channel=source.Id,Thread=thread,Home=source.Home,Account=source.AccountKey};
            var request=new RelayJobSpec{Id=Guid.NewGuid().ToString("N"),Title="Switcher 0.9 · mission par nom",Prompt="Test explicite et borné du relais. Sans aucun outil, aucune modification de fichier et aucune délégation, réponds uniquement SWITCHER_NAMED_OK puis sur une seconde ligne CREEZIO_OUTCOME {\"status\":\"succeeded\"}.",Access="read",ExplicitDelegation=true,ReturnToSource=true};
            var message=NamedInstances.Submit(store,session,destination.Name,request,token).GetAwaiter().GetResult();
            Console.WriteLine(Json.Write(new{message.Id,sourceThread=thread,destination=destination.Name}));
            var engine=new RelayEngine(store);DateTime until=DateTime.UtcNow.AddSeconds(110);string previous=null;
            do{
                engine.Process(message.Id,token).GetAwaiter().GetResult();message=store.Message(message.Id);
                string status=message.State+"/"+message.ReturnState+"/"+message.BlockReason;
                if(status!=previous){Console.WriteLine(Json.Write(new{message.State,message.ReturnState,message.BlockReason,message.Error,message.TargetThreadId}));previous=status;}
                if(message.ReturnState=="delivered"||message.BlockReason=="permissions"||message.State=="uncertain"||message.State=="failed")break;
                Thread.Sleep(1500);
            }while(DateTime.UtcNow<until);
            bool passed=message.State=="completed"&&message.ReturnState=="delivered"&&(message.Result??"").Contains("SWITCHER_NAMED_OK");
            Console.WriteLine(passed?"PASS named delegation between distinct accounts and native return delivered":"INCOMPLETE bounded acceptance; inspect the saved job, never resend blindly");
            return passed?0:2;
        }catch(Exception e){Console.WriteLine("FAIL "+Program.SafeError(e));return 1;}
    }
}
