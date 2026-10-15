using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Text written by the file on the screen of the module (MT-32 display area 20 00 00).
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class ModuleDisplayTests
{
    [Fact]
    public void Mt32_display_message()
    {
        try
        {
            ModuleDisplay.Clear();
            Assert.Null(ModuleDisplay.Text);
            ModuleDisplay.Read(Mt32Effects.SysEx(0x20 << 14, "Leisure Suit Larry 3".Select(c => (byte)c).ToArray()).Data);
            Assert.Equal("Leisure Suit Larry 3", ModuleDisplay.Text);
            ModuleDisplay.Read(Mt32Effects.SysEx((0x20 << 14) + 9, "Quest".Select(c => (byte)c).ToArray()).Data);   // from offset 9
            Assert.Equal("Leisure SQuestarry 3", ModuleDisplay.Text);
            ModuleDisplay.Read(Mt32Effects.SysEx(0x10 << 14, 1).Data);   // another area: ignored
            Assert.Equal("Leisure SQuestarry 3", ModuleDisplay.Text);
            ModuleDisplay.Read(Mt32Effects.SysEx(0x20 << 14, "** Quest Studios **".Select(c => (byte)c).ToArray()).Data);   // 19 characters
            Assert.Equal("** Quest Studios ** ", ModuleDisplay.Text);
        }
        finally { ModuleDisplay.Clear(); }
    }

    [Fact]
    public void Sound_Canvas_and_MU_display_messages()
    {
        static byte[] Bytes(string s) => s.Select(c => (byte)c).ToArray();
        try
        {
            ModuleDisplay.Clear();
            // Sound Canvas: 41 10 45 12 10 00 00 text 0A sum F7 (the line feed ends the text).
            var gs = new byte[] { 0x41, 0x10, 0x45, 0x12, 0x10, 0x00, 0x00 }.Concat(Bytes("BAD APPLE")).Concat(new byte[] { 0x0A, 0x00, 0xF7 }).ToArray();
            MidiTools.FixRolandChecksum(gs);
            ModuleDisplay.Read(gs);
            Assert.Equal("BAD APPLE", ModuleDisplay.Text);
            // MU: 43 10 4C 06 00 00 text F7.
            ModuleDisplay.Read(new byte[] { 0x43, 0x10, 0x4C, 0x06, 0x00, 0x00 }.Concat(Bytes("Hello XG")).Append((byte)0xF7).ToArray());
            Assert.Equal("Hello XG", ModuleDisplay.Text);
            ModuleDisplay.Read(new byte[] { 0x43, 0x10, 0x4C, 0x06, 0x00, 0x06 }.Concat(Bytes("MU")).Append((byte)0xF7).ToArray());   // from offset 6
            Assert.Equal("Hello MU", ModuleDisplay.Text);
        }
        finally { ModuleDisplay.Clear(); }
    }

    [Fact]
    public void Dot_pictures()
    {
        try
        {
            ModuleDisplay.Clear();
            Assert.False(ModuleDisplay.HasDots);
            // Sound Canvas: row 0, columns 0-4 = 10000 (bit 4 on the left); row 2 of the block of columns 15 (address 32 + 16 + 2).
            var gs = new byte[] { 0x41, 0x10, 0x45, 0x12, 0x10, 0x01, 0x00, 0x10, 0, 0xF7 };
            MidiTools.FixRolandChecksum(gs);
            ModuleDisplay.Read(gs);
            var gs2 = new byte[] { 0x41, 0x10, 0x45, 0x12, 0x10, 0x01, 0x32, 0x10, 0, 0xF7 };
            MidiTools.FixRolandChecksum(gs2);
            ModuleDisplay.Read(gs2);
            var dots = ModuleDisplay.Dots();
            Assert.True(ModuleDisplay.HasDots && dots[0] && !dots[1] && dots[2 * 16 + 15]);
            Assert.Equal(2, dots.Count(x => x));
            // MU: row 1, columns 7-13 = 0000001 (bit 6 on the left): column 13.
            ModuleDisplay.Read(new byte[] { 0x43, 0x10, 0x4C, 0x07, 0x00, 0x11, 0x01, 0xF7 });
            Assert.True(ModuleDisplay.Dots()[1 * 16 + 13]);
        }
        finally { ModuleDisplay.Clear(); }
    }
}
