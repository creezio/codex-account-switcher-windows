using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Creezio.Switcher;

internal static class NavigationSmoke
{
    static void Assert(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static System.Collections.Generic.IEnumerable<Control> All(Control c){foreach(Control child in c.Controls){yield return child;foreach(var nested in All(child))yield return nested;}}
    static Button Button(Control form,string text){return All(form).OfType<Button>().First(b=>b.Text==text);}
    static void Pump(){Application.DoEvents();}
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles();var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        var service=new AccountService(root);service.Settings.CodexHome=Path.Combine(root,"home");Directory.CreateDirectory(service.Settings.CodexHome);service.Settings.AutoResetCredits=false;service.Settings.AutoRefresh=false;
        service.Data.Profiles.Add(new Profile{Key="fake",Label="Démonstration"});
        var store=new RelayStore(Path.Combine(root,"relay"));
        using(var form=new MainForm(service,false,store)){
            form.ShowInTaskbar=false;form.Opacity=0;form.Show();Pump();
            foreach(var page in new[]{"Comptes","Agents","Projets","Travaux","Limites et resets","Premiers pas","Instances","Vue d'ensemble"}){
                Button(form,page).PerformClick();Pump();Assert(form.CurrentSection==page,"navigation selects "+page);
                Assert(Button(form,page).AccessibleDescription=="Page actuelle","selected accessibility state "+page);
            }
            var pending=new TaskCompletionSource<int>();
            var operation=(Task)typeof(MainForm).GetMethod("RunOperation",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{new Func<Task>(()=>pending.Task),false});
            Button(form,"Comptes").PerformClick();Pump();Assert(form.CurrentSection=="Comptes","navigation remains usable during pending operation");pending.SetResult(0);Pump();
            Assert(operation.IsCompleted,"pending operation completes");
            Button(form,"Limites et resets").PerformClick();Pump();
            var pageForm=All(form).OfType<UsageSettingsForm>().Single();Button(pageForm,"Enregistrer la politique").PerformClick();Pump();
            Assert(!pageForm.IsDisposed&&form.CurrentSection=="Limites et resets","save keeps embedded limits page open");
            var saved=UsageCoordinator.Policies(store);Assert(saved.Accounts.Count==1,"limits saved in isolated store");
        }
        Console.WriteLine("PASS navigation smoke completed with fake accounts only");return 0;
    }
}
