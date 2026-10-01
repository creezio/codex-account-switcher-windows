using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

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
            Directory.SetAccessControl(path, security);
        }
        public static void AtomicWrite(string path, byte[] data)
        {
            RejectLinks(path);
            string temp = path + ".switcher-tmp";
            RejectLinks(temp);
            bool created = false;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { created = true; stream.Write(data, 0, data.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (created && File.Exists(temp)) File.Delete(temp); }
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
                if (Directory.Exists(item)) DeleteOwnedTree(root, item); else File.Delete(item);
            }
            Directory.Delete(target);
        }
    }
    internal sealed class Vault
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Creezio.CodexAccountSwitcher.v1");
        public readonly string Root;
        public string FilePath { get { return Path.Combine(Root, "accounts.dpapi"); } }
        public Vault(string root) { Root = Path.GetFullPath(root); SafeFiles.PrivateDirectory(Root); }
        public VaultData Load()
        {
            if (!File.Exists(FilePath)) return new VaultData();
            SafeFiles.RejectLinks(FilePath);
            if (new FileInfo(FilePath).Length > 16777216) throw new IOException("Coffre trop volumineux.");
            byte[] clear = null;
            try
            {
                clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                var data = Json.Read<VaultData>(Encoding.UTF8.GetString(clear));
                if (data == null || (data.Version != 1 && data.Version != 2) || data.Profiles == null) throw new InvalidDataException();
                foreach (var profile in data.Profiles) if (AuthIdentity.Parse(profile.AuthJson).Key != profile.Key) throw new InvalidDataException();
                InstanceRules.Normalize(data);
                return data;
            }
            catch { throw new InvalidOperationException("Impossible d'ouvrir le coffre. Il doit être lu par le même utilisateur Windows. Le fichier existant est conservé."); }
            finally { if (clear != null) Array.Clear(clear, 0, clear.Length); }
        }
        public void Save(VaultData data)
        {
            InstanceRules.Normalize(data);
            // Keep the encrypted v1 vault once; older versions refuse the new schema.
            if(File.Exists(FilePath) && !File.Exists(Path.Combine(Root,"accounts-before-instances.dpapi")))
            {
                SafeFiles.RejectLinks(FilePath);
                SafeFiles.AtomicWrite(Path.Combine(Root,"accounts-before-instances.dpapi"),File.ReadAllBytes(FilePath));
            }
            byte[] clear = Encoding.UTF8.GetBytes(Json.Write(data));
            try { SafeFiles.AtomicWrite(FilePath, ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(clear, 0, clear.Length); }
        }
        public Settings LoadSettings()
        {
            string path = Path.Combine(Root, "settings.json");
            if (!File.Exists(path)) return new Settings();
            try { return Json.Read<Settings>(SafeFiles.ReadText(path)) ?? new Settings(); }
            catch { throw new InvalidOperationException("Les paramètres ne peuvent pas être lus. Le fichier existant est conservé."); }
        }
        public void SaveSettings(Settings settings) { SafeFiles.AtomicWrite(Path.Combine(Root, "settings.json"), Encoding.UTF8.GetBytes(Json.Write(settings))); }
    }
}
