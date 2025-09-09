using Apex.SummarizerWithRAG.Interfaces;
using Microsoft.KernelMemory;
using Serilog;
using System.Text.RegularExpressions;
using Apex.SummarizerWithRAG.Models;
using Microsoft.Extensions.Options;

namespace Apex.SummarizerWithRAG.Services;

public class ImportingService(IKernelMemory memory, IOptions<RagSettings> ragSettings) : IImportingService
{
    private readonly string _indexName = ragSettings.Value.IngestionIndex!;

    public async Task<string> ImportAsync(string filePath, string country)
    {
        string documentId;

        var document = ToValidDocumentId(filePath);

        try
        {
            documentId = await memory.ImportDocumentAsync(
                new Document(document).AddFile(filePath).AddTag("country", country),
                index: _indexName,
                steps: ["extract", "partition", "gen_embeddings", "save_records"]);

            Log.Debug($"MEMORY indexed: {filePath} into index: {_indexName} with documentId: {documentId}");

            return documentId;
        }
        catch (Exception ex)
        {
            Log.Error($"MEMORY Failed to process {filePath}: {ex.Message}");
            throw;
        }
    }

    public async Task ImportDirectoryAsync(string directoryPath, string country)
    {
        var files = Directory.GetFiles(directoryPath);

        foreach (var file in files)
        {
            await ImportAsync(file, country);
        }
    }

    private static string ToValidDocumentId(string id)
    {
        var cleaned = Regex.Replace(id, @"[^A-Za-z0-9._-]", "_");
        cleaned = cleaned.Trim('.', '_');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "doc_" + Guid.NewGuid().ToString("N");
        }
        return cleaned;
    }
}
