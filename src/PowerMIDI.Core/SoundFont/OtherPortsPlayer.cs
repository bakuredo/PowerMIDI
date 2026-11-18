using System.Diagnostics;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// Plays the channel events of the other MIDI ports of a file (MidiPorts.Split) on the SoundFont engine's channels above
// 16 (port 1: channels 17-32…), following the playback of the rest of the file: its position (ticks), whether it runs,
// its speed. The playback itself is not changed: it plays the first port with the Mixer as usual; this player only
// looks at it, every millisecond, on a thread of its own:
// - running: the events up to the position are sent (notes transposed as the others, except on drum channels: the
//   10th of each port);
// - stopped or paused: the notes of the other ports are released;
// - the position jumps (seek, loop, pause then seek): their sounds are cut, and their state at the new position
//   (bank, program, controllers, RPN, pitch bend, channel pressure) is sent again, as a module would have it there.
public sealed class OtherPortsPlayer : IDisposable
{
    readonly IReadOnlyList<PortEvent> events;
    readonly TempoMap tempoMap;
    readonly Func<(bool Running, long Tick, double Speed)?> position;   // null: the playback is over (the player ends)
    readonly Action<MidiEvent, int> send;                                // an event and its port
    readonly Func<int> transpose;                                        // semitones of the transposition (Pitch slider)
    readonly (int Port, int Channel)[] channels;                         // the channels the events use
    readonly Dictionary<(int Port, int Channel, int Key), Queue<int>> sounding = new();   // note → key sent (transposed)
    readonly Thread thread;
    volatile bool stopping;

