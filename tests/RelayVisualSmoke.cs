using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Creezio.Switcher;

internal static class RelayVisualSmoke
{
    private static void Capture(Form form,string path,int tab=-1)
    {
        using(form){form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
            if(tab>=0){var tabs=form.Controls.OfType<TabControl>().First();tabs.SelectedIndex=tab;Application.DoEvents();}
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save(path,ImageFormat.Png);}form.Close();}
    }
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var store=new RelayStore(Path.Combine(root,"store"));
        foreach(string id in new[]{"developpement","revue","outils-prives"})store.Register(new RelayChannel{Id=id,Name=id,AccountKey=id,Home=root,Workspace=root,Email=id+"@example.com",Enabled=true,AnchorThreadId="demo"});
        var policy=new RelayPolicy();policy.Projects.Add(new RelayProject{Id="mon-projet",Name="Mon projet",Workspace=root});policy.Agents.Add(new RelayAgent{Channel="revue",Description="Relire et tester",Capabilities="review,test",AutoRoute=true});RelayPolicies.Save(store,policy);
        var task=store.Enqueue("developpement","demo","revue","Revue des modifications prêtes","Vérifier les cas limites et proposer les corrections.","commit exemple",true,null);
        task.State="completed";task.Outcome="succeeded";task.ReturnState="delivered";task.RoutingReason="Règle du projet · capacité review";task.Result="Revue terminée. Deux cas limites identifiés ; corrections proposées dans la conversation source.";store.Save(task);
        Capture(new RelayForm(store),Path.Combine(root,"travaux.png"));
        Capture(new RelaySettingsForm(store),Path.Combine(root,"configuration.png"),1);
        Capture(new RelayComposeForm(store,null),Path.Combine(root,"nouvelle-demande.png"));
        Console.WriteLine(root);return 0;
    }
}
