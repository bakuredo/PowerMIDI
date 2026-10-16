using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// A row of the Event Editor: an event of the current track (see MidiEventList), with its position as bar:beat:tick and
// its time, worked out when the row is shown (the list only shows its visible rows: a long file is listed at once).
// FixedText: what replaces the event (a value fixed in the Mixer or Effects tab: the row is blue), or null.
public sealed class EventRowViewModel : ObservableObject
{
    public MidiEventRow Row { get; init; }
    public TempoMap TempoMap { get; init; }
    string position, time;

    public string Position => position ??= BarBeat(TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(Row.Tick, TempoMap));
    public string Time => time ??= TimeSpan.FromSeconds(Seconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    public double Seconds => ((TimeSpan)TimeConverter.ConvertTo<MetricTimeSpan>(Row.Tick, TempoMap)).TotalSeconds;

    static string BarBeat(BarBeatTicksTimeSpan bbt) => $"{bbt.Bars + 1}:{bbt.Beats + 1}:{bbt.Ticks:000}";
    public string Track => Row.Track.ToString();
    public string Channel => Row.Channel >= 0 ? (Row.Channel + 1).ToString() : "";
    public string Type => Row.Type;
    public string Data => Row.Data;
    public string Description => Row.Description;

    string fixedText;
    public string FixedText { get => fixedText; private set { if (Set(ref fixedText, value)) OnPropertyChanged(nameof(IsFixed)); } }
    public bool IsFixed => fixedText != null;

    internal void UpdateFixed() => FixedText = MidiEventList.FixedText(Row);
}

// Event Editor (separate window, read-only for now): all the events of the current track in a list, filtered by track,
// channel, kind and text; the row being played is followed during playback (Follow); double click on a row: playback
// from there. The list is built again when the track changes (start, insertion, save).
public sealed class EventEditorViewModel : ObservableObject
{
    readonly InstrumentDefinitions defs;
    readonly EffectsViewModel effects;
    readonly Action<double> seek;   // moves the playback to a time (seconds)
    List<EventRowViewModel> rows = new();
    TempoMap tempoMap;

    public EventEditorViewModel(InstrumentDefinitions defs, EffectsViewModel effects, Action<double> seek)
    {
        this.defs = defs;
        this.effects = effects;
        this.seek = seek;
        SeekCommand = new RelayCommand(p => { if (p is EventRowViewModel row) seek(row.Seconds); });
        View = CollectionViewSource.GetDefaultView(rows);
    }

    public ICommand SeekCommand { get; }

    string title = "Events";
    public string Title { get => title; private set => Set(ref title, value); }

    ICollectionView view;
    public ICollectionView View { get => view; private set => Set(ref view, value); }

    // ---------- Track ----------

    // Builds the list of a track (file with its insertions) in its standard (names of the instruments).
    public void Load(MidiFile midi, string standard, string fileName)
    {
        tempoMap = midi.GetTempoMap();
        var list = MidiEventList.Build(midi, (c, program, msb, lsb, rhythm) => defs.Resolve(standard, c, program, msb, lsb, rhythm).Name, DescribeSysEx);
        rows = list.Select(r => new EventRowViewModel { Row = r, TempoMap = tempoMap }).ToList();
        RefreshFixed();
        Title = $"Events · {fileName}";

        // Tracks: their name (first Track Name of each).
        var names = list.Where(r => r.Kind == EventKind.Meta && r.Type == "Track Name").GroupBy(r => r.Track).ToDictionary(g => g.Key, g => g.First().Data);
        Tracks = new[] { "All tracks" }.Concat(list.Select(r => r.Track).Distinct().OrderBy(t => t)
                                                   .Select(t => names.TryGetValue(t, out var n) && n != "" ? $"{t} · {n}" : $"{t}")).ToList();
        trackFilter = 0;
        OnPropertyChanged(nameof(TrackFilter));
        current = null;
        View = CollectionViewSource.GetDefaultView(rows);
        View.Filter = Matches;
        UpdateCount();
    }

