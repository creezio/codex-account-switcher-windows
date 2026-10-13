using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Creezio.Switcher
{
    internal sealed class ResourceProcess
    {
        internal int Pid,Parent;
        internal long Started;
        internal string Name,Role,Owner,Association;
        internal long? Resident,PrivateBytes;
        internal double? CpuSeconds,CpuPercent;
    }
    internal sealed class ResourceScope
    {
        internal string Id=null,Name=null,Account=null;
        internal bool Primary=false,Managed=false;
        internal InstanceState State=null;
        internal string Home=null;
    }
    internal sealed class ResourceGroup
    {
        internal ResourceScope Scope;
        internal ResourceProcess[] Processes;
        internal long Resident,PrivateBytes;
        internal double? Cpu;
        internal bool Partial;
        internal bool CpuPartial=false;
    }
    internal sealed class ResourceSnapshot
    {
        internal DateTime Captured;
        internal ResourceGroup[] Groups;
        internal ResourceProcess[] Unassigned;
        internal long TotalPhysical,AvailablePhysical;
        internal double CollectionMilliseconds;
    }
    internal sealed class ProcessResources
    {
        private Dictionary<int,ResourceProcess> previous=new Dictionary<int,ResourceProcess>();
        private double previousClock;
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private Dictionary<string,string> roles=new Dictionary<string,string>();
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Entry {public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
        [StructLayout(LayoutKind.Sequential)] private struct Memory {public uint Size,Load;public ulong Total,Available,TotalPage,AvailablePage,TotalVirtual,AvailableVirtual,Extended;}
        [StructLayout(LayoutKind.Sequential)] private struct NativeString {public ushort Length,Maximum;public IntPtr Buffer;}
        [StructLayout(LayoutKind.Sequential)] private struct ProcessMemory {public uint Size,Faults;public UIntPtr PeakWorking,Working,PeakPaged,Paged,PeakNonPaged,NonPaged,Pagefile,PeakPagefile,Private;}
        [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32First(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32Next(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref Memory memory);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr process,ref ProcessMemory memory,uint size);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process,int kind,IntPtr value,int size,out int returned);
        private static string CommandLine(int pid)
        {
            var handle=OpenProcess(0x1000,false,pid);if(handle==IntPtr.Zero)return null;
            try{int length;NtQueryInformationProcess(handle,60,IntPtr.Zero,0,out length);if(length<=0||length>262144)return null;var buffer=Marshal.AllocHGlobal(length);
                try{if(NtQueryInformationProcess(handle,60,buffer,length,out length)!=0)return null;var text=(NativeString)Marshal.PtrToStructure(buffer,typeof(NativeString));long offset=text.Buffer.ToInt64()-buffer.ToInt64();if(offset<0||offset+text.Length>length||text.Length%2!=0)return null;return Marshal.PtrToStringUni(text.Buffer,text.Length/2);}finally{Marshal.FreeHGlobal(buffer);}
            }finally{CloseHandle(handle);}
        }
        private static bool CodexName(string name)
        {return new[]{"ChatGPT.exe","Codex.exe","CreezioRelay.exe","CodexAccountSwitcher.exe","node_repl.exe","codex-computer-use.exe"}.Contains(name,StringComparer.OrdinalIgnoreCase);}
        internal static string Role(string name,string command)
        {
            command=command??"";
            if(String.Equals(name,"ChatGPT.exe",StringComparison.OrdinalIgnoreCase)){
                if(command.Contains("--type=renderer"))return "Rendu de fenêtre";
                if(command.Contains("--type=gpu-process"))return "Affichage GPU";
                if(command.Contains("--type=utility"))return "Service de fenêtre / navigateur";
                if(command.Contains("crashpad"))return "Rapports de plantage";
                return "Application Codex";
            }
            if(String.Equals(name,"codex.exe",StringComparison.OrdinalIgnoreCase))return "Moteur Codex";
            if(String.Equals(name,"CreezioRelay.exe",StringComparison.OrdinalIgnoreCase))return "Relais / outils partagés";
            if(String.Equals(name,"CodexAccountSwitcher.exe",StringComparison.OrdinalIgnoreCase))return "Switcher / hôte d’instance";
            if(String.Equals(name,"node_repl.exe",StringComparison.OrdinalIgnoreCase)||String.Equals(name,"codex-computer-use.exe",StringComparison.OrdinalIgnoreCase))return "Outils et navigateur";
            if(new[]{"powershell.exe","pwsh.exe","cmd.exe","conhost.exe"}.Contains(name,StringComparer.OrdinalIgnoreCase))return "Commande / terminal";
            if(String.Equals(name,"node.exe",StringComparison.OrdinalIgnoreCase))return "Plugin, script ou serveur Node";
            return "Processus enfant";
        }
        // PID alone is never an identity. A reused parent PID must not steal a live family.
        internal static ResourceSnapshot Build(ResourceProcess[] rows,ResourceScope[] scopes,ResourceProcess[] before,double elapsed,int processors)
        {
            var byPid=rows.ToDictionary(p=>p.Pid);var roots=new Dictionary<int,string>();
            foreach(var scope in scopes){var state=scope.State;if(state==null||!state.Running)continue;
                foreach(var pair in new[]{new KeyValuePair<int,long>(state.DesktopPid,state.DesktopStartTicks),new KeyValuePair<int,long>(state.HostPid,state.HostStartTicks)}){
                    ResourceProcess process;if(pair.Key>0&&pair.Value>0&&byPid.TryGetValue(pair.Key,out process)&&process.Started==pair.Value){if(roots.ContainsKey(pair.Key)&&roots[pair.Key]!=scope.Id)throw new InvalidOperationException("Attribution de processus ambiguë. Aucune action de fermeture n’est proposée.");roots[pair.Key]=scope.Id;}
                }
            }
            var old=before.ToDictionary(p=>p.Pid);
            foreach(var row in rows){row.Owner=null;row.Association=null;row.CpuPercent=null;var cursor=row;var seen=new HashSet<int>();
                while(cursor!=null&&seen.Add(cursor.Pid)){
                    string owner;if(cursor.Started>0&&roots.TryGetValue(cursor.Pid,out owner)){row.Owner=owner;row.Association=row.Pid==cursor.Pid?"Processus identifié":"Descendant vérifié";break;}
                    ResourceProcess parent;if(cursor.Started<=0||!byPid.TryGetValue(cursor.Parent,out parent)||parent.Started<=0||parent.Started>cursor.Started)break;cursor=parent;
                }
                ResourceProcess prior;if(elapsed>0&&elapsed<=30&&processors>0&&row.CpuSeconds.HasValue&&old.TryGetValue(row.Pid,out prior)&&prior.Started==row.Started&&prior.CpuSeconds.HasValue){double delta=row.CpuSeconds.Value-prior.CpuSeconds.Value;if(delta>=0)row.CpuPercent=Math.Min(100,100*delta/elapsed/processors);}
            }
            var groups=scopes.Select(scope=>{
                var children=rows.Where(p=>p.Owner==scope.Id).ToArray();var measured=children.Where(p=>p.CpuPercent.HasValue).ToArray();
                return new ResourceGroup{Scope=scope,Processes=children,Resident=children.Sum(p=>p.Resident??0),PrivateBytes=children.Sum(p=>p.PrivateBytes??0),Cpu=measured.Length>0?(double?)measured.Sum(p=>p.CpuPercent.Value):null,CpuPartial=measured.Length<children.Length,Partial=children.Any(p=>!p.Resident.HasValue||!p.PrivateBytes.HasValue)||scope.State!=null&&scope.State.Running&&!children.Any()};
            }).ToArray();
            return new ResourceSnapshot{Captured=DateTime.UtcNow,Groups=groups,Unassigned=rows.Where(p=>p.Owner==null&&CodexName(p.Name)).ToArray()};
        }
        internal ResourceSnapshot Capture(ResourceScope[] scopes)
        {
            double started=clock.Elapsed.TotalMilliseconds;var rows=new List<ResourceProcess>();var handle=CreateToolhelp32Snapshot(2,0);
            if(handle==new IntPtr(-1))throw new InvalidOperationException("Windows ne permet pas de lire les processus.");
            try{var entry=new Entry{Size=(uint)Marshal.SizeOf(typeof(Entry))};if(!Process32First(handle,ref entry))throw new InvalidOperationException("La liste de processus n’est pas disponible.");
                do{var row=new ResourceProcess{Pid=(int)entry.Pid,Parent=(int)entry.Parent,Name=entry.Exe};if(row.Pid<=0)continue;
                    // One handle and two native reads per PID. Diagnostics.Process
                    // memory properties otherwise enumerate all system processes
                    // repeatedly on Framework, adding avoidable monitoring load.
                    var process=OpenProcess(0x1000,false,row.Pid);if(process!=IntPtr.Zero)try{
                        long created,exited,kernel,user;if(GetProcessTimes(process,out created,out exited,out kernel,out user)){if(exited>0)continue;row.Started=DateTime.FromFileTimeUtc(created).Ticks;row.CpuSeconds=(kernel+user)/10000000d;}
                        var processMemory=new ProcessMemory{Size=(uint)Marshal.SizeOf(typeof(ProcessMemory))};if(GetProcessMemoryInfo(process,ref processMemory,processMemory.Size)){row.Resident=(long)processMemory.Working.ToUInt64();row.PrivateBytes=(long)processMemory.Private.ToUInt64();}
                    }finally{CloseHandle(process);}
                    rows.Add(row);
                }while(Process32Next(handle,ref entry));
            }finally{CloseHandle(handle);}
            double now=clock.Elapsed.TotalSeconds;var result=Build(rows.ToArray(),scopes,previous.Values.ToArray(),previousClock==0?0:now-previousClock,Environment.ProcessorCount);
            var keepRoles=new Dictionary<string,string>();foreach(var row in result.Groups.SelectMany(g=>g.Processes).Concat(result.Unassigned)){
                string key=row.Pid+":"+row.Started,role;if(!roles.TryGetValue(key,out role))role=Role(row.Name,CommandLine(row.Pid));row.Role=role;keepRoles[key]=role;
            }
            roles=keepRoles;previous=rows.ToDictionary(p=>p.Pid);previousClock=now;
            var memory=new Memory{Size=(uint)Marshal.SizeOf(typeof(Memory))};if(GlobalMemoryStatusEx(ref memory)){result.TotalPhysical=(long)memory.Total;result.AvailablePhysical=(long)memory.Available;}
            result.CollectionMilliseconds=clock.Elapsed.TotalMilliseconds-started;return result;
        }
    }
    internal sealed class ResourceActivity
    {
        internal string State="unknown",Message="Activité non vérifiée";
        internal DateTime Checked;
        internal int Active,Waiting;
        internal int Pid=0;
        internal long Started=0;
        internal string[] Titles=new string[0];
        internal static ResourceActivity FromSidebar(object value)
        {
            var data=Json.Get(value,"threads");var pinned=Json.Get(value,"pinnedThreads");
            if(data==null||pinned==null||RelayEngine.Rows(Json.Get(value,"unavailableHosts")).Any()||RelayEngine.Rows(Json.Get(value,"unavailableSources")).Any())return new ResourceActivity{Message="Activité partiellement disponible",Checked=DateTime.UtcNow};
            var rows=RelayEngine.Rows(data).Concat(RelayEngine.Rows(pinned)).Where(r=>Json.Str(Json.Get(r,"kind"))=="codex"&&new[]{"","local"}.Contains(Json.Str(Json.Get(r,"hostId")))).GroupBy(r=>Json.Str(Json.Get(r,"id"))).Select(g=>g.First()).ToArray();
            var active=rows.Where(r=>Json.Str(Json.Get(r,"status"))=="active").ToArray();var waiting=rows.Where(r=>new[]{"waiting","waitingForApproval","needsAttention"}.Contains(Json.Str(Json.Get(r,"status")))).ToArray();
            bool unknown=rows.Any(r=>!new[]{"active","idle","notLoaded","waiting","waitingForApproval","needsAttention"}.Contains(Json.Str(Json.Get(r,"status"))));
            return new ResourceActivity{Checked=DateTime.UtcNow,Active=active.Length,Waiting=waiting.Length,State=active.Length+waiting.Length>0?"busy":unknown?"unknown":"visible_idle",Message=active.Length+waiting.Length>0?active.Length+" chat(s) actif(s) · "+waiting.Length+" en attente":unknown?"État de certains chats inconnu":"Chats visibles au repos · contrôle complet avant fermeture",Titles=active.Concat(waiting).Select(r=>Json.Str(Json.Get(r,"title"))).ToArray()};
        }
    }
}