    public OtherPortsPlayer(IReadOnlyList<PortEvent> events, TempoMap tempoMap, Func<(bool Running, long Tick, double Speed)?> position,
                            Action<MidiEvent, int> send, Func<int> transpose)
    {
        this.events = events;
        this.tempoMap = tempoMap;
        this.position = position;
        this.send = send;
        this.transpose = transpose;
        channels = events.Where(e => e.Event is ChannelEvent).Select(e => (e.Port, (int)((ChannelEvent)e.Event).Channel)).Distinct().ToArray();
        thread = new Thread(Run) { IsBackground = true, Name = "Other MIDI ports", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public void Dispose()
    {
        stopping = true;
        if (Thread.CurrentThread != thread) thread.Join(1000);
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);

    // ---------- Following the playback ----------
    int next;            // first event not sent yet
    long lastTick = -1;  // position at the previous look
    bool wasRunning;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastLook;

    void Run()
    {
        timeBeginPeriod(1);   // a look every millisecond (the default timer would wake up every 15.6 ms)
        try
        {
            while (!stopping)
            {
                (bool Running, long Tick, double Speed)? now;
                try { now = position(); }
                catch (ObjectDisposedException) { now = null; }
                if (now is not { } p) break;
                Look(p.Running, p.Tick, p.Speed);
                Thread.Sleep(1);
            }
        }
        catch { }   // an error of the engine (closed meanwhile): the player ends
        finally
        {
            try { CutSounds(); } catch { }
            timeEndPeriod(1);
        }
    }

    void Look(bool running, long tick, double speed)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (lastTick < 0) MoveTo(tick);   // first look: the state at the start position
        else if (IsJump(running, tick, now - lastLook, speed)) { CutSounds(); MoveTo(tick); }
        if (running)
        {
            while (next < events.Count && events[next].Tick <= tick) Play(events[next++]);
        }
        else if (wasRunning) ReleaseNotes();
        wasRunning = running;
        lastTick = tick;
        lastLook = now;
    }

    // The position went back, moved while stopped, or went forward faster than playing does (twice the speed, plus 60
    // ms for a late look).
    bool IsJump(bool running, long tick, double elapsed, double speed)
    {
        if (tick < lastTick) return true;
        if (tick == lastTick) return false;
        if (!running && !wasRunning) return true;
        double played = Ms(tick) - Ms(lastTick);
        return played > elapsed * Math.Max(speed, 0.1) * 2 + 60;
    }

    double Ms(long tick) => ((TimeSpan)TimeConverter.ConvertTo<MetricTimeSpan>(tick, tempoMap)).TotalMilliseconds;

    // ---------- Sending ----------
    void Play(PortEvent e)
    {
        switch (e.Event)
        {
            case NoteOnEvent on when on.Velocity > 0:
            {
                int key = TransposedKey(on.Channel, on.NoteNumber);
                var id = (e.Port, (int)on.Channel, (int)on.NoteNumber);
                if (!sounding.TryGetValue(id, out var keys)) sounding[id] = keys = new Queue<int>();
                keys.Enqueue(key);
                send(new NoteOnEvent((SevenBitNumber)key, on.Velocity) { Channel = on.Channel }, e.Port);
                break;
            }
            case NoteOnEvent off0: NoteOff(e.Port, off0.Channel, off0.NoteNumber, SevenBitNumber.MinValue); break;   // velocity 0: a Note Off
            case NoteOffEvent off: NoteOff(e.Port, off.Channel, off.NoteNumber, off.Velocity); break;
            default: send(e.Event, e.Port); break;
        }
    }

    // The Note Off of the key its Note On was sent to (the transposition may have changed meanwhile).
    void NoteOff(int port, FourBitNumber channel, SevenBitNumber note, SevenBitNumber velocity)
    {
        var id = (port, (int)channel, (int)note);
        int key = sounding.TryGetValue(id, out var keys) && keys.Count > 0 ? keys.Dequeue() : TransposedKey(channel, note);
        send(new NoteOffEvent((SevenBitNumber)key, velocity) { Channel = channel }, port);
    }

    int TransposedKey(FourBitNumber channel, SevenBitNumber note)
    {
        int semitones = transpose();
        if (semitones == 0 || channel == 9) return note;   // the drums are not transposed
        int key = note + semitones;
        while (key > 127) key -= 12;
        while (key < 0) key += 12;
        return key;
    }

    static ControlChangeEvent Cc(int channel, int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };

    // Pause: the notes end (released, their sound fades as when a key is let go).
    void ReleaseNotes()
    {
        foreach (var (port, channel) in channels)
        {
            send(Cc(channel, 64, 0), port);    // sustain off
            send(Cc(channel, 123, 0), port);   // all notes off
        }
        sounding.Clear();
    }

    // A jump or the end: the sounds stop at once.
    void CutSounds()
    {
        foreach (var (port, channel) in channels)
        {
            send(Cc(channel, 64, 0), port);
            send(Cc(channel, 120, 0), port);   // all sound off
        }
        sounding.Clear();
    }

    // To a new position: the state of each channel there (the events before it), then the events from it on.
    void MoveTo(long tick)
    {
        int index = 0;
        while (index < events.Count && events[index].Tick < tick) index++;
        next = index;
        foreach (var e in State(events.Take(index))) send(e.Event, e.Port);
    }

    // State of the channels after events: bank (MSB, LSB) and program, the last value of each other controller, each
    // RPN with its last data (then the RPN selected last), the pitch bend and the channel pressure.
    static IEnumerable<PortEvent> State(IEnumerable<PortEvent> before)
    {
        var program = new Dictionary<(int, int), ProgramChangeEvent>();
        var controls = new SortedDictionary<(int, int, int), int>();
        var rpnData = new Dictionary<(int, int), Dictionary<(int, int), (int Msb, int Lsb)>>();
        var rpnSelected = new Dictionary<(int, int), (int Msb, int Lsb)>();
        var bend = new Dictionary<(int, int), PitchBendEvent>();
        var pressure = new Dictionary<(int, int), ChannelAftertouchEvent>();
        foreach (var e in before)
        {
            var key = (e.Port, (int)((ChannelEvent)e.Event).Channel);
            switch (e.Event)
            {
                case ProgramChangeEvent pc: program[key] = pc; break;
                case PitchBendEvent pb: bend[key] = pb; break;
                case ChannelAftertouchEvent at: pressure[key] = at; break;
                case ControlChangeEvent cc:
                {
                    int number = cc.ControlNumber, value = cc.ControlValue;
                    var selected = rpnSelected.GetValueOrDefault(key, (127, 127));
                    if (number == 101) rpnSelected[key] = (value, selected.Item2);
                    else if (number == 100) rpnSelected[key] = (selected.Item1, value);
                    else if (number is 6 or 38)
                    {
                        if (!rpnData.TryGetValue(key, out var data)) rpnData[key] = data = new();
                        (int Msb, int Lsb) old = data.GetValueOrDefault(selected);
                        data[selected] = number == 6 ? (value, old.Lsb) : (old.Msb, value);
                    }
                    else controls[(key.Item1, key.Item2, number)] = value;
                    break;
                }
            }
        }
        foreach (var key in program.Keys.Union(controls.Keys.Select(k => (k.Item1, k.Item2))).Union(rpnData.Keys).Union(bend.Keys).Union(pressure.Keys).Distinct())
        {
            var (port, channel) = key;
            PortEvent Out(MidiEvent m) => new(0, port, m);
            foreach (int bank in new[] { 0, 32 })
                if (controls.TryGetValue((port, channel, bank), out int v)) yield return Out(Cc(channel, bank, v));
            if (program.TryGetValue(key, out var pc)) yield return Out(pc);
            foreach (var ((p, c, number), value) in controls)
                if (p == port && c == channel && number is not (0 or 32)) yield return Out(Cc(channel, number, value));
            if (rpnData.TryGetValue(key, out var data))
                foreach (var ((msb, lsb), (dataMsb, dataLsb)) in data.Where(d => d.Key != (127, 127)))
                {
                    yield return Out(Cc(channel, 101, msb));
                    yield return Out(Cc(channel, 100, lsb));
                    yield return Out(Cc(channel, 6, dataMsb));
                    yield return Out(Cc(channel, 38, dataLsb));
                }
            if (rpnSelected.TryGetValue(key, out var last))
            {
                yield return Out(Cc(channel, 101, last.Msb));
                yield return Out(Cc(channel, 100, last.Lsb));
            }
            if (bend.TryGetValue(key, out var b)) yield return Out(b);
            if (pressure.TryGetValue(key, out var a)) yield return Out(a);
        }
    }
}
