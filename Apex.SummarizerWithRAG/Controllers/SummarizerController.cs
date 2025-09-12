using Apex.SummarizerWithRAG.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.KernelMemory;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.Extensions.Options;
using Serilog;
using System.Collections.Concurrent;
using System.Text.Json;
using Apex.SummarizerWithRAG.Models;

namespace Apex.SummarizerWithRAG.Controllers;

[ApiController]
public class SummarizerController(IKernelMemory memory, Kernel kernel, IImportingService documentExtractionService,
    IConfiguration configuration, IOptionsSnapshot<RagSettings> ragSettings) : ControllerBase
{
    private readonly RagSettings _rag = ragSettings.Value;
    private readonly string _ingestionIndex = ragSettings.Value?.IngestionIndex!;

    private static readonly ConcurrentDictionary<string, (string DocumentId, string Index)> IngestedByFileName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Handles file uploads and ingests them into Kernel Memory without persisting to local disk.
    /// </summary>
    [HttpPost("/extract/upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ImportAsync([FromForm] List<IFormFile> files, string? country)
    {
        if (files is null || files.Count == 0)
        {
            return BadRequest("No files uploaded.");
        }

        country ??= "Netherlands";

        try
        {
            var results = new List<UploadIngestionResult>(files.Count);

            foreach (var file in files)
            {
                if (file?.Length > 0)
                {
                    await using var stream = file.OpenReadStream();
                    var fileName = Path.GetFileName(file.FileName);
                    var docId = await documentExtractionService.ImportAsync(stream, fileName, country);

                    results.Add(new UploadIngestionResult
                    {
                        FilePath = null,                  // no local disk copy
                        FileName = fileName,
                        DocumentId = docId,
                        Index = _ingestionIndex
                    });
                }
            }

            if (results.Count == 0)
            {
                return BadRequest("All uploaded files were empty.");
            }

            foreach (var item in results)
            {
                if (!string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(item.DocumentId))
                {
                    IngestedByFileName[item.FileName] = (item.DocumentId!, item.Index!);
                }
            }

            return Ok(new { Files = results.ToArray() });
        }
        catch (Exception ex)
        {
            return BadRequest($"Extraction failed: {ex.Message}");
        }
    }

    [HttpGet("/indexed")]
    public async Task<IActionResult> GetIndexedDocumentsAsync([FromQuery] string? country = null, [FromQuery] int? limit = null)
    {
        try
        {
            // Default country tag if not provided
            country ??= "Netherlands";

            // Build an optional filter (by country)
            MemoryFilter? filter = null;
            if (!string.IsNullOrWhiteSpace(country))
            {
                filter = MemoryFilters.ByTag("country", country);
            }

            // Broad search to retrieve citations and their partitions
            // - query: blank to match broadly
            // - minRelevance: 0 to include everything
            // - limit: allow override via query arg, otherwise use configured limit
            var sr = await memory.SearchAsync(
                query: " ",
                index: _ingestionIndex,
                filter: filter,
                minRelevance: 0,
                limit: limit ?? _rag.Limit
            );

            var results = sr?.Results ?? [];

            // Group by document to present a document-centric view
            var items =
                results
                    .GroupBy(r => new
                    {
                        r.Index,
                        r.DocumentId,
                        r.SourceName,
                        r.SourceContentType,
                        r.SourceUrl,
                        r.Link
                    })
                    .Select(g =>
                    {
                        var partitions = g.SelectMany(r => r.Partitions ?? []);
                        var firstText = partitions.Select(p => p.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
                        var preview = firstText.Length > 200 ? firstText[..200] + "…" : firstText;

                        var countries = partitions
                            .SelectMany(p =>
                                p.Tags?
                                    .Where(kv => string.Equals(kv.Key, "country", StringComparison.OrdinalIgnoreCase))
                                    .SelectMany(kv => kv.Value) // kv.Value is List<string>
                                ?? Enumerable.Empty<string>())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                        var maxRelevance = partitions.Any() ? Math.Round(partitions.Max(p => p.Relevance), 3) : 0;

                        return new
                        {
                            g.Key.Index,
                            g.Key.DocumentId,
                            g.Key.SourceName,
                            g.Key.SourceContentType,
                            g.Key.SourceUrl,
                            g.Key.Link,
                            Countries = countries,
                            PartitionCount = partitions.Count(),
                            MaxRelevance = maxRelevance,
                            Preview = preview
                        };
                    })
                    .OrderByDescending(d => d.MaxRelevance)
                    .ToArray();

            // If nothing was found via KM (e.g., connector can’t enumerate), fall back to in-process cache
            if (items.Length == 0 && IngestedByFileName.Count > 0)
            {
                var cached = IngestedByFileName.Select(kvp => new
                {
                    Index = kvp.Value.Index,
                    DocumentId = kvp.Value.DocumentId,
                    SourceName = kvp.Key,
                    SourceContentType = (string?)null,
                    SourceUrl = (string?)null,
                    Link = (string?)null,
                    Countries = Array.Empty<string>(),
                    PartitionCount = 0,
                    MaxRelevance = 0d,
                    Preview = string.Empty
                }).ToArray();

                return Ok(new
                {
                    Index = _ingestionIndex,
                    Country = country,
                    Count = cached.Length,
                    Items = cached,
                    Note = "Returned from in-memory cache (Kernel Memory returned no results)."
                });
            }

            return Ok(new
            {
                Index = _ingestionIndex,
                Country = country,
                Count = items.Length,
                Items = items
            });
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to list indexed documents: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete a document and all its derived memories from Kernel Memory (RAG).
    /// </summary>
    [HttpDelete("/memory/{documentId}")]
    [ProducesResponseType(Microsoft.AspNetCore.Http.StatusCodes.Status204NoContent)]
    [ProducesResponseType(Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound, Type = typeof(string))]
    [ProducesResponseType(Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict, Type = typeof(string))]
    [ProducesResponseType(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest, Type = typeof(string))]
    public async Task<IActionResult> DeleteIndexedDocumentsAsync(string documentId, [FromQuery] string? index)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return BadRequest("documentId is required.");
        }

        try
        {
            static string ResolveFileName(string docId)
            {
                try
                {
                    foreach (var kvp in IngestedByFileName)
                    {
                        if (kvp.Value.DocumentId == docId) return kvp.Key;
                    }
                }
                catch { /* ignore */ }
                return "<unknown>";
            }

            void CleanupCache()
            {
                foreach (var kvp in IngestedByFileName.Where(k => k.Value.DocumentId == documentId).ToList())
                {
                    IngestedByFileName.TryRemove(kvp.Key, out _);
                }
            }

            if (!string.IsNullOrWhiteSpace(index))
            {
                var ready = await memory.IsDocumentReadyAsync(documentId, index);
                if (!ready)
                {
                    Log.Debug("MEMORY Delete skipped (not ready): docId={DocumentId}, index={Index}", documentId, index);
                    return StatusCode(StatusCodes.Status409Conflict, $"Document '{documentId}' not ready in index '{index}'.");
                }

                await memory.DeleteDocumentAsync(documentId, index);
                Log.Debug("MEMORY Deleted document: '{FileName}' (docId={DocumentId}, index={Index})", ResolveFileName(documentId), documentId, index);
                CleanupCache();
                return NoContent();
            }

            // No index provided: try all known indexes
            var indexes = await memory.ListIndexesAsync();
            foreach (var idx in indexes
                         .Select(i => i.Name)
                         .Where(n => !string.IsNullOrWhiteSpace(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (await memory.IsDocumentReadyAsync(documentId, idx))
                {
                    await memory.DeleteDocumentAsync(documentId, idx);
                    Log.Debug("MEMORY Deleted document: '{FileName}' (docId={DocumentId}, index={Index})", ResolveFileName(documentId), documentId, idx);
                    CleanupCache();
                    return NoContent();
                }
            }

            // Last resort: try default (null) index
            if (await memory.IsDocumentReadyAsync(documentId, null))
            {
                await memory.DeleteDocumentAsync(documentId, null);
                Log.Debug("MEMORY Deleted document: '{FileName}' (docId={DocumentId}, index=<default>)", ResolveFileName(documentId), documentId);
                CleanupCache();
                return NoContent();
            }

            Log.Error("MEMORY Delete failed (not found): docId={DocumentId}", documentId);
            return NotFound($"Document '{documentId}' not found.");
        }
        catch (Exception ex)
        {
            Log.Error("MEMORY Delete error: docId={DocumentId}, error={Error}", documentId, ex.Message);
            return BadRequest($"Failed to delete document '{documentId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Performs a semantic search using the specified query and model, and returns an answer with supporting citations.
    /// </summary>
    /// <remarks>The response includes the answer generated by the selected model and a set of citations referencing
    /// relevant source documents. The search is filtered by country if specified. This endpoint is intended for use in
    /// question-answering scenarios where supporting evidence is required.</remarks>
    /// <param name="query">The user question or search query to process. Cannot be null or empty.</param>
    /// <param name="model">The identifier of the language model to use for generating the answer. If null or empty, the default model is used.</param>
    /// <param name="country">An optional country code used to filter search results. If not specified, defaults to "Netherlands".</param>
    /// <returns>An <see cref="IActionResult"/> containing the answer to the query, the model used, and a list of supporting
    /// citations. Returns a bad request result if the query is invalid or an error occurs.</returns>
    [HttpGet("/search")]
    public async Task<IActionResult> SearchAsync(string query, string model, string? country = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Query cannot be empty.");
        }

        country ??= "Netherlands";

        kernel.ImportPluginFromObject(new MemoryPlugin(memory, waitForIngestionToComplete: true), "memory");

        Log.Information("QUERY: {Query}", query);

        var filter = MemoryFilters.ByTag("country", country);

        //var isReady = await memory.IsDocumentReadyAsync("");
        //if (isReady)
        //{
        //    return NotFound(@"Document `` not ready!");
        //}

        var search = await memory.SearchAsync(query, index: _ingestionIndex, limit: _rag.Limit, minRelevance: _rag.MinRelevance, filter: filter);

        foreach (var r in search.Results)
        {
            foreach (var p in r.Partitions ?? [])
            {
                var preview = p.Text is null
                    ? ""
                    : (p.Text.Length > 100 ? (p.Text.Length >= 200 ? p.Text[..200] + "…" : p.Text + "…") : p.Text);

                Log.Debug("MEMORY index={Index} source={Source} country={Country} rel={Relevance} part#{Partition} text={Preview}",
                    r.Index, r.SourceName ?? r.Link, p.Tags["country"], p.Relevance, p.PartitionNumber, preview);
            }
        }

        var prompt = """
            Please use this information to answer the question:
            -----------------
            {{memory.ask question=$query index=$index limit=$limit minRelevance=$minRelevance}}
            -----------------

            Question: {{$query}}
            """;

        var executionSettings = new OllamaPromptExecutionSettings
        {
            NumPredict = _rag.MaxTokens,
            Temperature = _rag.Temperature
        };

        if (!string.IsNullOrWhiteSpace(model))
        {
            executionSettings.ModelId = model;
        }

        var kernelArguments = new KernelArguments(executionSettings)
        {
            ["query"] = query,
            ["index"] = _ingestionIndex,
            ["limit"] = _rag.Limit,
            ["minRelevance"] = _rag.MinRelevance
        };

        try
        {
            var answerText = await kernel.InvokePromptAsync<string>(prompt, kernelArguments);

            Log.Information("ANSWER model={Model} index={Index} limit={Limit} minRelevance={MinRelevance} text={TextPreview}",
                string.IsNullOrWhiteSpace(model) ? "default" : model,
                _ingestionIndex,
                _rag.Limit,
                _rag.MinRelevance,
                (((answerText ?? string.Empty).Length > 200) ? answerText![..200] + "…" : answerText));

            // Build a compact citation DTO
            var citations = (search.Results ?? [])
                .Select(r => new
                {
                    r.Index,
                    r.DocumentId,
                    r.SourceName,
                    r.SourceContentType,
                    r.SourceUrl,
                    r.Link,
                    Partitions = (r.Partitions ?? [])
                        .OrderByDescending(p => p.Relevance)
                        .Take(3)
                        .Select(p => new
                        {
                            p.PartitionNumber,
                            p.SectionNumber,
                            Relevance = Math.Round(p.Relevance, 3),
                            p.Text
                        })
                        .ToArray()
                })
                .ToArray();

            return Ok(new
            {
                Question = query,
                Answer = answerText,
                Model = string.IsNullOrWhiteSpace(model) ? "default" : model,
                Citations = citations
            });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Reads Ollama endpoint from configuration and returns the available models from the Ollama server.
    /// </summary>
    [HttpGet("/models")]
    public async Task<IActionResult> GetOllamaModelsAsync()
    {
        var endpoint = configuration["Ollama:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Ollama endpoint not configured.");
        }

        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = TimeSpan.FromSeconds(10)
            };

            // 1) List all local models
            using var resp = await http.GetAsync("/api/tags");
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync();
            var data = await JsonSerializer.DeserializeAsync<OllamaTagsResponse>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var models = (data?.Models ?? [])
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Ok(models);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, $"Failed to retrieve models from Ollama: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists all Kernel Memory chunks (partitions) in an index.
    /// Optional filters: documentId and country tag.
    /// NOTE: This issues a broad search to retrieve as many partitions as the connector allows.
    /// </summary>
    [HttpGet("/chunks")]
    public async Task<IActionResult> GetChunksAsync([FromQuery] string? country = null)
    {
        try
        {
            MemoryFilter? filter = null;

            country ??= "Netherlands";

            if (!string.IsNullOrWhiteSpace(country))
            {
                filter = (filter is null) ? MemoryFilters.ByTag("country", country) : filter.ByTag("country", country);
            }

            // Broad search to retrieve partitions; set minRelevance low and limit high/unbounded
            var sr = await memory.SearchAsync(
                query: " ",
                index: _ingestionIndex,
                filter: filter,
                minRelevance: _rag.MinRelevance,
                limit: _rag.Limit);

            var chunks = (sr?.Results ?? [])
                .SelectMany(r => (r.Partitions ?? [])
                    .Select(p => new
                    {
                        r.Index,
                        r.DocumentId,
                        r.SourceName,
                        r.SourceContentType,
                        r.SourceUrl,
                        r.Link,
                        p.PartitionNumber,
                        p.SectionNumber,
                        Relevance = Math.Round(p.Relevance, 3),
                        p.Text,
                        Tags = p.Tags?.ToDictionary(kv => kv.Key, kv => kv.Value)
                    }))
                .OrderByDescending(c => c.Relevance)
                .ToArray();

            Log.Information("CHUNKS index={Index} count={Count} minRel={MinRel} limit={Limit} country={Country}",
                _ingestionIndex, chunks.Length, _rag.MinRelevance, _rag.Limit, country);

            return Ok(new
            {
                Index = _ingestionIndex,
                Country = country,
                _rag.MinRelevance,
                _rag.Limit,
                Chunks = chunks
            });
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to list chunks: {ex.Message}");
        }
    }

    // Helper to extract the sort token from a JsonElement (used for Elasticsearch search_after)
    private static object? ExtractSortToken(JsonElement sortToken)
    {
        // Handles common Elasticsearch sort token types (string, number, etc.)
        // Returns the appropriate .NET type for the search_after array
        switch (sortToken.ValueKind)
        {
            case JsonValueKind.String:
                return sortToken.GetString();
            case JsonValueKind.Number:
                if (sortToken.TryGetInt64(out var l)) return l;
                if (sortToken.TryGetDouble(out var d)) return d;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return sortToken.GetBoolean();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
        }
        // Fallback: return the raw text
        return sortToken.GetRawText();
    }
}