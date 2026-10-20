using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// The module definition files of the instruments folder (copied next to the tests).
[Collection("ChannelMixer")]   // the GS default map of ChannelMixer names the sounds of bank LSB 0
public class InstrumentDefinitionsTests
{
    static readonly InstrumentDefinitions Defs = InstrumentDefinitions.Load(Path.Combine(AppContext.BaseDirectory, "instruments"));

    [Fact]
    public void Gm_has_128_sounds_and_the_gm2_drum_kits()
    {
        Assert.Equal(128, Defs.GmVoices.Count);
        Assert.Equal("Acoustic Grand Piano", Defs.GmVoices[0]);
        // The kits of GM2 (programs 1, 9, 17, 25, 26, 33, 41, 49, 57), used by many GM files.
        Assert.Equal(new[] { 0, 8, 16, 24, 25, 32, 40, 48, 56 }, Defs.GmDrums.Keys.OrderBy(p => p));
        Assert.Equal("Standard Kit", Defs.GmDrums[0]);
    }

    [Fact]
    public void Gm2_has_256_sounds_and_9_drum_kits()
    {
        Assert.Equal(256, Defs.Gm2Voices.Count);
        Assert.Equal(9, Defs.Gm2Drums.Count);
    }

    [Fact]
    public void Mt32_has_128_patches() => Assert.Equal(128, Defs.Mt32Voices.Count);

    [Fact]
    public void Gs_and_xg_are_loaded_with_their_effect_types()
    {
        Assert.NotEmpty(Defs.GsVoices);
        Assert.NotEmpty(Defs.GsDrums);
        Assert.NotEmpty(Defs.EfxTypes);
        Assert.NotEmpty(Defs.XgVoices);
        Assert.NotEmpty(Defs.XgDrums);
        Assert.NotEmpty(Defs.XgReverbTypes);
        Assert.NotEmpty(Defs.XgChorusTypes);
        Assert.NotEmpty(Defs.XgVariationTypes);
    }

    [Fact]
    public void Gm_channel_10_is_the_drum_kit()
    {
        var drums = Defs.Resolve("GM", 9, 0, 0, 0, 0);
        Assert.True(drums.Drum);
        Assert.Equal("Drums · Standard Kit", drums.Name);

        var piano = Defs.Resolve("GM", 0, 0, 0, 0, 0);
        Assert.False(piano.Drum);
        Assert.Equal("Acoustic Grand Piano", piano.Name);
    }

    [Fact]
    public void Gs_names_follow_the_map_and_fall_back_to_the_capital_tone()
    {
        Assert.Equal("Piano 1", Defs.Resolve("GS", 0, 0, 0, 1, 0).Name);                    // SC-55 map
        Assert.Contains("default", Defs.Resolve("GS", 0, 0, 0, 0, 0).Detail);              // no map: the SC-8850's
        int missing = Enumerable.Range(1, 126).First(msb => !Defs.GsVoices.ContainsKey((1, msb, 0)));
        Assert.Contains("variation missing", Defs.Resolve("GS", 0, 0, missing, 1, 0).Detail);
        Assert.True(Defs.Resolve("GS", 9, 0, 0, 0, 1).Drum);                               // drum part
    }

    [Fact]
    public void Gm2_and_xg_drums_follow_the_bank()
    {
        Assert.True(Defs.Resolve("GM2", 0, 0, InstrumentDefinitions.Gm2DrumMsb, 0, 0).Drum);
        Assert.False(Defs.Resolve("GM2", 0, 0, InstrumentDefinitions.Gm2MelodicMsb, 0, 0).Drum);
        Assert.True(Defs.Resolve("XG", 0, 0, 127, 0, 0).Drum);
        Assert.False(Defs.Resolve("XG", 0, 0, 0, 0, 0).Drum);
    }

    // A sound that is not in the list of the mode is flagged (shown in red in the mixer), with its likely fallback as name.
    [Fact]
    public void Sounds_missing_from_the_list_are_flagged()
    {
        Assert.False(Defs.Resolve("GS", 0, 0, 0, 1, 0).Missing);
        int gsMsb = Enumerable.Range(1, 126).First(msb => !Defs.GsVoices.ContainsKey((1, msb, 0)));
        var gs = Defs.Resolve("GS", 0, 0, gsMsb, 1, 0);
        Assert.True(gs.Missing);
        Assert.Equal("Piano 1", gs.Name);
        int gsKit = Enumerable.Range(1, 126).First(pc => !Defs.GsDrums.ContainsKey((InstrumentDefinitions.DefaultGsMap, pc)));
        Assert.True(Defs.Resolve("GS", 9, gsKit, 0, 0, 1).Missing);
        Assert.False(Defs.Resolve("GS", 9, 0, 0, 0, 1).Missing);

        Assert.False(Defs.Resolve("XG", 0, 0, 0, 0, 0).Missing);
        int xgLsb = Enumerable.Range(1, 126).First(lsb => !Defs.XgVoices.ContainsKey((0, lsb, 0)));
        Assert.True(Defs.Resolve("XG", 0, 0, 0, xgLsb, 0).Missing);

        Assert.False(Defs.Resolve("GM2", 0, 0, InstrumentDefinitions.Gm2MelodicMsb, 0, 0).Missing);
        int gm2Lsb = Enumerable.Range(1, 126).First(lsb => !Defs.Gm2Voices.ContainsKey((lsb, 0)));
        Assert.True(Defs.Resolve("GM2", 0, 0, InstrumentDefinitions.Gm2MelodicMsb, gm2Lsb, 0).Missing);

        Assert.False(Defs.Resolve("GM", 0, 40, 0, 0, 0).Missing);
        Assert.False(Defs.Resolve("GM", 9, 0, 0, 0, 0).Missing);
        Assert.True(Defs.Resolve("GM", 9, 127, 0, 0, 0).Missing);     // a kit of a larger standard (GS CM-64/32L) on the GM drum channel

        Assert.False(Defs.Resolve("MT-32", 0, 127, 0, 0, 0).Missing);
    }

