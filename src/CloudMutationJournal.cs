using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class CloudMutationRecord
    {
        public int SchemaVersion {get;set;}
        public string Id {get;set;}
        public string Action {get;set;}
        public string InstanceId {get;set;}
        public string AccountKey {get;set;}
        public string Fingerprint {get;set;}
        public string State {get;set;}
        public string CreatedUtc {get;set;}
        public string CompletedUtc {get;set;}
        public string ErrorCode {get;set;}
        public int? HttpStatus {get;set;}
        public string ConfigurationId {get;set;}
    }
    internal static class CloudMutationJournal
    {
        internal static async Task<CloudConfiguration> Run(string vaultRoot,string id,string action,string instanceId,string accountKey,object request,Func<Task<CloudConfiguration>> mutate,Func<string,Task<CloudConfiguration>> reread)
        {
            if(!Regex.IsMatch(id??"","^[a-f0-9]{32}$"))throw new InvalidOperationException("Identifiant d'opération Cloud invalide.");
            if(action!="create"&&action!="rename")throw new InvalidOperationException("Opération Cloud non prise en charge par ce journal.");
            if(String.IsNullOrEmpty(instanceId)||String.IsNullOrEmpty(accountKey)||mutate==null||reread==null)throw new InvalidOperationException("Association Cloud incomplète.");
            string directory=Path.Combine(Path.GetFullPath(vaultRoot),"cloud-operations-v1");SafeFiles.PrivateDirectory(directory);
            string file=Path.Combine(directory,id+".json"),lease=Path.Combine(directory,id+".lock");SafeFiles.RejectLinks(file);SafeFiles.RejectLinks(lease);
            string fingerprint;
            using(var hash=SHA256.Create())fingerprint=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(request)))).Replace("-","").ToLowerInvariant();
            // A file's existence is not a lock: the open handle is. A crashed process releases it.
            using(var exclusive=new FileStream(lease,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
                if(File.Exists(file)){
                    var old=Json.Read<CloudMutationRecord>(SafeFiles.ReadText(file));
                    if(old==null||old.SchemaVersion!=1||old.Id!=id||old.Action!=action||old.InstanceId!=instanceId||old.AccountKey!=accountKey||old.Fingerprint!=fingerprint)
                        throw new InvalidOperationException("Cet identifiant Cloud appartient à une autre demande.");
                    if(old.State=="completed"&&!String.IsNullOrEmpty(old.ConfigurationId))return await reread(old.ConfigurationId);
                    throw new InvalidOperationException(old.State=="failed"?"L'opération précédente a échoué ("+old.ErrorCode+"). Vérifiez l'état Cloud avant une nouvelle demande.":"Le résultat de cette opération Cloud est incertain. Aucun renvoi automatique : actualisez les environnements et rapprochez le résultat.");
                }
                var record=new CloudMutationRecord{SchemaVersion=1,Id=id,Action=action,InstanceId=instanceId,AccountKey=accountKey,Fingerprint=fingerprint,State="sending",CreatedUtc=DateTime.UtcNow.ToString("o")};
                Save(file,record);
                try{
                    var result=await mutate();
                    if(result==null)throw new InvalidOperationException("Réponse Cloud absente après envoi.");
                    record.ConfigurationId=result.Id;record.State="completed";record.CompletedUtc=DateTime.UtcNow.ToString("o");Save(file,record);return result;
                }catch(CloudBackendException e){
                    record.State=e is CloudBackendUncertainException?"uncertain":"failed";
                    record.ErrorCode=SafeCode(e.Code);record.HttpStatus=e.HttpStatus;Save(file,record);throw;
                }catch{
                    // Conservatively retain a possible write when a local failure follows dispatch.
                    record.State="uncertain";record.ErrorCode="local-outcome-unknown";Save(file,record);throw;
                }
            }
        }
        private static string SafeCode(string code){return Regex.IsMatch(code??"","^[a-zA-Z0-9_.-]{1,100}$")?code:"cloud-error";}
        private static void Save(string path,CloudMutationRecord record){SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes(Json.Write(record)));}
    }
}
