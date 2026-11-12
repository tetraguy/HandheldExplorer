using System.Windows;

namespace HandheldExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep the app alive if something unexpected goes wrong on the UI thread.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Handheld Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
