using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    // Codex owns the SQLite/history conversion. Always restrict it to the new
    // copy, after releasing our writer; never migrate the user's other chats.
    internal static class ThreadCopyMigration
    {
        internal static void Validate(object report,string thread)
        {
            var rows=RelayEngine.Rows(Json.Get(report,"outcomes")).ToArray();
            if(rows.Length!=1||Json.Str(Json.Get(rows[0],"thread_id"))!=thread)throw new InvalidOperationException("Codex n’a pas confirmé le format de la conversation copiée.");
            string status=Json.Str(Json.Get(rows[0],"status"));
            if(status!="migrated"&&status!="already_paginated")throw new InvalidOperationException(status=="skipped_busy"?"La copie est encore ouverte par Codex. Attendez puis utilisez Vérifier ; aucune seconde copie ne sera créée.":"Cette version de Codex ne peut pas préparer l’historique pour son interface ("+status+"). La copie est conservée ; utilisez Vérifier après mise à jour de Codex.");
        }
        internal static async Task Run(string executable,string home,string thread,CancellationToken token)
        {
            Guid id;if(!Guid.TryParseExact(thread,"D",out id))throw new InvalidOperationException("Identifiant de copie invalide.");
            SafeFiles.RejectLinks(home);
            var start=new ProcessStartInfo(executable,"migrate-rollouts --apply --thread "+id.ToString("D")+" --json") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=home};
            ThreadProfileRpc.ConfigureHome(start,home);
            using(var process=new Process{StartInfo=start}){
                token.ThrowIfCancellationRequested();process.Start();
                using(var job=new ProcessJob(process))using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
                    try{while(!process.HasExited)await Task.Delay(100,timeout.Token).ConfigureAwait(false);}
                    catch{try{process.Kill();}catch{}throw;}
                    string report=await output.ConfigureAwait(false);await errors.ConfigureAwait(false);
                    if(process.ExitCode!=0)throw new InvalidOperationException("La conversion native de l’historique a échoué. Mettez Codex à jour puis utilisez Vérifier ; la copie est conservée.");
                    object parsed;try{parsed=Json.Read<object>(report);}catch{throw new InvalidOperationException("Réponse de conversion Codex non reconnue. La copie reste à vérifier.");}
                    Validate(parsed,thread);
                }
            }
        }
    }
}
