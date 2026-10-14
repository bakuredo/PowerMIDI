// PowerMIDI (console) — MIDI file player for sound modules (GM / GM2 / GS / XG / MT-32), based on DryWetMIDI.
// - Detects each file's standard (XG, GS, MT-32, GM2, GM) from its SysEx.
// - Sends the matching reset before each track, and a "panic" between tracks.
// - Plays a file or a whole folder (playlist).
// Keys: Space pause/resume · ←/→ -10 s/+10 s · N next · P previous · R restart · +/- master volume · Q or Ctrl+C quit
// GUI version: PowerMIDI.exe
//
// Examples:
//   PowerMIDI-Console -List
//   PowerMIDI-Console arielxg.mid
//   PowerMIDI-Console D:\Midi -Recurse -Shuffle -Device "SC-8850"
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;   // "·", "←", "→" in the messages
string path = null, device = null, mode = "Auto";
bool recurse = false, shuffle = false, repeat = false, list = false;
var modes = new[] { "Auto", "GS", "XG", "GM", "GM2", "MT-32" };

for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    switch (a.TrimStart('-', '/').ToLowerInvariant())
    {
        case "device" when i + 1 < args.Length: device = args[++i]; break;
        case "mode" when i + 1 < args.Length:
            var value = args[++i];
            mode = modes.FirstOrDefault(m => m.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (mode == null) { Console.Error.WriteLine($"Unknown mode: {value} ({string.Join(", ", modes)})"); return 1; }
            break;
        case "recurse": recurse = true; break;
        case "shuffle": shuffle = true; break;
        case "repeat": repeat = true; break;
        case "list": list = true; break;
        default:
            if (a.StartsWith('-')) { Console.Error.WriteLine($"Unknown option: {a}"); return 1; }
            path = a;
            break;
    }
}

// ---------- MIDI ports ----------
var names = MidiTools.GetOutputNames();
if (list || path == null)
{
    Console.WriteLine("MIDI output ports:");
    for (int i = 0; i < names.Length; i++) Console.WriteLine($"  [{i}] {names[i]}");
    if (path == null) Console.WriteLine("\nUsage: PowerMIDI-Console <file|folder> [-Device <name|index>] [-Mode <mode>] [-Recurse] [-Shuffle] [-Repeat]");
    return 0;
}

string deviceName;
if (device != null)
{
    deviceName = int.TryParse(device, out int n) && n >= 0 && n < names.Length ? names[n]
               : names.FirstOrDefault(x => x.Contains(device, StringComparison.OrdinalIgnoreCase));
    if (deviceName == null) { Console.Error.WriteLine($"MIDI port not found: {device} (see -List)"); return 1; }
}
else deviceName = MidiTools.GetPreferredOutput(names);
if (deviceName == null) { Console.Error.WriteLine("No MIDI output port."); return 1; }
using var output = OutputDevice.GetByName(deviceName);

// ---------- Playlist ----------
List<string> files;
if (Directory.Exists(path))
    files = Directory.EnumerateFiles(path, "*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                     .Where(MidiTools.IsMidiFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
else if (File.Exists(path)) files = new List<string> { path };
else { Console.Error.WriteLine($"File or folder not found: {path}"); return 1; }
if (files.Count == 0) { Console.Error.WriteLine($"No MIDI file in {path}"); return 1; }
if (shuffle) files = files.OrderBy(_ => Random.Shared.Next()).ToList();

// ---------- Playback ----------
var tenSeconds = new MetricTimeSpan(0, 0, 10);
int masterVolume = 127;
int index = 0;
bool quit = false;

// Keyboard control only in an interactive console (not when the input is redirected).
bool interactive = !Console.IsInputRedirected;
if (interactive) Console.TreatControlCAsInput = true;
Console.WriteLine($"Output: {output.Name}   ({files.Count} file(s))");
Console.WriteLine("Space pause · ←/→ ±10 s · N/P next/previous · R restart · +/- volume · Q quit");

try
{
    while (!quit && index < files.Count)
    {
        var file = files[index];
        var action = "next";
        MidiFile midi;
        try { midi = MidiTools.ReadFile(file); }
        catch (Exception ex)
        {
            WriteColor($"\n[{index + 1}/{files.Count}] {Path.GetFileName(file)}: unreadable ({ex.Message})", ConsoleColor.Red);
            index++;
            continue;
        }

        var std = mode == "Auto" ? MidiTools.GetStandard(midi) : mode;
        WriteColor($"\n[{index + 1}/{files.Count}] {Path.GetFileName(file)}  ({std})", ConsoleColor.Cyan);

        MidiTools.SendPanic(output);
        MidiTools.SendReset(output, std);
        if (masterVolume != 127) ChannelMixer.SetMasterVolume(output, masterVolume);

        using (var playback = midi.GetPlayback(output))
        {
            playback.InterruptNotesOnStop = true;
            // After a seek (←/→), resends the up-to-date programs, controllers and pitch bend.
            playback.TrackProgram = true;
            playback.TrackControlValue = true;
            playback.TrackPitchValue = true;
            var duration = MidiTools.FormatTime(playback.GetDuration<MetricTimeSpan>());

            bool paused = false;
            playback.Start();
            try
            {
                while (playback.IsRunning || paused)
                {
                    Console.Write($"\r  {MidiTools.FormatTime(playback.GetCurrentTime<MetricTimeSpan>())} / {duration}  {(paused ? "PAUSE" : "     ")}  vol {masterVolume,3}  ");
                    if (interactive && Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(true);
                        bool ctrlC = k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control);
                        if (ctrlC || k.Key == ConsoleKey.Q) { action = "quit"; break; }
                        if (k.Key == ConsoleKey.N) { action = "next"; break; }
                        if (k.Key == ConsoleKey.P) { action = "prev"; break; }
                        switch (k.Key)
                        {
                            case ConsoleKey.Spacebar:
                                if (paused) playback.Start(); else playback.Stop();
                                paused = !paused;
                                break;
                            case ConsoleKey.R: playback.MoveToStart(); break;
                            case ConsoleKey.RightArrow: playback.MoveForward(tenSeconds); break;
                            case ConsoleKey.LeftArrow: playback.MoveBack(tenSeconds); break;
                        }
                        if (k.KeyChar is '+' or '=') { masterVolume = Math.Min(127, masterVolume + 8); ChannelMixer.SetMasterVolume(output, masterVolume); }
                        else if (k.KeyChar == '-') { masterVolume = Math.Max(0, masterVolume - 8); ChannelMixer.SetMasterVolume(output, masterVolume); }
                    }
                    Thread.Sleep(50);
                }
            }
            finally { playback.Stop(); }
        }

        switch (action)
        {
            case "quit": quit = true; break;
            case "prev": index = Math.Max(0, index - 1); break;
            default: index++; break;
        }
        if (repeat && index >= files.Count) index = 0;
    }
}
finally
{
    MidiTools.SendPanic(output);
    if (interactive) Console.TreatControlCAsInput = false;
    Console.WriteLine("\nStopped.");
}
return 0;

static void WriteColor(string text, ConsoleColor color)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}
