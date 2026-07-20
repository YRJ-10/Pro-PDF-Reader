using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
using ProPdfReader.Viewing;
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
    private string? _currentPassword;
    private DocumentState? _documentState;
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<PdfSearchMatch> _searchMatches = [];
    private bool _searchResultsTruncated;
    private int _searchMatchIndex = -1;
    private string _completedSearchQuery = string.Empty;
    private bool _isSearching;
    private readonly TextSelectionLayer _emptyTextSelectionLayer = new();
    private IReadOnlyList<PdfPageViewModel> _pageModels = [];
    private ScrollViewer? _documentScrollViewer;
    private PdfPageView? _activePageView;
    private bool _isUpdatingViewportPage;
    private bool _isUpdatingRecentDocuments;

    private TextSelectionLayer TextSelectionLayer => _activePageView?.TextLayer ?? _emptyTextSelectionLayer;

    private ScrollViewer DocumentScrollViewer =>
        _documentScrollViewer ?? throw new InvalidOperationException("The document viewport is not ready.");

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
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
        Loaded += MainWindow_Loaded;
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
        HomePanel.Visibility = Visibility.Collapsed;
        SetBusy(true);
        _stateSaveTimer.Stop();
        await SaveCurrentStateAsync(reportFailure: false);
        var generation = BeginDocumentLoad();

        try
        {
            SetStatus("Opening PDF...");

            var stateTask = _stateStore.LoadAsync(path);
            var file = await StorageFile.GetFileFromPathAsync(path);
            var loadedDocument = await LoadPdfDocumentAsync(file);
            if (loadedDocument is null)
            {
                await stateTask;
                ShowOpenFailure("Opening was canceled.");
                return;
            }

            var document = loadedDocument.Document;
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
            _currentPassword = loadedDocument.Password;
            _currentPath = path;
            _currentPageIndex = Math.Min(documentState.LastPageIndex, document.PageCount - 1);
            _documentState = documentState;
            _documentState.LastOpenedUtc = DateTime.UtcNow;
            _documentState.BookmarkedPages.RemoveAll(pageIndex => pageIndex >= document.PageCount);
            _documentState.Highlights.RemoveAll(highlight => highlight.PageIndex >= document.PageCount);
            _documentState.Notes.RemoveAll(note => note.PageIndex >= document.PageCount);

            FileNameText.Text = Path.GetFileName(path);
            Title = $"{Path.GetFileName(path)} - Pro PDF Reader";
            RefreshBookmarksList();
            RefreshNotesList();
            InitializeDocumentPages(document);

            await RenderCurrentPageAsync(generation);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            var elapsed = Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds;
            var restoredPage = _currentPageIndex > 0 ? $" | Restored page {_currentPageIndex + 1}" : string.Empty;
            SetStatus($"Opened in {elapsed:0} ms{restoredPage} | {path}");
            QueueStateSave();
            _ = PrefetchNearbyPagesAsync(generation);
            _ = LoadTextLayerAsync(generation, _currentPageIndex);
            _ = LoadOutlineAsync(generation);
        }
        catch (Exception ex)
        {
            if (generation == _documentGeneration)
            {
                ShowOpenFailure(GetOpenFailureMessage(ex));
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

    private async Task<LoadedPdfDocument?> LoadPdfDocumentAsync(StorageFile file)
    {
        try
        {
            return new LoadedPdfDocument(await PdfDocument.LoadFromFileAsync(file), null);
        }
        catch (Exception ex) when (IsWrongPassword(ex))
        {
        }

        var previousAttemptFailed = false;
        while (true)
        {
            var passwordWindow = new PdfPasswordWindow(file.Name, previousAttemptFailed)
            {
                Owner = this
            };

            if (passwordWindow.ShowDialog() != true)
            {
                return null;
            }

            var password = passwordWindow.Password;
            try
            {
                var document = await PdfDocument.LoadFromFileAsync(file, password);
                return new LoadedPdfDocument(document, password);
            }
            catch (Exception ex) when (IsWrongPassword(ex))
            {
                previousAttemptFailed = true;
            }
        }
    }

    private static bool IsWrongPassword(Exception exception)
    {
        const int errorWrongPassword = unchecked((int)0x8007052B);
        const int errorInvalidPassword = unchecked((int)0x80070056);
        return exception.HResult is errorWrongPassword or errorInvalidPassword ||
               (exception.InnerException is not null && IsWrongPassword(exception.InnerException));
    }

    private void ShowOpenFailure(string message)
    {
        _document = null;
        _currentPath = null;
        _currentPassword = null;
        _documentState = null;
        DocumentPagesList.ItemsSource = null;
        _pageModels = [];
        _activePageView = null;
        ContentsList.Items.Clear();
        ContentsTab.Visibility = Visibility.Collapsed;
        FileNameText.Text = string.Empty;
        Title = "Pro PDF Reader";
        HomeMessageText.Text = message;
        HomePanel.Visibility = Visibility.Visible;
        SetStatus(message);
    }

    private static string GetOpenFailureMessage(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException => "The PDF file could not be found.",
            UnauthorizedAccessException => "Windows denied access to this PDF.",
            InvalidDataException => exception.Message,
            COMException { HResult: unchecked((int)0x8003001E) } =>
                "This PDF is damaged or could not be read completely.",
            _ => "This PDF could not be opened. It may be damaged or use an unsupported format."
        };
    }

    private int BeginDocumentLoad()
    {
        var generation = ++_documentGeneration;
        _qualityRenderTimer.Stop();
        CancelSearch();
        ResetSearchResults();
        var previousTextService = _textService;
        _textService = null;
        _currentPassword = null;
        _documentState = null;
        _emptyTextSelectionLayer.ClearPage();
        _pageBaseWidth = 0;
        _pageBaseHeight = 0;
        _rotation = 0;
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
        var pageView = await RealizePageViewAsync(pageIndex);
        if (pageView is null)
        {
            return;
        }

        _activePageView = pageView;
        var renderedPage = await GetRenderedPageAsync(document, pageIndex, generation);

        if (generation != _documentGeneration || pageIndex != _currentPageIndex)
        {
            return;
        }

        pageView.Model?.SetSourceSize(renderedPage.Width, renderedPage.Height);
        pageView.Model?.UpdateDisplay(GetDisplayScale());
        pageView.SetImage(renderedPage.Image);
        pageView.SetRotation(_rotation);
        _pageBaseWidth = Math.Min(Math.Max(renderedPage.Width, 520), 1200);
        _pageBaseHeight = _pageBaseWidth * renderedPage.Height / renderedPage.Width;
        ApplyPageView();
        pageView.TextLayer.SetPageGeometry(
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
                textService = new PdfTextService(path, _currentPassword);
                _textService = textService;
            }

            var pageText = await textService.GetPageAsync((int)pageIndex + 1);
            var pageView = GetPageView(pageIndex);

            if (generation == _documentGeneration &&
                pageView?.Model?.PageIndex == pageIndex)
            {
                pageView.TextLayer.SetPage(pageText);
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

    private void InitializeDocumentPages(PdfDocument document)
    {
        using var currentPage = document.GetPage(_currentPageIndex);
        var width = currentPage.Size.Width;
        var height = currentPage.Size.Height;
        _pageBaseWidth = Math.Min(Math.Max(width, 520), 1200);
        _pageBaseHeight = _pageBaseWidth * height / width;
        var models = new PdfPageViewModel[document.PageCount];
        for (uint pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            models[pageIndex] = new PdfPageViewModel(pageIndex, width, height);
        }

        _pageModels = models;
        ApplyPageView();
        DocumentPagesList.ItemsSource = models;
        DocumentPagesList.ScrollIntoView(models[_currentPageIndex]);
        DocumentPagesList.UpdateLayout();
    }

    private async Task<PdfPageView?> RealizePageViewAsync(uint pageIndex)
    {
        if (pageIndex >= _pageModels.Count)
        {
            return null;
        }

        var model = _pageModels[(int)pageIndex];
        DocumentPagesList.ScrollIntoView(model);
        await Dispatcher.InvokeAsync(DocumentPagesList.UpdateLayout, DispatcherPriority.Loaded);
        return GetPageView(pageIndex);
    }

    private PdfPageView? GetPageView(uint pageIndex)
    {
        return FindVisualChildren<PdfPageView>(DocumentPagesList)
            .FirstOrDefault(view => view.Model?.PageIndex == pageIndex);
    }

    private IEnumerable<PdfPageView> GetRealizedPageViews()
    {
        return FindVisualChildren<PdfPageView>(DocumentPagesList);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private async void PdfPageView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PdfPageView pageView || pageView.Model is null)
        {
            return;
        }

        AttachPageViewEvents(pageView);
        pageView.SetRotation(_rotation);
        await PreparePageViewAsync(pageView, _documentGeneration);
    }

    private void AttachPageViewEvents(PdfPageView pageView)
    {
        if (pageView.EventsAttached)
        {
            return;
        }

        pageView.AttachEvents();
        pageView.SelectionChanged += PageView_SelectionChanged;
        pageView.HighlightRequested += view => ActivatePageView(view, () => AddHighlightFromSelection(HighlightStyle.Highlight));
        pageView.UnderlineRequested += view => ActivatePageView(view, () => AddHighlightFromSelection(HighlightStyle.Underline));
        pageView.HighlightRemovalRequested += (view, id) => ActivatePageView(view, () => RemoveHighlight(id));
        pageView.NoteRequested += view => ActivatePageView(view, AddNoteFromSelection);
        pageView.NoteEditRequested += (view, id) => ActivatePageView(view, () => EditNote(id));
        pageView.NoteRemovalRequested += (view, id) => ActivatePageView(view, () => RemoveNote(id));
        pageView.LinkRequested += PageView_LinkRequested;
    }

    private void ActivatePageView(PdfPageView pageView, Action action)
    {
        SetActivePageView(pageView);
        action();
    }

    private void PageView_SelectionChanged(PdfPageView pageView)
    {
        if (pageView.TextLayer.HasSelection)
        {
            if (_activePageView is not null && _activePageView != pageView)
            {
                _activePageView.TextLayer.ClearSelection();
            }

            SetActivePageView(pageView);
        }

        UpdateDocumentToolState();
    }

    private async void PageView_LinkRequested(PdfPageView pageView, PageLink link)
    {
        SetActivePageView(pageView);
        if (link.TargetPageIndex is uint pageIndex)
        {
            await NavigateToPageAsync(pageIndex);
        }
        else if (!string.IsNullOrWhiteSpace(link.Uri))
        {
            OpenExternalLink(link.Uri);
        }
    }

    private void OpenExternalLink(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            SetStatus("This PDF link uses an unsupported or unsafe address.");
            return;
        }

        var result = MessageBox.Show(
            $"Open this link in your default application?\n\n{uri}",
            "Open external link",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open link: {ex.Message}");
        }
    }

    private void SetActivePageView(PdfPageView pageView)
    {
        if (pageView.Model is null)
        {
            return;
        }

        _activePageView = pageView;
        SetCurrentPageFromViewport(pageView.Model.PageIndex);
    }

    private async Task PreparePageViewAsync(PdfPageView pageView, int generation)
    {
        var document = _document;
        var model = pageView.Model;
        if (document is null || model is null || generation != _documentGeneration)
        {
            return;
        }

        try
        {
            if (pageView.ImageSource is null)
            {
                var renderedPage = await GetRenderedPageAsync(document, model.PageIndex, generation);
                if (generation != _documentGeneration || pageView.Model != model)
                {
                    return;
                }

                model.SetSourceSize(renderedPage.Width, renderedPage.Height);
                model.UpdateDisplay(GetDisplayScale());
                pageView.SetImage(renderedPage.Image);
                pageView.TextLayer.SetPageGeometry(
                    renderedPage.Width,
                    renderedPage.Height,
                    GetPageHighlights(model.PageIndex),
                    GetPageNotes(model.PageIndex));
            }

            await LoadTextLayerAsync(generation, model.PageIndex);
        }
        catch (Exception ex)
        {
            if (generation == _documentGeneration)
            {
                SetStatus($"Could not render page {model.PageIndex + 1}: {ex.Message}");
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
        await OpenPdfFromDialogAsync();
    }

    private async void HomeOpenButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenPdfFromDialogAsync();
    }

    private async void FileOpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenPdfFromDialogAsync();
    }

    private async void FileSaveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SaveLocalStateAsync();
    }

    private void FileCloseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task OpenPdfFromDialogAsync()
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

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_isBusy && GetDroppedPdfPath(e.Data) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var path = GetDroppedPdfPath(e.Data);
        if (path is null || _isBusy)
        {
            return;
        }

        e.Handled = true;
        await OpenPdfAsync(path);
    }

    private static string? GetDroppedPdfPath(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            return null;
        }

        return Path.GetExtension(files[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;
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

    private void DocumentPagesList_Loaded(object sender, RoutedEventArgs e)
    {
        _documentScrollViewer = FindVisualChildren<ScrollViewer>(DocumentPagesList).FirstOrDefault();
    }

    private void DocumentPagesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scrollViewer)
        {
            _documentScrollViewer = scrollViewer;
        }

        if (_document is null || _isNavigating || _isUpdatingViewportPage)
        {
            return;
        }

        var realizedViews = GetRealizedPageViews().ToArray();
        foreach (var pageView in realizedViews)
        {
            _ = PreparePageViewAsync(pageView, _documentGeneration);
        }

        var viewportCenter = DocumentPagesList.ActualHeight / 2;
        var currentView = realizedViews
            .Select(view => new { View = view, Bounds = GetBoundsInViewport(view) })
            .Where(item => item.Bounds.Bottom > 0 && item.Bounds.Top < DocumentPagesList.ActualHeight)
            .OrderBy(item => Math.Abs(item.Bounds.Top + (item.Bounds.Height / 2) - viewportCenter))
            .Select(item => item.View)
            .FirstOrDefault();

        if (currentView?.Model is not null)
        {
            _activePageView ??= currentView;
            SetCurrentPageFromViewport(currentView.Model.PageIndex);
        }
    }

    private Rect GetBoundsInViewport(FrameworkElement element)
    {
        try
        {
            return element.TransformToAncestor(DocumentPagesList)
                .TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private void SetCurrentPageFromViewport(uint pageIndex)
    {
        if (_document is null || pageIndex == _currentPageIndex)
        {
            return;
        }

        _isUpdatingViewportPage = true;
        try
        {
            _currentPageIndex = pageIndex;
            if (_documentState is not null)
            {
                _documentState.LastPageIndex = pageIndex;
                QueueStateSave();
            }

            UpdateNavigationState();
            SyncBookmarkSelection();
            SetStatus($"Page {pageIndex + 1} | {_currentPath}");
        }
        finally
        {
            _isUpdatingViewportPage = false;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_document is null || e.Delta == 0)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ChangeZoom(e.Delta > 0 ? ZoomStep : -ZoomStep);
            e.Handled = true;
            return;
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
        foreach (var pageView in GetRealizedPageViews())
        {
            pageView.SetRotation(_rotation);
        }

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
        foreach (var model in _pageModels)
        {
            model.UpdateDisplay(scale);
        }

        _viewRevision++;
        QueueQualityRender();
    }

    private void QueueQualityRender()
    {
        if (_document is null || _activePageView?.ImageSource is not BitmapSource)
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
        var pageView = GetPageView(_currentPageIndex) ?? _activePageView;
        if (document is null || _isBusy || _isNavigating || pageView?.ImageSource is not BitmapSource currentImage)
        {
            return;
        }

        var destinationWidth = (uint)Math.Clamp(Math.Ceiling(pageView.ActualWidth), 1, 2400);
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
                pageView.SetImage(renderedPage.Image);
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
            ZoomModeComboBox.SelectedItem = ZoomModeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    double.TryParse(
                        item.Tag?.ToString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var factor) &&
                    Math.Abs(factor - _zoomFactor) < 0.001);
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

    private async Task ScrollOrNavigateAsync(int direction)
    {
        if (direction > 0)
        {
            DocumentScrollViewer.PageDown();
        }
        else
        {
            DocumentScrollViewer.PageUp();
        }

        await Task.CompletedTask;
    }

    private async Task NavigateToPageAsync(uint targetPage)
    {
        var document = _document;
        if (document is null || _isNavigating || targetPage >= document.PageCount || targetPage == _currentPageIndex)
        {
            return;
        }

        _currentPageIndex = targetPage;
        _isNavigating = true;
        var generation = _documentGeneration;
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            SetNavigationBusy(true);
            SetStatus($"Rendering page {_currentPageIndex + 1}...");
            var pageView = await RealizePageViewAsync(targetPage);
            if (pageView is not null)
            {
                _activePageView = pageView;
                await PreparePageViewAsync(pageView, generation);
                pageView.BringIntoView();
            }

            if (generation == _documentGeneration)
            {
                var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                SetStatus($"Page {_currentPageIndex + 1} ready in {elapsed:0} ms | {_currentPath}");
                QueueStateSave();
                SyncBookmarkSelection();
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
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            await OpenPdfFromDialogAsync();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            await SaveLocalStateAsync();

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            Close();
            e.Handled = true;
            return;
        }

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
            AddHighlightFromSelection(HighlightStyle.Highlight);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.U)
        {
            AddHighlightFromSelection(HighlightStyle.Underline);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            AddNoteFromSelection();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Home or Key.End)
        {
            var document = _document;
            if (document is not null)
            {
                var targetPage = e.Key == Key.Home ? 0u : document.PageCount - 1;
                await NavigateToPageAsync(targetPage);
                DocumentScrollViewer.ScrollToTop();
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Home or Key.End)
        {
            if (e.Key == Key.Home)
            {
                DocumentScrollViewer.ScrollToTop();
            }
            else
            {
                DocumentScrollViewer.ScrollToEnd();
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.PageUp or Key.PageDown or Key.Space)
        {
            var scrollDirection = e.Key == Key.PageUp ||
                                  (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                ? -1
                : 1;
            e.Handled = true;
            await ScrollOrNavigateAsync(scrollDirection);
            return;
        }

        var direction = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
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
        UpdateActivityProgress();
        OpenButton.IsEnabled = !isBusy;
        UpdateNavigationState();
    }

    private void SetNavigationBusy(bool isBusy)
    {
        UpdateActivityProgress();
        OpenButton.IsEnabled = !_isBusy && !isBusy;
        UpdateNavigationState();
    }

    private void UpdateActivityProgress()
    {
        ReaderProgressBar.Visibility = _isBusy || _isNavigating || _isSearching
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message;
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
        TextSelectionLayer.Focus();
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
        UpdateActivityProgress();
        UpdateSearchControls();
        SearchStatusText.Text = $"Searching 0/{document.PageCount}";

        try
        {
            var textService = _textService;
            if (textService is null)
            {
                textService = new PdfTextService(path, _currentPassword);
                _textService = textService;
            }

            var progress = new Progress<int>(pageNumber =>
            {
                if (generation == _documentGeneration && !cancellation.IsCancellationRequested)
                {
                    SearchStatusText.Text = $"Searching {pageNumber}/{document.PageCount}";
                }
            });
            var searchResult = await textService.SearchAsync(
                query,
                (int)document.PageCount,
                progress,
                cancellation.Token);

            if (generation != _documentGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }

            _searchMatches = searchResult.Matches;
            _searchResultsTruncated = searchResult.IsTruncated;
            _completedSearchQuery = query;

            if (_searchMatches.Count == 0)
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
                UpdateActivityProgress();
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
        var truncatedSuffix = _searchResultsTruncated ? "+" : string.Empty;
        SearchStatusText.Text = $"{_searchMatchIndex + 1} of {_searchMatches.Count}{truncatedSuffix}";

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
            var pageView = GetPageView(pageIndex);
            if (pageView is not null)
            {
                _activePageView = pageView;
                pageView.TextLayer.SelectRange(match.StartWordIndex, match.EndWordIndex);
            }
        }
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
    }

    private void ResetSearchResults()
    {
        _searchMatches = [];
        _searchResultsTruncated = false;
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

    private async Task LoadOutlineAsync(int generation)
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
                textService = new PdfTextService(path, _currentPassword);
                _textService = textService;
            }

            var outline = await textService.GetOutlineAsync();
            if (generation != _documentGeneration)
            {
                return;
            }

            ContentsList.Items.Clear();
            AddOutlineItems(outline, level: 0);
            ContentsTab.Visibility = ContentsList.Items.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (generation == _documentGeneration)
            {
                SetStatus($"Contents could not be loaded: {ex.Message}");
            }
        }
    }

    private void AddOutlineItems(IEnumerable<PdfOutlineItem> items, int level)
    {
        foreach (var outlineItem in items)
        {
            var item = new ListBoxItem
            {
                Content = outlineItem.Title,
                Tag = outlineItem,
                Margin = new Thickness(level * 14, 0, 0, 0),
                Padding = new Thickness(8, 6, 6, 6),
                IsEnabled = outlineItem.PageIndex.HasValue || !string.IsNullOrWhiteSpace(outlineItem.Uri)
            };
            ContentsList.Items.Add(item);
            AddOutlineItems(outlineItem.Children, level + 1);
        }
    }

    private async void ContentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentsList.SelectedItem is not ListBoxItem { Tag: PdfOutlineItem outlineItem })
        {
            return;
        }

        ContentsList.SelectedItem = null;
        if (outlineItem.PageIndex is uint pageIndex)
        {
            await NavigateToPageAsync(pageIndex);
        }
        else if (!string.IsNullOrWhiteSpace(outlineItem.Uri))
        {
            OpenExternalLink(outlineItem.Uri);
        }
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
        AddHighlightFromSelection(HighlightStyle.Highlight);
    }

    private void UnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        AddHighlightFromSelection(HighlightStyle.Underline);
    }

    private void AddHighlightFromSelection(HighlightStyle style)
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
            Style = style,
            Rectangles = selection.Rectangles.ToList()
        });

        TextSelectionLayer.SetHighlights(GetCurrentPageHighlights());
        TextSelectionLayer.ClearSelection();
        QueueStateSave();
        var annotationName = style == HighlightStyle.Underline ? "Underlined" : "Highlighted";
        SetStatus($"{annotationName} text on page {_currentPageIndex + 1}.");
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
        return GetPageHighlights(_currentPageIndex);
    }

    private IReadOnlyList<NoteState> GetCurrentPageNotes()
    {
        return GetPageNotes(_currentPageIndex);
    }

    private IReadOnlyList<HighlightState> GetPageHighlights(uint pageIndex)
    {
        return _documentState?.Highlights
            .Where(highlight => highlight.PageIndex == pageIndex)
            .ToArray() ?? [];
    }

    private IReadOnlyList<NoteState> GetPageNotes(uint pageIndex)
    {
        return _documentState?.Notes
            .Where(note => note.PageIndex == pageIndex)
            .ToArray() ?? [];
    }

    private void UpdateDocumentToolState()
    {
        var canUseDocumentTools = !_isBusy && !_isNavigating && _documentState is not null;
        FileSaveMenuItem.IsEnabled = canUseDocumentTools;
        BookmarksPaneButton.IsEnabled = canUseDocumentTools;
        BookmarkPageButton.IsEnabled = canUseDocumentTools;
        HighlightButton.IsEnabled = canUseDocumentTools && TextSelectionLayer.HasSelection;
        UnderlineButton.IsEnabled = canUseDocumentTools && TextSelectionLayer.HasSelection;
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

    private async Task<bool> SaveCurrentStateAsync(bool reportFailure)
    {
        var state = _documentState;
        if (state is null)
        {
            return false;
        }

        try
        {
            await _stateStore.SaveAsync(state);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (reportFailure && state == _documentState)
            {
                SetStatus($"Could not save reading position: {ex.Message}");
            }

            return false;
        }
    }

    private async Task SaveLocalStateAsync()
    {
        _stateSaveTimer.Stop();
        if (await SaveCurrentStateAsync(reportFailure: true))
        {
            SetStatus("Bookmarks, highlights, notes, and reading position saved locally.");
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (_isBusy || _currentPath is not null)
        {
            return;
        }

        await RefreshRecentDocumentsAsync();
    }

    private async Task RefreshRecentDocumentsAsync()
    {
        var recentDocuments = await _stateStore.GetRecentAsync();
        if (_currentPath is not null)
        {
            return;
        }

        _isUpdatingRecentDocuments = true;
        try
        {
            RecentDocumentsList.Items.Clear();
            foreach (var document in recentDocuments)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = Path.GetFileName(document.FilePath),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                content.Children.Add(new TextBlock
                {
                    Text = Path.GetDirectoryName(document.FilePath),
                    Margin = new Thickness(0, 3, 0, 0),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("MutedTextBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                RecentDocumentsList.Items.Add(new ListBoxItem
                {
                    Content = content,
                    Tag = document.FilePath,
                    Padding = new Thickness(10, 8, 10, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    ToolTip = document.FilePath
                });
            }

            EmptyRecentText.Visibility = RecentDocumentsList.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _isUpdatingRecentDocuments = false;
        }
    }

    private async void RecentDocumentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingRecentDocuments || _isBusy ||
            RecentDocumentsList.SelectedItem is not ListBoxItem { Tag: string path })
        {
            return;
        }

        RecentDocumentsList.SelectedItem = null;
        await OpenPdfAsync(path);
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

    private sealed record LoadedPdfDocument(PdfDocument Document, string? Password);

    private enum ZoomMode
    {
        FitWidth,
        FitPage,
        Custom
    }
}
