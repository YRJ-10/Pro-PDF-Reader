using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ProPdfReader.State;
using ProPdfReader.Text;
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
    private readonly DocumentStateStore _stateStore = new();
    private readonly DispatcherTimer _stateSaveTimer;

    private PdfDocument? _document;
    private string? _currentPath;
    private uint _currentPageIndex;
    private int _documentGeneration;
    private bool _isNavigating;
    private PdfTextService? _textService;
    private DocumentState? _documentState;

    public MainWindow()
    {
        InitializeComponent();
        _stateSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _stateSaveTimer.Tick += StateSaveTimer_Tick;
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
        SetBusy(true);
        _stateSaveTimer.Stop();
        await SaveCurrentStateAsync(reportFailure: false);
        var generation = BeginDocumentLoad();

        try
        {
            SetStatus("Opening PDF...");

            var stateTask = _stateStore.LoadAsync(path);
            var file = await StorageFile.GetFileFromPathAsync(path);
            var document = await PdfDocument.LoadFromFileAsync(file);
            var documentState = await stateTask;

            if (generation != _documentGeneration)
            {
                return;
            }

            if (document.PageCount == 0)
            {
                throw new InvalidDataException("The PDF contains no pages.");
            }

            _document = document;
            _currentPath = path;
            _currentPageIndex = Math.Min(documentState.LastPageIndex, document.PageCount - 1);
            _documentState = documentState;
            _documentState.LastOpenedUtc = DateTime.UtcNow;

            FileNameText.Text = Path.GetFileName(path);
            EmptyStateText.Visibility = Visibility.Collapsed;

            await RenderCurrentPageAsync(generation);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            var elapsed = Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds;
            var restoredPage = _currentPageIndex > 0 ? $" | Restored page {_currentPageIndex + 1}" : string.Empty;
            SetStatus($"Opened in {elapsed:0} ms{restoredPage} | {path}");
            QueueStateSave();
            _ = PrefetchNearbyPagesAsync(generation);
            _ = LoadTextLayerAsync(generation, _currentPageIndex);
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
        var previousTextService = _textService;
        _textService = null;
        _documentState = null;
        TextSelectionLayer.ClearPage();

        if (previousTextService is not null)
        {
            _ = Task.Run(previousTextService.Dispose);
        }

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
        var displayWidth = Math.Min(Math.Max(renderedPage.Width, 520), 1200);
        PageHost.Width = displayWidth;
        PageHost.Height = displayWidth * renderedPage.Height / renderedPage.Width;
        UpdateNavigationState();
    }

    private async Task LoadTextLayerAsync(int generation, uint pageIndex)
    {
        var path = _currentPath;
        if (path is null || generation != _documentGeneration)
        {
            return;
        }

        try
        {
            var textService = _textService;
            if (textService is null)
            {
                textService = new PdfTextService(path);
                _textService = textService;
            }

            var pageText = await textService.GetPageAsync((int)pageIndex + 1);

            if (generation == _documentGeneration && pageIndex == _currentPageIndex)
            {
                TextSelectionLayer.SetPage(pageText);
            }
        }
        catch (ObjectDisposedException)
        {
            // The user opened another document while text extraction was running.
        }
        catch (Exception ex)
        {
            if (generation == _documentGeneration && pageIndex == _currentPageIndex)
            {
                SetStatus($"Text selection is unavailable on this page: {ex.Message}");
            }
        }
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
        TextSelectionLayer.ClearPage();
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
                QueueStateSave();
                _ = PrefetchNearbyPagesAsync(generation);
                _ = LoadTextLayerAsync(generation, _currentPageIndex);
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

    private void QueueStateSave()
    {
        if (_documentState is null)
        {
            return;
        }

        _documentState.LastPageIndex = _currentPageIndex;
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private async void StateSaveTimer_Tick(object? sender, EventArgs e)
    {
        _stateSaveTimer.Stop();
        await SaveCurrentStateAsync(reportFailure: true);
    }

    private async Task SaveCurrentStateAsync(bool reportFailure)
    {
        var state = _documentState;
        if (state is null)
        {
            return;
        }

        try
        {
            await _stateStore.SaveAsync(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (reportFailure && state == _documentState)
            {
                SetStatus($"Could not save reading position: {ex.Message}");
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _stateSaveTimer.Stop();

        if (_documentState is not null)
        {
            try
            {
                _stateStore.SaveAsync(_documentState).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var textService = _textService;
        _textService = null;

        if (textService is not null)
        {
            _ = Task.Run(textService.Dispose);
        }

        base.OnClosed(e);
    }

    private sealed record RenderedPage(BitmapSource Image, double Width, double Height);

    private sealed record CachedPage(RenderedPage Page, LinkedListNode<uint> OrderNode);
}
