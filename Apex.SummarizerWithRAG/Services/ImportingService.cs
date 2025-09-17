using Apex.SummarizerWithRAG.Interfaces;
using Microsoft.KernelMemory;
using Serilog;
using Apex.SummarizerWithRAG.Models;
using Microsoft.Extensions.Options;
using Elastic.Transport;

namespace Apex.SummarizerWithRAG.Services;

public class ImportingService(IKernelMemory memory, IOptions<RagSettings> ragSettings) : IImportingService
{
    private readonly RagSettings _ragSettings = ragSettings.Value;

    public async Task<string> ImportAsync(Stream content, string fileName, string collection)
    {
        var documentId = ToValidDocumentId(fileName);
        var index = GetIngestionIndex();

        try
        {
            var tags = new TagCollection
            {
                { "collection", collection }
            };

            Log.Debug("MEMORY Ingesting stream fileName='{FileName}'...", fileName);

            var returnedId = await memory.ImportDocumentAsync(
                content: content,
                fileName: fileName,
                documentId: documentId,
                tags: tags,
                index: index);

            Log.Debug("MEMORY Ingest success fileName='{FileName}' docId='{DocId}'", fileName, returnedId);
            return returnedId;
        }
        catch (TransportException tex)
        {
            Log.Error("MEMORY Ingest failed due to failed connectivity to Elastic. Exception {message}", tex.Message);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("MEMORY Ingest failure stream fileName='{FileName}' docId='{DocId}'. Exception {message}", fileName, documentId, ex.Message);
            throw;
        }
    }

    private static string ToValidDocumentId(string fileNameOrPath)
    {
        var fileBaseName = Path.GetFileNameWithoutExtension(fileNameOrPath) ?? string.Empty;

        // Sanitize first, then take up to 10 chars from the base name
        var sanitized = Document.ReplaceInvalidChars(fileBaseName);
        var prefix = sanitized.Length > 16 ? sanitized[..16] : sanitized;

        // Fallback if the name becomes empty after sanitization
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "doc";
        }

        var guid = Guid.NewGuid().ToString("N")[..8];

        // Result: <= 16 + 1 + 8 = 25 chars
        var documentId = $"{prefix}-{guid}".ToLowerInvariant();
        return documentId;
    }

    private string GetIngestionIndex() =>
        string.IsNullOrWhiteSpace(_ragSettings.IngestionIndex) ? "apex" : _ragSettings.IngestionIndex;
}
