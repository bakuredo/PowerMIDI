using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Reverb and chorus of the SoundFont engine (SoundFontEffects): the defaults recommended by the author of GeneralUser
// GS, their texts, and PowerMIDI.ini (only the values changed, in FluidSynth's units).
[Collection("ChannelMixer")]   // static values, as the other settings modules
public class SoundFontEffectsTests
{
    [Fact]
    public void Defaults_are_the_recommended_ones()
    {
        SoundFontEffects.Reset();
        Assert.Equal(1, SoundFontEffects.Value(SoundFontEffects.ReverbOn));
        Assert.Equal(0.5, SoundFontEffects.Real(SoundFontEffects.RoomSize), 6);
        Assert.Equal(0.3, SoundFontEffects.Real(SoundFontEffects.Damping), 6);
        Assert.Equal(0.8, SoundFontEffects.Real(SoundFontEffects.Width), 6);
        Assert.Equal(0.7, SoundFontEffects.Real(SoundFontEffects.ReverbLevel), 6);
        Assert.Equal(4, SoundFontEffects.Value(SoundFontEffects.Voices));
        Assert.Equal(0.55, SoundFontEffects.Real(SoundFontEffects.ChorusLevel), 6);
        Assert.Equal(0.36, SoundFontEffects.Real(SoundFontEffects.Speed), 6);
        Assert.Equal(3.6, SoundFontEffects.Real(SoundFontEffects.Depth), 6);
        Assert.Equal(0, SoundFontEffects.Value(SoundFontEffects.Wave));
        Assert.All(Enumerable.Range(0, SoundFontEffects.Count), p => Assert.False(SoundFontEffects.IsChanged(p)));
    }

    [Fact]
    public void Texts_with_units()
    {
        Assert.Equal("0.36 Hz", SoundFontEffects.Format(SoundFontEffects.Speed, 36));
        Assert.Equal("3.6 ms", SoundFontEffects.Format(SoundFontEffects.Depth, 36));
        Assert.Equal("0.50", SoundFontEffects.Format(SoundFontEffects.RoomSize, 50));
        Assert.Equal("Off", SoundFontEffects.Format(SoundFontEffects.ReverbOn, 0));
        Assert.Equal("Triangle", SoundFontEffects.Format(SoundFontEffects.Wave, 1));
    }

    [Fact]
    public void Ini_keeps_the_changed_values()
    {
        try
        {
            SoundFontEffects.Reset();
            SoundFontEffects.Set(SoundFontEffects.Speed, 120);
            SoundFontEffects.Set(SoundFontEffects.ReverbOn, 0);
            SoundFontEffects.Set(SoundFontEffects.Voices, 500);   // out of range: kept within it
            var ini = new IniFile();
            SoundFontEffects.Write(ini);
            Assert.Equal("1.2", ini.Get("SoundFont", "ChorusSpeed"));
            Assert.Equal("0", ini.Get("SoundFont", "ReverbOn"));
            Assert.Null(ini.Get("SoundFont", "ReverbLevel"));      // default: not written

            SoundFontEffects.Reset();
            SoundFontEffects.Read(ini);
            Assert.Equal(120, SoundFontEffects.Value(SoundFontEffects.Speed));
            Assert.Equal(0, SoundFontEffects.Value(SoundFontEffects.ReverbOn));
            Assert.Equal(99, SoundFontEffects.Value(SoundFontEffects.Voices));
            Assert.False(SoundFontEffects.IsChanged(SoundFontEffects.ReverbLevel));
        }
        finally { SoundFontEffects.Reset(); }
    }
}
