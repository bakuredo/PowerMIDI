namespace PowerMIDI.Core;

// MT-32 sounds redefined by the SysEx of a file (F0 41 dev 16 12 aa aa aa data … sum F7), read to name what a
// channel really plays. A program number picks a patch; a patch points to a timbre (the sound) of one of four
// groups: A and B (the 128 preset timbres), Memory (64 timbres loaded by the file) and Rhythm.
// - Patch Memory (05 00 00, 8 bytes per patch): the table program -> timbre group and number.
// - Timbre Memory (08 00 00, 256 bytes per timbre): the 64 Memory timbres; the first 10 bytes are the name.
// - Patch Temp (03 00 00, 16 bytes per part): timbre group and number of a part, set without a Program Change.
// - Timbre Temp (04 00 00, 246 bytes per part): a timbre written straight into a part; the name comes first.
// Read is called for each SysEx of the file on the playback thread; nothing is changed in the messages.
public static class Mt32Patches
{
    public const int GroupA = 0, GroupB = 1, GroupMemory = 2, GroupRhythm = 3;
    const int PatchMemory = 0x05 << 14, TimbreMemory = 0x08 << 14, PatchTemp = 0x03 << 14, TimbreTemp = 0x04 << 14;
    const int PatchSize = 8, TimbreSize = 256, PatchTempSize = 16, TimbreTempSize = 246, NameSize = 10, Parts = 8;

    static readonly int[] patchGroup = new int[128], patchNumber = new int[128];
    static readonly char[][] memoryNames = new char[64][];
    // Timbre of a part set by Patch Temp (group, number) or Timbre Temp (name) since its last Program Change.
    static readonly int[] partGroup = new int[Parts], partNumber = new int[Parts];
    static readonly char[][] partNames = new char[Parts][];
    static readonly int[] partSource = new int[Parts];   // 0 = the program, 1 = Patch Temp, 2 = Timbre Temp

    // Changes at each value read: the display is refreshed when it differs.
    public static int Version { get; private set; }

    static Mt32Patches() { ResetFile(); }

    // State after a reset: program n plays the preset timbre n (A1-A64, then B1-B64); no Memory timbre.
    public static void ResetFile()
    {
        for (int p = 0; p < 128; p++) { patchGroup[p] = p / 64; patchNumber[p] = p % 64; }
        for (int t = 0; t < 64; t++) memoryNames[t] = null;
        for (int part = 0; part < Parts; part++) { partSource[part] = 0; partNames[part] = null; }
        Version++;
    }

    // A Program Change on the channel of a part: its timbre is the one of the patch again.
    public static void ProgramChanged(int channel)
    {
        int part = PartOf(channel);
        if (part < 0 || partSource[part] == 0) return;
        partSource[part] = 0;
        Version++;
    }

    // Part (0-7) played by a channel, or -1 (the rhythm part and the other channels).
    static int PartOf(int channel)
    {
        for (int part = 0; part < Parts; part++) if (Mt32Effects.PartChannel[part] == channel) return part;
        return -1;
    }

    internal static void Read(byte[] d) => Read(d, true);

    // Does a SysEx redefine sounds (patch table, Memory timbres, timbre of a part)? Nothing is changed.
    public static bool IsCustomPatch(byte[] d) => Read(d, false);

    // apply false: only tells whether the message changes a sound (at its first such value).
    static bool Read(byte[] d, bool apply)
    {
        if (d.Length < 10 || d[0] != 0x41 || d[2] != 0x16 || d[3] != 0x12) return false;
        int address = (d[4] << 14) | (d[5] << 7) | d[6];
        bool changed = false;
        for (int pos = 7; pos < d.Length - 2; pos++, address++)   // … data, checksum, F7
        {
            byte v = d[pos];
            int area = address & ~0x3FFF, offset = address & 0x3FFF;
            if (area == PatchMemory && offset < 128 * PatchSize)
            {
                int patch = offset / PatchSize, field = offset % PatchSize;
                if (field > 1) continue;
                if (!apply) return true;
                if (field == 0) patchGroup[patch] = v & 3; else patchNumber[patch] = v & 63;
            }
            else if (area == TimbreMemory && offset < 64 * TimbreSize)
            {
                int timbre = offset / TimbreSize, field = offset % TimbreSize;
                if (field >= NameSize) continue;
                if (!apply) return true;
                (memoryNames[timbre] ??= new string(' ', NameSize).ToCharArray())[field] = (char)v;
            }
            else if (area == PatchTemp && offset < Parts * PatchTempSize)
            {
                int part = offset / PatchTempSize, field = offset % PatchTempSize;
                if (field > 1) continue;
                if (!apply) return true;
                if (field == 0) partGroup[part] = v & 3; else partNumber[part] = v & 63;
                partSource[part] = 1;
            }
            else if (area == TimbreTemp && offset < Parts * TimbreTempSize)
            {
                int part = offset / TimbreTempSize, field = offset % TimbreTempSize;
                if (field >= NameSize) continue;
                if (!apply) return true;
                (partNames[part] ??= new string(' ', NameSize).ToCharArray())[field] = (char)v;
                partSource[part] = 2;
            }
            else continue;
            changed = true;
        }
        if (changed) Version++;
        return changed;
    }

    // Name of a memory timbre (0-63) loaded by the file, or null.
    public static string MemoryTimbreName(int number)
    {
        var name = memoryNames[number] != null ? new string(memoryNames[number]).Trim() : "";
        return name != "" ? name : null;
    }

    // Name of a timbre: presetNames = the 128 preset timbres (A1-A64, B1-B64), by number.
    static string TimbreName(int group, int number, IReadOnlyDictionary<int, string> presetNames)
    {
        switch (group)
        {
            case GroupA:
            case GroupB:
                return presetNames.TryGetValue(group * 64 + number, out var preset) ? preset : $"Timbre {"AB"[group]}{number + 1}";
            case GroupMemory:
                var name = memoryNames[number] != null ? new string(memoryNames[number]).Trim() : "";
                return name != "" ? name : $"Memory timbre {number + 1}";
            default:
                return $"Rhythm timbre {number + 1}";
        }
    }

    // Timbre a program plays, by the patch table of the file.
    public static string ProgramName(int program, IReadOnlyDictionary<int, string> presetNames) =>
        TimbreName(patchGroup[program], patchNumber[program], presetNames);

    // Is program n no longer the preset timbre n?
    public static bool IsCustom(int program) =>
        patchGroup[program] != program / 64 || patchNumber[program] != program % 64;

    // Timbre written into the part of a channel by a SysEx since its last Program Change (null: none, the part
    // plays its program).
    public static string PartName(int channel, IReadOnlyDictionary<int, string> presetNames)
    {
        int part = PartOf(channel);
        if (part < 0 || partSource[part] == 0) return null;
        if (partSource[part] == 1) return TimbreName(partGroup[part], partNumber[part], presetNames);
        var name = new string(partNames[part]).Trim();
        return name != "" ? name : "Custom timbre";
    }
}
