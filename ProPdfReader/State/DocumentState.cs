using System.Text.Json.Serialization;

namespace ProPdfReader.State;

internal sealed class DocumentState
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string FilePath { get; set; } = string.Empty;

    public long FileLength { get; set; }

    public DateTime FileLastWriteUtc { get; set; }

    public uint LastPageIndex { get; set; }

    public DateTime LastOpenedUtc { get; set; }

    public List<uint> BookmarkedPages { get; set; } = [];

    public List<HighlightState> Highlights { get; set; } = [];

    [JsonIgnore]
    public bool IsWritable { get; set; } = true;

    public DocumentState Snapshot()
    {
        return new DocumentState
        {
            SchemaVersion = SchemaVersion,
            FilePath = FilePath,
            FileLength = FileLength,
            FileLastWriteUtc = FileLastWriteUtc,
            LastPageIndex = LastPageIndex,
            LastOpenedUtc = LastOpenedUtc,
            BookmarkedPages = [.. BookmarkedPages],
            Highlights = Highlights.Select(highlight => highlight.Snapshot()).ToList(),
            IsWritable = IsWritable
        };
    }

    public void Normalize()
    {
        BookmarkedPages ??= [];
        Highlights ??= [];

        BookmarkedPages = BookmarkedPages.Distinct().Order().ToList();
        foreach (var highlight in Highlights.Where(highlight => highlight is not null))
        {
            highlight.Normalize();
        }

        Highlights = Highlights
            .Where(highlight => highlight is not null)
            .Where(highlight => highlight.Id != Guid.Empty)
            .Where(highlight => highlight.Rectangles is not null && highlight.Rectangles.Count > 0)
            .ToList();
    }
}
