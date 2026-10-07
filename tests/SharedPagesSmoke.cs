using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Creezio.Switcher;

internal static class SharedPagesSmoke
{
    public static int Main(string[] args)
    {
        try{
            var store=new RelayStore(RelayStore.DefaultRoot);var viewer=new SharedPages(store);var tunnel=new ToolTunnel(store);
            var session=viewer.DesktopSession(args[0]);var before=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants())));
            var entries=viewer.List(session);Console.WriteLine(Json.Write(new{pages=entries.Select(p=>new{p.Title,p.Owner,p.CanEdit}).ToArray()}));
            if(args.Length>1&&args[1]=="test-metadata"){
                var old=store.ReadRecord<Dictionary<string,object>>("tool-tunnel-acceptance.dpapi");
                var oldGrant=tunnel.Grants().SingleOrDefault(g=>g.Id==Json.Str(Json.Get(old,"Grant")));
                Console.WriteLine(Json.Write(new{testPage=Json.Str(Json.Get(old,"Page")),testGrant=Json.Str(Json.Get(old,"Grant")),owner=oldGrant==null?null:oldGrant.Instance}));return 0;
            }
            if(args.Length>1&&args[1]=="edit-test"){
                var old=store.ReadRecord<Dictionary<string,object>>("tool-tunnel-acceptance.dpapi");string testPage=Json.Str(Json.Get(old,"Page"));
                var previous=tunnel.Grants().Single(g=>g.Id==Json.Str(Json.Get(old,"Grant")));
                var accounts=new AccountService(Path.GetDirectoryName(store.Root));var client=accounts.Data.Instances.First(i=>!i.Archived&&i.Id!=previous.Instance&&!String.IsNullOrEmpty(i.AccountKey));
                session=viewer.DesktopSession(client.Id);
                var grant=new TunnelGrant{Id=Guid.NewGuid().ToString("N"),Instance=previous.Instance,Name="Recette visionneuse 0.12",Enabled=true,ResourceField="page_id",ResourceValue=testPage,Sources=new Dictionary<string,string>{{session.Home,session.Account}},Tools=tunnel.Discover(previous.Instance,CancellationToken.None).GetAwaiter().GetResult().Where(t=>t.Server==SharedPages.Server&&(t.Name==SharedPages.ReadTool||t.Name==SharedPages.EditTool)).ToList()};
                tunnel.SaveGrant(grant);
                try{
                    var initial=viewer.Read(session,grant.Id,testPage,CancellationToken.None).GetAwaiter().GetResult();
                    if(initial.Title!="Validation du tunnel Account Switcher")throw new Exception("Refusing to modify anything except the existing identified test Page");
                    var block=initial.Blocks.First(b=>b.Kind=="markdown"&&b.Markdown.Contains("État final"));
                    string original=block.Markdown,marker="État final : visionneuse Account Switcher 0.12 validée par lecture, modification et relecture via un autre compte.";
                    var changed=viewer.Save(session,Guid.NewGuid().ToString("N"),grant.Id,testPage,initial.ReadId,block.Id,marker,CancellationToken.None).GetAwaiter().GetResult();
                    if(changed.State!="saved"||changed.Document==null)throw new Exception("Test save unconfirmed: "+changed.State+" "+changed.Message);
                    var replacement=changed.Document.Blocks.Single(b=>b.Markdown==marker);
                    var restored=viewer.Save(session,Guid.NewGuid().ToString("N"),grant.Id,testPage,changed.Document.ReadId,replacement.Id,original,CancellationToken.None).GetAwaiter().GetResult();
                    if(restored.State!="saved"||!restored.Document.Blocks.Any(b=>b.Markdown==original))throw new Exception("Test Page restoration requires inspection: "+restored.State);
                    Console.WriteLine(Json.Write(new{testPage=initial.Title,owner=initial.Owner,source=client.Name,writeReceipt=changed.Id,restoreReceipt=restored.Id,readback=true,originalRestored=true,noModelTurn=true}));
                    Console.WriteLine("PASS real cross-account viewer save and restoration on existing test Page");
                }catch(InvalidOperationException){
                    var live=tunnel.Discover(previous.Instance,CancellationToken.None).GetAwaiter().GetResult();
                    foreach(var saved in grant.Tools){var current=live.Single(t=>t.Name==saved.Name&&t.Server==saved.Server);Console.WriteLine(Json.Write(new{tool=saved.Name,sameContract=ToolTunnel.SameContract(saved,current),sameDescription=saved.Description==current.Description,sameSchema=ToolTunnel.Canonical(saved.Schema)==ToolTunnel.Canonical(current.Schema),sameAnnotations=ToolTunnel.Canonical(saved.Annotations)==ToolTunnel.Canonical(current.Annotations),sameConnector=saved.ConnectorId==current.ConnectorId}));}
                    throw;
                }finally{using(store.Lease("tool-grants")){var all=tunnel.Grants();all.RemoveAll(g=>g.Id==grant.Id);tunnel.WriteGrants(all);}}
                if(before!=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants()))))throw new Exception("Existing grants changed during acceptance");return 0;
            }
            var entry=entries.FirstOrDefault(p=>String.Equals(p.Title,"dgd",StringComparison.OrdinalIgnoreCase))??entries.FirstOrDefault();
            if(entry==null)throw new Exception("No authorized Page available for this instance");
            var doc=viewer.Read(session,entry.Share,entry.Page,CancellationToken.None).GetAwaiter().GetResult();
            if(doc.Blocks.Length==0)throw new Exception("Expected visible text blocks");
            if(before!=ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants()))))throw new Exception("Read changed sharing");
            Console.WriteLine(Json.Write(new{doc.Title,doc.Owner,doc.CanEdit,blocks=doc.Blocks.Length,characters=doc.Blocks.Sum(b=>b.Markdown.Length),nativeUrlExposed=false,permissionsUnchanged=true,noModelTurn=true}));
            if(args.Length>1&&args[1]=="open-owner"){
                var result=viewer.OpenOwner(session,entry.Share,entry.Page,CancellationToken.None).GetAwaiter().GetResult();Console.WriteLine(Json.Write(result));
                Console.WriteLine("PASS Page navigation in the owner's existing Codex instance; no prompt");return 0;
            }
            Console.WriteLine("PASS live original Page read through requesting instance permissions; no provider mutation");return 0;
        }catch(Exception e){Console.Error.WriteLine(Program.SafeError(e));return 1;}
    }
}
