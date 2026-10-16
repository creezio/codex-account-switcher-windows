using System;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace Creezio.Switcher
{
    internal sealed class InstanceStartupException : InvalidOperationException
    {
        internal readonly InstanceStartupResult Result;
        internal InstanceStartupException(InstanceStartupResult result) : base(result.Message) { Result=result; }
    }
    public sealed class InstanceStartupResult
    {
        public string LaunchId {get;set;}
        public string Stage {get;set;}
        public string ErrorType {get;set;}
        public string ErrorCode {get;set;}
        public string Message {get;set;}
        public int? ExitCode {get;set;}
        public int? NativeErrorCode {get;set;}
        public string At {get;set;}
    }
    internal static class InstanceStartup
    {
        internal static string StageLabel(string stage)
        {
            switch(stage){case "read-request":return "lecture de la demande";case "write-request":return "écriture de launch.json";case "prepare-profile":return "préparation du profil";case "acquire-instance":return "verrouillage de l’instance";case "recover-temporaries":return "récupération des temporaires d’instance";case "write-state":return "écriture de state.json";case "start-desktop":return "lancement de Codex";case "bootstrap":return "initialisation de Codex";case "windows-launcher":return "activation Windows";default:return "lancement de l’instance";}
        }
        internal static InstanceStartupResult Failure(string launch,string stage,Exception error,int? exit=null)
        {
            error=error.GetBaseException();int hr=error.HResult;
            var win32=error as Win32Exception;
            return FailureCode(launch,stage,error.GetType().Name,hr,win32!=null?(int?)win32.NativeErrorCode:null,exit);
        }
        internal static InstanceStartupResult FailureCode(string launch,string stage,string type,int hr,int? nativeCode=null,int? exit=null)
        {
            string native=nativeCode.HasValue?new Win32Exception(nativeCode.Value).Message:(hr&unchecked((int)0xffff0000))==unchecked((int)0x80070000)?new Win32Exception(hr&0xffff).Message:
                type=="InvalidOperationException"?"Opération refusée":type=="FormatException"||type=="InvalidDataException"?"Données de lancement invalides":"";
            string code="0x"+hr.ToString("X8");
            native=native.Replace("%1","Codex");
            return new InstanceStartupResult{LaunchId=launch,Stage=stage,ErrorType=type,ErrorCode=code,ExitCode=exit,NativeErrorCode=nativeCode,At=DateTime.UtcNow.ToString("o"),Message="Échec — "+StageLabel(stage)+" : "+(String.IsNullOrWhiteSpace(native)?type:native)+" ("+type+", "+code+")"+(nativeCode.HasValue?" · erreur Windows "+nativeCode.Value:"")+(exit.HasValue?" · code de sortie "+exit.Value:"")+". Les autres instances restent ouvertes."};
        }
        internal static InstanceStartupResult DesktopExit(string launch,int code)
        {return new InstanceStartupResult{LaunchId=launch,Stage="bootstrap",ErrorType="CodexEarlyExit",ExitCode=code,At=DateTime.UtcNow.ToString("o"),Message="Codex a quitté avant d’ouvrir sa fenêtre (code de sortie "+code+"). Les autres instances restent ouvertes."};}
        internal static InstanceStartupResult Bootstrap(string launch)
        {return new InstanceStartupResult{LaunchId=launch,Stage="bootstrap",ErrorType="CodexBootstrapFailed",At=DateTime.UtcNow.ToString("o"),Message="Codex a signalé « Desktop bootstrap failed » pendant son initialisation. Les autres instances restent ouvertes."};}
        internal static InstanceStartupResult LauncherExit(int code)
        {return new InstanceStartupResult{Stage="windows-launcher",ErrorType="WindowsLauncherExit",ExitCode=code,At=DateTime.UtcNow.ToString("o"),Message="Échec de l’activation Windows (code de sortie "+code+"). Vérifiez l’installation Codex et le diagnostic Windows."};}
        internal static void Save(string folder,InstanceStartupResult result)
        {SafeFiles.AtomicWrite(Path.Combine(folder,"startup.json"),Encoding.UTF8.GetBytes(Json.Write(result)));}
        internal static InstanceStartupResult Read(string folder,string launch)
        {
            try{string path=Path.Combine(folder,"startup.json");if(!File.Exists(path))return null;var value=Json.Read<InstanceStartupResult>(SafeFiles.ReadText(path));return value!=null&&value.LaunchId==launch?value:null;}catch{return null;}
        }
        internal static string Timeout(InstanceState state,string launch)
        {return state!=null&&state.LaunchId==launch&&state.Running&&state.Phase=="starting"?"Codex est encore en cours de démarrage. L’hôte répond ; patientez puis actualisez.":"Échec du lancement : le lanceur n’a fourni aucun processus actif pour cette demande. Consultez le diagnostic de lancement ; aucune autre instance n’a été fermée.";}
    }
}
