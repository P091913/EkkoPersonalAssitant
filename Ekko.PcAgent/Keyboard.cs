using System.Runtime.InteropServices;

namespace Ekko.PcAgent;

/// <summary>Key presses through SendInput. Names: "pageup", "volume_up", "ctrl+shift+m", "f13", single letters and digits.</summary>
public static class Keyboard
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["esc"] = 0x1B, ["space"] = 0x20,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["insert"] = 0x2D, ["delete"] = 0x2E,
        ["volume_mute"] = 0xAD, ["volume_down"] = 0xAE, ["volume_up"] = 0xAF,
        ["next"] = 0xB0, ["previous"] = 0xB1, ["play_pause"] = 0xB3,
    };

    // Navigation keys must be flagged extended, or Windows delivers the numpad key instead (numpad 9 for page up).
    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B];

    /// <summary>"ctrl+shift+m" -> virtual-key codes in press order. Throws ArgumentException on an unknown name.</summary>
    public static ushort[] Parse(string combo) => combo.Split('+').Select(k => Code(k.Trim())).ToArray();

    private static ushort Code(string name)
    {
        if (Named.TryGetValue(name, out var vk))
            return vk;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
            return char.ToUpperInvariant(name[0]); // VK codes for A-Z and 0-9 are their ASCII codes
        if (name.Length > 1 && name[0] is 'f' or 'F' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return (ushort)(0x6F + f);
        throw new ArgumentException($"unknown key '{name}'");
    }

    /// <summary>All keys down in order, then up in reverse: "ctrl+shift+m" holds ctrl and shift while m goes down.</summary>
    public static void Press(ushort[] keys)
    {
        var inputs = keys.Select(k => Key(k, up: false)).Concat(keys.Reverse().Select(k => Key(k, up: true))).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException($"SendInput was blocked (Windows error {Marshal.GetLastWin32Error()})");
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 0x0002u : 0) | (Extended.Contains(vk) ? 0x0001u : 0) } },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The union must be as large as its biggest member (MOUSEINPUT), or SendInput rejects the struct size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }
}
