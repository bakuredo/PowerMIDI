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

    // A file without line breaks (Aria di Mezzo Carattere: the whole song on one line): shown by pages that fit on the two
    // lines of the screen, the one being sung; preferably cut before a capitalized word; '_' read as a space.
    [Fact]
    public void Song_without_line_breaks_shown_by_pages()
    {
        var words = "Oh my hero So_far away now Will I ever see your smile Love goes away Like night into day It's just a fading dream "
                  + "I'm the Darkness You are the stars Our_love is brighter than the sun";
        var lyrics = KaraokeLyrics.Read(File(words.Split(' ').Select(w => (MidiEvent)new LyricEvent(w + " ") { DeltaTime = 100 }).ToArray()));
        Assert.Single(lyrics.Lines);
        var shown = new List<string>();
        for (long tick = 100; tick <= 100 * words.Split(' ').Length; tick += 100)
        {
            var (sung, coming) = lyrics.At(tick, 32);
            var text = sung + coming;
            Assert.True(text.Split('\n').All(row => row.Length <= 32), text);          // fits the screen
            Assert.EndsWith(words.Split(' ')[tick / 100 - 1].Replace('_', ' '), sung.Replace('\n', ' ').TrimEnd());   // the syllable sung is on it
            if (shown.Count == 0 || shown[^1] != text) shown.Add(text);
        }
        Assert.Equal("Oh my hero So far away now Will\nI ever see your smile", shown[0]);
        Assert.StartsWith("Love goes away", shown[1]);                                  // a new page at a capital letter
        Assert.Contains(shown, s => s.Contains("Our love"));
    }

    // Japanese lyrics (Shift-JIS bytes, as the file has them): decoded; a Japanese character takes two columns of the
    // screen, the lines cut between them (no spaces).
    [Fact]
    public void Japanese_lyrics_decoded_and_cut_by_columns()
    {
        var sjis = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(932);
        string Raw(string text) => System.Text.Encoding.Latin1.GetString(sjis.GetBytes(text));   // as ReadFile gives them
        var syllables = new[] { "ＯＮＥ～輝く季節へ～/Tactics", "作曲　YET11", "データ作成　hiro", "Thank you for listening" };
        var lyrics = KaraokeLyrics.Read(File(syllables.Select(s => (MidiEvent)new LyricEvent(Raw(s)) { DeltaTime = 100 }).ToArray()));
        Assert.Equal(string.Concat(syllables), string.Concat(lyrics.Lines[0].Select(s => s.Text)));
        static int Columns(string row) => row.Sum(c => c >= '⺀' ? 2 : 1);
        for (long tick = 100; tick <= 400; tick += 100)
        {
            var (sung, coming) = lyrics.At(tick, 32);
            Assert.True((sung + coming).Split('\n').All(row => Columns(row) <= 32), sung + coming);   // fits the screen
        }
        var (endSung, endComing) = lyrics.At(400, 32);
        Assert.EndsWith("listening", (endSung + endComing).Replace("\n", " "));
    }
}
