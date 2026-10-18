using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Creezio.Switcher;

// Pure parsing/policy and fake-native admission tests: no desktop settings,
// existing conversations, native prompts, or account credentials are touched.
internal static class PortalPermissionTests
{
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static void Invalid(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected permission or immutable-binding rejection");}
    private static void Reject(int status,Action action){try{action();}catch(WebhookException ex){Assert(ex.StatusCode==status,"Expected "+status+", got "+ex.StatusCode);return;}throw new Exception("Expected rejection "+status);}
    private static object Normal(object value){return Json.Read<object>(Json.Write(value));}
    private static string Body(string profile){return Json.Write(new{prompt="Fictional permission fixture only",options=new{permissionProfile=profile}});}
    private static string Profile(PortalMessageOptions value){return value==null?null:Json.Str(Json.Get(Json.Read<object>(value.Canonical()),"permissionProfile"));}
    private sealed class FakeNative : IPortalNative
    {
        internal int Verifications;
        public void Verify(PortalInstanceGrant grant){Verifications++;}
        public object Describe(PortalInstanceGrant grant){throw new Exception("Permission admission must not enumerate desktops");}
        public object List(PortalInstanceGrant grant){throw new Exception("Permission admission must not enumerate chats");}
        public object Read(PortalInstanceGrant grant,string thread){throw new Exception("New fixture chat must not read existing conversations");}
    }
    private sealed class Fixture
    {
        internal readonly RelayStore Store;internal WebhookRoute Route;internal PortalConfig Config;internal readonly PortalInstanceGrant Grant;internal readonly FakeNative Native=new FakeNative();internal readonly string Key;
        internal Fixture(string root,string name)
        {
            string folder=Path.Combine(Path.GetFullPath(root),"portal-permission-tests",name+"-"+Guid.NewGuid().ToString("N").Substring(0,8)),home=Path.Combine(folder,"home"),workspace=Path.Combine(folder,"workspace");Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);Store=new RelayStore(Path.Combine(folder,"relay"));
            Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true});Route=Webhooks.SaveRoute(Store,new WebhookRoute{Slug="permission-fixture",Name="Fictional permission route",InstanceId=new string('a',32),AccountKey="fictional-root-account",Home=home,Workspace=workspace,Enabled=true,PermissionProfile="inherit"});Key=Webhooks.RotateKey(Store,Route.Id);Route=Webhooks.Route(Store,Route.Id);
            Grant=new PortalInstanceGrant{InstanceId=new string('b',32),AccountKey="fictional-selected-account",Home=home,Workspace=workspace,Access="desktop",PermissionProfile="inherit",Model="gpt-6.1-sol",ReasoningEffort="ultra"};Config=Portal.SaveConfig(Store,new PortalConfig{RootRouteId=Route.Id,Enabled=true,Instances=new List<PortalInstanceGrant>{Grant}});
        }
        internal WebhookJob Admit(string key,string profile,string bearer=null){return Portal.Admit(Store,Route.Slug,bearer??Key,Grant.InstanceId,null,key,Body(profile),Native);}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("permission selections accept only the explicit published profiles",()=>{
            foreach(string profile in new[]{"read-only","workspace-write","full-access"})Assert(Profile(PortalMessageOptions.FromPayload(Webhooks.ParseBody(Body(profile))))==profile,"Explicit profile was omitted or changed");
            foreach(string profile in new[]{"inherit",(string)null})Assert(PortalMessageOptions.FromPayload(Webhooks.ParseBody(Body(profile)))==null,"Inheritance became an explicit permission change");
        });
        check("permission parser rejects ambiguous escaped keys raw policies and unknown profiles",()=>{
            foreach(string value in new[]{"true","1","[]","{}","\"danger-full-access\"","\"never\"","\"Full-Access\"","\"full-access \"","\"\""})Reject(400,()=>Webhooks.ParseBody("{\"prompt\":\"Fixture\",\"options\":{\"permissionProfile\":"+value+"}}"));
            Reject(400,()=>Webhooks.ParseBody("{\"prompt\":\"Fixture\",\"options\":{\"permissionProfile\":\"read-only\",\"permissionProfi\\u006ce\":\"full-access\"}}"));
            foreach(string field in new[]{"sandboxPolicy","approvalPolicy","permissions","writableRoots","networkAccess"})Reject(400,()=>Webhooks.ParseBody("{\"prompt\":\"Fixture\",\"options\":{\""+field+"\":\"full-access\"}}"));
            Reject(400,()=>Webhooks.ParseBody("{\"prompt\":\"Fixture\",\"permissionProfile\":\"full-access\"}"));
        });
        check("permission omission null and inheritance preserve all legacy rich fingerprints",()=>{
            string canonical=Json.Write(new{model="gpt-6.1-sol",reasoningEffort="ultra",collaborationMode=(string)null,plugins=new[]{"fixture-plugin"},skills=new string[0],attachments=new[]{new string('a',32)}});
            string oldFingerprint=Webhooks.Hash(Json.Write(new[]{"Mission externe","Legacy fixture","",canonical}));
            foreach(string extra in new[]{"",",\"permissionProfile\":null",",\"permissionProfile\":\"inherit\""}){
                var payload=Webhooks.ParseBody("{\"prompt\":\"Legacy fixture\",\"options\":"+canonical.Substring(0,canonical.Length-1)+extra+"}}");var options=PortalMessageOptions.FromPayload(payload);
                Assert(options.Canonical()==canonical&&Webhooks.PayloadFingerprint(payload)==oldFingerprint,"Permission upgrade broke an existing rich retry");string basis=Portal.FrozenRouteId(new string('b',32),new string('c',32));Assert(Webhooks.ExecutionRouteId(new string('b',32),new string('c',32),options)==Webhooks.Hash(basis+"\ncomposer-v1\n"+canonical).Substring(0,32),"Permission upgrade invalidated queued rich jobs");
            }
        });
        check("permission changes participate in fingerprint and old-engine fail-closed identities",()=>{
            var payloads=new[]{"read-only","workspace-write","full-access"}.Select(p=>Webhooks.ParseBody(Body(p))).ToArray();Assert(payloads.Select(Webhooks.PayloadFingerprint).Distinct().Count()==3,"Different permission choices share an idempotency fingerprint");
            string route=new string('a',32),instance=new string('b',32);foreach(var payload in payloads){var options=PortalMessageOptions.FromPayload(payload);Assert(Webhooks.ExecutionRouteId(route,null,options)!=route&&Webhooks.ExecutionRouteId(route,instance,options)!=Portal.FrozenRouteId(route,instance),"Old engine can silently drop explicit permissions");}
        });
        check("permission retry cannot silently turn a read-only request into full access",()=>{
            var f=new Fixture(root,"retry");var first=f.Admit("same","read-only");Assert(f.Admit("same","read-only").Id==first.Id,"Same permission retry duplicated admission");Reject(409,()=>f.Admit("same","full-access"));Assert(Webhooks.Jobs(f.Store).Length==1&&Profile(Webhooks.Job(f.Store,first.Id).Options)=="read-only","Permission conflict altered or duplicated the queued request");
        });
        check("permission options never bypass portal authorization or select another account",()=>{
            var f=new Fixture(root,"authorization");Reject(401,()=>f.Admit("unauthorized","full-access","wrong-key"));Assert(f.Native.Verifications==0&&Webhooks.Jobs(f.Store).Length==0,"Invalid token reached profile or queue");
            var job=f.Admit("authorized","full-access");Assert(job.RouteSnapshot.AccountKey==f.Grant.AccountKey&&job.RouteSnapshot.InstanceId==f.Grant.InstanceId&&job.RouteSnapshot.Home==f.Grant.Home&&job.RouteSnapshot.AccountKey!=f.Route.AccountKey,"Permission selection changed account routing");
            Assert(f.Config.Instances.Single().PermissionProfile=="inherit"&&Webhooks.Route(f.Store,f.Route.Id).PermissionProfile=="inherit","Per-message choice changed route or account settings");
        });
        check("permission profile is immutable and revocation still prevents dispatch",()=>{
            var f=new Fixture(root,"immutable");var job=f.Admit("one","full-access");var changed=Webhooks.Job(f.Store,job.Id);changed.Options=PortalMessageOptions.FromPayload(Webhooks.ParseBody(Body("read-only")));Invalid(()=>Webhooks.SaveJob(f.Store,changed));Assert(Profile(Webhooks.Job(f.Store,job.Id).Options)=="full-access","Accepted permission profile was overwritten");
            f.Config.Enabled=false;Portal.SaveConfig(f.Store,f.Config);Invalid(()=>Portal.ValidateJob(f.Store,job));Assert(Webhooks.Job(f.Store,job.Id).State=="queued","Read-only validation mutated or sent the request");
        });
        check("permission inheritance produces no native policy fields",()=>{
            foreach(string profile in new[]{(string)null,"inherit"})Assert(RelayPermissions.NativeProfile(profile,Path.GetFullPath(root)).Count==0&&!RelayPermissions.IsExplicitProfile(profile),"An omitted setting changes native security policy");
            foreach(string profile in new[]{"read-only","workspace-write","full-access"})Assert(RelayPermissions.IsExplicitProfile(profile),"Explicit profile was not identified");Invalid(()=>RelayPermissions.NativeProfile("danger-full-access",Path.GetFullPath(root)));
        });
        check("permission full access maps to exact unrestricted sandbox and never approvals",()=>{
            var policy=Normal(RelayPermissions.NativeProfile("full-access",Path.GetFullPath(root)));Assert(Json.Str(Json.Get(policy,"approvalPolicy"))=="never"&&Json.Str(Json.Get(Json.Get(policy,"sandboxPolicy"),"type"))=="dangerFullAccess","Full access was silently reduced or did not set both native controls");Assert(!Json.Write(policy).Contains("writableRoots"),"Full access unexpectedly froze unrelated roots");
        });
        check("permission constrained profiles keep on-request approval and one authorized workspace",()=>{
            string workspace=Path.GetFullPath(root);var read=Normal(RelayPermissions.NativeProfile("read-only",workspace));var write=Normal(RelayPermissions.NativeProfile("workspace-write",workspace));
            Assert(Json.Str(Json.Get(read,"approvalPolicy"))=="on-request"&&Json.Str(Json.Get(Json.Get(read,"sandboxPolicy"),"type"))=="readOnly"&&Object.Equals(Json.Get(Json.Get(read,"sandboxPolicy"),"networkAccess"),false),"Read-only mapping became broader");
            var sandbox=Json.Get(write,"sandboxPolicy");var roots=RelayEngine.Rows(Json.Get(sandbox,"writableRoots")).Select(Json.Str).ToArray();Assert(Json.Str(Json.Get(write,"approvalPolicy"))=="on-request"&&Json.Str(Json.Get(sandbox,"type"))=="workspaceWrite"&&Object.Equals(Json.Get(sandbox,"networkAccess"),false)&&roots.Length==1&&RelayStore.SamePath(roots[0],workspace),"Workspace write widened roots network or approval policy");
        });
        check("permission verification requires effective sandbox and approval together",()=>{
            var full=Normal(new{approval_policy="never",sandbox_policy=new{type="danger-full-access"}});var camel=Normal(new{approvalPolicy="never",sandboxPolicy=new{type="dangerFullAccess"}});
            Assert(RelayPermissions.MatchesProfile(full,"full-access")&&RelayPermissions.MatchesProfile(camel,"full-access"),"Native full-access spelling was not recognized");
            Assert(!RelayPermissions.MatchesProfile(Normal(new{approval_policy="on-request",sandbox_policy=new{type="danger-full-access"}}),"full-access"),"Sandbox alone was accepted as full access");
            Assert(!RelayPermissions.MatchesProfile(Normal(new{approval_policy="never",sandbox_policy=new{type="workspace-write",network_access=false}}),"full-access"),"Never approvals alone was accepted as full access");
            Assert(!RelayPermissions.MatchesProfile(new Dictionary<string,object>(),"full-access"),"Missing context was treated as confirmed full access");
        });
        check("permission verification rejects broader effective constrained policies",()=>{
            Assert(!RelayPermissions.MatchesProfile(Normal(new{approval_policy="never",sandbox_policy=new{type="danger-full-access"}}),"read-only"),"Full access was accepted for a read-only request");
            foreach(string profile in new[]{"read-only","workspace-write"})Assert(!RelayPermissions.MatchesProfile(Normal(new{approval_policy="on-request",sandbox_policy=new Dictionary<string,object>{{"type",profile},{"network_access",true}}}),profile),"Constrained permission accepted unrestricted network access");
            Assert(RelayPermissions.MatchesProfile(new Dictionary<string,object>(),"inherit"),"Inherited policy was treated as a permission upgrade");
        });
        check("permission verification never treats malformed network values as confirmed restriction",()=>{
            foreach(object network in new object[]{"true",1,new object[0]})Assert(!RelayPermissions.MatchesProfile(Normal(new{approval_policy="on-request",sandbox_policy=new Dictionary<string,object>{{"type","workspace-write"},{"network_access",network}}}),"workspace-write"),"Malformed network policy was accepted as restricted");
        });
        check("permission native changes require an explicit verified operator mandate",()=>{
            var message=new RelayMessage{ComposerOptions=new PortalMessageOptions{PermissionProfile="full-access"},ResolvedComposer=new PortalResolvedComposer{PermissionProfile="full-access"}};
            Invalid(()=>PortalNativeComposer.ExplicitPermission(message));message.OperatorOrigin=true;Invalid(()=>PortalNativeComposer.ExplicitPermission(message));message.WebhookJobId=new string('a',32);Assert(PortalNativeComposer.ExplicitPermission(message)=="full-access","Verified explicit choice was ignored");
            message.ResolvedComposer.PermissionProfile="read-only";Invalid(()=>PortalNativeComposer.ExplicitPermission(message));message.ResolvedComposer=null;Invalid(()=>PortalNativeComposer.ExplicitPermission(message));
            message.ComposerOptions=new PortalMessageOptions();Assert(PortalNativeComposer.ExplicitPermission(message)==null,"Omitted profile caused an implicit permission change");
        });
        check("permission native packet inherits unchanged settings and carries only an explicit profile",()=>{
            string thread="01a14ad6-eaff-7671-ae85-99ab61d47587",workspace=Path.GetFullPath(root);var inherited=new PortalResolvedComposer{InputItems=new object[0]};var initial=Normal(PortalNativeComposer.Packet(thread,"Fiction",new string('a',32),inherited,"gpt-6.1-sol","ultra",workspace));var request=Json.Obj(Json.Get(Json.Get(initial,"turnStart"),"request"));Assert(!request.ContainsKey("approvalPolicy")&&!request.ContainsKey("sandboxPolicy"),"Inheritance added a security override");
            inherited.PermissionProfile="full-access";var explicitPacket=Normal(PortalNativeComposer.Packet(thread,"Fiction",new string('a',32),inherited,"gpt-6.1-sol","ultra",workspace));request=Json.Obj(Json.Get(Json.Get(explicitPacket,"turnStart"),"request"));Assert(Json.Str(request["approvalPolicy"])=="never"&&Json.Str(Json.Get(request["sandboxPolicy"],"type"))=="dangerFullAccess","Explicit choice was reduced to a prompt instruction");Assert(!request.ContainsKey("cwd")&&!request.ContainsKey("threadIdOverride"),"Remote profile selection redirected the workspace or thread");
        });
        check("permission-only desktop selections use native scoped transport while inheritance does not",()=>{
            var message=new RelayMessage{ComposerOptions=new PortalMessageOptions{PermissionProfile="full-access"}};Assert(PortalNativeComposer.RequiresNative(message,new RelayChannel{Provider="codex"}),"Explicit permissions were silently sent through an unsupported plain gateway");Assert(!PortalNativeComposer.RequiresNative(message,new RelayChannel{Provider="codex-headless"}),"Headless policy changed another desktop through global IPC");message.ComposerOptions.PermissionProfile="inherit";Assert(!PortalNativeComposer.RequiresNative(message,new RelayChannel{Provider="codex"}),"Inheritance unnecessarily changed desktop transport");
        });
        check("permission effective evidence belongs to the exact turn and selected profile home",()=>{
            var f=new Fixture(root,"effective-context");string thread="01900000-0000-7000-8000-"+Guid.NewGuid().ToString("N").Substring(0,12);long ms=Convert.ToInt64(thread.Replace("-","").Substring(0,12),16);var day=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ms);string directory=Path.Combine(f.Grant.Home,"sessions",day.ToString("yyyy"),day.ToString("MM"),day.ToString("dd"));Directory.CreateDirectory(directory);string rollout=Path.Combine(directory,"rollout-fixture-"+thread+".jsonl");
            File.WriteAllText(rollout,Json.Write(new{type="session_meta",payload=new{id=thread,cwd=f.Grant.Workspace}})+"\n"+Json.Write(new{type="turn_context",payload=new{turn_id="explicit-probe",approval_policy="never",sandbox_policy=new{type="danger-full-access"}}})+"\n"+Json.Write(new{type="turn_context",payload=new{turn_id="other-turn",approval_policy="on-request",sandbox_policy=new{type="read-only",network_access=false}}})+"\n");
            Assert(RelayPermissions.ReadProfile(f.Grant.Home,thread,"explicit-probe")=="full-access","Effective proof used another turn");Assert(RelayPermissions.ReadProfile(f.Grant.Home,thread,"other-turn")=="read-only","Latest constrained profile was lost");Assert(RelayPermissions.ReadProfile(f.Grant.Home,thread,"missing-probe")=="unknown","Missing proof fell back to another turn");Assert(RelayPermissions.ReadProfile(Path.Combine(f.Store.Root,"another-home"),thread,"explicit-probe")=="unknown","Effective proof crossed profile homes");
        });
    }
}
