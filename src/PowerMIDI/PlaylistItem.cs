using PowerMIDI.ViewModels;

namespace PowerMIDI;

// Row of the playlist.
public sealed class PlaylistItem : ObservableObject
{
    string playing = "";
    int index;

    public string Name { get; init; }
    public string Path { get; init; }
    public string Standard { get; init; }
    public string Duration { get; init; }

    // "▶" (playing), "❚❚" (paused) or empty.
    public string Playing { get => playing; set => Set(ref playing, value); }
    public int Index { get => index; set => Set(ref index, value); }
}
