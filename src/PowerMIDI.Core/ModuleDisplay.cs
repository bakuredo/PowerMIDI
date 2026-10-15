namespace PowerMIDI.Core;

// Text a file writes on the screen of the module, shown in the player during playback:
// - MT-32: display area 20 00 00 (DT1 F0 41 dev 16 12 20 00 aa …, 20 characters);
// - Sound Canvas (GS): display letters 10 00 00 (DT1 F0 41 dev 45 12 10 00 aa …, 32 characters);
// - MU (XG): display letters 06 00 00 (F0 43 1n 4C 06 00 aa … F7, 32 characters; MU2000 manual p. 105).
// And the 16 × 16 dot picture of the Sound Canvas (10 01 00, page 1: 64 bytes of 5 dots, blocks of 16 rows for columns
// 0-4, 5-9, 10-14, 15; bit 4 on the left) and of the MU (07 00 00: 48 bytes of 7 dots, blocks of 16 rows for columns
// 0-6, 7-13, 14-15; bit 6 on the left). Pictures can be sent in part: the other dots keep their state.
// The characters are written from offset aa; a message from the first character replaces the whole line, and a control
// character (a line feed ends the Sound Canvas texts of some files) ends it. Cleared by a reset (in the file or at the
// start of a track).
public static class ModuleDisplay
{
    public const int Mt32Width = 20, Width = 32;
    static readonly char[] line = new char[Width];
    static volatile string text;
    static readonly bool[] dots = new bool[256];   // row × 16 + column
    static volatile int dotsVersion;
    static volatile bool hasDots;

    static ModuleDisplay() { Clear(); }

    // Text shown (null: none since the last reset).
    public static string Text => text;

    // Dot picture: lit dots (row × 16 + column), whether the file sent one, and a counter of its changes.
    public static bool HasDots => hasDots;
    public static int DotsVersion => dotsVersion;
    public static bool[] Dots() => (bool[])dots.Clone();

    public static void Clear()
    {
        Array.Fill(line, ' ');
        text = null;
        Array.Clear(dots);
        hasDots = false;
        dotsVersion++;
    }

    // Dot picture: the bytes from offset address, each with dotsPerByte dots (the leftmost one in the highest bit used),
    // in blocks of 16 rows.
    static void ReadDots(byte[] d, int first, int last, int address, int dotsPerByte)
    {
        for (int i = first; i < last; i++, address++)
        {
            int block = address / 16, row = address % 16;
            for (int k = 0; k < dotsPerByte; k++)
            {
                int column = block * dotsPerByte + k;
                if (column < 16 && row < 16) dots[row * 16 + column] = (d[i] >> (dotsPerByte - 1 - k) & 1) == 1;
            }
        }
        hasDots = true;
        dotsVersion++;
    }

    // SysEx played from the file: a display message updates the text or the dot picture (the other messages are ignored).
    public static void Read(byte[] d)
    {
        if (d.Length >= 10 && d[0] == 0x41 && d[2] == 0x45 && d[3] == 0x12 && d[4] == 0x10 && d[5] == 0x01)
        {
            ReadDots(d, 7, d.Length - 2, d[6], 5);   // Sound Canvas, page 1
            return;
        }
        if (d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0x07 && d[4] == 0x00)
        {
            ReadDots(d, 6, d.Length - 1, d[5], 7);   // MU
            return;
        }
        int width, offset, first, last = d.Length - 2;   // last: before the checksum and F7 (Roland)
        if (d.Length >= 10 && d[0] == 0x41 && d[2] == 0x16 && d[3] == 0x12 && d[4] == 0x20 && d[5] == 0x00)
            (width, offset, first) = (Mt32Width, d[6], 7);
        else if (d.Length >= 10 && d[0] == 0x41 && d[2] == 0x45 && d[3] == 0x12 && d[4] == 0x10 && d[5] == 0x00)
            (width, offset, first) = (Width, d[6], 7);
        else if (d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0x06 && d[4] == 0x00)
            (width, offset, first, last) = (Width, d[5], 6, d.Length - 1);   // no checksum (Yamaha)
        else return;

        if (offset == 0) Array.Fill(line, ' ');
        for (int i = first, pos = offset; i < last && pos < width; i++, pos++)
        {
            if (d[i] < 0x20) break;
            line[pos] = d[i] < 0x7F ? (char)d[i] : ' ';
        }
        // The MT-32 screen keeps its 20 characters; the 32-character texts lose their trailing spaces.
        text = width == Mt32Width ? new string(line, 0, Mt32Width) : new string(line).TrimEnd();
    }
}
