using System.Windows;
using System.Windows.Input;
using PowerMIDI.Behaviors;
using PowerMIDI.Core;
using PowerMIDI.ViewModels;

namespace PowerMIDI;

// PowerMIDI — MIDI file player and editor for sound modules: GM / GM2 / GS / XG / MT-32.
// The logic is in the view models (ViewModels\); the window only provides what is purely visual:
// dialog boxes, drag and drop, keyboard shortcuts, the position bar and its own position and size
// (saved with the other settings in PowerMIDI.ini, next to the program).
public partial class MainWindow : Window, IMainView
{
    static readonly string BaseDir = AppContext.BaseDirectory;
    static readonly string SettingsPath = Path.Combine(BaseDir, "PowerMIDI.ini");

    readonly MainViewModel vm;

    public MainWindow(string[] paths)
    {
        InitializeComponent();
        // Title with the version of the program (<Version> in PowerMIDI.csproj): "PowerMIDI 1.0.1", or "PowerMIDI 1.1" for 1.1.0.
        var version = typeof(MainWindow).Assembly.GetName().Version;
        Title = $"PowerMIDI {version.Major}.{version.Minor}" + (version.Build > 0 ? $".{version.Build}" : "");
        AboutTitle.Text = Title;
        // Position bar: a click on the track is handled by the slider itself (IsMoveToPointEnabled), so handlers set
        // in XAML would miss it; without IsSeeking, the timer would put the bar back before the button is released.
        PositionBar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPositionMouseDown), true);
        PositionBar.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPositionMouseUp), true);
        PositionBar.PreviewMouseRightButtonDown += OnPositionRightClick;
        var defs =InstrumentDefinitions.Load(Path.Combine(BaseDir, "instruments"));
        vm = new MainViewModel(this, defs);
        DataContext = vm;

        Run(ImportSettings);
        Run(vm.UpdatePortList);
        // First launch: creates PowerMIDI.ini right away (once the window is shown, to get its size).
        if (!File.Exists(SettingsPath))
            ContentRendered += (_, _) => Run(() => { if (!File.Exists(SettingsPath)) SaveSettings(); });
        if (paths.Length > 0) Loaded += (_, _) => Run(() => vm.AddFiles(paths));
        vm.Start();
    }

    // Files opened while PowerMIDI was already running (Explorer, command line): the window comes to the front, the
    // files replace the playlist and the first one plays.
    public void OpenFromOtherLaunch(string[] paths)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (paths.Length > 0) Run(() => vm.OpenFiles(paths));
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError(ex); }
    }

    // ---------- View events ----------

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) Run(() => vm.AddFiles((string[])e.Data.GetData(DataFormats.FileDrop)));
    }

    // Shortcuts handled before the focused control (so that Space does not click the focused button).
    // Keyboard mode (a channel number clicked in the Mixer): letters play notes, Esc stops the mode.
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Typing in a text box (search of the instrument list): no shortcut, no keyboard notes.
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;
        if (vm.KeyboardChannel >= 0 && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            if (e.Key == Key.Escape) { Run(() => vm.ToggleKeyboardChannel(vm.KeyboardChannel)); e.Handled = true; return; }
            bool used = false;
            Run(() => used = vm.KeyboardKeyDown(PhysicalKeys.ScanCode(e.Key), e.IsRepeat));
            if (used) { e.Handled = true; return; }
        }
        var command = e.Key switch
        {
            Key.Space or Key.MediaPlayPause => vm.PlayPauseCommand,
            Key.MediaNextTrack => vm.NextCommand,
            Key.MediaPreviousTrack => vm.PreviousCommand,
            Key.MediaStop => vm.StopCommand,
            _ => null,
        };
        if (command == null) return;
        command.Execute(null);
        e.Handled = true;
    }

    bool themeReady;   // false while the theme list is filled at startup

    // Theme chosen in the About tab: applied at once and remembered.
    void OnThemeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!themeReady || ThemeBox.SelectedItem is not string name || name == Themes.Current) return;
        Run(() =>
        {
            Themes.Apply(name);
            if (IsLoaded) SaveSettings();
        });
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        bool used = false;
        Run(() => used = vm.KeyboardKeyUp(PhysicalKeys.ScanCode(e.Key)));
        if (used) e.Handled = true;
    }

    // Another window gets the focus: the notes held on the computer keyboard are released (their key up would be lost).
    void OnDeactivated(object sender, EventArgs e) => Run(vm.ReleaseKeyboardNotes);

    // Double click on a row: plays that track. (A mouse binding on the list itself does not work on the
    // rows: they handle the click for the selection.)
    // Search box of an instrument list: focused when the list opens (after the list has focused its current item).
    void OnInstrumentSearchVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box && box.IsVisible)
            Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // Search box: Enter chooses the first sound found, Down goes to the list, Escape closes it.
    void OnInstrumentSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: ChannelViewModel ch } box) return;
        var combo = box.TemplatedParent as System.Windows.Controls.ComboBox;
        switch (e.Key)
        {
            case Key.Enter:
                Run(() => ch.ChooseFirstMatch());
                if (combo != null) combo.IsDropDownOpen = false;
                e.Handled = true;
                break;
            case Key.Down:
                var item = combo?.ItemContainerGenerator.ContainerFromItem(ch.SelectedInstrument ?? ch.InstrumentItems.FirstOrDefault())
                           as System.Windows.Controls.ComboBoxItem
                           ?? combo?.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ComboBoxItem;
                item?.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                if (combo != null) combo.IsDropDownOpen = false;
                e.Handled = true;
                break;
        }
    }

    void OnPlaylistItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListViewItem { DataContext: PlaylistItem item }) return;
        vm.SelectedTrack = item;
        vm.PlaySelectedCommand.Execute(null);
        e.Handled = true;
    }

    // The playing track is selected by the view model: keep it visible.
    void OnPlaylistSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PlaylistView.SelectedItem != null) PlaylistView.ScrollIntoView(PlaylistView.SelectedItem);
    }

    // Position bar: the jump happens when the mouse button is released.
    void OnPositionMouseDown(object sender, MouseButtonEventArgs e) => vm.IsSeeking = true;
    void OnPositionMouseUp(object sender, MouseButtonEventArgs e) => vm.EndSeek();

    // Right click: the time under the mouse, for the loop menu.
    void OnPositionRightClick(object sender, MouseButtonEventArgs e)
    {
        double width = PositionBar.ActualWidth;
        if (width > 0) vm.PrepareLoopMenu(Math.Clamp(e.GetPosition(PositionBar).X / width, 0, 1) * PositionBar.Maximum);
    }

    // Link of the About tab: opened in the default browser.
    void OnLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing with unsaved settings: same question as when changing tracks.
        bool keepOpen = false;
        try { keepOpen = !vm.ConfirmLeaveSettings(); } catch { }
        if (keepOpen) { e.Cancel = true; return; }
        try { SaveSettings(); } catch { }
        CloseEventEditor();
        vm.Shutdown();
    }

    // ---------- IMainView ----------

    public IReadOnlyList<string> PickMidiFiles(string initialFolder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "MIDI files (*.mid;*.midi;*.kar;*.rmi)|*.mid;*.midi;*.kar;*.rmi|All files|*.*",
            Multiselect = true,
        };
        if (initialFolder != null) dlg.InitialDirectory = initialFolder;
        return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
    }

    public string PickFolder(string initialFolder)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder of MIDI files (subfolders included)" };
        if (initialFolder != null) dlg.InitialDirectory = initialFolder;
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    public string PickSaveFile(string initialFolder, string fileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "MIDI file (*.mid)|*.mid",
            InitialDirectory = initialFolder,
            FileName = fileName,
            OverwritePrompt = true,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    public void ShowMessage(string text) => MessageBox.Show(this, text, "PowerMIDI");

    public void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "PowerMIDI", MessageBoxButton.OK, MessageBoxImage.Error);

    public bool? AskYesNoCancel(string text) =>
        MessageBox.Show(this, text, "PowerMIDI", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public void SetBusy(bool busy) => Cursor = busy ? Cursors.Wait : null;

    // Event Editor: one window, of its own (not kept above this one), closed with this one.
    EventEditorWindow eventWindow;

    public void ShowEventEditor(EventEditorViewModel editor, Action closed)
    {
        if (eventWindow != null) { eventWindow.Activate(); return; }
        eventWindow = new EventEditorWindow(editor);
        eventWindow.Closed += (_, _) => { eventWindow = null; closed(); };
        eventWindow.Show();
    }

    void CloseEventEditor() => eventWindow?.Close();

    // ---------- PowerMIDI settings (PowerMIDI.ini, next to the program) ----------

    // Writes the current settings. Called on close and when a port is chosen.
    public void SaveSettings()
    {
        var ini = new IniFile();
        vm.WriteSettings(ini);
        // Maximized window: remember the "normal" size for restoring.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        ini.Set("Window", "Left", (int)bounds.Left);
        ini.Set("Window", "Top", (int)bounds.Top);
        ini.Set("Window", "Width", (int)bounds.Width);
        ini.Set("Window", "Height", (int)bounds.Height);
        ini.Set("Window", "Maximized", WindowState == WindowState.Maximized ? 1 : 0);
        ini.Set("Window", "Theme", Themes.Current);
        ini.Write(SettingsPath, "PowerMIDI settings (written on close).");
    }

    // Position of the window at the first start (its size is the one of the XAML).
    const double DefaultLeft = 312, DefaultTop = 270;

    // Applies PowerMIDI.ini at startup. Any missing or invalid value is ignored.
    void ImportSettings()
    {
        var ini = IniFile.Read(SettingsPath);
        vm.LoadSettings(ini);

        // Position and size: only if the window stays visible on one of the current screens.
        if (ini.GetInt("Window", "Left") is int left && ini.GetInt("Window", "Top") is int top &&
            ini.GetInt("Window", "Width") is int width && ini.GetInt("Window", "Height") is int height &&
            width >= MinWidth && height >= MinHeight)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(left, top, width, height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left; Top = top; Width = width; Height = height;
            }
        }
        // No position remembered (first start): the default one if the window fits there on the main screen,
        // otherwise centered (WindowStartupLocation of the XAML).
        else if (SystemParameters.WorkArea.Contains(new Rect(DefaultLeft, DefaultTop, Width, Height)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = DefaultLeft; Top = DefaultTop;
        }
        if (ini.Get("Window", "Maximized") == "1") WindowState = WindowState.Maximized;

        // Color theme (About tab); a missing or unknown name gives the default one.
        Themes.Apply(ini.Get("Window", "Theme"));
        ThemeBox.ItemsSource = Themes.Names;
        ThemeBox.SelectedItem = Themes.Current;
        themeReady = true;
    }
}
