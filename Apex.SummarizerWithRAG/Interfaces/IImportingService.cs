namespace Apex.SummarizerWithRAG.Interfaces;

public interface IImportingService
{
    Task<string> ImportAsync(string filePath, string country);
    Task ImportDirectoryAsync(string directoryPath, string country);
}
