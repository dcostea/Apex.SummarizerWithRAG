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
    /// Handles file uploads, saves them, and ingests them into Kernel Memory.
    /// </summary>
    [HttpPost("/extract/upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadAndExtractAsync([FromForm] List<IFormFile> files, string? country)
    {
        if (files is null || files.Count == 0)
        {
            return BadRequest("No files uploaded.");
        }

        var importPath = "Data";
        Directory.CreateDirectory(importPath);

        var savedFiles = new List<string>(files.Count);

        foreach (var file in files)
        {
            if (file?.Length > 0)
            {
                var safeName = Path.GetFileName(file.FileName);
                var destination = Path.Combine(importPath, safeName);

                if (System.IO.File.Exists(destination))
                {
                    var name = Path.GetFileNameWithoutExtension(safeName);
                    var ext = Path.GetExtension(safeName);
                    destination = Path.Combine(importPath, $"{name}{ext}");
                }

                using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                await file.CopyToAsync(stream);
                savedFiles.Add(destination);
            }
        }

        if (savedFiles.Count == 0)
        {
            return BadRequest("All uploaded files were empty.");
        }

        country ??= "Netherlands";

        try
        {
            var ingestionTasks = savedFiles
                .Select(path => documentExtractionService.ImportAsync(path, country))
                .ToArray();

            var documentIds = await Task.WhenAll(ingestionTasks);

            var result = savedFiles
                .Zip(documentIds, (path, docId) => new UploadIngestionResult
                {
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    DocumentId = docId,
                    Index = _ingestionIndex
                })
                .ToArray();

            // Track ingested docs during this process lifetime (fallback when KM cannot enumerate)
            foreach (var item in result)
            {
                if (!string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(item.DocumentId))
                {
                    IngestedByFileName[item.FileName] = (item.DocumentId!, item.Index!);
                }
            }

            return Ok(new { Files = result });
        }
        catch (Exception ex)
        {
            return BadRequest($"Extraction failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates currently indexed files from Kernel Memory, best-effort.
    /// Falls back to runtime registry (current process) if KM cannot enumerate all documents.
    /// </summary>
    [HttpGet("/indexed")]
    public async Task<IActionResult> GetIndexedDocumentsAsync()
    {
        try
        {
            var items = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Helper to add KM results
            async Task AddFromKmAsync(string idx)
            {
                try
                {
                    var sr = await memory.SearchAsync(" ", index: idx, minRelevance: _rag.MinRelevance, limit: _rag.Limit);
                    foreach (var r in sr?.Results ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(r.DocumentId)) continue;
                        if (!seen.Add(r.DocumentId)) continue;

                        var fileName = string.IsNullOrWhiteSpace(r.SourceName)
                            ? Path.GetFileName(r.Link ?? "")
                            : r.SourceName;

                        items.Add(new
                        {
                            FileName = fileName,
                            DocumentId = r.DocumentId,
                            Index = string.IsNullOrWhiteSpace(r.Index) ? idx : r.Index
                        });
                    }
                }
                catch
                {
                    // ignore KM listing errors; fallback below will cover
                }
            }

            if (!string.IsNullOrWhiteSpace(_rag.IngestionIndex))
            {
                // Specific index requested
                await AddFromKmAsync(_rag.IngestionIndex);

                // Fallback: include runtime-tracked entries in that index
                foreach (var kvp in IngestedByFileName)
                {
                    var (docId, ix) = kvp.Value;
                    if (!string.IsNullOrWhiteSpace(docId) &&
                        ix.Equals(_rag.IngestionIndex, StringComparison.OrdinalIgnoreCase) &&
                        seen.Add(docId))
                    {
                        items.Add(new { FileName = kvp.Key, DocumentId = docId, Index = ix });
                    }
                }

                return Ok(items);
            }

            // No index specified: gather known indexes + always include ingestion index
            var indexes = new List<string>();
            var all = await memory.ListIndexesAsync();
            indexes.AddRange(all.Select(i => i.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
            if (!indexes.Contains(_ingestionIndex, StringComparer.OrdinalIgnoreCase))
            {
                indexes.Add(_ingestionIndex);
            }

            foreach (var idx in indexes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await AddFromKmAsync(idx);
            }

            // Fallback: include ALL runtime-tracked entries regardless of index
            foreach (var kvp in IngestedByFileName)
            {
                var (docId, ix) = kvp.Value;
                if (!string.IsNullOrWhiteSpace(docId) && seen.Add(docId))
                {
                    items.Add(new { FileName = kvp.Key, DocumentId = docId, Index = ix });
                }
            }

            return Ok(items);
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to list indexed files: {ex.Message}");
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

            if (!string.IsNullOrWhiteSpace(index))
            {
                var ready = await memory.IsDocumentReadyAsync(documentId, index);
                if (!ready)
                {
                    Log.Debug("MEMORY Delete skipped (not found or not ready): docId={DocumentId}, index={Index}", documentId, index);
                    return NotFound($"Document '{documentId}' not found or not ready in index '{index}'.");
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
            return NotFound($"Document '{documentId}' not found or not ready.");
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
}