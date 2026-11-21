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
            SoundFontEffects.Set(SoundFontEffects.ChorusSend, 25);
            var ini = new IniFile();
            SoundFontEffects.Write(ini);
            Assert.Equal("1.2", ini.Get("SoundFont", "ChorusSpeed"));
            Assert.Equal("25", ini.Get("SoundFont", "ChorusGlobalSend"));
            Assert.Equal("0", ini.Get("SoundFont", "ReverbOn"));
            Assert.Null(ini.Get("SoundFont", "ReverbLevel"));      // default: not written

            SoundFontEffects.Reset();
            SoundFontEffects.Read(ini);
            Assert.Equal(120, SoundFontEffects.Value(SoundFontEffects.Speed));
            Assert.Equal(0, SoundFontEffects.Value(SoundFontEffects.ReverbOn));
            Assert.Equal(99, SoundFontEffects.Value(SoundFontEffects.Voices));
            Assert.Equal(25, SoundFontEffects.Value(SoundFontEffects.ChorusSend));
            Assert.False(SoundFontEffects.IsChanged(SoundFontEffects.ReverbLevel));
        }
        finally { SoundFontEffects.Reset(); }
    }

    [Fact]
    public void Presets_set_values_of_their_page()
    {
        try
        {
            // Each preset: one value per parameter of its page, within its range; the first one: the defaults.
            foreach (var (first, last) in new[] { (0, SoundFontEffects.ChorusFirst), (SoundFontEffects.ChorusFirst, SoundFontEffects.ChorusEnd) })
            {
                var presets = SoundFontEffects.PresetsOf(first);
                Assert.Equal("Default", presets[0].Name);
                Assert.Equal(presets.Length, presets.Select(x => x.Name).Distinct().Count());
                foreach (var preset in presets)
                {
                    Assert.Equal(last - first, preset.Values.Length);
                    for (int i = 0; i < preset.Values.Length; i++)
                        Assert.InRange(preset.Values[i], SoundFontEffects.Params[first + i].Min, SoundFontEffects.Params[first + i].Max);
                }
            }
            SoundFontEffects.Reset();
            Assert.Equal(0, SoundFontEffects.PresetIndex(0));
            Assert.Equal(0, SoundFontEffects.PresetIndex(SoundFontEffects.ChorusFirst));

            // Hall 2: the values of the reverb page only (its global send kept, and not part of the preset); one of them
            // moved: no preset (values set by hand).
            int hall2 = Array.FindIndex(SoundFontEffects.ReverbPresets, x => x.Name == "Hall 2");
            SoundFontEffects.Set(SoundFontEffects.ReverbSend, 30);
            SoundFontEffects.SetPreset(0, hall2);
            Assert.Equal(hall2, SoundFontEffects.PresetIndex(0));
            Assert.Equal(30, SoundFontEffects.Value(SoundFontEffects.ReverbSend));
            Assert.Equal("30 %", SoundFontEffects.Format(SoundFontEffects.ReverbSend, 30));
            Assert.Equal(0.85, SoundFontEffects.Real(SoundFontEffects.RoomSize), 6);
            Assert.Equal(0, SoundFontEffects.PresetIndex(SoundFontEffects.ChorusFirst));
            SoundFontEffects.Set(SoundFontEffects.Damping, 90);
            Assert.Equal(-1, SoundFontEffects.PresetIndex(0));

            // Chorus 3 after the chorus switched off: the preset switches it on again.
            int chorus3 = Array.FindIndex(SoundFontEffects.ChorusPresets, x => x.Name == "Chorus 3");
            SoundFontEffects.Set(SoundFontEffects.ChorusOn, 0);
            SoundFontEffects.SetPreset(SoundFontEffects.ChorusFirst, chorus3);
            Assert.Equal(1, SoundFontEffects.Value(SoundFontEffects.ChorusOn));
            Assert.Equal("6.0 ms", SoundFontEffects.Format(SoundFontEffects.Depth, SoundFontEffects.Value(SoundFontEffects.Depth)));
        }
        finally { SoundFontEffects.Reset(); }
    }
}
