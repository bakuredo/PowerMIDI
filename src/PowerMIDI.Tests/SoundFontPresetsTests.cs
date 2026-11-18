using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// SF2 mode: the instruments of the Mixer are the presets of the SoundFont (InstrumentDefinitions.SetSoundFont), found
// as the engine finds them (FluidSynth, GS bank style); the resets of the modes that the engine turns into a reset of
// the synthesizer (SoundFontOutput.IsModeReset).
[Collection("ChannelMixer")]
public class SoundFontPresetsTests
{
    static InstrumentDefinitions WithSoundFont()
    {
        var defs = InstrumentDefinitions.Load(Path.Combine(AppContext.BaseDirectory, "instruments"));
        defs.SetSoundFont(new[]
        {
            new SoundFontPreset(0, 0, "Grand Piano"), new SoundFontPreset(8, 0, "Bright Piano"), new SoundFontPreset(0, 4, "E.Piano"),
            new SoundFontPreset(1, 7, "Odd Clavinet"),
            new SoundFontPreset(128, 0, "Standard"), new SoundFontPreset(128, 8, "Room"),
        });
        return defs;
    }

    const string Sf2 = InstrumentDefinitions.Sf2Standard;

    [Fact]
    public void Melodic_preset_is_bank_msb_and_program()
    {
        var defs = WithSoundFont();
        Assert.Equal("Bright Piano", defs.Resolve(Sf2, 0, 0, 8, 0, 0).Name);
        Assert.False(defs.Resolve(Sf2, 0, 0, 8, 3, 0).Missing);                 // the bank LSB is ignored
        var noBank = defs.Resolve(Sf2, 0, 4, 16, 0, 0);                          // no bank 16 at all: bank 0 is meant
        Assert.Equal("E.Piano", noBank.Name);
        Assert.False(noBank.Missing);
        Assert.Contains("no bank 16", noBank.Detail);
        var notInBank = defs.Resolve(Sf2, 0, 4, 8, 0, 0);                        // bank 8 exists, without program 5
        Assert.Equal("E.Piano", notInBank.Name);
        Assert.True(notInBank.Missing);
        Assert.Equal(0, defs.SoundFontBank(16, 4));
        Assert.Equal(8, defs.SoundFontBank(8, 0));
        Assert.Equal("Grand Piano", defs.Resolve(Sf2, 0, 50, 0, 0, 0).Name);    // no program 51: the first sound
        Assert.Equal("Grand Piano", defs.Resolve(Sf2, 0, 7, 0, 0, 0).Name);     // program 8 only in bank 1
    }

    [Fact]
    public void Drum_channel_plays_kits_of_bank_128()
    {
        var defs = WithSoundFont();
        var room = defs.Resolve(Sf2, 9, 8, 0, 0, 1);
        Assert.Equal("Drums · Room", room.Name);
        Assert.True(room.Drum);
        Assert.Equal("Drums · Room", defs.Resolve(Sf2, 9, 8, 127, 0, 1).Name);   // the MSB is ignored (XG kit bank)
        var missing = defs.Resolve(Sf2, 9, 25, 0, 0, 1);
        Assert.Equal("Drums · Standard", missing.Name);
        Assert.True(missing.Missing);
    }

    [Fact]
    public void Lists_by_program_then_bank()
    {
        var defs = WithSoundFont();
        Assert.Equal(new[] { "001 Grand Piano", "      · Bright Piano (bank 8)", "005 E.Piano", "008 Odd Clavinet" },
                     defs.GetChoices(Sf2, false, 0).Select(c => c.Text));
        Assert.Equal(8, defs.GetChoices(Sf2, false, 0)[1].Msb);
        Assert.Equal(new[] { "001 Standard", "009 Room" }, defs.GetChoices(Sf2, true, 0).Select(c => c.Text));
    }

    [Fact]
    public void Without_soundfont_the_gm_names()
    {
        var defs = InstrumentDefinitions.Load(Path.Combine(AppContext.BaseDirectory, "instruments"));
        Assert.Equal("Acoustic Grand Piano", defs.Resolve(Sf2, 0, 0, 0, 0, 0).Name);
        Assert.Equal(128, defs.GetChoices(Sf2, false, 0).Count);
    }

    [Theory]
    [InlineData(new byte[] { 0x7E, 0x7F, 0x09, 0x01, 0xF7 }, true)]                                  // GM System On
    [InlineData(new byte[] { 0x7E, 0x7F, 0x09, 0x03, 0xF7 }, true)]                                  // GM2 System On
    [InlineData(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 }, true)]    // GS Reset
    [InlineData(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 }, true)]                // XG System On
    [InlineData(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x01, 0x1A, 0xF7 }, false)]   // Use for Rhythm Part
    [InlineData(new byte[] { 0x7F, 0x7F, 0x04, 0x01, 0x00, 0x64, 0xF7 }, false)]                     // Master Volume
    public void Mode_resets_are_recognized(byte[] data, bool reset) =>
        Assert.Equal(reset, SoundFontOutput.IsModeReset(data, data.Length - 1));
}
