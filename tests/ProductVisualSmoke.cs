using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Creezio.Switcher;

internal static class ProductVisualSmoke
{
    private static void Capture(Form form,string path,Size? size=null)
    {
        using(form){form.ShowInTaskbar=false;form.Opacity=0;if(size.HasValue){form.MinimumSize=Size.Empty;form.ClientSize=size.Value;}form.Show();DateTime until=DateTime.UtcNow.AddSeconds(1);while(DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(15);}using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(path,ImageFormat.Png);}form.Close();Application.DoEvents();}
    }
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var store=new RelayStore(Path.Combine(root,"store"));
        foreach(string id in new[]{"developpement","revue","outils-prives"})store.Register(new RelayChannel{Id=id,Name=id,AccountKey=id,Home=root,Workspace=root,Email=id+"@example.com",Enabled=true,AnchorThreadId="demo"});
        var p=new RelayPolicy();p.Projects.Add(new RelayProject{Id="mon-projet",Name="Mon projet",Workspace=root});p.Agents.Add(new RelayAgent{Channel="revue",Description="Relire et tester",Capabilities="review,test",AutoRoute=true});RelayPolicies.Save(store,p);
        var service=new AccountService(Path.Combine(root,"fake-accounts"));service.Settings.CodexHome=root;service.Settings.CodexExecutable="";service.Data.Profiles.Add(new Profile{Key="demo",Label="Compte de démonstration",Email="demo@example.com"});
        Capture(new OverviewForm(service,store,s=>{}),Path.Combine(root,"overview.png"));
        Capture(new SetupForm(service,store,s=>{}),Path.Combine(root,"setup.png"));
        Capture(new RelaySettingsForm(store,"Agents"),Path.Combine(root,"configuration-v06.png"));
        Capture(new RelayForm(store),Path.Combine(root,"travaux-v06.png"));
        Capture(new UsageSettingsForm(store,service),Path.Combine(root,"quotas-v06.png"));
        Capture(new MainForm(null,true),Path.Combine(root,"instances-v06.png"));
        Capture(new MainForm(null,true),Path.Combine(root,"small-v06.png"),new Size(1024,700));
        Capture(new RelayComposeForm(store,null),Path.Combine(root,"compose-v06.png"));
        Console.WriteLine("PASS 8 product screens rendered with fake data, no Codex instance launched");return 0;
    }
}
