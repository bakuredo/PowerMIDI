using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// GS and XG effects (Effects tab in GS and XG modes): the values sent by the file and those fixed in the interface,
// which replace the file's. Read is called for each SysEx of the file on the DryWetMIDI playback thread.
// GS: SysEx DT1 F0 41 dev 42 12 40 01 aa vv … sum F7 (1 byte per parameter, Roland checksum).
// XG: SysEx F0 43 1n 4C 02 01 aa vv … F7 (no checksum; types are 2 bytes MSB/LSB).
// EFX (SC-88Pro / SC-8850): DT1 40 03 xx (2-byte type) and 40 4x 22 (EFX on part x).
// XG insertion effects 1 to 4 (MU100 and later): SysEx F0 43 1n 4C 03 0x aa vv … F7, x = 0..3.
// XG Multi EQ (MU100 and later): SysEx F0 43 1n 4C 02 40 aa vv F7.
// A type sets the other parameters of its block: they are then -1 ("auto", the type's default) until set.
public static class GsXgEffects
{
    // Parameters of the Effects tab (index e). Blocks: 0 GS Reverb, 1 GS Chorus, 2 GS Delay,
    // 3 XG Reverb, 4 XG Chorus, 5 XG Variation, 6 GS EFX. first parameter of each block is its type.
    // 42: EFX type; 43-62: EFX parameters 1 to 20; 63-65: sends to reverb / chorus / delay;
    // 66-81: EFX on channels 1 to 16; 82-97: XG Variation parameters 1 to 16 (1-10 are 2 bytes:
    // 02 01 42, 44 … 54; 11-16: 02 01 70..75), placed at end of list but in block 5;
    // 98-112: XG Reverb parameters 2 to 16 (02 01 03..0B, 10..15; parameter 1 is index 27);
    // 113-125: XG Chorus parameters 4 to 16 (02 01 25..2B, 30..35; parameters 1-3 are 32-34).
    // Block 7, GS EQ (SC-88 and later; no type): 126-129 low frequency (0 = 200 Hz, 1 = 400 Hz), low gain
    // (52..76 = -12..+12 dB), high frequency (0 = 3 kHz, 1 = 6 kHz), high gain: DT1 40 02 00..03;
    // 130-145: EQ on channels 1 to 16 (Part EQ, DT1 40 4x 20; 1 = on, the state after a reset).
    // Block 12, XG Multi EQ, 18 parameters from index 218 (XgEqFirst): type (02 40 00: flat, jazz, pops, rock,
    // concert), then the five bands: gain (52..76 = -12..+12 dB), frequency (index in the XG frequency table),
    // Q (1..120 = 0.1..12.0) and, for the first and the last band, shape (0 = shelving, 1 = peaking).
    static readonly int[] XgEqAddresses = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x11, 0x12, 0x13, 0x14 };
    // Values of the 18 parameters: type, band 1 (gain, frequency, Q, shape), bands 2 to 4 (gain, frequency, Q), band 5.
    static int[] XgEq(int type, int gain, int q, int shape, params int[] frequencies) => new[] {
        type, gain, frequencies[0], q, shape, gain, frequencies[1], q, gain, frequencies[2], q, gain, frequencies[3], q,
        gain, frequencies[4], q, shape };
    // Blocks 8-11, XG insertion effects 1 to 4: 18 parameters each from index 146 (InsFirst): type (03 0x 00,
    // 2 bytes), part (0C: 0-63, 64-65 = AD inputs, 127 = off), parameters 1 to 10 and 11 to 16 (20..25).
    // Parameters 1 to 10 have two addresses: 02..0B (1 byte) and 30, 32 … 42 (MSB + LSB); the module only
    // listens to the second ones when the type needs 2-byte values (InsertionNeedsMsb), otherwise to the first.
    public static readonly int[] Addresses = Cat(new[] {
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x37,                      // 0-6   GS Reverb
        0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40,          // 7-15  GS Chorus
        0x50, 0x51, 0x52, 0x53, 0x54, 0x58, 0x59, 0x5A,                // 16-23 GS Delay
        0x00, 0x0C, 0x0D, 0x02,                                        // 24-27 XG Reverb: type, return, pan, time
        0x20, 0x2C, 0x2D, 0x2E, 0x22, 0x23, 0x24,                      // 28-34 XG Chorus: type, return, pan, to reverb, rate, depth, feedback
        0x40, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B },                    // 35-41 XG Variation: type, return, pan, to reverb, to chorus, connection, part
        new[] { 0x00 }, Range(0x03, 20), new[] { 0x17, 0x18, 0x19 }, Repeat(0x22, 16),    // 42-81 EFX
        Range2(0x42, 10), Range(0x70, 6),                                                  // 82-97 XG Variation
        Range(0x03, 9), Range(0x10, 6), Range(0x25, 7), Range(0x30, 6),                    // 98-125 XG Reverb / Chorus
        new[] { 0x00, 0x01, 0x02, 0x03 }, Repeat(0x20, 16),                                // 126-145 GS EQ
        InsEach(_ => Cat(new[] { 0x00, 0x0C }, Range(0x02, 10), Range(0x20, 6))),            // 146-217 XG Insertion 1-4
        XgEqAddresses);                                                                    // 218-235 XG Multi EQ
    // Middle address byte: GS 40 01 / XG 02 01 for blocks 0-5; EFX 40 03; EQ 40 02; EFX / EQ per part 40 4x
    // (block x: 1..9 = parts 1..9, 0 = part 10, A..F = parts 11..16; same numbering as 40 1x).
    // XG: 01 (02 01 xx), 40 for the Multi EQ (02 40 xx); insertion effects: the number of the effect (0..3), after 03.
    public static readonly int[] AddressHi = Cat(Repeat(0x01, 42), Repeat(0x03, 24),
        new[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x40, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F },
        Repeat(0x01, 16), Repeat(0x01, 28),
        Repeat(0x02, 4), new[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x40, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F },
        InsEach(n => Repeat(n, InsSize)), Repeat(0x40, XgEqSize));
    public static readonly int[] Block = Cat(new[] {
        0, 0, 0, 0, 0, 0, 0,  1, 1, 1, 1, 1, 1, 1, 1, 1,  2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3,  4, 4, 4, 4, 4, 4, 4,  5, 5, 5, 5, 5, 5, 5 }, Repeat(6, 40), Repeat(5, 16), Repeat(3, 15), Repeat(4, 13), Repeat(7, 20),
        InsEach(n => Repeat(InsBlockFirst + n, InsSize)), Repeat(XgEqBlock, XgEqSize));
    public static readonly int[] Min = Cat(new[] {
        0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 1, 1, 1, 0, 0, 0,
        0, 0, 1, 0,  0, 0, 1, 0, 0, 0, 1,  0, 0, 1, 0, 0, 0, 0 }, Repeat(0, 40), Repeat(0, 16), Repeat(0, 28),
        new[] { 0, 52, 0, 52 }, Repeat(0, 16), Repeat(0, InsCount * InsSize), XgEq(0, 52, 1, 0, 4, 14, 14, 14, 28));
    public static readonly int[] Max = Cat(new[] {
        7, 7, 7, 127, 127, 127, 127,  7, 7, 127, 127, 127, 127, 127, 127, 127,  9, 7, 115, 120, 120, 127, 127, 127,
        16383, 127, 127, 127,  16383, 127, 127, 127, 127, 127, 127,  16383, 127, 127, 127, 127, 1, 127 },
        new[] { 16383 }, Repeat(127, 23), Repeat(1, 16), Repeat(16383, 10), Repeat(127, 6), Repeat(127, 28),
        new[] { 1, 76, 1, 76 }, Repeat(1, 16),
        InsEach(_ => Cat(new[] { 16383, 127 }, Repeat(16383, 10), Repeat(127, 6))), XgEq(4, 76, 120, 1, 40, 54, 54, 54, 58));
    // Values after a reset; -1 = set by module according to type ("auto").
    // XG types: MSB × 128 + LSB (Hall 1 = 01 00, Chorus 1 = 41 00, Delay L,C,R = 05 00).
    // EFX: type and parameters unknown after a reset ("auto"); no part goes through EFX.
    // XG insertion effects: Distortion (49 00) on no part. XG Multi EQ: flat.
    public static readonly int[] Defaults = Cat(new[] {
        4, 4, 0, 64, 64, 0, 0,  2, 0, 64, 8, 80, 3, 19, 0, 0,  0, 0, 97, 1, 1, 64, 80, 0,
        128, 64, 64, -1,  65 * 128, 64, 64, 0, -1, -1, -1,  5 * 128, 64, 64, 0, 0, 0, 127 },
        Repeat(-1, 24), Repeat(0, 16), Repeat(-1, 16), Repeat(-1, 28),
        new[] { 0, 64, 0, 64 }, Repeat(1, 16),   // EQ: 200 Hz / 3 kHz, gains 0 dB, every part through the EQ
        InsEach(_ => Cat(new[] { 0x49 * 128, 127 }, Repeat(-1, 16))), XgEq(0, 64, 7, 0, 0x0C, 0x1C, 0x22, 0x2E, 0x34));
    // Parameter set by type: in GS, type sets all effect's parameters;
    // in XG, only effect's own parameters (not return, pan or sends).
    public static readonly bool[] TypeDependent = CatBool(new[] {
        true, true, true, true, true, true, true,  true, true, true, true, true, true, true, true, true,
        true, true, true, true, true, true, true, true,
        true, false, false, true,  true, false, false, false, true, true, true,  true, false, false, false, false, false, false },
        RepeatBool(true, 24), RepeatBool(false, 16), RepeatBool(true, 16), RepeatBool(true, 28), RepeatBool(false, 20),
        InsTypeDependent, InsTypeDependent, InsTypeDependent, InsTypeDependent, RepeatBool(true, XgEqSize));
    static bool[] InsTypeDependent => CatBool(new[] { true, false }, RepeatBool(true, 16));
    public const int InsFirst = 146, InsSize = 18, InsCount = 4, InsBlockFirst = 8;
    public const int XgEqFirst = InsFirst + InsCount * InsSize, XgEqSize = 18, XgEqBlock = InsBlockFirst + InsCount;
    public const int Count = XgEqFirst + XgEqSize;
    // Type of each block (GS Reverb, GS Chorus, GS Delay, XG Reverb, XG Chorus, XG Variation, EFX, XG Insertion 1-4,
    // XG Multi EQ); the GS EQ has none.
    static readonly int[] TypeParams = { 0, 7, 16, 24, 28, 35, 42, InsFirst, InsFirst + InsSize, InsFirst + 2 * InsSize, InsFirst + 3 * InsSize, XgEqFirst };
    public const int Blocks = XgEqBlock + 1;

    static int[] Cat(params int[][] parts) { var l = new List<int>(); foreach (var a in parts) l.AddRange(a); return l.ToArray(); }
    static bool[] CatBool(params bool[][] parts) { var l = new List<bool>(); foreach (var a in parts) l.AddRange(a); return l.ToArray(); }
    static int[] Repeat(int v, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }
    static bool[] RepeatBool(bool v, int n) { var a = new bool[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }
    static int[] Range(int start, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = start + i; return a; }
    static int[] Range2(int start, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = start + 2 * i; return a; }
    static int[] InsEach(Func<int, int[]> part) { var l = new List<int>(); for (int n = 0; n < InsCount; n++) l.AddRange(part(n)); return l.ToArray(); }
    static readonly int[] effectFile = new int[Count];      // file value (-1 = unknown: set by type)
    static readonly bool[] effectFixed = new bool[Count];   // fixed in interface?
    static readonly int[] effectFixedValue = new int[Count];

    public static bool IsXg(int e) { return (Block[e] >= 3 && Block[e] <= 5) || e >= InsFirst; }
    public static bool IsInsertion(int e) { return e >= InsFirst && e < XgEqFirst; }
    // Type of the insertion effect of a parameter, and its parameters 1 to 10 (see Addresses).
    public static int InsertionType(int e) { return InsFirst + (e - InsFirst) / InsSize * InsSize; }
    static bool IsInsertionParam10(int e) { return IsInsertion(e) && Addresses[e] >= 0x02 && Addresses[e] <= 0x0B; }
    // Insertion types whose parameters 1 to 10 are 2 bytes (MSB × 128 + LSB): the delays (Delay L,C,R, Delay L,R,
    // Echo, Cross Delay), the …+Delay types, V-Flanger and Digital Scratch.
    public static bool InsertionNeedsMsb(int type)
    {
        int msb = type >> 7, lsb = type & 0x7F;
        return (msb >= 5 && msb <= 8 && lsb == 0) || (msb >= 0x5F && msb <= 0x61 && lsb <= 1) || (msb == 0x62 && (lsb == 1 || lsb == 3))
            || type == 0x68 * 128 || type == 0x76 * 128;
    }
    static int Size(int e) { return Max[e] > 127 ? 2 : 1; }
    public static bool IsType(int e) { return Array.IndexOf(TypeParams, e) >= 0; }
    public static int FileValue(int e) { return effectFile[e]; }
    public static bool IsFixed(int e) { return effectFixed[e]; }
    public static int FixedValue(int e) { return effectFixedValue[e]; }
    public static int Value(int e) { return effectFixed[e] ? effectFixedValue[e] : effectFile[e]; }

    public static void ResetFile()
    {
        for (int e = 0; e < Count; e++) effectFile[e] = Defaults[e];
    }

    // SysEx message for an effect parameter (GS: Roland checksum; XG: 2-byte type).
    // insertionType: type the insertion effect has when the message is played (default: the current one); it
    // chooses the address of its parameters 1 to 10.
    public static NormalSysExEvent ParamEvent(int e, int value, int insertionType = -1)
    {
        if (IsInsertion(e))
        {
            byte n = (byte)AddressHi[e];
            if (IsInsertionParam10(e))
            {
                if (insertionType < 0) insertionType = Value(InsertionType(e));
                if (!InsertionNeedsMsb(insertionType))
                    return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x03, n, (byte)Addresses[e], (byte)Math.Min(value, 127), 0xF7 });
                return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x03, n, (byte)(0x30 + 2 * (Addresses[e] - 0x02)), (byte)(value >> 7), (byte)(value & 0x7F), 0xF7 });
            }
            if (Size(e) == 2)
                return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x03, n, (byte)Addresses[e], (byte)(value >> 7), (byte)(value & 0x7F), 0xF7 });
            return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x03, n, (byte)Addresses[e], (byte)value, 0xF7 });
        }
        int address = Addresses[e];
        if (IsXg(e))
        {
            if (Size(e) == 2)
                return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x02, (byte)AddressHi[e], (byte)address, (byte)(value >> 7), (byte)(value & 0x7F), 0xF7 });
            return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x02, (byte)AddressHi[e], (byte)address, (byte)value, 0xF7 });
        }
        int hi = AddressHi[e];
        byte[] data = Size(e) == 2
            ? new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)hi, (byte)address, (byte)(value >> 7), (byte)(value & 0x7F), 0, 0xF7 }
            : new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)hi, (byte)address, (byte)value, 0, 0xF7 };
        MidiTools.FixRolandChecksum(data);
        return new NormalSysExEvent(data);
    }

    // Effect bytes of a SysEx: for each relevant data byte, { position, parameter, part }
    // (part 0 = single byte or MSB, 1 = LSB of a 2-byte value, 2 = 1-byte form of an insertion parameter 1 to 10).
    // null if it is not an effect SysEx.
    internal static List<int[]> ValueBytes(byte[] d, out bool gs, out int dataCount)
    {
        gs = false; dataCount = 0;
        int first, start, address, hi = 0x01;
        // GS: areas 40 01 (reverb, chorus, delay), 40 02 (EQ), 40 03 (EFX) and 40 4x (parts: EQ per channel
        // 40 4x 20, EFX per channel 40 4x 22).
        if (d.Length >= 10 && d[0] == 0x41 && d[2] == 0x42 && d[3] == 0x12 && d[4] == 0x40 &&
            (d[5] == 0x01 || d[5] == 0x02 || d[5] == 0x03 || (d[5] & 0xF0) == 0x40))
        {
            hi = d[5];
            gs = true; first = 7; start = 6; dataCount = d.Length - 9;      // … data, checksum, F7
        }
        else if (d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0x02 && (d[4] == 0x01 || d[4] == 0x40))
        {
            hi = d[4];
            first = 6; start = 5; dataCount = d.Length - 7;                // … data, F7
        }
        else if (d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0x03 && d[4] < InsCount)
        {
            // XG insertion effect d[4].
            dataCount = d.Length - 7;
            int insBase = InsFirst + d[4] * InsSize;
            var ins = new List<int[]>();
            for (int i = 0; i < dataCount; i++)
            {
                int a = d[5] + i, pos = 6 + i;
                if (a <= 0x01) ins.Add(new[] { pos, insBase, a });
                else if (a <= 0x0B) ins.Add(new[] { pos, insBase + a, 2 });                        // parameters 1-10 are insBase + 2..11
                else if (a == 0x0C) ins.Add(new[] { pos, insBase + 1, 0 });
                else if (a >= 0x20 && a <= 0x25) ins.Add(new[] { pos, insBase + 12 + a - 0x20, 0 });
                else if (a >= 0x30 && a <= 0x43) ins.Add(new[] { pos, insBase + 2 + (a - 0x30) / 2, (a - 0x30) % 2 });
            }
            return ins;
        }
        else return null;
        var result = new List<int[]>();
        for (int i = 0; i < dataCount; i++)
        {
            address = d[start] + i;
            for (int e = 0; e < Count; e++)
            {
                if (IsXg(e) == gs || IsInsertion(e) || AddressHi[e] != hi) continue;
                if (Addresses[e] == address) { result.Add(new[] { first + i, e, 0 }); break; }
                if (Size(e) == 2 && Addresses[e] + 1 == address) { result.Add(new[] { first + i, e, 1 }); break; }
            }
        }
        return result;
    }

    // Byte to write for value v of a parameter (part 0 = single byte or MSB, 1 = LSB).
    internal static byte ValueByte(int e, int part, int v)
    {
        if (Size(e) == 1) return (byte)v;
        if (part == 2) return (byte)Math.Min(v, 127);
        return part == 0 ? (byte)(v >> 7) : (byte)(v & 0x7F);
    }

    // A block's type has just changed: its parameters set by type become "auto".
    static void TypeChanged(int macro)
    {
        for (int e = 0; e < Count; e++)
            if (e != macro && Block[e] == Block[macro] && TypeDependent[e]) effectFile[e] = -1;
    }

    // Resends a block's fixed parameters (after its type, which may have overwritten them in module).
    static void SendFixedParams(IOutputDevice device, int block)
    {
        for (int e = 0; e < Count; e++)
            if (Block[e] == block && !IsType(e) && effectFixed[e]) device.SendEvent(ParamEvent(e, effectFixedValue[e]));
    }

    public static void Set(IOutputDevice device, int e, int value)
    {
        effectFixed[e] = true;
        effectFixedValue[e] = value;
        if (IsType(e)) TypeChanged(e);
        if (device == null) return;
        device.SendEvent(ParamEvent(e, value));
        if (IsType(e)) SendFixedParams(device, Block[e]);
    }

    // Hands a parameter back to file: resends file value if it is known.
    public static void Clear(IOutputDevice device, int e)
    {
        effectFixed[e] = false;
        if (device == null || effectFile[e] < 0) return;
        device.SendEvent(ParamEvent(e, effectFile[e]));
        if (IsType(e)) { TypeChanged(e); SendFixedParams(device, Block[e]); }
    }

    // Sends the fixed values, each block's type before its parameters (start of track, seek, port change, after a reset).
    public static void Apply(IOutputDevice device)
    {
        for (int block = 0; block < Blocks; block++)
        {
            for (int e = 0; e < Count; e++)
                if (Block[e] == block && IsType(e) && effectFixed[e]) device.SendEvent(ParamEvent(e, effectFixedValue[e]));
            SendFixedParams(device, block);
        }
    }

    // Effect SysEx (GS or XG) sent by file: stores values, replaces ones that are
    // fixed. Returns message to play (null = unchanged).
    internal static MidiEvent Read(byte[] d)
    {
        var device = ChannelMixer.Device;   // read first: the first use of ChannelMixer resets the effect values
        bool gs; int count;
        var bytes = ValueBytes(d, out gs, out count);
        if (bytes == null) return null;
        bool changed = false;
        var macroBlocks = new List<int>();
        var data = (byte[])d.Clone();
        foreach (var b in bytes)
        {
            int pos = b[0], e = b[1], part = b[2];
            int old = effectFile[e] < 0 ? 0 : effectFile[e];
            if (Size(e) == 1 || part == 2) effectFile[e] = d[pos];
            else if (part == 0) effectFile[e] = d[pos] * 128 + old % 128;
            else effectFile[e] = (old / 128) * 128 + d[pos];
            if (IsType(e) && !macroBlocks.Contains(Block[e])) { TypeChanged(e); macroBlocks.Add(Block[e]); }
            if (effectFixed[e]) { data[pos] = ValueByte(e, part, effectFixedValue[e]); changed = true; }
        }
        // Type changed by file: fixed parameters of this block are resent right after.
        foreach (int block in macroBlocks)
        {
            bool hasFixed = false;
            for (int e = 0; e < Count; e++) if (Block[e] == block && !IsType(e) && effectFixed[e]) hasFixed = true;
            if (hasFixed && device != null)
            {
                int blk = block;
                System.Threading.Tasks.Task.Delay(30).ContinueWith(_ => { try { SendFixedParams(device, blk); } catch { } });
            }
        }
        if (!changed) return null;
        if (gs) MidiTools.FixRolandChecksum(data);
        return new NormalSysExEvent(data);
    }

    // State after a reset: the file's values are the defaults.
    static GsXgEffects() { ResetFile(); }
}
