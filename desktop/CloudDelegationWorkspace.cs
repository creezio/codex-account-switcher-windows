using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudDelegationWorkspace : UserControl
    {
        internal readonly CloudJobsDashboard Dashboard;
        internal readonly CloudPreparedQueueView Prepared;
        internal readonly TabControl Sections = new TabControl { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        internal CloudDelegationWorkspace(DesktopContext context, ShellWindow shell)
        {
            Dashboard = new CloudJobsDashboard(context, shell);
            Prepared = new CloudPreparedQueueView(context, shell) { EnvironmentLoader = Dashboard.LoadEnvironments };
            Content = Sections;
            var style = new Style(typeof(TabItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 9, 18, 9)));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 8, 8)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AccentBrush")));
            style.Triggers.Add(selected);
            Sections.Items.Add(new TabItem { Header = "Missions", Content = Dashboard, Style = style });
            Sections.Items.Add(new TabItem { Header = "File préparée", Content = Prepared, Style = style });
            AutomationProperties.SetName(Sections, "Vues des délégations Cloud");
        }
        internal Task RefreshNow() => Sections.SelectedIndex == 1 ? Prepared.RefreshNow() : Dashboard.RefreshNow();
        internal void ShowPrepared() => Sections.SelectedIndex = 1;
        internal void Suspend() { Dashboard.Suspend(); Prepared.Suspend(); }
    }
}
