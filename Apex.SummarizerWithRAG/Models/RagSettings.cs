namespace Apex.SummarizerWithRAG.Models;

public sealed class RagSettings
{
    public string? IngestionIndex { get; set; }
    public double MinRelevance { get; set; }
    public int Limit { get; set; }
    public int MaxTokens { get; set; }
    public float Temperature { get; set; }
}