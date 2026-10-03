using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static class AdvancedTools
    {
        internal static bool Preview(Window owner,string title,RelayPolicy before,RelayPolicy after)
        {
            bool accepted=false;
            var d=new Window{Owner=owner,Title=title,Width=720,Height=560,MinWidth=480,MinHeight=400,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var dock=new DockPanel{Margin=new Thickness(24)};d.Content=dock;
            var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Bottom);dock.Children.Add(actions);
            actions.Children.Add(Ui.Button("Appliquer ces changements",()=>{accepted=true;d.Close();},true));actions.Children.Add(Ui.Button("Annuler",d.Close));
            dock.Children.Add(new TextBox{Text="La configuration précédente sera sauvegardée. Les conversations en cours conservent leur mandat.\n\n"+ConfigPreviewForm.Difference(before,after),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            d.ShowDialog();return accepted;
        }
        internal static Task Template(DesktopContext context,Window owner)
        {
            var d=new EditWindow(owner,"Créer un projet depuis un modèle");
            var models=new[]{"Relecture et analyse","Développement délégué","Accès à une ressource privée"};
            var model=Ui.Select("Point de départ",models,models[0],d.Fields);
            var channels=context.Store.Channels().Select(c=>c.Id).ToArray();
            var source=Ui.Select("Source",channels,channels.FirstOrDefault(),d.Fields);var target=Ui.Select("Destinataire",channels,channels.Skip(1).FirstOrDefault(),d.Fields);
            var id=Ui.Input("Identifiant du projet","mon-projet",d.Fields);
            d.Fields.Children.Add(Ui.Text("Choisissez vos propres participants. Le projet utilisera la délégation explicite et sa nouvelle règle restera désactivée.",14,true));
            d.Save=async delegate{
                var before=RelayPolicies.Load(context.Store);var next=Json.Read<RelayPolicy>(Json.Write(before));
                TemplateForm.Apply(context.Store,next,id.Text.Trim(),(string)source.SelectedItem,(string)target.SelectedItem,model.SelectedIndex);RelayPolicies.ValidateReferences(next,context.Store);
                if(!Preview(d,"Vérifier le modèle",before,next))throw new InvalidOperationException("Aucun changement appliqué.");
                await context.Mutate(a=>{if(RelayPolicies.Load(context.Store).Revision!=before.Revision)throw new InvalidOperationException("La configuration a changé. Recommencez la prévisualisation.");RelayPolicies.Save(context.Store,next);return Task.CompletedTask;});
            };
            d.ShowDialog();return Task.CompletedTask;
        }
        internal static Task Workspace(DesktopContext context,Window owner)
        {
            var d=new Window{Owner=owner,Title="Espaces Git et fichiers",Width=710,Height=650,MinWidth=480,MinHeight=420,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var fields=new StackPanel{Margin=new Thickness(24)};d.Content=new ScrollViewer{Content=fields};
            fields.Children.Add(Ui.Text("Espaces Git et fichiers",24));
            var root=Ui.Input("Dépôt source",context.Store.Channels().Select(c=>c.Workspace).FirstOrDefault(),fields);
            fields.Children.Add(Ui.Button("Choisir le dépôt",()=>{string folder=Ui.Folder();if(folder!=null)root.Text=folder;}));
            var destination=Ui.Input("Dossier isolé à créer ou réutiliser","",fields);var revision=Ui.Input("Référence Git","HEAD",fields);
            fields.Children.Add(Ui.Text("Les changements non commités ne sont pas copiés. Les dépendances ne sont pas installées. Un espace existant du même dépôt est conservé.",13,true));
            var result=Ui.Text("",14,true);
            fields.Children.Add(Ui.AsyncButton("Créer ou réutiliser",async delegate{string repo=root.Text,dest=destination.Text,reference=revision.Text;result.Text=await Task.Run(()=>WorkspaceService.Create(repo,dest,reference));},e=>result.Text=Program.SafeError(e),true));
            fields.Children.Add(Ui.Button("Exporter les empreintes de fichiers",()=>{try{var open=new Microsoft.Win32.OpenFileDialog{Multiselect=true};if(open.ShowDialog(d)!=true)return;var manifest=WorkspaceService.Manifest(root.Text,open.FileNames);var save=new Microsoft.Win32.SaveFileDialog{Filter="Manifeste JSON|*.json",FileName="artifact-manifest.json"};if(save.ShowDialog(d)==true){SafeFiles.AtomicWrite(save.FileName,Encoding.UTF8.GetBytes(Json.Write(manifest)));result.Text=manifest.Count+" empreinte(s) exportée(s).";}}catch(Exception e){result.Text=Program.SafeError(e);}}));
            fields.Children.Add(result);fields.Children.Add(Ui.Button("Fermer",d.Close));d.ShowDialog();return Task.CompletedTask;
        }
        internal static async Task Maintenance(DesktopContext context,Window owner)
        {
            var candidates=await context.Read(a=>CacheMaintenance.Candidates(a.Vault.Root));
            var d=new Window{Owner=owner,Title="Temporaires du switcher",Width=710,Height=520,MinWidth=480,MinHeight=400,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var fields=new StackPanel{Margin=new Thickness(24)};d.Content=new ScrollViewer{Content=fields};
            fields.Children.Add(Ui.Text("Temporaires inutilisés",24));fields.Children.Add(Ui.Text("Seuls les dossiers temporaires créés et marqués par le switcher, dont le processus propriétaire est arrêté, sont proposés. Les profils Codex, comptes, projets et conversations sont conservés.",14,true));
            var selected=candidates.Select(path=>new{Path=path,Box=Ui.Check(path,false,fields)}).ToArray();var status=Ui.Text(candidates.Length==0?"Aucun temporaire admissible.":"Sélectionnez les dossiers à supprimer.",14,true);fields.Children.Add(status);
            fields.Children.Add(Ui.AsyncButton("Nettoyer la sélection",async delegate{var paths=selected.Where(x=>x.Box.IsChecked==true).Select(x=>x.Path).ToArray();if(paths.Length==0)return;if(!Ui.Confirm(d,"Supprimer définitivement les "+paths.Length+" dossiers sélectionnés ?","Nettoyer"))return;int count=await context.Read(a=>CacheMaintenance.Clean(a.Vault.Root,paths));status.Text=count+" dossier(s) supprimé(s).";foreach(var x in selected){x.Box.IsChecked=false;x.Box.IsEnabled=false;}},e=>status.Text=Program.SafeError(e)));
            fields.Children.Add(Ui.Button("Fermer",d.Close));d.ShowDialog();
        }
    }
}
