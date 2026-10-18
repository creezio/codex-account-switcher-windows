using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace Creezio.Switcher.Desktop
{
    internal sealed class DesktopApp : Application
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [STAThread]
        public static int Main(string[] args)
        {
            if(args.Length==2&&args[0]=="--webhook-worker")return WebhookWorker.Run(args[1]);
            if(args.Length==2&&args[0]=="--conversation-schedule-worker")return PortalScheduleWorker.Run(args[1]);
            if(args.Length==2&&args[0]=="--chat-watch-worker")return ChatWatchWorker.Run(args[1]);
            if(args.Length==2&&args[0]=="--cloud-queue-worker")return CloudQueueWorker.Run(args[1]);
            if(args.Length==2&&args[0]=="--cloud-worker")return CloudJobWorker.Run(args[1]);
            if (args.Length == 2 && args[0] == "--instance-host")
                return InstanceHost.Run(args[1]);
            if (args.Length == 3 && args[0] == "--instance-show")
                return InstanceHost.Activate(args[1],args[2]);
            if (args.Length == 3 && args[0] == "--lifecycle-test")
                return InstanceLifecycleValidation.Run(args[1],Path.GetFullPath(args[2]));
            if (args.Length == 4 && args[0] == "--lifecycle-test" && args[3] == "--open-only")
                return InstanceLifecycleValidation.Run(args[1],Path.GetFullPath(args[2]),true);
            if (args.Length == 1 && args[0] == "--relay")
                return RelayCommand.Run();
            if (args.Length == 2 && args[0] == "--instance-test")
                return InstanceSmoke.Main(new[] { args[1] });
            if (args.Length == 2 && args[0] == "--compat-test")
                return DesktopTests.VerifyCompatibility(Path.GetFullPath(args[1]));
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            var app = new DesktopApp();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodexAccountSwitcher;component/Theme.xaml", UriKind.Relative) });
            if(args.Length==2&&args[0]=="--webhook-ui-test")return DesktopTests.RunWebhookUi(app,Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--ui-test")
                return DesktopTests.Run(app, Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--monitoring-ui-test")
                return DesktopTests.RunMonitoringUi(app, Path.GetFullPath(args[1]));
            if(args.Length==1&&args[0]=="--interventions"){
                var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Creezio","CodexAccountSwitcher");
                return app.Run(new HeadlessInterventionsWindow(new DesktopContext(root),null){WindowStartupLocation=WindowStartupLocation.CenterScreen});
            }
            if (args.Length == 3 && args[0] == "--shared-pages-live-test")
                return DesktopTests.SharedPagesLive(app,args[1],Path.GetFullPath(args[2]));
            bool first;
            using (var mutex = new Mutex(true, "Local\\Creezio.CodexAccountSwitcher", out first))
            {
                if (!first)
                {
                    if(!args.Contains("--startup"))SingleWindow.ActivateExisting();
                    return 0;
                }
                try
                {
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Creezio", "CodexAccountSwitcher");
                    string pending=args.Length>=2&&args[0]=="--update-pending"?args[1]:null;
                    return app.Run(new ShellWindow(new DesktopContext(root){ShowSurveillancesOnLoad=args.Length==1&&args[0]=="--surveillances",InitialPage=args.Length==1&&args[0]=="--performances"?"Performances":args.Length==1&&args[0]=="--storage"?"Stockage":args.Length==1&&args[0]=="--delegations"?"Délégations":args.Length==1&&args[0]=="--surveillances"?"Tâches":"Accueil"},args.Contains("--startup"),pending));
                }
                catch (Exception e) { MessageBox.Show(Program.SafeError(e), "Démarrage du switcher", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
            }
        }
    }
}