    // XG (MU2000): the bank MSB gives the map of a voice.
    [Theory]
    [InlineData(0, "2000")]        // every bank LSB, 126 and 127 (MU100 Native / MU Basic) included
    [InlineData(48, "MEx")]
    [InlineData(64, "SFX")]
    [InlineData(16, "Spl")]
    [InlineData(121, "GM2")]
    [InlineData(5, "2000")]        // a bank the module does not have
    public void Xg_map_of_a_bank(int msb, string map) =>
        Assert.Equal(map, InstrumentDefinitions.XgMapNames[InstrumentDefinitions.XgMap(msb)]);

    [Fact]
    public void Xg_choices_follow_the_map()
    {
        IReadOnlyList<InstrumentChoice> Map(string name) =>
            Defs.GetChoices("XG", false, InstrumentDefinitions.XgMapNames.First(m => m.Value == name).Key);
        var native = Map("2000");
        Assert.DoesNotContain("Basic", InstrumentDefinitions.XgMapNames.Values);

        // The 128 capital voices: bank LSB 0 (MU100 Native, "#" on the reworked ones).
        var nativeCapital = native.Where(c => c.Lsb == 0).ToList();
        Assert.Equal(128, nativeCapital.Count);
        Assert.Equal("001 Grand Piano #", nativeCapital[0].Text);
        Assert.Contains(nativeCapital, c => c.Text.EndsWith("#"));
        Assert.Equal(native[0], nativeCapital[0]);          // the capital voice first, then its variations

        // The variations are indented under their capital voice.
        var variations = native.Where(c => c.Lsb != 0).ToList();
        Assert.NotEmpty(variations);
        Assert.All(variations, c => Assert.StartsWith("      · ", c.Text));
        Assert.All(native, c => Assert.Equal(0, c.Msb));
        // The two explicit versions of a capital voice, after its variations: LSB 126 (MU100 Native), 127 (MU Basic).
        int piano = native.ToList().FindIndex(c => c.Program == 1);
        Assert.Equal(new[] { ("      · Grand Piano #", 126), ("      · Grand Piano", 127) }, native.Take(piano).TakeLast(2).Select(c => (c.Text, c.Lsb)));

        // The other maps are families of sounds, each with its bank MSB.
        Assert.All(Map("MEx"), c => Assert.Equal(48, c.Msb));
        Assert.All(Map("SFX"), c => Assert.Equal(64, c.Msb));
        Assert.All(Map("GM2"), c => Assert.Equal(121, c.Msb));
        var sampling = Map("Spl");
        Assert.Equal(256, sampling.Count);
        Assert.Equal(Enumerable.Range(1, 256).Select(n => $"Voice{n:000}"), sampling.Select(c => c.Text[4..]));
        Assert.Equal(256, Map("GM2").Count);
    }

