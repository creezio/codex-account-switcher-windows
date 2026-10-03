using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class ReleaseCheck
    {
        public const string Page="https://github.com/creezio/codex-account-switcher-windows/releases";
        public static async Task<string> Read()
        {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create("https://api.github.com/repos/creezio/codex-account-switcher-windows/releases?per_page=10");request.UserAgent="CodexAccountSwitcher/"+RelayWorker.Version;request.Accept="application/vnd.github+json";request.Timeout=15000;request.ReadWriteTimeout=15000;
            using(var deadline=new System.Threading.Timer(o=>request.Abort(),null,15000,System.Threading.Timeout.Infinite))
            using(var response=await request.GetResponseAsync())using(var reader=new StreamReader(response.GetResponseStream())){
                string raw=await reader.ReadToEndAsync();if(raw.Length>1000000)throw new InvalidOperationException("Réponse de mise à jour trop volumineuse.");
                var latest=RelayEngine.Rows(Json.Read<object>(raw)).FirstOrDefault(x=>!Object.Equals(Json.Get(x,"draft"),true));
                if(latest==null)return "Aucune version publique disponible. Version installée : "+RelayWorker.Version;
                string version=Json.Str(Json.Get(latest,"tag_name"));return "Version installée : "+RelayWorker.Version+"\r\nDernière publication GitHub : "+version+(Object.Equals(Json.Get(latest,"prerelease"),true)?" (bêta)":"")+"\r\n\r\nConsultez les notes, la signature et SHA256SUMS avant installation. Aucun fichier téléchargé ni application remplacée.";
            }
        }
    }
}
