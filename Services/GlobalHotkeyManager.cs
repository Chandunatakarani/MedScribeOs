using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MedScribeOS.Services;

/// <summary>
/// Registers a single system-wide hotkey using the Win32 RegisterHotKey API.
/// Needs a real window handle to receive WM_HOTKEY messages, so this creates
/// a 0x0 invisible WPF window purely to host that handle - it never shows on
/// screen or in the taskbar.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 9000;

    private readonly Window _messageWindow;
    private readonly HwndSource _source;
    private Action? _callback;

    public GlobalHotkeyManager()
    {
        _messageWindow = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -2000,
            Top = -2000
        };
        _messageWindow.Show();
        _messageWindow.Hide();

        var helper = new WindowInteropHelper(_messageWindow);
        _source = HwndSource.FromHwnd(helper.EnsureHandle())
                  ?? throw new InvalidOperationException("Could not create message-only window handle.");
        _source.AddHook(WndProc);
    }

    public void RegisterHotkey(uint modifiers, uint virtualKey, Action callback)
    {
        _callback = callback;
        var handle = new WindowInteropHelper(_messageWindow).Handle;

        if (!RegisterHotKey(handle, HotkeyId, modifiers, virtualKey))
        {
            throw new InvalidOperationException(
                "Failed to register global hotkey - it may already be in use by another application.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _callback?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        var handle = new WindowInteropHelper(_messageWindow).Handle;
        UnregisterHotKey(handle, HotkeyId);
        _source.RemoveHook(WndProc);
        _messageWindow.Close();
    }
}