    // MU50 map (offered with the MU Basic voice map): the voices an XG Level 1 module also has.
    [Fact]
    public void Mu50_map_lists_the_voices_of_the_mu50()
    {
        Assert.Equal(480, Defs.XgMu50Voices.Count);
        var mu50 = Defs.GetChoices("XG", false, InstrumentDefinitions.XgMu50Map);
        Assert.Equal(438, mu50.Count);                                       // the others are SFX voices (their own map)
        Assert.All(mu50, c => Assert.Equal(0, c.Msb));
        Assert.Equal(128, mu50.Count(c => c.Lsb == 0));                      // capital voices: plain bank LSB 0
        Assert.Equal("001 Grand Piano", mu50[0].Text);                       // with their MU Basic name
        Assert.DoesNotContain(mu50, c => c.Text.EndsWith("#"));
        Assert.Contains(mu50, c => c.Text == "      · Grand Piano KSP" && c.Lsb == 1);
        var full = Defs.GetChoices("XG", false, InstrumentDefinitions.XgNativeMap);
        Assert.True(mu50.Count < full.Count);

        // Map of a voice when the MU50 map is offered, or not.
        Assert.Equal(InstrumentDefinitions.XgMu50Map, Defs.XgMap(0, 0, 0, true));
        Assert.Equal(InstrumentDefinitions.XgMu50Map, Defs.XgMap(0, 1, 0, true));
        Assert.Equal(InstrumentDefinitions.XgMu50Map, Defs.XgMap(0, InstrumentDefinitions.XgBasicLsb, 0, true));    // the same voice
        Assert.Equal(InstrumentDefinitions.XgNativeMap, Defs.XgMap(0, InstrumentDefinitions.XgNativeLsb, 0, true));
        var notOnMu50 = full.First(c => c.Lsb != 0 && !Defs.XgMu50Voices.Contains((0, c.Lsb, c.Program)));
        Assert.Equal(InstrumentDefinitions.XgNativeMap, Defs.XgMap(0, notOnMu50.Lsb, notOnMu50.Program, true));
        Assert.Equal(InstrumentDefinitions.XgNativeMap, Defs.XgMap(0, 0, 0, false));
        Assert.Equal(4, Defs.XgMap(64, 0, 0, true));

        // With the MU Basic voice map: only the SFX voices and the drum kits of the MU50.
        var sfx = Defs.GetChoices("XG", false, 4, true);
        Assert.Equal(42, sfx.Count);
        Assert.True(sfx.Count < Defs.GetChoices("XG", false, 4).Count);
        var kits = Defs.GetChoices("XG", true, 0, true);
        Assert.Equal(new[] { "001 Standard Kit", "002 Standard Kit 2", "009 Room Kit", "017 Rock Kit", "025 Electro Kit", "026 Analog Kit",
                             "033 Jazz Kit", "041 Brush Kit", "049 Symphony Kit", "001 SFX Kit 1", "002 SFX Kit 2" }, kits.Select(c => c.Text));
        Assert.True(kits.Count < Defs.GetChoices("XG", true, 0).Count);
    }

    [Fact]
    public void Xg_names_of_the_mu2000()
    {
        Assert.Equal("Grand Piano #", Defs.Resolve("XG", 0, 0, 0, 0, 0).Name);
        Assert.Equal("Grand Piano #", Defs.Resolve("XG", 0, 0, 0, InstrumentDefinitions.XgNativeLsb, 0).Name);
        Assert.Equal("Grand Piano", Defs.Resolve("XG", 0, 0, 0, InstrumentDefinitions.XgBasicLsb, 0).Name);
        Assert.Equal("Drums · Standard Kit", Defs.Resolve("XG", 9, 0, 127, 0, 0).Name);
        Assert.Equal("Drums · SFX Kit 1", Defs.Resolve("XG", 9, 0, 126, 0, 0).Name);
        Assert.Equal("Voice001", Defs.Resolve("XG", 0, 0, 16, 0, 0).Name);
    }

    [Fact]
    public void Xg_effect_types_of_the_mu2000()
    {
        Assert.Equal(19, Defs.XgReverbTypes.Count);
        Assert.Equal(21, Defs.XgChorusTypes.Count);
        Assert.Equal(118, Defs.XgVariationTypes.Count);
        Assert.Equal("Hall 1", Defs.XgReverbTypes[1 * 128].Name);
        var cross = Defs.XgVariationTypes[8 * 128];
        Assert.Equal("Cross Delay", cross.Name);
        var delay = cross.Params[0x42];
        Assert.Equal(("L->R Delay", 1, 7430, 1700, "1/10 ms"), (delay.Name, delay.Min, delay.Max, delay.Default, delay.Unit));
        Assert.Equal("THRU", Defs.XgReverbTypes[1 * 128].Params[0x05].Labels[0]);     // HPF cutoff, wide open
        Assert.Equal("22Hz", Defs.XgReverbTypes[1 * 128].Params[0x05].Labels[1]);
    }

    // In GM, the drum channel offers the GM kits (those of GM2), not those of the GS maps.
    [Fact]
    public void Choices_of_a_gm_drum_channel_are_the_gm_kits()
    {
        Assert.Equal("001 Standard Kit", Defs.GetChoices("GM", true, 0)[0].Text);
        Assert.Equal(9, Defs.GetChoices("GM", true, 0).Count);
        Assert.Equal(128, Defs.GetChoices("GM", false, 0).Count);
        Assert.Equal(256, Defs.GetChoices("GM2", false, 0).Count);
        Assert.Equal(9, Defs.GetChoices("GM2", true, 0).Count);
    }

    [Fact]
    public void Names_are_in_english()
    {
        var names = Defs.GmVoices.Values.Concat(Defs.Gm2Voices.Values).Concat(Defs.Mt32Voices.Values)
            .Concat(Defs.GsVoices.Values).Concat(Defs.GsDrums.Values).Concat(Defs.XgVoices.Values).Concat(Defs.XgDrums.Values)
            .Concat(Types(Defs.EfxTypes)).Concat(Types(Defs.XgReverbTypes)).Concat(Types(Defs.XgChorusTypes)).Concat(Types(Defs.XgVariationTypes));
        Assert.DoesNotContain(names, n => n.Any(c => c >= 0x2E80));

        static IEnumerable<string> Types(Dictionary<int, EffectTypeDef> types) =>
            types.Values.SelectMany(t => t.Params.Values.SelectMany(p => p.Labels.Values.Append(p.Name)).Append(t.Name));
    }
}
