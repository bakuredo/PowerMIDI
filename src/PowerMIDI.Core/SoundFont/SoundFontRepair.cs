using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static PowerMIDI.Core.FluidSynthNative;

namespace PowerMIDI.Core;

// SoundFonts whose zones list their generators out of the order of the specification, read as BASSMIDI reads them.
// The SF2 specification wants a zone's keyRange first and its velRange second (its instrument / sampleID last); some
// SoundFonts (those ripped from Game Boy Advance games: Mother 3…) write the keyRange after other generators. FluidSynth
// then discards the keyRange ("out of order generator"): each zone covers all the keys, so a note of a key-split
// instrument sounds all its samples at once (up to 125 for a drum kit). BASSMIDI accepts the keyRange anywhere.
// The file is never changed: FluidSynth reads it through PatchedLoader, which gives the bytes of the file, except those
// of the generator lists (pgen, igen), given with each zone's generators in the order of the specification (same size).
public static class SoundFontRepair
{
    // Bytes to give in place of the file's, from Offset.
    public sealed record Patch(long Offset, byte[] Bytes);

    // Generator order within a zone: keyRange, velRange, the others (in their order), instrument / sampleID last.
    static int Rank(int generator) => generator switch { 43 => 0, 44 => 1, 41 or 53 => 3, _ => 2 };

    // Puts the generators of each zone in order. bags: a pbag / ibag list (4 bytes per zone: index of its first
    // generator, then of its modulators; the last entry closes the list); gens: the pgen / igen list it indexes
    // (4 bytes per generator: operator, amount). Returns the number of zones changed.
    public static int ReorderZones(ReadOnlySpan<byte> bags, Span<byte> gens)
    {
        int zones = bags.Length / 4 - 1, changed = 0, count = gens.Length / 4;
        for (int z = 0; z < zones; z++)
        {
            int first = BitConverter.ToUInt16(bags[(z * 4)..]), end = BitConverter.ToUInt16(bags[((z + 1) * 4)..]);
            if (first >= end || end > count) continue;
            var items = new List<byte[]>();
            for (int g = first; g < end; g++) items.Add(gens.Slice(g * 4, 4).ToArray());
            var sorted = items.Select((b, i) => (b, i)).OrderBy(x => Rank(BitConverter.ToUInt16(x.b))).ThenBy(x => x.i).Select(x => x.b).ToList();
            if (sorted.Select((b, i) => ReferenceEquals(b, items[i])).All(same => same)) continue;
            changed++;
            for (int i = 0; i < sorted.Count; i++) sorted[i].CopyTo(gens.Slice((first + i) * 4, 4));
        }
        return changed;
    }

    // The patches of a SoundFont file (none if its zones are in order), and the number of zones put in order.
    public static (IReadOnlyList<Patch> Patches, int Zones) Find(string path)
    {
        using var stream = File.OpenRead(path);
        var chunks = PdtaChunks(stream);
        var patches = new List<Patch>();
        int zones = 0;
        foreach (var (bagId, genId) in new[] { ("pbag", "pgen"), ("ibag", "igen") })
        {
            if (!chunks.TryGetValue(bagId, out var bag) || !chunks.TryGetValue(genId, out var gen)) continue;
            var bags = ReadAt(stream, bag.Offset, bag.Size);
            var gens = ReadAt(stream, gen.Offset, gen.Size);
            int n = ReorderZones(bags, gens);
            if (n == 0) continue;
            zones += n;
            patches.Add(new Patch(gen.Offset, gens));
        }
        return (patches, zones);
    }

    // Offsets and sizes of the sub-chunks of the pdta list (the sample data, sdta, is skipped).
    static Dictionary<string, (long Offset, int Size)> PdtaChunks(FileStream stream)
    {
        var chunks = new Dictionary<string, (long, int)>();
        var header = new byte[12];
        if (stream.Read(header, 0, 12) != 12 || Encoding.ASCII.GetString(header, 0, 4) != "RIFF") return chunks;
        long end = Math.Min(stream.Length, 8 + (long)BitConverter.ToUInt32(header, 4));
        var head = new byte[8];
        while (stream.Position + 8 <= end)
        {
            long start = stream.Position;
            if (stream.Read(head, 0, 8) != 8) break;
            string id = Encoding.ASCII.GetString(head, 0, 4);
            long size = BitConverter.ToUInt32(head, 4);
            long next = start + 8 + size + (size & 1);
            if (id == "LIST" && size >= 4 && stream.Read(head, 0, 4) == 4 && Encoding.ASCII.GetString(head, 0, 4) == "pdta")
            {
                long listEnd = Math.Min(end, start + 8 + size);
                while (stream.Position + 8 <= listEnd)
                {
                    long subStart = stream.Position;
                    if (stream.Read(head, 0, 8) != 8) break;
                    long subSize = BitConverter.ToUInt32(head, 4);
                    if (subSize <= int.MaxValue) chunks[Encoding.ASCII.GetString(head, 0, 4)] = (subStart + 8, (int)subSize);
                    stream.Position = subStart + 8 + subSize + (subSize & 1);
                }
            }
            stream.Position = next;
        }
        return chunks;
    }

