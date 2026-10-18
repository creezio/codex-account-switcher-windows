using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creezio.Switcher;

internal static class VaultConcurrencyTests
{
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static void Conflict(Action action){try{action();}catch(InvalidOperationException error){Assert(error.Message.Contains("coffre")&&error.Message.Contains("Actualisez"),"Vault conflict lost its actionable diagnostic");return;}throw new Exception("Obsolete vault snapshot was silently saved");}
    private static string Folder(string root,string name){string path=Path.Combine(root,"vault-concurrency-"+name+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string Auth(int n)
    {
        string claims=Json.Write(new{ sub="fictional-user-"+n,email="fictional-"+n+"@example.test",@auth=new{} });
        var payload=Json.Obj(Json.Read<object>(claims));payload["https://api.openai.com/auth"]=new{chatgpt_account_id="fictional-account-"+n,chatgpt_plan_type="pro"};
        string encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(payload))).TrimEnd('=').Replace('+','-').Replace('/','_');
        return Json.Write(new{auth_mode="chatgpt",tokens=new{access_token="fictional-access-token-not-valid-"+n,id_token="e30."+encoded+".fictional",account_id="fictional-account-"+n}});
    }
    private static void Initialize(string root){var vault=new Vault(root);vault.Save(new VaultData());}
    private static Mutex AtomicMutex(string path)
    {string key;using(var sha=SHA256.Create())key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-","");return new Mutex(false,"Local\\Creezio.AtomicWrite."+key);}
    private static byte[] Encrypted(VaultData value)
    {return ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Write(value)),Encoding.UTF8.GetBytes("Creezio.CodexAccountSwitcher.v1"),DataProtectionScope.CurrentUser);}
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("vault refuses a stale loaded snapshot and preserves the first writer and encrypted bytes",()=>{
            string folder=Folder(root,"stale");Initialize(folder);var first=new Vault(folder);var a=first.Load();var second=new Vault(folder);var b=second.Load();a.Instances.Single().Name="First writer";first.Save(a);byte[] saved=File.ReadAllBytes(first.FilePath);b.Instances.Single().Name="Obsolete writer";Conflict(()=>second.Save(b));Assert(saved.SequenceEqual(File.ReadAllBytes(first.FilePath))&&new Vault(folder).Load().Instances.Single().Name=="First writer","Stale save changed the committed vault or its bytes");
        });
        check("vault baseline also protects constructor snapshots which have never called Load",()=>{
            string folder=Folder(root,"constructor");Initialize(folder);var stale=new Vault(folder);var writer=new Vault(folder);var data=writer.Load();data.Instances.Single().Name="Committed";writer.Save(data);Conflict(()=>stale.Save(new VaultData()));Assert(new Vault(folder).Load().Instances.Single().Name=="Committed","Constructor snapshot lost a newer account state");
        });
        check("vault Load advances its baseline to the exact bytes actually decrypted",()=>{
            string folder=Folder(root,"load-refresh");Initialize(folder);var delayed=new Vault(folder);var writer=new Vault(folder);var data=writer.Load();data.Instances.Single().Name="Before actual Load";writer.Save(data);var actual=delayed.Load();actual.Instances.Single().Name="After actual Load";delayed.Save(actual);Assert(new Vault(folder).Load().Instances.Single().Name=="After actual Load","Load retained an earlier constructor fingerprint");
        });
        check("vault repeated saves and repeated AccountService imports advance their own baseline",()=>{
            string folder=Folder(root,"repeat");var service=new AccountService(folder);service.Import(Auth(1),"One");service.Import(Auth(2),"Two");service.Data.Profiles.Single(p=>p.Label=="One").Label="Updated";service.Save();Assert(new AccountService(folder).Data.Profiles.Count==2&&new AccountService(folder).Data.Profiles.Any(p=>p.Label=="Updated"),"Successful Save did not advance the service fingerprint");
        });
        check("vault protects two AccountService snapshots against lost account rows",()=>{
            string folder=Folder(root,"services");Initialize(folder);var first=new AccountService(folder);var second=new AccountService(folder);first.Import(Auth(3),"Accepted");Conflict(()=>second.Import(Auth(4),"Rejected"));var actual=new AccountService(folder);Assert(actual.Data.Profiles.Count==1&&actual.Data.Profiles.Single().Label=="Accepted","Stale service erased or merged unrelated credentials");
        });
        check("vault competing initial creation preserves the first new vault",()=>{
            string folder=Folder(root,"initial");var first=new Vault(folder);var second=new Vault(folder);var data=new VaultData();data.Instances.Add(new DesktopInstance{Id="local",Name="Created once"});first.Save(data);Conflict(()=>second.Save(new VaultData()));Assert(new Vault(folder).Load().Instances.Single().Name=="Created once","Missing-file baseline overwrote another creator");
        });
        check("vault legacy DPAPI data remains readable and migrates without adding a required revision",()=>{
            string folder=Folder(root,"legacy");var legacy=new VaultData{Version=1};legacy.Profiles.Add(new Profile{Key=AuthIdentity.Parse(Auth(5)).Key,AuthJson=Auth(5),Label="Legacy",AllInstances=false});SafeFiles.AtomicWrite(Path.Combine(folder,"accounts.dpapi"),Encrypted(legacy));var vault=new Vault(folder);var data=vault.Load();Assert(data.Version==2&&data.Profiles.Single().AllInstances,"Legacy account associations were not normalized");vault.Save(data);Assert(new Vault(folder).Load().Profiles.Single().Label=="Legacy"&&File.Exists(Path.Combine(folder,"accounts-before-instances.dpapi")),"Existing encrypted format or retained backup broke");
        });
        check("vault transaction lease serializes fresh loads across awaited continuations",()=>{
            string folder=Folder(root,"async");Initialize(folder);IDisposable held=VaultMutation.Acquire(folder,CancellationToken.None);var entered=new ManualResetEventSlim();Task following=null;
            try{following=Task.Run(async()=>{using(await Task.Run(()=>VaultMutation.Acquire(folder,CancellationToken.None))){entered.Set();var fresh=new AccountService(folder);Assert(fresh.Data.Instances.Single().Name=="First committed","Second transaction loaded before its file lease");await Task.Delay(10);fresh.Data.Instances.Single().Name="Second committed";fresh.Save();}});Assert(!entered.Wait(80),"Concurrent transaction bypassed the held file lease");var first=new AccountService(folder);first.Data.Instances.Single().Name="First committed";first.Save();}
            finally{held.Dispose();}
            Assert(following.Wait(3000)&&new AccountService(folder).Data.Instances.Single().Name=="Second committed","Fresh async transaction lost data or required thread-affine release");entered.Dispose();
        });
        check("vault transaction wait observes cancellation and its explicit bounded timeout",()=>{
            string folder=Folder(root,"bounded");using(VaultMutation.Acquire(folder,CancellationToken.None)){
                var watch=Stopwatch.StartNew();using(var cancel=new CancellationTokenSource(60)){bool cancelled=false;try{using(VaultMutation.Acquire(folder,cancel.Token,TimeSpan.FromSeconds(2))){} }catch(OperationCanceledException){cancelled=true;}Assert(cancelled&&watch.Elapsed<TimeSpan.FromSeconds(2),"Blocked lease ignored cancellation");}
                watch.Restart();bool expired=false;try{using(VaultMutation.Acquire(folder,CancellationToken.None,TimeSpan.FromMilliseconds(80))){} }catch(IOException){expired=true;}Assert(expired&&watch.Elapsed<TimeSpan.FromSeconds(2),"Blocked lease exceeded its configured timeout");
            }
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool failed=false;try{using(VaultMutation.Acquire(folder,cancelled.Token)){} }catch(OperationCanceledException){failed=true;}Assert(failed,"Already cancelled transaction acquired a lease");}
        });
        check("vault transaction exception releases only its own lease and independent roots stay available",()=>{
            string a=Folder(root,"release-a"),b=Folder(root,"release-b");try{using(VaultMutation.Acquire(a,CancellationToken.None)){using(VaultMutation.Acquire(b,CancellationToken.None,TimeSpan.Zero)){}throw new InvalidOperationException("Fictional mutation exception");}}catch(InvalidOperationException){}using(VaultMutation.Acquire(a,CancellationToken.None,TimeSpan.Zero)){}Assert(File.Exists(Path.Combine(a,"accounts-mutation.lock")),"Transaction unexpectedly deleted a shared lock pathname");
        });
        check("vault CAS runs inside the legacy atomic writer mutex before replacing encrypted bytes",()=>{
            string folder=Folder(root,"legacy-writer");Initialize(folder);var stale=new Vault(folder);var data=stale.Load();data.Instances.Single().Name="Stale value";var actual=new Vault(folder).Load();actual.Instances.Single().Name="Legacy atomic writer";Task saving;
            using(var mutex=AtomicMutex(stale.FilePath)){Assert(mutex.WaitOne(1000),"Could not acquire fixture atomic writer mutex");try{saving=Task.Run(()=>Conflict(()=>stale.Save(data)));Assert(!saving.Wait(100),"Save did not honor the existing AtomicFiles mutex");SafeFiles.AtomicWrite(stale.FilePath,Encrypted(actual));}finally{mutex.ReleaseMutex();}}
            Assert(saving.Wait(3000)&&new Vault(folder).Load().Instances.Single().Name=="Legacy atomic writer","CAS checked too early and overwrote the legacy atomic writer");
        });
        check("vault permits stable initial absence but never replaces a previously observed vault with empty data",()=>{
            string folder=Folder(root,"initial-absence");var vault=new Vault(folder);Assert(vault.Load().Profiles.Count==0&&!File.Exists(vault.FilePath),"Initial missing vault was fabricated on disk");
            var data=new VaultData();data.Instances.Add(new DesktopInstance{Id="local",Name="Preserved fixture"});vault.Save(data);byte[] before=File.ReadAllBytes(vault.FilePath);string parked=vault.FilePath+".fixture-parked";File.Move(vault.FilePath,parked);
            try{bool failed=false;try{vault.Load();}catch(IOException){failed=true;}Assert(failed,"A known vault disappearing became empty account data");}
            finally{File.Move(parked,vault.FilePath);}
            Assert(before.SequenceEqual(File.ReadAllBytes(vault.FilePath))&&vault.Load().Instances.Single().Name=="Preserved fixture","Missing-file rejection changed the encrypted vault");
        });
        check("vault corrupt encrypted bytes and validly encrypted invalid documents fail without becoming empty",()=>{
            foreach(string kind in new[]{"ciphertext","null","version"}){
                string folder=Folder(root,"corrupt-"+kind),path=Path.Combine(folder,"accounts.dpapi");byte[] bytes;
                if(kind=="ciphertext")bytes=new byte[]{1,2,3,4};else{string json=kind=="null"?"null":Json.Write(new VaultData{Version=99});bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(json),Encoding.UTF8.GetBytes("Creezio.CodexAccountSwitcher.v1"),DataProtectionScope.CurrentUser);}
                File.WriteAllBytes(path,bytes);var vault=new Vault(folder);bool failed=false;try{vault.Load();}catch(InvalidOperationException){failed=true;}
                Assert(failed&&bytes.SequenceEqual(File.ReadAllBytes(path)),"Corruption was returned as an empty or modified vault");
            }
        });
        check("vault persistent read sharing refusal is bounded and preserves its last encrypted contents",()=>{
            string folder=Folder(root,"read-refusal");Initialize(folder);var vault=new Vault(folder);byte[] before=File.ReadAllBytes(vault.FilePath);var watch=Stopwatch.StartNew();
            using(new FileStream(vault.FilePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
                bool failed=false;try{vault.Load();}catch(IOException){failed=true;}Assert(failed&&watch.Elapsed<TimeSpan.FromSeconds(3),"Locked vault became empty or its read retry was unbounded");
            }
            Assert(before.SequenceEqual(File.ReadAllBytes(vault.FilePath))&&vault.Load().Instances.Count==1,"Read refusal changed encrypted bytes or invalidated a later valid read");
        });
        check("vault recovers a brief read sharing refusal using fresh bytes without empty account data",()=>{
            string folder=Folder(root,"brief-read-refusal");Initialize(folder);var vault=new Vault(folder);var data=vault.Load();data.Instances.Single().Name="Fresh fixture";vault.Save(data);
            var held=new FileStream(vault.FilePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);var started=new ManualResetEventSlim();Task<VaultData> reading=null;
            try{reading=Task.Run(()=>{started.Set();return vault.Load();});Assert(started.Wait(2000),"Fixture reader did not start");Thread.Sleep(60);}
            finally{held.Dispose();try{if(reading!=null)reading.GetAwaiter().GetResult();}finally{started.Dispose();}}
            Assert(reading!=null&&reading.Wait(3000)&&reading.Result.Instances.Single().Name=="Fresh fixture","Brief read refusal lost the established vault or its fresh contents");
        });
        check("vault concurrent Windows atomic replacements never hide nonempty accounts or lose encrypted data",()=>{
            string folder=Folder(root,"replace-readers"),path=Path.Combine(folder,"accounts.dpapi"),auth=Auth(8);var identity=AuthIdentity.Parse(auth);var data=new VaultData();data.Profiles.Add(new Profile{Key=identity.Key,AuthJson=auth,Label="Fictional fixture"});data.Instances.Add(new DesktopInstance{Id="local",Name="Generation A"});byte[] first=Encrypted(data);data.Instances.Single().Name="Generation B";byte[] last=Encrypted(data);SafeFiles.AtomicWrite(path,first);var vault=new Vault(folder);var start=new ManualResetEventSlim();int writes=0,reads=0;
            var writer=Task.Run(()=>{start.Wait();for(int n=0;n<120;n++){SafeFiles.AtomicWrite(path,(n&1)==0?last:first);Interlocked.Increment(ref writes);}SafeFiles.AtomicWrite(path,last);Interlocked.Increment(ref writes);});
            try{start.Set();do{var observed=(reads%10==0?new Vault(folder):vault).Load();Assert(observed.Profiles.Count==1&&observed.Profiles[0].Key==identity.Key&&observed.Instances.Count==1&&(observed.Instances[0].Name=="Generation A"||observed.Instances[0].Name=="Generation B"),"Atomic replacement exposed an empty or mixed vault");reads++;}while(!writer.IsCompleted||reads<50);}
            finally{writer.GetAwaiter().GetResult();start.Dispose();}
            Assert(writes==121&&reads>=50&&last.SequenceEqual(File.ReadAllBytes(path))&&vault.Load().Profiles.Single().Key==identity.Key,"Concurrent reads lost a writer generation or modified encrypted data");
        });
    }
}
