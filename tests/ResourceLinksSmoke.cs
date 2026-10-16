using System;
using System.IO;
using System.Linq;
using System.Threading;
using Creezio.Switcher;

internal static class ResourceLinksSmoke
{
    public static int Main(string[] args)
    {
        try{
            var store=new RelayStore(RelayStore.DefaultRoot);var tunnel=new ToolTunnel(store);var service=new SharedResourceLinks(store);
            var session=new SharedPages(store).DesktopSession(args[0]);string field=args.Length>1&&args[1]=="site"?"project_id":"page_id";
            var grants=tunnel.Grants().Where(g=>ToolTunnel.CanUse(g,session)&&g.ResourceField==field).ToArray();
            var grant=grants.FirstOrDefault(g=>g.ResourceLabels!=null&&g.ResourceLabels.Values.Any(v=>String.Equals(v,"dgd",StringComparison.OrdinalIgnoreCase)))??grants.FirstOrDefault();
            if(grant==null){Console.WriteLine("No authorized "+field+" grant for this instance. No access was added.");return 2;}
            string resource=grant.ResourceValues!=null&&grant.ResourceValues.Count>0?grant.ResourceValues[0]:grant.ResourceValue;
            var before=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants())));var link=service.Create(session,grant.Id,resource);
            Console.WriteLine(Json.Write(link));
            if(args.Length>2&&args[2]=="open")Console.WriteLine(Json.Write(service.Open(new Uri(link.Url).AbsolutePath.Substring(3),CancellationToken.None).GetAwaiter().GetResult()));
            if(before!=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants()))))throw new Exception("Sharing changed");
            Console.WriteLine("PASS existing resource link; grants unchanged; no content edit or prompt");return 0;
        }catch(Exception e){Console.Error.WriteLine(Program.SafeError(e));return 1;}
    }
}
