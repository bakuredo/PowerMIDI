using System;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// 16-channel mixer, effects and master volume. Callback / NoteCallback run on the DryWetMIDI playback
// thread: each event of the file passes through them, so that the values fixed in the interface
// replace the file's (and the LEDs see the notes).
public static class ChannelMixer
{
    // Controllers adjustable in mixer (index k):
    // 0 = Volume (CC7), 1 = Pan (CC10), 2 = Reverb (CC91), 3 = Chorus (CC93),
    // 4 = Variation XG / Delay GS (CC94),
    // 5 = Attack Time (CC73), 6 = Decay Time (CC75), 7 = Release Time (CC72),
    // 8 = Filter Cutoff (CC74), 9 = Filter Resonance (CC71),
    // 10 = Vibrato Rate (CC76), 11 = Vibrato Depth (CC77), 12 = Vibrato Delay (CC78) :
    // relative values, 64 = sound's original setting.
    // 13 = Modulation (CC1), absolute (0 = none); added last so that the other indexes stay the same
    // (the mixer list shows it first).
    public static readonly int[] ControllerNumbers = { 7, 10, 91, 93, 94, 73, 75, 72, 74, 71, 76, 77, 78, 1 };
    public static readonly string[] ControllerNames =
        { "volume", "pan", "reverb", "chorus", "variation/delay", "attack", "decay", "release", "cutoff", "resonance",
          "vibrato rate", "vibrato depth", "vibrato delay", "modulation" };
    public static readonly int[] ControllerDefaults = { 100, 64, 40, 0, 0, 64, 64, 64, 64, 64, 64, 64, 64, 0 };   // values after a reset
    public const int Modulation = 13;
    const int Controllers = 14;
    static readonly int[,] FileCc = new int[Controllers, 16];       // last value sent by file
    static readonly bool[,] CcOverride = new bool[Controllers, 16]; // value fixed in interface?
    static readonly int[,] OverrideCc = new int[Controllers, 16];   // fixed value

    public static int ControllerIndex(int number)
    {
        return Array.IndexOf(ControllerNumbers, number);
    }

