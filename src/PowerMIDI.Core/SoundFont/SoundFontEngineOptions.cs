using System.Globalization;

namespace PowerMIDI.Core;

// Settings of the SoundFont engine, chosen in the SoundFonts tab and remembered in PowerMIDI.ini ([SoundFont]):
// - taken by FluidSynth when it is created (the engine restarts when one changes): reverb engine, sample loading,
//   audio output (driver, device, sample rate, buffer);
// - taken at once: interpolation, polyphony, gain.
// Defaults: FluidSynth's (the gain: PowerMIDI's, 0.5; FluidSynth's 0.2 is quiet next to a module), WASAPI output.
public sealed record SoundFontEngineOptions
{
    public string ReverbEngine { get; init; } = "dat";
    public int Interpolation { get; init; } = 4;
    public int Polyphony { get; init; } = 256;
    public bool LoadOnDemand { get; init; }          // samples loaded when a preset is first used, instead of all at load
    public double Gain { get; init; } = 0.5;
    public string Driver { get; init; } = "wasapi";
    public string Device { get; init; } = "default";
    public int SampleRate { get; init; } = 44100;
    public int Buffer { get; init; } = 8;            // FluidSynth's audio.periods

    // Choices offered (FluidSynth 2.6 names and values).
    public static readonly (string Value, string Text)[] ReverbEngines =
        { ("dat", "Dattorro"), ("fdn", "FDN"), ("free", "Freeverb"), ("lex", "Lexverb"), ("smith", "Signalsmith") };
    // Shown as a scale of quality, the method in brackets: FluidSynth's 4th order (4) is a cubic interpolation, 5 and 6
    // sincs of 11th and 17th order. Its sinc of 25th order (7) is too slow to play in real time: not offered.
    public static readonly (int Value, string Text)[] Interpolations =
        { (0, "None"), (1, "Low (linear)"), (4, "Medium (cubic)"), (5, "High (sinc)"), (6, "Best (sinc)") };
    public static readonly int[] Polyphonies = { 64, 128, 256, 512, 1024 };
    public static readonly (string Value, string Text)[] Drivers = { ("wasapi", "WASAPI"), ("dsound", "DirectSound"), ("waveout", "WaveOut") };
    public static readonly int[] SampleRates = { 44100, 48000, 96000 };
    public static readonly int[] Buffers = { 3, 4, 6, 8, 12, 16 };

    // A setting FluidSynth only takes when created differs: the engine restarts.
    public bool NeedsRestart(SoundFontEngineOptions other) =>
        ReverbEngine != other.ReverbEngine || LoadOnDemand != other.LoadOnDemand || Driver != other.Driver || Device != other.Device ||
        SampleRate != other.SampleRate || Buffer != other.Buffer;

    // Length of the audio buffer (ms): WASAPI (shared mode) counts periods of the sound card (10 ms on most of them),
    // DirectSound and WaveOut periods of 512 samples (FluidSynth's period size on Windows).
    public static double BufferMs(string driver, int periods, int sampleRate) =>
        driver == "wasapi" ? periods * 10.0 : periods * 512.0 * 1000 / sampleRate;

    // ---------- PowerMIDI.ini ----------
    public static SoundFontEngineOptions Read(IniFile ini)
    {
        var d = new SoundFontEngineOptions();
        string Text(string key, string def, IEnumerable<string> allowed) =>
            ini.Get("SoundFont", key) is { } v && allowed.Contains(v) ? v : def;
        int Number(string key, int def, IEnumerable<int> allowed) =>
            ini.GetInt("SoundFont", key) is int v && allowed.Contains(v) ? v : def;
        double gain = double.TryParse(ini.Get("SoundFont", "Gain"), NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : d.Gain;
        return new SoundFontEngineOptions
        {
            ReverbEngine = Text("ReverbEngine", d.ReverbEngine, ReverbEngines.Select(x => x.Value)),
            Interpolation = Number("Interpolation", d.Interpolation, Interpolations.Select(x => x.Value)),
            Polyphony = Number("Polyphony", d.Polyphony, Polyphonies),
            LoadOnDemand = ini.Get("SoundFont", "SampleLoading") == "OnDemand",
            Gain = Math.Clamp(Math.Round(gain * 20) / 20, 0, 2),
            Driver = Text("AudioDriver", d.Driver, Drivers.Select(x => x.Value)),
            Device = ini.Get("SoundFont", "AudioDevice") is { Length: > 0 } device ? device : d.Device,
            SampleRate = Number("SampleRate", d.SampleRate, SampleRates),
            Buffer = Number("Buffer", d.Buffer, Buffers),
        };
    }

    public void Write(IniFile ini)
    {
        ini.Set("SoundFont", "ReverbEngine", ReverbEngine);
        ini.Set("SoundFont", "Interpolation", Interpolation);
        ini.Set("SoundFont", "Polyphony", Polyphony);
        ini.Set("SoundFont", "SampleLoading", LoadOnDemand ? "OnDemand" : "All");
        ini.Set("SoundFont", "Gain", Gain.ToString("0.##", CultureInfo.InvariantCulture));
        ini.Set("SoundFont", "AudioDriver", Driver);
        ini.Set("SoundFont", "AudioDevice", Device);
        ini.Set("SoundFont", "SampleRate", SampleRate);
        ini.Set("SoundFont", "Buffer", Buffer);
    }
}
