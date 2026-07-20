using System.Text.Json.Serialization;

namespace ProPdfReader.State;

internal sealed class DocumentState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string FilePath { get; set; } = string.Empty;

    public long FileLength { get; set; }

    public DateTime FileLastWriteUtc { get; set; }

    public uint LastPageIndex { get; set; }

    public DateTime LastOpenedUtc { get; set; }

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
            IsWritable = IsWritable
        };
    }
}
