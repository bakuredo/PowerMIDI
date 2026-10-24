using System.Windows;
using System.Windows.Threading;

namespace PowerMIDI;

public partial class App : Application
{
    // Files or folders given on the command line (or dropped on the exe) are added to the playlist.
    // PowerMIDI already running: they go to it instead (SingleInstance), and this launch exits.
    void OnStartup(object sender, StartupEventArgs e)
    {
        if (!SingleInstance.IsFirst() && SingleInstance.Send(e.Args))
        {
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;
        MainWindow window;
        try { window = new MainWindow(e.Args); }
        catch (Exception ex)
        {
            // The window could not be built: said, then PowerMIDI exits (it would otherwise run without a window).
            MessageBox.Show($"PowerMIDI cannot start: {ex.Message}", "PowerMIDI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        window.Show();
        SingleInstance.Listen(paths => Dispatcher.BeginInvoke(() => window.OpenFromOtherLaunch(paths)));
    }

    // An error that no action caught (most of them show their own message): shown, and PowerMIDI goes on instead of
    // closing at once (the settings would not be saved).
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var owner = MainWindow is { IsLoaded: true } main ? main : null;
            var text = $"Unexpected error: {e.Exception.Message}";
            if (owner != null) MessageBox.Show(owner, text, "PowerMIDI", MessageBoxButton.OK, MessageBoxImage.Error);
            else MessageBox.Show(text, "PowerMIDI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
