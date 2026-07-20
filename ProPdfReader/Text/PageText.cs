namespace ProPdfReader.Text;

internal sealed record PageText(
    double Width,
    double Height,
    IReadOnlyList<PageWord> Words,
    IReadOnlyList<PageLink> Links);

internal sealed record PageWord(
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top)
{
    public double Height => Top - Bottom;
}

internal sealed record PdfSearchMatch(
    uint PageIndex,
    int StartWordIndex,
    int EndWordIndex);

internal sealed record PdfSearchResult(
    IReadOnlyList<PdfSearchMatch> Matches,
    bool IsTruncated);

internal sealed record PageLink(
    double Left,
    double Bottom,
    double Right,
    double Top,
    uint? TargetPageIndex,
    string? Uri);

internal sealed record PdfOutlineItem(
    string Title,
    uint? PageIndex,
    string? Uri,
    IReadOnlyList<PdfOutlineItem> Children);
