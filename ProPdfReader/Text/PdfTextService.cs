using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Outline;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ProPdfReader.Text;

internal sealed class PdfTextService : IDisposable
{
    private const int MaximumCachedPages = 8;
    private const int MaximumSearchMatches = 10_000;

    private readonly object _gate = new();
    private readonly string _path;
    private string? _password;
    private readonly Dictionary<int, PageText> _cache = [];
    private readonly Queue<int> _cacheOrder = [];

    private PdfPigDocument? _document;
    private bool _isDisposed;

    public PdfTextService(string path, string? password = null)
    {
        _path = path;
        _password = password;
    }

    public Task<PageText> GetPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetPage(pageNumber, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<PdfOutlineItem>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                EnsureDocumentOpen();

                if (!_document!.TryGetBookmarks(out var bookmarks))
                {
                    return [];
                }

                return bookmarks.Roots.Select(CreateOutlineItem).ToArray();
            }
        }, cancellationToken);
    }

    public Task<PdfSearchResult> SearchAsync(
        string query,
        int pageCount,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeSearchText(query);
        if (normalizedQuery.Length == 0)
        {
            return Task.FromResult(new PdfSearchResult([], false));
        }

        return Task.Run(() =>
        {
            var matches = new List<PdfSearchMatch>();

            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = GetPage(pageNumber, cancellationToken);
                if (AddPageMatches(page, (uint)(pageNumber - 1), normalizedQuery, matches))
                {
                    return new PdfSearchResult(matches, true);
                }

                if (pageNumber == pageCount || pageNumber % 8 == 0)
                {
                    progress?.Report(pageNumber);
                }
            }

            return new PdfSearchResult(matches, false);
        }, cancellationToken);
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

            EnsureDocumentOpen();
            var page = _document!.GetPage(pageNumber);
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

            var links = page.GetAnnotations()
                .Where(annotation => annotation.Type == AnnotationType.Link)
                .Select(CreatePageLink)
                .Where(link => link is not null)
                .Cast<PageLink>()
                .ToArray();

            var pageText = new PageText(page.Width, page.Height, words, links);
            AddToCache(pageNumber, pageText);
            return pageText;
        }
    }

    private void EnsureDocumentOpen()
    {
        _document ??= _password is null
            ? PdfPigDocument.Open(_path)
            : PdfPigDocument.Open(
                _path,
                new UglyToad.PdfPig.ParsingOptions { Password = _password });
    }

    private static PageLink? CreatePageLink(Annotation annotation)
    {
        uint? pageIndex = annotation.Action is GoToAction goTo && goTo.Destination.PageNumber > 0
            ? (uint)(goTo.Destination.PageNumber - 1)
            : null;
        var uri = annotation.Action is UriAction uriAction ? uriAction.Uri : null;
        if (!pageIndex.HasValue && string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var rectangle = annotation.Rectangle;
        return new PageLink(
            rectangle.Left,
            rectangle.Bottom,
            rectangle.Right,
            rectangle.Top,
            pageIndex,
            uri);
    }

    private static PdfOutlineItem CreateOutlineItem(BookmarkNode node)
    {
        var pageIndex = node is DocumentBookmarkNode documentNode && documentNode.PageNumber > 0
            ? (uint?)(documentNode.PageNumber - 1)
            : null;
        var uri = node is UriBookmarkNode uriNode ? uriNode.Uri : null;
        return new PdfOutlineItem(
            node.Title,
            pageIndex,
            uri,
            node.Children.Select(CreateOutlineItem).ToArray());
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

    private static bool AddPageMatches(
        PageText page,
        uint pageIndex,
        string query,
        List<PdfSearchMatch> matches)
    {
        if (page.Words.Count == 0)
        {
            return false;
        }

        var wordStarts = new int[page.Words.Count];
        var text = new System.Text.StringBuilder();

        for (var index = 0; index < page.Words.Count; index++)
        {
            if (index > 0)
            {
                text.Append(' ');
            }

            wordStarts[index] = text.Length;
            text.Append(page.Words[index].Text);
        }

        var searchableText = text.ToString();
        var searchIndex = 0;

        while (searchIndex < searchableText.Length)
        {
            var matchIndex = searchableText.IndexOf(
                query,
                searchIndex,
                StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            var startWordIndex = FindWordIndex(wordStarts, matchIndex);
            var endWordIndex = FindWordIndex(wordStarts, matchIndex + query.Length - 1);
            matches.Add(new PdfSearchMatch(pageIndex, startWordIndex, endWordIndex));
            if (matches.Count >= MaximumSearchMatches)
            {
                return true;
            }

            searchIndex = matchIndex + Math.Max(1, query.Length);
        }

        return false;
    }

    private static int FindWordIndex(int[] wordStarts, int characterIndex)
    {
        var result = Array.BinarySearch(wordStarts, characterIndex);
        return result >= 0 ? result : Math.Max(0, ~result - 1);
    }

    private static string NormalizeSearchText(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
            _password = null;
        }
    }
}
