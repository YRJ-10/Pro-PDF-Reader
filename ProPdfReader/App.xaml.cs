using System.IO;
using System.Windows;

namespace ProPdfReader;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            await window.OpenPdfAsync(e.Args[0]);
        }
    }
}