    // ---------- GS and XG effects ----------
    // GS: SysEx DT1 F0 41 dev 42 12 40 01 aa vv … sum F7 (1 byte per parameter, Roland checksum).
    // XG: SysEx F0 43 1n 4C 02 01 aa vv … F7 (no checksum; types are 2 bytes MSB/LSB).
    // EFX (SC-88Pro / SC-8850): DT1 40 03 xx (2-byte type) and 40 4x 22 (EFX on part x).
    // Effects tab parameters (index e). Blocks: 0 GS Reverb, 1 GS Chorus, 2 GS Delay,
    // 3 XG Reverb, 4 XG Chorus, 5 XG Variation, 6 GS EFX. first parameter of each block is its type.
    // 42: EFX type; 43-62: EFX parameters 1 to 20; 63-65: sends to reverb / chorus / delay;
    // 66-81: EFX on channels 1 to 16; 82-97: XG Variation parameters 1 to 16 (1-10 are 2 bytes:
    // 02 01 42, 44 … 54; 11-16: 02 01 70..75), placed at end of list but in block 5;
    // 98-112: XG Reverb parameters 2 to 16 (02 01 03..0B, 10..15; parameter 1 is index 27);
    // 113-125: XG Chorus parameters 4 to 16 (02 01 25..2B, 30..35; parameters 1-3 are 32-34).
    // Block 7, GS EQ (SC-88 and later; no type): 126-129 low frequency (0 = 200 Hz, 1 = 400 Hz), low gain
    // (52..76 = -12..+12 dB), high frequency (0 = 3 kHz, 1 = 6 kHz), high gain: DT1 40 02 00..03;
    // 130-145: EQ on channels 1 to 16 (Part EQ, DT1 40 4x 20; 1 = on, the state after a reset).
    public static readonly int[] EffectAddresses = Cat(new[] {
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x37,                      // 0-6   GS Reverb
        0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40,          // 7-15  GS Chorus
        0x50, 0x51, 0x52, 0x53, 0x54, 0x58, 0x59, 0x5A,                // 16-23 GS Delay
        0x00, 0x0C, 0x0D, 0x02,                                        // 24-27 XG Reverb: type, return, pan, time
        0x20, 0x2C, 0x2D, 0x2E, 0x22, 0x23, 0x24,                      // 28-34 XG Chorus: type, return, pan, to reverb, rate, depth, feedback
        0x40, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B },                    // 35-41 XG Variation: type, return, pan, to reverb, to chorus, connection, part
        new[] { 0x00 }, Range(0x03, 20), new[] { 0x17, 0x18, 0x19 }, Repeat(0x22, 16),    // 42-81 EFX
        Range2(0x42, 10), Range(0x70, 6),                                                  // 82-97 XG Variation
        Range(0x03, 9), Range(0x10, 6), Range(0x25, 7), Range(0x30, 6),                    // 98-125 XG Reverb / Chorus
        new[] { 0x00, 0x01, 0x02, 0x03 }, Repeat(0x20, 16));                               // 126-145 GS EQ
    // Middle address byte: GS 40 01 / XG 02 01 for blocks 0-5; EFX 40 03; EQ 40 02; EFX / EQ per part 40 4x
    // (block x: 1..9 = parts 1..9, 0 = part 10, A..F = parts 11..16; same numbering as 40 1x).
    public static readonly int[] EffectAddressHi = Cat(Repeat(0x01, 42), Repeat(0x03, 24),
        new[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x40, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F },
        Repeat(0x01, 16), Repeat(0x01, 28),
        Repeat(0x02, 4), new[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x40, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F });
    public static readonly int[] EffectBlock = Cat(new[] {
        0, 0, 0, 0, 0, 0, 0,  1, 1, 1, 1, 1, 1, 1, 1, 1,  2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3,  4, 4, 4, 4, 4, 4, 4,  5, 5, 5, 5, 5, 5, 5 }, Repeat(6, 40), Repeat(5, 16), Repeat(3, 15), Repeat(4, 13), Repeat(7, 20));
    public static readonly int[] EffectMin = Cat(new[] {
        0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 1, 1, 1, 0, 0, 0,
        0, 0, 1, 0,  0, 0, 1, 0, 0, 0, 1,  0, 0, 1, 0, 0, 0, 0 }, Repeat(0, 40), Repeat(0, 16), Repeat(0, 28),
        new[] { 0, 52, 0, 52 }, Repeat(0, 16));
    public static readonly int[] EffectMax = Cat(new[] {
        7, 7, 7, 127, 127, 127, 127,  7, 7, 127, 127, 127, 127, 127, 127, 127,  9, 7, 115, 120, 120, 127, 127, 127,
        16383, 127, 127, 127,  16383, 127, 127, 127, 127, 127, 127,  16383, 127, 127, 127, 127, 1, 127 },
        new[] { 16383 }, Repeat(127, 23), Repeat(1, 16), Repeat(16383, 10), Repeat(127, 6), Repeat(127, 28),
        new[] { 1, 76, 1, 76 }, Repeat(1, 16));
    // Values after a reset; -1 = set by module according to type ("auto").
    // XG types: MSB × 128 + LSB (Hall 1 = 01 00, Chorus 1 = 41 00, Delay L,C,R = 05 00).
    // EFX: type and parameters unknown after a reset ("auto"); no part goes through EFX.
    public static readonly int[] EffectDefaults = Cat(new[] {
        4, 4, 0, 64, 64, 0, 0,  2, 0, 64, 8, 80, 3, 19, 0, 0,  0, 0, 97, 1, 1, 64, 80, 0,
        128, 64, 64, -1,  65 * 128, 64, 64, 0, -1, -1, -1,  5 * 128, 64, 64, 0, 0, 0, 127 },
        Repeat(-1, 24), Repeat(0, 16), Repeat(-1, 16), Repeat(-1, 28),
        new[] { 0, 64, 0, 64 }, Repeat(1, 16));   // EQ: 200 Hz / 3 kHz, gains 0 dB, every part through the EQ
    // Parameter set by type: in GS, type sets all effect's parameters;
    // in XG, only effect's own parameters (not return, pan or sends).
    public static readonly bool[] EffectTypeDependent = CatBool(new[] {
        true, true, true, true, true, true, true,  true, true, true, true, true, true, true, true, true,
        true, true, true, true, true, true, true, true,
        true, false, false, true,  true, false, false, false, true, true, true,  true, false, false, false, false, false, false },
        RepeatBool(true, 24), RepeatBool(false, 16), RepeatBool(true, 16), RepeatBool(true, 28), RepeatBool(false, 20));
    const int Effects = 146;
    // Type of each block (GS Reverb, GS Chorus, GS Delay, XG Reverb, XG Chorus, XG Variation, EFX); the EQ has none.
    static readonly int[] MacroParams = { 0, 7, 16, 24, 28, 35, 42 };
    public const int EffectBlocks = 8;

