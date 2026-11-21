using System.Diagnostics;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using static PowerMIDI.Core.FluidSynthNative;

namespace PowerMIDI.Core;

// The SoundFont engine: a FluidSynth synthesizer that plays a .sf2 file on the computer's audio output. It is a MIDI
// port like the others (its name in the port list: PortName), so playback, the Mixer and the effects send it the
// same events as to a module.
// It is created when its port is chosen. Loading the .sf2 then takes a moment (a few seconds for a large one), so the
// caller creates it outside the interface's thread. It is disposed when another port is chosen, which frees its memory
// (the samples).
//
// Timing: FluidSynth's audio output computes the sound in bursts (WASAPI shared mode: all its free buffer, ~40 ms at
// once). An event given straight to the synthesizer would start at the beginning of the next burst, so notes would
// be grouped by bursts (an uneven tempo). Each event is therefore sent through FluidSynth's sequencer, dated: the
// synthesizer plays it at that time of its rendering (to 64 samples). The date is the time the event was sent plus the
// lead of the rendering over the clock (the highest seen recently), plus a small margin: a constant delay instead of
// an uneven one.
public sealed class SoundFontOutput : IOutputDevice
{
    public const string PortName = "SoundFont engine (FluidSynth)";

    // The engine is offered only when its library is next to the program.
    public static bool IsAvailable => File.Exists(LibraryPath);

    // Gain at full master volume (SoundFontEngineOptions.Gain), and the master volume (0-1, universal SysEx).
    volatile float fullGain;
    volatile float masterVolume = 1;

    const double Margin = 4;   // ms added to the lead: the sequencer's clock counts whole milliseconds

    readonly object gate = new();   // the events (playback thread, interface) and Dispose
    IntPtr settings, synth, sequencer, sequencerEvent, driver;
    short synthClient, ownClient;   // sequencer clients: the synthesizer, and this engine (dated SysEx)
    readonly SequencerCallback callback;   // kept alive: the library calls it
    readonly Stopwatch clock = new();
    Timer leadTimer;

    public string SoundFontPath { get; }

    // Library version, e.g. "2.6.2".
    public static string Version
    {
        get { Prepare(); return Text(fluid_version_str()); }
    }

    // Audio driver actually used (the one chosen, or WASAPI / DirectSound when it could not start), e.g. "wasapi".
    public string DriverUsed { get; private set; }

    // Zones of the SoundFont whose generators were put in the order of the specification when it was read (see
    // SoundFontRepair; 0: none needed).
    public int ZonesReordered => PatchedLoader.ZonesReordered(SoundFontPath);

