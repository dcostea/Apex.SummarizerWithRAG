namespace Apex.SummarizerWithRAG.Interfaces;

public interface IImportingService
{
    Task<string> ImportAsync(string filePath, string country);
    Task<string> ImportAsync(Stream content, string fileName, string country);
    Task ImportDirectoryAsync(string directoryPath, string country);
}
