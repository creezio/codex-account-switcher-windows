using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Creezio Codex Account Switcher")]
#if NETCOREAPP
[assembly: System.Reflection.AssemblyVersion("0.9.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.9.0-beta.2")]
#else
[assembly: System.Reflection.AssemblyVersion("0.9.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.9.0-beta.2")]
#endif
[assembly: System.Reflection.AssemblyCompany("Creezio")]

namespace Creezio.Switcher
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [STAThread] private static int Main(string[] args)
        {
            if(args.Length==2 && args[0]=="--instance-host") return InstanceHost.Run(args[1]);
            if(args.Length==1 && args[0]=="--relay") return RelayCommand.Run();
            SetProcessDPIAware();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length==2 && (args[0]=="--render-demo" || args[0]=="--render-accounts-demo"))
            {
                using(var form=new MainForm(null,true)) {if(args[0]=="--render-accounts-demo") form.ShowAccountsDemo();form.RenderDemo(Path.GetFullPath(args[1])); }
                return 0;
            }
            bool first;
            using(var instance=new Mutex(true,"Local\\Creezio.CodexAccountSwitcher",out first))
            {
                if(!first) { SingleWindow.ActivateExisting(); return 0; }
                try
                {
                    string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher");
                    var service=new AccountService(root);
                    Application.Run(new MainForm(service,false)); return 0;
                }
                catch(Exception error) { MessageBox.Show("L'application n'a pas pu démarrer.\n\n"+SafeError(error),"Creezio",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
            }
        }
        public static string SafeError(Exception error)
        {
            if(error is InvalidOperationException) return error.Message;
            if(error is OperationCanceledException) return "Opération annulée ou délai dépassé. Vous pouvez réessayer.";
            return "L'opération n'a pas abouti. Vérifiez les droits d'accès, le dossier Codex et l'espace disque disponible. Les jetons ne sont jamais affichés dans les diagnostics.";
        }
    }
}
