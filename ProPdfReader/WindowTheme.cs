using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProPdfReader;

internal static class WindowTheme
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    internal static void ApplyDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyDarkTitleBar(new WindowInteropHelper(window).Handle);
    }

    private static void ApplyDarkTitleBar(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(windowHandle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(windowHandle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
