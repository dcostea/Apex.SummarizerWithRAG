using Apex.SummarizerWithRAG.Interfaces;
using Apex.SummarizerWithRAG.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI.Ollama;
using Microsoft.KernelMemory.Configuration;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using Serilog;
using Apex.SummarizerWithRAG.Models;
using Microsoft.KernelMemory.DocumentStorage.DevTools;
using Microsoft.KernelMemory.FileSystem.DevTools;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json").Build();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Services.Configure<RagSettings>(builder.Configuration.GetSection("Rag"));
builder.Services.Configure<TextPartitioningSettings>(builder.Configuration.GetSection("TextPartitioning"));

var ollamaEndpoint = builder.Configuration["Ollama:Endpoint"];
var timeoutSeconds = builder.Configuration.GetValue<int>("Ollama:TimeoutSeconds");

var ollamaHttpClient = new HttpClient(new HttpClientHandler())
{
    BaseAddress = new Uri(ollamaEndpoint!),
    Timeout = TimeSpan.FromSeconds(timeoutSeconds),
};
//ollamaHttpClient.DefaultRequestHeaders.Add("api-key", "");

try
{
    var response = await ollamaHttpClient.GetAsync("/");
    response.EnsureSuccessStatusCode();
    var content = await response.Content.ReadAsStringAsync();
    if (content.Contains("Ollama is running"))
    {
        Log.Information("Ollama connection successful!");
    }
    else
    {
        Log.Error("Ollama connection failed: Unexpected response.");
    }
}
catch (Exception ex)
{
    Log.Error($"Ollama connection failed: {ex.Message}");
}

builder.Services.AddKernel()
    .AddOllamaChatCompletion(configuration["Ollama:TextModel"]!, ollamaHttpClient);

builder.Services.AddKernelMemory(km =>
{
    km.WithElasticsearch(op =>
    {
        op.WithUserNameAndPassword(configuration["Elastic:ElasticUser"]!, configuration["Elastic:ElasticPassword"]!);
        op.WithCertificateFingerPrint(configuration["Elastic:ElasticCertFingerprint"]!);
        op.WithEndpoint(configuration["Elastic:ElasticHost"]!);
        op.WithIndexPrefix("km");
    });

    km.WithSimpleFileStorage(new SimpleFileStorageConfig
    {
        StorageType = FileSystemTypes.Disk,
        Directory = Path.Combine(builder.Environment.ContentRootPath, "kmdata", "docs")
    });

    km.WithOllamaTextGeneration(new OllamaConfig
    {
        Endpoint = ollamaEndpoint!,
        TextModel = new OllamaModelConfig { ModelName = configuration["Ollama:TextModel"]! }
    });

    km.WithOllamaTextEmbeddingGeneration(new OllamaConfig
    {
        Endpoint = ollamaEndpoint!,
        EmbeddingModel = new OllamaModelConfig { ModelName = configuration["Ollama:EmbeddingModel"]! }
    });

    km.WithCustomTextPartitioningOptions(new TextPartitioningOptions
    {
        MaxTokensPerParagraph = int.Parse(configuration["TextPartitioning:MaxTokensPerParagraph"]!),
        OverlappingTokens = int.Parse(configuration["TextPartitioning:OverlappingTokens"]!)
    });
});

builder.Services.AddScoped<IImportingService, ImportingService>();

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddSwaggerGen(static c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Apex.Summarizer", Version = "v1" });
    c.UseInlineDefinitionsForEnums();
    c.MapType<Enum>(() => new OpenApiSchema { Type = "string" });
});

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 100 * 1024 * 1024; // 100 MB
});

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseAuthorization();
app.MapControllers();

Log.Information("Apex.Summarizer application started successfully");

app.Run();
