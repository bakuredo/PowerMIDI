using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// The module definition files of the instruments folder (copied next to the tests).
public class InstrumentDefinitionsTests
{
    static readonly InstrumentDefinitions Defs = InstrumentDefinitions.Load(Path.Combine(AppContext.BaseDirectory, "instruments"));

    [Fact]
    public void Gm_has_128_sounds_and_one_drum_kit()
    {
        Assert.Equal(128, Defs.GmVoices.Count);
        Assert.Equal("Acoustic Grand Piano", Defs.GmVoices[0]);
        Assert.Equal("Standard Kit", Assert.Single(Defs.GmDrums).Value);
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
        Assert.True(Defs.Resolve("GM", 9, 16, 0, 0, 0).Missing);      // a kit of a larger standard on the GM drum channel

        Assert.False(Defs.Resolve("MT-32", 0, 127, 0, 0, 0).Missing);
    }

    // In GM, the drum channel only offers the GM kit, not those of the GS maps.
    [Fact]
    public void Choices_of_a_gm_drum_channel_are_the_gm_kits()
    {
        Assert.Equal("001 Standard Kit", Assert.Single(Defs.GetChoices("GM", true, 0)).Text);
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
