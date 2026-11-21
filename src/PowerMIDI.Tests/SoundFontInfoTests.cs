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

    // phdr records: name on 20 bytes, program, bank, then 14 bytes (bag index, library, genre, morphology); the
    // terminal record "EOP" closes the list.
    static byte[] Phdr(params (string Name, int Program, int Bank)[] presets)
    {
        var bytes = new List<byte>();
        foreach (var (name, program, bank) in presets.Append(("EOP", 0, 0)))
        {
            var text = new byte[20];
            Encoding.ASCII.GetBytes(name).CopyTo(text, 0);
            bytes.AddRange(text);
            bytes.AddRange(BitConverter.GetBytes((ushort)program));
            bytes.AddRange(BitConverter.GetBytes((ushort)bank));
            bytes.AddRange(new byte[14]);
        }
        return bytes.ToArray();
    }

    // presets: number of presets (named "Preset n", program n - 1, bank 0).
    static string Write(int presets, byte[] author)
    {
        var info = List("INFO",
            Chunk("ifil", new byte[] { 2, 0, 1, 0 }),
            Chunk("INAM", Zstr("Test Bank")),
            Chunk("IENG", author),
            Chunk("ICOP", Zstr("Free")));
        var sdta = List("sdta", Chunk("smpl", new byte[100]));
        var records = Enumerable.Range(0, presets).Select(n => ($"Preset {n + 1}", n, 0)).ToArray();
        var pdta = List("pdta", Chunk("phdr", Phdr(records)), Chunk("pbag", new byte[4]));
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
            Assert.Equal(new SoundFontPreset(0, 2, "Preset 3"), info.PresetList[2]);
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

    static byte[] U16(params int[] values) => values.SelectMany(v => BitConverter.GetBytes((ushort)v)).ToArray();
    static byte[] Name20(string name) { var b = new byte[20]; Encoding.ASCII.GetBytes(name).CopyTo(b, 0); return b; }
    static byte[] KeyRange(int low, int high) => U16(43).Concat(new[] { (byte)low, (byte)high }).ToArray();

    // Bank 128: a kit (4 keys, a zone of one key each) and an instrument (one zone over all the keys, as the copies of a
    // converter). Presets → instruments → zones, each list closed by a last record.
    [Fact]
    public void TellsKitsFromInstrumentsOfDrumBank()
    {
        byte[] Preset(string name, int program, int bag) => Name20(name).Concat(U16(program, 128, bag)).Concat(new byte[12]).ToArray();
        var phdr = Preset("Kit", 0, 0).Concat(Preset("Copy", 1, 1)).Concat(Preset("EOP", 0, 2)).ToArray();
        var pbag = U16(0, 0, 1, 0, 2, 0);
        var pgen = U16(41, 0, 41, 1, 0, 0);
        var inst = Name20("Kit").Concat(U16(0)).Concat(Name20("Copy")).Concat(U16(4)).Concat(Name20("EOI")).Concat(U16(5)).ToArray();
        var ibag = U16(0, 0, 2, 0, 4, 0, 6, 0, 8, 0, 10, 0);
        var sample = U16(53, 0);
        var igen = new[] { 36, 38, 42, 46 }.SelectMany(k => KeyRange(k, k).Concat(sample)).Concat(KeyRange(0, 127)).Concat(sample).Concat(U16(0, 0)).ToArray();
        var pdta = List("pdta", Chunk("phdr", phdr), Chunk("pbag", pbag), Chunk("pmod", new byte[10]), Chunk("pgen", pgen),
                        Chunk("inst", inst), Chunk("ibag", ibag), Chunk("imod", new byte[10]), Chunk("igen", igen));
        var body = Encoding.ASCII.GetBytes("sfbk").Concat(List("INFO", Chunk("ifil", new byte[] { 2, 0, 1, 0 })))
                                                  .Concat(List("sdta", Chunk("smpl", new byte[100]))).Concat(pdta).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"powermidi-test-{Guid.NewGuid():N}.sf2");
        File.WriteAllBytes(path, Chunk("RIFF", body));
        try
        {
            var presets = SoundFontInfo.Read(path).PresetList;
            Assert.Equal(new SoundFontPreset(128, 0, "Kit", true), presets[0]);
            Assert.Equal(new SoundFontPreset(128, 1, "Copy", false), presets[1]);

            // Named and played as such on a drum channel: the instrument as an instrument; a missing program: the first
            // kit, a kit.
            var defs = new InstrumentDefinitions();
            defs.SetSoundFont(presets);
            var copy = defs.Resolve(InstrumentDefinitions.Sf2Standard, 9, 1, 0, 0, 1);
            Assert.Equal(("Copy", false), (copy.Name, copy.Drum));
            Assert.True(defs.Resolve(InstrumentDefinitions.Sf2Standard, 9, 0, 0, 0, 1).Drum);
            Assert.Equal(new[] { true, false, true }, new[] { 0, 1, 5 }.Select(defs.IsSoundFontKit));
        }
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
