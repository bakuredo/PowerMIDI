using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Loop of video game tracks (markers loopStart / loopEnd or [ / ], or CC111; see MidiTools.GetLoop):
// - repeat track: the loop plays forever (the intro once);
// - otherwise: the loop plays one more time, then the track goes on to its end (next track or stop).
// The playback library loops without gap: PlaybackEnd = loop end + 1 tick (see ApplyLoopMode), Loop = true, and
// PlaybackStart = loop start, set
// once the intro has played (set before, the playback would start there and skip the intro).
public sealed partial class MainViewModel
{
    const int LoopsWithoutRepeat = 1;            // extra passes of the loop when the track is not repeated

    (long Start, long End, string Source)? loop;  // loop of the current track, in ticks
    volatile int loopsDone;                       // passes back to the loop start (counted by the playback thread)

    public bool HasLoop => loop != null;
    public double LoopStartFraction { get; private set; }
    public double LoopEndFraction { get; private set; }
    public string LoopToolTip { get; private set; }

    // New playback of the current track (start, seek rebuild, insertion): its loop and how it plays.
    void SetUpLoop(MidiFile midi, Playback pb)
    {
        ShowLoop(midi);
        pb.RepeatStarted += (_, _) => { loopsDone++; RestoreLoopStart(); };
        loopRestore = null;
        loopEndNotes = null;
        if (loop is { } l)
        {
            loopRestore = MidiTools.GetLoopRestore(midi, l.Start, l.End);
            var from = TimeConverter.ConvertFrom(new MetricTimeSpan(Math.Max(0, (long)(TickToTime(l.End) - LoopEndWindow).TotalMicroseconds)), tempoMap);
            loopEndNotes = MidiTools.GetLoopEndNotes(midi, l.Start, from, l.End);
        }
        ApplyLoopMode();
    }

    // The loop of a track, and its markers above the position bar.
    void ShowLoop(MidiFile midi)
    {
        loop = MidiTools.GetLoop(midi);
        if (loop is { } l)
        {
            double start = TickToTime(l.Start).TotalSeconds, end = TickToTime(l.End).TotalSeconds;
            LoopStartFraction = Math.Min(1, start / Duration);
            LoopEndFraction = Math.Min(1, end / Duration);
            LoopToolTip = $"Loop from {MidiTools.FormatTime(TimeSpan.FromSeconds(start))} to {MidiTools.FormatTime(TimeSpan.FromSeconds(end))}";
        }
        OnPropertyChanged(nameof(HasLoop));
        OnPropertyChanged(nameof(LoopStartFraction));
        OnPropertyChanged(nameof(LoopEndFraction));
        OnPropertyChanged(nameof(LoopToolTip));
    }

    // ---------- Loop menu (right click on the position bar) ----------

    // The bar clicked: the loop starts at its start, or ends at its end (the start of the next bar, at most the end of the
    // track). The markers go into the copy of the track the Event Editor changes (undo, saved by "Save MIDI file…").
    long menuBarStart, menuBarEnd;
    string loopStartMenuText = "Loop start", loopEndMenuText = "Loop end";
    public string LoopStartMenuText { get => loopStartMenuText; private set => Set(ref loopStartMenuText, value); }
    public string LoopEndMenuText { get => loopEndMenuText; private set => Set(ref loopEndMenuText, value); }

