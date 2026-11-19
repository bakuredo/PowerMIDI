using System.Globalization;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Append of the Mixer and Effects tabs: the Append of the Event Editor (MainViewModel.Insert.cs), at the position of the
// playback. First click: the playback pauses, its position is kept (the start of its beat), the settings in blue so far
// are written in the whole track. The settings are then changed (heard, paused). Second click: they are written as
// events at that position, those of each channel in the track of its events (the effects in the first track; the rows
// B1-B16 of a file on several MIDI ports in a track of their port), after the other events there and before its notes;
// the playback stays paused. One Append / Replace at a time with the Event
// Editor: started in one place, it shows pressed in the other, and is finished or cancelled from either.
public sealed partial class MainViewModel
{
    long? mixerAppendTick;   // Append started in the Mixer / Effects tabs: where to write (null: none)
    string editorPending;     // Append / Replace started in the Event Editor (see EventEditorViewModel)

    // "Append" or "Replace" while one is in progress, from either place; null otherwise.
    string PendingInsert => mixerAppendTick != null ? "Append" : editorPending;

    // Mixer / Effects tabs: Append pressed (the click itself changes nothing: the command does, the state is shown
    // again), Cancel shown; Append can finish an Append of the Event Editor, not a Replace.
    public bool IsInsertPending { get => PendingInsert != null; set => OnPropertyChanged(); }
    public bool CanMixerAppend => PendingInsert != "Replace";

    // IEventEditing: what the Event Editor has in progress, and the Append of the Mixer / Effects tabs.
    public string EditorPending { set { editorPending = value; NotifyInsert(false); } }
    public bool MixerAppendPending => mixerAppendTick != null;

    void NotifyInsert(bool editor = true)
    {
        OnPropertyChanged(nameof(IsInsertPending));
        OnPropertyChanged(nameof(CanMixerAppend));
        if (editor) eventEditor?.PendingChanged();
    }

    // Append button of the Mixer / Effects tabs: starts, or finishes (an Append of the Event Editor too).
    void MixerAppend()
    {
        if (editorPending == "Append") { eventEditor?.FinishPending(); return; }
        if (editorPending != null) return;
        if (mixerAppendTick != null) { FinishMixerAppend(); return; }
        if (EditBlocked is { } blocked) { Status = blocked; return; }
        if (state == "playing") PausePlayback();
        long at = BeatStart(CurrentTick());
        WriteSettingsInTrack();
        mixerAppendTick = at;
        NotifyInsert();
        Status = $"Append at {BarBeat(at)}: change settings, then click Append again";
    }

    // Cancel button of the Mixer / Effects tabs (also an Append / Replace of the Event Editor).
    void CancelInsert()
    {
        if (editorPending != null) { eventEditor?.CancelPending(); return; }
        CancelMixerAppend();
    }

    public void CancelMixerAppend()
    {
        if (mixerAppendTick == null) return;
        mixerAppendTick = null;
        NotifyInsert();
        DropFixed();
        Status = "Append cancelled";
    }

    // Second click (here or in the Event Editor): the settings changed since the first one, written at its position. A file
    // played on several MIDI ports: the settings of rows A1-A16 in tracks of the first port, those of rows B1-B16 in
    // tracks of theirs.
    public void FinishMixerAppend()
    {
        if (mixerAppendTick is not long tick) return;
        mixerAppendTick = null;
        NotifyInsert();
        if (EditBlocked is { } blocked) { Status = blocked; return; }
        var (events, description) = BuildInsertion();
        var (portEvents, portTexts) = BuildPortInsertion();
        if (events.Count == 0 && portEvents.Count == 0) { Status = "No setting changed: nothing written"; return; }
        var written = ActiveControllers();   // the controllers written (see BuildInsertion)
        // The rows of port B written: back to the file's values now, by the player that has them (the playback and its
        // player are rebuilt by the change).
        ClearPortSettings(false);
        bool onPorts = playedOnPorts;
        long position = playback != null ? Math.Max(CurrentTick(), tick) : tick;
        var all = events.Select(e => (Port: 0, e.Channel, e.Event)).Concat(portEvents).ToList();
        Edit(file => MidiEventEdits.InsertByChannel(file, tick, all, onPorts), position, false);   // the first one: selected in the Event Editor
        // The file's values as they are now at the position (with the events written), sent as the settings are freed.
        RebuildFileState(editedFile, position, CurrentStandard);
        ClearWrittenSettings(written);
        Mixer.Refresh();
        Effects.Refresh();
        var described = string.Join(" · ", new[] { description }.Concat(portTexts).Where(s => s != ""));
        Status = $"Appended at {BarBeat(tick)}: {described} (Ctrl+Z in Event Editor: undo)";
    }

    // Start of the beat of a position (the time signature of the track).
    long BeatStart(long tick)
    {
        if (tempoMap == null) return tick;
        var at = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(tick, tempoMap);
        return TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(at.Bars, at.Beats, 0), tempoMap);
    }

    string BarBeat(long tick) => tempoMap != null ? MidiTools.FormatPosition(tick, tempoMap) : tick.ToString(CultureInfo.InvariantCulture);
}
