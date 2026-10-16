using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class SimpleWorkflowTests
{
    private static void Assert(bool v,string m){if(!v)throw new Exception(m);}
    private static void Refused(Action a){try{a();}catch(InvalidOperationException){return;}throw new Exception("Expected refusal");}
    private sealed class Transport:IRelayTransport
    {
        public void Verify(RelayChannel c){}
        public void VerifySend(RelayChannel c,string thread){}
        public Task<object> Call(RelayChannel c,string tool,object args,CancellationToken token){return Task.FromResult(Json.Read<object>(Json.Write(new{thread=new{id="source-chat"}})));}
    }
    internal static void RunAll(Action<string,Action> check,string root,string auth,string other)
    {
        var a=new AccountService(Path.Combine(root,"simple-workflow"));var profile=a.Import(auth,"Alice");var instance=a.Instances.Create("Léa");a.Instances.BindAccount(instance,profile);
        var store=new RelayStore(Path.Combine(a.Vault.Root,"relay"));var workspace=Path.Combine(a.Vault.Root,"project");Directory.CreateDirectory(workspace);
        var source=new RelayChannel{Id="source",Home=a.Vault.Root,AccountKey="source-account",Workspace=workspace,Enabled=true};store.Register(source);
        var session=new RelaySession{Channel=source.Id,Home=source.Home,Account=source.AccountKey,Thread="source-chat"};
        Func<DesktopInstance,string,Task<RelayChannel>> connector=(i,w)=>{var channel=new RelayChannel{Id=NamedInstances.ChannelId(a.Instances.Home(i),w),Name=i.Name,AccountKey=i.AccountKey,Home=a.Instances.Home(i),Workspace=w,Enabled=true,AnchorThreadId="target-anchor"};store.Register(channel);return Task.FromResult(channel);};
        Func<RelayJobSpec> spec=()=>new RelayJobSpec{Id=Guid.NewGuid().ToString("N"),Title="Review",Prompt="Review this folder",Access="read",ExplicitDelegation=true,ReturnToSource=true};
        check("named destination matches case and stable id but refuses fuzzy names",delegate{Assert(NamedInstances.Resolve(a," LÉA ").Id==instance.Id&&NamedInstances.Resolve(a,instance.Id).Name=="Léa","name resolution");Refused(()=>NamedInstances.Resolve(a,"Le"));});
        check("ambiguous legacy names are never chosen arbitrarily",delegate{a.Data.Instances.Add(new DesktopInstance{Id=Guid.NewGuid().ToString("N"),Name="LÉA"});try{Refused(()=>NamedInstances.Resolve(a,"Léa"));}finally{a.Data.Instances.RemoveAt(a.Data.Instances.Count-1);}});
        check("named explicit mission needs no agent project or rule configuration",delegate{var m=NamedInstances.Submit(store,session,"Léa",spec(),CancellationToken.None,new Transport(),connector).GetAwaiter().GetResult();Assert(m.State=="queued"&&m.TargetAccountKey==profile.Key&&m.ReturnToSource&&m.Workspace==workspace,"incorrect named mission");});
        check("named retry stays idempotent even if destination is offline",delegate{var request=spec();var m=NamedInstances.Submit(store,session,instance.Id,request,CancellationToken.None,new Transport(),connector).GetAwaiter().GetResult();var again=NamedInstances.Submit(store,session,instance.Id,request,CancellationToken.None,new Transport(),(i,w)=>{throw new Exception("Must not reconnect");}).GetAwaiter().GetResult();Assert(m.Id==again.Id,"duplicated job");request.Prompt="different";Refused(()=>NamedInstances.Submit(store,session,instance.Id,request,CancellationToken.None,new Transport(),connector).GetAwaiter().GetResult());});
        check("named routing cannot invent automatic delegation authorization",delegate{var request=spec();request.ExplicitDelegation=false;Refused(()=>NamedInstances.Submit(store,session,"Léa",request,CancellationToken.None,new Transport(),connector).GetAwaiter().GetResult());});
        check("simple routing cannot bypass existing advanced project scopes",delegate{var policy=new RelayPolicy();policy.Projects.Add(new RelayProject{Id="protected",Name="Protected",Workspace=workspace});RelayPolicies.Save(store,policy);Refused(()=>NamedInstances.Submit(store,session,"Léa",spec(),CancellationToken.None,new Transport(),connector).GetAwaiter().GetResult());RelayPolicies.Save(store,new RelayPolicy());});
        check("simple routing cannot bypass a disabled advanced agent",delegate{var target=connector(instance,workspace).Result;var policy=new RelayPolicy();policy.Agents.Add(new RelayAgent{Channel=target.Id,Enabled=false});RelayPolicies.Save(store,policy);Refused(()=>NamedInstances.Submit(store,session,"Léa",spec(),CancellationToken.None,new Transport(),connector).GetAwaiter().GetResult());RelayPolicies.Save(store,new RelayPolicy());});
        check("manual account drift cannot be captured or silently rebound",delegate{string path=Path.Combine(a.Instances.Home(instance),"auth.json");File.WriteAllText(path,other);Refused(()=>a.Instances.Capture(instance));Refused(()=>NamedInstances.VerifyAccount(a,instance));Assert(instance.AccountKey==profile.Key&&File.ReadAllText(path)==other,"drift modified auth or identity");File.WriteAllText(path,auth);});
        check("legacy migration pins the connected account once and never rewrites auth",delegate{var old=a.Instances.Create("Legacy");old.AccountKey=AuthIdentity.Parse(other).Key;string path=Path.Combine(a.Instances.Home(old),"auth.json");File.WriteAllText(path,auth);a.Instances.AdoptAccounts();Assert(old.AccountLocked&&old.AccountKey==profile.Key&&File.ReadAllText(path)==auth,"legacy identity not adopted");File.WriteAllText(path,other);a.Instances.AdoptAccounts();Assert(old.AccountKey==profile.Key&&File.ReadAllText(path)==other,"migration repeated on permanent account");});
        check("permanent binding survives archive rename and vault reload",delegate{a.Instances.Rename(instance,"Studio");a.Instances.Archive(instance,true);Refused(()=>a.Instances.Forget(profile));var fresh=new AccountService(a.Vault.Root);Assert(fresh.Data.Instances.Single(i=>i.Id==instance.Id).AccountKey==profile.Key,"binding lost");a.Instances.Archive(instance,false);});
        check("maintenance default interval and opt out survive legacy settings",delegate{var settings=Json.Read<Settings>("{}");Assert(settings.MaintainIntegration&&settings.IntegrationCheckMinutes==5,"migration defaults");var now=DateTime.UtcNow;var state=new RelayIntegrationState{Updated=now.AddMinutes(-4).ToString("o")};Assert(!IntegrationMaintenance.Due(settings,state,now,false),"too early");state.Updated=now.AddMinutes(-5).ToString("o");Assert(IntegrationMaintenance.Due(settings,state,now,false),"missing periodic check");settings.MaintainIntegration=false;Assert(!IntegrationMaintenance.Due(settings,state,now,false)&&IntegrationMaintenance.Due(settings,state,now,true),"opt out or explicit repair ignored");});
        check("MCP exposes named delegation with explicit authorization and idempotency",delegate{var tools=RelayMcp.Tools().Select(t=>Json.Read<object>(Json.Write(t))).ToArray();var tool=tools.Single(t=>Json.Str(Json.Get(t,"name"))=="delegate_to_instance");var required=RelayEngine.Rows(Json.Get(Json.Get(tool,"inputSchema"),"required")).Select(Json.Str).ToArray();Assert(required.Contains("explicitDelegation")&&required.Contains("id")&&tools.Any(t=>Json.Str(Json.Get(t,"name"))=="list_instances"),"missing schema protection");});
    }
}
