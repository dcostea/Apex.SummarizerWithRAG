namespace Apex.SummarizerWithRAG.Models;

internal sealed class UploadIngestionResult
{
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public string? DocumentId { get; set; }
    public string? Index { get; set; }
}