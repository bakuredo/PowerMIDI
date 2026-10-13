using System.Text;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Texts of a MIDI file (title, copyright, comments): kept byte for byte, decoded for display.
public class TextEncodingTests
{
    // Text as ReadFile gives it: one character per byte of the file.
    static string Raw(Encoding encoding, string text) => Encoding.Latin1.GetString(encoding.GetBytes(text));

    static TextEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void Text_is_decoded_as_utf8_shift_jis_or_windows_1252()
    {
        Assert.Equal("Plain title", MidiTools.DecodeText("Plain title"));
        Assert.Null(MidiTools.DecodeText(null));
        Assert.Equal("", MidiTools.DecodeText(""));

        const string japanese = "FFVI-2-18 ストラゴスのテーマ -Stragus- for XG by Shake";
        Assert.Equal(japanese, MidiTools.DecodeText(Raw(Encoding.GetEncoding(932), japanese)));
        Assert.Equal(japanese, MidiTools.DecodeText(Raw(Encoding.UTF8, japanese)));

        // Western accents (Windows-1252): bytes E0-FF only, never taken for Shift-JIS.
        foreach (var french in new[] { "Été à Noël", "Café", "Thème d'entrée", "Füße © 1999" })
        {
            Assert.Equal(french, MidiTools.DecodeText(Raw(Encoding.GetEncoding(1252), french)));
            Assert.Equal(french, MidiTools.DecodeText(Raw(Encoding.UTF8, french)));
        }
    }

    [Fact]
    public void A_saved_file_keeps_the_bytes_of_its_texts()
    {
        var name = Raw(Encoding.GetEncoding(932), "ストラゴスのテーマ");
        var source = Path.Combine(Path.GetTempPath(), $"powermidi-text-{Guid.NewGuid():N}.mid");
        var copy = source + ".copy.mid";
        try
        {
            new MidiFile(new TrackChunk(new SequenceTrackNameEvent(name), new CopyrightNoticeEvent(Raw(Encoding.GetEncoding(1252), "© Été"))))
                .Write(source, true, settings: new WritingSettings { TextEncoding = Encoding.Latin1 });
            var file = MidiTools.ReadFile(source);
            var info = MidiTools.GetInfo(file);
            Assert.Equal("ストラゴスのテーマ", info.Title);
            Assert.Equal("© Été", info.Copyright);

            MidiTools.WriteFile(file, copy);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy));
        }
        finally { File.Delete(source); File.Delete(copy); }
    }
}
