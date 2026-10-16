using System.Runtime.InteropServices;
using System.Windows.Input;

namespace PowerMIDI.Behaviors;

// Physical position of the keys (scan codes), the same on every keyboard layout (AZERTY, QWERTY, QWERTZ…):
// the key right of Tab is 0x10 whether it reads A, Q or anything else.
public static class PhysicalKeys
{
    // Loaded from the system folder only (never a DLL of the same name next to the program).
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);
    const uint VkToScan = 0, ScanToVk = 1, VkToChar = 2;

    // Scan code of a WPF key in the current layout (0 if none).
    public static int ScanCode(Key key)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 ? 0 : (int)MapVirtualKey((uint)vk, VkToScan);
    }

    // Character printed on a physical key in the current layout ("Q" for 0x1E on AZERTY, "A" on QWERTY).
    public static string Label(int scanCode)
    {
        uint vk = MapVirtualKey((uint)scanCode, ScanToVk);
        uint ch = vk == 0 ? 0 : MapVirtualKey(vk, VkToChar) & 0x7FFFFFFF;
        return ch == 0 ? "?" : char.ToUpperInvariant((char)ch).ToString();
    }
}
