using System.Text;

namespace PowerMIDI.Core;

// A preset of a SoundFont: its bank (0-127: melodic sounds; 128: drum kits), its program (0-127) and its name.
public sealed record SoundFontPreset(int Bank, int Program, string Name);

// What a .sf2 file says about itself, read without loading its samples:
// - from its INFO list: name (INAM), author (IENG), copyright (ICOP), comment (ICMT), date (ICRD), tool (ISFT) and
//   version (ifil);
// - its presets, from the phdr records of its pdta list (38 bytes each: name on 20 bytes, program, bank…; the last
//   one closing the list).
public sealed record SoundFontInfo(string Name, string Author, string Copyright, string Comment, string Date, string Tool,
                                   string Version, IReadOnlyList<SoundFontPreset> PresetList)
{
    public int Presets => PresetList.Count;

    public static SoundFontInfo Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12 || Id(reader) != "RIFF") throw new InvalidDataException("Not a SoundFont file.");
        reader.ReadUInt32();
        if (Id(reader) != "sfbk") throw new InvalidDataException("Not a SoundFont file.");

        var texts = new Dictionary<string, string>();
        string version = null;
        var presets = new List<SoundFontPreset>();
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Id(reader);
            long size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (id == "LIST" && size >= 4)
            {
                var type = Id(reader);
                long end = Math.Min(stream.Length, stream.Position - 4 + size);
                while (stream.Position + 8 <= end)
                {
                    var sub = Id(reader);
                    long length = reader.ReadUInt32();
                    long after = stream.Position + length + (length & 1);
                    if (type == "INFO" && sub == "ifil" && length >= 4)
                        version = $"{reader.ReadUInt16()}.{reader.ReadUInt16():D2}";
                    else if (type == "INFO" && length <= 65536)
                        texts[sub] = Text(reader.ReadBytes((int)length));
                    else if (type == "pdta" && sub == "phdr")
                        for (long n = length / 38 - 1; n > 0; n--)
                        {
                            var name = Text(reader.ReadBytes(20));
                            int program = reader.ReadUInt16(), bank = reader.ReadUInt16();
                            reader.ReadBytes(14);   // bag index, library, genre, morphology
                            presets.Add(new SoundFontPreset(bank, program, name));
                        }
                    stream.Position = after;
                }
            }
            stream.Position = next;
        }
        string Get(string key) => texts.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        return new SoundFontInfo(Get("INAM"), Get("IENG"), Get("ICOP"), Get("ICMT"), Get("ICRD"), Get("ISFT"), version, presets);
    }

    static string Id(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));

    // Texts are ASCII by the specification; UTF-8 when valid, otherwise Latin-1 (older editors). Ends at the first zero.
    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    static string Text(byte[] bytes)
    {
        int zero = Array.IndexOf(bytes, (byte)0);
        if (zero >= 0) bytes = bytes[..zero];
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        return text.Replace("\r\n", "\n").Trim();
    }
}
