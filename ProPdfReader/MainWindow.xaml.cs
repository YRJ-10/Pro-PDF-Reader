using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ProPdfReader.Controls;
using ProPdfReader.State;
using ProPdfReader.Text;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ProPdfReader;

public partial class MainWindow : Window
{
    private const int MaximumCachedPages = 5;
    private const double MinimumZoomFactor = 0.5;
    private const double MaximumZoomFactor = 2.0;
    private const double ZoomStep = 0.25;

    private readonly object _cacheGate = new();
    private readonly Dictionary<uint, CachedPage> _pageCache = [];
    private readonly Dictionary<uint, Task<RenderedPage>> _renderTasks = [];
    private readonly LinkedList<uint> _cacheOrder = [];
    private readonly DocumentStateStore _stateStore = new();
    private readonly DispatcherTimer _stateSaveTimer;
    private readonly DispatcherTimer _qualityRenderTimer;

    private PdfDocument? _document;
    private string? _currentPath;
    private uint _currentPageIndex;
    private int _documentGeneration;
    private bool _isNavigating;
    private bool _isBusy;
    private bool _isUpdatingBookmarks;
    private bool _isUpdatingNotes;
    private bool _isUpdatingZoom;
    private double _pageBaseWidth;
    private double _pageBaseHeight;
    private double _zoomFactor = 1;
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private int _rotation;
    private int _viewRevision;
    private bool _isQualityRendering;
    private PdfTextService? _textService;
    private DocumentState? _documentState;
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<PdfSearchMatch> _searchMatches = [];
    private int _searchMatchIndex = -1;
    private string _completedSearchQuery = string.Empty;
    private bool _isSearching;

    public MainWindow()
    {
        InitializeComponent();
        _stateSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _stateSaveTimer.Tick += StateSaveTimer_Tick;
        _qualityRenderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _qualityRenderTimer.Tick += QualityRenderTimer_Tick;
        TextSelectionLayer.SelectionChanged += UpdateDocumentToolState;
        TextSelectionLayer.HighlightRequested += AddHighlightFromSelection;
        TextSelectionLayer.HighlightRemovalRequested += RemoveHighlight;
        TextSelectionLayer.NoteRequested += AddNoteFromSelection;
        TextSelectionLayer.NoteEditRequested += EditNote;
        TextSelectionLayer.NoteRemovalRequested += RemoveNote;
        UpdateSearchControls();
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
            _documentState.BookmarkedPages.RemoveAll(pageIndex => pageIndex >= document.PageCount);
            _documentState.Highlights.RemoveAll(highlight => highlight.PageIndex >= document.PageCount);
            _documentState.Notes.RemoveAll(note => note.PageIndex >= document.PageCount);

            FileNameText.Text = Path.GetFileName(path);
            EmptyStateText.Visibility = Visibility.Collapsed;
            RefreshBookmarksList();
            RefreshNotesList();

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
        _qualityRenderTimer.Stop();
        CancelSearch();
        ResetSearchResults();
        var previousTextService = _textService;
        _textService = null;
        _documentState = null;
        TextSelectionLayer.ClearPage();
        _pageBaseWidth = 0;
        _pageBaseHeight = 0;
        _rotation = 0;
        PageHost.LayoutTransform = Transform.Identity;
        RefreshBookmarksList();
        RefreshNotesList();

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
        _pageBaseWidth = Math.Min(Math.Max(renderedPage.Width, 520), 1200);
        _pageBaseHeight = _pageBaseWidth * renderedPage.Height / renderedPage.Width;
        ApplyPageView();
        TextSelectionLayer.SetPageGeometry(
            renderedPage.Width,
            renderedPage.Height,
            GetCurrentPageHighlights(),
            GetCurrentPageNotes());
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
                ApplyCurrentSearchSelection(pageIndex);
                UpdateDocumentToolState();
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

    private static async Task<RenderedPage> RenderPageAsync(
        PdfDocument document,
        uint pageIndex,
        uint? destinationWidth = null)
    {
        using var page = document.GetPage(pageIndex);
        using var stream = new InMemoryRandomAccessStream();
        if (destinationWidth.HasValue)
        {
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = destinationWidth.Value
            };
            await page.RenderToStreamAsync(stream, options);
        }
        else
        {
            await page.RenderToStreamAsync(stream);
        }

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

    private async void PageNumberTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await NavigateFromPageNumberAsync();
    }

