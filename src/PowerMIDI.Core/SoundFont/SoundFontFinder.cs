using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// SoundFont played with a MIDI file by the SoundFont engine (SF2 mode), looked for in this order:
// 1. the SF_BANK tag of the file: a Text event "SF_BANK=name.sf2" (the first one; written at the start by PowerMIDI);
//    the file is looked for next to the MIDI file, then in the soundfonts folder (a full path is taken as such);
// 2. the .sf2 with the same name as the MIDI file, next to it (song.mid: song.sf2);
// 3. the only .sf2 of the MIDI file's folder; if there are several, the one named after the folder;
// 4. the SoundFont chosen in the SoundFonts tab.
// A tag naming a file that cannot be found is said (Missing), and the search goes on.
public static class SoundFontFinder
{
    public const string Tag = "SF_BANK";
    static readonly Regex TagText = new(@"^\s*SF_BANK\s*[=:]\s*(.+?)\s*$", RegexOptions.IgnoreCase);

    public enum Source { Tag, SameName, Folder, Default }

    public sealed record Result(string Path, Source From, string Missing = null)
    {
        // How it was found, for the SoundFonts tab ("SF_BANK tag", "same name as file"…).
        public string FromText => From switch
        {
            Source.Tag => "SF_BANK tag",
            Source.SameName => "same name as file",
            Source.Folder => "SoundFont of folder",
            _ => "SoundFonts tab",
        };
    }

    // Name given by the SF_BANK tag of a file, or null.
    public static string TagValue(MidiFile midi)
    {
        foreach (var e in midi.GetTrackChunks().SelectMany(c => c.Events).OfType<TextEvent>())
            if (e.Text != null && TagText.Match(e.Text) is { Success: true } m) return m.Groups[1].Value.Trim('"');
        return null;
    }

    // midi: the file read (its tag), or null (not read: no tag). defaultPath: the SoundFonts tab's choice (null: none).
    public static Result Find(string midiPath, MidiFile midi, string soundFontsFolder, string defaultPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(midiPath));
        string missing = null;

        if (midi != null && TagValue(midi) is { Length: > 0 } name)
        {
            var candidates = Path.IsPathRooted(name) ? new[] { name } : new[] { Path.Combine(folder, name), Path.Combine(soundFontsFolder, name) };
            if (candidates.FirstOrDefault(File.Exists) is { } found) return new Result(Path.GetFullPath(found), Source.Tag);
            missing = name;
        }

        var sameName = Path.Combine(folder, Path.GetFileNameWithoutExtension(midiPath) + ".sf2");
        if (File.Exists(sameName)) return new Result(sameName, Source.SameName, missing);

        var inFolder = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.sf2") : Array.Empty<string>();
        if (inFolder.Length == 1) return new Result(inFolder[0], Source.Folder, missing);
        var named = inFolder.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase));
        if (named != null) return new Result(named, Source.Folder, missing);

        return new Result(defaultPath, Source.Default, missing);
    }
}
