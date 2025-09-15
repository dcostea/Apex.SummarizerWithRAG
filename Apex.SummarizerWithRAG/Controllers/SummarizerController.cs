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
    /// Optional: wait for ingestion pipeline readiness before returning.
    /// </summary>
    [HttpPost("/extract/upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ImportAsync([FromForm] List<IFormFile> files, string? country, [FromQuery] bool wait = false, [FromQuery] int waitSeconds = 60)
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

                    if (wait)
                    {
                        var seconds = Math.Max(1, waitSeconds);
                        var (ready, timedOut, error) = await WaitForDocumentReadinessAsync(docId, _ingestionIndex, TimeSpan.FromSeconds(seconds));
                        if (!ready)
                        {
                            if (timedOut)
                            {
                                Log.Error("MEMORY Ingest timeout after {Seconds}s: docId={DocumentId}, index={Index}, error={Error}", seconds, docId, _ingestionIndex, error ?? "<none>");
                            }
                            else
                            {
                                Log.Information("MEMORY Ingest not ready yet: docId={DocumentId}, index={Index}, status={Status}", docId, _ingestionIndex, error ?? "<none>");
                            }
                        }
                    }

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

    /// <summary>
    /// Retrieves a list of indexed documents from Kernel Memory.
    /// Supports optional filtering by country and limiting the number of results.
    /// </summary>
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
    /// Check if a document is ready (ingestion pipeline completed).
    /// </summary>
    [HttpGet("/memory/{documentId}/ready")]
    public async Task<IActionResult> IsReadyAsync(string documentId, [FromQuery] string? index = null)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return BadRequest("documentId is required.");
        var idx = string.IsNullOrWhiteSpace(index) ? _ingestionIndex : index;
        try
        {
            var ready = await memory.IsDocumentReadyAsync(documentId, idx);
            return Ok(new { Index = idx, DocumentId = documentId, Ready = ready });
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to get readiness for '{documentId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Get full pipeline status and possible errors for a document.
    /// </summary>
    [HttpGet("/memory/{documentId}/status")]
    public async Task<IActionResult> GetStatusAsync(string documentId, [FromQuery] string? index = null)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return BadRequest("documentId is required.");
        var idx = string.IsNullOrWhiteSpace(index) ? _ingestionIndex : index;
        try
        {
            var status = await memory.GetDocumentStatusAsync(documentId, idx);
            return Ok(status);
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to get status for '{documentId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Delete a document and all its derived memories from Kernel Memory (RAG).
    /// </summary>
    [HttpDelete("/memory/{documentId}")]
    [ProducesResponseType(Microsoft.AspNetCore.Http.StatusCodes.Status204NoContent)]
    [ProducesResponseType(Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound, Type = typeof(string))]
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

            async Task WaitForDeletionAsync(string docId, string? idx, TimeSpan timeout)
            {
                var start = DateTimeOffset.UtcNow;
                while (DateTimeOffset.UtcNow - start < timeout)
                {
                    try
                    {
                        // If status is null, consider it deleted from coordination store
                        var status = await memory.GetDocumentStatusAsync(docId, idx);
                        if (status is null)
                        {
                            // Double-check via a broad search snapshot to see if any partition still surfaces
                            var sr = await memory.SearchAsync(" ", index: idx, minRelevance: 0, limit: Math.Max(1000, _rag.Limit));
                            var stillThere = sr?.Results?.Any(r => string.Equals(r.DocumentId, docId, StringComparison.OrdinalIgnoreCase)) ?? false;
                            if (!stillThere) return; // deleted from both status and memory db
                        }
                    }
                    catch
                    {
                        // Ignore transient errors
                    }

                    await Task.Delay(500);
                }
            }

            if (!string.IsNullOrWhiteSpace(index))
            {
                await memory.DeleteDocumentAsync(documentId, index);
                Log.Debug("MEMORY Deleted document: '{FileName}' (docId={DocumentId}, index={Index})", ResolveFileName(documentId), documentId, index);

                // Best-effort wait for deletion to propagate to the vector DB
                await WaitForDeletionAsync(documentId, index, TimeSpan.FromSeconds(5));

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
                try
                {
                    await memory.DeleteDocumentAsync(documentId, idx);
                    Log.Debug("MEMORY Deleted document: '{FileName}' (docId={DocumentId}, index={Index})", ResolveFileName(documentId), documentId, idx);

                    await WaitForDeletionAsync(documentId, idx, TimeSpan.FromSeconds(5));

                    CleanupCache();
                    return NoContent();
                }
                catch
                {
                    // Ignore and continue trying other indexes
                }
            }

            // Last resort: try default (null) index
            try
            {
                await memory.DeleteDocumentAsync(documentId, null);
                Log.Debug("MEMORY Deleted document: '{FileName}' (docId={DocumentId}, index=<default>)", ResolveFileName(documentId), documentId);

                await WaitForDeletionAsync(documentId, null, TimeSpan.FromSeconds(5));

                CleanupCache();
                return NoContent();
            }
            catch
            {
                // fallthrough to not found
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

        var search = await memory.SearchAsync(query, index: _ingestionIndex, limit: _rag.Limit, minRelevance: _rag.MinRelevance, filter: filter);

        foreach (var r in search.Results)
        {
            foreach (var p in r.Partitions ?? [])
            {
                var preview = p.Text is null
                    ? ""
                    : (p.Text.Length > 100 ? (p.Text.Length >= 200 ? p.Text[..200] + "…" : p.Text + "…") : p.Text);

                Log.Debug("MEMORY index={Index} docId={DocumentId} source={Source} country={Country} rel={Relevance} part#{Partition} text={Preview}",
                    r.Index, r.DocumentId, r.SourceName, p.Tags["country"], p.Relevance, p.PartitionNumber, preview);
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

            // Build a compact citation DTO (top 3 chunks per source)
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

            // Log which KM chunks were referenced
            if (citations.Length > 0)
            {
                foreach (var c in citations)
                {
                    var parts = c.Partitions;
                    var partsSummary = parts.Length == 0
                        ? "<none>"
                        : string.Join(", ", parts.Select(p => $"#{p.PartitionNumber}" + (p.SectionNumber > 0 ? $"/p{p.SectionNumber}" : "") + $"(rel={p.Relevance:F3})"));
                    Log.Information("CITATIONS index={Index} docId={DocId} source={Source} parts=[{Parts}]",
                        c.Index,
                        string.IsNullOrWhiteSpace(c.DocumentId) ? "<none>" : c.DocumentId,
                        c.SourceName ?? c.Link ?? c.SourceUrl ?? "<unknown>",
                        partsSummary);
                }
            }
            else
            {
                Log.Information("CITATIONS <none>");
            }

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

    // Helper to wait for document readiness with basic polling
    private async Task<(bool Ready, bool TimedOut, string? Error)> WaitForDocumentReadinessAsync(string documentId, string index, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(2);
        var start = DateTimeOffset.UtcNow;
        string? lastInfo = null;

        while (DateTimeOffset.UtcNow - start < timeout)
        {
            try
            {
                if (await memory.IsDocumentReadyAsync(documentId, index))
                {
                    return (true, false, null);
                }

                var status = await memory.GetDocumentStatusAsync(documentId, index);
                if (status is not null)
                {
                    if (status.RemainingSteps is { Count: > 0 })
                    {
                        lastInfo = "Remaining: " + string.Join(", ", status.RemainingSteps);
                    }
                    else if (status.CompletedSteps is { Count: > 0 })
                    {
                        lastInfo = "Completed: " + string.Join(", ", status.CompletedSteps);
                    }
                }
            }
            catch (Exception ex)
            {
                lastInfo = "Error: " + ex.Message;
                Log.Debug("MEMORY readiness check transient error: {Error}", ex.Message);
            }

            await Task.Delay(delay);
        }

        var msg = lastInfo is null
            ? $"Timed out after {timeout.TotalSeconds:N0}s"
            : $"Timed out after {timeout.TotalSeconds:N0}s. Last status: {lastInfo}";

        return (false, true, msg);
    }
}