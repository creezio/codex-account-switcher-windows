using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Creezio.Switcher
{
    internal static class SafeFiles
    {
        public static void RejectLinks(string path)
        {
            string cursor = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Un lien ou une jonction empêche l'accès sécurisé au dossier.");
                cursor = Path.GetDirectoryName(cursor);
            }
        }
        public static void PrivateDirectory(string path)
        {
            RejectLinks(path);
            Directory.CreateDirectory(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
#if NETCOREAPP
            new DirectoryInfo(path).SetAccessControl(security);
#else
            Directory.SetAccessControl(path, security);
#endif
        }
        public static void AtomicWrite(string path, byte[] data)
        {
            AtomicFiles.Write(path,data);
        }
        public static string ReadText(string path)
        {
            RejectLinks(path);
            if (new FileInfo(path).Length > 4194304) throw new IOException("Fichier trop volumineux.");
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new StreamReader(stream,Encoding.UTF8)) return reader.ReadToEnd();
        }
        public static void DeleteOwnedTree(string root, string target)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            target = Path.GetFullPath(target);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || target.TrimEnd(Path.DirectorySeparatorChar) == root.TrimEnd(Path.DirectorySeparatorChar)) throw new IOException("Suppression hors du dossier de travail refusée.");
            if (!Directory.Exists(target)) return;
            RejectLinks(target);
            // Inspect each child before descending; never follow reparse points.
            foreach (string item in Directory.GetFileSystemEntries(target))
            {
                RejectLinks(item);
                if (Directory.Exists(item)) DeleteOwnedTree(root, item); else {var attributes=File.GetAttributes(item);if((attributes&FileAttributes.ReadOnly)!=0)File.SetAttributes(item,attributes&~FileAttributes.ReadOnly);File.Delete(item);}
            }
            Directory.Delete(target);
        }
    }
    internal sealed class Vault
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Creezio.CodexAccountSwitcher.v1");
        public readonly string Root;
        private byte[] baseline;
        public string FilePath { get { return Path.Combine(Root, "accounts.dpapi"); } }
        public Vault(string root) { Root = Path.GetFullPath(root); SafeFiles.PrivateDirectory(Root); baseline=Digest(ReadBytes()); }
        private byte[] ReadBytes()
        {
            // File.Exists can report a false absence during an atomic replace.
            // Only a vault never observed by this object may be initially absent.
            using(var file=AtomicFiles.OpenRead(FilePath,baseline==null,true,path=>new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))){
                if(file==null)return null;
                if(file.Length>16777216)throw new IOException("Coffre trop volumineux.");var bytes=new byte[(int)file.Length];int offset=0;while(offset<bytes.Length){int read=file.Read(bytes,offset,bytes.Length-offset);if(read==0)throw new IOException("Lecture du coffre incomplete.");offset+=read;}return bytes;
            }
        }
        private static byte[] Digest(byte[] bytes){if(bytes==null)return null;using(var sha=SHA256.Create())return sha.ComputeHash(bytes);}
        private static bool Equal(byte[] first,byte[] second)
        {if(first==null||second==null)return first==null&&second==null;if(first.Length!=second.Length)return false;int difference=0;for(int i=0;i<first.Length;i++)difference|=first[i]^second[i];return difference==0;}
        private sealed class AtomicVaultLease : IDisposable
        {
            private readonly Mutex mutex;private bool held;
            internal AtomicVaultLease(string path)
            {
                string key;using(var sha=SHA256.Create())key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-","");mutex=new Mutex(false,"Local\\Creezio.AtomicWrite."+key);
                try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){held=true;}if(!held)throw new IOException("Une autre ecriture du coffre est en cours. Actualisez puis reessayez.");}catch{mutex.Dispose();throw;}
            }
            public void Dispose(){if(held){held=false;mutex.ReleaseMutex();}mutex.Dispose();}
        }
        public VaultData Load()
        {
            var encrypted=ReadBytes();if(encrypted==null){baseline=null;return new VaultData();}
            byte[] clear = null;
            try
            {
                clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                var data = Json.Read<VaultData>(Encoding.UTF8.GetString(clear));
                if (data == null || (data.Version != 1 && data.Version != 2) || data.Profiles == null) throw new InvalidDataException();
                foreach (var profile in data.Profiles) if (AuthIdentity.Parse(profile.AuthJson).Key != profile.Key) throw new InvalidDataException();
                InstanceRules.Normalize(data);
                baseline=Digest(encrypted);
                return data;
            }
            catch { throw new InvalidOperationException("Impossible d'ouvrir le coffre. Il doit être lu par le même utilisateur Windows. Le fichier existant est conservé."); }
            finally { if (clear != null) Array.Clear(clear, 0, clear.Length); }
        }
        public void Save(VaultData data)
        {
            using(VaultMutation.AcquireWrite(Root))using(new AtomicVaultLease(FilePath)){
                byte[] current=ReadBytes();if(!Equal(baseline,Digest(current)))throw new InvalidOperationException("Le coffre a ete modifie depuis sa lecture. Actualisez avant toute nouvelle ecriture ; aucun compte n'a ete remplace.");
                InstanceRules.Normalize(data);
                // Keep the encrypted v1 vault once; never create this backup from
                // an obsolete snapshot or reread different bytes after the CAS.
                if(current!=null&&!File.Exists(Path.Combine(Root,"accounts-before-instances.dpapi")))SafeFiles.AtomicWrite(Path.Combine(Root,"accounts-before-instances.dpapi"),current);
                byte[] clear=Encoding.UTF8.GetBytes(Json.Write(data));
                try{byte[] encrypted=ProtectedData.Protect(clear,Entropy,DataProtectionScope.CurrentUser);SafeFiles.AtomicWrite(FilePath,encrypted);baseline=Digest(encrypted);}
                finally{Array.Clear(clear,0,clear.Length);}
            }
        }
        public Settings LoadSettings()
        {
            string path = Path.Combine(Root, "settings.json");
            if (!File.Exists(path)) return new Settings{AutoResetCredits=false};
            try { return Json.Read<Settings>(SafeFiles.ReadText(path)) ?? new Settings(); }
            catch { throw new InvalidOperationException("Les paramètres ne peuvent pas être lus. Le fichier existant est conservé."); }
        }
        public void SaveSettings(Settings settings) { SafeFiles.AtomicWrite(Path.Combine(Root, "settings.json"), Encoding.UTF8.GetBytes(Json.Write(settings))); }
    }
}
