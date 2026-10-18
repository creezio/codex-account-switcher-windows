using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudDelegationsPage : PageView
    {
        internal readonly CloudJobsDashboard Dashboard;
        internal readonly CloudDelegationWorkspace Workspace;
        internal CloudDelegationsPage(DesktopContext context, ShellWindow shell)
            : base(context, shell, "Délégations", "Suivi des missions et des notifications de leurs orchestrateurs.")
        {
            Children.Clear();
            RowDefinitions.Clear();
            RowDefinitions.Add(new RowDefinition());
            Margin = new Thickness(24, 20, 24, 20);
            Workspace = new CloudDelegationWorkspace(context, shell);
            Dashboard = Workspace.Dashboard;
            Children.Add(Workspace);
        }
        public override Task Refresh() => Workspace.RefreshNow();
    }
}
