using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// Parameter written by a data entry (CC6): an NRPN (CC99 MSB, CC98 LSB) or an RPN (CC101 MSB, CC100 LSB).
public readonly record struct DataEntry(bool Rpn, int Msb, int Lsb);

// NRPN or RPN selected on each channel, to know what a data entry (CC6) writes: XG drum setup (XgDrumSetup) and part
// (XgPartParams) parameters, pitch bend range (RPN 00 00).
public sealed class NrpnState
{
    readonly int[] msb = new int[16], lsb = new int[16];
    readonly bool[] rpn = new bool[16];

    public NrpnState() => Reset();

    public void Reset() { Array.Fill(msb, -1); Array.Fill(lsb, -1); }

    // Updates the state with a controller; for a data entry, returns the parameter it writes (null: none selected, or
    // another controller). Selecting an NRPN number deselects the RPN and conversely; 7F 7F = none.
    public DataEntry? Read(ControlChangeEvent cc)
    {
        int channel = cc.Channel, value = cc.ControlValue;
        switch ((int)cc.ControlNumber)
        {
            case 99: Select(channel, false); msb[channel] = value; return null;
            case 98: Select(channel, false); lsb[channel] = value; return null;
            case 101: Select(channel, true); msb[channel] = value; return null;
            case 100: Select(channel, true); lsb[channel] = value; return null;
            case 6:
                if (msb[channel] < 0 || lsb[channel] < 0 || (msb[channel] == 0x7F && lsb[channel] == 0x7F)) return null;
                return new DataEntry(rpn[channel], msb[channel], lsb[channel]);
            default: return null;
        }
    }

    void Select(int channel, bool isRpn)
    {
        if (rpn[channel] == isRpn) return;
        rpn[channel] = isRpn;
        msb[channel] = lsb[channel] = -1;
    }
}
