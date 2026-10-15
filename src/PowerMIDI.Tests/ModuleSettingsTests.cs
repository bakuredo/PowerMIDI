using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Settings modules of the Effects tab (ModuleSettings: effects, Part and Drums pages): each one sets, lists, sends and
// clears its values the same way, which the player, the Insert mode, the save and the Reset button rely on.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class ModuleSettingsTests
{
    // A valid value of each module: key and value.
    static readonly (SettingsModule Module, SettingKey Key, int Value)[] Samples =
    {
        (ModuleSettings.GsXgFx, new(0, 0, 3), 64),
        (ModuleSettings.Mt32Reverb, new(0, 0, 1), 4),
        (ModuleSettings.Gm2Fx, new(0, 0, 1), 50),
        (ModuleSettings.XgPart, new(2, 0, XgPartParams.BassGain), 70),
        (ModuleSettings.XgPartModes, new(10), 3),
        (ModuleSettings.XgDrums, new(0, 38, 0), 70),
        (ModuleSettings.GsPart, new(2, 0, GsPartParams.KeyShift), 70),
        (ModuleSettings.GsDrums, new(0, 38, 1), 100),
        (ModuleSettings.Gm2Part, new(2, 0, Gm2PartParams.BendRange), 5),
        (ModuleSettings.Gm2Drums, new(9, 38, Gm2DrumParams.Pan), 64),
        (ModuleSettings.Mt32Part, new(0, 0, Mt32PartParams.KeyShift), 30),
        (ModuleSettings.Mt32Drums, new(0, 38, Mt32DrumParams.Level), 50),
    };

    [Fact]
    public void Every_module_sets_lists_sends_and_clears_its_values()
    {
        Assert.Equal(Samples.Length, ModuleSettings.All.Length);
        try
        {
            foreach (var (m, key, value) in Samples)
            {
                Assert.False(m.HasFixed, m.Name);
                m.Set(null, key, value);
                Assert.True(m.HasFixed, m.Name);
                Assert.Equal(new[] { (key, value) }, m.Fixed());
                Assert.NotEmpty(m.Events(key, value));
                Assert.NotNull(m.Write);
                m.Clear(null, key);
                Assert.False(m.HasFixed, m.Name);
            }
            // The XG part modes and drum setups share their writer (one pass in the saved file).
            Assert.Equal(ModuleSettings.XgPartModes.Write, ModuleSettings.XgDrums.Write);
            Assert.Equal(11, ModuleSettings.All.Select(m => m.Write).Distinct().Count());
        }
        finally
        {
            foreach (var m in ModuleSettings.All) m.ClearAll(null);
        }
    }

    [Fact]
    public void Effect_types_come_before_their_parameters()
    {
        try
        {
            var m = ModuleSettings.Gm2Fx;
            m.Set(null, new(0, 0, 1), 50);                               // reverb time, then reverb type
            m.Set(null, new(0, 0, Gm2Effects.ReverbType), 1);
            Assert.Equal(new[] { Gm2Effects.ReverbType, 1 }, m.Fixed().Select(x => x.Key.C));
            // MT-32: the reverb switch of part 1 goes in the track of its channel (2 after a reset); the reverb mode in
            // the first track.
            Assert.Equal((1, -1), (ModuleSettings.Mt32Reverb.ChannelOf(new(0, 0, Mt32Effects.PartFirst)), ModuleSettings.Mt32Reverb.ChannelOf(new(0, 0, 0))));
        }
        finally
        {
            foreach (var m in ModuleSettings.All) m.ClearAll(null);
        }
    }

    [Fact]
    public void Clear_all_and_reset()
    {
        try
        {
            foreach (var (m, key, value) in Samples) m.Set(null, key, value);
            Assert.All(ModuleSettings.All, m => Assert.True(m.HasFixed, m.Name));
            ModuleSettings.ResetFiles();   // the file's values: the fixed ones stay
            Assert.All(ModuleSettings.All, m => Assert.True(m.HasFixed, m.Name));
            foreach (var m in ModuleSettings.All) m.ClearAll(null);
            Assert.All(ModuleSettings.All, m => Assert.False(m.HasFixed, m.Name));
        }
        finally
        {
            foreach (var m in ModuleSettings.All) m.ClearAll(null);
            ModuleSettings.ResetFiles();
        }
    }
}
