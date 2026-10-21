using System.Windows;

namespace TestMaker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "Test Maker — 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