    // SysEx: the effect parameters it writes (GS / XG, GM2, MT-32 reverb), otherwise MidiEventList's description.
    string DescribeSysEx(byte[] d)
    {
        var texts = GsXgEffects.ValuesOf(d).Select(x => effects.DescribeEffect(x.Param, x.Value)).ToList();
        var gm2 = new int[Gm2Effects.Count];
        var positions = new List<(int Position, int Param)>();
        if (texts.Count == 0 && Gm2Effects.ApplyBytes(d, gm2, positions))
            texts = positions.Select(x => effects.DescribeGm2(x.Param, gm2[x.Param])).ToList();
        var mt32 = new int[Mt32Effects.Count];
        positions.Clear();
        if (texts.Count == 0 && Mt32Effects.ApplyBytes(d, mt32, new int[9], positions) != Mt32Effects.Changes.None)
            texts = positions.Select(x => x.Param).Distinct().Select(p => effects.DescribeMt32(p, mt32[p])).ToList();
        if (texts.Count == 0) return MidiEventList.DescribeSysEx(d);
        return string.Join(" · ", texts.Take(3)) + (texts.Count > 3 ? $" · +{texts.Count - 3} more" : "");
    }

    // The values fixed in the Mixer or Effects tab changed: the rows they replace turn blue (or back).
    public void RefreshFixed()
    {
        foreach (var row in rows) row.UpdateFixed();
    }

    // ---------- Filters ----------

    public IReadOnlyList<string> Tracks { get => tracks; private set => Set(ref tracks, value); }
    IReadOnlyList<string> tracks = new[] { "All tracks" };

    public IReadOnlyList<string> Channels => ChannelNames;
    static readonly IReadOnlyList<string> ChannelNames = new[] { "All channels" }.Concat(Enumerable.Range(1, 16).Select(c => $"Channel {c}"))
                                                                                    .Append("No channel").ToList();

    public IReadOnlyList<string> Kinds => KindNames;
    static readonly IReadOnlyList<string> KindNames =
        new[] { "All events", "Notes", "Control Change", "Program Change", "Pitch Bend", "Pressure", "SysEx", "Meta / text" };
    static readonly EventKind?[] KindOf =
        { null, EventKind.Note, EventKind.Controller, EventKind.Program, EventKind.PitchBend, EventKind.Aftertouch, EventKind.SysEx, EventKind.Meta };

    int trackFilter, channelFilter, kindFilter;
    string searchText = "";

    public int TrackFilter { get => trackFilter; set { if (Set(ref trackFilter, value)) Refilter(); } }
    public int ChannelFilter { get => channelFilter; set { if (Set(ref channelFilter, value)) Refilter(); } }
    public int KindFilter { get => kindFilter; set { if (Set(ref kindFilter, value)) Refilter(); } }
    public string SearchText { get => searchText; set { if (Set(ref searchText, value ?? "")) Refilter(); } }

    int TrackNumber(int index) => index <= 0 ? -1 : int.Parse(Tracks[index].Split(' ')[0], CultureInfo.InvariantCulture);

    bool Matches(object item)
    {
        var row = ((EventRowViewModel)item).Row;
        if (trackFilter > 0 && row.Track != TrackNumber(trackFilter)) return false;
        if (channelFilter is > 0 and <= 16 && row.Channel != channelFilter - 1) return false;
        if (channelFilter == 17 && row.Channel >= 0) return false;
        if (KindOf[kindFilter] is { } kind && row.Kind != kind) return false;
        return searchText == "" || row.Type.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               row.Data.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               row.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    void Refilter()
    {
        View.Refresh();
        current = null;
        UpdateCount();
    }

    string countText = "";
    public string CountText { get => countText; private set => Set(ref countText, value); }
    void UpdateCount() => CountText = $"{View.Cast<object>().Count()} / {rows.Count} events";

    // ---------- Follow ----------

    bool follow = true;
    public bool Follow { get => follow; set { if (Set(ref follow, value) && !value) current = null; } }

    EventRowViewModel current;
    EventRowViewModel selectedRow;
    public EventRowViewModel SelectedRow { get => selectedRow; set => Set(ref selectedRow, value); }

    // The row to bring into view (the window scrolls to it).
    public event Action<EventRowViewModel> FollowRow;

    // Playback position (seconds): with Follow, the last row shown at or before it is selected.
    public void UpdatePosition(double seconds)
    {
        if (!follow || rows.Count == 0) return;
        long tick = TimeConverter.ConvertFrom(new MetricTimeSpan((long)(seconds * 1_000_000)), tempoMap);
        int lo = 0, hi = rows.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (rows[mid].Row.Tick <= tick) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        while (found >= 0 && !Matches(rows[found])) found--;
        var row = found >= 0 ? rows[found] : null;
        if (row == null || row == current) return;
        current = row;
        SelectedRow = row;
        FollowRow?.Invoke(row);
    }
}
