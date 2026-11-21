using System.Text;

namespace PowerMIDI.Core;

// A preset of a SoundFont: its bank (0-127: melodic sounds; 128: drum kits), its program (0-127) and its name. IsKit:
// its keys are mostly separate sounds, as in a drum kit; false for one sound spread over the keys, as in an
// instrument (see SoundFontInfo.KitsOf). The bank 128 of a SoundFont made by some converters holds instruments: those
// of the channel 10 of a tracker song (CONGON~1.sf2).
public sealed record SoundFontPreset(int Bank, int Program, string Name, bool IsKit = true);

// What a .sf2 file says about itself, read without loading its samples:
// - from its INFO list: name (INAM), author (IENG), copyright (ICOP), comment (ICMT), date (ICRD), tool (ISFT) and
//   version (ifil);
// - its presets, from the phdr records of its pdta list (38 bytes each: name on 20 bytes, program, bank, index of its
//   first zone…; the last one closing the list), and how their zones lay their sounds on the keys (KitsOf).
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
        var pdta = new Dictionary<string, byte[]>();   // the lists of the presets and instruments
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
                    else if (type == "pdta" && sub is "phdr" or "pbag" or "pgen" or "inst" or "ibag" or "igen" && length <= int.MaxValue)
                        pdta[sub] = reader.ReadBytes((int)length);
                    stream.Position = after;
                }
            }
            stream.Position = next;
        }
        var presets = new List<SoundFontPreset>();
        if (pdta.TryGetValue("phdr", out var phdr))
        {
            var kits = KitsOf(pdta);
            for (int n = 0; n < phdr.Length / 38 - 1; n++)
                presets.Add(new SoundFontPreset(BitConverter.ToUInt16(phdr, n * 38 + 22), BitConverter.ToUInt16(phdr, n * 38 + 20),
                                                Text(phdr[(n * 38)..(n * 38 + 20)]), kits[n]));
        }
        string Get(string key) => texts.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        return new SoundFontInfo(Get("INAM"), Get("IENG"), Get("ICOP"), Get("ICMT"), Get("ICRD"), Get("ISFT"), version, presets);
    }

    // Is each preset a drum kit? Its zones resolved through its instruments: the keys it plays, and those a zone of one
    // key plays (a sound of its own). A kit: at least half of its keys so; an instrument: fewer (its samples spread over
    // ranges of keys). Measured: the kits of GeneralUser GS, SEGAudio and Alex_GM, 73-100 % (the Orchestra kits lowest:
    // timpani over ranges); their instruments, under 20 %; the bank 128 of CONGON~1.sf2 (copies of instruments), 0 %.
    // A preset whose zones cannot be read: a kit (as its bank says).
    // Lists (pdta): phdr (38 bytes: …, index of its first zone at 24), pbag / ibag (4 bytes: index of the first generator
    // of the zone), pgen / igen (4 bytes: operator, amount; keyRange 43: low and high key; instrument 41, sampleID 53:
    // index), inst (22 bytes: name, index of its first zone at 20); the last record of each list closes it.
    static bool[] KitsOf(Dictionary<string, byte[]> pdta)
    {
        var phdr = pdta["phdr"];
        var kits = Enumerable.Repeat(true, Math.Max(phdr.Length / 38 - 1, 0)).ToArray();
        if (!pdta.TryGetValue("pbag", out var pbag) || !pdta.TryGetValue("pgen", out var pgen) || !pdta.TryGetValue("inst", out var inst) ||
            !pdta.TryGetValue("ibag", out var ibag) || !pdta.TryGetValue("igen", out var igen))
            return kits;
        static int U16(byte[] b, int o) => o + 2 <= b.Length ? BitConverter.ToUInt16(b, o) : -1;
        // A zone: its key range and the index its last generator gives (instrument or sample; -1: none, a global zone).
        static (int Low, int High, int Target) Zone(byte[] bags, byte[] gens, int z, int target)
        {
            int first = U16(bags, z * 4), end = U16(bags, z * 4 + 4);
            int low = 0, high = 127, index = -1;
            for (int g = first; g >= 0 && g < end && g * 4 + 4 <= gens.Length; g++)
            {
                int op = U16(gens, g * 4);
                if (op == 43) (low, high) = (gens[g * 4 + 2], gens[g * 4 + 3]);
                else if (op == target) index = U16(gens, g * 4 + 2);
            }
            return (low, Math.Min(high, 127), index);
        }
        for (int p = 0; p < kits.Length; p++)
        {
            var played = new bool[128];
            var alone = new bool[128];
            int pFirst = U16(phdr, p * 38 + 24), pEnd = U16(phdr, p * 38 + 62);
            for (int pz = pFirst; pz >= 0 && pz < pEnd && pz * 4 + 8 <= pbag.Length; pz++)
            {
                var (low, high, instrument) = Zone(pbag, pgen, pz, 41);
                if (instrument < 0 || instrument * 22 + 44 > inst.Length) continue;
                int iFirst = U16(inst, instrument * 22 + 20), iEnd = U16(inst, instrument * 22 + 42);
                for (int iz = iFirst; iz >= 0 && iz < iEnd && iz * 4 + 8 <= ibag.Length; iz++)
                {
                    var (iLow, iHigh, sample) = Zone(ibag, igen, iz, 53);
                    if (sample < 0) continue;
                    int from = Math.Max(low, iLow), to = Math.Min(high, iHigh);
                    for (int k = from; k <= to; k++)
                    {
                        played[k] = true;
                        if (from == to) alone[k] = true;
                    }
                }
            }
            int keys = played.Count(x => x);
            if (keys > 0) kits[p] = alone.Count(x => x) * 2 >= keys;
        }
        return kits;
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
