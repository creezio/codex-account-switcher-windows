using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    public sealed class EnrollmentRequest
    {
        public string Name {get;set;}
        // Opaque portal/actor-specific identifier from Accounts, never auth JSON.
        public string AccountId {get;set;}
        public bool ExposeInPortal {get;set;}
        public string LoginMethod {get;set;}
    }
    internal sealed class PortalEnrollmentRecord
    {
        public string Id {get;set;} public string RootRouteId {get;set;} public string ActorHash {get;set;}
        public string RequestHash {get;set;} public string Name {get;set;} public string InstanceId {get;set;}
        public string State {get;set;} public string CreatedUtc {get;set;} public string LocalDeadlineUtc {get;set;}
        public string IdentityHash {get;set;} public string AccountKey {get;set;} public string Email {get;set;} public string Plan {get;set;}
        public string SelectedAccountId {get;set;} public bool DeviceLogin {get;set;} public string LoginMethod {get;set;} public bool AlreadyKnown {get;set;} public bool ExposeInPortal {get;set;}
        public bool Committed {get;set;} public bool IntegrationReady {get;set;} public bool PortalReady {get;set;}
        public string Owner {get;set;} public int OwnerPid {get;set;} public long OwnerStarted {get;set;}
        public int LoginPid {get;set;} public long LoginStarted {get;set;}
        public int CleanupAttempts {get;set;} public string CleanupState {get;set;} public string CleanupWarning {get;set;} public string CleanupNextUtc {get;set;}
        public string Error {get;set;}
    }
    internal sealed class PortalEnrollmentHomeOwner
    {public string EnrollmentId {get;set;}public string InstanceId {get;set;}public string Route {get;set;}public string Actor {get;set;}public string Folder {get;set;}public string Home {get;set;}public List<PortalEnrollmentGeneratedFile> NativeFiles {get;set;}}
    internal sealed class PortalEnrollmentGeneratedFile
    {public string Path {get;set;}public string Sha256 {get;set;}}
    internal sealed class PortalEnrollmentReference
    {public string Route {get;set;}public string Actor {get;set;}public string Id {get;set;}}
    internal sealed class PortalEnrollmentIndex
    {public List<PortalEnrollmentReference> Active {get;set;}public List<PortalEnrollmentReference> Recent {get;set;}public PortalEnrollmentIndex(){Active=new List<PortalEnrollmentReference>();Recent=new List<PortalEnrollmentReference>();}}
    internal interface IPortalEnrollmentLogin : IDisposable
    {
        int ProcessId {get;} long ProcessStartedTicks {get;}
        Task Initialize(CancellationToken token);
        Task<object> Start(string loginMethod,CancellationToken token);
        Task<object> Read(CancellationToken token);
        Task<object> Completed(string loginId,CancellationToken token);
        Task Cancel(string loginId,CancellationToken token);
    }
    internal sealed class PortalEnrollmentLogin : IPortalEnrollmentLogin
    {
        private readonly RpcClient rpc;
        public int ProcessId{get{return rpc.ProcessId;}}public long ProcessStartedTicks{get{return rpc.ProcessStartedTicks;}}
        internal PortalEnrollmentLogin(string executable,string root,string home){rpc=RpcClient.OpenEnrollment(executable,root,home);}
        public Task Initialize(CancellationToken token){return rpc.Initialize(token);}
        public Task<object> Start(string loginMethod,CancellationToken token){return rpc.Call("account/login/start",loginMethod=="browser"?(object)new{type="chatgpt",useHostedLoginSuccessPage=true}:new{type="chatgptDeviceCode"},token);}
        public Task<object> Read(CancellationToken token){return rpc.Call("account/read",new{refreshToken=false},token);}
        public Task<object> Completed(string loginId,CancellationToken token){return rpc.WaitLoginResult(loginId,token);}
        public async Task Cancel(string loginId,CancellationToken token){await rpc.Call("account/login/cancel",new{loginId=loginId},token).ConfigureAwait(false);}
        public void Dispose(){rpc.Dispose();}
    }
    internal sealed class PortalEnrollmentDependencies
    {
        internal Func<DateTime> Clock=()=>DateTime.UtcNow;
        internal Action<PortalEnrollmentRecord> RecordSaving=null;
        internal Action IndexSaving=null;
        internal Action CleanupWaiting=null;
        internal Func<string,string,string,IPortalEnrollmentLogin> Login=(executable,root,home)=>new PortalEnrollmentLogin(executable,root,home);
        internal Func<IDisposable> BrowserAdmission=PortalEnrollmentBrowserAuth.Acquire;
        internal Action BrowserPreflight=PortalEnrollmentBrowserAuth.RequirePortsFree;
        internal Action<int,int,long> BrowserOwner=PortalEnrollmentBrowserAuth.RequireOwner;
        internal Func<Uri,CancellationToken,Task<PortalEnrollmentBrowserResponse>> BrowserSend=PortalEnrollmentBrowserAuth.Send;
        internal Func<AccountService,DesktopInstance,CancellationToken,Task> Readiness=(accounts,instance,token)=>{
            // Refresh only this new, permanently pinned instance if needed, then
            // require its native server to recognize the same account. No turn
            // or existing account/instance is used for this readiness check.
            HeadlessAccounts.Read(accounts,instance,false,token);
            using(new InstanceLease(InstancePaths.Folder(accounts.Vault.Root,instance.Id)))
            using(var rpc=new HeadlessRpc(accounts.Settings.CodexExecutable,accounts.Instances.Home(instance),instance.AccountKey,token))
                rpc.Call("account/read",new{refreshToken=false},token).GetAwaiter().GetResult();
            return Task.FromResult(0);
        };
        internal Func<RelayStore,string,Settings,CancellationToken,Task<RelayIntegrationState>> Integration=async(store,home,settings,token)=>{
            await IntegrationMaintenance.Check(store,home,settings,true,true,token).ConfigureAwait(false);return RelayIntegration.Status(store,home);
        };
    }
    // The private Site is only a remote control of this local enrollment owner.
    // Challenge codes/native login IDs/auth JSON never enter durable records.
    internal sealed class PortalEnrollmentService : IDisposable
    {
        private const string InitialConfig="cli_auth_credentials_store = \"file\"\ncheck_for_update_on_startup = false\n[analytics]\nenabled = false\n[features]\nworkspace_dependencies = false\n";
        private sealed class Session
        {
            internal PortalEnrollmentRecord Record;
            internal readonly CancellationTokenSource Stop=new CancellationTokenSource();
            internal IPortalEnrollmentLogin Login;
            internal string LoginId,VerificationUrl,UserCode;
            internal PortalEnrollmentBrowserChallenge Browser;
            internal IDisposable BrowserAdmission;
            internal string BrowserCallbackHash;
            internal Task Work,StopWork,BrowserCallbackWork;
        }
        private sealed class CleanupEntry {internal string Path,Hash;internal bool Directory;}
        private readonly RelayStore store;private readonly string root,owner=Guid.NewGuid().ToString("N");
        private readonly Action<string,string> authorize;private readonly PortalEnrollmentDependencies dependencies;
        private readonly object sync=new object();private readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>();
        private readonly int pid;private readonly long started;private bool disposed;
        internal PortalEnrollmentService(RelayStore store,Action<string,string> authorize):this(store,authorize,new PortalEnrollmentDependencies()){}
        internal PortalEnrollmentService(RelayStore store,Action<string,string> authorize,PortalEnrollmentDependencies dependencies)
        {
            if(store==null||authorize==null||dependencies==null)throw new ArgumentNullException();this.store=store;this.authorize=authorize;this.dependencies=dependencies;root=Path.GetDirectoryName(store.Root);
            using(var process=Process.GetCurrentProcess()){pid=process.Id;started=process.StartTime.ToUniversalTime().Ticks;}
            Recover();
        }
        private static bool Waiting(string state){return new[]{"starting","awaiting-authorization","identity-confirmation"}.Contains(state);}
        private static bool Terminal(string state){return new[]{"ready","cancelled","expired","interrupted","failed"}.Contains(state);}
        private static string Scope(string route,string actor){return Webhooks.Hash("portal-enrollment-owner\n"+route+"\n"+actor).Substring(0,24);}
        private static string FileName(string route,string actor,string id){RelayStore.MessageId(id);return "portal-enrollment-"+Scope(route,actor)+"-"+id+".dpapi";}
        private static string AccountId(string route,string actor,string key){return Webhooks.Hash("portal-enrollment-account\n"+route+"\n"+actor+"\n"+key);}
        private string Home(PortalEnrollmentRecord record){return InstancePaths.Home(root,record.InstanceId);}
        private string Workspace(PortalEnrollmentRecord record){return Path.Combine(InstancePaths.Folder(root,record.InstanceId),"workspace");}
        private static string Now(DateTime value){return value.ToUniversalTime().ToString("o");}
        private void Require(string route,string actor)
        {
            if(disposed)throw new ObjectDisposedException("PortalEnrollmentService");RelayStore.MessageId(route);
            if(!Regex.IsMatch(actor??"",@"\A[a-f0-9]{64}\z"))throw new WebhookException(403,"Acteur du portail non reconnu.");authorize(route,actor);
        }
        private void Save(PortalEnrollmentRecord record){if(dependencies.RecordSaving!=null)dependencies.RecordSaving(record);store.WriteRecord(FileName(record.RootRouteId,record.ActorHash,record.Id),record);}
        private PortalEnrollmentIndex Index()
        {var value=store.ReadRecord<PortalEnrollmentIndex>("portal-enrollment-index-v1.dpapi");if(value.Active==null||value.Recent==null||value.Active.Count>4||value.Recent.Count>100)throw new InvalidOperationException("Index des inscriptions non reconnu.");return value;}
        private void Prune(PortalEnrollmentIndex index)
        {index.Active.RemoveAll(reference=>{try{var record=Read(reference.Route,reference.Actor,reference.Id);Session local;bool retiring=sessions.TryGetValue(record.Id,out local)&&local.StopWork!=null&&!local.StopWork.IsCompleted;return Terminal(record.State)&&!retiring&&!DesktopRuntime.SameProcess(record.LoginPid,record.LoginStarted);}catch(WebhookException){return true;}});}
        private void Track(PortalEnrollmentIndex index,PortalEnrollmentRecord record)
        {
            Prune(index);if(index.Active.Any(reference=>reference.Id!=record.Id&&reference.Route==record.RootRouteId&&reference.Actor==record.ActorHash))throw new WebhookException(409,"Terminez ou annulez d'abord votre inscription en cours.");
            if(!index.Active.Any(reference=>reference.Id==record.Id)){if(index.Active.Count>=4)throw new WebhookException(409,"Quatre inscriptions sont déjà en cours sur ce PC.");index.Active.Add(new PortalEnrollmentReference{Route=record.RootRouteId,Actor=record.ActorHash,Id=record.Id});}
            index.Recent.RemoveAll(reference=>reference.Id==record.Id);index.Recent.Insert(0,new PortalEnrollmentReference{Route=record.RootRouteId,Actor=record.ActorHash,Id=record.Id});index.Recent=index.Recent.Take(100).ToList();if(dependencies.IndexSaving!=null)dependencies.IndexSaving();store.WriteRecord("portal-enrollment-index-v1.dpapi",index);
        }
        private PortalEnrollmentRecord Read(string route,string actor,string id)
        {
            if(!InstanceRules.ValidId(id))throw new WebhookException(404,"Inscription introuvable.");var record=store.ReadRecord<PortalEnrollmentRecord>(FileName(route,actor,id));
            if(record.Id!=id||record.RootRouteId!=route||record.ActorHash!=actor)throw new WebhookException(404,"Inscription introuvable.");return record;
        }
        private PortalEnrollmentRecord Current(Session session)
        {
            var record=Read(session.Record.RootRouteId,session.Record.ActorHash,session.Record.Id);
            if(record.Owner!=owner)throw new InvalidOperationException("Inscription prise en charge par un autre moteur.");session.Record=record;return record;
        }
        private void ReconcileCommitted(PortalEnrollmentRecord record)
        {
            if(record.Committed||String.IsNullOrEmpty(record.IdentityHash)||String.IsNullOrEmpty(record.AccountKey)||!new[]{"provisioning","failed","interrupted"}.Contains(record.State))return;
            var accounts=new AccountService(root);var instance=accounts.Data.Instances.SingleOrDefault(value=>value.Id==record.InstanceId);
            if(instance!=null&&instance.AccountLocked&&!instance.Archived&&instance.AccountKey==record.AccountKey&&accounts.Instances.ActiveKey(instance)==record.AccountKey){record.Committed=true;Save(record);}
        }
        private void ReconcileFinished(PortalEnrollmentRecord record)
        {
            Session session;if(!sessions.TryGetValue(record.Id,out session))return;
            if(Waiting(record.State)&&session.Stop.IsCancellationRequested&&(session.Work==null||session.Work.IsCompleted)){DateTime deadline;bool expired=DateTime.TryParse(record.LocalDeadlineUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out deadline)&&dependencies.Clock()>=deadline.ToUniversalTime();record.State=expired?"expired":"interrupted";record.Error=expired?"Le délai local de connexion est écoulé.":"La connexion a été interrompue avant l'association du compte.";Save(record);return;}
            if(record.State!="provisioning"||session.Work==null||!session.Work.IsCompleted)return;
            ReconcileCommitted(record);record.State="failed";record.Error=record.Committed?"L'instance créée est conservée. Confirmez pour reprendre sa préparation sans doublon.":"La préparation a été interrompue avant l'association du compte.";Save(record);
        }
        private IEnumerable<PortalEnrollmentRecord> OwnRecords(string route,string actor)
        {
            var index=Index();return index.Active.Concat(index.Recent).Where(reference=>reference.Route==route&&reference.Actor==actor).Select(reference=>reference.Id).Distinct().Select(id=>Read(route,actor,id));
        }
        private void Recover()
        {
            using(store.Lease("portal-enrollment-admission")){var index=Index();foreach(var reference in index.Active.ToArray()){
                var record=Read(reference.Route,reference.Actor,reference.Id);if(!Waiting(record.State)&&record.State!="provisioning")continue;
                if(record.OwnerPid!=0&&DesktopRuntime.SameProcess(record.OwnerPid,record.OwnerStarted))continue;
                using(store.Lease("portal-enrollment-"+record.Id)){
                    record=Read(record.RootRouteId,record.ActorHash,record.Id);if(!Waiting(record.State)&&record.State!="provisioning")continue;ReconcileCommitted(record);
                    record.State="interrupted";record.Error=record.Committed?"L'instance créée est conservée. Confirmez pour reprendre sa préparation sans doublon.":"La connexion a été interrompue. Démarrez une nouvelle inscription ; aucune reconnexion automatique n'a été lancée.";Save(record);
                }
            }Prune(index);store.WriteRecord("portal-enrollment-index-v1.dpapi",index);var cleanup=index.Recent.Select(reference=>Read(reference.Route,reference.Actor,reference.Id)).Where(record=>!record.Committed&&Terminal(record.State)&&record.CleanupState!="complete"&&record.CleanupAttempts<3).ToArray();if(cleanup.Length>0)Task.Run(()=>{foreach(var record in cleanup)CleanupPending(record);});}
        }
        private object[] AccountRows(string route,string actor)
        {
            var accounts=new AccountService(root);return accounts.Data.Profiles.Take(100).Select(profile=>(object)new{id=AccountId(route,actor,profile.Key),label=WebhookChats.Safe(profile.Label,100),email=WebhookChats.Safe(profile.Email,160),plan=WebhookChats.Safe(profile.Plan,60),instanceNames=accounts.Data.Instances.Where(instance=>!instance.Archived&&instance.AccountKey==profile.Key).Select(instance=>WebhookChats.Safe(instance.Name,70)).Take(20).ToArray()}).ToArray();
        }
        internal object Accounts(string rootRouteId,string actorHash)
        {lock(sync){Require(rootRouteId,actorHash);return new{accounts=AccountRows(rootRouteId,actorHash)};}}
        internal object Capabilities(string rootRouteId,string actorHash)
        {lock(sync){Require(rootRouteId,actorHash);return new{enabled=true,canCreateInstances=true,canManagePlugins=true,canManageSkills=true,canManageGoals=true,canManageSchedules=true,deviceCodeSupported=true,browserLoginSupported=true,localLoginWindowSeconds=600,accounts=AccountRows(rootRouteId,actorHash),active=OwnRecords(rootRouteId,actorHash).Where(record=>!Terminal(record.State)||record.Committed&&(record.State=="failed"||record.State=="interrupted")).OrderByDescending(record=>record.CreatedUtc).Take(20).Select(View).ToArray()};}}
        private static EnrollmentRequest Normalize(EnrollmentRequest value)
        {
            if(value==null)throw new WebhookException(400,"Précisez le nom de l'instance.");string name=(value.Name??"").Trim();
            if(name.Length==0||name.Length>70||name.Any(Char.IsControl))throw new WebhookException(400,"Choisissez un nom de 1 à 70 caractères sans caractères de contrôle.");
            if(!String.IsNullOrEmpty(value.AccountId)&&!Regex.IsMatch(value.AccountId,@"\A[a-f0-9]{64}\z"))throw new WebhookException(400,"Compte proposé non reconnu.");
            string method=value.LoginMethod??"device";if(method!="device"&&method!="browser")throw new WebhookException(400,"Choisissez une connexion par code appareil ou par navigateur.");
            return new EnrollmentRequest{Name=name,AccountId=String.IsNullOrEmpty(value.AccountId)?null:value.AccountId,ExposeInPortal=value.ExposeInPortal,LoginMethod=method};
        }
        private void ReconcileStartRetry(PortalEnrollmentRecord record)
        {
            // A storage failure can leave the durable reservation before its
            // session exists, or before a known-account preview was saved. Only
            // this exact owner can prove that no work was launched. Interrupt
            // rather than replaying a possibly uncertain device authorization.
            if(record.Owner!=owner||!Waiting(record.State))return;Session session;
            if(sessions.TryGetValue(record.Id,out session)){ReconcileFinished(record);if(record.State!="starting"||session.Work!=null)return;}
            else{session=new Session{Record=record};sessions.Add(record.Id,session);}
            record.State="interrupted";record.Error="La réservation a été interrompue avant le démarrage de la connexion. Recommencez avec une nouvelle demande ; aucune connexion n'a été relancée automatiquement.";Save(record);session.Record=record;Stop(session,true);
        }
        private static string KnownAuthentication(AccountService accounts,Profile profile)
        {
            // SyncFreshAuth only reads authorized instance homes. Its target is
            // detached here so preparation never replaces the saved profile's
            // credentials or changes an existing instance's authentication.
            var candidate=new Profile{Key=profile.Key,AuthJson=profile.AuthJson,AllInstances=profile.AllInstances,InstanceIds=(profile.InstanceIds??new List<string>()).ToList()};
            accounts.Instances.SyncFreshAuth(candidate);if(AuthIdentity.Parse(candidate.AuthJson).Key!=profile.Key)throw new InvalidOperationException("Le compte proposé a changé.");return candidate.AuthJson;
        }
        internal object Start(string rootRouteId,string actorHash,string idempotency,EnrollmentRequest request)
        {
            lock(sync){Require(rootRouteId,actorHash);request=Normalize(request);if(String.IsNullOrEmpty(idempotency)||idempotency.Length>128||idempotency.Any(c=>c<33||c>126))throw new WebhookException(400,"Idempotency-Key est obligatoire (1 à 128 caractères ASCII visibles).");
                // Preserve the original device request hash across upgrades.
                string id=Webhooks.Hash("portal-enrollment\n"+rootRouteId+"\n"+actorHash+"\n"+idempotency).Substring(0,32),hash=Webhooks.Hash(Json.Write(new{Name=request.Name,AccountId=request.AccountId,ExposeInPortal=request.ExposeInPortal})+(request.LoginMethod=="browser"?"\nbrowser":""));
                using(store.Lease("portal-enrollment-admission"))using(store.Lease("portal-enrollment-"+id)){
                    var old=store.ReadRecord<PortalEnrollmentRecord>(FileName(rootRouteId,actorHash,id));if(old.Id!=null){if(old.RootRouteId!=rootRouteId||old.ActorHash!=actorHash||old.RequestHash!=hash)throw new WebhookException(409,"Cette clé d'inscription correspond déjà à une autre demande.");ReconcileStartRetry(old);return View(old);}
                    var index=Index();Prune(index);if(index.Active.Any(reference=>reference.Route==rootRouteId&&reference.Actor==actorHash))throw new WebhookException(409,"Terminez ou annulez d'abord votre inscription en cours.");if(index.Active.Count>=4)throw new WebhookException(409,"Quatre inscriptions sont déjà en cours sur ce PC.");
                    var accounts=new AccountService(root);if(accounts.Data.Instances.Any(instance=>!instance.Archived&&String.Equals(instance.Name,request.Name,StringComparison.CurrentCultureIgnoreCase)))throw new WebhookException(409,"Ce nom d'instance est déjà utilisé.");
                    Profile selected=null;if(request.AccountId!=null){selected=accounts.Data.Profiles.SingleOrDefault(profile=>AccountId(rootRouteId,actorHash,profile.Key)==request.AccountId);if(selected==null)throw new WebhookException(404,"Compte proposé introuvable.");}
                    var selectedIdentity=selected==null?null:AuthIdentity.Parse(KnownAuthentication(accounts,selected));
                    var record=new PortalEnrollmentRecord{Id=id,RootRouteId=rootRouteId,ActorHash=actorHash,RequestHash=hash,Name=request.Name,InstanceId=Guid.NewGuid().ToString("N"),SelectedAccountId=request.AccountId,DeviceLogin=selectedIdentity==null||HeadlessAccounts.Remaining(selectedIdentity)<=120,LoginMethod=request.LoginMethod,ExposeInPortal=request.ExposeInPortal,State="starting",CreatedUtc=Now(dependencies.Clock()),LocalDeadlineUtc=Now(dependencies.Clock().AddMinutes(10)),Owner=owner,OwnerPid=pid,OwnerStarted=started};
                    // Durable reservation precedes any filesystem/account side effect.
                    IDisposable admission=record.DeviceLogin&&record.LoginMethod=="browser"?dependencies.BrowserAdmission():null;
                    try{Save(record);Track(index,record);var session=new Session{Record=record,BrowserAdmission=admission};session.Stop.CancelAfter(TimeSpan.FromMinutes(10));sessions.Add(id,session);admission=null;
                        if(!record.DeviceLogin){Preview(record,selectedIdentity,true);Save(record);}
                        else session.Work=Task.Run(()=>Connect(session));return View(record);
                    }finally{if(admission!=null)admission.Dispose();}
                }
            }
        }
        private void Preview(PortalEnrollmentRecord record,AuthIdentity identity,bool known)
        {
            record.AccountKey=identity.Key;record.Email=identity.Email;record.Plan=WebhookChats.Safe(identity.Plan,60);record.AlreadyKnown=known;
            record.IdentityHash=Webhooks.Hash("portal-enrollment-identity\n"+record.RootRouteId+"\n"+record.ActorHash+"\n"+record.Id+"\n"+record.InstanceId+"\n"+identity.Key+"\n"+record.Email+"\n"+record.Plan);record.State="identity-confirmation";
        }
        private void PrepareHome(PortalEnrollmentRecord record)
        {
            string folder=InstancePaths.Folder(root,record.InstanceId),home=Home(record),manifest="portal-enrollment-home-"+record.InstanceId+".dpapi";var ownership=store.ReadRecord<PortalEnrollmentHomeOwner>(manifest);
            if(Directory.Exists(folder)){if(ownership.EnrollmentId!=record.Id||ownership.InstanceId!=record.InstanceId||ownership.Route!=record.RootRouteId||ownership.Actor!=record.ActorHash||!RelayStore.SamePath(ownership.Folder??root,folder)||!RelayStore.SamePath(ownership.Home??root,home))throw new InvalidOperationException("Ce dossier d'instance n'appartient pas à l'inscription réservée.");}
            else store.WriteRecord(manifest,new PortalEnrollmentHomeOwner{EnrollmentId=record.Id,InstanceId=record.InstanceId,Route=record.RootRouteId,Actor=record.ActorHash,Folder=folder,Home=home});
            SafeFiles.PrivateDirectory(folder);SafeFiles.PrivateDirectory(home);
            SafeFiles.PrivateDirectory(InstancePaths.UserData(root,record.InstanceId));SafeFiles.PrivateDirectory(Workspace(record));
            string config=Path.Combine(home,"config.toml");if(!File.Exists(config))SafeFiles.AtomicWrite(config,Encoding.UTF8.GetBytes(InitialConfig));CodexEnvironment.CheckFileStorage(home);
        }
        private static string FileHash(string path)
        {using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        private static string Relative(string root,string file){return file.Substring(root.Length).TrimStart('\\','/').Replace('\\','/');}
        private static bool NativeCache(string relative){return relative.StartsWith(".tmp/",StringComparison.Ordinal)||relative.StartsWith("tmp/",StringComparison.Ordinal)||relative.StartsWith(".sandbox/",StringComparison.Ordinal)||relative.StartsWith("skills/.system/",StringComparison.Ordinal);}
        private static bool ProtectedNested(string relative)
        {
            string[] segments=relative.Split('/');if(segments.Any(part=>new[]{".git","sessions","archived_sessions","archived-sessions","history","history.jsonl"}.Contains(part)))return true;
            if(relative.StartsWith("skills/.system/",StringComparison.Ordinal))return false;
            return new[]{".md",".txt",".jsonl",".cs",".js",".mjs",".cjs",".ts",".tsx",".jsx",".py",".yaml",".yml",".toml",".env"}.Contains(System.IO.Path.GetExtension(relative).ToLowerInvariant());
        }
        private static CleanupEntry[] ScanPlan(string folder,bool nativeSnapshot=false)
        {
            var result=new List<CleanupEntry>();var stack=new Stack<string>();stack.Push(folder);long bytes=0;int seen=0;
            while(stack.Count>0){string next=stack.Pop();SafeFiles.RejectLinks(next);result.Add(new CleanupEntry{Path=next,Directory=true});foreach(string path in System.IO.Directory.EnumerateFileSystemEntries(next)){
                    SafeFiles.RejectLinks(path);if(++seen>4096)throw new IOException("cleanup-limit");if(System.IO.Directory.Exists(path))stack.Push(path);else{long length=new FileInfo(path).Length;if((bytes+=length)>67108864)throw new IOException("cleanup-limit");string relative=Relative(folder,path);if(!nativeSnapshot||NativeCache(relative))result.Add(new CleanupEntry{Path=path,Hash=nativeSnapshot&&length==0?"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855":FileHash(path)});}
                }}return result.ToArray();
        }
        private void CaptureNativeFiles(PortalEnrollmentRecord record)
        {
            // The fresh private home has run only initialize/read/login, and the
            // Site cannot use it yet. Capture immutable bundled/generated files
            // before displaying the challenge; later additions are never owned.
            try{string home=Home(record),name="portal-enrollment-home-"+record.InstanceId+".dpapi";var manifest=store.ReadRecord<PortalEnrollmentHomeOwner>(name);var files=ScanPlan(home,true).Where(entry=>!entry.Directory).Select(entry=>new PortalEnrollmentGeneratedFile{Path=Relative(home,entry.Path),Sha256=entry.Hash}).Where(entry=>NativeCache(entry.Path)&&!ProtectedNested(entry.Path)).ToList();manifest.NativeFiles=files;store.WriteRecord(name,manifest);}catch(IOException){}
        }
        private static void DeletePlan(string root,string folder,CleanupEntry[] plan)
        {
            root=System.IO.Path.GetFullPath(root).TrimEnd('\\')+"\\";folder=System.IO.Path.GetFullPath(folder);if(!folder.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("cleanup-confinement");
            foreach(var entry in plan.Where(item=>!item.Directory)){
                if(!entry.Path.StartsWith(folder+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("cleanup-confinement");SafeFiles.RejectLinks(entry.Path);if(FileHash(entry.Path)!=entry.Hash)throw new IOException("cleanup-file-changed");var attributes=File.GetAttributes(entry.Path);if((attributes&FileAttributes.ReadOnly)!=0)File.SetAttributes(entry.Path,attributes&~FileAttributes.ReadOnly);File.Delete(entry.Path);
            }
            // Nonrecursive deletion preserves every unplanned/new child. It
            // fails safely if another process adds a file after inspection.
            foreach(var entry in plan.Where(item=>item.Directory).OrderByDescending(item=>item.Path.Length)){SafeFiles.RejectLinks(entry.Path);System.IO.Directory.Delete(entry.Path,false);}
        }
        private async Task Connect(Session session)
        {
            try{
                PortalEnrollmentRecord record;Settings settings;
                lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);if(record.State!="starting")return;settings=new AccountService(root).Settings;PrepareHome(record);}
                var login=dependencies.Login(settings.CodexExecutable,root,Home(record));lock(sync){if(disposed||session.Stop.IsCancellationRequested){login.Dispose();return;}session.Login=login;record=Current(session);record.LoginPid=login.ProcessId;record.LoginStarted=login.ProcessStartedTicks;Save(record);}
                await login.Initialize(session.Stop.Token).ConfigureAwait(false);var before=await login.Read(session.Stop.Token).ConfigureAwait(false);if(Json.Get(before,"account")!=null)throw new InvalidOperationException("Le profil réservé contient déjà une connexion.");
                if(record.LoginMethod=="browser"){CaptureNativeFiles(record);dependencies.BrowserPreflight();}
                var challenge=await login.Start(record.LoginMethod??"device",session.Stop.Token).ConfigureAwait(false);string loginId=Json.Str(Json.Get(challenge,"loginId")),url,code=null;Guid parsed;Uri uri;PortalEnrollmentBrowserChallenge browser=null;
                if(!Guid.TryParse(loginId,out parsed))throw new InvalidOperationException("Le contrat de connexion native a changé.");
                if(record.LoginMethod=="browser"){
                    if(Json.Str(Json.Get(challenge,"type"))!="chatgpt")throw new InvalidOperationException("Le contrat de connexion par navigateur a changé.");
                    browser=PortalEnrollmentBrowserAuth.Pin(Json.Str(Json.Get(challenge,"authUrl")));dependencies.BrowserOwner(browser.Port,login.ProcessId,login.ProcessStartedTicks);url=browser.AuthUrl;
                }else{
                    url=Json.Str(Json.Get(challenge,"verificationUrl"));code=Json.Str(Json.Get(challenge,"userCode"));
                    if(Json.Str(Json.Get(challenge,"type"))!="chatgptDeviceCode"||!Regex.IsMatch(code,@"\A[A-Z0-9-]{4,32}\z")||!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="auth.openai.com"||uri.Port!=443||uri.AbsolutePath!="/codex/device"||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.UserInfo.Length!=0)throw new InvalidOperationException("Le contrat de connexion par code appareil a changé.");
                }
                CaptureNativeFiles(record);
                lock(sync){record=Current(session);session.LoginId=loginId;if(record.State!="starting"||session.Stop.IsCancellationRequested)return;Require(record.RootRouteId,record.ActorHash);session.Browser=browser;session.VerificationUrl=url;session.UserCode=code;record.State="awaiting-authorization";Save(record);}
                var completed=await login.Completed(loginId,session.Stop.Token).ConfigureAwait(false);if(Json.Str(Json.Get(completed,"loginId"))!=loginId||!Object.Equals(Json.Get(completed,"success"),true))throw new InvalidOperationException("La connexion native a été annulée ou refusée.");
                var native=await login.Read(session.Stop.Token).ConfigureAwait(false);var identity=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(Home(record),"auth.json")));HeadlessRpc.VerifyAccount(native,identity);
                if(record.SelectedAccountId!=null&&AccountId(record.RootRouteId,record.ActorHash,identity.Key)!=record.SelectedAccountId){Fail(session,"Le compte autorisé ne correspond pas au compte choisi. Recommencez la connexion avec le compte sélectionné ; aucune instance n'a été associée.",true);return;}
                lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);if(record.State!="awaiting-authorization"||session.Stop.IsCancellationRequested)return;Preview(record,identity,new AccountService(root).Data.Profiles.Any(profile=>profile.Key==identity.Key));session.UserCode=null;session.VerificationUrl=null;session.Browser=null;Save(record);}
            }
            catch(OperationCanceledException){}
            catch(WebhookException error){Fail(session,error.StatusCode==409&&session.Record.LoginMethod=="browser"?"Un port de connexion Codex est devenu occupé. Terminez la connexion en cours sur le PC, puis démarrez une nouvelle inscription.":"L'autorisation du portail a changé. Aucune instance n'a été associée ; démarrez une nouvelle inscription.",true);}
            catch(Exception){Fail(session,"La connexion n'a pas pu être confirmée. Aucun compte n'a été associé. Vous pouvez lancer une nouvelle inscription.");}
        }
        internal object Get(string rootRouteId,string actorHash,string id)
        {lock(sync){Require(rootRouteId,actorHash);var record=Read(rootRouteId,actorHash,id);ReconcileCommitted(record);ReconcileFinished(record);Expire(record);return View(Read(rootRouteId,actorHash,id));}}
        internal object BrowserCallback(string rootRouteId,string actorHash,string id,string callbackUrl)
        {
            lock(sync){Require(rootRouteId,actorHash);var record=Read(rootRouteId,actorHash,id);ReconcileFinished(record);Expire(record);record=Read(rootRouteId,actorHash,id);Session session;
                if(record.LoginMethod!="browser"||record.Owner!=owner||!sessions.TryGetValue(id,out session))throw new WebhookException(409,"Cette connexion par navigateur a été interrompue. Démarrez une nouvelle inscription.");
                if(session.BrowserCallbackHash!=null){if(session.BrowserCallbackHash==Webhooks.Hash(callbackUrl??""))return View(record);throw new WebhookException(409,"Un retour a déjà été reçu pour cette connexion. Consultez sa progression ou démarrez une nouvelle inscription.");}
                if(record.State!="awaiting-authorization"||session.Stop.IsCancellationRequested||session.Browser==null||session.Login==null)throw new WebhookException(409,"Cette connexion n'attend plus de retour du navigateur.");
                var target=PortalEnrollmentBrowserAuth.Callback(session.Browser,callbackUrl);dependencies.BrowserOwner(session.Browser.Port,session.Login.ProcessId,session.Login.ProcessStartedTicks);
                session.BrowserCallbackHash=Webhooks.Hash(callbackUrl);var browser=session.Browser;int loginPid=session.Login.ProcessId;long loginStarted=session.Login.ProcessStartedTicks;
                session.BrowserCallbackWork=Task.Run(()=>ForwardBrowserCallback(session,browser,target,loginPid,loginStarted));return View(record);
            }
        }
        private async Task ForwardBrowserCallback(Session session,PortalEnrollmentBrowserChallenge browser,Uri target,int loginPid,long loginStarted)
        {
            try{
                // No enrollment lock is held across local HTTP. Native login
                // notifications can finish concurrently on their RPC reader.
                lock(sync){var record=Current(session);Require(record.RootRouteId,record.ActorHash);if(record.State!="awaiting-authorization"||session.Stop.IsCancellationRequested)return;}
                dependencies.BrowserOwner(browser.Port,loginPid,loginStarted);
                var response=await dependencies.BrowserSend(target,session.Stop.Token).ConfigureAwait(false);var success=PortalEnrollmentBrowserAuth.Success(browser,response);
                if(success!=null){lock(sync){var record=Current(session);Require(record.RootRouteId,record.ActorHash);if(record.State!="awaiting-authorization"||session.Stop.IsCancellationRequested)return;}
                    dependencies.BrowserOwner(browser.Port,loginPid,loginStarted);var completed=await dependencies.BrowserSend(success,session.Stop.Token).ConfigureAwait(false);if(completed.Status!=200)throw new InvalidOperationException("native-browser-success-response");}
            }catch(OperationCanceledException){if(!session.Stop.IsCancellationRequested)Fail(session,"Le retour du navigateur n'a pas été confirmé dans le délai prévu. Démarrez une nouvelle connexion ; ce retour ne sera pas renvoyé automatiquement.",true);}
            catch(Exception){Fail(session,"Le retour du navigateur n'a pas pu être confirmé. Démarrez une nouvelle connexion ; aucune instance n'a été associée et ce retour ne sera pas renvoyé automatiquement.",true);}
        }
        private void Expire(PortalEnrollmentRecord record)
        {
            DateTime deadline;if(!Waiting(record.State)||record.Committed||!DateTime.TryParse(record.LocalDeadlineUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out deadline)||dependencies.Clock()<deadline.ToUniversalTime())return;
            record.State="expired";record.Error="Le délai local de connexion de 10 minutes est écoulé. Aucune instance n'a été associée.";Save(record);Session session;if(sessions.TryGetValue(record.Id,out session))Stop(session,true);
        }
        internal object Confirm(string rootRouteId,string actorHash,string id,string identityHash)
        {
            lock(sync){Require(rootRouteId,actorHash);using(store.Lease("portal-enrollment-admission"))using(store.Lease("portal-enrollment-"+id)){
                    var record=Read(rootRouteId,actorHash,id);ReconcileCommitted(record);ReconcileFinished(record);Expire(record);record=Read(rootRouteId,actorHash,id);
                    if(String.IsNullOrEmpty(record.IdentityHash)||identityHash!=record.IdentityHash)throw new WebhookException(409,"Confirmez l'identité actuellement affichée avant de préparer cette instance.");
                    if(record.State=="ready"||record.State=="provisioning")return View(record);
                    if(record.State!="identity-confirmation"&&!(record.Committed&&(record.State=="failed"||record.State=="interrupted")))throw new WebhookException(409,"Cette inscription ne peut plus être confirmée. Démarrez une nouvelle connexion.");
                    Session session;if(!sessions.TryGetValue(id,out session)){if(!record.Committed)throw new WebhookException(409,"La connexion native a été interrompue. Démarrez une nouvelle inscription.");session=new Session{Record=record};sessions.Add(id,session);}
                    var index=Index();Track(index,record);record.Owner=owner;record.OwnerPid=pid;record.OwnerStarted=started;record.State="provisioning";record.Error=null;Save(record);session.Record=record;session.Work=Task.Run(()=>Provision(session));return View(record);
                }
            }
        }
        private void VerifyIdentity(PortalEnrollmentRecord record,AuthIdentity identity)
        {if(identity.Key!=record.AccountKey||identity.Email!=record.Email)throw new InvalidOperationException("Le compte de cette inscription a changé.");}
        private async Task Provision(Session session)
        {
            try{
                PortalEnrollmentRecord record;
                lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);if(record.State!="provisioning")return;}
                // The login server no longer owns this home before installation
                // opens an app-server there. It never touches another instance.
                var callback=session.BrowserCallbackWork;if(callback!=null)await callback.ConfigureAwait(false);
                var login=Interlocked.Exchange(ref session.Login,null);if(login!=null)login.Dispose();ReleaseBrowser(session);
                Settings settings;
                using(VaultMutation.Acquire(root,CancellationToken.None)){
                    lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);if(record.State!="provisioning")return;
                        var accounts=new AccountService(root);settings=accounts.Settings;var instance=accounts.Data.Instances.SingleOrDefault(value=>value.Id==record.InstanceId);
                        if(instance!=null){if(!record.Committed||instance.AccountKey!=record.AccountKey||!instance.AccountLocked||instance.Archived)throw new InvalidOperationException("La réservation ne correspond plus à l'instance créée.");}
                        else{
                            if(record.Committed)throw new InvalidOperationException("L'instance déjà créée a été retirée ; aucun doublon n'a été créé.");
                            if(accounts.Data.Instances.Any(value=>!value.Archived&&String.Equals(value.Name,record.Name,StringComparison.CurrentCultureIgnoreCase)))throw new InvalidOperationException("Ce nom d'instance est maintenant utilisé.");
                            PrepareHome(record);var profile=accounts.Data.Profiles.SingleOrDefault(value=>value.Key==record.AccountKey);string auth;
                            if(profile!=null){var existing=AuthIdentity.Parse(profile.AuthJson);VerifyIdentity(record,existing);auth=record.SelectedAccountId!=null&&!record.DeviceLogin?KnownAuthentication(accounts,profile):SafeFiles.ReadText(Path.Combine(Home(record),"auth.json"));VerifyIdentity(record,AuthIdentity.Parse(auth));record.AlreadyKnown=true;}
                            else{auth=SafeFiles.ReadText(Path.Combine(Home(record),"auth.json"));var identity=AuthIdentity.Parse(auth);VerifyIdentity(record,identity);profile=new Profile{Key=identity.Key,Label=identity.Email,Email=identity.Email,Plan=identity.Plan,AuthJson=auth,AllInstances=false,InstanceIds=new List<string>{record.InstanceId}};accounts.Data.Profiles.Add(profile);}
                            // Existing profile auth and broad/narrow permissions are
                            // preserved; only this explicitly confirmed ID is added.
                            if(!profile.Allows(record.InstanceId))profile.InstanceIds.Add(record.InstanceId);
                            instance=new DesktopInstance{Id=record.InstanceId,Name=record.Name,AccountKey=record.AccountKey,AccountLocked=true,HeadlessEnabled=true,HeadlessSandbox="workspace-write",HeadlessApprovalPolicy="on-request"};
                            string authPath=Path.Combine(Home(record),"auth.json");SafeFiles.AtomicWrite(authPath,Encoding.UTF8.GetBytes(auth));accounts.Data.Instances.Add(instance);
                            Require(record.RootRouteId,record.ActorHash);accounts.Save();record.Committed=true;Save(record);
                        }
                    }
                }
                AccountService readyAccounts;DesktopInstance readyInstance;
                lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);readyAccounts=new AccountService(root);readyInstance=readyAccounts.Data.Instances.Single(value=>value.Id==record.InstanceId&&!value.Archived);if(!readyInstance.AccountLocked||readyInstance.AccountKey!=record.AccountKey)throw new InvalidOperationException("Le compte permanent de l'instance ne correspond plus.");}
                using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60)))await dependencies.Readiness(readyAccounts,readyInstance,timeout.Token).ConfigureAwait(false);
                lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);}
                var status=await dependencies.Integration(store,Home(record),settings,CancellationToken.None).ConfigureAwait(false);
                lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);if(status==null||!status.Healthy)throw new InvalidOperationException("Le relais n'est pas encore préparé.");record.IntegrationReady=true;Save(record);}
                if(record.ExposeInPortal){bool published=false;for(int attempt=0;attempt<3&&!published;attempt++){
                        lock(sync){Require(record.RootRouteId,record.ActorHash);record=Current(session);var accounts=new AccountService(root);var instance=accounts.Data.Instances.SingleOrDefault(value=>value.Id==record.InstanceId&&!value.Archived);if(instance==null||instance.AccountKey!=record.AccountKey||!instance.AccountLocked||accounts.Instances.ActiveKey(instance)!=record.AccountKey)throw new InvalidOperationException("Le compte permanent de l'instance ne correspond plus.");
                            var config=Portal.Config(store,record.RootRouteId);if(!config.Enabled||config.RootRouteId!=record.RootRouteId)throw new InvalidOperationException("Ce portail n'est plus activé.");var existing=config.Instances.SingleOrDefault(value=>value.InstanceId==record.InstanceId);
                            if(existing!=null){if(existing.AccountKey!=record.AccountKey||!RelayStore.SamePath(existing.Home,Home(record)))throw new InvalidOperationException("L'accès existant ne correspond plus à cette instance.");published=true;}
                            else{config.Instances.Add(new PortalInstanceGrant{InstanceId=record.InstanceId,AccountKey=record.AccountKey,Home=Home(record),Workspace=Workspace(record),Access="headless",PermissionProfile="inherit",Model="gpt-6.1-sol",ReasoningEffort="ultra"});Require(record.RootRouteId,record.ActorHash);try{Portal.SaveConfig(store,config);published=true;}catch(InvalidOperationException){if(attempt==2)throw;}}
                        }
                    }
                    lock(sync){record=Current(session);record.PortalReady=published;Save(record);}
                }
                lock(sync){record=Current(session);Require(record.RootRouteId,record.ActorHash);record.State="ready";record.Error=null;Save(record);session.UserCode=null;session.VerificationUrl=null;}
            }
            catch(HeadlessRpcException error){Fail(session,error.Code=="identity"||error.Code=="server-identity"?AuthenticationRequired():PreparationFailed());}
            catch(CloudBackendException error){Fail(session,error.Code=="authentication"||error.Code=="authentication-busy"?AuthenticationRequired():PreparationFailed());}
            catch(Exception){Fail(session,PreparationFailed());}
        }
        private static string AuthenticationRequired(){return "La connexion de ce compte doit être renouvelée. L'instance est conservée. Reconnectez son compte dans Codex sur le PC, puis reprenez cette préparation.";}
        private static string PreparationFailed(){return "La préparation n'est pas terminée. L'instance déjà créée est conservée ; réessayez la confirmation pour reprendre sans doublon.";}
        private void Fail(Session session,string safeMessage,bool discloseUncommitted=false)
        {
            lock(sync){try{var record=Current(session);if(Terminal(record.State))return;ReconcileCommitted(record);record.State="failed";record.Error=record.Committed||discloseUncommitted?safeMessage:"L'inscription n'a pas abouti. Aucune instance n'a été associée ; démarrez une nouvelle inscription.";Save(record);session.Record=record;}catch(Exception){}finally{Stop(session,!session.Record.Committed);}}
        }
        private void SaveCleanup(PortalEnrollmentRecord observation)
        {
            // Cleanup may finish while a committed enrollment is being retried.
            // Rebase only its diagnostic fields, never an old state/account pin.
            lock(sync){var current=Read(observation.RootRouteId,observation.ActorHash,observation.Id);if(current.InstanceId!=observation.InstanceId)throw new InvalidOperationException("Réservation modifiée.");
                current.CleanupAttempts=Math.Max(current.CleanupAttempts,observation.CleanupAttempts);current.CleanupNextUtc=observation.CleanupNextUtc;
                if(!current.Committed&&Terminal(current.State)){current.CleanupState=observation.CleanupState;current.CleanupWarning=observation.CleanupWarning;}Save(current);
            }
        }
        private CleanupEntry[] CheckedCleanupPlan(PortalEnrollmentRecord record,PortalEnrollmentHomeOwner manifest)
        {
            string folder=InstancePaths.Folder(root,record.InstanceId),home=Home(record);
            SafeFiles.RejectLinks(folder);foreach(string item in Directory.EnumerateFileSystemEntries(folder)){SafeFiles.RejectLinks(item);string name=Path.GetFileName(item);if(!Directory.Exists(item)||name!="codex-home"&&name!="electron-profile"&&name!="workspace")throw new IOException("unknown-instance-content");}
            foreach(string empty in new[]{InstancePaths.UserData(root,record.InstanceId),Workspace(record)})if(Directory.Exists(empty)&&Directory.EnumerateFileSystemEntries(empty).Any())throw new IOException("user-content");
            foreach(string history in new[]{"sessions","archived_sessions","archived-sessions","history","history.jsonl"}){string path=Path.Combine(home,history);if(File.Exists(path)||Directory.Exists(path)&&Directory.EnumerateFileSystemEntries(path).Any())throw new IOException("chat-history");}
            string configFile=Path.Combine(home,"config.toml");if(File.Exists(configFile)&&SafeFiles.ReadText(configFile)!=InitialConfig)throw new IOException("user-config");
            if(Directory.Exists(home))foreach(string item in Directory.EnumerateFileSystemEntries(home).Take(4097)){
                SafeFiles.RejectLinks(item);string name=Path.GetFileName(item);
                if(Directory.Exists(item)){if(!new[]{".tmp","tmp",".sandbox","skills","sessions","archived_sessions","archived-sessions","history"}.Contains(name))throw new IOException("unknown-home-directory");if(name=="skills"&&
                        (Directory.EnumerateFileSystemEntries(item).Any(path=>Path.GetFileName(path)!=".system")||!File.Exists(Path.Combine(item,".system",".codex-system-skills.marker"))))throw new IOException("user-skill");}
                // Native SQLite maintenance can create this empty lock after
                // the initial login snapshot. The owned home and retired
                // engine are checked by the caller; any content is preserved.
                else if(name==".sqlite-maintenance.lock"){if(new FileInfo(item).Length!=0)throw new IOException("changed-native-lock");}
                else if(!new[]{"config.toml","auth.json","installation_id","switcher-runtime-owner.json",".creezio-headless-engine.lock"}.Contains(name)&&!Regex.IsMatch(name,@"\A(state|logs|goals|memories|queue)_\d+\.sqlite(?:-wal|-shm)?\z"))throw new IOException("unknown-home-file");
            }
            var generated=(manifest.NativeFiles??new List<PortalEnrollmentGeneratedFile>()).ToDictionary(entry=>entry.Path,entry=>entry.Sha256,StringComparer.Ordinal);
            var plan=ScanPlan(folder);foreach(var entry in plan.Where(item=>item.Path.StartsWith(home+"\\",StringComparison.OrdinalIgnoreCase))){string relative=Relative(home,entry.Path);if(relative==".sqlite-maintenance.lock"&&entry.Hash!="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")throw new IOException("changed-native-lock");if(!NativeCache(relative))continue;if(ProtectedNested(relative))throw new IOException("protected-cache-content");
                if(!entry.Directory){string hash;if(!generated.TryGetValue(relative,out hash))throw new IOException("unowned-cache-file");bool nativeLog=Regex.IsMatch(relative,@"\A\.sandbox/sandbox\.\d{4}-\d{2}-\d{2}\.log\z");if(!nativeLog&&entry.Hash!=hash)throw new IOException("changed-generated-file");}
            }return plan;
        }
        private void CleanupPending(PortalEnrollmentRecord record)
        {
            // Only this feature's newly reserved homes may be removed. Existing
            // instances, earlier scratch folders, source trees and chats are not
            // cleanup candidates, even when their contents look regenerable.
            if(record.Committed||!Terminal(record.State)||record.CleanupState=="complete"||record.CleanupAttempts>=3)return;
            DateTime due;if(DateTime.TryParse(record.CleanupNextUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out due)&&dependencies.Clock()<due.ToUniversalTime())return;
            record.CleanupAttempts++;record.CleanupNextUtc=Now(dependencies.Clock().AddSeconds(30));
            try{
                string folder=InstancePaths.Folder(root,record.InstanceId),home=Home(record);var manifest=store.ReadRecord<PortalEnrollmentHomeOwner>("portal-enrollment-home-"+record.InstanceId+".dpapi");
                if(!Directory.Exists(folder)){record.CleanupState="complete";record.CleanupWarning=null;SaveCleanup(record);return;}
                if(manifest.EnrollmentId!=record.Id||manifest.InstanceId!=record.InstanceId||manifest.Route!=record.RootRouteId||manifest.Actor!=record.ActorHash||!RelayStore.SamePath(manifest.Folder??root,folder)||!RelayStore.SamePath(manifest.Home??root,home))throw new IOException("owner");
                var accounts=new AccountService(root);if(accounts.Data.Instances.Any(value=>value.Id==record.InstanceId)||accounts.Data.Profiles.Any(profile=>profile.InstanceIds!=null&&profile.InstanceIds.Contains(record.InstanceId)))throw new IOException("registered");
                if(DesktopRuntime.SameProcess(record.LoginPid,record.LoginStarted))throw new IOException("active");
                string marker=Path.Combine(home,"switcher-runtime-owner.json");if(File.Exists(marker)){var runtime=Json.Read<RuntimeOwner>(SafeFiles.ReadText(marker));if(runtime.Kind!="codex-rpc-scratch-v1"||!RelayStore.SamePath(runtime.Directory??root,home)||DesktopRuntime.SameProcess(runtime.Pid,runtime.Started))throw new IOException("runtime-owner");}
                using(new InstanceLease(folder)){
                    // Re-read the vault immediately before deleting, under the
                    // same global mutation lease used by confirmed creation.
                    // Inspect after acquiring it, never before a 30s wait.
                    if(dependencies.CleanupWaiting!=null)dependencies.CleanupWaiting();using(VaultMutation.Acquire(root,CancellationToken.None)){accounts=new AccountService(root);if(accounts.Data.Instances.Any(value=>value.Id==record.InstanceId)||accounts.Data.Profiles.Any(profile=>profile.InstanceIds!=null&&profile.InstanceIds.Contains(record.InstanceId)))throw new IOException("registered");var plan=CheckedCleanupPlan(record,manifest);DeletePlan(Path.Combine(root,"instances"),folder,plan);}
                }
                record.CleanupState="complete";record.CleanupWarning=null;SaveCleanup(record);
            }catch(Exception){record.CleanupState="retained";record.CleanupWarning="Le dossier de préparation a été conservé : son propriétaire, son usage ou son contenu ne permet pas un nettoyage sûr. Aucune discussion ni instance existante n'a été supprimée.";try{SaveCleanup(record);}catch(Exception){}}
        }
        private void Stop(Session session,bool discardUncommitted)
        {
            session.UserCode=null;session.VerificationUrl=null;session.Browser=null;session.Stop.Cancel();
            if(session.StopWork!=null&&!session.StopWork.IsCompleted)return;
            session.StopWork=Task.Run(async()=>{var login=Interlocked.Exchange(ref session.Login,null);if(login!=null){try{if(!String.IsNullOrEmpty(session.LoginId))using(var timeout=new CancellationTokenSource(3000))await login.Cancel(session.LoginId,timeout.Token).ConfigureAwait(false);}catch(Exception){}finally{try{login.Dispose();}catch(Exception){}}}
                var work=session.Work;if(work!=null)try{await work.ConfigureAwait(false);}catch(Exception){}
                var callback=session.BrowserCallbackWork;if(callback!=null)try{await callback.ConfigureAwait(false);}catch(Exception){}ReleaseBrowser(session);
                if(discardUncommitted)try{CleanupPending(Read(session.Record.RootRouteId,session.Record.ActorHash,session.Record.Id));}catch(Exception){}
            });
        }
        private static void ReleaseBrowser(Session session){var admission=Interlocked.Exchange(ref session.BrowserAdmission,null);if(admission!=null)admission.Dispose();}
        internal object Cancel(string rootRouteId,string actorHash,string id)
        {
            lock(sync){Require(rootRouteId,actorHash);using(store.Lease("portal-enrollment-"+id)){var record=Read(rootRouteId,actorHash,id);if(record.Committed||record.State=="provisioning")throw new WebhookException(409,"L'instance en préparation est conservée. Son annulation ne la supprime pas.");if(Terminal(record.State))return View(record);record.State="cancelled";record.Error=null;try{Save(record);}finally{Session session;if(sessions.TryGetValue(id,out session))Stop(session,true);}return View(record);}}
        }
        private object View(PortalEnrollmentRecord record)
        {
            Session session;sessions.TryGetValue(record.Id,out session);bool challenge=record.State=="awaiting-authorization"&&session!=null&&session.Record.Owner==owner;
            return new{id=record.Id,state=record.State,name=record.Name,exposeInPortal=record.ExposeInPortal,loginMethod=record.LoginMethod??"device",browserCallbackSubmitted=session!=null&&session.BrowserCallbackHash!=null,createdUtc=record.CreatedUtc,localDeadlineUtc=record.LocalDeadlineUtc,nextPollMs=record.State=="ready"?0:1500,verificationUrl=challenge?session.VerificationUrl:null,userCode=challenge?session.UserCode:null,
                identity=record.IdentityHash==null?null:new{email=WebhookChats.Safe(record.Email,160),plan=record.Plan,hash=record.IdentityHash,alreadyKnown=record.AlreadyKnown},
                steps=new[]{new{id="account",label="Confirmer le compte",state=record.IdentityHash!=null?"complete":Waiting(record.State)?"active":"pending"},new{id="instance",label="Préparer l'instance",state=record.Committed?"complete":record.State=="provisioning"?"active":"pending"},new{id="integration",label="Installer le relais et les skills",state=record.IntegrationReady?"complete":record.Committed&&record.State=="provisioning"?"active":"pending"},new{id="portal",label=record.ExposeInPortal?"Rendre disponible sur ce portail":"Conserver sur ce PC",state=!record.ExposeInPortal||record.PortalReady?"complete":"pending"}},
                instanceId=record.Committed?record.InstanceId:null,error=record.Error,retryable=record.Committed&&(record.State=="failed"||record.State=="interrupted"),cleanupState=record.CleanupState,cleanupWarning=record.CleanupWarning};
        }
        internal void Tick()
        {
            lock(sync){if(disposed)return;foreach(var session in sessions.Values.ToArray())try{var record=Current(session);ReconcileFinished(record);if(Waiting(record.State)){Require(record.RootRouteId,record.ActorHash);Expire(record);}else if(Terminal(record.State)&&(session.Work==null||session.Work.IsCompleted)&&(session.StopWork==null||session.StopWork.IsCompleted)){
                            if(!record.Committed&&record.CleanupState!="complete"&&record.CleanupAttempts<3)session.StopWork=Task.Run(()=>CleanupPending(record));else{sessions.Remove(record.Id);session.Stop.Dispose();}
                        }}catch(Exception){Fail(session,"L'autorisation du portail a changé ; aucune nouvelle opération n'est lancée.");}}
        }
        public void Dispose()
        {
            lock(sync){if(disposed)return;foreach(var session in sessions.Values){try{var record=Current(session);if(Waiting(record.State)){record.State="interrupted";record.Error="Le moteur a été arrêté. Démarrez une nouvelle inscription pour vous reconnecter.";Save(record);}}catch(Exception){}finally{if(!session.Record.Committed&&session.Record.State!="provisioning")Stop(session,true);}}
                // A committed provisioning task is allowed to finish its bounded
                // preparation; it never loses its reserved instance identity.
                disposed=true;
            }
        }
    }
}
