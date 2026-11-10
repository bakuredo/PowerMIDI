using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PowerMIDI.ViewModels;

namespace PowerMIDI;

// Event Editor window (see EventEditorViewModel): scrolls to the row being played, plays from a double-clicked row.
public partial class EventEditorWindow : Window
{
    readonly EventEditorViewModel vm;

    public EventEditorWindow(EventEditorViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        vm.FollowRow += row => EventList.ScrollIntoView(row);
    }

    // Until its first picture (the list of a long track takes a moment), a new window is empty: Windows shows it white,
    // fading it in. It stays hidden from the screen (cloaked) until that picture, then appears drawn.
    // Loaded from the system folder only (never a DLL of the same name next to the program).
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    const int DwmCloak = 13;   // DWMWA_CLOAK (Windows 8 and later; without it, the window opens as before)

    void Cloak(bool on)
    {
        int value = on ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmCloak, ref value, sizeof(int));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Cloak(true);
        // Shown anyway after a second, should that first picture never come (an error while building it): never a
        // window open but invisible.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { timer.Stop(); Cloak(false); };
        timer.Start();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Cloak(false);
    }

    void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: EventRowViewModel row }) vm.SeekCommand.Execute(row);
    }
}
