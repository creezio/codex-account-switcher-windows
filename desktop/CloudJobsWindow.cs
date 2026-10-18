using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class CloudJobsWindow : Window
    {
        internal readonly CloudJobsDashboard Dashboard;
        internal CloudJobsWindow(DesktopContext context, ShellWindow shell)
        {
            Owner=shell;Title="Délégations Cloud";Width=1220;Height=820;MinWidth=800;MinHeight=560;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty,"CanvasBrush");SetResourceReference(ForegroundProperty,"TextBrush");
            Dashboard=new CloudJobsDashboard(context,shell){Margin=new Thickness(22)};Content=Dashboard;Closed+=delegate{Dashboard.Suspend();};
        }
        internal Func<Task<CloudJob[]>> JobsFixture {get=>Dashboard.JobsFixture;set=>Dashboard.JobsFixture=value;}
        internal Func<CloudJob,CloudJobMonitorSnapshot> MonitoringFixture {get=>Dashboard.MonitoringFixture;set=>Dashboard.MonitoringFixture=value;}
        internal Func<string,CancellationToken,Task<CloudConfiguration[]>> EnvironmentsFixture {get=>Dashboard.EnvironmentsFixture;set=>Dashboard.EnvironmentsFixture=value;}
        internal Func<CloudJobSpec,CancellationToken,Task<CloudJob>> SubmitFixture {get=>Dashboard.SubmitFixture;set=>Dashboard.SubmitFixture=value;}
        internal Func<string,CancellationToken,Task<CloudJob>> AdvanceFixture {get=>Dashboard.AdvanceFixture;set=>Dashboard.AdvanceFixture=value;}
        internal Action EnsureWorkerFixture {get=>Dashboard.EnsureWorkerFixture;set=>Dashboard.EnsureWorkerFixture=value;}
        internal ListBox JobsList=>Dashboard.JobsList;
        internal ScrollViewer DetailsScroll=>Dashboard.DetailsScroll;
        internal TextBox ResultText=>Dashboard.ResultText;
        internal string SelectedJobId=>Dashboard.SelectedJobId;
        internal bool RefreshTimerRunning=>Dashboard.RefreshTimerRunning;
        internal bool Refreshing=>Dashboard.Refreshing;
        internal Task RefreshNow()=>Dashboard.RefreshNow();
        internal Task StartNewMission(string instanceId=null,string configurationId=null)=>Dashboard.StartNewMission(instanceId,configurationId);
        internal static string State(string state)=>CloudJobsDashboard.State(state);
    }
}
