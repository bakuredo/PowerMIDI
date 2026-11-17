using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Settings of the SoundFont engine (SoundFontEngineOptions): PowerMIDI.ini, values out of the lists, and which changes
// restart the engine.
public class SoundFontEngineOptionsTests
{
    [Fact]
    public void Ini_round_trip()
    {
        var options = new SoundFontEngineOptions
        {
            ReverbEngine = "fdn", Interpolation = 1, Polyphony = 512, LoadOnDemand = true, Gain = 0.75,
            Driver = "dsound", Device = "Casque (USB)", SampleRate = 48000, Buffer = 4,
        };
        var ini = new IniFile();
        options.Write(ini);
        Assert.Equal("0.75", ini.Get("SoundFont", "Gain"));
        Assert.Equal("OnDemand", ini.Get("SoundFont", "SampleLoading"));
        Assert.Equal(options, SoundFontEngineOptions.Read(ini));
    }

    [Fact]
    public void Unknown_values_give_the_defaults()
    {
        var ini = new IniFile();
        ini.Set("SoundFont", "ReverbEngine", "plate");
        ini.Set("SoundFont", "Polyphony", 300);
        ini.Set("SoundFont", "Buffer", 5);
        ini.Set("SoundFont", "Gain", "9");
        var options = SoundFontEngineOptions.Read(ini);
        var defaults = new SoundFontEngineOptions();
        Assert.Equal(defaults.ReverbEngine, options.ReverbEngine);
        Assert.Equal(defaults.Polyphony, options.Polyphony);
        Assert.Equal(defaults.Buffer, options.Buffer);
        Assert.Equal(2, options.Gain);   // kept within 0-2
        Assert.Equal(defaults, SoundFontEngineOptions.Read(new IniFile()));
    }

    [Fact]
    public void Only_creation_settings_restart_the_engine()
    {
        var d = new SoundFontEngineOptions();
        Assert.False((d with { Gain = 1, Interpolation = 6, Polyphony = 1024 }).NeedsRestart(d));
        Assert.True((d with { ReverbEngine = "lex" }).NeedsRestart(d));
        Assert.True((d with { LoadOnDemand = true }).NeedsRestart(d));
        Assert.True((d with { Device = "Other" }).NeedsRestart(d));
        Assert.True((d with { Buffer = 4 }).NeedsRestart(d));
    }

    [Fact]
    public void Buffer_length_by_driver()
    {
        Assert.Equal(80, SoundFontEngineOptions.BufferMs("wasapi", 8, 44100));
        Assert.Equal(92.9, SoundFontEngineOptions.BufferMs("dsound", 8, 44100), 1);
    }
}
