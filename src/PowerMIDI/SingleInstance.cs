using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerMIDI;

// One PowerMIDI per user session: a second launch (e.g. a MIDI file opened from Explorer while PowerMIDI runs) hands
// its files to the running instance through a named pipe, then exits.
// Message: the full paths, one per line (none: just bring the window to the front).
public static class SingleInstance
{
    static readonly string Id = $"PowerMIDI.SingleInstance.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";
    static Mutex mutex;   // held by the running instance until it exits

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    const int AnyProcess = -1;

    // True if this process is the first instance (it then owns the mutex).
    public static bool IsFirst()
    {
        mutex = new Mutex(true, @"Local\" + Id, out bool created);
        return created;
    }

    // Second instance: sends the paths to the running one. False if it does not answer (then this one runs instead).
    public static bool Send(IEnumerable<string> paths)
    {
        try
        {
            AllowSetForegroundWindow(AnyProcess);   // lets the running instance come to the front
            using var pipe = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            pipe.Connect(3000);
            var text = string.Join("\n", paths.Select(p => Path.GetFullPath(p)));
            var bytes = Encoding.UTF8.GetBytes(text);
            pipe.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch { return false; }
    }

    // First instance: receives the paths of the next launches (on a background thread) and passes them to received.
    public static void Listen(Action<string[]> received)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Id, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var paths = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    received(paths);
                }
                catch { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "PowerMIDI single instance" };
        thread.Start();
    }
}