    static int[] Cat(params int[][] parts) { var l = new List<int>(); foreach (var a in parts) l.AddRange(a); return l.ToArray(); }
    static bool[] CatBool(params bool[][] parts) { var l = new List<bool>(); foreach (var a in parts) l.AddRange(a); return l.ToArray(); }
    static int[] Repeat(int v, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }
    static bool[] RepeatBool(bool v, int n) { var a = new bool[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }
    static int[] Range(int start, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = start + i; return a; }
    static int[] Range2(int start, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = start + 2 * i; return a; }
    static readonly int[] effectFile = new int[Effects];      // file value (-1 = unknown: set by type)
    static readonly bool[] effectFixed = new bool[Effects];   // fixed in interface?
    static readonly int[] effectFixedValue = new int[Effects];

    public static bool IsXgEffect(int e) { return EffectBlock[e] >= 3 && EffectBlock[e] <= 5; }
    static int EffectSize(int e) { return EffectMax[e] > 127 ? 2 : 1; }
    public static bool IsMacro(int e) { return Array.IndexOf(MacroParams, e) >= 0; }
    public static int EffectFileValue(int e) { return effectFile[e]; }
    public static bool IsEffectFixed(int e) { return effectFixed[e]; }
    public static int EffectFixedValue(int e) { return effectFixedValue[e]; }
    public static int EffectValue(int e) { return effectFixed[e] ? effectFixedValue[e] : effectFile[e]; }

    public static void ResetEffects()
    {
        for (int e = 0; e < Effects; e++) effectFile[e] = EffectDefaults[e];
    }

    // SysEx message for an effect parameter (GS: Roland checksum; XG: 2-byte type).
    public static NormalSysExEvent EffectEvent(int e, int value)
    {
        int address = EffectAddresses[e];
        if (IsXgEffect(e))
        {
            if (EffectSize(e) == 2)
                return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x02, 0x01, (byte)address, (byte)(value >> 7), (byte)(value & 0x7F), 0xF7 });
            return new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x02, 0x01, (byte)address, (byte)value, 0xF7 });
        }
        int hi = EffectAddressHi[e];
        byte[] data = EffectSize(e) == 2
            ? new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)hi, (byte)address, (byte)(value >> 7), (byte)(value & 0x7F), 0, 0xF7 }
            : new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)hi, (byte)address, (byte)value, 0, 0xF7 };
        FixGsChecksum(data);
        return new NormalSysExEvent(data);
    }

    // Effect bytes of a SysEx: for each relevant data byte, { position, parameter, part }
    // (part 0 = single byte or MSB, 1 = LSB of an XG type). null if it is not an effect SysEx.
    internal static List<int[]> EffectBytes(byte[] d, out bool gs, out int dataCount)
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
        else if (d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0x02 && d[4] == 0x01)
        {
            first = 6; start = 5; dataCount = d.Length - 7;                // … data, F7
        }
        else return null;
        var result = new List<int[]>();
        for (int i = 0; i < dataCount; i++)
        {
            address = d[start] + i;
            for (int e = 0; e < Effects; e++)
            {
                if (IsXgEffect(e) == gs || (gs && EffectAddressHi[e] != hi)) continue;
                if (EffectAddresses[e] == address) { result.Add(new[] { first + i, e, 0 }); break; }
                if (EffectSize(e) == 2 && EffectAddresses[e] + 1 == address) { result.Add(new[] { first + i, e, 1 }); break; }
            }
        }
        return result;
    }

    // Byte to write for value v of a parameter (part 0 = single byte or MSB, 1 = LSB).
    internal static byte EffectByte(int e, int part, int v)
    {
        if (EffectSize(e) == 1) return (byte)v;
        return part == 0 ? (byte)(v >> 7) : (byte)(v & 0x7F);
    }

    // Recomputes Roland checksum of a GS DT1 SysEx (address + data).
    internal static void FixGsChecksum(byte[] data)
    {
        int sum = 0;
        for (int i = 4; i < data.Length - 2; i++) sum += data[i];
        data[data.Length - 2] = (byte)((128 - sum % 128) % 128);
    }

    // A block's type has just changed: its parameters set by type become "auto".
    static void MacroChanged(int macro)
    {
        for (int e = 0; e < Effects; e++)
            if (e != macro && EffectBlock[e] == EffectBlock[macro] && EffectTypeDependent[e]) effectFile[e] = -1;
    }

    // Resends a block's fixed parameters (after its type, which may have overwritten them in module).
    static void SendFixedParams(IOutputDevice device, int block)
    {
        for (int e = 0; e < Effects; e++)
            if (EffectBlock[e] == block && !IsMacro(e) && effectFixed[e]) device.SendEvent(EffectEvent(e, effectFixedValue[e]));
    }

    public static void SetEffect(IOutputDevice device, int e, int value)
    {
        effectFixed[e] = true;
        effectFixedValue[e] = value;
        if (IsMacro(e)) MacroChanged(e);
        if (device == null) return;
        device.SendEvent(EffectEvent(e, value));
        if (IsMacro(e)) SendFixedParams(device, EffectBlock[e]);
    }

    // Hands a parameter back to file: resends file value if it is known.
    public static void ClearEffect(IOutputDevice device, int e)
    {
        effectFixed[e] = false;
        if (device == null || effectFile[e] < 0) return;
        device.SendEvent(EffectEvent(e, effectFile[e]));
        if (IsMacro(e)) { MacroChanged(e); SendFixedParams(device, EffectBlock[e]); }
    }

    // All fixed effects (start of track, seek, port change, after a reset in file).
    public static void ApplyEffects(IOutputDevice device)
    {
        for (int block = 0; block < EffectBlocks; block++)
        {
            for (int e = 0; e < Effects; e++)
                if (EffectBlock[e] == block && IsMacro(e) && effectFixed[e]) device.SendEvent(EffectEvent(e, effectFixedValue[e]));
            SendFixedParams(device, block);
        }
        // MT-32 mode: the MT-32 reverb values fixed in the interface.
        if (Mt32Mode) Mt32Effects.Apply(device);
        // GM2 mode: the GM2 reverb / chorus values fixed in the interface.
        if (Gm2Mode) Gm2Effects.Apply(device);
    }

    // Effect SysEx (GS or XG) sent by file: stores values, replaces ones that are
    // fixed. Returns message to play (null = unchanged).
    static MidiEvent ReadEffects(byte[] d)
    {
        bool gs; int count;
        var bytes = EffectBytes(d, out gs, out count);
        if (bytes == null) return null;
        bool changed = false;
        var macroBlocks = new List<int>();
        var data = (byte[])d.Clone();
        foreach (var b in bytes)
        {
            int pos = b[0], e = b[1], part = b[2];
            int old = effectFile[e] < 0 ? 0 : effectFile[e];
            if (EffectSize(e) == 1) effectFile[e] = d[pos];
            else if (part == 0) effectFile[e] = d[pos] * 128 + old % 128;
            else effectFile[e] = (old / 128) * 128 + d[pos];
            if (IsMacro(e) && !macroBlocks.Contains(EffectBlock[e])) { MacroChanged(e); macroBlocks.Add(EffectBlock[e]); }
            if (effectFixed[e]) { data[pos] = EffectByte(e, part, effectFixedValue[e]); changed = true; }
        }
        // Type changed by file: fixed parameters of this block are resent right after.
        var device = Device;
        foreach (int block in macroBlocks)
        {
            bool hasFixed = false;
            for (int e = 0; e < Effects; e++) if (EffectBlock[e] == block && !IsMacro(e) && effectFixed[e]) hasFixed = true;
            if (hasFixed && device != null)
            {
                int blk = block;
                System.Threading.Tasks.Task.Delay(30).ContinueWith(_ => { try { SendFixedParams(device, blk); } catch { } });
            }
        }
        if (!changed) return null;
        if (gs) FixGsChecksum(data);
        return new NormalSysExEvent(data);
    }

    // ---------- Master volume (universal SysEx Master Volume: F0 7F 7F 04 01 lsb msb F7) ----------
    // Volume sent = file's master volume × interface slider / 127.
    public static bool ForceMode;             // forced mode: file resets ignored during playback
    public static int[] DropManufacturers = new int[0];   // + "Clean": manufacturers of ignored SysEx
    public static int MasterVolume = 127;     // interface slider
    static int fileMasterVolume = 127;        // last master volume requested by file (127 after a reset)
    public static IOutputDevice Device;       // output port, for resending after a reset in file

    static NormalSysExEvent MasterVolumeEvent()
    {
        int value = (fileMasterVolume * MasterVolume + 63) / 127;
        return new NormalSysExEvent(new byte[] { 0x7F, 0x7F, 0x04, 0x01, 0x00, (byte)value, 0xF7 });
    }

    public static void SetMasterVolume(IOutputDevice device, int volume)
    {
        MasterVolume = volume;
        if (device != null) device.SendEvent(MasterVolumeEvent());
    }

    // file has just sent a reset (GS Reset, XG System On, GM On): module has reset everything,
    // including master volume and settings fixed in interface. They are resent a little
    // later, once module has finished its reset.
    static void ResendAfterReset()
    {
        var device = Device;
        if (device == null) return;
        System.Threading.Tasks.Task.Delay(150).ContinueWith(_ =>
        {
            try
            {
                device.SendEvent(MasterVolumeEvent());
                ApplyAll(device);
                ReapplyOverrides(device);
                ApplyEffects(device);
            }
            catch { }   // port closed in meantime
        });
    }
    public static readonly bool[] Muted = new bool[16];
    public static readonly long[] LastNoteOn = new long[16]; // DateTime.UtcNow.Ticks, for LEDs
    public static readonly int[] Program = new int[16];      // last Program Change (0..127)
    public static readonly int[] BankMsb = new int[16];      // CC0
    public static readonly int[] BankLsb = new int[16];      // CC32
    public static readonly bool[] Used = new bool[16];       // channel has received a Program Change or a note
    public static readonly int[] RhythmPart = new int[16];   // GS "Use for Rhythm Part": 0 = melodic, 1/2 = drums

    // Instrument chosen by user: replaces file's CC0 / CC32 / Program Change.
    public static readonly bool[] Override = new bool[16];
    public static readonly int[] OverrideMsb = new int[16];
    public static readonly int[] OverrideLsb = new int[16];
    public static readonly int[] OverrideProgram = new int[16];

    // Drum part chosen by the user (GS "Use for Rhythm Part"): -1 = the file's, 0 = normal part,
    // 1 / 2 = drum part 1 / 2. Replaces the file's Use for Rhythm Part messages for this channel.
    public static readonly int[] RhythmOverride = new int[16];

    // Drum part actually used on a channel: the one chosen by the user, otherwise the file's.
    public static int Rhythm(int channel) { return RhythmOverride[channel] >= 0 ? RhythmOverride[channel] : RhythmPart[channel]; }

    // Drum kit played by each GS drum part (index 1 / 2): the module has a single kit per drum part, set by the
    // last bank / program sent to any of its channels (by the file or by the interface). Program -1 = unknown.
    public static readonly int[] DrumKitMsb = new int[3];
    public static readonly int[] DrumKitLsb = new int[3];
    public static readonly int[] DrumKitProgram = { -1, -1, -1 };

    static void NoteSentProgram(int channel, int msb, int lsb, int program)
    {
        int part = Rhythm(channel);
        if (part != 1 && part != 2) return;
        DrumKitMsb[part] = msb;
        DrumKitLsb[part] = lsb;
        DrumKitProgram[part] = program;
    }

    static ChannelMixer()
    {
        for (int i = 0; i < 16; i++) RhythmOverride[i] = -1;
        ResetChannelState(false);
    }

    // After a reset, module sets CC7 to 100, pan to center, program and bank to 0,
    // and channel 10 to drums (in XG: bank MSB 127 on channel 10).
    public static void ResetChannelState(bool xg)
    {
        for (int i = 0; i < 16; i++)
        {
            for (int k = 0; k < Controllers; k++) FileCc[k, i] = ControllerDefaults[k];
            fileMasterVolume = 127;
            Program[i] = 0;
            BankMsb[i] = 0;
            BankLsb[i] = 0;
            Used[i] = false;
            LastNoteOn[i] = 0;
            RhythmPart[i] = 0;
            // instruments and fixed values (Override, CcOverride) are not touched here:
            // player clears them itself when it moves to another file.
        }
        RhythmPart[9] = 1;
        if (xg) BankMsb[9] = 127;
        DrumKitProgram[1] = DrumKitProgram[2] = -1;
        Mt32Effects.ResetFile();
        Gm2Effects.ResetFile();
        ResetEffects();
    }

    // GS DT1 SysEx "Use for Rhythm Part": F0 41 dev 42 12 40 1x 15 vv sum F7.
    // Block x: 1..9 = parts 1..9, 0 = part 10, A..F = parts 11..16.
    // Channel of such a message, or -1 if it is not one.
    public static int RhythmPartChannel(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x41 && d[2] == 0x42 && d[3] == 0x12 &&
            d[4] == 0x40 && (d[5] & 0xF0) == 0x10 && d[6] == 0x15)
        {
            int block = d[5] & 0x0F;
            return block == 0 ? 9 : block <= 9 ? block - 1 : block;
        }
        return -1;
    }

    public static NormalSysExEvent RhythmPartEvent(int channel, int value)
    {
        int block = channel == 9 ? 0 : channel < 9 ? channel + 1 : channel;
        var data = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)(0x10 | block), 0x15, (byte)value, 0, 0xF7 };
        FixGsChecksum(data);
        return new NormalSysExEvent(data);
    }

    // Use for Rhythm Part sent by the file: stores it; replaced if the user chose the drum part of this channel.
    // Returns the message to play (null = unchanged).
    static MidiEvent ReadRhythmPart(byte[] d)
    {
        int channel = RhythmPartChannel(d);
        if (channel < 0) return null;
        RhythmPart[channel] = d[7];
        return RhythmOverride[channel] >= 0 ? RhythmPartEvent(channel, RhythmOverride[channel]) : null;
    }

    // Assigns a channel to the normal part (0) or to drum part 1 / 2, then resends its instrument:
    // the module reads the program again as a drum kit (or as a sound).
    public static void SetRhythmOverride(IOutputDevice device, int channel, int value)
    {
        RhythmOverride[channel] = value;
        Used[channel] = true;
        SendRhythmAndInstrument(device, channel);
    }

    // Hands the drum part back to the file.
    public static void ClearRhythmOverride(IOutputDevice device, int channel)
    {
        RhythmOverride[channel] = -1;
        SendRhythmAndInstrument(device, channel);
    }

    static void SendRhythmAndInstrument(IOutputDevice device, int channel)
    {
        if (device != null) device.SendEvent(RhythmPartEvent(channel, Rhythm(channel)));
        if (Override[channel]) SendInstrument(device, channel, OverrideMsb[channel], OverrideLsb[channel], OverrideProgram[channel]);
        else SendInstrument(device, channel, BankMsb[channel], BankLsb[channel], Program[channel]);
    }

    public static int FileValue(int k, int channel) { return FileCc[k, channel]; }
    public static bool IsFixed(int k, int channel) { return CcOverride[k, channel]; }
    public static int FixedValue(int k, int channel) { return OverrideCc[k, channel]; }

    // Value shown on slider: one fixed in interface, otherwise file's.
    public static int Value(int k, int channel)
    {
        return CcOverride[k, channel] ? OverrideCc[k, channel] : FileCc[k, channel];
    }

    // Value actually sent to module (volume 0 if channel is muted).
    public static int Sent(int k, int channel)
    {
        return k == 0 && Muted[channel] ? 0 : Value(k, channel);
    }

    // MT-32 mode: the file's MT-32 reverb SysEx are read (Effects tab) and corrected with the values fixed there.
    public static bool Mt32Mode;

    // GM2 mode: the file's GM2 reverb / chorus SysEx are read (Effects tab) and corrected with the values fixed there.
    public static bool Gm2Mode;

    public static void SetController(IOutputDevice device, int k, int channel, int value)
    {
        OverrideCc[k, channel] = value;
        CcOverride[k, channel] = true;
        if (device != null) ApplyController(device, k, channel);
    }

    // Hands value back to file (its last value for this controller).
    public static void ClearController(IOutputDevice device, int k, int channel)
    {
        CcOverride[k, channel] = false;
        if (device != null) ApplyController(device, k, channel);
    }

    public static void ApplyController(IOutputDevice device, int k, int channel)
    {
        device.SendEvent(Cc(channel, ControllerNumbers[k], Sent(k, channel)));
    }

    public static MidiEvent Callback(MidiEvent e, long rawTime, TimeSpan playbackTime)
    {
        var cc = e as ControlChangeEvent;
        if (cc != null)
        {
            int channel = cc.Channel;
            switch ((int)cc.ControlNumber)
            {
                case 1:
                case 7:
                case 10:
                case 91:
                case 93:
                case 94:
                case 73:
                case 75:
                case 72:
                case 74:
                case 71:
                case 76:
                case 77:
                case 78:
                    int k = ControllerIndex(cc.ControlNumber);
                    FileCc[k, channel] = cc.ControlValue;
                    return Cc(channel, cc.ControlNumber, Sent(k, channel));
                case 0:
                    BankMsb[channel] = cc.ControlValue;
                    if (Override[channel]) return Cc(channel, 0, OverrideMsb[channel]);
                    break;
                case 32:
                    BankLsb[channel] = cc.ControlValue;
                    if (Override[channel]) return Cc(channel, 32, OverrideLsb[channel]);
                    break;
            }
            return e;
        }
        var pc = e as ProgramChangeEvent;
        if (pc != null)
        {
            int channel = pc.Channel;
            Program[channel] = pc.ProgramNumber;
            Used[channel] = true;
            if (Override[channel])
            {
                NoteSentProgram(channel, OverrideMsb[channel], OverrideLsb[channel], OverrideProgram[channel]);
                return Pc(channel, OverrideProgram[channel]);
            }
            NoteSentProgram(channel, BankMsb[channel], BankLsb[channel], Program[channel]);
            return e;
        }
        var sysEx = e as SysExEvent;
        if (sysEx != null)
        {
            var d = sysEx.Data;
            // file's master volume: scaled by interface slider.
            if (d.Length >= 6 && d[0] == 0x7F && d[2] == 0x04 && d[3] == 0x01)
            {
                fileMasterVolume = d[5];
                return MasterVolumeEvent();
            }
            // Forced mode + "Clean": SysEx of other modes ignored (manufacturer 41 Roland / 43 Yamaha).
            if (ForceMode && MidiFileEditor.IsOtherModeSysEx(d, DropManufacturers)) return null;
            if (MidiFileEditor.IsReset(e))
            {
                // Mode forced in interface: file's resets (GS / XG / GM / GM2) are ignored.
                if (ForceMode) return null;
                bool xg = d.Length >= 3 && d[0] == 0x43 && d[2] == 0x4C;
                ResetChannelState(xg);
                ResendAfterReset();
                return e;
            }
            if (Mt32Mode)
            {
                var mt32 = Mt32Effects.Read(d);
                if (mt32 != null) return mt32;
            }
            if (Gm2Mode)
            {
                var gm2 = Gm2Effects.Read(d);
                if (gm2 != null) return gm2;
            }
            var effect = ReadEffects(d);
            if (effect != null) return effect;
            var rhythm = ReadRhythmPart(d);
            if (rhythm != null) return rhythm;
        }
        return e;
    }

    // Notes do not go through EventCallback: DryWetMIDI plays them via NoteCallback.
    public static NotePlaybackData NoteCallback(NotePlaybackData note, long rawTime, long rawLength, TimeSpan playbackTime)
    {
        LastNoteOn[note.Channel] = DateTime.UtcNow.Ticks;
        Used[note.Channel] = true;
        return note;
    }

    // Volume of a channel (after mute / unmute).
    public static void Apply(IOutputDevice device, int channel)
    {
        ApplyController(device, 0, channel);
    }

    // All controllers of all channels (start of track, seek, port change).
    public static void ApplyAll(IOutputDevice device)
    {
        for (int i = 0; i < 16; i++)
            for (int k = 0; k < Controllers; k++) ApplyController(device, k, i);
    }

    static ControlChangeEvent Cc(int channel, int number, int value)
    {
        return new ControlChangeEvent((SevenBitNumber)(byte)number, (SevenBitNumber)(byte)value)
        {
            Channel = (FourBitNumber)(byte)channel
        };
    }

    static ProgramChangeEvent Pc(int channel, int program)
    {
        return new ProgramChangeEvent((SevenBitNumber)(byte)program) { Channel = (FourBitNumber)(byte)channel };
    }

    static void SendInstrument(IOutputDevice device, int channel, int msb, int lsb, int program)
    {
        NoteSentProgram(channel, msb, lsb, program);
        if (device == null) return;
        device.SendEvent(Cc(channel, 0, msb));
        device.SendEvent(Cc(channel, 32, lsb));
        device.SendEvent(Pc(channel, program));
    }

    // Forces an instrument on a channel and sends it to module immediately.
    public static void SetOverride(IOutputDevice device, int channel, int msb, int lsb, int program)
    {
        OverrideMsb[channel] = msb;
        OverrideLsb[channel] = lsb;
        OverrideProgram[channel] = program;
        Override[channel] = true;
        Used[channel] = true;
        SendInstrument(device, channel, msb, lsb, program);
    }

    // Hands control back to file: resends last instrument requested by file.
    public static void ClearOverride(IOutputDevice device, int channel)
    {
        Override[channel] = false;
        SendInstrument(device, channel, BankMsb[channel], BankLsb[channel], Program[channel]);
    }

    // After a seek, a port change or a reset: resends the chosen drum parts and the forced instruments.
    public static void ReapplyOverrides(IOutputDevice device)
    {
        for (int i = 0; i < 16; i++)
        {
            if (RhythmOverride[i] >= 0) SendRhythmAndInstrument(device, i);
            else if (Override[i]) SendInstrument(device, i, OverrideMsb[i], OverrideLsb[i], OverrideProgram[i]);
        }
    }
}
