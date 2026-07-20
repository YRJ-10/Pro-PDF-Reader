using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ProPdfReader;

public partial class App : Application
{
    private readonly long _startupTimestamp = Stopwatch.GetTimestamp();

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        if (await HandleIntegrationCommandAsync(e.Args))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (e.Args.Length > 0)
        {
            var path = Path.GetFullPath(e.Args[0]);
            if (File.Exists(path))
            {
                await window.OpenPdfAsync(path, _startupTimestamp);
            }
        }
    }

    private static async Task<bool> HandleIntegrationCommandAsync(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            return false;
        }

        try
        {
            if (arguments[0].Equals("--register", StringComparison.OrdinalIgnoreCase))
            {
                WindowsIntegration.RegisterCurrentUser(WindowsIntegration.GetExecutablePath());
                MessageBox.Show(
                    "Pro PDF Reader is now available in Open with. Choose it for .pdf files in Windows Default Apps.",
                    "Pro PDF Reader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                await WindowsIntegration.OpenDefaultAppsSettingsAsync();
                return true;
            }

            if (arguments[0].Equals("--unregister", StringComparison.OrdinalIgnoreCase))
            {
                WindowsIntegration.UnregisterCurrentUser();
                MessageBox.Show(
                    "The portable Pro PDF Reader registration was removed.",
                    "Pro PDF Reader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return true;
            }

            if (arguments[0].Equals("--default-apps", StringComparison.OrdinalIgnoreCase))
            {
                await WindowsIntegration.OpenDefaultAppsSettingsAsync();
                return true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Windows integration could not be updated.\n\n{ex.Message}",
                "Pro PDF Reader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return true;
        }

        return false;
    }
}
