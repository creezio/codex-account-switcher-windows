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
            if (args.Length == 2 && args[0] == "--instance-host")
                return InstanceHost.Run(args[1]);
            if (args.Length == 1 && args[0] == "--relay")
                return RelayCommand.Run();
            if (args.Length == 2 && args[0] == "--instance-test")
                return InstanceSmoke.Main(new[] { args[1] });
            if (args.Length == 2 && args[0] == "--compat-test")
                return DesktopTests.VerifyCompatibility(Path.GetFullPath(args[1]));
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            var app = new DesktopApp();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodexAccountSwitcher;component/Theme.xaml", UriKind.Relative) });
            if (args.Length == 2 && args[0] == "--ui-test")
                return DesktopTests.Run(app, Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--shared-pages-live-test")
                return DesktopTests.SharedPagesLive(app,args[1],Path.GetFullPath(args[2]));
            bool first;
            using (var mutex = new Mutex(true, "Local\\Creezio.CodexAccountSwitcher", out first))
            {
                if (!first)
                {
                    SingleWindow.ActivateExisting();
                    return 0;
                }
                try
                {
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Creezio", "CodexAccountSwitcher");
                    return app.Run(new ShellWindow(new DesktopContext(root)));
                }
                catch (Exception e) { MessageBox.Show(Program.SafeError(e), "Démarrage du switcher", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
            }
        }
    }
}
