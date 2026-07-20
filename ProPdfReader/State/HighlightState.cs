namespace ProPdfReader.State;

internal sealed class HighlightState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public uint PageIndex { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public HighlightStyle Style { get; set; }

    public List<HighlightRectangle> Rectangles { get; set; } = [];

    public HighlightState Snapshot()
    {
        return new HighlightState
        {
            Id = Id,
            PageIndex = PageIndex,
            Text = Text,
            CreatedUtc = CreatedUtc,
            Style = Style,
            Rectangles = Rectangles.Select(rectangle => rectangle with { }).ToList()
        };
    }

    public void Normalize()
    {
        Text ??= string.Empty;
        Rectangles ??= [];
        Rectangles = Rectangles
            .Where(rectangle => rectangle is not null)
            .Where(IsValidRectangle)
            .ToList();
    }

    internal static bool IsValidRectangle(HighlightRectangle rectangle)
    {
        return double.IsFinite(rectangle.Left) &&
               double.IsFinite(rectangle.Bottom) &&
               double.IsFinite(rectangle.Right) &&
               double.IsFinite(rectangle.Top) &&
               rectangle.Right > rectangle.Left &&
               rectangle.Top > rectangle.Bottom;
    }
}

internal enum HighlightStyle
{
    Highlight,
    Underline
}

internal sealed record HighlightRectangle(
    double Left,
    double Bottom,
    double Right,
    double Top);
