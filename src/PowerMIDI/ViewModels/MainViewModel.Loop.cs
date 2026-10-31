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
// A temporary loop (right click on the position bar) plays instead, forever, without changing the track: a listening
// aid, as the speed and the pitch, forgotten when another track starts. Removed: the loop of the track again.
public sealed partial class MainViewModel
{
    const int LoopsWithoutRepeat = 1;            // extra passes of the loop when the track is not repeated

    (long Start, long End, string Source)? loop;  // loop of the current track, in ticks
    (long Start, long End)? tempLoop;             // temporary loop, in ticks (null: none)
    volatile int loopsDone;                       // passes back to the loop start (counted by the playback thread)

    // The loop played: the temporary one, otherwise the track's.
    (long Start, long End)? ActiveLoop => tempLoop ?? (loop is { } l ? (l.Start, l.End) : null);

    public bool HasLoop => loop != null;
    public double LoopStartFraction { get; private set; }
    public double LoopEndFraction { get; private set; }
    public string LoopToolTip { get; private set; }

    public bool HasTempLoop => tempLoop != null;
    public double TempLoopStartFraction { get; private set; }
    public double TempLoopEndFraction { get; private set; }
    public string TempLoopToolTip { get; private set; }

    // New playback of the current track (start, seek rebuild, insertion): its loop and how it plays.
    void SetUpLoop(MidiFile midi, Playback pb)
    {
        ShowLoop(midi);
        pb.RepeatStarted += (_, _) => { loopsDone++; RestoreLoopStart(); };
        PrepareLoop(midi);
        ApplyLoopMode();
    }

