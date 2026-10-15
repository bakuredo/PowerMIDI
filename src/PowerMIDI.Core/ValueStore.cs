namespace PowerMIDI.Core;

// Values of a settings module of the Part and Drums pages, by group (channel, part, drum setup or map), note (one for
// the settings of a part) and parameter: the file's value (set by its messages; after a reset, the default given by
// defaultOf, -1 = unknown) and the value fixed in the interface, which replaces it.
public sealed class ValueStore
{
    readonly int groups, notes, count;
    readonly Func<int, int, int, int> defaultOf;
    readonly int[] file, fixedValue;
    readonly bool[] isFixed;
    int fixedCount;

    public ValueStore(int groups, int notes, int count, Func<int, int, int, int> defaultOf)
    {
        (this.groups, this.notes, this.count, this.defaultOf) = (groups, notes, count, defaultOf);
        file = new int[groups * notes * count];
        fixedValue = new int[file.Length];
        isFixed = new bool[file.Length];
        ResetFile();
    }

    int Index(int g, int n, int p) => (g * notes + n) * count + p;

    public int File(int g, int n, int p) => file[Index(g, n, p)];
    public void SetFile(int g, int n, int p, int value) => file[Index(g, n, p)] = value;
    // File values of a note's parameters as an array (to read a message into a copy), and back.
    public int[] Files(int g, int n) => file.AsSpan(Index(g, n, 0), count).ToArray();
    public void SetFiles(int g, int n, int[] values) => values.AsSpan(0, count).CopyTo(file.AsSpan(Index(g, n, 0), count));
    public bool IsFixed(int g, int n, int p) => isFixed[Index(g, n, p)];
    public int FixedValue(int g, int n, int p) => fixedValue[Index(g, n, p)];
    public int Value(int g, int n, int p) => IsFixed(g, n, p) ? FixedValue(g, n, p) : File(g, n, p);
    public bool HasFixed => fixedCount > 0;
    public bool IsNoteFixed(int g, int n) => Enumerable.Range(0, count).Any(p => IsFixed(g, n, p));
    public bool IsGroupFixed(int g) => Enumerable.Range(0, notes).Any(n => IsNoteFixed(g, n));

    // Reset of the module: the file's values of every group (or of one) back to their defaults.
    public void ResetFile()
    {
        for (int g = 0; g < groups; g++) ResetGroup(g);
    }

    public void ResetGroup(int g)
    {
        for (int n = 0; n < notes; n++)
            for (int p = 0; p < count; p++) file[Index(g, n, p)] = defaultOf(g, n, p);
    }

    public void Fix(int g, int n, int p, int value)
    {
        int i = Index(g, n, p);
        if (!isFixed[i]) fixedCount++;
        isFixed[i] = true;
        fixedValue[i] = value;
    }

    // Gives back to the file the fixed values of a group, a note or a parameter (-1 = all); returns them.
    public List<(int G, int N, int P)> Unfix(int group = -1, int note = -1, int param = -1)
    {
        var cleared = new List<(int, int, int)>();
        foreach (var (g, n, p, _) in Fixed().ToList())
        {
            if ((group >= 0 && g != group) || (note >= 0 && n != note) || (param >= 0 && p != param)) continue;
            isFixed[Index(g, n, p)] = false;
            fixedCount--;
            cleared.Add((g, n, p));
        }
        return cleared;
    }

    public IEnumerable<(int G, int N, int P, int Value)> Fixed()
    {
        for (int g = 0; g < groups; g++)
            for (int n = 0; n < notes; n++)
                for (int p = 0; p < count; p++)
                    if (isFixed[Index(g, n, p)]) yield return (g, n, p, fixedValue[Index(g, n, p)]);
    }

    // Values of a group that the module does not have after a reset: fixed, or set by the file (to write them again
    // after the group was reset).
    public IEnumerable<(int N, int P, int Value)> Known(int g)
    {
        for (int n = 0; n < notes; n++)
            for (int p = 0; p < count; p++)
                if (IsFixed(g, n, p) || File(g, n, p) != defaultOf(g, n, p)) yield return (n, p, Value(g, n, p));
    }
}
