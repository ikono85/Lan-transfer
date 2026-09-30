using System.Runtime.InteropServices;
using LanLink.Core.Remote;

namespace LanLink.App.Platform;

/// <summary>
/// Injection souris/clavier par SendInput. Ne peut pas agir sur des fenêtres d'un niveau de privilège supérieur
/// (application lancée en administrateur, écran UAC) sauf si LanLink est lui-même lancé en administrateur.
/// </summary>
public sealed class SendInputInjector : IInputInjector
{
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint MouseMove = 0x0001, MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000;
    private const uint MouseWheel = 0x0800, MouseHWheel = 0x1000;
    private const uint KeyExtended = 0x0001, KeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private static readonly Dictionary<string, (uint Down, uint Up, uint Data)> Buttons = new()
    {
        ["left"] = (0x0002, 0x0004, 0),
        ["right"] = (0x0008, 0x0010, 0),
        ["middle"] = (0x0020, 0x0040, 0),
        ["x1"] = (0x0080, 0x0100, 1),
        ["x2"] = (0x0080, 0x0100, 2),
    };

    private readonly object _lock = new();
    private readonly HashSet<string> _pressedButtons = new();
    private readonly Dictionary<int, bool> _pressedKeys = new(); // vk -> étendue

    public void MoveTo(int x, int y)
    {
        // Coordonnées absolues normalisées sur le bureau virtuel (tous les écrans).
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = Math.Max(GetSystemMetrics(78), 2), vh = Math.Max(GetSystemMetrics(79), 2);
        var dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        SendMouse(dx, dy, 0, MouseMove | MouseAbsolute | MouseVirtualDesk);
    }

    public void Button(string button, bool down)
    {
        if (!Buttons.TryGetValue(button, out var flags)) return;
        lock (_lock)
        {
            if (down) _pressedButtons.Add(button); else _pressedButtons.Remove(button);
        }
        SendMouse(0, 0, flags.Data, down ? flags.Down : flags.Up);
    }

    public void Wheel(int delta, bool horizontal) =>
        SendMouse(0, 0, unchecked((uint)delta), horizontal ? MouseHWheel : MouseWheel);

    public void Key(int vk, int scan, bool extended, bool down)
    {
        lock (_lock)
        {
            if (down) _pressedKeys[vk] = extended; else _pressedKeys.Remove(vk);
        }
        SendKey(vk, scan, extended, down);
    }

    public void ReleaseAll()
    {
        List<string> buttons;
        List<KeyValuePair<int, bool>> keys;
        lock (_lock)
        {
            buttons = _pressedButtons.ToList();
            keys = _pressedKeys.ToList();
            _pressedButtons.Clear();
            _pressedKeys.Clear();
        }
        foreach (var b in buttons) SendMouse(0, 0, Buttons[b].Data, Buttons[b].Up);
        foreach (var k in keys) SendKey(k.Key, 0, k.Value, false);
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new Input { Type = InputMouse };
        input.Union.Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    private static void SendKey(int vk, int scan, bool extended, bool down)
    {
        var flags = (extended ? KeyExtended : 0) | (down ? 0 : KeyUp);
        var input = new Input { Type = InputKeyboard };
        input.Union.Keyboard = new KeyboardInput
        {
            Vk = (ushort)vk,
            Scan = (ushort)(scan != 0 ? (uint)scan : MapVirtualKey((uint)vk, 0)),
            Flags = flags,
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    public void Dispose() => ReleaseAll();
}
