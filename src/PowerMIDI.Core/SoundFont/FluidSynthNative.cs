using System.Runtime.InteropServices;

namespace PowerMIDI.Core;

// The few functions of the FluidSynth C library (libfluidsynth-3.dll, version 2.x) used by the SoundFont engine. The
// library and the one it needs (sndfile.dll) are in the fluidsynth folder next to the program: they are loaded from
// there, only when the engine is first used (a program that never uses it never loads them).
static class FluidSynthNative
{
    const string Library = "libfluidsynth-3";
    public const int Ok = 0;   // FLUID_OK (FLUID_FAILED = -1)

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "fluidsynth");
    public static string LibraryPath => Path.Combine(Folder, Library + ".dll");

    // To call before the first function of the library: it is then looked for in the fluidsynth folder (loaded with
    // its full path, its own folder is searched for sndfile.dll). Once for the whole program.
    static bool resolverSet;
    static readonly object gate = new();

    public static void Prepare()
    {
        lock (gate)
        {
            if (resolverSet) return;
            NativeLibrary.SetDllImportResolver(typeof(FluidSynthNative).Assembly, (name, assembly, path) =>
                name == Library && File.Exists(LibraryPath) ? NativeLibrary.Load(LibraryPath) : IntPtr.Zero);
            resolverSet = true;
        }
    }

    // Log messages of the library: errors kept (the last one explains a failure), the others ignored (a warning per
    // missing preset, for example).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogFunction(int level, IntPtr message, IntPtr data);
    public const int LogPanic = 0, LogError = 1, LogWarning = 2, LogInfo = 3, LogDebug = 4;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fluid_set_log_function(int level, LogFunction fun, IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fluid_version_str();

    // ---------- Settings ----------
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr new_fluid_settings();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void delete_fluid_settings(IntPtr settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_settings_setstr(IntPtr settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_settings_setint(IntPtr settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_settings_setnum(IntPtr settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double value);

    // The options of a string setting (e.g. the audio devices of a driver: "audio.wasapi.device"), one call each.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OptionCallback(IntPtr data, IntPtr name, IntPtr option);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_settings_foreach_option(IntPtr settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                                            IntPtr data, OptionCallback callback);

    // ---------- Synthesizer ----------
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr new_fluid_synth(IntPtr settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void delete_fluid_synth(IntPtr synth);

    // The file name in UTF-8 (FluidSynth opens it as such on Windows).
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_sfload(IntPtr synth, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int resetPresets);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_synth_set_gain(IntPtr synth, float gain);

    // chan -1: all the channels. Methods: 0 none, 1 linear, 4 4th order, 5 / 6 sinc of 11th / 17th order.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_interp_method(IntPtr synth, int chan, int method);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_polyphony(IntPtr synth, int polyphony);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_noteon(IntPtr synth, int chan, int key, int vel);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_noteoff(IntPtr synth, int chan, int key);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_cc(IntPtr synth, int chan, int ctrl, int val);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_program_change(IntPtr synth, int chan, int program);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_pitch_bend(IntPtr synth, int chan, int val);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_channel_pressure(IntPtr synth, int chan, int val);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_key_pressure(IntPtr synth, int chan, int key, int val);

    // data: the message without F0 and F7. No response asked (response and its length null, dryrun 0).
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_sysex(IntPtr synth, byte[] data, int len, IntPtr response, IntPtr responseLen,
                                               out int handled, int dryrun);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_all_sounds_off(IntPtr synth, int chan);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_system_reset(IntPtr synth);

    // type: 0 melodic, 1 drum (enum fluid_midi_channel_type).
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_channel_type(IntPtr synth, int chan, int type);

    // The bank of a channel, whatever its bank style (a drum channel keeps its own: 128).
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_bank_select(IntPtr synth, int chan, int bank);

    // ---------- Modulators (a source controller drives a generator of the voices) ----------
    // Flags of a source: 2 bipolar (centered: -1..1), 12 switch (0 below the middle, 1 from it), 16 a MIDI CC (the
    // source is its number). Generators: SF2 numbers (fluid_gen_type).
    public const int ModBipolar = 2, ModSwitch = 12, ModCc = 16;
    public const int GenVibLfoToPitch = 6, GenFilterFc = 8, GenFilterQ = 9, GenChorusSend = 15, GenReverbSend = 16,
                     GenVibLfoDelay = 23, GenVibLfoFreq = 24, GenVolEnvAttack = 34, GenVolEnvDecay = 36, GenVolEnvRelease = 38;
    public const int SynthAddModOverwrite = 0;   // FLUID_SYNTH_OVERWRITE

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr new_fluid_mod();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void delete_fluid_mod(IntPtr mod);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_mod_set_source1(IntPtr mod, int src, int flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_mod_set_source2(IntPtr mod, int src, int flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_mod_set_dest(IntPtr mod, int dest);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_mod_set_amount(IntPtr mod, double amount);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_add_default_mod(IntPtr synth, IntPtr mod, int mode);

    // ---------- Reverb and chorus (fxGroup -1: all the effect groups; there is one) ----------
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_reverb_on(IntPtr synth, int fxGroup, int on);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_reverb_group_roomsize(IntPtr synth, int fxGroup, double roomSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_reverb_group_damp(IntPtr synth, int fxGroup, double damping);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_reverb_group_width(IntPtr synth, int fxGroup, double width);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_reverb_group_level(IntPtr synth, int fxGroup, double level);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_chorus_on(IntPtr synth, int fxGroup, int on);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_chorus_group_nr(IntPtr synth, int fxGroup, int voices);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_chorus_group_level(IntPtr synth, int fxGroup, double level);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_chorus_group_speed(IntPtr synth, int fxGroup, double speed);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_chorus_group_depth(IntPtr synth, int fxGroup, double depthMs);

    // 0 = sine, 1 = triangle (enum fluid_chorus_mod).
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_synth_set_chorus_group_type(IntPtr synth, int fxGroup, int type);

    // ---------- SoundFont loader with file callbacks (see PatchedLoader) ----------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr SfOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string filename);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SfRead(IntPtr buffer, long count, IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SfSeek(IntPtr handle, long offset, int origin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long SfTell(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SfClose(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr new_fluid_defsfloader(IntPtr settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_sfloader_set_callbacks(IntPtr loader, SfOpen open, SfRead read, SfSeek seek, SfTell tell, SfClose close);

    // The synthesizer owns the loader (freed with it); added before any SoundFont is loaded, it is tried first.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_synth_add_sfloader(IntPtr synth, IntPtr loader);

    // ---------- Sequencer ----------
    // Events given a time (ms) are played by the synthesizer at that time of its rendering, 64 samples (1.5 ms) at a
    // time, however its audio output computes the sound (in bursts). Without the system timer, its clock is the
    // rendering itself (samples computed so far).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SequencerCallback(uint time, IntPtr evt, IntPtr seq, IntPtr data);
    public const int SeqTimer = 17;   // FLUID_SEQ_TIMER

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr new_fluid_sequencer2(int useSystemTimer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void delete_fluid_sequencer(IntPtr seq);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern short fluid_sequencer_register_fluidsynth(IntPtr seq, IntPtr synth);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern short fluid_sequencer_register_client(IntPtr seq, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                                               SequencerCallback callback, IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_sequencer_send_at(IntPtr seq, IntPtr evt, uint time, int absolute);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint fluid_sequencer_get_tick(IntPtr seq);

    // ---------- Sequencer events (one reused: send_at copies it) ----------
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr new_fluid_event();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void delete_fluid_event(IntPtr evt);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_set_source(IntPtr evt, short src);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_set_dest(IntPtr evt, short dest);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_noteon(IntPtr evt, int channel, short key, short vel);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_noteoff(IntPtr evt, int channel, short key);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_control_change(IntPtr evt, int channel, short control, int val);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_program_change(IntPtr evt, int channel, int preset);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_pitch_bend(IntPtr evt, int channel, int val);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_channel_pressure(IntPtr evt, int channel, int val);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_key_pressure(IntPtr evt, int channel, short key, int val);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fluid_event_timer(IntPtr evt, IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fluid_event_get_type(IntPtr evt);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fluid_event_get_data(IntPtr evt);

    // ---------- Audio output ----------
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr new_fluid_audio_driver(IntPtr settings, IntPtr synth);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void delete_fluid_audio_driver(IntPtr driver);

    public static string Text(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
