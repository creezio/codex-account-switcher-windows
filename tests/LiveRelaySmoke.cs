using System;
using System.IO;
using System.Linq;
using System.Threading;
using Creezio.Switcher;

// Explicit live acceptance harness. No account identities or resource IDs are built in.
internal static class LiveRelaySmoke
{
    public sealed class Hold {public bool Active {get;set;} public bool Previous {get;set;} public string Revision {get;set;}}
    public static int Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--instance-host")return InstanceHost.Run(args[1]);
        try{
            var input=Json.Read<object>(Console.In.ReadToEnd());string op=Json.Str(Json.Get(input,"operation"));
            var service=new AccountService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher"));
            var store=new RelayStore(RelayStore.DefaultRoot);var token=CancellationToken.None;
            if(op=="hold-worker"){
                var policy=RelayPolicies.Load(store);var hold=store.ReadRecord<Hold>("acceptance-worker-hold.dpapi");if(!hold.Active){hold.Active=true;hold.Previous=policy.KeepWorkerRunning;policy.KeepWorkerRunning=true;RelayPolicies.Save(store,policy);hold.Revision=policy.Revision;store.WriteRecord("acceptance-worker-hold.dpapi",hold);}RelayWorker.Resume(store);Console.WriteLine("{\"workerHeldForAcceptance\":true}");
            }else if(op=="release-worker"){
                var policy=RelayPolicies.Load(store);var hold=store.ReadRecord<Hold>("acceptance-worker-hold.dpapi");if(hold.Active&&policy.Revision==hold.Revision){policy.KeepWorkerRunning=hold.Previous;RelayPolicies.Save(store,policy);hold.Active=false;store.WriteRecord("acceptance-worker-hold.dpapi",hold);Console.WriteLine("{\"restored\":true}");}else Console.WriteLine("{\"restored\":false,\"note\":\"Policy changed; preserved for review\"}");
            }else if(op=="status"){
                Console.WriteLine(Json.Write(new{version=RelayWorker.Version,worker=RelayWorker.Status(store),channels=store.Channels().Select(c=>new{c.Id,c.Email,c.InstanceId,c.AnchorThreadId,online=DesktopRuntime.SameProcess(c.ServerPid,c.ServerStartTicks)}),active=store.ActiveMessages().Select(m=>new{m.Id,m.SchemaVersion,m.State,m.ReturnState,m.BlockReason,m.Title}),instances=service.Data.Instances.Select(i=>new{i.Id,i.Name,i.IsLocal})}));
            }else if(op=="process-job"){
                string id=Json.Str(Json.Get(input,"id"));new RelayEngine(store).Process(id,token).GetAwaiter().GetResult();Console.WriteLine(Json.Write(RelayCommand.MessageSummary(store.Message(id),true)));
            }else if(op=="prepare"){
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
                    var result=client.Call(channel.AnchorThreadId,"create_thread",new{title="Relay acceptance · v0.6",prompt=Json.Str(Json.Get(input,"prompt")),target=new{type="projectless"}},token).GetAwaiter().GetResult();Console.WriteLine(Json.Write(result));
                }
            }else if(op=="read"){
                var channel=store.Channel(Json.Str(Json.Get(input,"channel")));var result=new DesktopRelayTransport().Call(channel,"read_thread",new{threadId=Json.Str(Json.Get(input,"thread")),turnLimit=6,includeOutputs=false,maxOutputCharsPerItem=5000},token).GetAwaiter().GetResult();
                // Native snapshots can include MCP arguments even with includeOutputs=false.
                Console.WriteLine(Json.Write(new{thread=Json.Get(result,"thread"),turns=RelayEngine.Rows(Json.Get(result,"turns")).Select(t=>new{id=Json.Str(Json.Get(t,"id")),status=Json.Str(Json.Get(t,"status")),messages=RelayEngine.Rows(Json.Get(t,"items")).Where(i=>Json.Str(Json.Get(i,"type"))=="agentMessage").Select(i=>new{phase=Json.Str(Json.Get(i,"phase")),text=Json.Str(Json.Get(i,"text"))})})}));
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
