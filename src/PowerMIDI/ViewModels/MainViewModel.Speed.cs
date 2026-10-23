using System.Globalization;
using System.Windows.Input;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Playback speed and pitch (sliders at the bottom of the Mixer tab): listening aids, never written in the file,
// back to normal when another track starts.
// - Speed: 50 to 200 % of the file's tempo, changed while playing (the position shown stays the file's time).
// - Pitch: -6 to +6 whole tones (one octave each way), applied to each note as it starts (a note in progress keeps
//   its pitch until it is released). The drum channels are left alone: each of their notes is a different instrument.
public sealed partial class MainViewModel
{
    public const int MinSpeed = 50, MaxSpeed = 200, MaxTranspose = 6;   // MaxTranspose in whole tones

    // Tempo of the track at the position (bottom left of the window), as the file has it; with the speed slider moved,
    // the tempo heard too. Empty without a track.
    string tempoText = "";
    public string TempoText { get => tempoText; private set => Set(ref tempoText, value); }

    void UpdateTempo()
    {
        if (current == null || tempoMap == null) { TempoText = ""; return; }
        double bpm = tempoMap.GetTempoAtTime(new MidiTimeSpan(playback != null ? CurrentTick() : 0)).BeatsPerMinute;
        string Bpm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
        TempoText = Math.Abs(speed - 100) < 0.5 ? $"Tempo: {Bpm(bpm)} BPM"
                  : $"Tempo: {Bpm(bpm)} BPM ({Bpm(bpm * speed / 100)} at {speed.ToString("0", CultureInfo.InvariantCulture)} %)";
    }

    double speed = 100;
    public double Speed
    {
        get => speed;
        set
        {
            if (!Set(ref speed, Math.Clamp(Math.Round(value), MinSpeed, MaxSpeed))) return;
            OnPropertyChanged(nameof(SpeedText));
            Run(() => { if (playback != null) playback.Speed = speed / 100; });
        }
    }

    public string SpeedText => $"{speed:0} %";

    volatile int transposeSemitones;   // read by the playback thread: 2 per whole tone

    // Pitch slider, in whole tones.
    public double Transpose
    {
        get => transposeSemitones / 2;
        set
        {
            int tones = (int)Math.Clamp(Math.Round(value), -MaxTranspose, MaxTranspose);
            if (tones * 2 == transposeSemitones) return;
            transposeSemitones = tones * 2;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TransposeText));
        }
    }

    public string TransposeText => MidiTools.FormatSigned(transposeSemitones / 2);

    // Right click on a slider: back to normal.
    public ICommand ResetSpeedCommand => new RelayCommand(() => Speed = 100);
    public ICommand ResetTransposeCommand => new RelayCommand(() => Transpose = 0);

    // Another track (or none): speed and pitch go back to normal.
    void ResetSpeedAndPitch()
    {
        Speed = 100;
        Transpose = 0;
    }

    // Note about to be played: transposed, except on a drum channel. Out of the MIDI range: moved by octaves into it.
    NotePlaybackData TransposeNote(NotePlaybackData note)
    {
        int semitones = transposeSemitones;
        if (note == null || semitones == 0 || Mixer.IsDrumChannel(note.Channel)) return note;
        int number = note.NoteNumber + semitones;
        while (number > 127) number -= 12;
        while (number < 0) number += 12;
        return new NotePlaybackData((SevenBitNumber)number, note.Velocity, note.OffVelocity, note.Channel);
    }
}
