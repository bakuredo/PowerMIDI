using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Behaviors;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Keyboard mode: the computer keyboard plays a channel, chosen by a click on its number in the Mixer (click it
// again, or Esc: off). Keys by physical position (scan codes), letters only, so that the layout does not matter
// (shown here for AZERTY / QWERTY):
//   middle row  Q S D F G H J K L M / A S D F G H J K L ;   white keys C D E F G A B C D E
//   upper row   Z E · T Y U · O P   / W E · T Y U · O P     black keys C# D# · F# G# A# · C# D#
//   lower row   W X / Z X: octave − / +, C V: velocity − / +, B: scale off, N: next scale (the first one from off),
//               , ; / M ,: step − / +, : ! / . /: key of the scale − / + (with a scale)
// Scales (as the chord mode of the Korg iM1 / Kaossilator): with a scale chosen, the keys of the middle row play the
// notes of the scale one after the other from its key (Japanese in C: C Db F G Ab, then C Db F G Ab an octave up),
// each with two more notes taken in the scale: the step (5 at start) is the number of scale notes between them (Major,
// step 2: C E G; Japanese, step 5: C and the two C above), from 1 to the number of notes of the scale (then the
// octave; 7 at most). The upper row plays nothing then. Drum channels keep single
// notes.
// Octaves as Yamaha / Roland name them: C3 = note 60 (middle C), the starting note of the middle row.
// A held key is one held note or chord (auto-repeat ignored). A note held by two keys (chords sharing notes) is played
// again by the second one and stops with the last one released. Held notes are released when the mode stops, the
// channel changes, the window loses the focus or the track stops.
public sealed partial class MainViewModel
{
    // Scan code -> semitone from the C of the current octave.
    static readonly Dictionary<int, int> NoteKeys = new()
    {
        [0x1E] = 0, [0x1F] = 2, [0x20] = 4, [0x21] = 5, [0x22] = 7, [0x23] = 9, [0x24] = 11, [0x25] = 12, [0x26] = 14, [0x27] = 16,
        [0x11] = 1, [0x12] = 3, [0x14] = 6, [0x15] = 8, [0x16] = 10, [0x18] = 13, [0x19] = 15,
    };
    const int OctaveDown = 0x2C, OctaveUp = 0x2D, VelocityDown = 0x2E, VelocityUp = 0x2F, ScaleOff = 0x30, ScaleNext = 0x31;
    const int StepDown = 0x32, StepUp = 0x33, TonicDown = 0x34, TonicUp = 0x35;   // after B N on the lower row
    const int ScaleKeys = 0x1E, ScaleKeyCount = 10;                            // middle row: scale notes 1 to 10
    const int ChordNotes = 3, MaxStep = 7;

    // Scales: semitones from their key. The scales of the Korg iM1, with the notes of the Korg manuals (EMX-1,
    // Kaossilator 2S), from the softest to the most dissonant (the fewer semitones, the fewer clashes in the chords),
    // the intervals at the end. Japanese first (N from single notes), with step 5 at start: the iM1 setting.
    static readonly (string Name, int[] Notes)[] Scales =
    {
        ("Japanese", new[] { 0, 1, 5, 7, 8 }),          // Miyako-bushi
        // Five notes or less, no semitone.
        ("Major penta", new[] { 0, 2, 4, 7, 9 }),
        ("minor penta", new[] { 0, 3, 5, 7, 10 }),
        ("Egyptian", new[] { 0, 2, 5, 7, 10 }),
        ("Bass Line", new[] { 0, 7, 10 }),
        // Five notes with semitones.
        ("Hawaiian", new[] { 0, 2, 3, 7, 9 }),
        ("Ryukyu", new[] { 0, 4, 5, 7, 11 }),
        ("Chinese", new[] { 0, 4, 6, 7, 11 }),
        ("Bali Pelog", new[] { 0, 1, 3, 7, 8 }),
        // Six and seven notes.
        ("Blues", new[] { 0, 3, 5, 6, 7, 10 }),
        ("Major", new[] { 0, 2, 4, 5, 7, 9, 11 }),
        ("Dorian", new[] { 0, 2, 3, 5, 7, 9, 10 }),
        ("minor", new[] { 0, 2, 3, 5, 7, 8, 10 }),
        ("Harmonic minor", new[] { 0, 2, 3, 5, 7, 8, 11 }),
        // Close semitones.
        ("Raga 2", new[] { 0, 1, 4, 6, 7, 9, 11 }),     // Gamanasrama
        ("Raga 1", new[] { 0, 1, 4, 5, 7, 8, 11 }),     // Bhairav
        ("Gypsy", new[] { 0, 2, 3, 6, 7, 8, 11 }),
        ("Spanish", new[] { 0, 1, 3, 4, 5, 7, 8, 10 }),
        ("Arabic", new[] { 0, 2, 4, 5, 6, 8, 10 }),
        ("Raga 3", new[] { 0, 1, 3, 6, 7, 8, 11 }),     // Todi
        // Intervals.
        ("Wholetone", new[] { 0, 2, 4, 6, 8, 10 }),
        ("minor 3rd", new[] { 0, 3, 6, 9 }),
        ("Major 3rd", new[] { 0, 4, 8 }),
        ("4th", new[] { 0, 5, 10 }),
        ("5th", new[] { 0, 7 }),
        ("Octave", new[] { 0 }),
    };
    static readonly string[] NoteNames = { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

    static int OctaveStep(int scanCode) => scanCode switch { OctaveDown => -1, OctaveUp => 1, _ => 0 };

    readonly Dictionary<int, int[]> heldNotes = new();   // scan code -> notes playing
    readonly Dictionary<int, int> noteHolds = new();     // note playing -> number of keys holding it
    int keyboardOctave = 3;                              // C3 = note 60 (Yamaha / Roland naming)
    int keyboardVelocity = 100;
    int scale = -1;                                      // index in Scales, -1: single notes
    int scaleKey;                                        // 0 = C … 11 = B
    int scaleStep = 5;

    // Channel played by the computer keyboard (-1: off).
    int keyboardChannel = -1;
    public int KeyboardChannel => keyboardChannel;

    // Click on a channel number in the Mixer: keyboard mode on this channel, or off if it was already on it.
    public void ToggleKeyboardChannel(int channel)
    {
        ReleaseKeyboardNotes();
        keyboardChannel = keyboardChannel == channel ? -1 : channel;
        foreach (var ch in Mixer.Channels) ch.IsKeyboard = ch.Channel == keyboardChannel;
        Status = keyboardChannel < 0 ? "Keyboard mode off." : KeyboardHelp();
    }

    string KeyboardHelp()
    {
        string Keys(params int[] scans) => string.Join(" ", scans.Select(PhysicalKeys.Label));
        int c = keyboardChannel;
        var name = Mixer.InstrumentName(c, ChannelMixer.Override[c] ? ChannelMixer.OverrideMsb[c] : ChannelMixer.BankMsb[c],
                                        ChannelMixer.Override[c] ? ChannelMixer.OverrideLsb[c] : ChannelMixer.BankLsb[c],
                                        ChannelMixer.Override[c] ? ChannelMixer.OverrideProgram[c] : ChannelMixer.Program[c]);
        var scaleText = scale < 0 ? $"{Keys(ScaleNext)}: scale (off)"
                      : Mixer.IsDrumChannel(c) ? $"{Keys(ScaleOff)}: scale off · {Keys(ScaleNext)}: {Scales[scale].Name} (not on drums)"
                      : $"{Keys(ScaleOff)}: scale off · {Keys(ScaleNext)}: {Scales[scale].Name} in {NoteNames[scaleKey]} " +
                        $"({string.Join(" ", Scales[scale].Notes.Select(n => NoteNames[(scaleKey + n) % 12]))}) · " +
                        $"{Keys(StepDown, StepUp)}: step {Step} · {Keys(TonicDown, TonicUp)}: key";
        return $"Keyboard on channel {c + 1} ({name}): {Keys(OctaveDown, OctaveUp)}: octave {keyboardOctave} · " +
               $"{Keys(VelocityDown, VelocityUp)}: velocity {keyboardVelocity} · {scaleText} · Esc or click {c + 1} again: off";
    }

    // Step used: at most the number of notes of the scale (a step of the whole scale is the octave), so that the chords
    // of the small scales (Octave, 5th, Bass Line…) stay close. The step asked for is kept for the larger scales.
    int MaxStepOfScale => scale < 0 ? MaxStep : Math.Min(MaxStep, Scales[scale].Notes.Length);
    int Step => Math.Min(scaleStep, MaxStepOfScale);

    // Notes played by a key: with a scale (not on a drum channel), its scale note and the two notes a step and two
    // steps above in the scale; else its note. Null if the key plays nothing.
    int[] KeyNotes(int scanCode)
    {
        int c0 = (keyboardOctave + 2) * 12;
        if (scale >= 0 && !Mixer.IsDrumChannel(keyboardChannel))
        {
            if (scanCode is < ScaleKeys or >= ScaleKeys + ScaleKeyCount) return null;
            var notes = Scales[scale].Notes;
            int Note(int index) => c0 + scaleKey + notes[index % notes.Length] + 12 * (index / notes.Length);
            int first = scanCode - ScaleKeys;
            return Enumerable.Range(0, ChordNotes).Select(i => Note(first + i * Step)).ToArray();
        }
        return NoteKeys.TryGetValue(scanCode, out int semitone) ? new[] { c0 + semitone } : null;
    }

    // A key pressed while the keyboard mode is on. Returns true if it was used (then nothing else gets it).
    public bool KeyboardKeyDown(int scanCode, bool isRepeat)
    {
        if (keyboardChannel < 0) return false;
        if (KeyNotes(scanCode) is { } notes)
        {
            if (isRepeat || heldNotes.ContainsKey(scanCode)) return true;
            notes = notes.Where(n => n is >= 0 and <= 127).ToArray();
            if (notes.Length == 0 || output == null) return true;
            foreach (int note in notes)
            {
                // Already held by another key: stopped first, so that the module plays it once.
                if (noteHolds.TryGetValue(note, out int holds) && holds > 0) SendNoteOff(note);
                noteHolds[note] = holds + 1;
                output.SendEvent(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)keyboardVelocity) { Channel = (FourBitNumber)keyboardChannel });
            }
            heldNotes[scanCode] = notes;
            ChannelMixer.Used[keyboardChannel] = true;
            return true;
        }
        // Key and step only with a scale.
        bool scaleSetting = scanCode is TonicDown or TonicUp or StepUp or StepDown;
        if (scaleSetting && scale < 0) return false;
        int octaveStep = OctaveStep(scanCode);
        if (isRepeat) return octaveStep != 0 || scaleSetting || scanCode is VelocityDown or VelocityUp or ScaleOff or ScaleNext;
        if (octaveStep != 0) keyboardOctave = Math.Clamp(keyboardOctave + octaveStep, -2, 8);   // C-2 = note 0
        else switch (scanCode)
        {
            case VelocityDown: keyboardVelocity = Math.Max(1, keyboardVelocity - 20); break;
            case VelocityUp: keyboardVelocity = Math.Min(127, keyboardVelocity + 20); break;
            // B: single notes (-1); N: the next scale, around (the first one from single notes).
            case ScaleOff: scale = -1; break;
            case ScaleNext: scale = (scale + 1) % Scales.Length; break;
            case TonicDown: scaleKey = (scaleKey + 11) % 12; break;
            case TonicUp: scaleKey = (scaleKey + 1) % 12; break;
            case StepDown: scaleStep = Math.Max(1, Step - 1); break;
            case StepUp: if (Step < MaxStepOfScale) scaleStep = Step + 1; break;   // at the top: the step asked for kept
            default: return false;
        }
        Status = KeyboardHelp();
        return true;
    }

    public bool KeyboardKeyUp(int scanCode)
    {
        if (!heldNotes.Remove(scanCode, out var notes)) return keyboardChannel >= 0 && KeyNotes(scanCode) != null;
        foreach (int note in notes)
        {
            // Still held by another key: it goes on.
            int holds = noteHolds.GetValueOrDefault(note) - 1;
            if (holds > 0) { noteHolds[note] = holds; continue; }
            noteHolds.Remove(note);
            SendNoteOff(note);
        }
        return true;
    }

    void SendNoteOff(int note) =>
        output?.SendEvent(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0) { Channel = (FourBitNumber)keyboardChannel });

    // Releases the notes held on the computer keyboard (focus lost, mode off, channel changed, track stopped).
    public void ReleaseKeyboardNotes()
    {
        if (keyboardChannel >= 0)
            foreach (int note in noteHolds.Keys) SendNoteOff(note);
        heldNotes.Clear();
        noteHolds.Clear();
    }

    // LED of the keyboard channel: lit while a key is held.
    int KeyboardLedChannel => heldNotes.Count > 0 ? keyboardChannel : -1;
}
