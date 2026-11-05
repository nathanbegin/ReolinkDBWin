using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ReolinkDBWin.Services;

/// <summary>Raccourcis clavier globaux (RegisterHotKey), actifs même quand l'application n'a pas le focus.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5100;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("ReolinkDBWinHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Enregistre un raccourci ; retourne false s'il est déjà utilisé par une autre application.</summary>
    public bool Register(Hotkey hotkey, Action action)
    {
        if (hotkey.IsEmpty) return true;
        var id = _nextId++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | ModNoRepeat, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
