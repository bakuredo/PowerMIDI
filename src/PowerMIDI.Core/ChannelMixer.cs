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
    // 14 = Expression (CC11, after Volume in the menu), 15 = Portamento Time (CC5, last): absolute.
    public static readonly int[] ControllerNumbers = { 7, 10, 91, 93, 94, 73, 75, 72, 74, 71, 76, 77, 78, 1, 11, 5 };
    public static readonly string[] ControllerNames =
        { "volume", "pan", "reverb", "chorus", "variation/delay", "attack", "decay", "release", "cutoff", "resonance",
          "vibrato rate", "vibrato depth", "vibrato delay", "modulation", "expression", "portamento time" };
    public static readonly int[] ControllerDefaults = { 100, 64, 40, 0, 0, 64, 64, 64, 64, 64, 64, 64, 64, 0, 127, 0 };   // values after a reset
    public const int Modulation = 13, Expression = 14, PortamentoTime = 15;
    const int Controllers = 16;
    // Portamento switch (CC65) of each channel: the file's. While Portamento Time is fixed in the interface, the
    // portamento is on (127), so that the time is heard, and off (0) when the time is fixed at 0; back to the file's
    // switch when it is no longer fixed.
    static readonly int[] filePortamento = new int[16];
    public const int PortamentoSwitch = 65, PortamentoOn = 127;
    public static int PortamentoSwitchValue(int channel) =>
        IsActive(PortamentoTime, channel) ? PortamentoSwitchFor(OverrideCc[PortamentoTime, channel]) : filePortamento[channel];
    public static int PortamentoSwitchFor(int time) => time > 0 ? PortamentoOn : 0;
    static readonly int[,] FileCc = new int[Controllers, 16];       // last value sent by file
    static readonly bool[,] CcOverride = new bool[Controllers, 16]; // value fixed in interface?
    static readonly int[,] OverrideCc = new int[Controllers, 16];   // fixed value

    // A fixed value replaces the file's in the first passage of its controller (see ControllerPassages), or in the whole
    // track (CcWholeTrack: chosen in the Mixer tab, or set past the first passage, where it would otherwise not be heard).
    public static readonly bool[,] CcWholeTrack = new bool[Controllers, 16];
    static readonly bool[,] ccFirst = new bool[Controllers, 16];      // playback in the first passage of the controller?
    // First passages of the track being played (null: the whole track is one passage).
    public static volatile ControllerPassages CcPassages;

    // The first passage is followed at each value of the controller read in the file (rawTime: its tick).
    static void ReadPassage(int k, int channel, long rawTime) => ccFirst[k, channel] = CcPassages?.InFirst(k, channel, rawTime) ?? true;

    // Does the fixed value replace the file's now? (fixed, and in its first passage or for the whole track)
    public static bool IsActive(int k, int channel) => CcOverride[k, channel] && (CcWholeTrack[k, channel] || ccFirst[k, channel]);

    // Does the fixed value replace a value of the file at a tick? (Event Editor)
    public static bool IsActiveAt(int k, int channel, long tick) =>
        CcOverride[k, channel] && (CcWholeTrack[k, channel] || (CcPassages?.InFirst(k, channel, tick) ?? true));

    // Playback past the first passage of the controller (the file's values play again)?
    public static bool IsPastFirst(int k, int channel) => !ccFirst[k, channel];

    // Playback goes on from a tick without reading the events before it (loop restart): the first passages there; a
    // fixed value active again is sent, one no longer active gets the file's value back.
    public static void EnterPassagesAt(IOutputDevice device, long tick)
    {
        for (int k = 0; k < Controllers; k++)
            for (int c = 0; c < 16; c++)
            {
                bool first = CcPassages?.InFirst(k, c, tick) ?? true;
                if (first == ccFirst[k, c]) continue;
                ccFirst[k, c] = first;
                if (device != null && CcOverride[k, c] && !CcWholeTrack[k, c]) ApplyController(device, k, c);
            }
    }

    public static int ControllerIndex(int number)
    {
        return Array.IndexOf(ControllerNumbers, number);
    }

    // ---------- Master volume (universal SysEx Master Volume: F0 7F 7F 04 01 lsb msb F7) ----------
    // Volume sent = file's master volume × interface slider / 127.
    public static bool ForceMode;             // forced mode: file resets ignored during playback
    public static int[] DropManufacturers = new int[0];   // forced mode: manufacturers of the ignored SysEx (other modes)
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
                ApplyVoiceMap(device);
                ApplyGsDefaultMap(device);
                ReapplyOverrides(device);
                ApplyEffects(device);
            }
            catch { }   // port closed in meantime
        });
    }

    // Messages to send right after the one being played (the callback returns only one), e.g. the fixed values of a
    // drum setup or of an effect block that the event played has just reset (the module needs a moment for that).
    internal static void SendLater(params MidiEvent[] events)
    {
        var device = Device;
        if (device == null || events.Length == 0) return;
        System.Threading.Tasks.Task.Delay(30).ContinueWith(_ =>
        {
            try { foreach (var e in events) device.SendEvent(e); }
            catch { }   // port closed in meantime
        });
    }

    static readonly NrpnState nrpn = new();   // NRPN / RPN selected on each channel (XG drum setup and part)

    static void SendDrumSetupLater(int setup)
    {
        if (setup >= 0) SendLater(XgDrumSetup.FixedEvents(setup).ToArray());
    }

    static void SendGsDrumMapLater(int map)
    {
        if (map >= 0) SendLater(GsDrumMap.FixedEvents(map).ToArray());
    }

    // Selects again the drum set of a GS drum map (the module then initializes the map), then writes the values set by
    // the file and the fixed ones: a value that depends on the note goes back to the drum set's.
    public static void ResetGsDrumMap(IOutputDevice device, int map)
    {
        if (device == null) return;
        int channel = Enumerable.Range(0, 16).FirstOrDefault(c => GsDrumMap.MapOf(c) == map, -1);
        if (channel >= 0)
        {
            var (msb, lsb, program) = Override[channel] ? (OverrideMsb[channel], OverrideLsb[channel], OverrideProgram[channel])
                                                        : (BankMsb[channel], BankLsb[channel], Program[channel]);
            device.SendEvent(Cc(channel, 0, msb));
            device.SendEvent(Cc(channel, 32, lsb));
            device.SendEvent(Pc(channel, program));
        }
        foreach (var e in GsDrumMap.KnownEvents(map)) device.SendEvent(e);
    }
    public static readonly bool[] Muted = new bool[16];
    public static readonly long[] LastNoteOn = new long[16]; // DateTime.UtcNow.Ticks, for LEDs
    // Notes played (channel, note), collected while the Drums page of the Effects tab follows them (Follow): every note
    // counts, also those played at the same time on a channel.
    public static volatile bool CollectNotes;
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(int Channel, int Note)> PlayedNotes = new();
    public static readonly int[] Program = new int[16];      // last Program Change (0..127)
    public static readonly int[] BankMsb = new int[16];      // CC0
    public static readonly int[] BankLsb = new int[16];      // CC32
    public static readonly bool[] Used = new bool[16];       // channel has received a Program Change or a note
    public static readonly int[] RhythmPart = new int[16];   // GS "Use for Rhythm Part": 0 = melodic, 1/2 = drums
    public static readonly int[] Sustain = new int[16];      // CC64 (hold pedal), given back after a pause
    public static readonly int[] Sostenuto = new int[16];    // CC66

    // Instrument chosen by the user for a passage of a channel (see InstrumentPassages): replaces the file's bank
    // selects and Program Changes of that passage. Override…: the choice for the passage being played (the one shown
    // in the Mixer tab), or none.
    public static readonly bool[] Override = new bool[16];
    public static readonly int[] OverrideMsb = new int[16];
    public static readonly int[] OverrideLsb = new int[16];
    public static readonly int[] OverrideProgram = new int[16];
    static readonly Dictionary<(int Channel, long Passage), (int Msb, int Lsb, int Program)> instruments = new();   // locked
    public static readonly long[] Passage = new long[16];   // passage being played on each channel
    // Passages of the track being played (null: one passage per channel).
    public static volatile InstrumentPassages Passages;
    static readonly int[] sentMsb = new int[16], sentLsb = new int[16];   // last bank sent to the module

    // The instruments chosen, for all passages (save, Event Editor).
    public static List<(int Channel, long Passage, int Msb, int Lsb, int Program)> Instruments()
    {
        lock (instruments)
            return instruments.OrderBy(x => x.Key.Channel).ThenBy(x => x.Key.Passage)
                              .Select(x => (x.Key.Channel, x.Key.Passage, x.Value.Msb, x.Value.Lsb, x.Value.Program)).ToList();
    }

    // Instrument chosen for a passage of a channel, or null.
    public static (int Msb, int Lsb, int Program)? InstrumentOf(int channel, long passage)
    {
        lock (instruments) return instruments.TryGetValue((channel, passage), out var x) ? x : null;
    }

    // An instrument chosen on a channel, in any passage?
    public static bool HasInstruments(int channel)
    {
        lock (instruments) return instruments.Keys.Any(k => k.Channel == channel);
    }

    // The channel plays another passage: the choice for it, if any, becomes the current one.
    static void EnterPassage(int channel, long passage)
    {
        Passage[channel] = passage;
        var chosen = InstrumentOf(channel, passage);
        if (chosen is var (msb, lsb, program)) (OverrideMsb[channel], OverrideLsb[channel], OverrideProgram[channel]) = (msb, lsb, program);
        Override[channel] = chosen != null;
    }

    static void SendBank(int channel, int msb, int lsb)
    {
        var device = Device;
        if (device == null) return;
        device.SendEvent(Cc(channel, 0, msb));
        device.SendEvent(Cc(channel, 32, lsb));
        (sentMsb[channel], sentLsb[channel]) = (msb, lsb);
    }

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
            for (int k = 0; k < Controllers; k++) { FileCc[k, i] = ControllerDefaults[k]; ccFirst[k, i] = true; }
            filePortamento[i] = 0;
            fileMasterVolume = 127;
            Program[i] = 0;
            BankMsb[i] = 0;
            BankLsb[i] = 0;
            Used[i] = false;
            LastNoteOn[i] = 0;
            RhythmPart[i] = 0;
            Sustain[i] = Sostenuto[i] = 0;
            SysExPatch[i] = false;
            // instruments and fixed values (Override, CcOverride) are not touched here:
            // player clears them itself when it moves to another file. The first passage starts again.
            EnterPassage(i, InstrumentPassages.First);
        }
        RhythmPart[9] = 1;
        if (xg) BankMsb[9] = 127;
        for (int i = 0; i < 16; i++) (sentMsb[i], sentLsb[i]) = (BankMsb[i], BankLsb[i]);
        DrumKitProgram[1] = DrumKitProgram[2] = -1;
        fileVoiceMap = VoiceMapNative;
        fileGsDefaultMap = InstrumentDefinitions.DefaultGsMap;
        Mt32Patches.ResetFile();
        ModuleDisplay.Clear();
        ModuleSettings.ResetFiles();   // Effects tab: effects, Part and Drums pages
        nrpn.Reset();
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
        ModuleSettings.Apply(device);   // Effects tab: effects, Part and Drums pages, in the mode of the track
    }

    public static int FileValue(int k, int channel) { return FileCc[k, channel]; }
    public static bool IsFixed(int k, int channel) { return CcOverride[k, channel]; }
    public static int FixedValue(int k, int channel) { return OverrideCc[k, channel]; }

    // Value shown on slider: one fixed in interface (while it replaces the file's), otherwise file's.
    public static int Value(int k, int channel)
    {
        return IsActive(k, channel) ? OverrideCc[k, channel] : FileCc[k, channel];
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

    // ---------- XG voice map (MU100 and later) ----------
    // The 128 capital voices (bank MSB 0, LSB 0) exist in two versions: MU Basic (0, the voices of the MU50) and
    // MU100 Native (1, the default of the module). SysEx F0 43 1n 49 00 00 12 vv F7 chooses the one bank LSB 0
    // plays; bank LSB 127 / 126 ask for one of them whatever this setting.
    public const int VoiceMapBasic = 0, VoiceMapNative = 1;
    public static bool XgMode;                     // XG track: the voice map chosen in the interface is sent
    public static bool GsMode;                     // GS track (see the GM On of the file, in Callback)
    public static int VoiceMapOverride = -1;       // chosen in the interface (-1 = Auto: the file's)
    static int fileVoiceMap = VoiceMapNative;      // last one asked by the file (MU100 Native after a reset)

    // Voice map in effect: the one chosen in the interface, otherwise the file's.
    public static int VoiceMap => VoiceMapOverride >= 0 ? VoiceMapOverride : fileVoiceMap;

    static NormalSysExEvent VoiceMapEvent(int map) => new(new byte[] { 0x43, 0x10, 0x49, 0x00, 0x00, 0x12, (byte)map, 0xF7 });

    internal static bool IsVoiceMapSysEx(byte[] d) =>
        d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x49 && d[3] == 0 && d[4] == 0 && d[5] == 0x12;

    // Chooses the voice map (-1 = Auto: back to the file's) and sends it to the module in XG mode.
    public static void SetVoiceMap(IOutputDevice device, int map)
    {
        VoiceMapOverride = map;
        if (device != null && XgMode) device.SendEvent(VoiceMapEvent(VoiceMap));
    }

    // The voice map chosen in the interface (start of track, seek, port change, after a reset in file).
    public static void ApplyVoiceMap(IOutputDevice device)
    {
        if (XgMode && VoiceMapOverride >= 0) device.SendEvent(VoiceMapEvent(VoiceMapOverride));
    }

    // ---------- XG Multi Part SysEx ----------
    // F0 43 1n 4C 08 pp aa vv … F7 sets a part like the bank selects, the Program Change and the controllers of its
    // channel do (some files use nothing else): 01 / 02 / 03 = bank MSB / LSB / program, 0B volume, 0E pan,
    // 12 / 13 / 14 chorus / reverb / variation send, 15-17 vibrato, 18 / 19 filter, 1A-1C envelope. They are read
    // as such, and corrected with the values fixed in the interface. Part pp is taken as channel pp (its default
    // receive channel); the parts above 16 and the other addresses are left as they are.
    public static readonly bool[] SysExPatch = new bool[16];   // instrument of the channel set by such a SysEx

    // Channel of an XG Multi Part SysEx, or -1 if it is not one (or a part above 16).
    public static int MultiPartChannel(byte[] d) =>
        d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0x08 && d[4] < 16 ? d[4] : -1;

    // Mixer controller (index k) set by a Multi Part address, or -1.
    public static int MultiPartController(int address) => address switch
    {
        0x0B => 0, 0x0E => 1, 0x13 => 2, 0x12 => 3, 0x14 => 4, 0x1A => 5, 0x1B => 6, 0x1C => 7, 0x18 => 8, 0x19 => 9,
        0x15 => 10, 0x16 => 11, 0x17 => 12, _ => -1,
    };

    // Multi Part SysEx with its values replaced: instrument (bank MSB, LSB, program; null = the file's) and
    // controllers (controller(k) = value to write, -1 = the file's) and part mode (-1 = the file's). Returns null if
    // nothing changes.
    public static byte[] ReplaceMultiPart(byte[] d, (int Msb, int Lsb, int Program)? instrument, Func<int, int> controller, int partMode = -1)
    {
        byte[] data = null;
        for (int pos = 6; pos < d.Length - 1; pos++)
        {
            int address = d[5] + pos - 6, k = MultiPartController(address);
            int value = address == 0x01 && instrument != null ? instrument.Value.Msb
                      : address == 0x02 && instrument != null ? instrument.Value.Lsb
                      : address == 0x03 && instrument != null ? instrument.Value.Program
                      : address == 0x07 ? partMode
                      : k >= 0 ? controller(k) : -1;
            if (value < 0 || value == d[pos]) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)value;
        }
        return data;
    }

    // Multi Part SysEx sent by the file: stores its values; returns the message to play (null = unchanged).
    static MidiEvent ReadMultiPart(byte[] d, long rawTime)
    {
        int channel = MultiPartChannel(d);
        if (channel < 0) return null;
        for (int pos = 6; pos < d.Length - 1; pos++)
        {
            int address = d[5] + pos - 6, k = MultiPartController(address);
            if (k >= 0) { FileCc[k, channel] = d[pos]; ReadPassage(k, channel, rawTime); }
            else if (address == 0x01) BankMsb[channel] = d[pos];
            else if (address == 0x02) BankLsb[channel] = d[pos];
            else if (address == 0x03) Program[channel] = d[pos];
            else if (address == 0x07) XgDrumSetup.ReadPartMode(channel, d[pos]);
            if (address is >= 0x01 and <= 0x03) Used[channel] = SysExPatch[channel] = true;
        }
        var data = ReplaceMultiPart(d,
            Override[channel] ? (OverrideMsb[channel], OverrideLsb[channel], OverrideProgram[channel]) : null,
            k => Sent(k, channel), XgDrumSetup.PartModeOverride[channel]);
        return data != null ? new NormalSysExEvent(data) : null;
    }

    // ---------- GS default map (SC-88 and later) ----------
    // A part whose bank LSB (CC32) is 0 plays the map set by TONE MAP-0 NUMBER, DT1 40 4x 01 mm (part block x as
    // for 40 1x; mm: 1 = SC-55, 2 = SC-88, 3 = SC-88Pro, 4 = SC-8850). After a GS Reset it is the native map of
    // the module (the SC-8850's here: InstrumentDefinitions.DefaultGsMap). The one chosen in the interface is sent
    // to the 16 parts and replaces the file's.
    public static int GsDefaultMapOverride = -1;                          // chosen in the interface (-1 = Auto)
    static int fileGsDefaultMap = InstrumentDefinitions.DefaultGsMap;     // last one set by the file

    // Map of bank LSB 0 in effect: the one chosen in the interface, otherwise the file's.
    public static int GsDefaultMap => GsDefaultMapOverride >= 1 ? GsDefaultMapOverride : fileGsDefaultMap;

    static NormalSysExEvent GsDefaultMapEvent(int channel, int map)
    {
        int block = channel == 9 ? 0 : channel < 9 ? channel + 1 : channel;
        var data = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)(0x40 | block), 0x01, (byte)map, 0, 0xF7 };
        MidiTools.FixRolandChecksum(data);
        return new NormalSysExEvent(data);
    }

    internal static bool IsGsDefaultMapSysEx(byte[] d) =>
        d.Length >= 10 && d[0] == 0x41 && d[2] == 0x42 && d[3] == 0x12 && d[4] == 0x40 && (d[5] & 0xF0) == 0x40 && d[6] == 0x01;

    static void SendGsDefaultMap(IOutputDevice device, int map)
    {
        for (int channel = 0; channel < 16; channel++) device.SendEvent(GsDefaultMapEvent(channel, map));
    }

    // Chooses the default map (-1 = Auto: back to the file's) and sends it to the module in GS mode.
    public static void SetGsDefaultMap(IOutputDevice device, int map)
    {
        GsDefaultMapOverride = map;
        if (device != null && GsMode) SendGsDefaultMap(device, GsDefaultMap);
    }

    // The default map chosen in the interface (start of track, seek, port change, after a reset in file).
    public static void ApplyGsDefaultMap(IOutputDevice device)
    {
        if (GsMode && GsDefaultMapOverride >= 1) SendGsDefaultMap(device, GsDefaultMapOverride);
    }

    // Fixes a value. Set past the first passage of the controller, it is for the whole track (otherwise it would not be
    // heard until a jump back).
    public static void SetController(IOutputDevice device, int k, int channel, int value)
    {
        OverrideCc[k, channel] = value;
        if (!ccFirst[k, channel]) CcWholeTrack[k, channel] = true;
        CcOverride[k, channel] = true;
        if (device != null) ApplyController(device, k, channel);
    }

    // A fixed value for the whole track, or only in the first passage of the controller (see ControllerPassages).
    public static void SetWholeTrack(IOutputDevice device, int k, int channel, bool wholeTrack)
    {
        CcWholeTrack[k, channel] = wholeTrack;
        if (device != null) ApplyController(device, k, channel);
    }

    // Hands value back to file (its last value for this controller).
    public static void ClearController(IOutputDevice device, int k, int channel)
    {
        CcOverride[k, channel] = false;
        CcWholeTrack[k, channel] = false;
        if (device != null) ApplyController(device, k, channel);
    }

    public static void ApplyController(IOutputDevice device, int k, int channel)
    {
        device.SendEvent(Cc(channel, ControllerNumbers[k], Sent(k, channel)));
        if (k == PortamentoTime) device.SendEvent(Cc(channel, PortamentoSwitch, PortamentoSwitchValue(channel)));
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
                case 5:
                case 7:
                case 11:
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
                    ReadPassage(k, channel, rawTime);
                    return Cc(channel, cc.ControlNumber, Sent(k, channel));
                // Bank select: played as it is; the one of an instrument chosen for the passage goes with its Program
                // Change (it may start another passage).
                case 0:
                    BankMsb[channel] = sentMsb[channel] = cc.ControlValue;
                    break;
                case 32:
                    BankLsb[channel] = sentLsb[channel] = cc.ControlValue;
                    break;
                case PortamentoSwitch:
                    filePortamento[channel] = cc.ControlValue;
                    return Cc(channel, PortamentoSwitch, PortamentoSwitchValue(channel));
                case 99:
                case 98:
                case 101:
                case 100:
                case 6:
                case 38:
                    // XG drum setup and part NRPN, GS drum map NRPN, RPN (pitch bend range, tuning, GM2 modulation
                    // depth range): stored, and replaced by the values fixed in the Effects tab.
                    if (!(XgMode || GsMode || Gm2Mode) || nrpn.Read(cc) is not { } entry) break;
                    if (GsMode) return GsDrumMap.ReadNrpn(cc, entry) ?? GsPartParams.ReadDataEntry(cc, entry) ?? e;
                    MidiEvent again;
                    if (Gm2Mode)
                    {
                        var rpn = Gm2PartParams.ReadDataEntry(cc, entry, out again);
                        if (again != null) SendLater(again);
                        return rpn ?? e;
                    }
                    var entered = XgDrumSetup.ReadNrpn(cc, entry, out again);
                    if (entered == null && again == null) entered = XgPartParams.ReadDataEntry(cc, entry, out again);
                    if (again != null) SendLater(again);
                    return entered ?? e;
                case 64:
                    Sustain[channel] = cc.ControlValue;
                    break;
                case 66:
                    Sostenuto[channel] = cc.ControlValue;
                    break;
                case 121:   // Reset All Controllers: pedals released
                    Sustain[channel] = Sostenuto[channel] = 0;
                    break;
                case 126:
                case 127:
                    // Mono / poly mode: stored, and replaced by the one fixed in the Effects tab (XG, GS).
                    if (GsMode) return GsPartParams.ReadMonoPoly(cc) ?? e;
                    if (!XgMode) break;
                    return XgPartParams.ReadMonoPoly(cc) ?? e;
            }
            return e;
        }
        var pc = e as ProgramChangeEvent;
        if (pc != null)
        {
            int channel = pc.Channel;
            EnterPassage(channel, Passages?.At(channel, rawTime) ?? InstrumentPassages.First);
            Program[channel] = pc.ProgramNumber;
            if (Mt32Mode) Mt32Patches.ProgramChanged(channel);
            if (Mt32Mode && Mt32PartParams.ProgramChanged(channel) is int mt32Part and >= 0) SendLater(Mt32PartParams.FixedPatchEvents(mt32Part).ToArray());
            Used[channel] = true;
            if (XgMode) SendDrumSetupLater(XgDrumSetup.ProgramChanged(channel));
            if (GsMode) SendGsDrumMapLater(GsDrumMap.ProgramChanged(channel));
            // GM2: the Key-Based Instrument Control values of a new kit are written again.
            if (Gm2Mode && Gm2DrumParams.IsChannelFixed(channel)) SendLater(Gm2DrumParams.FixedEvents(channel).ToArray());
            // The bank of the instrument chosen for the passage first; without one, the file's bank (the module may still
            // have the one chosen for the previous passage).
            if (Override[channel])
            {
                SendBank(channel, OverrideMsb[channel], OverrideLsb[channel]);
                NoteSentProgram(channel, OverrideMsb[channel], OverrideLsb[channel], OverrideProgram[channel]);
                return Pc(channel, OverrideProgram[channel]);
            }
            if (sentMsb[channel] != BankMsb[channel] || sentLsb[channel] != BankLsb[channel]) SendBank(channel, BankMsb[channel], BankLsb[channel]);
            NoteSentProgram(channel, BankMsb[channel], BankLsb[channel], Program[channel]);
            return e;
        }
        var sysEx = e as SysExEvent;
        if (sysEx != null)
        {
            var d = sysEx.Data;
            ModuleDisplay.Read(d);   // text for the screen of the module, shown in the player
            // file's master volume: scaled by interface slider.
            if (d.Length >= 6 && d[0] == 0x7F && d[2] == 0x04 && d[3] == 0x01)
            {
                fileMasterVolume = d[5];
                return MasterVolumeEvent();
            }
            // Forced mode: SysEx of other modes ignored (manufacturer 41 Roland / 43 Yamaha).
            if (ForceMode && MidiFileEditor.IsOtherModeSysEx(d, DropManufacturers)) return null;
            // XG voice map asked by the file: remembered; replaced if one is chosen in the interface.
            if (IsVoiceMapSysEx(d))
            {
                fileVoiceMap = d[6] == VoiceMapBasic ? VoiceMapBasic : VoiceMapNative;
                return VoiceMapOverride >= 0 ? VoiceMapEvent(VoiceMapOverride) : e;
            }
            // GS default map set by the file (one part per message; the last one counts for all): remembered;
            // replaced if one is chosen in the interface.
            if (IsGsDefaultMapSysEx(d))
            {
                if (d[7] is >= 1 and <= 4) fileGsDefaultMap = d[7];
                if (GsDefaultMapOverride < 1) return e;
                var data = (byte[])d.Clone();
                data[7] = (byte)GsDefaultMapOverride;
                MidiTools.FixRolandChecksum(data);
                return new NormalSysExEvent(data);
            }
            if (MidiFileEditor.IsReset(e))
            {
                // Mode forced in interface: file's resets (GS / XG / GM / GM2) are ignored.
                if (ForceMode) return null;
                bool xg = d.Length >= 3 && d[0] == 0x43 && d[2] == 0x4C;
                // GM On in a GS / XG track (a file with GS / XG SysEx but no reset of that mode): it would leave
                // the module in GM mode, where bank selects are ignored (variations, maps, the instruments chosen
                // in the Mixer). The reset of the mode of the track is played instead.
                bool gmOn = d.Length >= 4 && d[0] == 0x7E && d[2] == 0x09 && d[3] == 0x01;
                MidiEvent played = e;
                if (gmOn && (GsMode || XgMode))
                {
                    played = MidiTools.GetModeEvents(XgMode ? "XG" : "GS")[0];
                    xg = XgMode;
                }
                ResetChannelState(xg);
                ResendAfterReset();
                return played;
            }
            if (Mt32Mode)
            {
                Mt32Patches.Read(d);
                // Part and rhythm setup values (Effects tab, Part and Drums pages): corrected first (the reverb is read below).
                if (Mt32PartParams.Read(d) is { } part) { d = part; e = new NormalSysExEvent(part); }
                if (Mt32DrumParams.Read(d) is { } setup) { d = setup; e = new NormalSysExEvent(setup); }
                var mt32 = Mt32Effects.Read(d);
                if (mt32 != null) return mt32;
            }
            if (Gm2Mode)
            {
                if (Gm2DrumParams.Read(d) is { } drum) return drum;
                var gm2 = Gm2Effects.Read(d);
                if (gm2 != null) return gm2;
            }
            if (GsMode)
            {
                if (GsDrumMap.Read(d) is { } gsDrum) return gsDrum;
                // Part values (Effects tab, Part page): corrected first (the Mixer's and the drum part are read below).
                if (GsPartParams.Read(d) is { } part) { d = part; e = new NormalSysExEvent(part); }
            }
            if (XgMode)
            {
                var drum = XgDrumSetup.Read(d, out int drumReset);
                SendDrumSetupLater(drumReset);
                if (drum != null) return drum;
                // Part values of a Multi Part SysEx (Effects tab, Part page): corrected first (the Mixer's are read below).
                var eq = XgPartParams.Read(d);
                if (eq != null) { d = eq; e = new NormalSysExEvent(eq); }
            }
            var multiPart = ReadMultiPart(d, rawTime);
            if (multiPart != null) return multiPart;
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
        if (CollectNotes && PlayedNotes.Count < 1024) PlayedNotes.Enqueue((note.Channel, note.NoteNumber));
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
        if (Mt32Mode) Mt32Patches.ProgramChanged(channel);   // the part plays its program again
        if (device == null) return;
        device.SendEvent(Cc(channel, 0, msb));
        device.SendEvent(Cc(channel, 32, lsb));
        device.SendEvent(Pc(channel, program));
        (sentMsb[channel], sentLsb[channel]) = (msb, lsb);
        // XG: the Program Change has reset the drum setup of the part; its fixed values are written again.
        if (XgMode && XgDrumSetup.ProgramChanged(channel) is int setup and >= 0) XgDrumSetup.Apply(device, setup);
        if (GsMode && GsDrumMap.ProgramChanged(channel) is int map and >= 0) foreach (var e in GsDrumMap.FixedEvents(map)) device.SendEvent(e);
        if (Gm2Mode && Gm2DrumParams.IsChannelFixed(channel)) foreach (var e in Gm2DrumParams.FixedEvents(channel)) device.SendEvent(e);
        if (Mt32Mode && Mt32PartParams.ProgramChanged(channel) is int part and >= 0) foreach (var e in Mt32PartParams.FixedPatchEvents(part)) device.SendEvent(e);
    }

    // Forces an instrument on a channel for the passage being played and sends it to module immediately.
    public static void SetOverride(IOutputDevice device, int channel, int msb, int lsb, int program)
    {
        lock (instruments) instruments[(channel, Passage[channel])] = (msb, lsb, program);
        OverrideMsb[channel] = msb;
        OverrideLsb[channel] = lsb;
        OverrideProgram[channel] = program;
        Override[channel] = true;
        Used[channel] = true;
        SendInstrument(device, channel, msb, lsb, program);
    }

    // Hands control back to file for the passage being played (allPassages: for the whole track): resends last
    // instrument requested by file.
    public static void ClearOverride(IOutputDevice device, int channel, bool allPassages = false)
    {
        lock (instruments)
        {
            if (allPassages) foreach (var key in instruments.Keys.Where(k => k.Channel == channel).ToList()) instruments.Remove(key);
            else instruments.Remove((channel, Passage[channel]));
        }
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
