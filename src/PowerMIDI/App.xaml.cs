using System.Windows;

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
        var window = new MainWindow(e.Args);
        window.Show();
        SingleInstance.Listen(paths => Dispatcher.BeginInvoke(() => window.OpenFromOtherLaunch(paths)));
    }
}
