using PowerMIDI.ViewModels;

namespace PowerMIDI;

// Row of the playlist.
public sealed class PlaylistItem : ObservableObject
{
    string playing = "";
    int index;
    string standard, duration;
    bool unreadable;

    public string Name { get; init; }
    public string Path { get; init; }
    // Standard, duration and unreadable: known when the file is read, possibly after the row was added (many files are
    // read in the background, see MainViewModel.AddFiles).
    public string Standard { get => standard; set => Set(ref standard, value); }
    public string Duration { get => duration; set => Set(ref duration, value); }
    public bool Unreadable { get => unreadable; set => Set(ref unreadable, value); }   // could not be read: skipped when the playlist goes on by itself
    public ViewModels.LibraryKey Library { get; init; }   // file of a collection of the Library tab (its plays are counted)

    // "▶" (playing), "❚❚" (paused) or empty.
    public string Playing { get => playing; set => Set(ref playing, value); }
    public int Index { get => index; set => Set(ref index, value); }
}
