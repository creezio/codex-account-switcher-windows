using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Creezio.Switcher
{
    internal static class AtomicFiles
    {
        private static readonly Regex Owner = new Regex("^([0-9a-f]{1,8})-([0-9a-f]{16})-([0-9a-f]{8})$", RegexOptions.CultureInvariant);
        private static string Prefix(string path)
        {using(var sha=SHA256.Create())return ".swt-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())),0,4).Replace("-","").ToLowerInvariant()+"-";}
        internal static string Temporary(string path, int pid, long started, string nonce)
        { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),Prefix(path)+pid.ToString("x", CultureInfo.InvariantCulture) + "-" + started.ToString("x16", CultureInfo.InvariantCulture) + "-" + nonce.Substring(0,8)); }
        internal static string Reservation(int pid,long started,string nonce)
        {return "CREEZIO_ATOMIC_V1:"+pid.ToString("x",CultureInfo.InvariantCulture)+"-"+started.ToString("x16",CultureInfo.InvariantCulture)+"-"+nonce.Substring(0,8);}
        private static string LockName(string path)
        { using(var sha=SHA256.Create()) return "Local\\Creezio.AtomicWrite." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", ""); }
        private static bool MayBeAlive(int pid,long started)
        {
            try { using(var p=Process.GetProcessById(pid)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks==started; }
            catch(ArgumentException) { return false; }
            catch { return true; } // An unreadable process is not proof of an orphan.
        }
        internal static int RecoverOwned(string path,Func<int,long,bool> alive=null)
        {
            path=Path.GetFullPath(path);SafeFiles.RejectLinks(path);
            string folder=Path.GetDirectoryName(path),prefix=Prefix(path);
            if(!Directory.Exists(folder))return 0;
            int recovered=0;
            foreach(string file in Directory.GetFiles(folder,prefix+"*",SearchOption.TopDirectoryOnly)) {
                var match=Owner.Match(Path.GetFileName(file).Substring(prefix.Length));int pid;long started;
                if(!match.Success||!Int32.TryParse(match.Groups[1].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pid)||pid<=0||!Int64.TryParse(match.Groups[2].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out started)||started<=0)continue;
                if((alive??MayBeAlive)(pid,started))continue;
                if(RemoveExclusive(file))recovered++;
            }
            return recovered;
        }
        private static bool RemoveExclusive(string path)
        {
            try {
                SafeFiles.RejectLinks(path);
                // Delete only this exact file while holding an exclusive handle, never an open writer's file.
                using(new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose)){}
                return true;
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        private static bool RecoverReservation(string path)
        {
            if(!File.Exists(path))return true;
            SafeFiles.RejectLinks(path);
            try {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Delete)) {
                    if(stream.Length>160)return false;
                    var bytes=new byte[(int)stream.Length];int read=0;
                    while(read<bytes.Length){int count=stream.Read(bytes,read,bytes.Length-read);if(count==0)return false;read+=count;}
                    string text=Encoding.UTF8.GetString(bytes,0,read),prefix="CREEZIO_ATOMIC_V1:";
                    if(!text.StartsWith(prefix,StringComparison.Ordinal))return false;
                    var match=Owner.Match(text.Substring(prefix.Length));int pid;long started;
                    if(!match.Success||!Int32.TryParse(match.Groups[1].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pid)||pid<=0||!Int64.TryParse(match.Groups[2].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out started)||started<=0)return false;
                    bool self;using(var current=Process.GetCurrentProcess())self=current.Id==pid&&current.StartTime.ToUniversalTime().Ticks==started;
                    if(!self&&MayBeAlive(pid,started))return false;
                    File.Delete(path);return true;
                }
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        // Legacy files have no owner metadata. Only a caller holding the instance lifetime lease may recover them.
        internal static void RecoverLegacyInstance(string folder)
        {
            SafeFiles.RejectLinks(folder);
            foreach(string name in new[]{"state.json","launch.json","stop.json","startup.json"}) {
                string temp=Path.Combine(folder,name)+".switcher-tmp";
                if(File.Exists(temp)) {
                    SafeFiles.RejectLinks(temp);
                    try{using(new FileStream(temp,FileMode.Open,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose)){}}
                    catch(FileNotFoundException){}
                }
            }
        }
        internal static void Write(string path,byte[] data)
        {
            path=Path.GetFullPath(path);SafeFiles.RejectLinks(path);
            using(var mutex=new Mutex(false,LockName(path))) {
                bool held=false;string temp=null,legacy=null;bool created=false,reserved=false;FileStream reservation=null;
                try {
                    try {held=mutex.WaitOne(TimeSpan.FromSeconds(5));} catch(AbandonedMutexException){held=true;}
                    if(!held)throw new IOException("Une autre écriture de ce fichier n’est pas terminée.");
                    legacy=path+".switcher-tmp";SafeFiles.RejectLinks(legacy);
                    if(!RecoverReservation(legacy))throw new IOException("Un temporaire actif ou sans propriétaire vérifiable bloque cette écriture.",unchecked((int)0x80070050));
                    // Older versions use this exact name as write exclusion; keep honoring it.
                    reservation=new FileStream(legacy,FileMode.CreateNew,FileAccess.Write,FileShare.None);reserved=true;
                    using(var self=Process.GetCurrentProcess()) {
                        string nonce=Guid.NewGuid().ToString("N");long started=self.StartTime.ToUniversalTime().Ticks;
                        var marker=Encoding.UTF8.GetBytes(Reservation(self.Id,started,nonce));reservation.Write(marker,0,marker.Length);reservation.Flush(true);
                        temp=Temporary(path,self.Id,started,nonce);
                    }
                    RecoverOwned(path);
                    SafeFiles.RejectLinks(temp);
                    using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {created=true;stream.Write(data,0,data.Length);stream.Flush(true);}
                    if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
                } finally {
                    try{if(created&&temp!=null&&File.Exists(temp))File.Delete(temp);}
                    finally{try{if(reservation!=null)reservation.Dispose();if(reserved&&File.Exists(legacy))File.Delete(legacy);}finally{if(held)mutex.ReleaseMutex();}}
                }
            }
        }
    }
}
