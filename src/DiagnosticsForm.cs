using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal static class ProductDiagnostics
    {
        public static string Report(AccountService service,RelayStore store)
        {
            var worker=RelayWorker.Status(store);var jobs=store.Messages();var integration=service.Data.Instances.Where(i=>!i.Archived).Select(i=>RelayIntegration.Status(store,service.Instances.Home(i))).ToArray();
            // Allowlist only: no paths, identities, task text, tokens or raw error payloads.
            return Json.Write(new{version=RelayWorker.Version,windows=Environment.OSVersion.VersionString,framework=Environment.Version.ToString(),cliExists=File.Exists(service.Settings.CodexExecutable),cliVersion=File.Exists(service.Settings.CodexExecutable)?FileVersionInfo.GetVersionInfo(service.Settings.CodexExecutable).FileVersion:null,workerRunning=RelayWorker.Running(store),workerVersion=worker.Version,workerPaused=worker.Paused,dispatch=RelayDispatch.Read(store).Mode,usageWorker=UsageCoordinator.Running(store),accounts=service.Data.Profiles.Count,instances=service.Data.Instances.Count,channels=store.Channels().Count,pluginsInstalled=integration.Count(i=>!String.IsNullOrEmpty(i.Fingerprint)),jobs=jobs.GroupBy(m=>m.State).Select(g=>new{state=g.Key,count=g.Count()}),transport="desktop-local-IPC",qualification="Compatibility is checked during connection; headless private-plugin parity is not assumed"});
        }
    }
    internal sealed class DiagnosticsForm : Form
    {
        public DiagnosticsForm(AccountService accounts,RelayStore store)
        {
            Text="Diagnostic et maintenance";ClientSize=new Size(920,680);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var top=new FlowLayoutPanel{Dock=DockStyle.Top,Height=100,Padding=new Padding(16)};Controls.Add(top);
            var text=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both};Controls.Add(text);text.BringToFront();
            Action refresh=delegate{try{text.Text="Diagnostic expurgé · aucun compte, chemin privé, prompt ou jeton exporté.\r\n\r\n"+ProductDiagnostics.Report(accounts,store)+"\r\n\r\nStockage : "+accounts.Vault.Root+"\r\nLes profils et conversations sont conservés. Les caches ne sont retirés que lorsqu'ils portent une preuve d'appartenance et qu'aucun processus propriétaire n'est actif.\r\n\r\nLe transport actuel ne propose pas d'arrêt ciblé confirmé. Utilisez Arrêter dans le chat Codex.\r\n\r\nMise à jour : préparez le nouveau dossier, vérifiez sa signature et ses empreintes, puis réinstallez l'intégration sur les profils choisis. Les anciens chats peuvent conserver leur serveur MCP jusqu'à leur fermeture.";}catch(Exception e){text.Text=Program.SafeError(e);}};
            var reload=new Button{Text="Actualiser le diagnostic",AutoSize=true};top.Controls.Add(reload);reload.Click+=delegate{refresh();};
            var export=new Button{Text="Exporter le diagnostic expurgé",AutoSize=true};top.Controls.Add(export);export.Click+=delegate{using(var picker=new SaveFileDialog{Filter="Diagnostic (*.json)|*.json",FileName="diagnostic-switcher.json"})if(picker.ShowDialog(this)==DialogResult.OK)SafeFiles.AtomicWrite(picker.FileName,System.Text.Encoding.UTF8.GetBytes(ProductDiagnostics.Report(accounts,store)));};
            var inventory=new Button{Text="Vérifier les plugins",AutoSize=true};top.Controls.Add(inventory);inventory.Click+=delegate{using(var f=new RelayIntegrationForm(store,accounts))f.ShowDialog(this);};
            var spaces=new Button{Text="Espaces Git et artefacts",AutoSize=true};top.Controls.Add(spaces);spaces.Click+=delegate{using(var f=new WorkspaceForm(store))f.ShowDialog(this);};
            var cleanup=new Button{Text="Examiner les temporaires",AutoSize=true};top.Controls.Add(cleanup);cleanup.Click+=delegate{try{var candidates=CacheMaintenance.Candidates(accounts.Vault.Root);if(candidates.Length==0){text.Text="Aucun temporaire abandonné appartenant à cette application n'a été identifié. Profils, caches non identifiés et historiques conservés.";return;}text.Text=String.Join("\r\n",candidates);if(MessageBox.Show(this,"Retirer ces "+candidates.Length+" dossiers temporaires identifiés ? Leurs processus propriétaires ne sont plus actifs. Les profils et historiques restent conservés.","Temporaires vérifiés",MessageBoxButtons.YesNo)==DialogResult.Yes)text.Text=CacheMaintenance.Clean(accounts.Vault.Root,candidates)+" dossier(s) temporaire(s) retiré(s).";}catch(Exception e){text.Text=Program.SafeError(e);}};
            var updates=new Button{Text="Vérifier les mises à jour",AutoSize=true};top.Controls.Add(updates);updates.Click+=async delegate{updates.Enabled=false;try{text.Text=await ReleaseCheck.Read();}catch(Exception e){text.Text="Vérification indisponible : "+Program.SafeError(e);}finally{if(!IsDisposed)updates.Enabled=true;}};
            var releases=new Button{Text="Ouvrir les versions publiées",AutoSize=true};top.Controls.Add(releases);releases.Click+=delegate{Process.Start(new ProcessStartInfo(ReleaseCheck.Page){UseShellExecute=true});};
            refresh();ProductUx.Accessible(this);
        }
    }
}
