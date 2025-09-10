namespace Apex.SummarizerWithRAG.Models;

public class TextPartitioningSettings
{
    public int MaxTokensPerParagraph { get; set; }
    public int OverlappingTokens { get; set; }
}