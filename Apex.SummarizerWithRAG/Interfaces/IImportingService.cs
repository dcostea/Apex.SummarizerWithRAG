namespace Apex.SummarizerWithRAG.Interfaces;

public interface IImportingService
{
    Task<string> ImportAsync(Stream content, string fileName, string collection);
}