    public SoundFontOutput(string soundFontPath, SoundFontEngineOptions options)
    {
        SoundFontPath = soundFontPath;
        callback = OnSequencerEvent;
        Prepare();
        SetLogging();
        try
        {
            lastError = null;
            settings = new_fluid_settings();
            if (settings == IntPtr.Zero) throw Failure("cannot create its settings");
            // Settings taken when the synthesizer and the audio output are created.
            fluid_settings_setstr(settings, "synth.reverb.engine", options.ReverbEngine);
            fluid_settings_setint(settings, "synth.polyphony", options.Polyphony);
            fluid_settings_setint(settings, "synth.dynamic-sample-loading", options.LoadOnDemand ? 1 : 0);
            fluid_settings_setnum(settings, "synth.sample-rate", options.SampleRate);
            fluid_settings_setint(settings, "audio.periods", options.Buffer);
            // Channels: 16 per MIDI port, for the files on several ports (see MidiPorts, OtherPortsPlayer).
            fluid_settings_setint(settings, "synth.midi-channels", Channels);
            synth = new_fluid_synth(settings);
            if (synth == IntPtr.Zero) throw Failure("cannot create synthesizer");
            PatchedLoader.Install(settings, synth);   // SoundFonts with zones out of order read as BASSMIDI does
            AddSoundControllers(synth);
            if (fluid_synth_sfload(synth, soundFontPath, 1) < 0) throw Failure($"cannot load {Path.GetFileName(soundFontPath)}");
            SetDrumChannels(synth);
            fluid_synth_set_interp_method(synth, -1, options.Interpolation);
            fullGain = (float)options.Gain;
            fluid_synth_set_gain(synth, fullGain);

            // Sequencer clocked by the rendering (no system timer), with the synthesizer and this engine as clients.
            sequencer = new_fluid_sequencer2(0);
            if (sequencer == IntPtr.Zero) throw Failure("cannot create sequencer");
            synthClient = fluid_sequencer_register_fluidsynth(sequencer, synth);
            ownClient = fluid_sequencer_register_client(sequencer, "PowerMIDI", callback, IntPtr.Zero);
            if (synthClient < 0 || ownClient < 0) throw Failure("cannot connect sequencer");
            sequencerEvent = new_fluid_event();
            fluid_event_set_source(sequencerEvent, -1);

            // Audio output: the driver and device chosen; if it cannot start (device unplugged…), WASAPI then DirectSound
            // on the default device.
            foreach (var (name, device) in new[] { (options.Driver, options.Device), ("wasapi", "default"), ("dsound", "default") }.Distinct())
            {
                fluid_settings_setstr(settings, "audio.driver", name);
                fluid_settings_setstr(settings, $"audio.{name}.device", device);
                driver = new_fluid_audio_driver(settings, synth);
                if (driver == IntPtr.Zero) continue;
                DriverUsed = name;
                break;
            }
            if (driver == IntPtr.Zero) throw Failure("no audio output");
            lead = SoundFontEngineOptions.BufferMs(DriverUsed, options.Buffer, options.SampleRate) + StartMargin;
            clock.Start();
            // The lead of the rendering is also measured between events (a few events only would not see its highest).
            leadTimer = new Timer(_ => { lock (gate) { if (sequencer != IntPtr.Zero) MeasureLead(); } }, null, 0, 5);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Name => PortName;

    public event EventHandler<MidiEventSentEventArgs> EventSent { add { } remove { } }

    public void PrepareForEventsSending() { }

    // ---------- Events, in their order ----------
    // Within one tick (millisecond), FluidSynth's sequencer sorts its events by type, whatever the order they were sent
    // in (measured): a program change came before the bank select (CC0) sent just before it, so the program was taken
    // from the previous bank (a manual choice of another bank, or a file's bank and program at the same moment, played
    // a wrong instrument); a SysEx (GS Reset) came after the program changes sent after it, and cancelled them.
    // The events are therefore not given to the sequencer one by one: each tick has one timer event to this engine,
    // with the list of the events of that tick, played in the order they were sent (OnSequencerEvent), at that time of
    // the rendering, as the sequencer's own events.
    enum Kind : byte { NoteOn, NoteOff, Control, Program, PitchBend, ChannelPressure, KeyPressure, SysEx }

    readonly record struct Pending(Kind Kind, byte Channel, byte Data1, int Data2, byte[] SysEx = null);

    // Channels of the synthesizer: 4 ports of 16 (port p: channels 16 p + 1 to 16 p + 16).
    public const int Channels = 64;

    // The 10th channel of each port is a drum channel, as on a module with several ports (SC-88: part 10 of port B).
    // FluidSynth makes only channel 10 one (measured); the others are set after its creation and after each reset: drum
    // type, bank 128 (the type alone keeps bank 0: a channel keeps its bank), the first kit.
    static void SetDrumChannels(IntPtr synth)
    {
        for (int channel = 16 + 9; channel < Channels; channel += 16)
        {
            fluid_synth_set_channel_type(synth, channel, 1);
            fluid_synth_bank_select(synth, channel, InstrumentDefinitions.Sf2DrumBank);
            fluid_synth_program_change(synth, channel, 0);
        }
    }

    public void SendEvent(MidiEvent midiEvent) => SendEvent(midiEvent, 0);

    // An event of a MIDI port: its channel moved by 16 per port (port 1: channels 17-32); a SysEx keeps its port (see
    // PlaySysEx).
    public void SendEvent(MidiEvent midiEvent, int port)
    {
        if (port < 0 || port >= Channels / 16) return;
        int offset = port * 16;
        Pending item;
        switch (midiEvent)
        {
            // A Note On of velocity 0 is a Note Off.
            case NoteOnEvent on when on.Velocity == 0: item = new(Kind.NoteOff, on.Channel, on.NoteNumber, 0); break;
            case NoteOnEvent on: item = new(Kind.NoteOn, on.Channel, on.NoteNumber, on.Velocity); break;
            case NoteOffEvent off: item = new(Kind.NoteOff, off.Channel, off.NoteNumber, 0); break;
            case ControlChangeEvent cc: item = new(Kind.Control, cc.Channel, cc.ControlNumber, cc.ControlValue); break;
            case ProgramChangeEvent pc: item = new(Kind.Program, pc.Channel, pc.ProgramNumber, 0); break;
            case PitchBendEvent pb: item = new(Kind.PitchBend, pb.Channel, 0, pb.PitchValue); break;
            case ChannelAftertouchEvent at: item = new(Kind.ChannelPressure, at.Channel, 0, at.AftertouchValue); break;
            case NoteAftertouchEvent nat: item = new(Kind.KeyPressure, nat.Channel, nat.NoteNumber, nat.AftertouchValue); break;
            case NormalSysExEvent sysEx when sysEx.Data is { Length: > 0 } data: item = new(Kind.SysEx, (byte)port, 0, 0, data); break;
            default: return;
        }
        if (offset > 0 && item.Kind != Kind.SysEx) item = item with { Channel = (byte)(item.Channel + offset) };
        lock (gate)
        {
            if (sequencer == IntPtr.Zero) return;
            // A mode reset: counted when sent (see Resets), before anything sent after it.
            if (item.Kind == Kind.SysEx && IsModeReset(item.SysEx, item.SysEx[^1] == 0xF7 ? item.SysEx.Length - 1 : item.SysEx.Length))
                Interlocked.Increment(ref resets);
            uint at = Date();
            long id;
            lock (pending)
            {
                // The tick of the previous event, not played yet: this event joins its list.
                if (at == batchDate && pending.TryGetValue(batchId, out var batch)) { batch.Add(item); return; }
                id = batchId = ++lastBatch;
                batchDate = at;
                pending[id] = new List<Pending> { item };
            }
            fluid_event_timer(sequencerEvent, (IntPtr)id);
            fluid_event_set_dest(sequencerEvent, ownClient);
            fluid_sequencer_send_at(sequencer, sequencerEvent, at, 1);
        }
    }

    // ---------- Dates ----------
    // Lead of the rendering over the clock (ms): how far ahead the synthesizer has computed. It rises with each burst,
    // then falls until the next one (WASAPI shared mode: between about 25 and 80 ms). The estimate follows its highest:
    // it rises at once when the lead goes above it, and falls slowly (1 ms per second: the clocks of the computer and
    // of the sound card drift apart a little), so that the dates keep the spacing of the events. It starts at the
    // length of the audio buffer plus 10 ms, a little above the highest lead (which is about the buffer): the first
    // events of the engine are dated as evenly as the next ones, and a smaller buffer gives a shorter delay at once.
    // Measured with WASAPI: events played within 2 ms of their spacing, instead of up to 35 ms when given straight to
    // the synthesizer.
    const double StartMargin = 10, LeadFall = 0.001;   // ms, ms per ms
    double lead, lastMeasure;

    void MeasureLead()
    {
        double now = clock.Elapsed.TotalMilliseconds;
        double measured = fluid_sequencer_get_tick(sequencer) - now;
        lead = Math.Max(measured, lead - (now - lastMeasure) * LeadFall);
        lastMeasure = now;
    }

    // Date of an event sent now: never before one sent earlier (the order of the events is kept).
    uint lastDate;

    uint Date()
    {
        MeasureLead();
        double date = clock.Elapsed.TotalMilliseconds + lead + Margin;
        uint at = date <= 0 ? 0 : (uint)Math.Ceiling(date);
        if (at < lastDate) at = lastDate;
        lastDate = at;
        return at;
    }

    // Events not played yet: the list of each tick (its timer event's number), in the order they were sent.
    readonly Dictionary<long, List<Pending>> pending = new();
    long lastBatch, batchId;
    uint batchDate;

    // Called by the sequencer on the audio thread, at the time of a tick: its events given to the synthesizer, in order.
    // Never takes the gate (SendEvent may hold it while waiting for the sequencer), never lets an error out.
    void OnSequencerEvent(uint time, IntPtr evt, IntPtr seq, IntPtr data)
    {
        try
        {
            if (fluid_event_get_type(evt) != SeqTimer) return;
            List<Pending> batch;
            lock (pending) { if (!pending.Remove((long)fluid_event_get_data(evt), out batch)) return; }
            var s = synth;
            if (s == IntPtr.Zero) return;
            foreach (var e in batch)
                switch (e.Kind)
                {
                    case Kind.NoteOn: fluid_synth_noteon(s, e.Channel, e.Data1, e.Data2); break;
                    case Kind.NoteOff: fluid_synth_noteoff(s, e.Channel, e.Data1); break;
                    case Kind.Control:
                        if (e.Data1 == 0) PlayBank(s, e.Channel, e.Data2);
                        fluid_synth_cc(s, e.Channel, e.Data1, e.Data2);
                        break;
                    case Kind.Program: fluid_synth_program_change(s, e.Channel, e.Data1); break;
                    case Kind.PitchBend: fluid_synth_pitch_bend(s, e.Channel, e.Data2); break;
                    case Kind.ChannelPressure: fluid_synth_channel_pressure(s, e.Channel, e.Data2); break;
                    case Kind.KeyPressure: fluid_synth_key_pressure(s, e.Channel, e.Data1, e.Data2); break;
                    case Kind.SysEx: PlaySysEx(e.SysEx, e.Channel); break;
                }
        }
        catch { }
    }

    // A SysEx: given to FluidSynth (it handles GS Use for Rhythm Part and MIDI Tuning, and ignores the others).
    // - The universal Master Volume (7F dd 04 01 ll mm), which it ignores, sets its gain.
    // - A reset of a mode (GM / GM2 On, GS Reset, XG System On) resets the synthesizer, but is not given to it: it
    //   would switch its bank style to that mode's (XG: bank = bank LSB; GM: banks ignored), and the bank of the
    //   SoundFont presets chosen in the Mixer (bank MSB) would no longer be found. The style stays GS (bank = MSB, the
    //   style of most SoundFonts; a drum channel plays the kits of bank 128).
    // - A SysEx of a part of another port (port above 0, see MidiPorts.GsPart): FluidSynth would set the part of the first
    //   port (it knows only channels 1-16 in SysEx). Its Use for Rhythm Part is played here, as FluidSynth plays it on
    //   the first port; the others are ignored.
    // Number of mode resets sent so far (counted when sent: anything sent after one plays after it): the player of the
    // other MIDI ports (OtherPortsPlayer) sends the state and settings of their channels again after one (the reset sets
    // all the channels of the engine back).
    int resets;
    public int Resets => Volatile.Read(ref resets);

    void PlaySysEx(byte[] data, int port)
    {
        var s = synth;
        if (s == IntPtr.Zero) return;
        int length = data[^1] == 0xF7 ? data.Length - 1 : data.Length;   // without the final F7
        if (length == 6 && data[0] == 0x7F && data[2] == 0x04 && data[3] == 0x01)
        {
            masterVolume = ((data[5] << 7) | data[4]) / 16383f;
            fluid_synth_set_gain(s, fullGain * masterVolume);
            return;
        }
        if (IsModeReset(data, length))
        {
            fluid_synth_system_reset(s);
            SetDrumChannels(s);
            ApplyEffects(s);   // the reverb and chorus chosen stay
            return;
        }
        if (port > 0)
        {
            PlayRhythmPart(s, data, length, port);
            return;
        }
        if (length < 1) return;
        var message = length == data.Length ? data : data[..length];
        fluid_synth_sysex(s, message, message.Length, IntPtr.Zero, IntPtr.Zero, out _, 0);
    }

    // XG or GM2 track: a bank select makes the channel a drum or a normal channel by the rule of the mode
    // (InstrumentDefinitions.SoundFontBankDrum: XG MSB 127 drum kits / 126 SFX kits; GM2 MSB 120 drum kits / 121 sounds,
    // the 10th channel drums with another bank), before the bank select itself is played. FluidSynth does so in its XG
    // bank style only, which is not used (see PlaySysEx); in the GS style a drum channel ignores the bank select and
    // plays the kits of bank 128, the next Program Change choosing the kit. No SFX kit in a SoundFont: XG MSB 126 plays
    // its drum kits too. Other modes: nothing (the GS drum part decides).
    // Measured offline (FluidSynth 2.6.2, GeneralUser GS): channel 3 made a drum channel so plays the kit of channel 10
    // exactly, and back to a normal channel the piano exactly; the type alone (without bank 128) plays another sound.
    static void PlayBank(IntPtr s, int channel, int msb)
    {
        if (InstrumentDefinitions.SoundFontBankDrum(channel, msb) is not bool drums) return;
        fluid_synth_set_channel_type(s, channel, drums ? 1 : 0);
        if (drums) fluid_synth_bank_select(s, channel, InstrumentDefinitions.Sf2DrumBank);
    }

    // GS Use for Rhythm Part (41 dd 42 12 40 1x 15 vv sum) of a channel of another port, as FluidSynth plays it on the
    // first port (fluid_synth_sysex_gs_dt1, version 2.6): device 16 or all (7F), one value of 0-2, a right checksum; the
    // channel then normal (0) or drums, its controllers reset (CC121), bank 0 or the drum bank, program 1.
    static void PlayRhythmPart(IntPtr s, byte[] data, int length, int port)
    {
        if (length != 9 || data[1] is not (0x10 or 0x7F) || data[7] > 2) return;
        int channel = ChannelMixer.RhythmPartChannel(data);
        int sum = 0;
        for (int i = 4; i < 8; i++) sum += data[i];
        if (channel < 0 || (128 - sum % 128) % 128 != data[8]) return;
        bool drums = data[7] > 0;
        channel += 16 * port;
        if (channel >= Channels) return;
        fluid_synth_set_channel_type(s, channel, drums ? 1 : 0);
        fluid_synth_cc(s, channel, 121, 0);
        fluid_synth_bank_select(s, channel, drums ? InstrumentDefinitions.Sf2DrumBank : 0);
        fluid_synth_program_change(s, channel, 0);
    }

    // ---------- Sound controllers (CC71-78) ----------
    // FluidSynth has no default modulators for the sound controllers of GS / XG / GM2 (measured: CC71-78 change
    // nothing). They are added, as on a module: 64 (their value after a reset) changes nothing, the value moves the
    // sound's own setting up or down (bipolar, linear), by at most:
    // - CC74 brightness: cutoff ± 4800 cents (4 octaves); CC71 resonance: Q ± 24 dB (only up has an effect);
    // - CC73 attack: ± 9600 timecents (× / ÷ 256); CC75 decay and CC72 release: ± 4800 timecents (× / ÷ 16);
    // - CC76 vibrato rate: ± 2400 cents (× / ÷ 4); CC78 vibrato delay: ± 9600 timecents;
    // - CC77 vibrato depth: up to 100 cents, above 64 only (a second source, the same CC as a switch, cancels it
    //   below: there is no vibrato to take away, and a negative depth would still be a vibrato).
    // Starting values, to be adjusted by ear.
    static readonly (int Cc, int Generator, double Amount, bool UpOnly)[] SoundControllers =
    {
        (74, GenFilterFc, 4800, false), (71, GenFilterQ, 240, false),
        (73, GenVolEnvAttack, 9600, false), (75, GenVolEnvDecay, 4800, false), (72, GenVolEnvRelease, 4800, false),
        (76, GenVibLfoFreq, 2400, false), (77, GenVibLfoToPitch, 100, true), (78, GenVibLfoDelay, 9600, false),
    };

    static void AddSoundControllers(IntPtr synth)
    {
        foreach (var (cc, generator, amount, upOnly) in SoundControllers)
        {
            var mod = new_fluid_mod();
            if (mod == IntPtr.Zero) continue;
            try
            {
                fluid_mod_set_source1(mod, cc, ModCc | ModBipolar);
                fluid_mod_set_source2(mod, upOnly ? cc : 0, upOnly ? ModCc | ModSwitch : 0);   // 0 with no flags: none
                fluid_mod_set_dest(mod, generator);
                fluid_mod_set_amount(mod, amount);
                fluid_synth_add_default_mod(synth, mod, SynthAddModOverwrite);   // the synthesizer keeps a copy
            }
            finally { delete_fluid_mod(mod); }
        }
    }

    // ---------- Settings taken at once (SoundFonts tab) ----------
    public void SetGain(double gain)
    {
        lock (gate)
        {
            fullGain = (float)gain;
            if (synth != IntPtr.Zero) fluid_synth_set_gain(synth, fullGain * masterVolume);
        }
    }

    public void SetInterpolation(int method)
    {
        lock (gate) { if (synth != IntPtr.Zero) fluid_synth_set_interp_method(synth, -1, method); }
    }

    public void SetPolyphony(int voices)
    {
        lock (gate) { if (synth != IntPtr.Zero) fluid_synth_set_polyphony(synth, voices); }
    }

    // Delay between an event sent and its sound (ms): the lead of the rendering, plus the margin (see Dates).
    public double Delay => lead + Margin;

    // Audio devices of a driver (e.g. "wasapi"), as FluidSynth names them, "default" first.
    public static IReadOnlyList<string> Devices(string driver)
    {
        Prepare();
        var names = new List<string>();
        var options = new_fluid_settings();
        if (options == IntPtr.Zero) return names;
        try
        {
            OptionCallback add = (data, name, option) => { if (Text(option) is { } text) names.Add(text); };
            fluid_settings_foreach_option(options, $"audio.{driver}.device", IntPtr.Zero, add);
            GC.KeepAlive(add);
        }
        finally { delete_fluid_settings(options); }
        return names.OrderBy(n => n != "default").ToList();
    }

    // ---------- Reverb and chorus (see SoundFontEffects) ----------
    // Applied at once (not dated: they are settings of the engine, not events of the music), and again after a reset
    // of the synthesizer. Kept as one object each, replaced whole: the audio thread (reset) reads them without the gate.
    sealed record ReverbValues(bool On, double RoomSize, double Damping, double Width, double Level);
    sealed record ChorusValues(bool On, int Voices, double Level, double Speed, double Depth, int Wave);
    volatile ReverbValues reverb;
    volatile ChorusValues chorus;

    public void SetReverb(bool on, double roomSize, double damping, double width, double level)
    {
        lock (gate)
        {
            reverb = new ReverbValues(on, roomSize, damping, width, level);
            if (synth != IntPtr.Zero) ApplyEffects(synth);
        }
    }

    public void SetChorus(bool on, int voices, double level, double speed, double depth, int wave)
    {
        lock (gate)
        {
            chorus = new ChorusValues(on, voices, level, speed, depth, wave);
            if (synth != IntPtr.Zero) ApplyEffects(synth);
        }
    }

    // Global sends (percent, 0-100): a send to the reverb / chorus added to every note of all the channels, whatever their
    // CC91 / CC93. A default modulator without source (SF2 2.01, 8.2.1: its value is 1) on the generator of the send
    // (tenths of a percent): FluidSynth adds its amount to the send of each note when it starts (the notes playing keep
    // theirs); a mode reset and CC121 leave it (unlike the generator offsets of a channel: fluid_synth_set_gen).
    // Measured offline (FluidSynth 2.6.2, GeneralUser GS piano, CC91 = CC93 = 0): what it adds grows with its amount (50 %:
    // 2.5 times 20 %), the same after a CC121 or a reset.
    public void SetGlobalSends(double reverb, double chorus)
    {
        lock (gate)
        {
            if (synth == IntPtr.Zero) return;
            AddConstantSend(synth, GenReverbSend, reverb * 10);
            AddConstantSend(synth, GenChorusSend, chorus * 10);
        }
    }

    static void AddConstantSend(IntPtr synth, int generator, double amount)
    {
        var mod = new_fluid_mod();
        if (mod == IntPtr.Zero) return;
        try
        {
            fluid_mod_set_source1(mod, 0, 0);   // no source: 1
            fluid_mod_set_source2(mod, 0, 0);
            fluid_mod_set_dest(mod, generator);
            fluid_mod_set_amount(mod, Math.Clamp(amount, 0, 1000));
            fluid_synth_add_default_mod(synth, mod, SynthAddModOverwrite);   // replaces the previous one (same sources and generator)
        }
        finally { delete_fluid_mod(mod); }
    }

    void ApplyEffects(IntPtr s)
    {
        if (reverb is { } r)
        {
            fluid_synth_set_reverb_group_roomsize(s, -1, r.RoomSize);
            fluid_synth_set_reverb_group_damp(s, -1, r.Damping);
            fluid_synth_set_reverb_group_width(s, -1, r.Width);
            fluid_synth_set_reverb_group_level(s, -1, r.Level);
            fluid_synth_reverb_on(s, -1, r.On ? 1 : 0);
        }
        if (chorus is { } c)
        {
            fluid_synth_set_chorus_group_nr(s, -1, c.Voices);
            fluid_synth_set_chorus_group_level(s, -1, c.Level);
            fluid_synth_set_chorus_group_speed(s, -1, c.Speed);
            fluid_synth_set_chorus_group_depth(s, -1, c.Depth);
            fluid_synth_set_chorus_group_type(s, -1, c.Wave);
            fluid_synth_chorus_on(s, -1, c.On ? 1 : 0);
        }
    }

    // GM System On / Off and GM2 System On (7E dd 09 01-03), GS Reset or System Mode Set, XG System On
    // (43 1n 4C 00 00 7E 00).
    public static bool IsModeReset(byte[] d, int length) =>
        (length >= 4 && d[0] == 0x7E && d[2] == 0x09 && d[3] is >= 0x01 and <= 0x03)
        || MidiTools.IsGsReset(d)
        || (length >= 7 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0 && d[4] == 0 && d[5] == 0x7E && d[6] == 0);

    // The timer and the audio output first (nothing calls the synthesizer any more), then the sequencer, the
    // synthesizer (its samples freed) and the settings.
    public void Dispose()
    {
        leadTimer?.Dispose();
        leadTimer = null;
        lock (gate)
        {
            if (driver != IntPtr.Zero) { delete_fluid_audio_driver(driver); driver = IntPtr.Zero; }
            if (sequencer != IntPtr.Zero) { delete_fluid_sequencer(sequencer); sequencer = IntPtr.Zero; }
            if (sequencerEvent != IntPtr.Zero) { delete_fluid_event(sequencerEvent); sequencerEvent = IntPtr.Zero; }
            if (synth != IntPtr.Zero) { delete_fluid_synth(synth); synth = IntPtr.Zero; }
            if (settings != IntPtr.Zero) { delete_fluid_settings(settings); settings = IntPtr.Zero; }
        }
        lock (pending) pending.Clear();
    }

    // ---------- Messages of the library ----------
    // Its errors are kept: the last one explains a failure (e.g. a file that is not a SoundFont). Its warnings (one per
    // preset missing from the SoundFont, for example) and information are not shown.
    static LogFunction logFunction;   // kept alive: the library calls it
    static volatile string lastError;

    static void SetLogging()
    {
        if (logFunction != null) return;
        logFunction = (level, message, data) => lastError = Text(message);
        fluid_set_log_function(LogPanic, logFunction, IntPtr.Zero);
        fluid_set_log_function(LogError, logFunction, IntPtr.Zero);
        foreach (var level in new[] { LogWarning, LogInfo, LogDebug })
            fluid_set_log_function(level, null, IntPtr.Zero);
    }

    static InvalidOperationException Failure(string what) =>
        new(lastError is { } error ? $"SoundFont engine: {what} ({error})." : $"SoundFont engine: {what}.");
}
