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
//   lower row   W X / Z X: octave − / +, C V: velocity − / +
// Octaves as Yamaha / Roland name them: C3 = note 60 (middle C), the starting note of the middle row.
// A held key is one held note (auto-repeat ignored). Held notes are released when the mode stops, the channel
// changes, the window loses the focus or the track stops.
public sealed partial class MainViewModel
{
    // Scan code -> semitone from the C of the current octave.
    static readonly Dictionary<int, int> NoteKeys = new()
    {
        [0x1E] = 0, [0x1F] = 2, [0x20] = 4, [0x21] = 5, [0x22] = 7, [0x23] = 9, [0x24] = 11, [0x25] = 12, [0x26] = 14, [0x27] = 16,
        [0x11] = 1, [0x12] = 3, [0x14] = 6, [0x15] = 8, [0x16] = 10, [0x18] = 13, [0x19] = 15,
    };
    const int OctaveDown = 0x2C, OctaveUp = 0x2D, VelocityDown = 0x2E, VelocityUp = 0x2F;

    static int OctaveStep(int scanCode) => scanCode switch { OctaveDown => -1, OctaveUp => 1, _ => 0 };

    readonly Dictionary<int, int> heldNotes = new();   // scan code -> note playing
    int keyboardOctave = 3;                            // C3 = note 60 (Yamaha / Roland naming)
    int keyboardVelocity = 100;

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
        return $"Keyboard on channel {c + 1} ({name}): {Keys(OctaveDown, OctaveUp)}: octave {keyboardOctave} · " +
               $"{Keys(VelocityDown, VelocityUp)}: velocity {keyboardVelocity} · Esc or click {c + 1} again: off";
    }

    // A key pressed while the keyboard mode is on. Returns true if it was used (then nothing else gets it).
    public bool KeyboardKeyDown(int scanCode, bool isRepeat)
    {
        if (keyboardChannel < 0) return false;
        if (NoteKeys.TryGetValue(scanCode, out int semitone))
        {
            if (isRepeat || heldNotes.ContainsKey(scanCode)) return true;
            int note = (keyboardOctave + 2) * 12 + semitone;
            if (note is < 0 or > 127 || output == null) return true;
            output.SendEvent(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)keyboardVelocity) { Channel = (FourBitNumber)keyboardChannel });
            heldNotes[scanCode] = note;
            ChannelMixer.Used[keyboardChannel] = true;
            return true;
        }
        int octaveStep = OctaveStep(scanCode);
        if (isRepeat) return octaveStep != 0 || scanCode is VelocityDown or VelocityUp;
        if (octaveStep != 0) keyboardOctave = Math.Clamp(keyboardOctave + octaveStep, -2, 8);   // C-2 = note 0
        else switch (scanCode)
        {
            case VelocityDown: keyboardVelocity = Math.Max(1, keyboardVelocity - 20); break;
            case VelocityUp: keyboardVelocity = Math.Min(127, keyboardVelocity + 20); break;
            default: return false;
        }
        Status = KeyboardHelp();
        return true;
    }

    public bool KeyboardKeyUp(int scanCode)
    {
        if (!heldNotes.Remove(scanCode, out int note)) return keyboardChannel >= 0 && NoteKeys.ContainsKey(scanCode);
        output?.SendEvent(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0) { Channel = (FourBitNumber)keyboardChannel });
        return true;
    }

    // Releases the notes held on the computer keyboard (focus lost, mode off, channel changed, track stopped).
    public void ReleaseKeyboardNotes()
    {
        if (keyboardChannel >= 0 && output != null)
            foreach (int note in heldNotes.Values)
                output.SendEvent(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0) { Channel = (FourBitNumber)keyboardChannel });
        heldNotes.Clear();
    }

    // LED of the keyboard channel: lit while a key is held.
    int KeyboardLedChannel => heldNotes.Count > 0 ? keyboardChannel : -1;
}
