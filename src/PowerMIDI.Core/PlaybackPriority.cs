using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PowerMIDI.Core;

// Keeps the timing of the playback the same when the program is in the background (window minimized or hidden, session
// locked). While a track plays:
// - the 1 ms timer resolution is asked for, and Windows is told not to ignore it nor to slow the process down
//   (power throttling): otherwise Windows 11 gives a process without a visible window the default 15.6 ms timer, and
//   the playback clock (1 ms ticks) then plays the notes in bursts;
// - the process runs above normal priority, so that other programs do not delay the clock;
// - the computer does not go to sleep (the screen may still turn off).
// Everything goes back to normal when the playback stops or pauses. Windows only; errors are ignored (the playback
// still works, with the timing Windows gives).
public static class PlaybackPriority
{
    static bool active;

    // On when a track plays, off when it stops or pauses (called from one thread: the sleep setting belongs to it).
    public static void Set(bool playing)
    {
        if (playing == active || !OperatingSystem.IsWindows()) return;
        active = playing;
        try { SetPowerThrottling(!playing); } catch { }
        try { if (playing) timeBeginPeriod(1); else timeEndPeriod(1); } catch { }
        try { Process.GetCurrentProcess().PriorityClass = playing ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal; } catch { }
        try { SetThreadExecutionState(playing ? EsContinuous | EsSystemRequired : EsContinuous); } catch { }
    }

    // Power throttling of the process: allowed (Windows decides, the default) or refused for the timer resolution and
    // the execution speed (no efficiency mode).
    static void SetPowerThrottling(bool allowed)
    {
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = allowed ? 0 : ExecutionSpeed | IgnoreTimerResolution,
            StateMask = 0,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    const int ProcessPowerThrottling = 4;   // PROCESS_INFORMATION_CLASS
    const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    // Windows DLLs are loaded from the system folder only (never a DLL of the same name next to the program).
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint period);
}
