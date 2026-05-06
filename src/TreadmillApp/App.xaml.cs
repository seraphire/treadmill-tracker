using TreadmillApp.Services;

namespace TreadmillApp;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        ProcessPowerSettings.DisableEcoQos();
        base.OnStartup(e);
    }
}
