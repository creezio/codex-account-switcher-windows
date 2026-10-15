using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Creezio.Switcher
{
    internal static class HeadlessAccounts
    {
        private static CloudBackendException Authentication(){return new CloudBackendException("authentication","La connexion de ce compte doit être actualisée dans Codex. Aucun autre compte ni aucune application ouverte n'a été modifié.");}
        internal static AuthIdentity Read(AccountService accounts,DesktopInstance instance,bool forceRefresh,CancellationToken token)
        {
            if(accounts==null||instance==null||String.IsNullOrEmpty(instance.AccountKey))throw Authentication();
            string root=accounts.Vault.Root,id=instance.Id,key=instance.AccountKey,home=Path.GetFullPath(accounts.Instances.Home(instance));
            Func<DesktopInstance> target=delegate{
                var current=new AccountService(root);var found=current.Data.Instances.SingleOrDefault(value=>value.Id==id&&!value.Archived);
                if(found==null||found.AccountKey!=key||!RelayStore.SamePath(home,current.Instances.Home(found)))throw Authentication();return found;
            };
            Func<AuthIdentity> read=delegate{target();return HeadlessRpc.ReadProfile(home,key);};
            Func<bool> open=delegate{var current=new AccountService(root);return current.Instances.Runtime.Probe(target()).Running;};
            Func<IDisposable> lease=delegate{
                // The managed GUI launcher and its host hold this same lease. The
                // ordinary Windows primary-app launcher does not, so never rotate
                // its tokens silently without a proven coordination mechanism.
                if(target().IsLocal)throw Authentication();
                return new InstanceLease(InstancePaths.Folder(root,id));
            };
            return ReadCore(Path.Combine(root,"headless-auth-"+Key(home)+".lock"),key,read,open,lease,()=>new HeadlessRpc(accounts.Settings.CodexExecutable,home,key){AllowAccountRefresh=true},forceRefresh,token);
        }
        internal static AuthIdentity ReadCore(string lockPath,string expectedKey,Func<AuthIdentity> read,Func<bool> guiOpen,Func<IDisposable> profileLease,Func<IHeadlessRpc> connect,bool forceRefresh,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var original=Verified(read,expectedKey);
            if(!forceRefresh&&Remaining(original)>120)return original;
            if(guiOpen())return OpenProfile(read,expectedKey,original,forceRefresh);
            using(Acquire(lockPath,token)){
                token.ThrowIfCancellationRequested();var current=Verified(read,expectedKey);
                if(Remaining(current)>0&&current.AccessToken!=original.AccessToken)return current;
                if(!forceRefresh&&Remaining(current)>120)return current;
                if(guiOpen())return OpenProfile(read,expectedKey,current,forceRefresh);
                IDisposable lease;
                try{lease=profileLease();}
                catch{current=Verified(read,expectedKey);if(!forceRefresh&&Remaining(current)>0)return current;throw Authentication();}
                using(lease){
                    current=Verified(read,expectedKey);
                    if(guiOpen())return OpenProfile(read,expectedKey,current,forceRefresh);
                    try{
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
                            timeout.CancelAfter(TimeSpan.FromSeconds(35));
                            using(var rpc=connect())rpc.Call("account/read",new{refreshToken=true},timeout.Token).GetAwaiter().GetResult();
                        }
                    }catch(OperationCanceledException){throw;}
                    catch(HeadlessRpcException error){
                        if(error.Code!="engine-busy")throw Authentication();
                        var active=Verified(read,expectedKey);if(!forceRefresh&&Remaining(active)>0)return active;
                        throw new CloudBackendException("authentication-busy","Un moteur Codex de ce profil est déjà actif. L'actualisation attend son propriétaire ; aucun autre moteur n'a été lancé.");
                    }
                    catch{throw Authentication();}
                    var renewed=Verified(read,expectedKey);
                    if(Remaining(renewed)<=0)throw Authentication();
                    if(forceRefresh&&renewed.AccessToken==original.AccessToken)throw Authentication();
                    return renewed;
                }
            }
        }
        private static AuthIdentity OpenProfile(Func<AuthIdentity> read,string key,AuthIdentity previous,bool force)
        {var latest=Verified(read,key);if(Remaining(latest)>0&&(!force||latest.AccessToken!=previous.AccessToken))return latest;throw Authentication();}
        private static AuthIdentity Verified(Func<AuthIdentity> read,string key)
        {try{var value=read();if(value==null||value.Key!=key||String.IsNullOrEmpty(value.AccountId))throw Authentication();return value;}catch{throw Authentication();}}
        internal static double Remaining(AuthIdentity identity)
        {
            try{
                string[] parts=identity.AccessToken.Split('.');if(parts.Length!=3)return Double.NegativeInfinity;
                string part=parts[1].Replace('-','+').Replace('_','/');part=part.PadRight((part.Length+3)/4*4,'=');
                var claims=Json.Read<object>(new UTF8Encoding(false,true).GetString(Convert.FromBase64String(part)));
                var auth=Json.Get(claims,"https://api.openai.com/auth");
                if(Json.Str(Json.Get(auth,"chatgpt_account_id"))!=identity.AccountId||String.IsNullOrEmpty(Json.Str(Json.Get(auth,"chatgpt_user_id"))))return Double.NegativeInfinity;
                double? expiry=Json.Number(Json.Get(claims,"exp"));return expiry.HasValue&&!Double.IsNaN(expiry.Value)&&!Double.IsInfinity(expiry.Value)?expiry.Value-(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds:Double.NegativeInfinity;
            }catch{return Double.NegativeInfinity;}
        }
        private static IDisposable Acquire(string path,CancellationToken token)
        {
            SafeFiles.RejectLinks(Path.GetDirectoryName(Path.GetFullPath(path)));var end=DateTime.UtcNow.AddSeconds(30);
            do{
                token.ThrowIfCancellationRequested();
                try{SafeFiles.RejectLinks(path);return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException error){if((error.HResult&0xffff)!=32&&(error.HResult&0xffff)!=33)throw Authentication();}
                if(token.WaitHandle.WaitOne(25))token.ThrowIfCancellationRequested();
            }while(DateTime.UtcNow<end);
            throw new CloudBackendException("authentication-busy","Une actualisation de ce profil est déjà en cours. Réessayez sans ouvrir une autre connexion.");
        }
        private static string Key(string home){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(home).TrimEnd('\\').ToUpperInvariant()))).Replace("-","").ToLowerInvariant();}
    }
}
