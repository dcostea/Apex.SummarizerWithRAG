using Apex.SummarizerWithRAG.Interfaces;
using Microsoft.KernelMemory;
using Serilog;
using Apex.SummarizerWithRAG.Models;
using Microsoft.Extensions.Options;
using Microsoft.KernelMemory.MemoryDb.Elasticsearch;

namespace Apex.SummarizerWithRAG.Services;

public class ImportingService(IKernelMemory memory, IOptions<RagSettings> ragSettings) : IImportingService
{
    private readonly RagSettings _ragSettings = ragSettings.Value;

    public async Task<string> ImportAsync(string filePath, string country)
    {
        var documentId = ToValidDocumentId(filePath);

        try
        {
            var kmDocument = new Document(documentId)
                .AddFile(filePath)
                .AddTag("country", country);

            var returnedId = await memory.ImportDocumentAsync(kmDocument, _ragSettings.IngestionIndex);

            Log.Debug("MEMORY ingest success file='{File}' docId='{DocId}'", filePath, returnedId);
            return returnedId;
        }
        catch (InvalidIndexNameException iex)
        {
            Log.Error("MEMORY failingIndex='{Failing}' passedIndex='{Passed}' errors={Errors}",
                iex.IndexName,
                _ragSettings.IngestionIndex,
                string.Join(" | ", iex.Errors));
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MEMORY ingest failure file='{File}' docId='{DocId}'", filePath, documentId);
            throw;
        }
    }

    public async Task ImportDirectoryAsync(string directoryPath, string country)
    {
        foreach (var file in Directory.GetFiles(directoryPath))
        {
            await ImportAsync(file, country);
        }
    }

    private static string ToValidDocumentId(string filePath)
    {
        var fileBaseName = Path.GetFileNameWithoutExtension(filePath);
        var guid = Guid.NewGuid().ToString("N")[..8];
        var documentId = $"{Document.ReplaceInvalidChars(fileBaseName)}-{guid}".ToLowerInvariant();
        
        return documentId;
    }
}
