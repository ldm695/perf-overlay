using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;

namespace PerfOverlay.Interop;

public sealed class GlobalHotKey : IDisposable
{
    public const string DefaultShortcut = "CTRL+ALT+F4";

    private readonly int _hotKeyId;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private uint _modifiers;
    private uint _virtualKey;
    private bool _registered;

    public event EventHandler? ToggleRequested;

    public GlobalHotKey(IntPtr windowHandle, string shortcut, int hotKeyId = 0x2F10)
    {
        _windowHandle = windowHandle;
        _hotKeyId = hotKeyId;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("Cannot connect to the overlay window message source.");

        if (!TryParseShortcut(shortcut, out var modifiers, out var virtualKey))
            throw new ArgumentException("The global shortcut is invalid.", nameof(shortcut));

        if (!RegisterHotKey(_windowHandle, _hotKeyId, modifiers | ModNoRepeat, virtualKey))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The global shortcut is already in use or Windows rejected it.");

        _modifiers = modifiers;
        _virtualKey = virtualKey;
        _registered = true;
        _source.AddHook(WindowHook);
    }

    public bool TrySetShortcut(string shortcut, out string errorMessage)
    {
        errorMessage = "";
        if (!TryParseShortcut(shortcut, out var nextModifiers, out var nextVirtualKey))
        {
            errorMessage = "Enter a key plus at least one modifier (CTRL, ALT, SHIFT, or WIN).";
            return false;
        }

        if (_registered && nextModifiers == _modifiers && nextVirtualKey == _virtualKey)
            return true;

        if (_registered)
        {
            UnregisterHotKey(_windowHandle, _hotKeyId);
            _registered = false;
        }

        if (RegisterHotKey(_windowHandle, _hotKeyId, nextModifiers | ModNoRepeat, nextVirtualKey))
        {
            _modifiers = nextModifiers;
            _virtualKey = nextVirtualKey;
            _registered = true;
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (RegisterHotKey(_windowHandle, _hotKeyId, _modifiers | ModNoRepeat, _virtualKey))
            _registered = true;

        errorMessage = new Win32Exception(error, "The shortcut is already in use or Windows rejected it.").Message;
        return false;
    }

    public static bool TryFormatShortcut(ModifierKeys modifiers, Key key, out string shortcut)
    {
        shortcut = "";
        if (key == Key.System)
            return false;

        var parts = new List<string>(5);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("CTRL");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("ALT");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("SHIFT");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("WIN");
        if (parts.Count == 0 || IsModifierKey(key))
            return false;

        parts.Add(FormatKey(key));
        shortcut = string.Join('+', parts);
        return TryParseShortcut(shortcut, out _, out _);
    }

    private static bool TryParseShortcut(string shortcut, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(shortcut))
            return false;

        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var flag = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModControl,
                "ALT" => ModAlt,
                "SHIFT" => ModShift,
                "WIN" or "WINDOWS" => ModWin,
                _ => 0u
            };
            if (flag == 0 || (modifiers & flag) != 0)
                return false;
            modifiers |= flag;
        }

        if (modifiers == 0 || !TryParseKey(parts[^1], out var key) || IsModifierKey(key))
            return false;

        var virtualKeyValue = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKeyValue <= 0)
            return false;
        virtualKey = (uint)virtualKeyValue;
        return true;
    }

    private static bool TryParseKey(string value, out Key key)
    {
        key = Key.None;
        if (value.Length == 1 && char.IsAsciiDigit(value[0]))
        {
            key = (Key)((int)Key.D0 + (value[0] - '0'));
            return true;
        }

        if (value.Length == 4 && value.StartsWith("NUM", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(value[3]))
        {
            key = (Key)((int)Key.NumPad0 + (value[3] - '0'));
            return true;
        }

        return Enum.TryParse(value, true, out key) && key != Key.None;
    }

    private static string FormatKey(Key key)
    {
        if ((int)key >= (int)Key.D0 && (int)key <= (int)Key.D9)
            return ((int)key - (int)Key.D0).ToString();
        if ((int)key >= (int)Key.NumPad0 && (int)key <= (int)Key.NumPad9)
            return $"NUM{(int)key - (int)Key.NumPad0}";
        return key.ToString().ToUpperInvariant();
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == _hotKeyId)
        {
            handled = true;
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WindowHook);
        if (_registered)
        {
            UnregisterHotKey(_windowHandle, _hotKeyId);
            _registered = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
