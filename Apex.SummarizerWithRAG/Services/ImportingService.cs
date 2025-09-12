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
        var index = string.IsNullOrWhiteSpace(_ragSettings.IngestionIndex) ? "apex" : _ragSettings.IngestionIndex;

        try
        {
            var kmDocument = new Document(documentId)
                .AddFile(filePath)
                .AddTag("country", country);

            Log.Debug("MEMORY ingesting file='{File}'...", filePath);

            var returnedId = await memory.ImportDocumentAsync(kmDocument, index);

            Log.Debug("MEMORY ingest success file='{File}' docId='{DocId}'", filePath, returnedId);
            return returnedId;
        }
        catch (InvalidIndexNameException iex)
        {
            var errors = iex.Errors.Any() ? string.Join(" | ", iex.Errors) : "<none>";
            Log.Error("MEMORY failingIndex='{Failing}' passedIndex='{Passed}' errors={Errors}",
                iex.IndexName,
                index,
                errors);
            throw;
        }
        catch (ArgumentException aex) when (aex.Message.Contains("more tokens than configured batch size", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error(aex, "MEMORY ingest failed due to embedding batch size. Reduce TextPartitioning:MaxTokensPerParagraph or switch to Ollama embeddings.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MEMORY ingest failure file='{File}' docId='{DocId}'", filePath, documentId);
            throw;
        }
    }

    public async Task<string> ImportAsync(Stream content, string fileName, string country)
    {
        var documentId = ToValidDocumentId(fileName);
        var index = string.IsNullOrWhiteSpace(_ragSettings.IngestionIndex) ? "apex" : _ragSettings.IngestionIndex;

        try
        {
            // Apply tags (country) when importing stream-based content
            var tags = new TagCollection();
            tags.Add("country", country);

            Log.Debug("MEMORY ingesting stream fileName='{FileName}'...", fileName);

            var returnedId = await memory.ImportDocumentAsync(
                content: content,
                fileName: fileName,
                documentId: documentId,
                tags: tags,
                index: index);

            Log.Debug("MEMORY ingest success fileName='{FileName}' docId='{DocId}'", fileName, returnedId);
            return returnedId;
        }
        catch (InvalidIndexNameException iex)
        {
            var errors = iex.Errors.Any() ? string.Join(" | ", iex.Errors) : "<none>";
            Log.Error("MEMORY failingIndex='{Failing}' passedIndex='{Passed}' errors={Errors}",
                iex.IndexName,
                index,
                errors);
            throw;
        }
        catch (ArgumentException aex) when (aex.Message.Contains("more tokens than configured batch size", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error(aex, "MEMORY ingest failed due to embedding batch size. Reduce TextPartitioning:MaxTokensPerParagraph or switch to Ollama embeddings.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MEMORY ingest failure stream fileName='{FileName}' docId='{DocId}'", fileName, documentId);
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

    private static string ToValidDocumentId(string fileNameOrPath)
    {
        var fileBaseName = Path.GetFileNameWithoutExtension(fileNameOrPath);
        var guid = Guid.NewGuid().ToString("N")[..8];
        var documentId = $"{Document.ReplaceInvalidChars(fileBaseName)}-{guid}".ToLowerInvariant();

        return documentId;
    }
}
