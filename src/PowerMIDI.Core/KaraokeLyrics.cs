using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// Lyrics of a karaoke file, line by line, each syllable at its position (ticks): the Lyric events (FF 05), otherwise
// the Text events of the Soft Karaoke format (.kar: a file with "@" headers such as @KMIDI, @L, @T, left out; "/"
// starts a line, "\" a paragraph). A line also ends with an empty syllable, or a line feed / carriage return.
public sealed class KaraokeLyrics
{
    public sealed record Syllable(long Tick, string Text);

    public IReadOnlyList<IReadOnlyList<Syllable>> Lines { get; }

    KaraokeLyrics(List<List<Syllable>> lines) => Lines = lines;

    // Null: no lyrics in the file.
    public static KaraokeLyrics Read(MidiFile file)
    {
        var events = file.GetTimedEvents().ToList();
        var lyrics = events.Where(t => t.Event is LyricEvent).Select(t => (t.Time, Text: ((LyricEvent)t.Event).Text ?? "", Kar: false)).ToList();
        if (lyrics.Count == 0)
        {
            var texts = events.Where(t => t.Event is TextEvent).Select(t => (t.Time, Text: ((TextEvent)t.Event).Text ?? "")).ToList();
            if (texts.Any(t => t.Text.StartsWith("@K", StringComparison.OrdinalIgnoreCase) || t.Text.StartsWith("@T", StringComparison.Ordinal) ||
                                 t.Text.StartsWith("@L", StringComparison.Ordinal)))
                lyrics = texts.Where(t => !t.Text.StartsWith('@')).Select(t => (t.Time, t.Text, Kar: true)).ToList();
        }
        var lines = new List<List<Syllable>> { new() };
        void NewLine() { if (lines[^1].Count > 0) lines.Add(new()); }
        foreach (var (time, raw, kar) in lyrics)
        {
            var text = raw;
            if (kar && text.Length > 0 && text[0] is '/' or '\\') { NewLine(); text = text[1..]; }
            if (text.Trim('\r', '\n').Length == 0) { NewLine(); continue; }
            var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) NewLine();
                // '_' joins words in one syllable ("So_far"): shown as a space.
                if (parts[i].Length > 0) lines[^1].Add(new Syllable(time, parts[i].Replace('_', ' ')));
            }
        }
        lines.RemoveAll(l => l.Count == 0 || string.Concat(l.Select(s => s.Text)).Trim().Length == 0);
        return lines.Count > 0 ? new KaraokeLyrics(lines) : null;
    }

    // The line at a position: the last one started (before the first: the first one, nothing sung), cut in its part
    // already sung and the rest. Longer than width characters: on two lines, cut at the last space that fits (a line
    // feed in place of that space). Too long for two lines: the page of it being sung (see Page).
    public (string Sung, string Coming) At(long tick, int width)
    {
        var line = Page(Lines.LastOrDefault(l => l[0].Tick <= tick) ?? Lines[0], tick, width);
        string all = string.Concat(line.Select(s => s.Text)).Trim();
        int sung = Math.Min(string.Concat(line.Where(s => s.Tick <= tick).Select(s => s.Text)).TrimStart().Length, all.Length);
        if (all.Length > width)
        {
            int cut = all.LastIndexOf(' ', width);
            all = cut > 0 ? all[..cut] + "\n" + all[(cut + 1)..] : all[..width] + "\n" + all[width..];
            if (cut <= 0 && sung > width) sung++;   // the line feed added (no space to replace) is in the sung part
        }
        return (all[..sung], all[sung..]);
    }

    static string Text(IEnumerable<Syllable> syllables) => string.Concat(syllables.Select(s => s.Text));

    // The text fits on the two lines of the screen, cut as At cuts it.
    static bool Fits(string text, int width)
    {
        text = text.Trim();
        if (text.Length <= width) return true;
        int cut = text.LastIndexOf(' ', width);
        return cut > 0 ? text.Length - cut - 1 <= width : text.Length <= 2 * width;
    }

    // A line too long for the two lines of the screen (a file that marks no line breaks: the whole song on one line) is
    // shown by pages that fit, cut between words; preferably before a word starting with a capital letter (the start of
    // a phrase), when the page keeps at least one line of the screen. The page shown: the last one started.
    static IReadOnlyList<Syllable> Page(IReadOnlyList<Syllable> line, long tick, int width)
    {
        if (Fits(Text(line), width)) return line;
        // Words: the syllables up to one ending with a space (or followed by one starting with a space).
        var words = new List<List<Syllable>>();
        foreach (var s in line)
        {
            if (words.Count == 0 || words[^1][^1].Text.EndsWith(' ') || s.Text.StartsWith(' ')) words.Add(new());
            words[^1].Add(s);
        }
        var pages = new List<List<List<Syllable>>> { new() };
        foreach (var word in words)
        {
            var page = pages[^1];
            if (page.Count > 0 && !Fits(Text(page.Append(word).SelectMany(w => w)), width))
            {
                int cut = page.Count;
                for (int j = page.Count - 1; j > 0; j--)
                    if (char.IsUpper(Text(page[j]).TrimStart().FirstOrDefault()) && Text(page.Take(j).SelectMany(w => w)).Trim().Length >= width) { cut = j; break; }
                var rest = page.GetRange(cut, page.Count - cut);
                page.RemoveRange(cut, page.Count - cut);
                pages.Add(rest);
                page = rest;
            }
            page.Add(word);
        }
        var shown = pages.Select(p => p.SelectMany(w => w).ToList()).ToList();
        return shown.LastOrDefault(p => p[0].Tick <= tick) ?? shown[0];
    }
}
