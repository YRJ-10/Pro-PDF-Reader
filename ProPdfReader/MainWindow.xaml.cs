using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ProPdfReader;

public partial class MainWindow : Window
{
    private const int MaximumCachedPages = 5;

    private readonly object _cacheGate = new();
    private readonly Dictionary<uint, CachedPage> _pageCache = [];
    private readonly Dictionary<uint, Task<RenderedPage>> _renderTasks = [];
    private readonly LinkedList<uint> _cacheOrder = [];

    private PdfDocument? _document;
    private string? _currentPath;
    private uint _currentPageIndex;
    private int _documentGeneration;
    private bool _isNavigating;

    public MainWindow()
    {
        InitializeComponent();
        UpdateNavigationState();
    }

    public async Task OpenPdfAsync(string path, long requestStartedAt = 0)
    {
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Only PDF files are supported.");
            return;
        }

        requestStartedAt = requestStartedAt == 0 ? Stopwatch.GetTimestamp() : requestStartedAt;
        var generation = BeginDocumentLoad();

        try
        {
            SetBusy(true);
            SetStatus("Opening PDF...");

            var file = await StorageFile.GetFileFromPathAsync(path);
            var document = await PdfDocument.LoadFromFileAsync(file);

            if (generation != _documentGeneration)
            {
                return;
            }

            _document = document;
            _currentPath = path;
            _currentPageIndex = 0;

            FileNameText.Text = Path.GetFileName(path);
            EmptyStateText.Visibility = Visibility.Collapsed;

            await RenderCurrentPageAsync(generation);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            var elapsed = Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds;
            SetStatus($"Opened in {elapsed:0} ms | {path}");
            _ = PrefetchNearbyPagesAsync(generation);
        }
        catch (Exception ex)
        {
            if (generation == _documentGeneration)
            {
                _document = null;
                _currentPath = null;
                PageImage.Source = null;
                EmptyStateText.Visibility = Visibility.Visible;
                SetStatus($"Could not open PDF: {ex.Message}");
            }
        }
        finally
        {
            if (generation == _documentGeneration)
            {
                SetBusy(false);
                UpdateNavigationState();
            }
        }
    }

    private int BeginDocumentLoad()
    {
        var generation = ++_documentGeneration;

        lock (_cacheGate)
        {
            _pageCache.Clear();
            _renderTasks.Clear();
            _cacheOrder.Clear();
        }

        return generation;
    }

    private async Task RenderCurrentPageAsync(int generation)
    {
        var document = _document;
        if (document is null)
        {
            return;
        }

        var pageIndex = _currentPageIndex;
        var renderedPage = await GetRenderedPageAsync(document, pageIndex, generation);

        if (generation != _documentGeneration || pageIndex != _currentPageIndex)
        {
            return;
        }

        PageImage.Source = renderedPage.Image;
        PageHost.Width = Math.Min(Math.Max(renderedPage.Width, 520), 1200);
        PageHost.MinHeight = Math.Min(Math.Max(renderedPage.Height, 680), 1600);
        UpdateNavigationState();
    }

    private async Task<RenderedPage> GetRenderedPageAsync(
        PdfDocument document,
        uint pageIndex,
        int generation)
    {
        Task<RenderedPage> renderTask;

        lock (_cacheGate)
        {
            if (_pageCache.TryGetValue(pageIndex, out var cachedPage))
            {
                TouchCacheEntry(cachedPage);
                return cachedPage.Page;
            }

            if (!_renderTasks.TryGetValue(pageIndex, out renderTask!))
            {
                renderTask = RenderPageAsync(document, pageIndex);
                _renderTasks[pageIndex] = renderTask;
            }
        }

        try
        {
            var renderedPage = await renderTask;

            lock (_cacheGate)
            {
                if (generation == _documentGeneration && !_pageCache.ContainsKey(pageIndex))
                {
                    AddToCache(pageIndex, renderedPage);
                }
            }

            return renderedPage;
        }
        finally
        {
            lock (_cacheGate)
            {
                if (_renderTasks.TryGetValue(pageIndex, out var currentTask) && currentTask == renderTask)
                {
                    _renderTasks.Remove(pageIndex);
                }
            }
        }
    }

    private static async Task<RenderedPage> RenderPageAsync(PdfDocument document, uint pageIndex)
    {
        using var page = document.GetPage(pageIndex);
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

        return new RenderedPage(bitmap, page.Size.Width, page.Size.Height);
    }

    private void AddToCache(uint pageIndex, RenderedPage page)
    {
        var node = _cacheOrder.AddLast(pageIndex);
        _pageCache[pageIndex] = new CachedPage(page, node);

        while (_pageCache.Count > MaximumCachedPages)
        {
            var oldest = _cacheOrder.First;
            if (oldest is null)
            {
                break;
            }

            _cacheOrder.RemoveFirst();
            _pageCache.Remove(oldest.Value);
        }
    }

    private void TouchCacheEntry(CachedPage cachedPage)
    {
        _cacheOrder.Remove(cachedPage.OrderNode);
        _cacheOrder.AddLast(cachedPage.OrderNode);
    }

    private async Task PrefetchNearbyPagesAsync(int generation)
    {
        var document = _document;
        if (document is null || generation != _documentGeneration)
        {
            return;
        }

        var currentPage = _currentPageIndex;
        var candidates = new List<uint>(2);

        if (currentPage + 1 < document.PageCount)
        {
            candidates.Add(currentPage + 1);
        }

        if (currentPage > 0)
        {
            candidates.Add(currentPage - 1);
        }

        foreach (var pageIndex in candidates)
        {
            if (generation != _documentGeneration)
            {
                return;
            }

            try
            {
                await GetRenderedPageAsync(document, pageIndex, generation);
            }
            catch
            {
                // A failed prefetch must never interrupt reading the current page.
            }
        }
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
        await NavigateAsync(-1);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(1);
    }

    private async Task NavigateAsync(int direction)
    {
        var document = _document;
        if (document is null || _isNavigating)
        {
            return;
        }

        var targetPage = (long)_currentPageIndex + direction;
        if (targetPage < 0 || targetPage >= document.PageCount)
        {
            return;
        }

        _currentPageIndex = (uint)targetPage;
        _isNavigating = true;
        var generation = _documentGeneration;
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            SetNavigationBusy(true);
            SetStatus($"Rendering page {_currentPageIndex + 1}...");
            await RenderCurrentPageAsync(generation);

            if (generation == _documentGeneration)
            {
                var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                SetStatus($"Page {_currentPageIndex + 1} ready in {elapsed:0} ms | {_currentPath}");
                _ = PrefetchNearbyPagesAsync(generation);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not render page: {ex.Message}");
        }
        finally
        {
            _isNavigating = false;
            SetNavigationBusy(false);
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var direction = e.Key switch
        {
            Key.Left or Key.PageUp => -1,
            Key.Right or Key.PageDown or Key.Space => 1,
            _ => 0
        };

        if (direction == 0)
        {
            return;
        }

        e.Handled = true;
        await NavigateAsync(direction);
    }

    private void UpdateNavigationState()
    {
        var hasDocument = _document is not null;
        var pageCount = _document?.PageCount ?? 0;

        PreviousButton.IsEnabled = !_isNavigating && hasDocument && _currentPageIndex > 0;
        NextButton.IsEnabled = !_isNavigating && hasDocument && _currentPageIndex + 1 < pageCount;
        PageStatusText.Text = hasDocument ? $"{_currentPageIndex + 1} / {pageCount}" : "No file";
    }

    private void SetBusy(bool isBusy)
    {
        LoadingOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !isBusy;
        PreviousButton.IsEnabled = !isBusy && _document is not null && _currentPageIndex > 0;
        NextButton.IsEnabled = !isBusy && _document is not null && _currentPageIndex + 1 < _document.PageCount;
    }

    private void SetNavigationBusy(bool isBusy)
    {
        OpenButton.IsEnabled = !isBusy;
        UpdateNavigationState();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private sealed record RenderedPage(BitmapSource Image, double Width, double Height);

    private sealed record CachedPage(RenderedPage Page, LinkedListNode<uint> OrderNode);
}
