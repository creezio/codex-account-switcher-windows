using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Creezio.Switcher
{
    internal sealed class StorageBudgetVolume
    {internal string Root;internal long? AvailableBytes;}
    internal sealed class StorageBudgetDecision
    {
        internal string Volume,Reason;
        internal long? AvailableBytes,EstimatedBytes;
        internal long RequiredBytes,MarginBytes;
        internal bool Allowed,Large;
    }
    internal sealed class StorageBudgetException:InvalidOperationException
    {
        internal readonly StorageBudgetDecision Decision;
        internal StorageBudgetException(string operation,StorageBudgetDecision decision):base(StorageBudget.Message(operation,decision)){Decision=decision;}
    }
    // Admission only: reading free space never creates a folder, reserves a file,
    // cleans another operation's data or starts Codex. Auth and prompts do not use it.
    internal static class StorageBudget
    {
        internal const long MiB=1024L*1024,GiB=1024L*1024*1024;
        internal const long LargeOperationBytes=50L*MiB,MinimumFreeBytes=20L*GiB,DefaultMarginBytes=100L*MiB;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern bool GetDiskFreeSpaceEx(string directory,out ulong available,out ulong total,out ulong totalFree);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern bool GetVolumePathName(string path,StringBuilder volume,uint size);

        internal static StorageBudgetDecision Decide(string volume,long? available,long? estimate,long margin=DefaultMarginBytes)
        {
            if(margin<0||estimate<0||available<0)throw new ArgumentOutOfRangeException("estimate","Les tailles de stockage ne peuvent pas être négatives.");
            var result=new StorageBudgetDecision{Volume=volume??"disque inconnu",AvailableBytes=available,EstimatedBytes=estimate,MarginBytes=margin};
            if(!estimate.HasValue){result.Reason="estimate-unknown";return result;}
            result.Large=estimate.Value>LargeOperationBytes;
            try{result.RequiredBytes=checked(estimate.Value+margin);}catch(OverflowException){result.RequiredBytes=Int64.MaxValue;result.Reason="estimate-overflow";return result;}
            if(result.Large)result.RequiredBytes=Math.Max(result.RequiredBytes,MinimumFreeBytes);
            if(!available.HasValue){result.Reason="space-unknown";return result;}
            result.Allowed=available.Value>=result.RequiredBytes;result.Reason=result.Allowed?"allowed":"space-insufficient";return result;
        }
        internal static StorageBudgetDecision Inspect(string destination,long? estimate,long margin=DefaultMarginBytes,Func<string,StorageBudgetVolume> probe=null)
        {
            if(String.IsNullOrWhiteSpace(destination)||!Path.IsPathRooted(destination))throw new InvalidOperationException("Choisissez une destination de stockage absolue.");
            string full=Path.GetFullPath(destination);SafeFiles.RejectLinks(full);
            var disk=(probe??ReadVolume)(full)??new StorageBudgetVolume{Root=Path.GetPathRoot(full)};
            return Decide(disk.Root,disk.AvailableBytes,estimate,margin);
        }
        internal static void Require(string destination,long? estimate,string operation,long margin=DefaultMarginBytes,Func<string,StorageBudgetVolume> probe=null)
        {var decision=Inspect(destination,estimate,margin,probe);if(!decision.Allowed)throw new StorageBudgetException(operation,decision);}
        internal static StorageBudgetVolume ReadVolume(string destination)
        {
            string full=Path.GetFullPath(destination),folder=full;var result=new StorageBudgetVolume{Root=Path.GetPathRoot(full)};
            try{
                while(!Directory.Exists(folder)){folder=Path.GetDirectoryName(folder);if(String.IsNullOrEmpty(folder))return result;}
                var volume=new StringBuilder(1024);if(GetVolumePathName(folder,volume,(uint)volume.Capacity))result.Root=volume.ToString();
                ulong available,total,totalFree;if(GetDiskFreeSpaceEx(folder,out available,out total,out totalFree)&&available<=Int64.MaxValue)result.AvailableBytes=(long)available;
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}
            return result;
        }
        internal static long? Cost(long bytes,int simultaneousCopies)
        {if(bytes<0||simultaneousCopies<1)throw new ArgumentOutOfRangeException("bytes");try{return checked(bytes*simultaneousCopies);}catch(OverflowException){return null;}}
        // A prepared history may grow while paginated display items are rendered.
        // Recheck before crossing the large-write boundary, then every 8 MiB of
        // estimated peak cost. The caller retains cleanup of its own temporary.
        internal static Stream GuardWrites(Stream output,string destination,string operation,int copies=2,Func<string,StorageBudgetVolume> probe=null)
        {return new GuardedWriter(output,destination,operation,copies,probe);}
        private sealed class GuardedWriter:Stream
        {
            private readonly Stream output;private readonly string destination,operation;private readonly int copies;private readonly Func<string,StorageBudgetVolume> probe;private long checkedCost=-1;
            internal GuardedWriter(Stream output,string destination,string operation,int copies,Func<string,StorageBudgetVolume> probe)
            {if(output==null||!output.CanWrite||!output.CanSeek||copies<1)throw new ArgumentException("Flux de préparation invalide.");this.output=output;this.destination=destination;this.operation=operation;this.copies=copies;this.probe=probe;}
            public override void Write(byte[] buffer,int offset,int count)
            {
                if(buffer==null||offset<0||count<0||offset>buffer.Length-count)throw new ArgumentException("Bloc d’écriture invalide.");
                long length;try{length=Math.Max(output.Length,checked(output.Position+count));}catch(OverflowException){Require(destination,null,operation,DefaultMarginBytes,probe);throw;}
                long? cost=Cost(length,copies);
                if(!cost.HasValue||checkedCost<0||(cost>LargeOperationBytes&&checkedCost<=LargeOperationBytes)||cost.Value-checkedCost>=8*MiB){Require(destination,cost,operation,DefaultMarginBytes,probe);checkedCost=cost.Value;}
                output.Write(buffer,offset,count);
            }
            public override bool CanRead{get{return false;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return output.CanWrite;}}
            public override long Length{get{return output.Length;}}public override long Position{get{return output.Position;}set{throw new NotSupportedException();}}
            public override void Flush(){output.Flush();}public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)output.Dispose();base.Dispose(disposing);}
        }
        private static string Amount(long? bytes){return bytes.HasValue?(bytes.Value/(double)GiB).ToString("0.00",CultureInfo.GetCultureInfo("fr-FR"))+" Gio":"inconnu";}
        internal static string Message(string operation,StorageBudgetDecision value)
        {
            string reason=value.Reason=="estimate-unknown"?"Le volume à écrire ne peut pas être estimé.":value.Reason=="estimate-overflow"?"L’estimation dépasse la capacité prise en charge.":value.Reason=="space-unknown"?"L’espace disponible ne peut pas être vérifié.":"L’espace disponible est insuffisant.";
            return (String.IsNullOrWhiteSpace(operation)?"Opération volumineuse":operation)+" refusée. "+reason+" Disque « "+value.Volume+" » : "+Amount(value.AvailableBytes)+" disponibles ; "+(value.EstimatedBytes.HasValue?Amount(value.RequiredBytes):"estimation requise")+" requis (écriture estimée : "+Amount(value.EstimatedBytes)+", marge : "+Amount(value.MarginBytes)+(value.Large?", minimum avant opération : 20 Gio":"")+"). Ouvrez « Stockage » dans le Switcher pour examiner les temporaires connus, ou choisissez un autre disque. Pour un projet, le dossier commun évite une copie supplémentaire.";
        }
    }
}
