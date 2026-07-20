using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ProPdfReader;

public partial class MainWindow : Window
{
    private PdfDocument? _document;
    private string? _currentPath;
    private uint _currentPageIndex;

    public MainWindow()
    {
        InitializeComponent();
        UpdateNavigationState();
    }

    public async Task OpenPdfAsync(string path)
    {
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Only PDF files are supported in this first build.");
            return;
        }

        try
        {
            SetBusy(true);
            SetStatus("Opening PDF...");

            var file = await StorageFile.GetFileFromPathAsync(path);
            var document = await PdfDocument.LoadFromFileAsync(file);

            _document = document;
            _currentPath = path;
            _currentPageIndex = 0;

            FileNameText.Text = Path.GetFileName(path);
            EmptyStateText.Visibility = Visibility.Collapsed;

            await RenderCurrentPageAsync();
            SetStatus(path);
        }
        catch (Exception ex)
        {
            _document = null;
            _currentPath = null;
            PageImage.Source = null;
            EmptyStateText.Visibility = Visibility.Visible;
            SetStatus($"Could not open PDF: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            UpdateNavigationState();
        }
    }

    private async Task RenderCurrentPageAsync()
    {
        if (_document is null)
        {
            return;
        }

        using var page = _document.GetPage(_currentPageIndex);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream);

        var buffer = new byte[stream.Size];
        stream.Seek(0);

        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(buffer);
        }

        using var imageStream = new MemoryStream(buffer);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = imageStream;
        bitmap.EndInit();
        bitmap.Freeze();

        PageImage.Source = bitmap;
        PageHost.Width = Math.Min(Math.Max(page.Size.Width, 520), 1200);
        PageHost.MinHeight = Math.Min(Math.Max(page.Size.Height, 680), 1600);
        UpdateNavigationState();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            Title = "Open PDF"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenPdfAsync(dialog.FileName);
        }
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _currentPageIndex == 0)
        {
            return;
        }

        _currentPageIndex--;
        await RenderPageWithBusyStateAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _currentPageIndex + 1 >= _document.PageCount)
        {
            return;
        }

        _currentPageIndex++;
        await RenderPageWithBusyStateAsync();
    }

    private async Task RenderPageWithBusyStateAsync()
    {
        try
        {
            SetBusy(true);
            await RenderCurrentPageAsync();
            if (_currentPath is not null)
            {
                SetStatus(_currentPath);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not render page: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateNavigationState()
    {
        var hasDocument = _document is not null;
        var pageCount = _document?.PageCount ?? 0;

        PreviousButton.IsEnabled = hasDocument && _currentPageIndex > 0;
        NextButton.IsEnabled = hasDocument && _currentPageIndex + 1 < pageCount;
        PageStatusText.Text = hasDocument ? $"{_currentPageIndex + 1} / {pageCount}" : "No file";
    }

    private void SetBusy(bool isBusy)
    {
        LoadingOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !isBusy;
        PreviousButton.IsEnabled = !isBusy && _document is not null && _currentPageIndex > 0;
        NextButton.IsEnabled = !isBusy && _document is not null && _currentPageIndex + 1 < _document.PageCount;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
