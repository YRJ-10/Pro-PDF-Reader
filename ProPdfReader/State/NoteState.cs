namespace ProPdfReader.State;

internal sealed class NoteState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public uint PageIndex { get; set; }

    public string Text { get; set; } = string.Empty;

    public string SelectedText { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public List<HighlightRectangle> Anchors { get; set; } = [];

    public NoteState Snapshot()
    {
        return new NoteState
        {
            Id = Id,
            PageIndex = PageIndex,
            Text = Text,
            SelectedText = SelectedText,
            CreatedUtc = CreatedUtc,
            UpdatedUtc = UpdatedUtc,
            Anchors = Anchors.Select(anchor => anchor with { }).ToList()
        };
    }

    public void Normalize()
    {
        Text ??= string.Empty;
        SelectedText ??= string.Empty;
        Anchors ??= [];
        Anchors = Anchors
            .Where(anchor => anchor is not null)
            .Where(HighlightState.IsValidRectangle)
            .ToList();
    }
}
