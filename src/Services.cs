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
        public AccountService(string root)
        {
            Vault=new Vault(root); Data=Vault.Load(); Settings=Vault.LoadSettings();
            if(String.IsNullOrWhiteSpace(Settings.CodexHome)) Settings.CodexHome=CodexEnvironment.DefaultHome();
            if(String.IsNullOrWhiteSpace(Settings.CodexExecutable) || !File.Exists(Settings.CodexExecutable)) Settings.CodexExecutable=CodexEnvironment.FindExecutable();
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
                return Import(auth,null);
            }
        }
        public async Task<List<QuotaBucket>> FetchQuotas(string auth, CancellationToken token)
        {
            RequireExecutable(); var identity=AuthIdentity.Parse(auth);
            using(var rpc=new RpcClient(Settings.CodexExecutable,Vault.Root))
            {
                await rpc.Initialize(token);
                // External-token mode avoids rotating the refresh token copied from a running client.
                await rpc.Call("account/login/start",new { type="chatgptAuthTokens",accessToken=identity.AccessToken,chatgptAccountId=identity.AccountId,chatgptPlanType=String.IsNullOrEmpty(identity.Plan)?null:identity.Plan },token);
                var result=await rpc.Call("account/rateLimits/read",null,token);
                var quotas=Quotas.Parse(result);
                if(quotas.Count==0) throw new InvalidOperationException("Les quotas ne sont pas disponibles pour ce compte.");
                return quotas;
            }
        }
        public async Task Refresh(Profile profile, CancellationToken token)
        {
            try
            {
                // Reuse newly refreshed local tokens without touching the active auth file.
                string path=Path.Combine(Settings.CodexHome,"auth.json");
                if(ActiveKey()==profile.Key) profile.AuthJson=SafeFiles.ReadText(path);
                profile.Quotas=await FetchQuotas(profile.AuthJson,token);
                profile.QuotaTimeUtc=DateTime.UtcNow.ToString("o"); profile.Error=null;
            }
            catch(OperationCanceledException) { throw; }
            catch { profile.Error="Connexion expirée, réseau indisponible ou version Codex incompatible. Reconnectez ce compte ou réessayez."; }
            Save();
        }
        public async Task Switch(Profile profile, CancellationToken token)
        {
            CodexEnvironment.CheckFileStorage(Settings.CodexHome);
            if(CodexEnvironment.ClientsRunning()) throw new InvalidOperationException("Fermez complètement Codex/ChatGPT et ses terminaux Codex, puis réessayez. La bascule ne ferme aucune application.");
            // The authenticated service must accept the selected account before any local change.
            profile.Quotas=await FetchQuotas(profile.AuthJson,token);
            profile.QuotaTimeUtc=DateTime.UtcNow.ToString("o"); profile.Error=null;
            token.ThrowIfCancellationRequested();
            var transaction=new SwitchTransaction(Settings.CodexHome,CodexEnvironment.ClientsRunning);
            transaction.Execute(profile.AuthJson, previous => {
                Data.PreviousAuthJson=previous;
                if(previous!=null) { try { Import(previous,null); } catch(InvalidOperationException) { /* API-key backup remains encrypted as-is. */ } }
                Save();
            }, delegate {});
        }
        public void RestorePrevious()
        {
            if(String.IsNullOrEmpty(Data.PreviousAuthJson)) throw new InvalidOperationException("Aucun compte précédent dans le coffre.");
            string previous=Data.PreviousAuthJson;
            new SwitchTransaction(Settings.CodexHome,CodexEnvironment.ClientsRunning,true).Execute(previous, current=> {Data.PreviousAuthJson=current; Save();},delegate {});
        }
    }
}
