using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Creezio.Switcher
{
    // A private Windows job makes every helper child terminate when this owner exits.
    internal sealed class ProcessJob : IDisposable
    {
        private IntPtr handle;
        [StructLayout(LayoutKind.Sequential)] private struct Basic { public long a,b; public uint flags; public UIntPtr min,max; public uint active; public UIntPtr affinity; public uint priority,scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong a,b,c,d,e,f; }
        [StructLayout(LayoutKind.Sequential)] private struct Extended { public Basic basic; public Io io; public UIntPtr processMemory,jobMemory,peakProcess,peakJob; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref Extended info, uint length);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        public ProcessJob(Process process)
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            var info = new Extended(); info.basic.flags = 0x2000;
            if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf(typeof(Extended))) || !AssignProcessToJobObject(handle, process.Handle))
            { Dispose(); throw new Win32Exception("Impossible d'isoler le processus Codex."); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
