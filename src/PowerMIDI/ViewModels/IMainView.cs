namespace PowerMIDI.ViewModels;

// What the main view model asks of the window: dialog boxes, messages and the settings file
// (the window adds its own position and size to it).
public interface IMainView
{
    // MIDI files chosen in an open dialog, or null if cancelled.
    IReadOnlyList<string> PickMidiFiles(string initialFolder);

    // Folder chosen in a folder dialog, or null if cancelled.
    string PickFolder(string initialFolder);

    // Path chosen in a save dialog, or null if cancelled.
    string PickSaveFile(string initialFolder, string fileName);

    void ShowMessage(string text);
    void ShowError(Exception ex);

    // Yes = true, No = false, Cancel = null.
    bool? AskYesNoCancel(string text);

    // OK = true, Cancel = false.
    bool Confirm(string text) => true;

    // Writes PowerMIDI.ini (port, playback options and window geometry).
    void SaveSettings();

    // Wait cursor while files are being read.
    void SetBusy(bool busy);
}
