using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// MT-32 sounds redefined by the SysEx of a file: patch table, Memory timbres, timbre of a part.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class Mt32PatchesTests
{
    static readonly Dictionary<int, string> Presets = Enumerable.Range(0, 128).ToDictionary(n => n, n => $"Preset {n + 1}");

    static NormalSysExEvent Write(int a1, int a2, int a3, params byte[] data) =>
        new(new byte[] { 0x41, 0x10, 0x16, 0x12, (byte)a1, (byte)a2, (byte)a3 }.Concat(data).Concat(new byte[] { 0x00, 0xF7 }).ToArray());
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);
    static MidiEvent Program(int channel, int program) => new ProgramChangeEvent((SevenBitNumber)program) { Channel = (FourBitNumber)channel };
    static byte[] Name(string name) => Encoding.ASCII.GetBytes(name.PadRight(10));

    static void Start()
    {
        ChannelMixer.Mt32Mode = true;
        ChannelMixer.ResetChannelState(false);
    }

    [Fact]
    public void Patch_table_and_memory_timbres()
    {
        Start();
        try
        {
            Assert.False(Mt32Patches.IsCustom(5));
            Assert.Equal("Preset 6", Mt32Patches.ProgramName(5, Presets));
            Assert.Equal("Preset 70", Mt32Patches.ProgramName(69, Presets));     // B6

            // Patch 1 -> B50, patch 2 -> Memory 33, patch 3 -> Rhythm 4 (8 bytes per patch: group, number, …).
            int version = Mt32Patches.Version;
            var table = Write(0x05, 0x00, 0x00, 1, 49, 24, 50, 2, 0, 1, 0, 2, 32, 24, 50, 2, 0, 1, 0, 3, 3, 24, 50, 2, 0, 1, 0);
            Assert.Same(table, Play(table));                                      // the message is not changed
            Assert.NotEqual(version, Mt32Patches.Version);
            Assert.True(Mt32Patches.IsCustom(0) && Mt32Patches.IsCustom(1) && Mt32Patches.IsCustom(2));
            Assert.False(Mt32Patches.IsCustom(3));
            Assert.Equal("Preset 114", Mt32Patches.ProgramName(0, Presets));     // B50 = preset 64 + 50
            Assert.Equal("Memory timbre 33", Mt32Patches.ProgramName(1, Presets));
            Assert.Equal("Rhythm timbre 4", Mt32Patches.ProgramName(2, Presets));

            // Memory timbre 33: 256 bytes per timbre from 08 00 00, the name first (address 08 40 00).
            Play(Write(0x08, 0x40, 0x00, Name("ELEC.SHOCK").Concat(new byte[] { 1, 2, 3 }).ToArray()));
            Assert.Equal("ELEC.SHOCK", Mt32Patches.ProgramName(1, Presets));
            // A patch further in the table (patch 34 = 05 02 08), written alone.
            Play(Write(0x05, 0x02, 0x08, 2, 32));
            Assert.Equal("ELEC.SHOCK", Mt32Patches.ProgramName(33, Presets));

            // An MT-32 reset brings the presets back.
            Play(new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x16, 0x12, 0x7F, 0x00, 0x00, 0x01, 0x00, 0xF7 }));
            Assert.False(Mt32Patches.IsCustom(0));
            Assert.Equal("Preset 2", Mt32Patches.ProgramName(1, Presets));
        }
        finally { ChannelMixer.Mt32Mode = false; ChannelMixer.ResetChannelState(false); }
    }

    [Fact]
    public void Timbre_written_into_a_part()
    {
        Start();
        try
        {
            Assert.Null(Mt32Patches.PartName(1, Presets));                        // channel 2 = part 1
            // Patch Temp of part 2 (03 00 10): timbre A20.
            Play(Write(0x03, 0x00, 0x10, 0, 19));
            Assert.Equal("Preset 20", Mt32Patches.PartName(2, Presets));
            Assert.Null(Mt32Patches.PartName(1, Presets));
            // Timbre Temp of part 1 (04 00 00) and of part 2 (04 01 76: 246 bytes further).
            Play(Write(0x04, 0x00, 0x00, Name("MyLead")));
            Play(Write(0x04, 0x01, 0x76, Name("MyBass")));
            Assert.Equal("MyLead", Mt32Patches.PartName(1, Presets));
            Assert.Equal("MyBass", Mt32Patches.PartName(2, Presets));
            // A Program Change on the channel: the part plays its program again.
            Play(Program(1, 7));
            Assert.Null(Mt32Patches.PartName(1, Presets));
            Assert.Equal("MyBass", Mt32Patches.PartName(2, Presets));
            // Channels without a part (1, and 10 = rhythm).
            Assert.Null(Mt32Patches.PartName(0, Presets));
            Assert.Null(Mt32Patches.PartName(9, Presets));
        }
        finally { ChannelMixer.Mt32Mode = false; ChannelMixer.ResetChannelState(false); }
    }
}
