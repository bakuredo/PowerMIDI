using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Values of a settings module (ValueStore): file values with their defaults, fixed values, clearing by group, note or
// parameter, values to write again after a reset.
public class ValueStoreTests
{
    // 2 groups × 3 notes × 2 parameters; defaults: 64 for parameter 0, unknown (-1) for parameter 1.
    static ValueStore Store() => new(2, 3, 2, (g, n, p) => p == 0 ? 64 : -1);

    [Fact]
    public void File_and_fixed_values()
    {
        var s = Store();
        Assert.Equal((64, -1), (s.File(1, 2, 0), s.File(1, 2, 1)));
        s.SetFile(1, 2, 1, 30);
        s.Fix(1, 2, 0, 10);
        Assert.Equal((10, 30), (s.Value(1, 2, 0), s.Value(1, 2, 1)));
        Assert.True(s.HasFixed && s.IsNoteFixed(1, 2) && s.IsGroupFixed(1) && !s.IsGroupFixed(0));
        s.Fix(1, 2, 0, 11);                                              // fixed again: still one value
        Assert.Equal(new[] { (1, 2, 0, 11) }, s.Fixed());

        s.ResetGroup(1);                                                 // the file's values only
        Assert.Equal((64, -1, 11), (s.File(1, 2, 0), s.File(1, 2, 1), s.Value(1, 2, 0)));
    }

    [Fact]
    public void Unfix_by_group_note_or_parameter()
    {
        var s = Store();
        foreach (var (g, n, p) in new[] { (0, 0, 0), (0, 1, 1), (1, 1, 0), (1, 2, 1) }) s.Fix(g, n, p, 1);
        Assert.Equal(new[] { (1, 1, 0) }, s.Unfix(1, 1, -1).Where(x => x.P == 0));
        Assert.Equal(new[] { (0, 1, 1), (1, 2, 1) }, s.Unfix(-1, -1, 1));
        Assert.Equal(new[] { (0, 0, 0) }, s.Unfix());
        Assert.False(s.HasFixed);
    }

    [Fact]
    public void Known_values_of_a_group()
    {
        var s = Store();
        s.SetFile(0, 1, 0, 70);                                          // set by the file
        s.SetFile(0, 2, 0, 64);                                          // the default: not written again
        s.Fix(0, 0, 1, 5);                                               // fixed
        Assert.Equal(new[] { (0, 1, 5), (1, 0, 70) }, s.Known(0));
        Assert.Empty(s.Known(1));
    }
}
