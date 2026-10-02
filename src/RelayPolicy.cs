using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Creezio.Switcher
{
    public sealed class RelayPolicy
    {
        public int Version {get;set;}
        public bool AutoInstallManaged {get;set;}
        public bool KeepWorkerRunning {get;set;}
        public int MaxParallel {get;set;}
        public List<RelayAgent> Agents {get;set;}
        public List<RelayProject> Projects {get;set;}
        public List<RelayResource> Resources {get;set;}
        public List<RelayRule> Rules {get;set;}
        public RelayPolicy(){Version=1;MaxParallel=4;Agents=new List<RelayAgent>();Projects=new List<RelayProject>();Resources=new List<RelayResource>();Rules=new List<RelayRule>();}
    }
    public sealed class RelayAgent
    {
        [Description("Identifiant du canal connecté.")] public string Channel {get;set;}
        [Description("Rôle libre : revue, analyse, développement…")] public string Description {get;set;}
        [Description("Capacités déclarées, séparées par des virgules. Ce ne sont pas des droits vérifiés.")] public string Capabilities {get;set;}
        [Description("Types de tâches acceptés, séparés par des virgules ; * accepte tous les types.")] public string Tasks {get;set;}
        [Description("Projets autorisés, séparés par des virgules ; * pour tous les projets configurés.")] public string Projects {get;set;}
        public bool Enabled {get;set;}
        public bool AutoRoute {get;set;}
        [Description("Réutiliser un chat terminé pour le même projet. Son historique reste visible ; sinon, chaque tâche ouvre un chat.")] public bool ReuseConversation {get;set;}
        public int MaxConcurrent {get;set;}
        public double MinRemaining {get;set;}
        public bool AllowUnknownQuota {get;set;}
        [Description("inherit conserve le mode du chat existant et exige Accès complet si le chat de connexion l'a déjà ; full-access l'exige toujours. Aucune permission n'est modifiée.")] public string Permission {get;set;}
        [Description("Modèle explicite facultatif. Vide conserve le choix de Codex.")] public string Model {get;set;}
        [Description("Instructions de rôle ajoutées aux demandes destinées à cet agent.")] public string Instructions {get;set;}
        public RelayAgent(){Enabled=true;Tasks="*";Projects="*";MaxConcurrent=1;MinRemaining=1;AllowUnknownQuota=true;Permission="inherit";}
        public override string ToString(){return Channel??"Nouvel agent";}
    }
    public sealed class RelayProject
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Workspace {get;set;}
        [Description("explicit : l'utilisateur demande la délégation ; rules : les règles configurées peuvent la proposer dans leur périmètre.")] public string Delegation {get;set;}
        public string SourceChannels {get;set;}
        public string TargetChannels {get;set;}
        public string Instructions {get;set;}
        public int MaxConcurrent {get;set;}
        public int MaxJobs {get;set;}
        public int MaxDepth {get;set;}
        public int MaxMinutes {get;set;}
        public RelayProject(){Delegation="explicit";SourceChannels="*";TargetChannels="*";MaxConcurrent=4;MaxJobs=32;MaxDepth=3;MaxMinutes=120;}
        public override string ToString(){return Name??Id??"Nouveau projet";}
    }
    public sealed class RelayResource
    {
        public string Id {get;set;}
        public string Project {get;set;}
        [Description("Canaux autorisés à utiliser cette ressource. Aucun transfert de connexion.")] public string Channels {get;set;}
        public string Capabilities {get;set;}
        [Description("Identifiant externe, informatif ; aucun service particulier n'est imposé.")] public string ExternalId {get;set;}
        public string Instructions {get;set;}
        public override string ToString(){return Id??"Nouvelle ressource";}
    }
    public sealed class RelayRule
    {
        public string Id {get;set;}
        public bool Enabled {get;set;}
        public string Project {get;set;}
        public string Task {get;set;}
        public string Sources {get;set;}
        public string Targets {get;set;}
        public string Capabilities {get;set;}
        [Description("Explique à l'agent quand cette règle est utile, dans le périmètre demandé par l'utilisateur.")] public string When {get;set;}
        public int Priority {get;set;}
        public RelayRule(){Enabled=true;Project="*";Task="*";Sources="*";Targets="*";}
        public override string ToString(){return Id??"Nouvelle règle";}
    }
    public sealed class RelayJobSpec
    {
        public string Id {get;set;}
        public string From {get;set;}
        public string To {get;set;}
        public string Project {get;set;}
        public string Kind {get;set;}
        public string Resource {get;set;}
        public string Capabilities {get;set;}
        public string Access {get;set;}
        public string Title {get;set;}
        public string Prompt {get;set;}
        public string Revision {get;set;}
        public bool ReturnToSource {get;set;}
        public string ReplyTo {get;set;}
        public string Parent {get;set;}
        public List<string> DependsOn {get;set;}
        public Dictionary<string,string> Files {get;set;}
        public bool ExplicitDelegation {get;set;}
        public RelayJobSpec(){Kind="general";Access="write";DependsOn=new List<string>();Files=new Dictionary<string,string>();}
    }
    internal static class RelayPolicies
    {
        internal static string[] Tags(string value){return (value??"").Split(',').Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
        internal static bool Allows(string list,string value){return Tags(list).Any(x=>x=="*"||String.Equals(x,value,StringComparison.OrdinalIgnoreCase));}
        internal static bool Has(string available,string required){return Tags(required).All(x=>Tags(available).Contains(x,StringComparer.OrdinalIgnoreCase));}
        public static RelayPolicy Load(RelayStore store)
        {
            var p=store.ReadRecord<RelayPolicy>("policy.dpapi");Validate(p,false);return p;
        }
        public static void Save(RelayStore store,RelayPolicy policy)
        {
            Validate(policy,true);using(store.Lease("policy")){store.WriteRecord("policy.dpapi",policy);}
        }
        public static void Validate(RelayPolicy p,bool paths)
        {
            if(p==null||p.Version!=1||p.Agents==null||p.Projects==null||p.Resources==null||p.Rules==null)throw new InvalidOperationException("Version de configuration du relais non reconnue ; données conservées.");
            if(p.MaxParallel<1||p.MaxParallel>16)throw new InvalidOperationException("Parallélisme global : 1 à 16.");
            foreach(var list in new[]{p.Agents.Select(x=>x.Channel),p.Projects.Select(x=>x.Id),p.Resources.Select(x=>x.Id),p.Rules.Select(x=>x.Id)}){
                var ids=list.ToArray();foreach(string id in ids)RelayStore.ChannelId(id);if(ids.Distinct().Count()!=ids.Length)throw new InvalidOperationException("Identifiant de configuration en double.");
            }
            foreach(var a in p.Agents)if(a.MaxConcurrent<1||a.MaxConcurrent>8||a.MinRemaining<0||a.MinRemaining>100||!new[]{"inherit","full-access"}.Contains(a.Permission))throw new InvalidOperationException("Paramètres d'agent invalides.");
            foreach(var project in p.Projects){if(!new[]{"explicit","rules"}.Contains(project.Delegation)||project.MaxConcurrent<1||project.MaxConcurrent>16||project.MaxJobs<1||project.MaxJobs>256||project.MaxDepth<0||project.MaxDepth>8||project.MaxMinutes<1||project.MaxMinutes>10080)throw new InvalidOperationException("Limites de projet invalides.");if(paths)project.Workspace=RelayStore.WorkspacePath(project.Workspace);}
            foreach(var r in p.Resources)if(!p.Projects.Any(x=>x.Id==r.Project)||Tags(r.Channels).Length==0)throw new InvalidOperationException("Une ressource doit référencer un projet et ses canaux autorisés.");
            foreach(var r in p.Rules)if(r.Project!="*"&&!p.Projects.Any(x=>x.Id==r.Project))throw new InvalidOperationException("Projet de règle introuvable.");
        }
        public static string Fingerprint(RelayJobSpec spec){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(spec)))).Replace("-","");}
        public static void VerifyFiles(string workspace,Dictionary<string,string> files)
        {
            if(files==null)return;if(files.Count>100)throw new InvalidOperationException("Limitez le manifeste à 100 fichiers.");
            string root=Path.GetFullPath(workspace).TrimEnd('\\')+"\\";
            foreach(var pair in files){if(Path.IsPathRooted(pair.Key)||pair.Key.IndexOf(':')>=0)throw new InvalidOperationException("Le manifeste exige des chemins relatifs.");string path=Path.GetFullPath(Path.Combine(root,pair.Key));if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Fichier hors du projet.");SafeFiles.RejectLinks(path);using(var sha=SHA256.Create())using(var stream=File.OpenRead(path)){string hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();if(!String.Equals(hash,pair.Value,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Version du fichier modifiée : "+pair.Key);}}
        }
    }
}
