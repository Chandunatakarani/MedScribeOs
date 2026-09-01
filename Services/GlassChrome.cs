using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MedScribeOS.Services;

/// <summary>
/// Window chrome for the light "frosted glass" look. A real see-through
/// acrylic backdrop was tried and dropped: over a dark desktop it turned the
/// translucent panels muddy and hurt legibility. Instead the glass feel comes
/// from opaque near-white surfaces with soft shadows and sheen (Styles.xaml),
/// and this just makes the OS title bar light to match.
/// </summary>
public static class GlassChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var light = 0; // 0 = light title bar, to match the light UI
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref light, sizeof(int));
            }
            catch
            {
                // pre-Win10 1809 - title bar stays default, no harm.
            }
        };
    }
}
