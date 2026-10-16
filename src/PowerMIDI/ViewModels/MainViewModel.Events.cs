using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Event Editor (separate window, see EventEditorViewModel): the events of the current track, and its changes.
// The changes are made on a copy of the track in memory (editedFile), never on the file itself: the playback goes on
// from that copy at the same position, and "Save MIDI file…" writes it (with the settings in blue and the insertions,
// as for the file). Undo / redo keep the previous states. Leaving the track asks first, as for the settings.
public sealed partial class MainViewModel : IEventEditing
{
    EventEditorViewModel eventEditor;   // null while the window is closed
    bool eventsPending;                 // the track changed: not listed yet

    MidiFile editedFile;                // the track with the changes of the Event Editor (null: none)
    int editVersion;                    // counts the changes (fingerprint of the settings)
    // States before (undo) and after (redo) the changes; null: the file itself. They are never changed: each change
    // works on a copy.
    readonly List<MidiFile> undoEdits = new(), redoEdits = new();
    const int UndoLimit = 30;

    void OpenEventEditor()
    {
        if (eventEditor == null)
        {
            eventEditor = new EventEditorViewModel(defs, Effects, SeekTo, this);
            eventsPending = true;
        }
        LoadEvents();
        view.ShowEventEditor(eventEditor, () => eventEditor = null);
    }

    // The track as listed and played: the copy with the changes, otherwise the file.
    MidiFile BaseFile() => editedFile?.Clone() ?? MidiTools.ReadFile(settingsFile);

    // Lists the events of the current track and its insertions (a long file takes a moment: not before the playback
    // starts).
    void LoadEvents()
    {
        if (!eventsPending || eventEditor == null || settingsFile == null) return;
        eventsPending = false;
        var inserted = insertions.SelectMany(i => i.Events.Select(x => (i.Tick, x.Event)));
        eventEditor.Load(BaseFile(), inserted, Mixer.Standard, Path.GetFileName(settingsFile) + (editedFile != null ? " *" : ""));
    }

    // Values fixed in the Mixer or Effects tab (blue): the Event Editor shows the events they replace in blue too.
    string eventsFixed = "";

    void RefreshEventsFixed()
    {
        if (eventEditor == null) return;
        var signature = GetSettingsSignature(false);
        if (signature == eventsFixed) return;
        eventsFixed = signature;
        eventEditor.RefreshFixed();
    }

    // Playback from a time (seconds), as when the position bar is released there.
    void SeekTo(double seconds)
    {
        if (playback == null) return;
        SetPosition(seconds);
        EndSeek();
        TimeText = $"{MidiTools.FormatTime(TimeSpan.FromSeconds(seconds))} / {durationText}";
    }

    // ---------- Changes (IEventEditing) ----------

    public string EditBlocked =>
        current == null || settingsFile == null ? "Play a track first." : insertMode ? "Release Insert first." : null;

    public bool CanUndo => undoEdits.Count > 0;
    public bool CanRedo => redoEdits.Count > 0;

    public int TicksPerQuarter =>
        editedFile?.TimeDivision is TicksPerQuarterNoteTimeDivision d ? d.TicksPerQuarterNote
        : tempoMap?.TimeDivision is TicksPerQuarterNoteTimeDivision t ? t.TicksPerQuarterNote : 480;

    // Makes a change on a copy of the track; change returns the event to select afterwards (track, place), if any.
    public void Edit(Func<MidiFile, (int Track, int Index)?> change)
    {
        if (EditBlocked != null) return;
        var work = BaseFile();
        var select = change(work);
        undoEdits.Add(editedFile);
        if (undoEdits.Count > UndoLimit) undoEdits.RemoveAt(0);
        redoEdits.Clear();
        UseEdits(work, select);
    }

    public void Undo()
    {
        if (!CanUndo || EditBlocked != null) return;
        redoEdits.Add(editedFile);
        var previous = undoEdits[^1];
        undoEdits.RemoveAt(undoEdits.Count - 1);
        UseEdits(previous, null);
    }

    public void Redo()
    {
        if (!CanRedo || EditBlocked != null) return;
        undoEdits.Add(editedFile);
        var next = redoEdits[^1];
        redoEdits.RemoveAt(redoEdits.Count - 1);
        UseEdits(next, null);
    }

    // The track is now this file (null: the file on disk): playback rebuilt at the same position, list built again.
    void UseEdits(MidiFile file, (int Track, int Index)? select)
    {
        editedFile = file;
        editVersion++;
        if (playback != null) ReloadPlayback(CurrentTick(), state == "playing");
        eventsPending = true;
        LoadEvents();
        if (select is { } s) eventEditor?.SelectEvent(s.Track, s.Index);
    }

    // Another track, or the track saved with its changes: the changes are dropped. Returns true if there were any.
    bool ClearEdits()
    {
        bool had = editedFile != null;
        editedFile = null;
        undoEdits.Clear();
        redoEdits.Clear();
        eventsPending = true;
        return had;
    }
}
