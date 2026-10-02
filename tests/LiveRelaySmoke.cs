using System;
using System.IO;
using System.Linq;
using System.Threading;
using Creezio.Switcher;

// Explicit live acceptance harness. No account identities or resource IDs are built in.
internal static class LiveRelaySmoke
{
    public static int Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--instance-host")return InstanceHost.Run(args[1]);
        try{
            var input=Json.Read<object>(Console.In.ReadToEnd());string op=Json.Str(Json.Get(input,"operation"));
            var service=new AccountService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher"));
            var store=new RelayStore(RelayStore.DefaultRoot);var token=CancellationToken.None;
            if(op=="prepare"){
                var instance=service.Data.Instances.Single(i=>i.Id==Json.Str(Json.Get(input,"instance"))&&!i.IsLocal);
                string workspace=RelayStore.WorkspacePath(Json.Str(Json.Get(input,"workspace")));
                RelayIntegration.Install(store,service.Instances.Home(instance),service.Settings.CodexExecutable,true,token).GetAwaiter().GetResult();
                bool started=!service.Instances.Runtime.Probe(instance).Running;
                if(started)service.Instances.Start(instance,token).GetAwaiter().GetResult();
                var state=service.Instances.Runtime.Probe(instance);
                var owner=RelayCommand.Register(store,Json.Read<object>(Json.Write(new{channel="validation-owner",workspace=workspace,name="Validation owner",requireFullAccess=false})),token).GetAwaiter().GetResult();
                var old=store.Channel(Json.Str(Json.Get(input,"anchorChannel")));
                if(!RelayStore.SamePath(old.Home,service.Instances.Home(instance)))throw new InvalidOperationException("Anchor belongs to another instance");
                old.InstanceId=instance.Id;store.Register(old);RelayReconnect.Refresh(store,token).GetAwaiter().GetResult();old=store.Channel(old.Id);
                if(old.ServerPid!=state.DesktopPid)throw new InvalidOperationException("Reconnection was not verified");
                var policy=RelayPolicies.Load(store);
                policy.Projects.RemoveAll(p=>p.Id=="relay-validation");policy.Projects.Add(new RelayProject{Id="relay-validation",Name="Relay acceptance tests",Workspace=workspace,SourceChannels="validation-worker",TargetChannels="validation-owner",Delegation="explicit"});
                policy.Agents.RemoveAll(a=>a.Channel=="validation-owner"||a.Channel=="validation-worker");policy.Agents.Add(new RelayAgent{Channel="validation-owner",Description="Acceptance test recipient",Capabilities="review,resource-test",AutoRoute=true});policy.Agents.Add(new RelayAgent{Channel="validation-worker",Description="Acceptance test sender",Capabilities="review",AutoRoute=true});
                policy.Resources.RemoveAll(r=>r.Id=="validation-resource");string resource=Json.Str(Json.Get(input,"resource"));if(resource.Length>0)policy.Resources.Add(new RelayResource{Id="validation-resource",Project="relay-validation",Channels="validation-owner",Capabilities="resource-test",ExternalId=resource,Instructions=Json.Str(Json.Get(input,"resourceInstructions"))});
                RelayPolicies.Save(store,policy);
                Console.WriteLine(Json.Write(new{prepared=true,instance=instance.Id,startedForTest=started,pipe=state.AppToolsPipe,anchor=old.AnchorThreadId,ownerChannel=owner.Id}));
            }else if(op=="create-source"){
                var channel=store.Channel(Json.Str(Json.Get(input,"anchorChannel")));new DesktopRelayTransport().Verify(channel);
                using(var client=new AppToolsClient(channel.PipePath,channel.ServerPid,channel.ServerStartTicks)){
                    var result=client.Call(channel.AnchorThreadId,"create_thread",new{title="Relay acceptance · configurable delegation",prompt=Json.Str(Json.Get(input,"prompt")),target=new{type="projectless"}},token).GetAwaiter().GetResult();Console.WriteLine(Json.Write(result));
                }
            }else if(op=="read"){
                var channel=store.Channel(Json.Str(Json.Get(input,"channel")));var result=new DesktopRelayTransport().Call(channel,"read_thread",new{threadId=Json.Str(Json.Get(input,"thread")),turnLimit=6,includeOutputs=false,maxOutputCharsPerItem=5000},token).GetAwaiter().GetResult();Console.WriteLine(Json.Write(result));
            }else if(op=="permission-context"){
                var c=store.Channel(Json.Str(Json.Get(input,"channel")));Console.WriteLine(Json.Write(new{permissions=RelayPermissions.Read(c.Home,Json.Str(Json.Get(input,"thread")))}));
            }else if(op=="install-integration"){
                var c=store.Channel(Json.Str(Json.Get(input,"channel")));Console.WriteLine(Json.Write(RelayIntegration.Install(store,c.Home,service.Settings.CodexExecutable,true,token).GetAwaiter().GetResult()));
            }else if(op=="readiness-smoke"){
                if(System.Diagnostics.Process.GetProcessesByName("CodexAccountSwitcher").Any(p=>p.MainModule.FileVersionInfo.FileVersion.StartsWith("0.4")))throw new InvalidOperationException("Quit the legacy switcher before the live test.");
                string source=Json.Str(Json.Get(input,"source")),target=Json.Str(Json.Get(input,"target")),thread=Json.Str(Json.Get(input,"thread"));
                var spec=new RelayJobSpec{From=source,To=target,Project=Json.Str(Json.Get(input,"project")),Title="Relay v0.5 permission readiness",Prompt="Recette du transport et du controle de permissions uniquement. Sans outil, sans commande, sans fichier et sans nouvelle delegation : calcule 2 + 2 et reponds 4, puis une ligne CREEZIO_OUTCOME {\"status\":\"succeeded\"}.",Access="read",ExplicitDelegation=true,ReturnToSource=false};
                var policy=RelayPolicies.Load(store);var agent=policy.Agents.Single(a=>a.Channel==target);agent.Permission="full-access";agent.ReuseConversation=false;RelayPolicies.Save(store,policy);
                var message=new RelayRouter(store).Submit(spec,thread,token).GetAwaiter().GetResult();var engine=new RelayEngine(store);
                DateTime deadline=DateTime.UtcNow.AddSeconds(50);
                do{engine.Process(message.Id,token).GetAwaiter().GetResult();message=store.Message(message.Id);if(message.State=="completed"||message.BlockReason=="permissions"||message.State=="uncertain")break;Thread.Sleep(1000);}while(DateTime.UtcNow<deadline);
                Console.WriteLine(Json.Write(RelayCommand.MessageSummary(message,true)));
            }else if(op=="send"){
                var channel=store.Channel(Json.Str(Json.Get(input,"channel")));var result=new DesktopRelayTransport().Call(channel,"send_message_to_thread",new{threadId=Json.Str(Json.Get(input,"thread")),prompt=Json.Str(Json.Get(input,"prompt"))},token).GetAwaiter().GetResult();Console.WriteLine(Json.Write(result));
            }else if(op=="stop-managed"){
                var instance=service.Data.Instances.Single(i=>i.Id==Json.Str(Json.Get(input,"instance"))&&!i.IsLocal);service.Instances.Runtime.Stop(instance,token).GetAwaiter().GetResult();Console.WriteLine("{\"stopped\":true}");
            }else throw new InvalidOperationException("Unknown acceptance test operation");
            return 0;
        }catch(Exception e){Console.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
}
