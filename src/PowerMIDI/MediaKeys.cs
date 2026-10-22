using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PowerMIDI;

// Media keys of the keyboard (play / pause, next, previous, stop) for the whole session, as long as PowerMIDI runs, even
// when its window is in the background or minimized: registered as hot keys of the window (RegisterHotKey). Windows then
// gives them to PowerMIDI only (other players do not get them while it runs). A key another program has already taken
// is left to it (it still works in the window, see MainWindow.OnPreviewKeyDown).
public sealed class MediaKeys : IDisposable
{
    const int WmHotKey = 0x0312;
    const uint ModNoRepeat = 0x4000;   // a key held down: one press

    // Virtual key codes of the media keys, also the ids of their hot keys.
    static readonly (int VirtualKey, Key Key)[] Keys =
    {
        (0xB3, Key.MediaPlayPause), (0xB0, Key.MediaNextTrack), (0xB1, Key.MediaPreviousTrack), (0xB2, Key.MediaStop),
    };

    // Loaded from the system folder only (never a DLL of the same name next to the program).
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly HwndSource source;
    readonly Action<Key> pressed;
    readonly List<int> registered = new();

    // The keys taken (the others: already taken by another program).
    public IReadOnlyList<Key> Registered => registered.Select(id => Keys.First(k => k.VirtualKey == id).Key).ToList();

    // For a window that has its handle (after SourceInitialized); pressed: the key, on the thread of the window.
    public MediaKeys(Window window, Action<Key> pressed)
    {
        this.pressed = pressed;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source.AddHook(WndProc);
        foreach (var (virtualKey, _) in Keys)
            if (RegisterHotKey(source.Handle, virtualKey, ModNoRepeat, (uint)virtualKey)) registered.Add(virtualKey);
    }

    IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && registered.Contains(wParam.ToInt32()))
        {
            pressed(Keys.First(k => k.VirtualKey == wParam.ToInt32()).Key);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in registered) UnregisterHotKey(source.Handle, id);
        registered.Clear();
        source.RemoveHook(WndProc);
    }
}
