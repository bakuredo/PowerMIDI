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

    // Gain at full master volume. FluidSynth's default (0.2) is quiet next to a module; much more saturates on loud
    // passages.
    const float FullGain = 0.5f;

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

    public SoundFontOutput(string soundFontPath)
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
            synth = new_fluid_synth(settings);
            if (synth == IntPtr.Zero) throw Failure("cannot create synthesizer");
            if (fluid_synth_sfload(synth, soundFontPath, 1) < 0) throw Failure($"cannot load {Path.GetFileName(soundFontPath)}");
            fluid_synth_set_gain(synth, FullGain);

            // Sequencer clocked by the rendering (no system timer), with the synthesizer and this engine as clients.
            sequencer = new_fluid_sequencer2(0);
            if (sequencer == IntPtr.Zero) throw Failure("cannot create sequencer");
            synthClient = fluid_sequencer_register_fluidsynth(sequencer, synth);
            ownClient = fluid_sequencer_register_client(sequencer, "PowerMIDI", callback, IntPtr.Zero);
            if (synthClient < 0 || ownClient < 0) throw Failure("cannot connect sequencer");
            sequencerEvent = new_fluid_event();
            fluid_event_set_source(sequencerEvent, -1);

            // Audio output: WASAPI (Windows' own), otherwise DirectSound.
            foreach (var name in new[] { "wasapi", "dsound" })
            {
                fluid_settings_setstr(settings, "audio.driver", name);
                driver = new_fluid_audio_driver(settings, synth);
                if (driver != IntPtr.Zero) break;
            }
            if (driver == IntPtr.Zero) throw Failure("no audio output");
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

    public void SendEvent(MidiEvent midiEvent)
    {
        lock (gate)
        {
            if (sequencer == IntPtr.Zero) return;
            var e = sequencerEvent;
            short destination = synthClient;
            switch (midiEvent)
            {
                // A Note On of velocity 0 is a Note Off.
                case NoteOnEvent on when on.Velocity == 0: fluid_event_noteoff(e, on.Channel, on.NoteNumber); break;
                case NoteOnEvent on: fluid_event_noteon(e, on.Channel, on.NoteNumber, on.Velocity); break;
                case NoteOffEvent off: fluid_event_noteoff(e, off.Channel, off.NoteNumber); break;
                case ControlChangeEvent cc: fluid_event_control_change(e, cc.Channel, cc.ControlNumber, cc.ControlValue); break;
                case ProgramChangeEvent pc: fluid_event_program_change(e, pc.Channel, pc.ProgramNumber); break;
                case PitchBendEvent pb: fluid_event_pitch_bend(e, pb.Channel, pb.PitchValue); break;
                case ChannelAftertouchEvent at: fluid_event_channel_pressure(e, at.Channel, at.AftertouchValue); break;
                case NoteAftertouchEvent nat: fluid_event_key_pressure(e, nat.Channel, nat.NoteNumber, nat.AftertouchValue); break;
                // A SysEx (no such sequencer event): a timer event to this engine, which gives it to the synthesizer at
                // its time (OnSequencerEvent).
                case NormalSysExEvent sysEx when sysEx.Data is { Length: > 0 } data:
                    long id;
                    lock (pending) { id = ++lastSysEx; pending[id] = data; }
                    fluid_event_timer(e, (IntPtr)id);
                    destination = ownClient;
                    break;
                default: return;
            }
            fluid_event_set_dest(e, destination);
            fluid_sequencer_send_at(sequencer, e, Date(), 1);
        }
    }

    // ---------- Dates ----------
    // Lead of the rendering over the clock (ms): how far ahead the synthesizer has computed. It rises with each burst,
    // then falls until the next one (WASAPI shared mode: between about 25 and 80 ms). The estimate follows its highest:
    // it rises at once when the lead goes above it, and falls slowly (1 ms per second: the clocks of the computer and
    // of the sound card drift apart a little), so that the dates keep the spacing of the events. It starts high
    // (100 ms): the first events of the engine are dated as evenly as the next ones. Measured with WASAPI: events
    // played within 2 ms of their spacing, instead of up to 35 ms when given straight to the synthesizer.
    const double StartLead = 100, LeadFall = 0.001;   // ms, ms per ms
    double lead = StartLead, lastMeasure;
    uint lastDate;

    void MeasureLead()
    {
        double now = clock.Elapsed.TotalMilliseconds;
        double measured = fluid_sequencer_get_tick(sequencer) - now;
        lead = Math.Max(measured, lead - (now - lastMeasure) * LeadFall);
        lastMeasure = now;
    }

    // Date of an event sent now: never before one sent earlier (the order of the events is kept).
    uint Date()
    {
        MeasureLead();
        double date = clock.Elapsed.TotalMilliseconds + lead + Margin;
        uint at = date <= 0 ? 0 : (uint)Math.Ceiling(date);
        if (at < lastDate) at = lastDate;
        lastDate = at;
        return at;
    }

    // ---------- SysEx at their time ----------
    readonly Dictionary<long, byte[]> pending = new();   // dated SysEx not played yet, by number
    long lastSysEx;

    // Called by the sequencer on the audio thread: never takes the gate (SendEvent may hold it while waiting for the
    // sequencer), never lets an error out.
    void OnSequencerEvent(uint time, IntPtr evt, IntPtr seq, IntPtr data)
    {
        try
        {
            if (fluid_event_get_type(evt) != SeqTimer) return;
            byte[] sysEx;
            lock (pending) { if (!pending.Remove((long)fluid_event_get_data(evt), out sysEx)) return; }
            PlaySysEx(sysEx);
        }
        catch { }
    }

    // A SysEx: given to FluidSynth (it handles the resets, GS Use for Rhythm Part and MIDI Tuning, and ignores the
    // others). The universal Master Volume (7F dd 04 01 ll mm), which it ignores, sets its gain.
    void PlaySysEx(byte[] data)
    {
        var s = synth;
        if (s == IntPtr.Zero) return;
        int length = data[^1] == 0xF7 ? data.Length - 1 : data.Length;   // without the final F7
        if (length == 6 && data[0] == 0x7F && data[2] == 0x04 && data[3] == 0x01)
        {
            fluid_synth_set_gain(s, FullGain * ((data[5] << 7) | data[4]) / 16383f);
            return;
        }
        if (length < 1) return;
        var message = length == data.Length ? data : data[..length];
        fluid_synth_sysex(s, message, message.Length, IntPtr.Zero, IntPtr.Zero, out _, 0);
    }

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
