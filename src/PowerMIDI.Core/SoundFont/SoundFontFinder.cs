using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// SoundFont played with a MIDI file by the SoundFont engine (SF2 mode), looked for in this order:
// 1. the SF_BANK tag of the file (unless not read, see the SoundFonts tab): a Text event "SF_BANK=name.sf2" (the first
//    one; written at the start by PowerMIDI's save, see WriteTag); the file is looked for next to the MIDI file, then in
//    the soundfonts folder (a full path is taken as such);
// 2. the .sf2 with the same name as the MIDI file, next to it (song.mid: song.sf2);
// 3. the only .sf2 of the MIDI file's folder; if there are several, the one named after the folder;
// 4. the SoundFont chosen in the SoundFonts tab.
// A tag naming a file that cannot be found is said (Missing), and the search goes on.
public static class SoundFontFinder
{
    public const string Tag = "SF_BANK";
    static readonly Regex TagText = new(@"^\s*SF_BANK\s*[=:]\s*(.+?)\s*$", RegexOptions.IgnoreCase);

    // Chosen: picked in the list of SoundFonts for the track loaded (not by Find).
    public enum Source { Tag, SameName, Folder, Default, Chosen }

    public sealed record Result(string Path, Source From, string Missing = null)
    {
        // How it was found, for the SoundFonts tab ("SF_BANK tag", "same name as file"…).
        public string FromText => From switch
        {
            Source.Tag => "SF_BANK tag",
            Source.SameName => "same name as file",
            Source.Folder => "SoundFont of folder",
            Source.Chosen => "chosen in list",
            _ => "SoundFonts tab",
        };
    }

    // The soundfonts folder (the files of the SoundFonts tab; the SF_BANK files not next to the MIDI file): the Folder
    // setting of PowerMIDI.ini ([SoundFont]), its environment variables replaced (%USERPROFILE%…), a relative path taken
    // from the program's folder; empty: the soundfonts folder next to the program.
    public const string DefaultFolder = "soundfonts";

    public static string FolderOf(string setting, string programFolder)
    {
        var value = Environment.ExpandEnvironmentVariables(setting?.Trim().Trim('"') ?? "");
        if (value == "") value = DefaultFolder;
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(programFolder, value));
    }

    // Name given by the SF_BANK tag of a file, or null.
    public static string TagValue(MidiFile midi) => TagEvent(midi) is { } e ? TagText.Match(e.Text).Groups[1].Value.Trim('"') : null;

    static TextEvent TagEvent(MidiFile midi) =>
        midi.GetTrackChunks().SelectMany(c => c.Events).OfType<TextEvent>().FirstOrDefault(e => e.Text != null && TagText.IsMatch(e.Text));

    // Writes the SF_BANK tag of a file: the text of its tag replaced, otherwise a Text event at the start of its first
    // track (the times of the events kept). Returns false when the file has that tag already.
    public static bool WriteTag(MidiFile midi, string value)
    {
        var text = $"{Tag}={value}";
        if (TagEvent(midi) is { } tag)
        {
            if (TagValue(midi) == value) return false;
            tag.Text = text;
            return true;
        }
        var first = midi.GetTrackChunks().FirstOrDefault();
        if (first == null) midi.Chunks.Add(first = new TrackChunk());
        first.Events.Insert(0, new TextEvent(text));
        return true;
    }

    // Value of the tag naming a SoundFont, for a MIDI file saved at midiPath: its name alone when the search finds it so
    // (next to the MIDI file, otherwise in the soundfonts folder: see Find), its full path otherwise (also when another
    // file of that name next to the MIDI file would be found first).
    public static string TagFor(string soundFontPath, string midiPath, string soundFontsFolder)
    {
        var full = Path.GetFullPath(soundFontPath);
        var name = Path.GetFileName(full);
        var nextTo = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(midiPath)), name);
        if (File.Exists(nextTo)) return Same(nextTo, full) ? name : full;
        return Same(Path.Combine(soundFontsFolder, name), full) ? name : full;
    }

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