    // What the loop played needs at its seam: the settings of its start, the notes to release at its end.
    void PrepareLoop(MidiFile midi)
    {
        loopRestore = null;
        loopEndNotes = null;
        loopLeft = false;
        if (ActiveLoop is { } l)
        {
            loopRestore = MidiTools.GetLoopRestore(midi, l.Start, l.End);
            var from = TimeConverter.ConvertFrom(new MetricTimeSpan(Math.Max(0, (long)(TickToTime(l.End) - LoopEndWindow).TotalMicroseconds)), tempoMap);
            loopEndNotes = MidiTools.GetLoopEndNotes(midi, l.Start, from, l.End);
        }
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
    // track). The markers go into the copy of the track the Event Editor changes (undo, saved by "Save MIDI file…"); those
    // of the temporary loop stay in memory.
    long menuBarStart, menuBarEnd;
    string loopStartMenuText = "Loop start", loopEndMenuText = "Loop end";
    string tempStartMenuText = "Temporary loop start", tempEndMenuText = "Temporary loop end";
    public string LoopStartMenuText { get => loopStartMenuText; private set => Set(ref loopStartMenuText, value); }
    public string LoopEndMenuText { get => loopEndMenuText; private set => Set(ref loopEndMenuText, value); }
    public string TempLoopStartMenuText { get => tempStartMenuText; private set => Set(ref tempStartMenuText, value); }
    public string TempLoopEndMenuText { get => tempEndMenuText; private set => Set(ref tempEndMenuText, value); }

    // Right click at a time of the bar (seconds): the bar there, in the texts of the menu.
    public void PrepareLoopMenu(double seconds)
    {
        if (tempoMap == null) return;
        long tick = TimeConverter.ConvertFrom(new MetricTimeSpan((long)(Math.Clamp(seconds, 0, Duration) * 1_000_000)), tempoMap);
        long bar = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(tick, tempoMap).Bars;
        menuBarStart = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(bar, 0, 0), tempoMap);
        menuBarEnd = Math.Min(TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(bar + 1, 0, 0), tempoMap), LastTick());
        LoopStartMenuText = $"Loop start at bar {bar + 1} ({MidiTools.FormatTime(TickToTime(menuBarStart))})";
        LoopEndMenuText = $"Loop end after bar {bar + 1} ({MidiTools.FormatTime(TickToTime(menuBarEnd))})";
        TempLoopStartMenuText = $"Temporary loop start at bar {bar + 1} ({MidiTools.FormatTime(TickToTime(menuBarStart))})";
        TempLoopEndMenuText = $"Temporary loop end after bar {bar + 1} ({MidiTools.FormatTime(TickToTime(menuBarEnd))})";
    }

    long LastTick() => TimeConverter.ConvertFrom(new MetricTimeSpan((long)(Duration * 1_000_000)), tempoMap);

    // ---------- Temporary loop ----------

    // A start or an end of the temporary loop (tick; null: removed). The start alone loops to the end of the track, the
    // end alone from its start. Set while the playback is past its end: back to its start at once.
    void SetTempLoopPoint(MidiTools.LoopPoint point, long? tick)
    {
        if (current == null || tempoMap == null) { Status = "Play a track first."; return; }
        if (tick is not long at)
        {
            if (tempLoop == null) return;
            tempLoop = null;
            ApplyTempLoop();
            Status = loop != null ? "Temporary loop removed: loop of track again" : "Temporary loop removed";
            return;
        }
        var (start, end) = point == MidiTools.LoopPoint.Start ? (at, tempLoop?.End ?? LastTick()) : (tempLoop?.Start ?? 0, at);
        if (start >= end)
        {
            Status = point == MidiTools.LoopPoint.Start ? "Temporary loop start must be before its end" : "Temporary loop end must be after its start";
            return;
        }
        tempLoop = (start, end);
        ApplyTempLoop();
        Status = $"{TempLoopToolTip} (track not changed; right click: remove)";
    }

    // The temporary loop changed: its markers, and the playback loops on it (or on the track's loop again).
    void ApplyTempLoop()
    {
        ShowTempLoop();
        loopsDone = 0;   // the loop of the track, back: its extra pass as at the start
        if (playback == null || playedFile == null) return;   // stopped: at the next start
        PrepareLoop(playedFile);
        playback.PlaybackStart = null;   // set again at the start of the loop played
        long now = CurrentTick();
        if (tempLoop is { } t && now >= t.End) MoveTo(new MidiTimeSpan(t.Start));
        else PlaceInLoop(now);
    }

    void ShowTempLoop()
    {
        if (tempLoop is { } t && Duration > 0)
        {
            double start = TickToTime(t.Start).TotalSeconds, end = TickToTime(t.End).TotalSeconds;
            TempLoopStartFraction = Math.Min(1, start / Duration);
            TempLoopEndFraction = Math.Min(1, end / Duration);
            TempLoopToolTip = $"Temporary loop from {MidiTools.FormatTime(TimeSpan.FromSeconds(start))} to {MidiTools.FormatTime(TimeSpan.FromSeconds(end))}";
        }
        OnPropertyChanged(nameof(HasTempLoop));
        OnPropertyChanged(nameof(TempLoopStartFraction));
        OnPropertyChanged(nameof(TempLoopEndFraction));
        OnPropertyChanged(nameof(TempLoopToolTip));
    }

    // Another track, or none: no temporary loop any more.
    void ForgetTempLoop()
    {
        if (tempLoop == null) return;
        tempLoop = null;
        ShowTempLoop();
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
        if (loop == null && tick != null)
        {
            Status = point == MidiTools.LoopPoint.Start ? "Loop start set: set loop end too (none in track)"
                                                        : "Loop end set: set loop start too (none in track)";
            return;
        }
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
        if (events == null || device == null || ActiveLoop is not { } active) return;
        try
        {
            foreach (var (channel, note) in loopEndNotes ?? new())
            {
                int number = TransposeNote(new NotePlaybackData((SevenBitNumber)note, (SevenBitNumber)0, (SevenBitNumber)0, (FourBitNumber)channel)).NoteNumber;
                device.SendEvent(new NoteOffEvent((SevenBitNumber)number, (SevenBitNumber)0) { Channel = (FourBitNumber)channel });
            }
            // The values fixed until the first change of their controller: in force again if the loop starts before it.
            ChannelMixer.EnterPassagesAt(device, active.Start);
            foreach (var e in events)
            {
                var sent = ChannelMixer.Callback(e, active.Start, TimeSpan.Zero);
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
        ForgetTempLoop();
    }

    // While looping, the events starting exactly at the loop end belong to the rest of the track (played after the
    // last pass): they are skipped. Read by the playback thread (callbacks).
    volatile bool loopActive;
    long loopEnd;

    bool IsLoopSeam(long rawTime) => loopActive && rawTime == loopEnd;

    // A jump in the track, before the playback moves there: past the loop end (or at it), the loop is left: the rest of
    // the track plays, then its end (repeat track: the track again from its start); back before the loop end, the loop
    // again. Otherwise the playback, which ends at the loop end while looping, would go back to the loop start.
    bool loopLeft;

    void PlaceInLoop(long tick)
    {
        loopLeft = ActiveLoop is { } l && tick >= l.End;
        ApplyLoopMode();
    }

    // Loop on or off for the current playback, from the repeat mode, the passes already done and a jump past its end
    // (a temporary loop: always on, but after a jump past its end).
    // The playback ends one tick after the loop end: ending exactly there, the Note Off events at the loop end would
    // not be played before looping (notes left held, e.g. the strings of FF7's Victory Fanfare).
    void ApplyLoopMode()
    {
        if (playback == null) return;
        var played = ActiveLoop;
        bool active = played != null && !loopLeft && (tempLoop != null || repeatMode == 2 || loopsDone < LoopsWithoutRepeat);
        loopEnd = played?.End ?? -1;
        loopActive = active;
        playback.Loop = active;
        playback.PlaybackEnd = active ? new MidiTimeSpan(played.Value.End + 1) : null;
        if (!active) playback.PlaybackStart = null;
        UpdateLoopStart();
    }

    // Main timer: loop start set once the intro has played; after the last extra pass, the loop stops.
    void UpdateLoop()
    {
        if (playback == null || ActiveLoop == null) return;
        if (playback.Loop && tempLoop == null && repeatMode != 2 && loopsDone >= LoopsWithoutRepeat) ApplyLoopMode();
        else UpdateLoopStart();
    }

    void UpdateLoopStart()
    {
        if (playback is { Loop: true, PlaybackStart: null } && ActiveLoop is { } l && CurrentTick() >= l.Start)
            playback.PlaybackStart = new MidiTimeSpan(l.Start);
    }

    string LoopStatus => tempLoop is { } t ? $"Temporary loop {MidiTools.FormatTime(TickToTime(t.Start))} → {MidiTools.FormatTime(TickToTime(t.End))}"
                       : loop is { } l ? $"Loop {MidiTools.FormatTime(TickToTime(l.Start))} → {MidiTools.FormatTime(TickToTime(l.End))}" : null;
}
