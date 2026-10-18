using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Creezio.Switcher
{
    public sealed class RelayMcpEnginePointer
    {
        public string Home {get;set;} public string AccountKey {get;set;} public string Executable {get;set;}
        public string Version {get;set;} public string Sha256 {get;set;} public string UpdatedUtc {get;set;}
    }
    public sealed class RelayMcpBootstrapRuntime
    {
        public string Home {get;set;} public string AccountKey {get;set;} public string Version {get;set;} public string DesiredVersion {get;set;}
        public string State {get;set;} public string Message {get;set;} public string UpdatedUtc {get;set;} public string Origin {get;set;}
        public int BootstrapPid {get;set;} public long BootstrapStarted {get;set;} public int ChildPid {get;set;} public long ChildStarted {get;set;}
        public int DesktopPid {get;set;} public long DesktopStarted {get;set;}
    }
    internal interface IRelayMcpChild : IDisposable
    {bool Alive {get;} string Exchange(string request);void Notify(string notification);}
    // Stable stdio endpoint for OUR plugin only. Replacing its owned idle child
    // is not a reload of Codex's other MCPs or its already-loaded direct servers.
    internal static class RelayMcpBootstrap
    {
        internal static bool ServerProcess;
        private static string PointerName(string home){return "mcp-engine-"+RelayIntegration.HomeKey(home)+".dpapi";}
        private static int[] CoreVersion(string version,out string[] prerelease)
        {
            var match=Regex.Match(version??"",@"\A(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([A-Za-z0-9.-]+))?(?:\+[A-Za-z0-9.-]+)?\z");if(!match.Success)throw new InvalidOperationException("Version de moteur invalide.");
            prerelease=match.Groups[5].Success?match.Groups[5].Value.Split('.'):new string[0];return Enumerable.Range(1,4).Select(i=>match.Groups[i].Success?Int32.Parse(match.Groups[i].Value,CultureInfo.InvariantCulture):0).ToArray();
        }
        internal static int CompareVersion(string first,string second)
        {
            string[] a,b;int[] x=CoreVersion(first,out a),y=CoreVersion(second,out b);for(int i=0;i<4;i++){int c=x[i].CompareTo(y[i]);if(c!=0)return c;}if(a.Length==0||b.Length==0)return a.Length==b.Length?0:a.Length==0?1:-1;
            for(int i=0;i<Math.Min(a.Length,b.Length);i++){long n,m;bool an=Int64.TryParse(a[i],out n),bn=Int64.TryParse(b[i],out m);int c=an&&bn?n.CompareTo(m):an!=bn?(an?-1:1):String.Compare(a[i],b[i],StringComparison.Ordinal);if(c!=0)return c;}return a.Length.CompareTo(b.Length);
        }
        internal static bool Monotone(RelayMcpEnginePointer previous,RelayMcpEnginePointer next)
        {
            if(next==null)return false;if(previous==null||String.IsNullOrEmpty(previous.Version))return true;if(previous.AccountKey!=next.AccountKey||!RelayStore.SamePath(previous.Home,next.Home))return false;int order=CompareVersion(next.Version,previous.Version);return order>0||order==0&&next.Sha256==previous.Sha256;
        }
        private static string Hash(string path)
        {using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create()){if(file.Length>67108864)throw new InvalidOperationException("Moteur relais trop volumineux.");return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}}
        internal static void ValidateTarget(RelayMcpEnginePointer value,string home,string account)
        {
            if(value==null||!RelayStore.SamePath(value.Home,home)||value.AccountKey!=account||String.IsNullOrEmpty(value.Executable)||!Path.IsPathRooted(value.Executable)||value.Executable.StartsWith("\\\\",StringComparison.Ordinal)||Path.GetFileName(value.Executable)!="CreezioRelay.exe"||!Regex.IsMatch(value.Sha256??"",@"\A[a-f0-9]{64}\z"))throw new InvalidOperationException("Le moteur du relais ne correspond pas a ce profil.");
            SafeFiles.RejectLinks(home);SafeFiles.RejectLinks(value.Executable);if(!File.Exists(value.Executable)||Hash(value.Executable)!=value.Sha256)throw new InvalidOperationException("L'empreinte du moteur relais n'est plus valide.");
            string actual=FileVersionInfo.GetVersionInfo(value.Executable).ProductVersion;if(String.IsNullOrEmpty(actual)||CompareVersion(actual,value.Version)!=0)throw new InvalidOperationException("La version du moteur ne correspond pas au pointeur valide.");
        }
        internal static string Prepare(RelayStore store,string home,string executable)
        {
            if(ServerProcess)throw new InvalidOperationException("Un serveur MCP ne peut pas publier son propre moteur.");home=Path.GetFullPath(home);var account=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json"))).Key;
            var next=new RelayMcpEnginePointer{Home=home,AccountKey=account,Executable=Path.GetFullPath(executable),Version=RelayWorker.Version,Sha256=Hash(executable),UpdatedUtc=DateTime.UtcNow.ToString("o")};ValidateTarget(next,home,account);
            using(store.Lease("mcp-engine-publish-"+RelayIntegration.HomeKey(home))){var previous=store.ReadRecord<RelayMcpEnginePointer>(PointerName(home));if(!Monotone(previous,next))throw new InvalidOperationException("Un moteur plus recent est deja publie. Mise a jour MCP refusee sans retrogradation.");
                string directory=Path.Combine(Path.GetDirectoryName(store.Root),"integrations",RelayIntegration.HomeKey(home),"bootstrap"),stable=Path.Combine(directory,"CreezioRelay.exe");SafeFiles.RejectLinks(directory);SafeFiles.PrivateDirectory(directory);
                string record="mcp-bootstrap-file-"+RelayIntegration.HomeKey(home)+".dpapi";var installed=store.ReadRecord<RelayMcpEnginePointer>(record);
                if(!File.Exists(stable)){SafeFiles.AtomicWrite(stable,File.ReadAllBytes(executable));installed=new RelayMcpEnginePointer{Home=home,AccountKey=account,Executable=stable,Version=next.Version,Sha256=next.Sha256,UpdatedUtc=next.UpdatedUtc};store.WriteRecord(record,installed);}
                ValidateTarget(installed,home,account);store.WriteRecord(PointerName(home),next);return stable;
            }
        }
        private static RelayMcpEnginePointer Resolve(RelayStore store,string home)
        {string account=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json"))).Key;var pointer=store.ReadRecord<RelayMcpEnginePointer>(PointerName(home));if(pointer.Home==null||!RelayStore.SamePath(pointer.Home,home)||pointer.AccountKey!=account)throw new InvalidOperationException("Le compte du bootstrap a change.");return pointer;}
        internal sealed class Session : IDisposable
        {
            private readonly object gate=new object();private readonly Func<RelayMcpEnginePointer> resolve;private readonly Func<RelayMcpEnginePointer,IRelayMcpChild> create;private readonly Action<RelayMcpBootstrapRuntime> observe;private readonly Action<string> notification;
            private IRelayMcpChild child;private RelayMcpEnginePointer active;private string initialize,initialized,protocol,failedCandidate;private int attempts;private bool disposed,uncertain,updateBlocked;
            internal Session(Func<RelayMcpEnginePointer> resolve,Func<RelayMcpEnginePointer,IRelayMcpChild> create,Action<RelayMcpBootstrapRuntime> observe=null,Action<string> notification=null){this.resolve=resolve;this.create=create;this.observe=observe;this.notification=notification;}
            private void State(string state,string message,RelayMcpEnginePointer desired=null){if(observe!=null){var value=new RelayMcpBootstrapRuntime{Version=active==null?null:active.Version,DesiredVersion=desired==null?active==null?null:active.Version:desired.Version,State=state,Message=message,UpdatedUtc=DateTime.UtcNow.ToString("o")};var process=child as Child;if(process!=null&&process.Alive){value.ChildPid=process.Pid;value.ChildStarted=process.Started;}observe(value);}}
            private static string Identity(RelayMcpEnginePointer value){return value.Version+"\n"+value.Sha256+"\n"+value.Executable;}
            private string Handshake(IRelayMcpChild candidate,RelayMcpEnginePointer target,string line)
            {
                string response=candidate.Exchange(line);var parsed=Json.Read<object>(response);if(Json.Write(Json.Get(parsed,"id"))!=Json.Write(Json.Get(Json.Read<object>(line),"id"))||Json.Get(parsed,"error")!=null||Json.Str(Json.Get(Json.Get(Json.Get(parsed,"result"),"serverInfo"),"version"))!=target.Version)throw new InvalidOperationException("Le moteur enfant n'a pas confirme sa version.");string negotiated=Json.Str(Json.Get(Json.Get(parsed,"result"),"protocolVersion"));if(negotiated.Length==0||protocol!=null&&negotiated!=protocol)throw new InvalidOperationException("Le moteur enfant ne confirme pas le protocole MCP actif.");return response;
            }
            private void RefreshCore()
            {
                if(disposed)throw new ObjectDisposedException("MCP bootstrap");if(uncertain){if(child!=null&&child.Alive){State("error","Appel precedent incertain ; attend la fermeture de son moteur sans le rejouer.");return;}uncertain=false;}var desired=resolve();if(active!=null&&(active.AccountKey!=desired.AccountKey||!RelayStore.SamePath(active.Home,desired.Home))){updateBlocked=true;State("error","Le compte ou le profil du moteur a change. Nouveaux appels refuses.",desired);return;}if(active!=null&&Identity(active)==Identity(desired)&&child!=null&&child.Alive)return;
                if(active!=null&&!Monotone(active,desired)){State("error","Retrogradation du moteur refusee.",desired);return;}
                string key=Identity(desired);if(failedCandidate!=key){failedCandidate=key;attempts=0;}if(attempts>=3){updateBlocked=true;State("error","Reconnexion du relais suspendue apres trois echecs ; aucune demande rejouee.",desired);return;}attempts++;
                IRelayMcpChild candidate=null;try{candidate=create(desired);if(initialize!=null){Handshake(candidate,desired,initialize);if(initialized!=null)candidate.Notify(initialized);}var old=child;child=candidate;candidate=null;active=desired;attempts=0;updateBlocked=false;if(old!=null)old.Dispose();State(initialize==null?"pending":"confirmed",initialize==null?"Moteur prepare ; initialisation MCP attendue.":"Moteur du relais confirme pour cette connexion bootstrap.",desired);if(old!=null&&initialized!=null&&notification!=null)notification(Json.Write(new{jsonrpc="2.0",method="notifications/tools/list_changed"}));}
                catch{if(candidate!=null)candidate.Dispose();updateBlocked=true;State("error","Le nouveau moteur n'a pas confirme sa connexion. Nouveaux appels bloques ; aucun appel metier rejoue.",desired);if(child==null||!child.Alive)throw;}
            }
            internal void Refresh(){if(!Monitor.TryEnter(gate))return;try{RefreshCore();}catch{State("error","Reconnexion du relais indisponible ; aucune demande rejouee.");}finally{Monitor.Exit(gate);}}
            internal string Handle(string request)
            {
                lock(gate){object parsed=null,id=null;try{if(request==null||request.Length>262144)throw new InvalidOperationException("Message MCP invalide.");parsed=Json.Read<object>(request);id=Json.Get(parsed,"id");string method=Json.Str(Json.Get(parsed,"method"));
                    if(method=="initialize"){if(initialize!=null)throw new InvalidOperationException("Cette connexion MCP est deja initialisee.");RefreshCore();if(child==null)throw new InvalidOperationException("Moteur MCP indisponible.");string response=Handshake(child,active,request);initialize=request;protocol=Json.Str(Json.Get(Json.Get(Json.Read<object>(response),"result"),"protocolVersion"));var result=Json.Obj(Json.Read<object>(response));var body=Json.Obj(Json.Get(result,"result"));var capabilities=Json.Obj(Json.Get(body,"capabilities"));capabilities["tools"]=new{listChanged=true};body["capabilities"]=capabilities;result["result"]=body;State("confirmed","Moteur du relais confirme pour cette connexion bootstrap.");return Json.Write(result);}
                    if(initialize==null)throw new InvalidOperationException("Initialisation MCP requise.");RefreshCore();if(updateBlocked||uncertain||child==null||!child.Alive)throw new InvalidOperationException("Moteur MCP indisponible.");
                    if(id==null){child.Notify(request);if(method=="notifications/initialized")initialized=request;return null;}
                    // An exchange is attempted exactly once. A broken response is
                    // uncertain, even if the child may already have done the work.
                    try{return child.Exchange(request);}catch{uncertain=true;State("error","Reponse du moteur perdue ; resultat incertain, aucun renvoi automatique.");throw new InvalidOperationException("Connexion au moteur relais perdue. Resultat incertain ; aucune demande rejouee. Verifiez l'historique avant de reessayer.");}
                }catch{return id==null?null:Json.Write(RelayMcp.Error(id,-32001,updateBlocked?"Mise a jour du moteur relais non confirmee. Nouveaux appels bloques ; aucune demande rejouee.":"Connexion au relais non confirmee ou reponse perdue. Aucun appel metier rejoue ; verifiez le resultat avant de reessayer."));}}
            }
            public void Dispose(){lock(gate){disposed=true;if(child!=null){child.Dispose();child=null;}State("stopped","Connexion bootstrap fermee.");}}
        }
        private sealed class Child : IRelayMcpChild
        {
            private readonly Process process;private readonly StreamWriter input;private readonly StreamReader output;private readonly Action<string> notification;
            internal int Pid{get{return process.Id;}}internal long Started{get{return process.StartTime.ToUniversalTime().Ticks;}}
            internal Child(RelayStore store,string home,RelayMcpEnginePointer target,Action<string> notification)
            {
                ValidateTarget(target,home,target.AccountKey);this.notification=notification;var start=new ProcessStartInfo(target.Executable,"--mcp "+RelayWorker.Quote(store.Root)+" "+RelayWorker.Quote(home)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Path.GetDirectoryName(target.Executable)};
                start.EnvironmentVariables["CODEX_HOME"]=home;start.EnvironmentVariables["CODEX_SQLITE_HOME"]=home;process=Process.Start(start);input=process.StandardInput;input.AutoFlush=true;output=process.StandardOutput;process.ErrorDataReceived+=(sender,args)=>{};process.BeginErrorReadLine();
            }
            public bool Alive{get{try{return !process.HasExited;}catch(InvalidOperationException){return false;}}}
            public void Notify(string line){input.WriteLine(line);input.Flush();}
            public string Exchange(string request)
            {var wanted=Json.Get(Json.Read<object>(request),"id");Notify(request);for(int i=0;i<128;i++){var read=output.ReadLineAsync();if(!read.Wait(TimeSpan.FromMinutes(5)))throw new IOException("Delai MCP depasse.");string line=read.Result;if(line==null||line.Length>4000000)throw new IOException("Reponse MCP absente ou trop volumineuse.");var reply=Json.Read<object>(line);if(Json.Get(reply,"id")==null){if(notification!=null)notification(line);continue;}if(Json.Write(Json.Get(reply,"id"))!=Json.Write(wanted))throw new IOException("Recu MCP inattendu.");return line;}throw new IOException("Trop de notifications MCP sans reponse.");}
            public void Dispose(){try{input.Close();}catch(IOException){}try{output.Close();}catch(IOException){}process.Dispose();}
        }
        private static string RuntimeName(string home,int pid,long started){return "mcp-bootstrap-"+RelayIntegration.HomeKey(home)+"-"+pid.ToString(CultureInfo.InvariantCulture)+"-"+started.ToString(CultureInfo.InvariantCulture)+".dpapi";}
        [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32First(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32Next(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Entry{public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
        private static void Origin(RelayStore store,string home,RelayMcpBootstrapRuntime state)
        {
            state.Origin="unknown";var parents=new Dictionary<int,int>();IntPtr snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return;try{var entry=new Entry{Size=(uint)Marshal.SizeOf(typeof(Entry))};if(Process32First(snapshot,ref entry))do{parents[(int)entry.Pid]=(int)entry.Parent;}while(Process32Next(snapshot,ref entry));}finally{CloseHandle(snapshot);}int parent,desktop;if(!parents.TryGetValue(state.BootstrapPid,out parent)||!parents.TryGetValue(parent,out desktop))return;
            try{using(var native=Process.GetProcessById(parent)){if(!String.Equals(Path.GetFileName(native.MainModule.FileName),"codex.exe",StringComparison.OrdinalIgnoreCase))return;var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=accounts.Data.Instances.SingleOrDefault(i=>!i.Archived&&i.AccountKey==state.AccountKey&&RelayStore.SamePath(accounts.Instances.Home(i),home));if(instance==null)return;var runtime=accounts.Instances.Runtime.Probe(instance);if(runtime.Running&&runtime.DesktopPid==desktop&&DesktopRuntime.SameProcess(desktop,runtime.DesktopStartTicks)&&native.StartTime.ToUniversalTime().Ticks>=runtime.DesktopStartTicks){state.Origin="desktop";state.DesktopPid=desktop;state.DesktopStarted=runtime.DesktopStartTicks;}}}catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
        internal static void Describe(RelayStore store,string home,RelayIntegrationState state)
        {Describe(store,home,state,DesktopRuntime.SameProcess);}
        internal static void Describe(RelayStore store,string home,RelayIntegrationState state,Func<int,long,bool> inspectProcess)
        {
            // This is an inventory observation only. Security callers keep the strict
            // SameProcess behavior; an inaccessible receipt can never confirm a runtime.
            bool unavailable=false;
            DescribeCore(store,home,state,(pid,started)=>{
                try{return inspectProcess(pid,started);}
                catch(System.ComponentModel.Win32Exception){unavailable=true;return false;}
                catch(UnauthorizedAccessException){unavailable=true;return false;}
            });
            if(unavailable){state.RuntimeStatus="unknown";state.RuntimeVersion=null;state.RuntimeUpdatedUtc=null;state.BootstrapConnections=0;state.RuntimeMessage="État du relais à vérifier : Windows n'autorise pas la lecture des métadonnées d'un processus. L'installation sur disque est conservée ; aucun moteur actif n'est confirmé.";}
        }
        private static void DescribeCore(RelayStore store,string home,RelayIntegrationState state,Func<int,long,bool> inspectProcess)
        {
            state.DesiredVersion=state.InstalledVersion==null?null:state.InstalledVersion.Split('+')[0];state.RuntimeStatus=state.BootstrapConfigured?"reload-required":"unknown";state.RuntimeMessage="Les connexions MCP directes deja ouvertes ne sont pas rechargees. Le lanceur de mise à jour du relais doit être chargé une première fois par Codex.";
            string account=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json"))).Key;var rows=Directory.GetFiles(store.Root,"mcp-bootstrap-"+RelayIntegration.HomeKey(home)+"-*.dpapi",SearchOption.TopDirectoryOnly).Take(256).Select(p=>store.ReadRecord<RelayMcpBootstrapRuntime>(Path.GetFileName(p))).Where(r=>r.Origin=="desktop"&&r.AccountKey==account&&r.Home!=null&&RelayStore.SamePath(r.Home,home)&&inspectProcess(r.BootstrapPid,r.BootstrapStarted)&&inspectProcess(r.DesktopPid,r.DesktopStarted)).ToArray();state.BootstrapConnections=rows.Length;if(rows.Length==0){if(state.BootstrapConfigured){var accounts=new AccountService(Path.GetDirectoryName(store.Root));var instance=accounts.Data.Instances.SingleOrDefault(i=>!i.Archived&&i.AccountKey==account&&RelayStore.SamePath(accounts.Instances.Home(i),home));if(instance!=null){var runtime=accounts.Instances.Runtime.Probe(instance);if(!runtime.Running&&runtime.Phase!="unknown"){state.RuntimeStatus="pending";state.RuntimeMessage="Relais préparé sur disque ; le lanceur de mise à jour sera chargé au prochain démarrage du moteur. Aucune reconnexion manuelle d'une instance fermée n'est requise.";}}}return;}
            state.RuntimeVersion=String.Join(", ",rows.Select(r=>r.Version??"inconnue").Distinct().Take(4));state.RuntimeUpdatedUtc=rows.Select(r=>r.UpdatedUtc).OrderBy(x=>x,StringComparer.Ordinal).LastOrDefault();state.RuntimeStatus=rows.All(r=>r.State=="confirmed"&&r.Version==state.DesiredVersion&&inspectProcess(r.ChildPid,r.ChildStarted))?"confirmed":rows.Any(r=>r.State=="error")?"error":"pending";state.RuntimeMessage="Etat des connexions bootstrap observees uniquement. Les anciennes connexions MCP directes ne sont pas verifiees ni rechargees.";if(state.RuntimeStatus=="error")state.RuntimeMessage+=" "+String.Join(" ",rows.Where(r=>r.State=="error").Select(r=>WebhookChats.Safe(r.Message,300)).Where(s=>!String.IsNullOrEmpty(s)).Distinct().Take(2));
        }
        internal static int Run(string root,string home)
        {
            ServerProcess=true;var store=new RelayStore(root);home=Path.GetFullPath(home);using(var current=Process.GetCurrentProcess())using(var reader=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false)))using(var writer=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))){writer.AutoFlush=true;object outputGate=new object();string lastReceipt=null;DateTime lastWrite=DateTime.MinValue;Action<string> output=line=>{lock(outputGate){writer.WriteLine(line);writer.Flush();}};Action<RelayMcpBootstrapRuntime> observe=state=>{state.Home=home;string stamp=Json.Write(new{state.Version,state.DesiredVersion,state.State,state.Message,state.ChildPid,state.ChildStarted});if(stamp==lastReceipt&&DateTime.UtcNow-lastWrite<TimeSpan.FromSeconds(60))return;try{state.AccountKey=AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(home,"auth.json"))).Key;state.BootstrapPid=current.Id;state.BootstrapStarted=current.StartTime.ToUniversalTime().Ticks;Origin(store,home,state);store.WriteRecord(RuntimeName(home,state.BootstrapPid,state.BootstrapStarted),state);lastReceipt=stamp;lastWrite=DateTime.UtcNow;}catch(IOException){}catch(InvalidOperationException){}};
                using(var session=new Session(()=>Resolve(store,home),pointer=>new Child(store,home,pointer,output),observe,output))using(var timer=new System.Threading.Timer(o=>session.Refresh(),null,30000,30000)){string line;while((line=reader.ReadLine())!=null){string response=session.Handle(line);if(response!=null)output(response);}}
            }return 0;
        }
    }
}
