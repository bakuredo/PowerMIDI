using System.Text;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// SoundFonts whose zones have their keyRange out of place (SoundFontRepair): the generators of each zone put in the
// order of the specification, the file itself never changed, the patches laid over the bytes read.
public class SoundFontRepairTests
{
    static byte[] Gen(int op, int amount) => BitConverter.GetBytes((ushort)op).Concat(BitConverter.GetBytes((short)amount)).ToArray();
    static byte[] KeyRange(int lo, int hi) => BitConverter.GetBytes((ushort)43).Concat(new[] { (byte)lo, (byte)hi }).ToArray();
    static byte[] Bags(params int[] firstGens) => firstGens.SelectMany(g => BitConverter.GetBytes((ushort)g).Concat(new byte[2])).ToArray();
    static int Op(byte[] gens, int index) => BitConverter.ToUInt16(gens, index * 4);

    [Fact]
    public void Key_range_first_sample_last_others_kept_in_order()
    {
        // Zone 1: release, keyRange, sampleModes, sampleID (as in the Game Boy Advance rips); zone 2: already in order.
        var gens = new[] { Gen(38, -3507), KeyRange(0, 1), Gen(54, 1), Gen(53, 158), KeyRange(2, 23), Gen(38, -3507), Gen(53, 159), new byte[4] }
            .SelectMany(g => g).ToArray();
        int changed = SoundFontRepair.ReorderZones(Bags(0, 4, 7), gens);
        Assert.Equal(1, changed);
        Assert.Equal(new[] { 43, 38, 54, 53, 43, 38, 53 }, Enumerable.Range(0, 7).Select(i => Op(gens, i)));
        Assert.Equal(0, gens[2]);   // the keyRange, now first, kept its amount (0-1)
        Assert.Equal(1, gens[3]);
    }

    [Fact]
    public void Velocity_range_after_key_range_and_zones_in_order_untouched()
    {
        var gens = new[] { Gen(44, 0x7F00), Gen(51, 12), KeyRange(10, 20), Gen(53, 1), new byte[4] }.SelectMany(g => g).ToArray();
        Assert.Equal(1, SoundFontRepair.ReorderZones(Bags(0, 4), gens));
        Assert.Equal(new[] { 43, 44, 51, 53 }, Enumerable.Range(0, 4).Select(i => Op(gens, i)));
        Assert.Equal(0, SoundFontRepair.ReorderZones(Bags(0, 4), gens));   // now in order: nothing to do
    }

    [Fact]
    public void Patches_found_in_file_and_laid_over_its_bytes()
    {
        static byte[] Chunk(string id, byte[] data) =>
            Encoding.ASCII.GetBytes(id).Concat(BitConverter.GetBytes((uint)data.Length)).Concat(data).Concat(data.Length % 2 == 1 ? new byte[1] : Array.Empty<byte>()).ToArray();
        static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", Encoding.ASCII.GetBytes(type).Concat(chunks.SelectMany(c => c)).ToArray());
        var igen = new[] { Gen(58, 17), KeyRange(8, 8), Gen(53, 0), new byte[4] }.SelectMany(g => g).ToArray();
        var pdta = List("pdta", Chunk("phdr", new byte[76]), Chunk("pbag", Bags(0, 0)), Chunk("pgen", new byte[4]),
                        Chunk("inst", new byte[44]), Chunk("ibag", Bags(0, 3)), Chunk("igen", igen));
        var file = Chunk("RIFF", Encoding.ASCII.GetBytes("sfbk").Concat(List("INFO", Chunk("ifil", new byte[4]))).Concat(List("sdta", Chunk("smpl", new byte[64]))).Concat(pdta).ToArray());
        var path = Path.Combine(Path.GetTempPath(), $"powermidi-test-{Guid.NewGuid():N}.sf2");
        File.WriteAllBytes(path, file);
        try
        {
            var (patches, zones) = SoundFontRepair.Find(path);
            Assert.Equal(1, zones);
            var patch = Assert.Single(patches);
            Assert.Equal(igen.Length, patch.Bytes.Length);
            Assert.Equal(file, File.ReadAllBytes(path));   // the file is not changed

            // The whole file read with the patch laid over it: the zone has its keyRange first.
            var read = File.ReadAllBytes(path);
            SoundFontRepair.Overlay(patches, 0, read, read.Length);
            Assert.Equal(new[] { 43, 58, 53 }, Enumerable.Range(0, 3).Select(i => (int)BitConverter.ToUInt16(read, (int)patch.Offset + i * 4)));
            // A piece read across the end of the patch gets only the part it covers.
            var piece = file.Skip((int)patch.Offset + 2).Take(8).ToArray();
            SoundFontRepair.Overlay(patches, patch.Offset + 2, piece, piece.Length);
            Assert.Equal(read.Skip((int)patch.Offset + 2).Take(8), piece);
        }
        finally { File.Delete(path); }
    }
}
