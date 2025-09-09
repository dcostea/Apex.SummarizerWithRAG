using System.Text.Json.Serialization;

namespace Apex.SummarizerWithRAG.Models;

internal sealed class OllamaModel
{
    public string? Name { get; set; }
    public string? Model { get; set; }
    public string? Digest { get; set; }
    public long? Size { get; set; }

    [JsonPropertyName("modified_at")]
    public string? ModifiedAt { get; set; }
}
