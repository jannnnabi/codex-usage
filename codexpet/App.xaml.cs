using System.Windows;

namespace codexpet;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        NativeMethods.TryEnablePerMonitorDpi();
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