    private void PageNumberTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdatePageNumberText();
    }

    private async Task NavigateFromPageNumberAsync()
    {
        var document = _document;
        if (document is null ||
            !uint.TryParse(PageNumberTextBox.Text, out var pageNumber) ||
            pageNumber == 0 ||
            pageNumber > document.PageCount)
        {
            UpdatePageNumberText();
            return;
        }

        await NavigateToPageAsync(pageNumber - 1);
        Keyboard.ClearFocus();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeZoom(-ZoomStep);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeZoom(ZoomStep);
    }

    private void ZoomModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingZoom || ZoomModeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var tag = item.Tag?.ToString();
        if (tag == "FitWidth")
        {
            _zoomMode = ZoomMode.FitWidth;
        }
        else if (tag == "FitPage")
        {
            _zoomMode = ZoomMode.FitPage;
        }
        else if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
        {
            _zoomMode = ZoomMode.Custom;
            _zoomFactor = Math.Clamp(factor, MinimumZoomFactor, MaximumZoomFactor);
        }

        ApplyPageView();
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        RotateClockwise();
    }

    private void DocumentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomMode is ZoomMode.FitWidth or ZoomMode.FitPage)
        {
            ApplyPageView();
        }
    }

    private void ChangeZoom(double delta)
    {
        if (_document is null)
        {
            return;
        }

        var currentFactor = GetDisplayScale();
        _zoomMode = ZoomMode.Custom;
        _zoomFactor = Math.Clamp(
            Math.Round((currentFactor + delta) / ZoomStep) * ZoomStep,
            MinimumZoomFactor,
            MaximumZoomFactor);
        UpdateZoomComboBox();
        ApplyPageView();
    }

    private void RotateClockwise()
    {
        if (_document is null)
        {
            return;
        }

        _rotation = (_rotation + 90) % 360;
        PageHost.LayoutTransform = _rotation == 0
            ? Transform.Identity
            : new RotateTransform(_rotation);
        ApplyPageView();
        SetStatus($"Rotated to {_rotation} degrees | {_currentPath}");
    }

    private void ApplyPageView()
    {
        if (_pageBaseWidth <= 0 || _pageBaseHeight <= 0)
        {
            return;
        }

        var scale = GetDisplayScale();
        PageHost.Width = _pageBaseWidth * scale;
        PageHost.Height = _pageBaseHeight * scale;
        _viewRevision++;
        QueueQualityRender();
    }

    private void QueueQualityRender()
    {
        if (_document is null || PageImage.Source is not BitmapSource)
        {
            return;
        }

        _qualityRenderTimer.Stop();
        _qualityRenderTimer.Start();
    }

    private async void QualityRenderTimer_Tick(object? sender, EventArgs e)
    {
        _qualityRenderTimer.Stop();

        var document = _document;
        if (document is null || _isBusy || _isNavigating || PageImage.Source is not BitmapSource currentImage)
        {
            return;
        }

        var destinationWidth = (uint)Math.Clamp(Math.Ceiling(PageHost.Width), 1, 2400);
        if (currentImage.PixelWidth >= destinationWidth * 0.95)
        {
            return;
        }

        if (_isQualityRendering)
        {
            _qualityRenderTimer.Start();
            return;
        }

        var generation = _documentGeneration;
        var pageIndex = _currentPageIndex;
        var viewRevision = _viewRevision;
        _isQualityRendering = true;

        try
        {
            var renderedPage = await RenderPageAsync(document, pageIndex, destinationWidth);
            if (generation == _documentGeneration &&
                pageIndex == _currentPageIndex &&
                viewRevision == _viewRevision)
            {
                PageImage.Source = renderedPage.Image;
            }
        }
        catch
        {
            // Display scaling remains usable if an optional quality render fails.
        }
        finally
        {
            _isQualityRendering = false;
            if (generation == _documentGeneration && viewRevision != _viewRevision)
            {
                QueueQualityRender();
            }
        }
    }

    private double GetDisplayScale()
    {
        if (_zoomMode == ZoomMode.Custom)
        {
            return _zoomFactor;
        }

        var availableWidth = Math.Max(160, DocumentScrollViewer.ViewportWidth - 52);
        var availableHeight = Math.Max(160, DocumentScrollViewer.ViewportHeight - 52);
        var isQuarterTurn = _rotation is 90 or 270;
        var layoutWidth = isQuarterTurn ? _pageBaseHeight : _pageBaseWidth;
        var layoutHeight = isQuarterTurn ? _pageBaseWidth : _pageBaseHeight;
        var widthScale = availableWidth / layoutWidth;

        if (_zoomMode == ZoomMode.FitWidth)
        {
            return Math.Clamp(widthScale, 0.1, MaximumZoomFactor);
        }

        return Math.Clamp(
            Math.Min(widthScale, availableHeight / layoutHeight),
            0.1,
            MaximumZoomFactor);
    }

    private void UpdateZoomComboBox()
    {
        _isUpdatingZoom = true;
        try
        {
            ZoomModeComboBox.SelectedItem = null;
            ZoomModeComboBox.Text = $"{_zoomFactor * 100:0}%";
        }
        finally
        {
            _isUpdatingZoom = false;
        }
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

        await NavigateToPageAsync((uint)targetPage);
    }

    private async Task NavigateToPageAsync(uint targetPage)
    {
        var document = _document;
        if (document is null || _isNavigating || targetPage >= document.PageCount || targetPage == _currentPageIndex)
        {
            return;
        }

        _currentPageIndex = targetPage;
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
                SyncBookmarkSelection();
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
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            ShowSearchPane();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            if (_document is not null)
            {
                PageNumberTextBox.Focus();
                PageNumberTextBox.SelectAll();
            }

            e.Handled = true;
            return;
        }

        var zoomInModifiers = Keyboard.Modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            zoomInModifiers == ModifierKeys.None &&
            e.Key is Key.Add or Key.OemPlus)
        {
            ChangeZoom(ZoomStep);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Subtract or Key.OemMinus)
        {
            ChangeZoom(-ZoomStep);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D0)
        {
            SelectZoomMode(ZoomMode.FitWidth);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            ToggleCurrentPageBookmark();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SearchPane.Visibility == Visibility.Visible)
        {
            CloseSearchPane();
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is TextBox or ComboBoxItem)
        {
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.H)
        {
            AddHighlightFromSelection();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            AddNoteFromSelection();
            e.Handled = true;
            return;
        }

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

        PreviousButton.IsEnabled = !_isBusy && !_isNavigating && hasDocument && _currentPageIndex > 0;
        NextButton.IsEnabled = !_isBusy && !_isNavigating && hasDocument && _currentPageIndex + 1 < pageCount;
        PageNumberTextBox.IsEnabled = !_isBusy && !_isNavigating && hasDocument;
        PageCountText.Text = hasDocument ? $"/ {pageCount}" : "/ 0";
        UpdatePageNumberText();
        UpdateDocumentToolState();
    }

    private void UpdatePageNumberText()
    {
        if (!PageNumberTextBox.IsKeyboardFocusWithin)
        {
            PageNumberTextBox.Text = _document is null ? "-" : (_currentPageIndex + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void SelectZoomMode(ZoomMode mode)
    {
        _zoomMode = mode;
        _isUpdatingZoom = true;
        try
        {
            ZoomModeComboBox.SelectedIndex = mode == ZoomMode.FitWidth ? 0 : 1;
        }
        finally
        {
            _isUpdatingZoom = false;
        }

        ApplyPageView();
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        LoadingOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !isBusy;
        UpdateNavigationState();
    }

    private void SetNavigationBusy(bool isBusy)
    {
        OpenButton.IsEnabled = !_isBusy && !isBusy;
        UpdateNavigationState();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void SearchPaneButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSearchPane();
    }

    private void CloseSearchPaneButton_Click(object sender, RoutedEventArgs e)
    {
        CloseSearchPane();
    }

    private async void PreviousSearchResultButton_Click(object sender, RoutedEventArgs e)
    {
        await FindOrMoveAsync(-1);
    }

    private async void NextSearchResultButton_Click(object sender, RoutedEventArgs e)
    {
        await FindOrMoveAsync(1);
    }

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearchPane();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
        await FindOrMoveAsync(direction);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CancelSearch();
        ResetSearchResults();
    }

    private void ShowSearchPane()
    {
        if (_document is null)
        {
            return;
        }

        SearchPane.Visibility = Visibility.Visible;
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    private void CloseSearchPane()
    {
        CancelSearch();
        SearchPane.Visibility = Visibility.Collapsed;
        TextSelectionLayer.ClearSelection();
        Keyboard.Focus(PageHost);
    }

    private async Task FindOrMoveAsync(int direction)
    {
        var query = SearchTextBox.Text.Trim();
        if (_document is null || query.Length == 0 || _isSearching)
        {
            return;
        }

        if (query.Equals(_completedSearchQuery, StringComparison.Ordinal) && _searchMatches.Count > 0)
        {
            await MoveSearchResultAsync(direction);
            return;
        }

        await SearchDocumentAsync(query, direction);
    }

    private async Task SearchDocumentAsync(string query, int direction)
    {
        var document = _document;
        var path = _currentPath;
        if (document is null || path is null)
        {
            return;
        }

        CancelSearch();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var generation = _documentGeneration;
        _isSearching = true;
        UpdateSearchControls();
        SearchStatusText.Text = $"Searching 0/{document.PageCount}";

        try
        {
            var textService = _textService;
            if (textService is null)
            {
                textService = new PdfTextService(path);
                _textService = textService;
            }

            var progress = new Progress<int>(pageNumber =>
            {
                if (generation == _documentGeneration && !cancellation.IsCancellationRequested)
                {
                    SearchStatusText.Text = $"Searching {pageNumber}/{document.PageCount}";
                }
            });
            var matches = await textService.SearchAsync(
                query,
                (int)document.PageCount,
                progress,
                cancellation.Token);

            if (generation != _documentGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }

            _searchMatches = matches;
            _completedSearchQuery = query;

            if (matches.Count == 0)
            {
                _searchMatchIndex = -1;
                SearchStatusText.Text = "No matches";
                TextSelectionLayer.ClearSelection();
                return;
            }

            _searchMatchIndex = FindInitialSearchResult(direction);
            await ActivateSearchResultAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _documentGeneration)
            {
                SearchStatusText.Text = "Search failed";
                SetStatus($"Could not search this PDF: {ex.Message}");
            }
        }
        finally
        {
            if (_searchCancellation == cancellation)
            {
                _isSearching = false;
                _searchCancellation = null;
                UpdateSearchControls();
            }

            cancellation.Dispose();
        }
    }

    private int FindInitialSearchResult(int direction)
    {
        if (direction < 0)
        {
            for (var index = _searchMatches.Count - 1; index >= 0; index--)
            {
                if (_searchMatches[index].PageIndex <= _currentPageIndex)
                {
                    return index;
                }
            }

            return _searchMatches.Count - 1;
        }

        for (var index = 0; index < _searchMatches.Count; index++)
        {
            if (_searchMatches[index].PageIndex >= _currentPageIndex)
            {
                return index;
            }
        }

        return 0;
    }

    private async Task MoveSearchResultAsync(int direction)
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _searchMatchIndex = (_searchMatchIndex + direction + _searchMatches.Count) % _searchMatches.Count;
        await ActivateSearchResultAsync();
    }

    private async Task ActivateSearchResultAsync()
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count)
        {
            return;
        }

        var match = _searchMatches[_searchMatchIndex];
        SearchStatusText.Text = $"{_searchMatchIndex + 1} of {_searchMatches.Count}";

        if (match.PageIndex != _currentPageIndex)
        {
            await NavigateToPageAsync(match.PageIndex);
        }

        ApplyCurrentSearchSelection(_currentPageIndex);
        UpdateSearchControls();
    }

    private void ApplyCurrentSearchSelection(uint pageIndex)
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count)
        {
            return;
        }

        var match = _searchMatches[_searchMatchIndex];
        if (match.PageIndex == pageIndex)
        {
            TextSelectionLayer.SelectRange(match.StartWordIndex, match.EndWordIndex);
        }
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
    }

    private void ResetSearchResults()
    {
        _searchMatches = [];
        _searchMatchIndex = -1;
        _completedSearchQuery = string.Empty;
        SearchStatusText.Text = string.Empty;
        UpdateSearchControls();
    }

    private void UpdateSearchControls()
    {
        var canNavigateResults = !_isSearching && _searchMatches.Count > 0;
        PreviousSearchResultButton.IsEnabled = canNavigateResults;
        NextSearchResultButton.IsEnabled = !_isSearching && _document is not null &&
                                           (canNavigateResults || !string.IsNullOrWhiteSpace(SearchTextBox.Text));
        SearchTextBox.IsEnabled = !_isBusy && _document is not null;
    }

    private void BookmarksPaneButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSidePane(tabIndex: 0);
    }

    private void NotesPaneButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSidePane(tabIndex: 1);
    }

    private void ShowSidePane(int tabIndex)
    {
        var isCurrentTabVisible = SidePane.Visibility == Visibility.Visible &&
                                  SidePaneTabs.SelectedIndex == tabIndex;
        SidePane.Visibility = isCurrentTabVisible ? Visibility.Collapsed : Visibility.Visible;
        SidePaneTabs.SelectedIndex = tabIndex;

        if (SidePane.Visibility == Visibility.Visible)
        {
            RefreshBookmarksList();
            RefreshNotesList();
        }
    }

    private void CloseSidePaneButton_Click(object sender, RoutedEventArgs e)
    {
        SidePane.Visibility = Visibility.Collapsed;
    }

    private void BookmarkPageButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCurrentPageBookmark();
    }

    private void ToggleCurrentPageBookmark()
    {
        if (_documentState is null || _document is null || _isBusy || _isNavigating)
        {
            return;
        }

        var bookmarkIndex = _documentState.BookmarkedPages.IndexOf(_currentPageIndex);
        if (bookmarkIndex >= 0)
        {
            _documentState.BookmarkedPages.RemoveAt(bookmarkIndex);
            SetStatus($"Removed bookmark from page {_currentPageIndex + 1}.");
        }
        else
        {
            _documentState.BookmarkedPages.Add(_currentPageIndex);
            _documentState.BookmarkedPages.Sort();
            SetStatus($"Bookmarked page {_currentPageIndex + 1}.");
        }

        RefreshBookmarksList();
        UpdateDocumentToolState();
        QueueStateSave();
    }

    private async void BookmarksList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isUpdatingBookmarks || BookmarksList.SelectedItem is not System.Windows.Controls.ListBoxItem item)
        {
            return;
        }

        if (item.Tag is uint pageIndex)
        {
            await NavigateToPageAsync(pageIndex);
        }
    }

    private void RefreshBookmarksList()
    {
        _isUpdatingBookmarks = true;
        try
        {
            BookmarksList.Items.Clear();

            if (_documentState is not null)
            {
                foreach (var pageIndex in _documentState.BookmarkedPages.Order())
                {
                    var item = new System.Windows.Controls.ListBoxItem
                    {
                        Content = $"Page {pageIndex + 1}",
                        Tag = pageIndex,
                        Padding = new Thickness(10, 8, 10, 8)
                    };
                    BookmarksList.Items.Add(item);
                }
            }

            SyncBookmarkSelection();
            EmptyBookmarksText.Visibility = BookmarksList.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _isUpdatingBookmarks = false;
        }
    }

    private void SyncBookmarkSelection()
    {
        _isUpdatingBookmarks = true;
        try
        {
            BookmarksList.SelectedItem = BookmarksList.Items
                .OfType<System.Windows.Controls.ListBoxItem>()
                .FirstOrDefault(item => item.Tag is uint pageIndex && pageIndex == _currentPageIndex);
        }
        finally
        {
            _isUpdatingBookmarks = false;
        }
    }

    private void HighlightButton_Click(object sender, RoutedEventArgs e)
    {
        AddHighlightFromSelection();
    }

    private void AddHighlightFromSelection()
    {
        var selection = TextSelectionLayer.GetSelection();
        if (_documentState is null || selection is null || _isBusy || _isNavigating)
        {
            return;
        }

        _documentState.Highlights.Add(new HighlightState
        {
            PageIndex = _currentPageIndex,
            Text = selection.Text,
            Rectangles = selection.Rectangles.ToList()
        });

        TextSelectionLayer.SetHighlights(GetCurrentPageHighlights());
        TextSelectionLayer.ClearSelection();
        QueueStateSave();
        SetStatus($"Highlighted text on page {_currentPageIndex + 1}.");
    }

    private void RemoveHighlight(Guid highlightId)
    {
        if (_documentState is null)
        {
            return;
        }

        var removedCount = _documentState.Highlights.RemoveAll(highlight => highlight.Id == highlightId);
        if (removedCount == 0)
        {
            return;
        }

        TextSelectionLayer.SetHighlights(GetCurrentPageHighlights());
        QueueStateSave();
        SetStatus($"Removed highlight from page {_currentPageIndex + 1}.");
    }

    private void NoteButton_Click(object sender, RoutedEventArgs e)
    {
        AddNoteFromSelection();
    }

    private void AddNoteFromSelection()
    {
        var selection = TextSelectionLayer.GetSelection();
        if (_documentState is null || selection is null || _isBusy || _isNavigating)
        {
            return;
        }

        var editor = new NoteEditorWindow(selection.Text)
        {
            Owner = this
        };

        if (editor.ShowDialog() != true)
        {
            return;
        }

        var note = new NoteState
        {
            PageIndex = _currentPageIndex,
            Text = editor.NoteText,
            SelectedText = selection.Text,
            Anchors = selection.Rectangles.ToList()
        };
        _documentState.Notes.Add(note);

        TextSelectionLayer.SetNotes(GetCurrentPageNotes());
        TextSelectionLayer.ClearSelection();
        RefreshNotesList(note.Id);
        SidePane.Visibility = Visibility.Visible;
        SidePaneTabs.SelectedIndex = 1;
        QueueStateSave();
        SetStatus($"Added note on page {_currentPageIndex + 1}.");
    }

    private void EditNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedNoteId() is Guid noteId)
        {
            EditNote(noteId);
        }
    }

    private void DeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedNoteId() is Guid noteId)
        {
            RemoveNote(noteId);
        }
    }

    private void NotesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetSelectedNoteId() is Guid noteId)
        {
            EditNote(noteId);
        }
    }

    private async void NotesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateNoteListButtonState();

        if (_isUpdatingNotes || GetSelectedNoteId() is not Guid noteId || _documentState is null)
        {
            return;
        }

        var note = _documentState.Notes.FirstOrDefault(candidate => candidate.Id == noteId);
        if (note is not null)
        {
            await NavigateToPageAsync(note.PageIndex);
        }
    }

    private void EditNote(Guid noteId)
    {
        var note = _documentState?.Notes.FirstOrDefault(candidate => candidate.Id == noteId);
        if (note is null)
        {
            return;
        }

        var editor = new NoteEditorWindow(note.SelectedText, note.Text, isEditing: true)
        {
            Owner = this
        };

        if (editor.ShowDialog() != true)
        {
            return;
        }

        note.Text = editor.NoteText;
        note.UpdatedUtc = DateTime.UtcNow;
        RefreshNotesList(note.Id);
        QueueStateSave();
        SetStatus($"Updated note on page {note.PageIndex + 1}.");
    }

    private void RemoveNote(Guid noteId)
    {
        if (_documentState is null)
        {
            return;
        }

        var note = _documentState.Notes.FirstOrDefault(candidate => candidate.Id == noteId);
        if (note is null)
        {
            return;
        }

        _documentState.Notes.Remove(note);
        TextSelectionLayer.SetNotes(GetCurrentPageNotes());
        RefreshNotesList();
        QueueStateSave();
        SetStatus($"Removed note from page {note.PageIndex + 1}.");
    }

    private void RefreshNotesList(Guid? selectedNoteId = null)
    {
        _isUpdatingNotes = true;
        try
        {
            NotesList.Items.Clear();

            if (_documentState is not null)
            {
                foreach (var note in _documentState.Notes.OrderBy(note => note.PageIndex).ThenByDescending(note => note.UpdatedUtc))
                {
                    var content = new StackPanel();
                    content.Children.Add(new TextBlock
                    {
                        Text = $"Page {note.PageIndex + 1}",
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = System.Windows.Media.Brushes.DimGray
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text = note.Text,
                        Margin = new Thickness(0, 3, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = note.Text
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text = note.SelectedText,
                        Margin = new Thickness(0, 3, 0, 0),
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Foreground = System.Windows.Media.Brushes.Gray,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    var item = new System.Windows.Controls.ListBoxItem
                    {
                        Content = content,
                        Tag = note.Id,
                        Padding = new Thickness(8),
                        HorizontalContentAlignment = HorizontalAlignment.Stretch
                    };
                    NotesList.Items.Add(item);

                    if (note.Id == selectedNoteId)
                    {
                        NotesList.SelectedItem = item;
                    }
                }
            }

            EmptyNotesText.Visibility = NotesList.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _isUpdatingNotes = false;
        }

        UpdateNoteListButtonState();
    }

    private Guid? GetSelectedNoteId()
    {
        return NotesList.SelectedItem is System.Windows.Controls.ListBoxItem { Tag: Guid noteId }
            ? noteId
            : null;
    }

    private void UpdateNoteListButtonState()
    {
        var canEditSelectedNote = !_isBusy && !_isNavigating && GetSelectedNoteId().HasValue;
        EditNoteButton.IsEnabled = canEditSelectedNote;
        DeleteNoteButton.IsEnabled = canEditSelectedNote;
    }

    private IReadOnlyList<HighlightState> GetCurrentPageHighlights()
    {
        return _documentState?.Highlights
            .Where(highlight => highlight.PageIndex == _currentPageIndex)
            .ToArray() ?? [];
    }

    private IReadOnlyList<NoteState> GetCurrentPageNotes()
    {
        return _documentState?.Notes
            .Where(note => note.PageIndex == _currentPageIndex)
            .ToArray() ?? [];
    }

    private void UpdateDocumentToolState()
    {
        var canUseDocumentTools = !_isBusy && !_isNavigating && _documentState is not null;
        BookmarksPaneButton.IsEnabled = canUseDocumentTools;
        BookmarkPageButton.IsEnabled = canUseDocumentTools;
        HighlightButton.IsEnabled = canUseDocumentTools && TextSelectionLayer.HasSelection;
        NoteButton.IsEnabled = canUseDocumentTools && TextSelectionLayer.HasSelection;
        NotesPaneButton.IsEnabled = canUseDocumentTools;
        ZoomOutButton.IsEnabled = canUseDocumentTools;
        ZoomInButton.IsEnabled = canUseDocumentTools;
        ZoomModeComboBox.IsEnabled = canUseDocumentTools;
        RotateButton.IsEnabled = canUseDocumentTools;
        SearchPaneButton.IsEnabled = canUseDocumentTools;
        UpdateSearchControls();
        UpdateNoteListButtonState();

        var isBookmarked = _documentState?.BookmarkedPages.Contains(_currentPageIndex) == true;
        BookmarkPageButton.Content = isBookmarked ? "\uE735" : "\uE734";
        BookmarkPageButton.ToolTip = isBookmarked ? "Remove page bookmark" : "Bookmark this page";
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
        CancelSearch();
        _qualityRenderTimer.Stop();
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

    private enum ZoomMode
    {
        FitWidth,
        FitPage,
        Custom
    }
}
