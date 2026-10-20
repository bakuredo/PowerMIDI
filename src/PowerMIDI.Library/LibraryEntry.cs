using System.Security.Cryptography;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Library;

// A MIDI file of a library, as the database of its collection describes it (table files). Path: relative to the root of
// the collection, with '/' separators. Standard: null if the file could not be read (Error says why; the other values
// read from the file are then null too). Lyrics: the text of the karaoke lyrics, for the search only.
public sealed record LibraryEntry(string Path, string Name, string Title, string InternalTitle, string Copyright, long Size, string Sha1,
                                  string Standard, int? SmfFormat, int? Tracks, int? Channels, long? DurationMs, bool HasLyrics,
                                  bool HasLoop, string Lyrics, string Error)
{
    // Reads a file of a collection: its bytes once (SHA-1, then the MIDI data from memory).
    public static LibraryEntry Read(string root, string path) =>
        Read(path, File.ReadAllBytes(System.IO.Path.Combine(root, path.Replace('/', System.IO.Path.DirectorySeparatorChar))));

    // From the bytes of the file (an entry of the source zip).
    public static LibraryEntry Read(string path, byte[] bytes)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var entry = new LibraryEntry(path, name, TitleOf(name), null, null, bytes.Length, Convert.ToHexStringLower(SHA1.HashData(bytes)),
                                     null, null, null, null, null, false, false, null, null);
        try
        {
            var midi = MidiTools.ReadFile(new MemoryStream(bytes));
            var (title, copyright, _) = MidiTools.GetInfo(midi);
            var lyrics = KaraokeLyrics.Read(midi);
            int channels = 0;
            foreach (var on in midi.GetTrackChunks().SelectMany(c => c.Events).OfType<NoteOnEvent>())
                if (on.Velocity > 0) channels |= 1 << on.Channel;
            return entry with
            {
                InternalTitle = Clean(title), Copyright = Clean(copyright), Standard = MidiTools.GetStandard(midi),
                SmfFormat = midi.OriginalFormat switch { MidiFileFormat.SingleTrack => 0, MidiFileFormat.MultiTrack => 1, _ => 2 },
                Tracks = midi.GetTrackChunks().Count(), Channels = channels,
                DurationMs = (long)MidiTools.GetDuration(midi).TotalMilliseconds,
                HasLyrics = lyrics != null, HasLoop = MidiTools.GetLoop(midi) != null,
                Lyrics = lyrics == null ? null : string.Join(" ", lyrics.Lines.Select(l => MidiTools.DecodeText(string.Concat(l.Select(s => s.Text))).Trim())),
            };
        }
        catch (Exception ex)
        {
            return entry with { Error = ex.Message };
        }
    }

    // Name shown for a file: its name without the extension, '_' read as spaces (VGMusic names), spaces kept single.
    public static string TitleOf(string fileName)
    {
        var title = System.IO.Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ');
        title = string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return title.Length > 0 ? title : fileName;
    }

    // A text of the file, without control characters (some files end their texts with a 0 byte); null if empty.
    static string Clean(string text)
    {
        if (text == null) return null;
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length > 0 ? text : null;
    }
}
