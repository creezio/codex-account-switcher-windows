using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class InstanceManager
    {
        private readonly AccountService service;
        public readonly IInstanceRuntime Runtime;
        public InstanceManager(AccountService accounts,IInstanceRuntime runtime=null)
        {service=accounts;Runtime=runtime ?? new DesktopRuntime(accounts.Vault.Root);InstanceRules.Normalize(service.Data);}
        public string Home(DesktopInstance instance)
        {return instance.IsLocal?service.Settings.CodexHome:InstancePaths.Home(service.Vault.Root,instance.Id);}
        public string ActiveKey(DesktopInstance instance)
        {
            try {CodexEnvironment.CheckFileStorage(Home(instance));return AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(Home(instance),"auth.json"))).Key;} catch {return null;}
        }
        public DesktopInstance Create(string name)
        {
            name=(name??"").Trim();
            if(name.Length==0 || name.Length>70) throw new InvalidOperationException("Choisissez un nom de 1 à 70 caractères.");
            if(service.Data.Instances.Any(i=>!i.Archived && String.Equals(i.Name,name,StringComparison.CurrentCultureIgnoreCase))) throw new InvalidOperationException("Ce nom est déjà utilisé par une instance.");
            var instance=new DesktopInstance {Id=Guid.NewGuid().ToString("N"),Name=name};
            string folder=InstancePaths.Folder(service.Vault.Root,instance.Id);
            if(Directory.Exists(folder)) throw new IOException("Dossier d'instance déjà présent.");
            SafeFiles.PrivateDirectory(folder);SafeFiles.PrivateDirectory(Home(instance));SafeFiles.PrivateDirectory(InstancePaths.UserData(service.Vault.Root,instance.Id));
            // Fresh profiles use lightweight coding features. Runtime downloads remain an explicit Codex setting.
            SafeFiles.AtomicWrite(Path.Combine(Home(instance),"config.toml"),Encoding.UTF8.GetBytes("cli_auth_credentials_store = \"file\"\ncheck_for_update_on_startup = false\n[analytics]\nenabled = false\n[features]\nworkspace_dependencies = false\n"));
            service.Data.Instances.Add(instance);
            try {service.Save();} catch {service.Data.Instances.Remove(instance);throw;}
            return instance;
        }
        public void Rename(DesktopInstance instance,string name)
        {
            name=(name??"").Trim();if(name.Length==0 || name.Length>70) throw new InvalidOperationException("Nom d'instance non valide.");
            if(service.Data.Instances.Any(i=>i!=instance && !i.Archived && String.Equals(i.Name,name,StringComparison.CurrentCultureIgnoreCase))) throw new InvalidOperationException("Ce nom est déjà utilisé.");
            string before=instance.Name;instance.Name=name;try{service.Save();}catch{instance.Name=before;throw;}
        }
        public void Archive(DesktopInstance instance,bool archived)
        {
            if(instance.IsLocal) throw new InvalidOperationException("La session habituelle ne peut pas être archivée.");
            if(Runtime.Probe(instance).Running) throw new InvalidOperationException("Fermez cette instance avant de l'archiver.");
            bool before=instance.Archived;instance.Archived=archived;try{service.Save();}catch{instance.Archived=before;throw;}
        }
        public void SetScope(Profile profile,bool all,IEnumerable<string> ids)
        {
            var selected=ids.Distinct().ToList();
            if(!all&&service.Data.Instances.Any(i=>i.AccountKey==profile.Key&&!selected.Contains(i.Id)))throw new InvalidOperationException("Un compte lié à une instance ne peut pas en être dissocié, même lorsque l'instance est fermée ou archivée.");
            if(selected.Any(id=>!service.Data.Instances.Any(i=>i.Id==id))) throw new InvalidOperationException("Une instance sélectionnée n'existe plus.");
            foreach(var instance in service.Data.Instances.Where(i=>!i.Archived))
                if(!all && !selected.Contains(instance.Id) && ActiveKey(instance)==profile.Key && Runtime.Probe(instance).Running)
                    throw new InvalidOperationException("Le compte est utilisé dans « "+instance.Name+" ». Fermez cette instance avant de retirer son association.");
            bool beforeAll=profile.AllInstances;var beforeIds=profile.InstanceIds;
            profile.AllInstances=all;profile.InstanceIds=selected;
            try{service.Save();}catch{profile.AllInstances=beforeAll;profile.InstanceIds=beforeIds;throw;}
        }
        public Profile Capture(DesktopInstance instance)
        {
            CodexEnvironment.CheckFileStorage(Home(instance));
            string auth=SafeFiles.ReadText(Path.Combine(Home(instance),"auth.json"));
            var identity=AuthIdentity.Parse(auth);
            if(instance.AccountLocked&&instance.AccountKey!=identity.Key)throw new InvalidOperationException("Le compte connecté ne correspond plus au compte permanent de « "+instance.Name+" ». Reconnectez son compte d'origine dans Codex.");
            bool exists=service.Data.Profiles.Any(p=>p.Key==identity.Key);
            var profile=service.Import(auth,null);
            if(!exists) {profile.AllInstances=false;profile.InstanceIds=new List<string>{instance.Id};}
            else if(!profile.Allows(instance.Id)) profile.InstanceIds.Add(instance.Id);
            instance.AccountKey=profile.Key;instance.AccountLocked=true;service.Save();return profile;
        }
        public void SyncFreshAuth(Profile profile)
        {
            // Copies only newer credentials for the same identity into the encrypted vault.
            // Never writes to the files of an open Codex instance.
            var candidates=new List<Tuple<DateTime,string>>();
            foreach(var instance in service.Data.Instances.Where(i=>!i.Archived && profile.Allows(i.Id))) {
                try {
                    string path=Path.Combine(Home(instance),"auth.json");
                    if(!File.Exists(path)) continue;
                    string auth=SafeFiles.ReadText(path);
                    if(AuthIdentity.Parse(auth).Key==profile.Key) candidates.Add(Tuple.Create(AuthTime(auth,File.GetLastWriteTimeUtc(path)),auth));
                } catch { }
            }
            var latest=candidates.OrderByDescending(x=>x.Item1).FirstOrDefault();
            if(latest!=null && latest.Item1>=AuthTime(profile.AuthJson,DateTime.MinValue)) profile.AuthJson=latest.Item2;
        }
        private static DateTime AuthTime(string auth,DateTime fallback)
        {
            try {DateTime value;string at=Json.Str(Json.Get(Json.Read<object>(auth),"last_refresh"));return DateTime.TryParse(at,null,System.Globalization.DateTimeStyles.RoundtripKind,out value)?value.ToUniversalTime():fallback;}catch{return fallback;}
        }
        public bool IsResetActive(Profile profile)
        {
            return service.Data.Instances.Any(i=>!i.Archived && profile.Allows(i.Id) && ActiveKey(i)==profile.Key && (i.IsLocal || IsRunningKnown(i)));
        }
        private bool IsRunningKnown(DesktopInstance instance) {var state=Runtime.Probe(instance);return state.Running && state.Phase!="unknown";}
        public List<Profile> ActiveProfiles() {return service.Data.Profiles.Where(IsResetActive).ToList();}
        public async Task SelectAccount(DesktopInstance instance,Profile profile,CancellationToken token)
        {
            InstanceRules.RequireAvailable(profile,instance);
            if(Runtime.Probe(instance).Running) throw new InvalidOperationException("Fermez « "+instance.Name+" » avant de changer son compte. Les autres instances peuvent rester ouvertes.");
            SyncFreshAuth(profile);
            (await service.FetchUsage(profile.AuthJson,token)).Apply(profile,DateTime.UtcNow);
            token.ThrowIfCancellationRequested();
            Configure(instance,profile);
        }
        // Synchronous transaction shared with offline safety tests; the UI validates the account first.
        internal void Configure(DesktopInstance instance,Profile profile)
        {
            InstanceRules.RequireAvailable(profile,instance);
            if(instance.IsLocal) {
                new SwitchTransaction(Home(instance),CodexEnvironment.ClientsRunning).Execute(profile.AuthJson,previous=>{service.Data.PreviousAuthJson=previous;service.Save();},delegate{});
                instance.AccountKey=profile.Key;instance.AccountLocked=true;service.Save();return;
            }
            using(var lease=new InstanceLease(InstancePaths.Folder(service.Vault.Root,instance.Id))) {
                new SwitchTransaction(Home(instance),()=>Runtime.Probe(instance).Running).Execute(profile.AuthJson,previous=>{instance.PreviousAuthJson=previous;service.Save();},delegate{});
                instance.AccountKey=profile.Key;instance.AccountLocked=true;service.Save();
            }
        }
        public async Task Start(DesktopInstance instance,CancellationToken token)
        {
            if(instance.Archived) throw new InvalidOperationException("Restaurez l'instance avant de l'ouvrir.");
            if(instance.IsLocal) throw new InvalidOperationException("Ouvrez la session habituelle avec son raccourci Codex.");
            string key=ActiveKey(instance);
            if(!instance.AccountLocked&&key!=null)Capture(instance);
            if(instance.AccountLocked&&key!=instance.AccountKey)throw new InvalidOperationException("Connexion absente ou différente du compte permanent. Reconnectez le compte d'origine ; aucune connexion n'a été remplacée.");
            if(key!=null) {
                var profile=service.Data.Profiles.FirstOrDefault(p=>p.Key==key);
                if(profile==null) throw new InvalidOperationException("Importez d'abord le compte configuré dans cette instance avec « Importer sa connexion ».");
                InstanceRules.RequireAvailable(profile,instance);
                if(Runtime.Probe(instance).Running) throw new InvalidOperationException("Cette instance est déjà ouverte.");
                SyncFreshAuth(profile);
                if(SafeFiles.ReadText(Path.Combine(Home(instance),"auth.json"))!=profile.AuthJson) Configure(instance,profile);
            } else if(File.Exists(Path.Combine(Home(instance),"auth.json"))) throw new InvalidOperationException("La connexion de cette instance n'est pas reconnue. Choisissez un compte valide avant de l'ouvrir.");
            var relay=new RelayStore(Path.Combine(service.Vault.Root,"relay"));
            if(RelayPolicies.Load(relay).AutoInstallManaged)await RelayIntegration.Install(relay,Home(instance),service.Settings.CodexExecutable,false,token);
            await Runtime.Start(instance,token);
        }
        public void Forget(Profile profile)
        {
            if(service.Data.Instances.Any(i=>i.AccountKey==profile.Key))throw new InvalidOperationException("Ce compte est lié à une instance. Son association et ses conversations sont conservées.");
            service.Data.Profiles.Remove(profile);
            foreach(var instance in service.Data.Instances) {
                if(instance.AccountKey==profile.Key) instance.AccountKey=null;
                if(SameAuth(instance.PreviousAuthJson,profile.Key)) instance.PreviousAuthJson=null;
            }
            if(SameAuth(service.Data.PreviousAuthJson,profile.Key)) service.Data.PreviousAuthJson=null;
            service.Save();
        }
        private static bool SameAuth(string auth,string key) {try{return auth!=null && AuthIdentity.Parse(auth).Key==key;}catch{return false;}}
        // Adopt legacy profiles once. A detected identity change never replaces a locked binding.
        public void AdoptAccounts()
        {
            foreach(var i in service.Data.Instances.Where(i=>!i.Archived&&!i.AccountLocked)) {
                string key=ActiveKey(i);
                if(key!=null)Capture(i);
            }
        }
        public void BindAccount(DesktopInstance instance,Profile profile)
        {
            if(instance.AccountLocked&&instance.AccountKey!=profile.Key)throw new InvalidOperationException("Cette instance possède déjà son compte permanent.");
            var previous=profile.InstanceIds.ToList();
            if(!profile.Allows(instance.Id))profile.InstanceIds.Add(instance.Id);
            try{Configure(instance,profile);}catch{profile.InstanceIds=previous;throw;}
        }
    }
}
