using System.Text.Json.Serialization;

namespace ProPdfReader.State;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(DocumentState))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
