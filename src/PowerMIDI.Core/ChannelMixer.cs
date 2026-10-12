using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// 16-channel mixer and master volume. Callback / NoteCallback run on the DryWetMIDI playback
// thread: each event of the file passes through them, so that the values fixed in the interface
// replace the file's (and the LEDs see the notes). The effects are in GsXgEffects, Mt32Effects and Gm2Effects.
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
        GsXgEffects.ResetFile();
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
        MidiTools.FixRolandChecksum(data);
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

    // All fixed effects of the current mode (start of track, seek, port change, after a reset in file).
    public static void ApplyEffects(IOutputDevice device)
    {
        GsXgEffects.Apply(device);
        if (Mt32Mode) Mt32Effects.Apply(device);
        if (Gm2Mode) Gm2Effects.Apply(device);
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
            var effect = GsXgEffects.Read(d);
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