    // Right click at a time of the bar (seconds): the bar there, in the texts of the menu.
    public void PrepareLoopMenu(double seconds)
    {
        if (tempoMap == null) return;
        long tick = TimeConverter.ConvertFrom(new MetricTimeSpan((long)(Math.Clamp(seconds, 0, Duration) * 1_000_000)), tempoMap);
        long bar = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(tick, tempoMap).Bars;
        long last = TimeConverter.ConvertFrom(new MetricTimeSpan((long)(Duration * 1_000_000)), tempoMap);
        menuBarStart = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(bar, 0, 0), tempoMap);
        menuBarEnd = Math.Min(TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(bar + 1, 0, 0), tempoMap), last);
        LoopStartMenuText = $"Loop start at bar {bar + 1} ({MidiTools.FormatTime(TickToTime(menuBarStart))})";
        LoopEndMenuText = $"Loop end after bar {bar + 1} ({MidiTools.FormatTime(TickToTime(menuBarEnd))})";
    }

    void SetLoopPoint(MidiTools.LoopPoint point, long? tick)
    {
        if (EditBlocked is { } blocked) { Status = blocked; return; }
        if (tick is long at && loop is { } l &&
            (point == MidiTools.LoopPoint.Start ? at >= l.End : at <= l.Start))
        {
            Status = point == MidiTools.LoopPoint.Start ? "Loop start must be before loop end" : "Loop end must be after loop start";
            return;
        }
        Edit(file =>
        {
            if (tick == null) { MidiEventEdits.SetLoopPoint(file, MidiTools.LoopPoint.Start, null); MidiEventEdits.SetLoopPoint(file, MidiTools.LoopPoint.End, null); }
            else MidiEventEdits.SetLoopPoint(file, point, tick);
            return null;
        });
        if (loop == null && tick != null) { Status = "Loop end set: set loop start too (none in track)"; return; }
        Status = loop is { } now ? $"Loop from {MidiTools.FormatTime(TickToTime(now.Start))} to {MidiTools.FormatTime(TickToTime(now.End))} (Ctrl+Z in Event Editor: undo)"
                                 : "No loop";
    }

    // The loop restarts (playback thread): the module still has the settings of the loop end (e.g. the volume after
    // a fade out). Those of the loop start are sent again, through the mixer like the events of the file (values
    // set by hand keep priority, the display follows). Before them, the Note Off of the notes ending in the last
    // moments of the loop (LoopEndWindow: the playback library may have skipped them) and of those held past its end
    // (see MidiTools.GetLoopEndNotes); sent twice, a Note Off does nothing.
    List<MidiEvent> loopRestore;
    List<(int Channel, int Note)> loopEndNotes;
    static readonly TimeSpan LoopEndWindow = TimeSpan.FromMilliseconds(100);

    void RestoreLoopStart()
    {
        var events = loopRestore;
        var device = output;
        if (events == null || device == null || loop == null) return;
        try
        {
            foreach (var (channel, note) in loopEndNotes ?? new())
            {
                int number = TransposeNote(new NotePlaybackData((SevenBitNumber)note, (SevenBitNumber)0, (SevenBitNumber)0, (FourBitNumber)channel)).NoteNumber;
                device.SendEvent(new NoteOffEvent((SevenBitNumber)number, (SevenBitNumber)0) { Channel = (FourBitNumber)channel });
            }
            foreach (var e in events)
            {
                var sent = ChannelMixer.Callback(e, loop.Value.Start, TimeSpan.Zero);
                if (sent != null) device.SendEvent(sent);
            }
        }
        catch { }
    }

    // No track any more: no loop shown.
    void ClearLoop()
    {
        loop = null;
        OnPropertyChanged(nameof(HasLoop));
    }

    // While looping, the events starting exactly at the loop end belong to the rest of the track (played after the
    // last pass): they are skipped. Read by the playback thread (callbacks).
    volatile bool loopActive;
    long loopEnd;

    bool IsLoopSeam(long rawTime) => loopActive && rawTime == loopEnd;

    // Loop on or off for the current playback, from the repeat mode and the passes already done.
    // The playback ends one tick after the loop end: ending exactly there, the Note Off events at the loop end would
    // not be played before looping (notes left held, e.g. the strings of FF7's Victory Fanfare).
    void ApplyLoopMode()
    {
        if (playback == null) return;
        bool active = loop != null && (repeatMode == 2 || loopsDone < LoopsWithoutRepeat);
        loopEnd = loop?.End ?? -1;
        loopActive = active;
        playback.Loop = active;
        playback.PlaybackEnd = active ? new MidiTimeSpan(loop.Value.End + 1) : null;
        if (!active) playback.PlaybackStart = null;
        UpdateLoopStart();
    }

    // Main timer: loop start set once the intro has played; after the last extra pass, the loop stops.
    void UpdateLoop()
    {
        if (playback == null || loop == null) return;
        if (playback.Loop && repeatMode != 2 && loopsDone >= LoopsWithoutRepeat) ApplyLoopMode();
        else UpdateLoopStart();
    }

    void UpdateLoopStart()
    {
        if (playback is { Loop: true, PlaybackStart: null } && CurrentTick() >= loop.Value.Start)
            playback.PlaybackStart = new MidiTimeSpan(loop.Value.Start);
    }

    string LoopStatus => loop is { } l ? $" — loop {MidiTools.FormatTime(TickToTime(l.Start))} → {MidiTools.FormatTime(TickToTime(l.End))}" : "";
}
