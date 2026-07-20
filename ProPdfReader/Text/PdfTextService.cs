using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ProPdfReader.Text;

internal sealed class PdfTextService : IDisposable
{
    private const int MaximumCachedPages = 8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<int, PageText> _cache = [];
    private readonly Queue<int> _cacheOrder = [];

    private PdfPigDocument? _document;
    private bool _isDisposed;

    public PdfTextService(string path)
    {
        _path = path;
    }

    public Task<PageText> GetPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetPage(pageNumber, cancellationToken), cancellationToken);
    }

    private PageText GetPage(int pageNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_cache.TryGetValue(pageNumber, out var cachedPage))
            {
                return cachedPage;
            }

            _document ??= PdfPigDocument.Open(_path);
            var page = _document.GetPage(pageNumber);
            var words = NearestNeighbourWordExtractor.Instance
                .GetWords(page.Letters)
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .Select(word => new PageWord(
                    word.Text,
                    word.BoundingBox.Left,
                    word.BoundingBox.Bottom,
                    word.BoundingBox.Right,
                    word.BoundingBox.Top))
                .ToArray();

            var pageText = new PageText(page.Width, page.Height, words);
            AddToCache(pageNumber, pageText);
            return pageText;
        }
    }

    private void AddToCache(int pageNumber, PageText pageText)
    {
        _cache[pageNumber] = pageText;
        _cacheOrder.Enqueue(pageNumber);

        while (_cache.Count > MaximumCachedPages)
        {
            _cache.Remove(_cacheOrder.Dequeue());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _cache.Clear();
            _cacheOrder.Clear();
            _document?.Dispose();
            _document = null;
        }
    }
}
