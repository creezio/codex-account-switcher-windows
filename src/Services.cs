using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class CodexEnvironment
    {
        public static string DefaultHome()
        {
            string configured=Environment.GetEnvironmentVariable("CODEX_HOME");
            return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : Path.GetFullPath(configured);
        }
        public static string FindExecutable()
        {
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { string path=Path.Combine(entry.Trim('"'), "codex.exe"); if(File.Exists(path)) return path; } catch { }
            }
            string bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bundled))
            {
                var candidates=Directory.GetDirectories(bundled).Select(d => Path.Combine(d,"codex.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc);
                var first=candidates.FirstOrDefault(); if(first!=null) return first;
            }
            return "";
        }
        public static bool ClientsRunning()
        {
            foreach(var process in Process.GetProcesses())
            {
                using(process)
                {
                    try {
                        string name=process.ProcessName;
                        if (name.Equals("codex",StringComparison.OrdinalIgnoreCase) || name.StartsWith("codex-",StringComparison.OrdinalIgnoreCase) || name.Equals("ChatGPT",StringComparison.OrdinalIgnoreCase)) return true;
                    } catch { return true; } // A failed inspection must never enable a switch.
                }
            }
            return false;
        }
        public static void CheckFileStorage(string home)
        {
            SafeFiles.RejectLinks(home);
            string config=Path.Combine(home,"config.toml");
            if (!File.Exists(config)) return;
            foreach (string raw in SafeFiles.ReadText(config).Split('\n'))
            {
                string line=raw.Trim();
                if (line.StartsWith("[")) break; // Only root-level settings apply.
                if (Regex.IsMatch(line,"^(?:cli_auth_credentials_store|\"cli_auth_credentials_store\"|'cli_auth_credentials_store')\\s*="))
                {
                    if (!Regex.IsMatch(line, "^(?:cli_auth_credentials_store|\"cli_auth_credentials_store\"|'cli_auth_credentials_store')\\s*=\\s*(?:\"file\"|'file')\\s*(?:#.*)?$"))
                        throw new InvalidOperationException("La bascule nécessite le stockage Codex « file ». Les modes keyring, auto et ephemeral ne sont pas pris en charge dans cette version. Votre configuration reste inchangée.");
                }
            }
        }
    }

    internal sealed class SwitchTransaction
    {
        private readonly string home;
        private readonly Func<bool> clientsRunning;
        private readonly bool allowApiKeyRestore;
        public SwitchTransaction(string targetHome, Func<bool> busy, bool allowApiKey=false) { home=targetHome; clientsRunning=busy; allowApiKeyRestore=allowApiKey; }
        private string Validate(string auth)
        {
            if(allowApiKeyRestore)
            {
                var root=Json.Read<Dictionary<string,object>>(auth);
                string mode=Json.Str(Json.Get(root,"auth_mode"));
                if((mode=="apikey" || mode=="") && !String.IsNullOrEmpty(Json.Str(Json.Get(root,"OPENAI_API_KEY")))) return "api-key-backup";
            }
            return AuthIdentity.Parse(auth).Key;
        }
        public void Execute(string targetAuth, Action<string> persistBackup, Action verifyAfterWrite)
        {
            string expected=Validate(targetAuth);
            CodexEnvironment.CheckFileStorage(home);
            if(clientsRunning()) throw new InvalidOperationException("Fermez complètement Codex/ChatGPT et ses terminaux Codex, puis réessayez. Aucune tâche n'a été interrompue.");
            if(!Directory.Exists(home)) throw new InvalidOperationException("Le dossier Codex n'existe pas. Vérifiez les paramètres.");
            string target=Path.Combine(home,"auth.json");
            string previous=File.Exists(target) ? SafeFiles.ReadText(target) : null;
            // Preserve the exact previous bytes, even when the current mode is an API key.
            byte[] previousBytes=File.Exists(target) ? File.ReadAllBytes(target) : null;
            persistBackup(previous);
            if(clientsRunning()) throw new InvalidOperationException("Codex vient de démarrer. La bascule a été annulée.");
            byte[] written=Encoding.UTF8.GetBytes(targetAuth);
            bool replaced=false;
            try
            {
                // Recheck the on-disk state immediately before the atomic replacement.
                if (previousBytes == null ? File.Exists(target) : !File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(previousBytes))
                    throw new IOException("La connexion a changé pendant la préparation. Réessayez.");
                SafeFiles.AtomicWrite(target,written); replaced=true;
                if(Validate(SafeFiles.ReadText(target))!=expected) throw new IOException("L'identité enregistrée ne correspond pas au compte demandé.");
                verifyAfterWrite();
                if(clientsRunning()) throw new IOException("Codex a démarré pendant la bascule. Fermez-le puis réessayez.");
            }
            catch
            {
                if(replaced)
                {
                    // Do not overwrite credentials concurrently changed by another application.
                    if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(written))
                        throw new IOException("La connexion a été modifiée par une autre application. La sauvegarde précédente reste dans le coffre ; aucune restauration n'a été forcée.");
                    if(previousBytes!=null) SafeFiles.AtomicWrite(target,previousBytes); else File.Delete(target);
                }
                throw;
            }
        }
    }

    internal sealed class AccountService
    {
        public readonly Vault Vault;
        public VaultData Data;
        public Settings Settings;
        public readonly InstanceManager Instances;
        private readonly ResetController resets=new ResetController();
        public AccountService(string root)
        {
            Vault=new Vault(root); Data=Vault.Load(); Settings=Vault.LoadSettings();
            if(String.IsNullOrWhiteSpace(Settings.CodexHome)) Settings.CodexHome=CodexEnvironment.DefaultHome();
            if(String.IsNullOrWhiteSpace(Settings.CodexExecutable) || !File.Exists(Settings.CodexExecutable)) Settings.CodexExecutable=CodexEnvironment.FindExecutable();
            Instances=new InstanceManager(this);
        }
        public void Save() { Vault.Save(Data); }
        public Profile Import(string auth, string label)
        {
            var identity=AuthIdentity.Parse(auth);
            var profile=Data.Profiles.FirstOrDefault(p=>p.Key==identity.Key);
            bool isNew=profile==null;
            if(isNew) profile=new Profile {Key=identity.Key, Label=String.IsNullOrWhiteSpace(label)?identity.Email:label.Trim()};
            profile.Email=identity.Email; profile.Plan=identity.Plan; profile.AuthJson=auth; profile.Error=null;
            if(isNew) Data.Profiles.Add(profile);
            Save(); return profile;
        }
        public Profile ImportCurrent()
        {
            CodexEnvironment.CheckFileStorage(Settings.CodexHome);
            string path=Path.Combine(Settings.CodexHome,"auth.json");
            if(!File.Exists(path)) throw new InvalidOperationException("Aucune connexion dans le fichier auth.json de ce dossier. Utilisez « Ajouter un compte » ou choisissez le dossier Codex dans les paramètres.");
            return Import(SafeFiles.ReadText(path),null);
        }
        public string ActiveKey()
        {
            try { CodexEnvironment.CheckFileStorage(Settings.CodexHome); return AuthIdentity.Parse(SafeFiles.ReadText(Path.Combine(Settings.CodexHome,"auth.json"))).Key; }
            catch { return null; }
        }
        private void RequireExecutable()
        {
            if(!File.Exists(Settings.CodexExecutable) || !String.Equals(Path.GetExtension(Settings.CodexExecutable),".exe",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Codex CLI est introuvable. Sélectionnez codex.exe dans les paramètres. L'application de bureau Codex fournit généralement cet exécutable.");
        }
        public async Task<Profile> Login(Action<string> openUrl, CancellationToken token)
        { return Import(await LoginAuth(openUrl,token),null); }
        public async Task<string> LoginAuth(Action<string> openUrl, CancellationToken token)
        {
            RequireExecutable();
            using(var rpc=new RpcClient(Settings.CodexExecutable,Vault.Root))
            {
                await rpc.Initialize(token);
                var result=await rpc.Call("account/login/start",new {type="chatgpt"},token);
                string url=Json.Str(Json.Get(result,"authUrl"));
                Uri uri;
                if(!Uri.TryCreate(url,UriKind.Absolute,out uri) || uri.Scheme!="https" || uri.Host!="auth.openai.com") throw new InvalidOperationException("L'adresse de connexion renvoyée par Codex n'est pas reconnue.");
                openUrl(url);
                await rpc.WaitLogin(token);
                string auth=SafeFiles.ReadText(Path.Combine(rpc.Home,"auth.json"));
                return auth;
            }
        }
        private async Task<RpcClient> OpenUsageSession(string auth,CancellationToken token)
        {
            RequireExecutable(); var identity=AuthIdentity.Parse(auth);
            var rpc=new RpcClient(Settings.CodexExecutable,Vault.Root);
            try
            {
                await rpc.Initialize(token);
                // External-token mode avoids rotating the refresh token copied from a running client.
                await rpc.Call("account/login/start",new { type="chatgptAuthTokens",accessToken=identity.AccessToken,chatgptAccountId=identity.AccountId,chatgptPlanType=String.IsNullOrEmpty(identity.Plan)?null:identity.Plan },token);
                return rpc;
            }
            catch {rpc.Dispose();throw;}
        }
        private sealed class ResetGateway : IResetGateway
        {
            private readonly RpcClient rpc;
            public ResetGateway(RpcClient connection) {rpc=connection;}
            public async Task<UsageSnapshot> Read(CancellationToken token)
            {
                var result=await rpc.Call("account/rateLimits/read",null,token);
                var snapshot=UsageSnapshot.Parse(result);
                if(snapshot.Buckets.Count==0) throw new InvalidOperationException("Les quotas ne sont pas disponibles pour ce compte.");
                return snapshot;
            }
            public async Task<string> Consume(string key,string credit,CancellationToken token)
            {
                var parameters=new Dictionary<string,object>{{"idempotencyKey",key}};
                if(!String.IsNullOrEmpty(credit)) parameters["creditId"]=credit;
                var result=await rpc.Call("account/rateLimitResetCredit/consume",parameters,token);
                return Json.Str(Json.Get(result,"outcome"));
            }
        }
        public async Task<UsageSnapshot> FetchUsage(string auth,CancellationToken token)
        {
            using(var rpc=await OpenUsageSession(auth,token)) return await new ResetGateway(rpc).Read(token);
        }
        public async Task<List<QuotaBucket>> FetchQuotas(string auth, CancellationToken token)
        { return (await FetchUsage(auth,token)).Buckets; }

        public async Task Refresh(Profile profile, CancellationToken token,bool allowAutoReset=false)
        {
            await RefreshUsage(profile,()=>allowAutoReset&&Settings.AutoResetCredits&&Instances.IsResetActive(profile),Save,token);
        }
        internal async Task RefreshUsage(Profile profile,Func<bool> authorizeReset,Action persist,CancellationToken token,bool manual=false)
        {
            try
            {
                // Reuse newly refreshed local tokens without touching the active auth file.
                Instances.SyncFreshAuth(profile);
                using(var rpc=await OpenUsageSession(profile.AuthJson,token))
                {
                    var gateway=new ResetGateway(rpc);
                    (await gateway.Read(token)).Apply(profile,DateTime.UtcNow);
                    persist();
                    await resets.Run(profile,authorizeReset,gateway,persist,token,manual);
                }
            }
            catch(OperationCanceledException) { throw; }
            catch { profile.Error="Connexion expirée, réseau indisponible ou version Codex incompatible. Reconnectez ce compte ou réessayez."; }
            persist();
        }
        public async Task Switch(Profile profile, CancellationToken token)
        {
            await Instances.SelectAccount(Data.Instances.First(i=>i.IsLocal),profile,token);
        }
        public void RestorePrevious()
        {
            if(String.IsNullOrEmpty(Data.PreviousAuthJson)) throw new InvalidOperationException("Aucun compte précédent dans le coffre.");
            string previous=Data.PreviousAuthJson;
            var local=Data.Instances.First(i=>i.IsLocal);
            if(local.AccountLocked&&AuthIdentity.Parse(previous).Key!=local.AccountKey)throw new InvalidOperationException("La session habituelle est liée à son compte permanent. Utilisez une autre instance pour l'autre compte.");
            var matching=Data.Profiles.FirstOrDefault(p=>p.AuthJson==previous);
            if(matching!=null) InstanceRules.RequireAvailable(matching,Data.Instances.First(i=>i.IsLocal));
            new SwitchTransaction(Settings.CodexHome,CodexEnvironment.ClientsRunning,true).Execute(previous, current=> {Data.PreviousAuthJson=current; Save();},delegate {});
        }
    }
}
