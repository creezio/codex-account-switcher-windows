using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Explicit live acceptance helper, separate from the unit runner and product.
// Uses an existing compiled engine through reflection; never builds or changes it.
internal static class HeadlessAcceptance
{
    private static string trackingFile;
    private static readonly BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static readonly JavaScriptSerializer Serializer=new JavaScriptSerializer{MaxJsonLength=33554432};
    private static Assembly engine;
    private static string output;
    private static readonly Dictionary<string,object> receipt=new Dictionary<string,object>();
    private static Type Type(string name){return engine.GetType("Creezio.Switcher."+name,true);}
    private static object Get(object value,string member)
    {
        if(value==null)return null;var field=value.GetType().GetField(member,Any);if(field!=null)return field.GetValue(value);
        var property=value.GetType().GetProperty(member,Any);return property==null?null:property.GetValue(value,null);
    }
    private static object J(object value,string member){var map=value as IDictionary<string,object>;object found;return map!=null&&map.TryGetValue(member,out found)?found:null;}
    private static string Str(object value){return value as string??"";}
    private static IEnumerable<object> Rows(object value){return value is IEnumerable&&!(value is string)&&!(value is IDictionary)?((IEnumerable)value).Cast<object>():Enumerable.Empty<object>();}
    private static object New(string name,params object[] args){return Activator.CreateInstance(Type(name),Any,null,args,null);}
    private static object Call(object value,string name,params object[] args)
    {
        var method=value.GetType().GetMethods(Any).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length);return method.Invoke(value,args);
    }
    private static object Static(string type,string name,params object[] args){return Type(type).GetMethods(Any).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(null,args);}
    private static object Await(object value){var task=(Task)value;task.GetAwaiter().GetResult();var property=task.GetType().GetProperty("Result");return property==null?null:property.GetValue(task,null);}
    private static void Set(object value,string member,object data){var field=value.GetType().GetField(member,Any);if(field!=null)field.SetValue(value,data);else value.GetType().GetProperty(member,Any).SetValue(value,data,null);}
    private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    private static string TextHash(string value){return Hash(Encoding.UTF8.GetBytes(value));}
    private static void Save(string stage)
    {
        receipt["Stage"]=stage;receipt["UpdatedUtc"]=DateTime.UtcNow.ToString("o");File.WriteAllText(output,Serializer.Serialize(receipt),new UTF8Encoding(false));
    }
    private static object Identity(string home){return Static("AuthIdentity","Parse",File.ReadAllText(Path.Combine(home,"auth.json")));}
    private static object Scope(string id,string name,object state)
    {
        var scope=New("ResourceScope");Set(scope,"Id",id);Set(scope,"Name",name);Set(scope,"State",state);return scope;
    }
    private static Array Scopes(object accounts)
    {
        var instances=Get(accounts,"Instances");var runtime=Get(instances,"Runtime");var scopes=new List<object>();
        foreach(var instance in Rows(Get(Get(accounts,"Data"),"Instances"))){
            if(Object.Equals(Get(instance,"Archived"),true))continue;
            var state=Call(runtime,"Probe",instance);scopes.Add(Scope(Str(Get(instance,"Id")),Str(Get(instance,"Name")),state));
        }
        using(var own=Process.GetCurrentProcess()){
            var state=New("InstanceState");Set(state,"Running",true);Set(state,"HostPid",own.Id);Set(state,"HostStartTicks",own.StartTime.ToUniversalTime().Ticks);
            scopes.Add(Scope("acceptance-helper","Acceptance helper including its owned children",state));
        }
        var result=Array.CreateInstance(Type("ResourceScope"),scopes.Count);for(int i=0;i<scopes.Count;i++)result.SetValue(scopes[i],i);return result;
    }
    private static object Capture(object meter,Array scopes){return Call(meter,"Capture",scopes);}
    private static object Summary(object snapshot)
    {
        return new{CapturedUtc=Get(snapshot,"Captured"),TotalPhysical=Get(snapshot,"TotalPhysical"),AvailablePhysical=Get(snapshot,"AvailablePhysical"),Groups=Rows(Get(snapshot,"Groups")).Select(g=>new{
            Instance=Get(Get(g,"Scope"),"Name"),Id=Get(Get(g,"Scope"),"Id"),ResidentBytes=Get(g,"Resident"),PrivateBytes=Get(g,"PrivateBytes"),CpuPercentOfWholePc=Get(g,"Cpu"),Partial=Get(g,"Partial"),CpuPartial=Get(g,"CpuPartial"),
            Processes=Rows(Get(g,"Processes")).Select(p=>new{Pid=Get(p,"Pid"),Parent=Get(p,"Parent"),StartedUtcTicks=Get(p,"Started"),Name=Get(p,"Name"),ResidentBytes=Get(p,"Resident"),PrivateBytes=Get(p,"PrivateBytes"),CpuPercentOfWholePc=Get(p,"CpuPercent")}).ToArray()
        }).ToArray()};
    }
    private static object[] ProtectedRoots(object accounts)
    {
        var manager=Get(accounts,"Instances");var runtime=Get(manager,"Runtime");var roots=new List<object>();
        foreach(var instance in Rows(Get(Get(accounts,"Data"),"Instances"))){
            var state=Call(runtime,"Probe",instance);if(!Object.Equals(Get(state,"Running"),true))continue;
            foreach(string kind in new[]{"Desktop","Host"}){int pid=Convert.ToInt32(Get(state,kind+"Pid"));long started=Convert.ToInt64(Get(state,kind+"StartTicks"));if(pid>0&&started>0)roots.Add(new{Pid=pid,StartedUtcTicks=started,Instance=Get(instance,"Name"),Kind=kind});}
        }
        return roots.ToArray();
    }
    private static bool Alive(int pid,long started){try{using(var p=Process.GetProcessById(pid))return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==started;}catch(ArgumentException){return false;}}
    private static void VerifyRoots(object[] roots)
    {
        var missing=roots.Where(p=>!Alive((int)Get(p,"Pid"),(long)Get(p,"StartedUtcTicks"))).Select(p=>Get(p,"Pid")).ToArray();receipt["ProtectedGuiRootsPreserved"]=missing.Length==0;receipt["MissingProtectedRootPids"]=missing;
        if(missing.Length>0)throw new InvalidOperationException("A protected GUI root changed during acceptance.");
    }
    private static object Payload(object result)
    {
        if(Object.Equals(J(result,"isError"),true))throw new InvalidOperationException("Provider returned an explicit error.");
        var structured=J(result,"structuredContent");if(structured!=null)return structured;
        foreach(var part in Rows(J(result,"content")))if(Str(J(part,"type"))=="text")try{return Serializer.DeserializeObject(Str(J(part,"text")));}catch(ArgumentException){}
        throw new InvalidOperationException("Provider response has no structured metadata.");
    }
    private static object Normalized(object value){return Serializer.DeserializeObject(Str(Static("Json","Write",value)));}
    private static void Local(object accounts,object target,string home,string key,string work,object previous,string mode)
    {
        bool nameOnly=mode=="name-check",inspectOnly=mode=="inspect-local"||mode=="inspect-bind",unicode=mode=="local-utf8",bindOnly=mode=="local-bind"||mode=="local-bind-fixed";
        receipt["FixtureKind"]=bindOnly?"mcp-read-only":unicode?"utf8-file":"file-or-diagnostic";
        Save("refreshing-closed-profile");
        DateTime previousAt;bool reuseRefresh=Object.Equals(J(previous,"ForcedRefreshSucceeded"),true)&&Str(J(previous,"AccountKeyHash"))==TextHash(key)&&DateTime.TryParse(Str(J(previous,"UpdatedUtc")),out previousAt)&&DateTime.UtcNow-previousAt.ToUniversalTime()<TimeSpan.FromMinutes(10);
        var identity=reuseRefresh?Identity(home):Static("HeadlessAccounts","Read",accounts,target,true,CancellationToken.None);receipt["ReusedVerifiedAcceptanceRefresh"]=reuseRefresh;
        if(Str(Get(identity,"Key"))!=key)throw new InvalidOperationException("Refresh changed the permanent account.");
        receipt["ForcedRefreshSucceeded"]=true;receipt["AccountAfterRefreshMatches"]=true;Save("refreshed-closed-profile");
        string workspace=Path.Combine(work,"headless-local-lab"),file=Path.Combine(workspace,unicode?"résultat-sans-fenêtre.txt":"headless-result.txt"),marker=unicode?"HEADLESS_LOCAL_OK_ÉTÉ_20261015":"HEADLESS_LOCAL_OK_20261015",finalMarker=unicode?"HEADLESS_LOCAL_DONE_ÉTÉ":"HEADLESS_LOCAL_DONE";Directory.CreateDirectory(workspace);Static("SafeFiles","RejectLinks",workspace);Static("SafeFiles","RejectLinks",file);
        // Always use the existing application's registry; only this process's owned runtimes
        // are shut down below. No imported or user-created conversation is resumed.
        string vault=Str(Get(Get(accounts,"Vault"),"Root"));var store=New("RelayStore",Path.Combine(vault,"relay"));
        try{
            if(bindOnly){Save("installing-test-profile-integration");var installed=Await(Static("RelayIntegration","Install",store,home,Str(Get(Get(accounts,"Settings"),"CodexExecutable")),true,CancellationToken.None));receipt["IntegrationHealthy"]=Get(installed,"Healthy");receipt["IntegrationVersion"]=Get(installed,"InstalledVersion");string version=Str(Type("RelayWorker").GetField("Version",Any).GetValue(null));if(!Object.Equals(Get(installed,"Healthy"),true)||!Str(Get(installed,"InstalledVersion")).StartsWith(version+"+codex.",StringComparison.Ordinal))throw new InvalidOperationException("Current integration was not verified before the fixture prompt.");}
            Save("connecting-local-channel");
            var channel=Await(Static("NamedInstances","Connect",store,accounts,target,workspace,CancellationToken.None,null,true));
            if(Str(Get(channel,"Provider"))!="codex-headless")throw new InvalidOperationException("The closed owner was not routed to the headless provider.");
            receipt["NativeSandbox"]=Get(channel,"HeadlessSandbox");receipt["NativeApprovalPolicy"]=Get(channel,"HeadlessApprovalPolicy");receipt["LocalWorkspace"]=workspace;receipt["LocalChannel"]=Get(channel,"Id");
            var transport=Static("AgentProviders","Create",store);string thread=Str(J(previous,"LocalThreadId")),turn=Str(J(previous,"LocalTurnId"));
            if(thread.Length>0)receipt["LocalThreadId"]=thread;if(turn.Length>0)receipt["LocalTurnId"]=turn;
            if(thread.Length==0&&Object.Equals(J(previous,"LocalMutationMayHaveBeenSent"),true)){
                DateTime since;if(!DateTime.TryParse(Str(J(previous,"StartedUtc")),out since))throw new InvalidOperationException("The earlier acceptance time cannot be proved.");
                var readRecord=store.GetType().GetMethod("ReadRecord",Any).MakeGenericMethod(Type("HeadlessThreadReceipt"));
                var candidates=Directory.GetFiles(Str(Get(store,"Root")),"headless-thread-*.dpapi").Where(path=>File.GetLastWriteTimeUtc(path)>=since.ToUniversalTime().AddSeconds(-1)).Select(path=>readRecord.Invoke(store,new object[]{Path.GetFileName(path)})).Where(value=>Str(Get(value,"Title"))=="Recette sans fenêtre 0.18 - données fictives"&&Str(Get(value,"AccountKey"))==key&&Str(Get(value,"Home"))==home&&Str(Get(value,"Workspace"))==workspace).ToArray();
                if(candidates.Length!=1)throw new InvalidOperationException("An earlier submission is uncertain and its owned receipt is not unique.");
                var owned=candidates[0];thread=Str(Get(owned,"ThreadId"));turn=Str(Get(owned,"TurnId"));
                if(!inspectOnly&&(Str(Get(owned,"State"))!="created"||turn.Length>0||Str(Get(owned,"ClientUserMessageId")).Length>0||Str(Get(owned,"PromptHash")).Length>0))throw new InvalidOperationException("The prior submission may contain a model instruction. Never resend it.");
                receipt["RecoveredOwnedUnsentThread"]=true;receipt["OriginalOwnedState"]="created";receipt["LocalThreadId"]=thread;receipt["NoModelTurnStarted"]=true;Save("recovered-empty-owned-thread");
            }
            if(inspectOnly){
                if(thread.Length==0)throw new InvalidOperationException("Read-only inspection requires an existing exact owned chat.");Static("HeadlessRelayTransport","RequireOwned",store,channel,thread);receipt["NoModelTurnStarted"]=null;receipt["ReadOnlyInspection"]=true;Save("inspecting-existing-uncertain-fixture");
                object rpc=New("HeadlessRpc",Str(Get(Get(accounts,"Settings"),"CodexExecutable")),home,key);
                try{var read=Normalized(Await(Call(rpc,"Call","thread/read",new{threadId=thread,includeTurns=true},CancellationToken.None)));if(Str(J(J(read,"thread"),"id"))!=thread)throw new InvalidOperationException("The inspected chat differs from its owned receipt.");var turns=Rows(J(J(read,"thread"),"turns")).ToArray();receipt["HistoricalTurnCount"]=turns.Length;receipt["NoModelTurnStarted"]=turns.Length==0;receipt["HistoricalTurnStates"]=turns.Select(value=>new{Id=J(value,"id"),Status=J(value,"status")}).ToArray();
                    var failedBind=turns.SelectMany(value=>Rows(J(value,"items"))).Where(item=>Str(J(item,"type"))=="mcpToolCall"&&Str(J(item,"tool"))=="bind_headless_chat"&&Str(J(item,"status"))=="failed").ToArray();receipt["BindFailureSummaries"]=failedBind.Select(item=>System.Text.RegularExpressions.Regex.Replace(Str(Static("HeadlessApprovals","SafeSummary",String.Join("\n",Rows(J(J(item,"result"),"content")).Where(part=>Str(J(part,"type"))=="text").Select(part=>Str(J(part,"text")))))),"\\b[a-fA-F0-9]{64}\\b","[session masquée]")).ToArray();return;}finally{((IDisposable)rpc).Dispose();}
            }
            if(nameOnly){
                if(thread.Length==0||turn.Length>0)throw new InvalidOperationException("Naming diagnostics require only the exact existing empty owned chat.");
                var owned=Static("HeadlessRelayTransport","RequireOwned",store,channel,thread);
                if(Str(Get(owned,"State"))!="created"||Str(Get(owned,"ClientUserMessageId")).Length>0||Str(Get(owned,"PromptHash")).Length>0)throw new InvalidOperationException("The naming diagnostic refuses a chat with a submitted instruction.");
                object rpc=New("HeadlessRpc",Str(Get(Get(accounts,"Settings"),"CodexExecutable")),home,key);
                try{Save("naming-existing-empty-fixture");Await(Call(rpc,"Call","thread/name/set",new{threadId=thread,name=Str(Get(owned,"Title"))},CancellationToken.None));receipt["NativeNameSetSucceeded"]=true;receipt["NoModelTurnStarted"]=true;return;}finally{((IDisposable)rpc).Dispose();}
            }
            string prompt="This explicitly authorized acceptance task is limited to the current fixture workspace. Use apply_patch to create "+Path.GetFileName(file)+" containing exactly "+marker+" and an optional final newline. Do not change any other file, use the network, install anything, delegate, commit, push or create a PR. Then reply exactly "+finalMarker+". The file is fictitious and belongs only to this acceptance test.";
            if(bindOnly){finalMarker="BIND_HEADLESS_OK français日本語✅";prompt="This explicitly authorized READ ONLY acceptance task tests this actual headless chat's own relay integration. Use only the available creezio_relay MCP tools. First call bind_headless_chat with the exact threadId given in the CREEZIO_HEADLESS_CHAT context appended to this mission. Keep the returned session token private. Then call list_instances with that returned session. Do not print the session, account details or raw tool results. Do not use a shell, read or change files, use other network tools, delegate, commit, push, install anything or create a PR. Only after both MCP calls succeed, reply with exactly: "+finalMarker;}
            if(thread.Length==0){
                if(!bindOnly&&File.Exists(file))throw new InvalidOperationException("The fixture output already exists without a verified owned thread.");
                receipt["NoModelTurnStarted"]=false;receipt["LocalMutationMayHaveBeenSent"]=true;Save("sending-local-fixture");
                var started=Normalized(Await(Call(transport,"Call",channel,"create_thread",new{title="Recette sans fenêtre 0.18 - données fictives",prompt=prompt},CancellationToken.None)));
                thread=Str(J(started,"threadId"));turn=Str(J(started,"turnId"));if(thread.Length==0||turn.Length==0)throw new InvalidOperationException("The fixture submission has no confirmed native IDs.");
            }else if(turn.Length==0){
                var owned=Static("HeadlessRelayTransport","RequireOwned",store,channel,thread);
                if(Str(Get(owned,"State"))!="created"||Str(Get(owned,"ClientUserMessageId")).Length>0||Str(Get(owned,"PromptHash")).Length>0)throw new InvalidOperationException("A previously submitted instruction cannot be sent again.");
                receipt["NoModelTurnStarted"]=false;receipt["LocalMutationMayHaveBeenSent"]=true;Save("sending-first-turn-existing-fixture");
                var started=Normalized(Await(Call(transport,"Call",channel,"send_message_to_thread",new{threadId=thread,prompt=prompt},CancellationToken.None)));turn=Str(J(started,"turnId"));if(Str(J(started,"threadId"))!=thread||turn.Length==0)throw new InvalidOperationException("The first turn was not confirmed for the existing fixture chat.");
            }
            receipt["LocalThreadId"]=thread;receipt["LocalTurnId"]=turn;Save("following-local-fixture");
            DateTime deadline=DateTime.UtcNow.AddMinutes(4);
            while(true){
                var read=Normalized(Await(Call(transport,"Call",channel,"read_thread",new{threadId=thread},CancellationToken.None)));
                if(Str(J(J(read,"thread"),"id"))!=thread)throw new InvalidOperationException("Local read returned a different chat.");
                var native=Rows(J(read,"turns")).SingleOrDefault(value=>Str(J(value,"id"))==turn);string status=Str(J(native,"status"));receipt["LocalTurnStatus"]=status;
                var mcp=Rows(J(native,"items")).Where(item=>Str(J(item,"type"))=="mcpToolCall").ToArray();receipt["NativeMcpCalls"]=mcp.Select(item=>new{Server=Str(J(item,"server")),Tool=Str(J(item,"tool")),Status=Str(J(item,"status")),HasError=J(item,"error")!=null,ResultIsError=Object.Equals(J(J(item,"result"),"isError"),true)}).ToArray();
                if(bindOnly&&Rows(J(native,"items")).Any(item=>new[]{"commandExecution","fileChange"}.Contains(Str(J(item,"type")))))throw new InvalidOperationException("The read-only MCP fixture attempted a shell or file change.");
                var changes=Rows(J(native,"items")).Where(item=>Str(J(item,"type"))=="fileChange").SelectMany(item=>Rows(J(item,"changes")).Select(change=>new{ItemId=Str(J(item,"id")),Path=Str(J(change,"path")),Kind=Str(J(J(change,"kind"),"type"))})).ToArray();
                if(changes.Any(change=>change.Path.Length>2048||change.Path.Any(Char.IsControl)))throw new InvalidOperationException("The native file-change path is invalid.");receipt["NativeFileChanges"]=changes;
                if(changes.Length>0){bool scope=changes.All(change=>String.Equals(Path.GetFullPath(Path.IsPathRooted(change.Path)?change.Path:Path.Combine(workspace,change.Path)),file,StringComparison.OrdinalIgnoreCase));receipt["NativeFileChangesWithinFixture"]=scope;if(!scope)throw new InvalidOperationException("The native turn requested changes outside the one authorized fixture file.");}
                var pending=Rows(Call(New("HeadlessApprovals",store,null),"Pending")).Where(value=>Str(Get(value,"ThreadId"))==thread).ToArray();receipt["PendingApprovals"]=pending.Select(value=>new{Id=Get(value,"Id"),Method=Get(value,"Method"),Summary=Get(value,"Summary")}).ToArray();Save("following-local-fixture");
                if(status=="completed"){
                    string final=String.Join("\n",Rows(J(native,"items")).Where(item=>Str(J(item,"type"))=="agentMessage"&&new[]{"final","final_answer"}.Contains(Str(J(item,"phase")))).Select(item=>Str(J(item,"text"))));
                    Static("SafeFiles","RejectLinks",file);bool content=bindOnly||File.Exists(file)&&new FileInfo(file).Length<1024&&File.ReadAllText(file).TrimEnd('\r','\n')==marker;
                    if(bindOnly){var bound=mcp.LastOrDefault(item=>Str(J(item,"tool")).EndsWith("bind_headless_chat",StringComparison.Ordinal)&&Str(J(item,"status"))=="completed"&&J(item,"error")==null);var listed=mcp.LastOrDefault(item=>Str(J(item,"tool")).EndsWith("list_instances",StringComparison.Ordinal)&&Str(J(item,"status"))=="completed"&&J(item,"error")==null);if(bound==null||listed==null)throw new InvalidOperationException("Both required native MCP calls were not confirmed.");var session=Payload(J(bound,"result"));receipt["BindAccountMatches"]=Str(J(session,"Account"))==key;receipt["BindThreadMatches"]=Str(J(session,"Thread"))==thread;receipt["ListUsedBoundSession"]=Str(J(session,"Token")).Length==64&&Str(J(J(listed,"arguments"),"session"))==Str(J(session,"Token"));if(!Object.Equals(receipt["BindAccountMatches"],true)||!Object.Equals(receipt["BindThreadMatches"],true)||!Object.Equals(receipt["ListUsedBoundSession"],true))throw new InvalidOperationException("MCP did not prove the exact account, chat and bound session.");}
                    receipt["LocalFileVerified"]=bindOnly?(object)null:content;receipt["LocalFinalVerified"]=final.Trim()==finalMarker;
                    var owned=Static("HeadlessRelayTransport","RequireOwned",store,channel,thread);receipt["OwnedReceiptAccountMatches"]=Str(Get(owned,"AccountKey"))==key;
                    if(!content||!Object.Equals(receipt["LocalFinalVerified"],true))throw new InvalidOperationException("The native fixture result or file did not match.");return;
                }
                if(status=="failed"||status=="interrupted")throw new InvalidOperationException("The fixture's native turn did not complete.");
                if(DateTime.UtcNow>=deadline)throw new TimeoutException("The owned fixture did not finish within its acceptance deadline.");
                Thread.Sleep(1000);
            }
        }finally{Static("HeadlessRelayTransport","Shutdown",store);}
    }
    public static int Main(string[] args)
    {int status=Run(args);return receipt.ContainsKey("State")&&Object.Equals(receipt["State"],"passed")?status:1;}
    private static int Run(string[] args)
    {
        object tunnel=null;object[] protectedRoots=null;var samples=new List<object>();var sampleLock=new object();Timer sampler=null;
        try{
            if(args.Length!=4&&args.Length!=5)throw new ArgumentException("engine, target, receipt, work root and optional local phase are required");
            string work=Path.GetFullPath(args[3]).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;output=Path.GetFullPath(args[2]);
            if(!output.StartsWith(work,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Acceptance receipt must remain within work.");
            object previous=File.Exists(output)?Serializer.DeserializeObject(File.ReadAllText(output)):null;
            engine=Assembly.LoadFrom(Path.GetFullPath(args[0]));Static("SafeFiles","RejectLinks",output);
            string vault=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher");
            var accounts=New("AccountService",vault,null);var manager=Get(accounts,"Instances");var target=Rows(Get(Get(accounts,"Data"),"Instances")).Single(i=>!Object.Equals(Get(i,"Archived"),true)&&String.Equals(Str(Get(i,"Name")),args[1],StringComparison.OrdinalIgnoreCase));
            if(Object.Equals(Get(target,"IsLocal"),true))throw new InvalidOperationException("Only a previously closed managed profile may be tested.");
            var runtime=Get(manager,"Runtime");if(Object.Equals(Get(Call(runtime,"Probe",target),"Running"),true))throw new InvalidOperationException("Target already has a live desktop; acceptance never closes it.");
            string home=Str(Call(manager,"Home",target)),key=Str(Get(target,"AccountKey")),executable=Str(Get(Get(accounts,"Settings"),"CodexExecutable"));
            var identity=Identity(home);if(key.Length==0||Str(Get(identity,"Key"))!=key)throw new InvalidOperationException("Permanent target identity differs from the profile credentials.");
            var scopes=Scopes(accounts);var meter=New("ProcessResources");protectedRoots=ProtectedRoots(accounts);receipt["ProtectedGuiRoots"]=protectedRoots;receipt["Target"]=args[1];receipt["TargetInstanceId"]=Get(target,"Id");receipt["AccountKeyHash"]=TextHash(key);receipt["StartedUtc"]=DateTime.UtcNow.ToString("o");receipt["TargetDesktopInitiallyClosed"]=true;receipt["NoModelTurnStarted"]=true;
            byte[] authBefore=File.ReadAllBytes(Path.Combine(home,"auth.json"));receipt["AuthHashBefore"]=Hash(authBefore);Capture(meter,scopes);Thread.Sleep(1000);receipt["GuiBaseline"]=Summary(Capture(meter,scopes));Save("baseline-captured");
            sampler=new Timer(o=>{try{lock(sampleLock)samples.Add(Capture(meter,scopes));}catch{}},null,0,250);
            if(args.Length==5&&new[]{"local","name-check","inspect-local","inspect-bind","local-utf8","local-bind","local-bind-fixed"}.Contains(args[4])){
                Local(accounts,target,home,key,work,previous,args[4]);receipt["AuthHashAfter"]=Hash(File.ReadAllBytes(Path.Combine(home,"auth.json")));receipt["AuthBytesUnchanged"]=Object.Equals(receipt["AuthHashBefore"],receipt["AuthHashAfter"]);
                if(Str(Get(Identity(home),"Key"))!=key||Object.Equals(Get(Call(runtime,"Probe",target),"Running"),true))throw new InvalidOperationException("The fixture owner account or GUI changed.");
                receipt["TargetDesktopFinallyClosed"]=true;receipt["State"]="passed";return 0;
            }
            if(args.Length==5&&args[4]=="inventory-production"){
                var store=New("RelayStore",Path.Combine(vault,"relay"));var service=New("ToolTunnel",store,null,null,null);var owner=Call(service,"Owner",Str(Get(target,"Id")));receipt["ProductionOwnerValidated"]=Str(Get(owner,"Account"))==key&&String.Equals(Path.GetFullPath(Str(Get(owner,"Home"))),Path.GetFullPath(home),StringComparison.OrdinalIgnoreCase);if(!Object.Equals(receipt["ProductionOwnerValidated"],true))throw new InvalidOperationException("The production factory resolved a different owner.");tunnel=Call(service,"Connection",owner);var rpc=Get(tunnel,"client");trackingFile=Path.Combine(Str(Get(store,"Root")),"headless-tools-"+Get(rpc,"ProcessId")+"-"+Get(rpc,"ProcessStartedTicks")+".dpapi");receipt["ProductionTrackingRecordPresentDuring"]=File.Exists(trackingFile);if(!File.Exists(trackingFile))throw new InvalidOperationException("The production tools helper was not attributed to its instance.");
            }else{bool boundConstructor=Type("ToolTunnelRpc").GetConstructors(Any).Any(c=>c.GetParameters().Length==4);tunnel=boundConstructor?New("ToolTunnelRpc",executable,home,key,null):New("ToolTunnelRpc",executable,home);}
            var token=CancellationToken.None;
            Await(Call(tunnel,"Ready",token));var server=Await(Call(tunnel,"Rpc","account/read",new{refreshToken=false},token));var native=J(server,"account");
            var nativeFields=native as IDictionary<string,object>;receipt["NativeAccountFields"]=nativeFields==null?new string[0]:nativeFields.Keys.OrderBy(v=>v).ToArray();
            bool email=String.Equals(Str(J(native,"email")),Str(Get(identity,"Email")),StringComparison.OrdinalIgnoreCase);receipt["ServerEmailMatchesProfile"]=email;receipt["NativeAccountType"]=J(native,"type");
            if(!email||Str(J(native,"type"))!="chatgpt")throw new InvalidOperationException("Native account read did not confirm the expected ChatGPT profile.");
            string nativeAccountId=Str(J(native,"id"));if(nativeAccountId.Length==0)nativeAccountId=Str(J(native,"accountId"));
            receipt["ServerAccountIdProvided"]=nativeAccountId.Length>0;receipt["ServerAccountIdMatchesProfile"]=nativeAccountId.Length==0?(object)null:nativeAccountId==Str(Get(identity,"AccountId"));
            if(nativeAccountId.Length>0&&nativeAccountId!=Str(Get(identity,"AccountId")))throw new InvalidOperationException("Native account workspace differs from the expected account.");
            Save("identity-confirmed");var tools=Rows(Await(Call(tunnel,"Inventory",token))).ToArray();receipt["ToolCount"]=tools.Length;receipt["PluginGroups"]=tools.GroupBy(t=>Str(Get(t,"Group"))).Select(g=>new{Name=g.Key,Tools=g.Count(),ReadOnly=g.Count(t=>Object.Equals(Get(t,"ReadOnly"),true))}).ToArray();
            var installed=Await(Call(tunnel,"Rpc","app/installed",new{threadId=Get(tunnel,"thread"),forceRefresh=false},token));receipt["InstalledApps"]=Rows(J(installed,"apps")).Select(a=>new{IdHash=TextHash(Str(J(a,"id"))),Name=J(a,"name"),Enabled=J(a,"enabled"),Callable=J(a,"callable")}).ToArray();
            receipt["InventoryContractHash"]=TextHash(Str(Static("Json","Write",(object)tools)));Save("inventory-completed");
            var list=tools.FirstOrDefault(t=>Str(Get(t,"Server"))=="codex_apps"&&Str(Get(t,"Name"))=="chatgpt_space.list_pages"&&Object.Equals(Get(t,"ReadOnly"),true));
            if(list!=null){
                var schema=Get(list,"Schema");receipt["ReadArgumentFields"]=((IDictionary<string,object>)J(schema,"properties")).Keys.OrderBy(v=>v).ToArray();receipt["ReadRequiredArguments"]=Rows(J(schema,"required")).Select(Str).ToArray();Save("representative-read");
                var result=Await(Call(tunnel,"Invoke","codex_apps","chatgpt_space.list_pages",new Dictionary<string,object>{{"limit",1}},token));var data=Payload(result);
                receipt["RepresentativeRead"]=new{Tool="chatgpt_space.list_pages",Succeeded=true,ItemCount=Rows(J(data,"items")).Count(),ResultHash=TextHash(Serializer.Serialize(data)),MetadataOnly=true};
            }else receipt["RepresentativeRead"]=new{Succeeded=false,Reason="No supported read-only Pages catalogue advertised"};
            Thread.Sleep(1500);Save("idle-sampled");
            if(Str(Get(Identity(home),"Key"))!=key)throw new InvalidOperationException("Profile identity changed while its tools were queried.");
            receipt["AuthHashAfter"]=Hash(File.ReadAllBytes(Path.Combine(home,"auth.json")));receipt["AuthBytesUnchanged"]=Object.Equals(receipt["AuthHashBefore"],receipt["AuthHashAfter"]);
            if(Object.Equals(Get(Call(runtime,"Probe",target),"Running"),true))throw new InvalidOperationException("The owner Desktop appeared during acceptance.");
            receipt["TargetDesktopFinallyClosed"]=true;receipt["State"]="passed";return 0;
        }catch(Exception error){var cause=error.GetBaseException();receipt["FailureStage"]=receipt.ContainsKey("Stage")?receipt["Stage"]:null;receipt["State"]="failed";receipt["FailureType"]=cause.GetType().Name;receipt["FailureHResult"]=cause.HResult.ToString("X8");
            if(cause.TargetSite!=null)receipt["FailureMember"]=cause.TargetSite.DeclaringType.FullName+"."+cause.TargetSite.Name;
            receipt["FailureMethodChain"]=new StackTrace(cause,false).GetFrames().Select(frame=>frame.GetMethod()).Where(method=>method!=null).Take(14).Select(method=>method.DeclaringType.FullName+"."+method.Name).ToArray();
            if(cause is System.ComponentModel.Win32Exception)receipt["FailureNativeErrorCode"]=((System.ComponentModel.Win32Exception)cause).NativeErrorCode;
            string code=Str(Get(cause,"Code"));if(System.Text.RegularExpressions.Regex.IsMatch(code,"^[a-z][a-z0-9_.-]{0,63}$"))receipt["FailureCode"]=code;
            string rpcMethod=Str(Get(cause,"Method"));if(System.Text.RegularExpressions.Regex.IsMatch(rpcMethod,"^[A-Za-z0-9_/-]{1,180}$"))receipt["FailureRpcMethod"]=rpcMethod;
            if(Get(cause,"RequestMayHaveBeenSent") is bool)receipt["FailureRequestMayHaveBeenSent"]=Get(cause,"RequestMayHaveBeenSent");
            string diagnostic=Str(Get(cause,"Diagnostic"));if(System.Text.RegularExpressions.Regex.IsMatch(diagnostic,"^[A-Za-z0-9_.:/ -]{1,200}$"))receipt["FailureDiagnostic"]=diagnostic;
            var rpc=System.Text.RegularExpressions.Regex.Match(cause.Message??"","^Codex a refusé l'appel d'outil \\(code (-?[0-9]{1,8})\\)");if(rpc.Success)receipt["NativeRpcErrorCode"]=rpc.Groups[1].Value;
            if(cause.Message=="Provider returned an explicit error.")receipt["ProviderReturnedError"]=true;
            return 1;}
        finally{
            if(sampler!=null){using(var finished=new ManualResetEvent(false)){sampler.Dispose(finished);finished.WaitOne(3000);}}
            if(tunnel!=null)try{((IDisposable)tunnel).Dispose();}catch{}
            if(trackingFile!=null){receipt["ProductionTrackingRecordRemovedAfter"]=!File.Exists(trackingFile);if(File.Exists(trackingFile))receipt["State"]="failed";}
            if(protectedRoots!=null)try{VerifyRoots(protectedRoots);}catch{receipt["State"]="failed";}
            lock(sampleLock){
                var groups=samples.SelectMany(s=>Rows(Get(s,"Groups"))).Where(g=>Str(Get(Get(g,"Scope"),"Id"))=="acceptance-helper").ToArray();
                if(groups.Length>0)receipt["HelperMeasurement"]=new{Samples=groups.Length,IntervalMilliseconds=250,PeakResidentBytes=groups.Max(g=>Convert.ToInt64(Get(g,"Resident"))),PeakPrivateBytes=groups.Max(g=>Convert.ToInt64(Get(g,"PrivateBytes"))),PeakProcessCount=groups.Max(g=>Rows(Get(g,"Processes")).Count()),PeakCpuPercentOfWholePc=groups.Select(g=>Get(g,"Cpu")).Where(v=>v!=null).Select(Convert.ToDouble).DefaultIfEmpty().Max(),LastSample=Summary(samples.Last()),Note="Observed helper tree plus its app-server/MCP children. Existing GUI baseline is a different account/workload; no causal RAM saving is asserted."};
                var owned=groups.SelectMany(g=>Rows(Get(g,"Processes"))).Select(p=>new{Pid=Convert.ToInt32(Get(p,"Pid")),Started=Convert.ToInt64(Get(p,"Started"))}).GroupBy(p=>p.Pid+":"+p.Started).Select(g=>g.First()).Where(p=>p.Pid!=Process.GetCurrentProcess().Id).ToArray();
                receipt["OwnedChildProcessesRemaining"]=owned.Where(p=>Alive(p.Pid,p.Started)).Select(p=>p.Pid).ToArray();
            }
            if(output!=null){Save("finished");Console.WriteLine(Serializer.Serialize(new{receipt=output,state=receipt.ContainsKey("State")?receipt["State"]:null,target=receipt.ContainsKey("Target")?receipt["Target"]:null,toolCount=receipt.ContainsKey("ToolCount")?receipt["ToolCount"]:null,failureType=receipt.ContainsKey("FailureType")?receipt["FailureType"]:null}));}
        }
    }
}
