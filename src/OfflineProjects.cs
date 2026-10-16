using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Creezio.Switcher
{
    // Compatibility adapter for Desktop's local project index. Never edit its
    // live cache: managed instance callers also hold the launch lease.
    internal static class OfflineProjects
    {
        internal static void Register(string home,object project,Func<bool> closed)
        {
            if(!closed())throw new InvalidOperationException("Fermez l’instance destinataire avant d’enregistrer son projet.");
            string id=Json.Str(Json.Get(project,"id"));Guid parsed;if(!Guid.TryParse(id,out parsed))throw new InvalidOperationException("Projet natif non reconnu.");
            string[] roots=RelayEngine.Rows(Json.Get(project,"roots")).Select(p=>RelayStore.WorkspacePath(RolloutSnapshot.NativePath(Json.Str(Json.Get(p,"path"))))).ToArray();if(roots.Length==0)throw new InvalidOperationException("Projet sans dossier.");
            string path=Path.Combine(home,".codex-global-state.json");SafeFiles.RejectLinks(path);string before=File.Exists(path)?SafeFiles.ReadText(path):null;
            object parsedState=before==null?new Dictionary<string,object>():Json.Read<object>(before);if(!(parsedState is Dictionary<string,object>))throw new InvalidOperationException("Format du profil Codex non reconnu ; aucun réglage remplacé.");var state=Json.Obj(parsedState);
            var existing=Json.Get(state,"local-projects");if(existing!=null&&!(existing is Dictionary<string,object>))throw new InvalidOperationException("Format des projets Codex non reconnu ; aucun réglage remplacé.");var projects=existing==null?new Dictionary<string,object>():Json.Obj(existing);
            if(projects.ContainsKey(id)){
                var old=projects[id];if(!RelayEngine.Rows(Json.Get(old,"rootPaths")).Select(Json.Str).SequenceEqual(roots,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Le projet destinataire a changé de dossiers. Aucun réglage existant ne sera remplacé.");return;
            }
            long now=(long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalMilliseconds;
            projects.Add(id,new{id=id,name=Json.Str(Json.Get(project,"name")),rootPaths=roots,createdAt=now,updatedAt=now});state["local-projects"]=projects;
            var order=RelayEngine.Rows(Json.Get(state,"project-order")).Select(Json.Str).ToList();if(!order.Contains(id))order.Add(id);state["project-order"]=order.ToArray();
            if(!closed()||(File.Exists(path)?SafeFiles.ReadText(path):null)!=before)throw new InvalidOperationException("Le profil Codex a changé pendant la préparation. Aucun réglage n’a été remplacé.");
            SafeFiles.AtomicWrite(path,Encoding.UTF8.GetBytes(Json.Write(state)));
        }
    }
}
