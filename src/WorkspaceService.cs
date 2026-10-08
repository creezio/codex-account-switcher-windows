using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal static class WorkspaceService
    {
        internal static string Git(string root,string arguments)
        {
            SafeFiles.RejectLinks(root);var start=new ProcessStartInfo("git.exe",arguments){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            using(var p=Process.Start(start)){var output=p.StandardOutput.ReadToEndAsync();var errors=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(30000)){try{p.Kill();}catch{}throw new InvalidOperationException("Git a dépassé le délai de 30 secondes.");}string result=output.GetAwaiter().GetResult();errors.GetAwaiter().GetResult();if(p.ExitCode!=0)throw new InvalidOperationException("Git n'a pas pu effectuer cette opération. Vérifiez le dépôt, la référence et le dossier choisi.");return result.Trim();}
        }
        public static string Create(string repository,string destination,string reference)
        {
            repository=RelayStore.WorkspacePath(repository);destination=Path.GetFullPath(destination);SafeFiles.RejectLinks(destination);
            if(String.IsNullOrWhiteSpace(reference)||reference.StartsWith("-")||reference.IndexOfAny(new[]{'\r','\n','"'})>=0)throw new InvalidOperationException("Référence Git invalide.");
            if(RelayStore.SamePath(repository,destination))throw new InvalidOperationException("Choisissez un espace différent du dépôt source.");
            string commit=Git(repository,"rev-parse --verify "+RelayWorker.Quote(reference+"^{commit}"));
            if(Directory.Exists(destination)){
                string common=Git(repository,"rev-parse --path-format=absolute --git-common-dir");string other=Git(destination,"rev-parse --path-format=absolute --git-common-dir");
                if(!RelayStore.SamePath(common,other))throw new InvalidOperationException("Le dossier existant appartient à un autre dépôt.");
                return "Espace existant réutilisé, contenu conservé · "+Git(destination,"rev-parse HEAD");
            }
            long size=0;foreach(string name in Git(repository,"ls-files -z").Split('\0').Where(n=>n.Length>0)){string file=Path.GetFullPath(Path.Combine(repository,name));if(!file.StartsWith(repository.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))continue;SafeFiles.RejectLinks(file);if(File.Exists(file))size+=new FileInfo(file).Length;}
            var drive=new DriveInfo(Path.GetPathRoot(destination));if(size>50L*1024*1024&&drive.AvailableFreeSpace<20L*1024*1024*1024)throw new InvalidOperationException("Moins de 20 Gio libres : libérez les temporaires connus avant de créer cet espace volumineux.");
            if(drive.AvailableFreeSpace<size*2+100L*1024*1024)throw new InvalidOperationException("Espace disque insuffisant pour ce worktree.");
            Git(repository,"worktree add --detach -- "+RelayWorker.Quote(destination)+" "+RelayWorker.Quote(commit));
            return "Espace Git créé sans installation de dépendances · "+commit;
        }
        public static Dictionary<string,string> Manifest(string root,IEnumerable<string> paths)
        {
            root=RelayStore.WorkspacePath(root);string prefix=root.TrimEnd('\\')+"\\";var manifest=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(string requested in paths){string path=Path.GetFullPath(requested);if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Fichier hors du dossier sélectionné.");SafeFiles.RejectLinks(path);using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))manifest.Add(path.Substring(prefix.Length),BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant());}
            RelayPolicies.VerifyFiles(root,manifest);return manifest;
        }
    }
    internal sealed class WorkspaceForm : Form
    {
        public WorkspaceForm(RelayStore store)
        {
            Text="Espaces Git et artefacts";ClientSize=new Size(900,620);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var body=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(18)};body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,220));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(body);
            Func<string,string,TextBox> field=(name,value)=>{body.Controls.Add(new Label{Text=name,AutoSize=true});var t=new TextBox{Text=value,Dock=DockStyle.Top};body.Controls.Add(t);return t;};
            var root=field("Dépôt / dossier source",store.Channels().Select(c=>c.Workspace).FirstOrDefault()??"");var target=field("Espace isolé à créer / réutiliser","");var reference=field("Référence Git", "HEAD");
            body.Controls.Add(new Label{Text="Les modifications non commitées ne sont pas copiées. Les dépendances restent à configurer dans le projet. Aucun espace n'est supprimé automatiquement.",AutoSize=true,MaximumSize=new Size(780,0)});body.SetColumnSpan(body.Controls[body.Controls.Count-1],2);
            var create=new Button{Text="Créer / réutiliser l'espace",AutoSize=true};body.Controls.Add(create);var manifest=new Button{Text="Empreintes de fichiers…",AutoSize=true};body.Controls.Add(manifest);
            var output=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};Controls.Add(output);output.BringToFront();
            create.Click+=async delegate{create.Enabled=false;try{string repo=root.Text,dest=target.Text,revision=reference.Text;output.Text=await Task.Run(()=>WorkspaceService.Create(repo,dest,revision));output.AppendText("\r\n\r\nDans Projets, choisissez Espaces isolés et autorisez ce dossier. Connectez ensuite l'agent destinataire à cet espace.");}catch(Exception e){output.Text=Program.SafeError(e);}finally{create.Enabled=true;}};
            manifest.Click+=delegate{using(var files=new OpenFileDialog{Multiselect=true})if(files.ShowDialog(this)==DialogResult.OK){try{var hashes=WorkspaceService.Manifest(root.Text,files.FileNames);output.Text=Json.Write(hashes);using(var save=new SaveFileDialog{Filter="Manifeste (*.json)|*.json",FileName="artifact-manifest.json"})if(save.ShowDialog(this)==DialogResult.OK)SafeFiles.AtomicWrite(save.FileName,Encoding.UTF8.GetBytes(output.Text));}catch(Exception e){output.Text=Program.SafeError(e);}}};ProductUx.Accessible(this);
        }
    }
}
