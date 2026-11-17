using System.Text;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// What a .sf2 file says about itself (SoundFontInfo.Read): its INFO texts, its version and its number of presets, read
// from a small file written here (RIFF sfbk: an INFO list, an sdta list, a pdta list with its phdr records).
public class SoundFontInfoTests
{
    static byte[] Chunk(string id, byte[] data)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(id));
        bytes.AddRange(BitConverter.GetBytes((uint)data.Length));
        bytes.AddRange(data);
        if (data.Length % 2 == 1) bytes.Add(0);   // chunks of odd size are padded
        return bytes.ToArray();
    }

    static byte[] List(string type, params byte[][] chunks) =>
        Chunk("LIST", Encoding.ASCII.GetBytes(type).Concat(chunks.SelectMany(c => c)).ToArray());

    static byte[] Zstr(string text, Encoding encoding = null) => (encoding ?? Encoding.ASCII).GetBytes(text).Append((byte)0).ToArray();

    // presets: number of presets; the phdr list has one more record (the terminal one).
    static string Write(int presets, byte[] author)
    {
        var info = List("INFO",
            Chunk("ifil", new byte[] { 2, 0, 1, 0 }),
            Chunk("INAM", Zstr("Test Bank")),
            Chunk("IENG", author),
            Chunk("ICOP", Zstr("Free")));
        var sdta = List("sdta", Chunk("smpl", new byte[100]));
        var pdta = List("pdta", Chunk("phdr", new byte[38 * (presets + 1)]), Chunk("pbag", new byte[4]));
        var body = Encoding.ASCII.GetBytes("sfbk").Concat(info).Concat(sdta).Concat(pdta).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"powermidi-test-{Guid.NewGuid():N}.sf2");
        File.WriteAllBytes(path, Chunk("RIFF", body));
        return path;
    }

    [Fact]
    public void ReadsTextsVersionAndPresets()
    {
        var path = Write(3, Zstr("Someone"));
        try
        {
            var info = SoundFontInfo.Read(path);
            Assert.Equal("Test Bank", info.Name);
            Assert.Equal("Someone", info.Author);
            Assert.Equal("Free", info.Copyright);
            Assert.Null(info.Comment);
            Assert.Equal("2.01", info.Version);
            Assert.Equal(3, info.Presets);
        }
        finally { File.Delete(path); }
    }

    // A text in UTF-8 is read as such; a byte that is not UTF-8 is read as Latin-1 (older editors).
    [Theory]
    [InlineData("utf-8")]
    [InlineData("latin1")]
    public void ReadsAccentedText(string encoding)
    {
        var path = Write(1, Zstr("Hélène", Encoding.GetEncoding(encoding)));
        try { Assert.Equal("Hélène", SoundFontInfo.Read(path).Author); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsOtherFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"powermidi-test-{Guid.NewGuid():N}.sf2");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MThd\0\0\0\u0006\0\0\0\u0001\0`"));
        try { Assert.Throws<InvalidDataException>(() => SoundFontInfo.Read(path)); }
        finally { File.Delete(path); }
    }
}