    static byte[] ReadAt(FileStream stream, long offset, int size)
    {
        var bytes = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    // The bytes read at position (count of them in buffer) with the patches laid over them.
    public static void Overlay(IReadOnlyList<Patch> patches, long position, byte[] buffer, int count)
    {
        foreach (var patch in patches)
        {
            long from = Math.Max(position, patch.Offset), to = Math.Min(position + count, patch.Offset + patch.Bytes.Length);
            if (from < to) Array.Copy(patch.Bytes, from - patch.Offset, buffer, from - position, to - from);
        }
    }
}

// The loader FluidSynth reads SoundFonts with (see SoundFontRepair): its file callbacks give the file's bytes with the
// patches laid over them. Shared by all the engines: its callbacks are kept alive for the whole program; FluidSynth may
// call them from several threads (loading, samples loaded on demand), so each open file has its own stream.
static class PatchedLoader
{
    const int Failed = -1;
    static readonly ConcurrentDictionary<long, PatchedFile> files = new();
    static readonly ConcurrentDictionary<string, (DateTime Written, long Length, IReadOnlyList<SoundFontRepair.Patch> Patches, int Zones)> found = new();
    static long lastHandle;

    static readonly SfOpen open = Open;
    static readonly SfRead read = Read;
    static readonly SfSeek seek = Seek;
    static readonly SfTell tell = Tell;
    static readonly SfClose close = Close;

    // Number of zones of a file put in order (0: none needed), once it was loaded.
    public static int ZonesReordered(string path) => found.TryGetValue(Path.GetFullPath(path), out var f) ? f.Zones : 0;

    // A loader for this synthesizer (it owns it: freed with it), reading through the callbacks.
    public static void Install(IntPtr settings, IntPtr synth)
    {
        var loader = new_fluid_defsfloader(settings);
        if (loader == IntPtr.Zero) return;   // the synthesizer's own loader stays
        fluid_sfloader_set_callbacks(loader, open, read, seek, tell, close);
        fluid_synth_add_sfloader(synth, loader);
    }

    sealed class PatchedFile(FileStream stream, IReadOnlyList<SoundFontRepair.Patch> patches)
    {
        public readonly FileStream Stream = stream;
        public readonly IReadOnlyList<SoundFontRepair.Patch> Patches = patches;
    }

    static IntPtr Open(string filename)
    {
        try
        {
            var path = Path.GetFullPath(filename);
            var file = new FileInfo(path);
            var known = found.TryGetValue(path, out var f) && f.Written == file.LastWriteTimeUtc && f.Length == file.Length ? f : default;
            if (known.Patches == null)
            {
                var (patches, zones) = SoundFontRepair.Find(path);
                known = (file.LastWriteTimeUtc, file.Length, patches, zones);
                found[path] = known;
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            long handle = Interlocked.Increment(ref lastHandle);
            files[handle] = new PatchedFile(stream, known.Patches);
            return (IntPtr)handle;
        }
        catch { return IntPtr.Zero; }
    }

    // Exactly count bytes, or nothing (FLUID_FAILED); in pieces of at most 1 MB (the sample data can be large).
    static int Read(IntPtr buffer, long count, IntPtr handle)
    {
        try
        {
            if (!files.TryGetValue((long)handle, out var file) || count < 0 || file.Stream.Position + count > file.Stream.Length) return Failed;
            var piece = new byte[(int)Math.Min(count, 1 << 20)];
            for (long done = 0; done < count;)
            {
                int n = (int)Math.Min(piece.Length, count - done);
                long position = file.Stream.Position;
                file.Stream.ReadExactly(piece, 0, n);
                SoundFontRepair.Overlay(file.Patches, position, piece, n);
                Marshal.Copy(piece, 0, buffer + (nint)done, n);
                done += n;
            }
            return Ok;
        }
        catch { return Failed; }
    }

    // origin: 0 from the start, 1 from the current position, 2 from the end (as fseek).
    static int Seek(IntPtr handle, long offset, int origin)
    {
        try
        {
            if (!files.TryGetValue((long)handle, out var file)) return Failed;
            long target = origin switch { 0 => offset, 1 => file.Stream.Position + offset, 2 => file.Stream.Length + offset, _ => -1 };
            if (target < 0 || target > file.Stream.Length) return Failed;
            file.Stream.Position = target;
            return Ok;
        }
        catch { return Failed; }
    }

    static long Tell(IntPtr handle) => files.TryGetValue((long)handle, out var file) ? file.Stream.Position : Failed;

    static int Close(IntPtr handle)
    {
        if (!files.TryRemove((long)handle, out var file)) return Failed;
        file.Stream.Dispose();
        return Ok;
    }
}
