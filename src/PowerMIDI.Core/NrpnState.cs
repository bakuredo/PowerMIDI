using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// Parameter written by a data entry (CC6, or CC38 for its fine part: Fine): an NRPN (CC99 MSB, CC98 LSB) or an RPN (CC101
// MSB, CC100 LSB).
public readonly record struct DataEntry(bool Rpn, int Msb, int Lsb, bool Fine = false);

// NRPN or RPN selected on each channel, to know what a data entry writes: XG / GS drum (XgDrumSetup, GsDrumMap) and part
// (XgPartParams, GsPartParams, Gm2PartParams) parameters, RPN (pitch bend range, tuning, modulation depth range).
public sealed class NrpnState
{
    readonly int[] msb = new int[16], lsb = new int[16];
    readonly bool[] rpn = new bool[16];

    public NrpnState() => Reset();

    public void Reset() { Array.Fill(msb, -1); Array.Fill(lsb, -1); }

    // Updates the state with a controller; for a data entry (CC6, CC38), returns the parameter it writes (null: none
    // selected, or another controller). Selecting an NRPN number deselects the RPN and conversely; 7F 7F = none.
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
            case 38:
                if (msb[channel] < 0 || lsb[channel] < 0 || (msb[channel] == 0x7F && lsb[channel] == 0x7F)) return null;
                return new DataEntry(rpn[channel], msb[channel], lsb[channel], cc.ControlNumber == 38);
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
