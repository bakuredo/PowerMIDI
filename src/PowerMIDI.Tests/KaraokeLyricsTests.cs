using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Lyrics of karaoke files (KaraokeLyrics): line by line, the part sung at a position, scrolled when too long.
public class KaraokeLyricsTests
{
    static MidiFile File(params MidiEvent[] events) => new(new TrackChunk(events)) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };

    [Fact]
    public void Lyric_events_make_lines_with_the_part_sung()
    {
        // "You and me" (empty syllable: end of line), then "e-ver-last" ended by a line feed, then "love".
        var lyrics = KaraokeLyrics.Read(File(
            new LyricEvent("You ") { DeltaTime = 100 }, new LyricEvent("and ") { DeltaTime = 100 }, new LyricEvent("me") { DeltaTime = 100 },
            new LyricEvent("") { DeltaTime = 0 },
            new LyricEvent("e") { DeltaTime = 100 }, new LyricEvent("ver") { DeltaTime = 100 }, new LyricEvent("last\n") { DeltaTime = 100 },
            new LyricEvent("love") { DeltaTime = 100 }));
        Assert.Equal(3, lyrics.Lines.Count);
        Assert.Equal(("", "You and me"), lyrics.At(0, 32));         // before the first syllable: the first line
        Assert.Equal(("You and ", "me"), lyrics.At(200, 32));
        Assert.Equal(("e", "verlast"), lyrics.At(400, 32));          // the next line once started
        Assert.Equal(("love", ""), lyrics.At(10000, 32));            // the last one stays
    }

    [Fact]
    public void Soft_karaoke_text_events()
    {
        // .kar: "@" headers left out, "/" and "\" start a line; a file without these headers: its texts are not lyrics.
        var lyrics = KaraokeLyrics.Read(File(
            new TextEvent("@KMIDI KARAOKE FILE"), new TextEvent("@TEverlasting"),
            new TextEvent("\\You ") { DeltaTime = 100 }, new TextEvent("and me") { DeltaTime = 100 },
            new TextEvent("/This ") { DeltaTime = 100 }, new TextEvent("could be") { DeltaTime = 100 }));
        Assert.Equal(new[] { "You and me", "This could be" }, lyrics.Lines.Select(l => string.Concat(l.Select(s => s.Text))).ToArray());
        Assert.Null(KaraokeLyrics.Read(File(new TextEvent("Composed by someone"), new TextEvent("Produced by"))));
    }

    [Fact]
    public void Long_line_on_two_lines()
    {
        // "I kept the vibes of the good times" (34 characters): cut at the last space within 32.
        var syllables = new[] { "I ", "kept ", "the ", "vi", "bes ", "of ", "the ", "good ", "times" };
        var lyrics = KaraokeLyrics.Read(File(syllables.Select(s => new LyricEvent(s) { DeltaTime = 100 }).ToArray()));
        Assert.Equal(("", "I kept the vibes of the good\ntimes"), lyrics.At(0, 32));
        Assert.Equal(("I kept the vi", "bes of the good\ntimes"), lyrics.At(400, 32));
        Assert.Equal(("I kept the vibes of the good\n", "times"), lyrics.At(800, 32));   // the line feed: with the sung part
        Assert.Equal(("I kept the vibes of the good\ntimes", ""), lyrics.At(900, 32));
        Assert.Equal(("You and", ""), KaraokeLyrics.Read(File(new LyricEvent("You "), new LyricEvent("and"))).At(10, 32));   // short: one line
    }
}
