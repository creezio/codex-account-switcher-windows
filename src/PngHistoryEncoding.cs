using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Creezio.Switcher
{
    // Native rollout migration has a 16 MiB line limit. Repack PNG IDAT
    // streams in a private import snapshot; raster bytes, filtering and every
    // other PNG chunk remain identical. No source file or image is rewritten.
    internal sealed class PngHistoryEncoding
    {
        private const string Prefix="data:image/png;base64,";
        private const int MaxImage=32*1024*1024,MaxRaster=128*1024*1024;
        private readonly Dictionary<string,string> cache=new Dictionary<string,string>();
        private int cachedBytes;
        internal bool Changed;
        private sealed class Png {internal byte[] Raw,Ancillary;internal List<byte[]> Chunks;}
        internal void Optimize(object value)
        {
            var obj=value as IDictionary<string,object>;
            if(obj!=null){
                string url=Json.Str(Json.Get(obj,"image_url"));
                if(Json.Str(Json.Get(obj,"type"))=="input_image"&&url.StartsWith(Prefix,StringComparison.Ordinal)){
                    string key;using(var sha=SHA256.Create())key=Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(url)));
                    string replacement;if(!cache.TryGetValue(key,out replacement)){
                        byte[] source=Convert.FromBase64String(url.Substring(Prefix.Length));if(source.Length>MaxImage)throw new InvalidOperationException("Image trop volumineuse pour vérifier sa recompression sans perte.");
                        byte[] packed=Repack(source);
                        replacement=packed.Length<source.Length?Prefix+Convert.ToBase64String(packed):url;
                        if(cachedBytes+replacement.Length<=64*1024*1024){cache[key]=replacement;cachedBytes+=replacement.Length;}
                    }
                    if(replacement!=url){obj["image_url"]=replacement;Changed=true;}
                }
                foreach(var pair in obj.ToArray())Optimize(pair.Value);
            }else{var items=value as System.Collections.IEnumerable;if(items!=null&&!(value is string))foreach(var item in items)Optimize(item);}
        }
        internal static byte[] Repack(byte[] original)
        {
            var png=Read(original);byte[] deflated;
            using(var output=new MemoryStream()){
                using(var zip=new DeflateStream(output,CompressionLevel.Optimal,true))zip.Write(png.Raw,0,png.Raw.Length);
                deflated=output.ToArray();
            }
            // DeflateStream emits raw DEFLATE on both supported runtimes.
            var zlib=new byte[deflated.Length+6];zlib[0]=0x78;zlib[1]=0x9c;Buffer.BlockCopy(deflated,0,zlib,2,deflated.Length);Put(zlib,zlib.Length-4,Adler(png.Raw));
            byte[] packed;using(var output=new MemoryStream()){
                output.Write(original,0,8);bool written=false;
                foreach(var chunk in png.Chunks){if(Encoding.ASCII.GetString(chunk,4,4)=="IDAT"){if(!written){WriteChunk(output,"IDAT",zlib);written=true;}}else output.Write(chunk,0,chunk.Length);}
                packed=output.ToArray();
            }
            var verified=Read(packed);
            if(!png.Raw.SequenceEqual(verified.Raw)||!png.Ancillary.SequenceEqual(verified.Ancillary))throw new InvalidOperationException("La recompression PNG n’a pas conservé intégralement les pixels et métadonnées.");
            return packed;
        }
        private static Png Read(byte[] bytes)
        {
            if(bytes.Length>MaxImage||bytes.Length<20||!bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidOperationException("PNG non compatible avec une recompression vérifiée.");
            var chunks=new List<byte[]>();byte[] compressed,ancillary;bool seen=false,ended=false,iend=false;
            using(var idat=new MemoryStream())using(var other=new MemoryStream()){
                int offset=8;while(offset<bytes.Length){
                    if(bytes.Length-offset<12)throw new InvalidOperationException("PNG incomplet.");
                    uint length=Get(bytes,offset);if(length>MaxImage||length>(uint)(bytes.Length-offset-12))throw new InvalidOperationException("PNG incomplet.");
                    int size=(int)length;string type=Encoding.ASCII.GetString(bytes,offset+4,4);
                    if(Crc(bytes,offset+4,size+4)!=Get(bytes,offset+8+size))throw new InvalidOperationException("PNG dont la somme de contrôle est invalide.");
                    if(type=="acTL"||type=="fcTL"||type=="fdAT")throw new InvalidOperationException("Une image animée ne peut pas être recompressée pour cet import.");
                    var chunk=new byte[size+12];Buffer.BlockCopy(bytes,offset,chunk,0,chunk.Length);chunks.Add(chunk);
                    if(type=="IDAT"){if(ended)throw new InvalidOperationException("Blocs PNG non contigus.");seen=true;idat.Write(bytes,offset+8,size);}
                    else{if(seen)ended=true;other.Write(chunk,0,chunk.Length);}
                    offset+=size+12;if(type=="IEND"){iend=true;if(offset!=bytes.Length)throw new InvalidOperationException("Données inattendues après le PNG.");}
                }
                if(!seen||!iend)throw new InvalidOperationException("PNG incomplet.");compressed=idat.ToArray();ancillary=other.ToArray();
            }
            if(compressed.Length<6||(compressed[0]&15)!=8||((compressed[0]<<8)|compressed[1])%31!=0||(compressed[1]&32)!=0)throw new InvalidOperationException("Compression PNG non prise en charge.");
            byte[] raw;using(var input=new MemoryStream(compressed,2,compressed.Length-6))using(var zip=new DeflateStream(input,CompressionMode.Decompress))using(var output=new MemoryStream()){
                var buffer=new byte[65536];int count;while((count=zip.Read(buffer,0,buffer.Length))>0){if(output.Length+count>MaxRaster)throw new InvalidOperationException("Image décompressée trop volumineuse pour la vérifier.");output.Write(buffer,0,count);}raw=output.ToArray();
            }
            if(Adler(raw)!=Get(compressed,compressed.Length-4))throw new InvalidOperationException("Données PNG décompressées invalides.");
            return new Png{Raw=raw,Ancillary=ancillary,Chunks=chunks};
        }
        private static uint Get(byte[] b,int offset){return ((uint)b[offset]<<24)|((uint)b[offset+1]<<16)|((uint)b[offset+2]<<8)|b[offset+3];}
        private static void Put(byte[] b,int offset,uint value){b[offset]=(byte)(value>>24);b[offset+1]=(byte)(value>>16);b[offset+2]=(byte)(value>>8);b[offset+3]=(byte)value;}
        private static uint Adler(byte[] b){uint a=1,z=0;foreach(byte value in b){a=(a+value)%65521;z=(z+a)%65521;}return (z<<16)|a;}
        private static uint Crc(byte[] b,int offset,int count){uint crc=0xffffffff;for(int i=offset;i<offset+count;i++){crc^=b[i];for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)==0?0:0xedb88320);}return crc^0xffffffff;}
        private static void WriteChunk(Stream output,string type,byte[] data){var chunk=new byte[data.Length+12];Put(chunk,0,(uint)data.Length);Encoding.ASCII.GetBytes(type).CopyTo(chunk,4);data.CopyTo(chunk,8);Put(chunk,chunk.Length-4,Crc(chunk,4,data.Length+4));output.Write(chunk,0,chunk.Length);}
    }
}
