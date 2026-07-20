using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.System;

namespace ProPdfReader;

internal static class WindowsIntegration
{
    internal const string RegisteredApplicationName = "Pro PDF Reader";

    private const string ProgId = "ProPdfReader.Pdf";
    private const string CapabilitiesPath = @"Software\ProPdfReader\Capabilities";

    public static void RegisterCurrentUser(string executablePath)
    {
        executablePath = Path.GetFullPath(executablePath);
        var command = $"\"{executablePath}\" \"%1\"";
        var icon = $"\"{executablePath}\",0";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(string.Empty, "Pro PDF Reader Document");
            progId.SetValue("FriendlyTypeName", "Pro PDF Reader Document");
        }

        using (var defaultIcon = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
        {
            defaultIcon.SetValue(string.Empty, icon);
        }

        using (var openCommand = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            openCommand.SetValue(string.Empty, command);
        }

        using (var application = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\ProPdfReader.exe"))
        {
            application.SetValue("FriendlyAppName", RegisteredApplicationName);
        }

        using (var supportedTypes = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\ProPdfReader.exe\SupportedTypes"))
        {
            supportedTypes.SetValue(".pdf", string.Empty);
        }

        using (var applicationCommand = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\ProPdfReader.exe\shell\open\command"))
        {
            applicationCommand.SetValue(string.Empty, command);
        }

        using (var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            capabilities.SetValue("ApplicationName", RegisteredApplicationName);
            capabilities.SetValue("ApplicationDescription", "Fast, lightweight PDF reader for Windows.");
            capabilities.SetValue("ApplicationIcon", icon);
        }

        using (var fileAssociations = Registry.CurrentUser.CreateSubKey($@"{CapabilitiesPath}\FileAssociations"))
        {
            fileAssociations.SetValue(".pdf", ProgId);
        }

        using (var registeredApplications = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registeredApplications.SetValue(RegisteredApplicationName, CapabilitiesPath);
        }

        using (var appPath = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\ProPdfReader.exe"))
        {
            appPath.SetValue(string.Empty, executablePath);
            appPath.SetValue("Path", Path.GetDirectoryName(executablePath) ?? string.Empty);
        }

        NotifyAssociationChanged();
    }

    public static void UnregisterCurrentUser()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\ProPdfReader.exe", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\ProPdfReader", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Microsoft\Windows\CurrentVersion\App Paths\ProPdfReader.exe",
            throwOnMissingSubKey: false);

        using (var openWith = Registry.CurrentUser.OpenSubKey(
                   @"Software\Classes\.pdf\OpenWithProgids",
                   writable: true))
        {
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        using (var registeredApplications = Registry.CurrentUser.OpenSubKey(
                   @"Software\RegisteredApplications",
                   writable: true))
        {
            registeredApplications?.DeleteValue(RegisteredApplicationName, throwOnMissingValue: false);
        }

        NotifyAssociationChanged();
    }

    public static async Task OpenDefaultAppsSettingsAsync()
    {
        var appName = Uri.EscapeDataString(RegisteredApplicationName);
        var launched = await Launcher.LaunchUriAsync(
            new Uri($"ms-settings:defaultapps?registeredAppUser={appName}"));

        if (!launched)
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
            {
                UseShellExecute = true
            });
        }
    }

    public static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine the executable path.");
    }

    private static void NotifyAssociationChanged()
    {
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
