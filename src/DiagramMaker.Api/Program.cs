using System.Text.Json;
using DiagramMaker.Background;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Security;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var explicitLlmPolicyPath = Environment.GetEnvironmentVariable("DIAGRAMMAKER_LLM_POLICY_PATH");
var defaultLlmPolicyPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DiagramMaker",
    "llm-policy.json");
var llmPolicyPath = string.IsNullOrWhiteSpace(explicitLlmPolicyPath) ? defaultLlmPolicyPath : explicitLlmPolicyPath;
if (!string.IsNullOrWhiteSpace(explicitLlmPolicyPath) && !Path.IsPathFullyQualified(llmPolicyPath))
{
    throw new InvalidOperationException("DIAGRAMMAKER_LLM_POLICY_PATH must be an absolute path.");
}
if (File.Exists(llmPolicyPath))
{
    builder.Configuration.AddJsonFile(llmPolicyPath, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
}
else if (!string.IsNullOrWhiteSpace(explicitLlmPolicyPath))
{
    throw new FileNotFoundException("The configured Diagram Maker LLM policy file does not exist.", llmPolicyPath);
}
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.Configure<GitWorkerOptions>(builder.Configuration.GetSection(GitWorkerOptions.SectionName));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.AddProblemDetails();
builder.Services.AddCors(options => options.AddPolicy("development", policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<IAppStore>(services =>
{
    var options = services.GetRequiredService<IOptions<StorageOptions>>().Value;
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    return options.Provider.ToLowerInvariant() switch
    {
        "postgresql" => new PostgresAppStore(options.ConnectionString ?? throw new InvalidOperationException("Storage:ConnectionString is required.")),
        "localfile" => new LocalFileAppStore(Path.GetFullPath(options.LocalFilePath, environment.ContentRootPath)),
        "inmemory" => new InMemoryAppStore(),
        _ => throw new InvalidOperationException($"Unsupported Storage:Provider '{options.Provider}'.")
    };
});
builder.Services.AddSingleton<SecretMasker>();
builder.Services.AddSingleton<DiagramValidator>();
builder.Services.AddSingleton<MermaidCompiler>();
builder.Services.AddSingleton<DiagramProjectionService>();
builder.Services.AddSingleton<SourceGraphAnalyzer>();
builder.Services.AddSingleton<NaturalDiagramSessionCache>();
builder.Services.AddScoped<MermaidDslRevisionService>();
builder.Services.AddSingleton(services => new VllmClient(
    services.GetRequiredService<IOptions<LlmOptions>>().Value,
    services.GetRequiredService<ILogger<VllmClient>>()));
builder.Services.AddSingleton<StructuredLlmCompletion>();
builder.Services.AddSingleton<IInternalLlmClient, InternalLlmClient>();
builder.Services.AddSingleton<IGitWorkerClient, GitWorkerClient>();
builder.Services.AddScoped<NaturalDiagramService>();
builder.Services.AddScoped<AnalysisJobProcessor>();
builder.Services.AddHostedService<AnalysisWorker>();

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'self'";
    await next();
});
if (app.Environment.IsDevelopment())
{
    app.UseCors("development");
}

app.UseMiddleware<InternalIdentityMiddleware>();
var localWebRoot = Path.GetFullPath("../../web/dist", app.Environment.ContentRootPath);
var packagedWebRoot = Path.GetFullPath("wwwroot", AppContext.BaseDirectory);
var selectedWebRoot = app.Environment.IsDevelopment() && Directory.Exists(localWebRoot)
    ? localWebRoot
    : packagedWebRoot;
if (!Directory.Exists(selectedWebRoot))
{
    throw new InvalidOperationException($"Static web directory does not exist: {selectedWebRoot}");
}
var staticFiles = new PhysicalFileProvider(selectedWebRoot);
app.Lifetime.ApplicationStopped.Register(staticFiles.Dispose);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = staticFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = staticFiles });

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<IAppStore>().InitializeAsync(CancellationToken.None);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "diagram-maker-api" }));

var api = app.MapGroup("/api/v1");

api.MapGet("/repositories", async (HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    var repositories = await store.ListRepositoriesAsync(cancellationToken);
    return Results.Ok(repositories.Where(identity.CanAccess).Select(static repository => new
    {
        repository.Id,
        repository.Name,
        repository.LocalPath,
        repository.DefaultBranch,
        repository.AllowedRoles,
        repository.CreatedAt
    }));
});

api.MapPost("/repositories/inspect", async (
    InspectRepositoryRequest request,
    HttpContext context,
    IGitWorkerClient gitWorker,
    CancellationToken cancellationToken) =>
{
    if (!context.GetInternalIdentity().Roles.Contains("Admin"))
    {
        return Results.Forbid();
    }

    try
    {
        return Results.Ok(await gitWorker.InspectAsync(request.LocalPath, cancellationToken));
    }
    catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or InvalidOperationException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

api.MapPost("/repositories", async (
    RegisterRepositoryRequest request,
    HttpContext context,
    IAppStore store,
    IGitWorkerClient gitWorker,
    CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    if (!identity.Roles.Contains("Admin"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.LocalPath))
    {
        return Results.BadRequest(new { error = "Name and localPath are required." });
    }

    GitRepositoryInspection inspection;
    try
    {
        inspection = await gitWorker.InspectAsync(request.LocalPath, cancellationToken);
    }
    catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or InvalidOperationException or IOException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }

    var existing = await store.ListRepositoriesAsync(cancellationToken);
    if (existing.Any(repository => repository.LocalPath.Equals(inspection.NormalizedPath, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.Conflict(new { error = "This local Git repository is already registered." });
    }

    var defaultBranch = string.IsNullOrWhiteSpace(request.DefaultBranch)
        ? inspection.DefaultBranch
        : request.DefaultBranch.Trim();
    if (inspection.Branches.Count > 0 && !inspection.Branches.Contains(defaultBranch, StringComparer.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = $"Branch '{defaultBranch}' does not exist in this repository." });
    }

    var repository = new RepositoryDefinition(
        Guid.NewGuid(),
        request.Name.Trim(),
        inspection.NormalizedPath,
        defaultBranch,
        request.AllowedRoles?.Where(static role => !string.IsNullOrWhiteSpace(role)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? ["Reviewer"],
        DateTimeOffset.UtcNow);
    await store.SaveRepositoryAsync(repository, cancellationToken);
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "repository.register", repository.Id, "allowed", DateTimeOffset.UtcNow), cancellationToken);
    return Results.Created($"/api/v1/repositories/{repository.Id}", new
    {
        repository.Id,
        repository.Name,
        repository.LocalPath,
        repository.DefaultBranch,
        repository.AllowedRoles,
        repository.CreatedAt
    });
});

api.MapPost("/analyses", async (
    AnalyzeRequest request,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var repository = await store.GetRepositoryAsync(request.RepositoryId, cancellationToken);
    if (repository is null) return Results.NotFound(new { error = "Repository is not registered." });
    var identity = context.GetInternalIdentity();
    if (!identity.CanAccess(repository)) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.BaseRevision) || string.IsNullOrWhiteSpace(request.TargetRevision))
    {
        return Results.BadRequest(new { error = "Base and target revisions are required." });
    }
    if (request.CallerDepth is < 0 or > 3 || request.CalleeDepth is < 0 or > 2)
    {
        return Results.BadRequest(new { error = "CallerDepth must be 0-3 and CalleeDepth must be 0-2." });
    }
    if (request.DiagramTypes?.Any(type => !DiagramProjectionService.IsSupported(type)) == true)
    {
        return Results.BadRequest(new { error = "DiagramTypes must contain only flowchart, class, sequence, or state." });
    }

    var now = DateTimeOffset.UtcNow;
    var job = new AnalysisJob(Guid.NewGuid(), request, AnalysisState.Queued, null, null, 0, "Queued", null, null, null, now, now, null);
    await store.SaveAnalysisAsync(job, cancellationToken);
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "analysis.create", repository.Id, "allowed", now), cancellationToken);
    return Results.Accepted($"/api/v1/analyses/{job.Id}", ToAnalysisResponse(job));
});

api.MapGet("/analyses/{id:guid}", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var job = await store.GetAnalysisAsync(id, cancellationToken);
    if (job is null) return Results.NotFound();
    var repository = await store.GetRepositoryAsync(job.Request.RepositoryId, cancellationToken);
    if (repository is null || !context.GetInternalIdentity().CanAccess(repository)) return Results.Forbid();
    return Results.Ok(ToAnalysisResponse(job));
});

api.MapGet("/analyses/{id:guid}/graph", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var job = await AuthorizedJob(id, context, store, cancellationToken);
    return job switch
    {
        null => Results.NotFound(),
        { Result: null } => Results.Conflict(new { error = "Analysis result is not available yet." }),
        _ => Results.Ok(job.Result.Graph)
    };
});

api.MapGet("/analyses/{id:guid}/diagrams", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var job = await AuthorizedJob(id, context, store, cancellationToken);
    return job switch
    {
        null => Results.NotFound(),
        { Result: null } => Results.Conflict(new { error = "Analysis result is not available yet." }),
        _ => Results.Ok(job.Result.Diagrams)
    };
});

api.MapGet("/analyses/{id:guid}/evidence/{evidenceId}", async (
    Guid id,
    string evidenceId,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var job = await AuthorizedJob(id, context, store, cancellationToken);
    if (job?.Result is null) return Results.NotFound();
    var evidence = job.Result.Graph.Evidence.FirstOrDefault(item => item.Id.Equals(evidenceId, StringComparison.Ordinal));
    return evidence is null ? Results.NotFound() : Results.Ok(evidence);
});

api.MapGet("/analyses/{id:guid}/events", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    AnalysisState? previous = null;
    while (!cancellationToken.IsCancellationRequested)
    {
        var job = await AuthorizedJob(id, context, store, cancellationToken);
        if (job is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (job.State != previous)
        {
            var payload = JsonSerializer.Serialize(ToAnalysisResponse(job), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await context.Response.WriteAsync($"event: progress\ndata: {payload}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            previous = job.State;
        }

        if (job.State is AnalysisState.Completed or AnalysisState.Partial or AnalysisState.Failed) return;
        await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
    }
});

api.MapPost("/natural-diagrams", async (
    NaturalDiagramRequest request,
    HttpContext context,
    NaturalDiagramService service,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    try
    {
        var identity = context.GetInternalIdentity();
        var record = await service.GenerateAsync(request, identity.UserId, cancellationToken);
        await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "natural-diagram.create", null, "allowed", DateTimeOffset.UtcNow), cancellationToken);
        return Results.Created($"/api/v1/natural-diagrams/{record.Id}", record);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (LlmClientException exception)
    {
        return LlmFailure(exception);
    }
});

api.MapPost("/llm/tests/connection", async (
    HttpContext context,
    IInternalLlmClient llm,
    CancellationToken cancellationToken) =>
{
    if (!context.GetInternalIdentity().Roles.Contains("Admin")) return Results.Forbid();
    try
    {
        return Results.Ok(await llm.TestConnectionAsync(cancellationToken));
    }
    catch (LlmClientException exception)
    {
        return LlmFailure(exception);
    }
});

api.MapPost("/llm/tests/diagram-contract", async (
    HttpContext context,
    IInternalLlmClient llm,
    CancellationToken cancellationToken) =>
{
    if (!context.GetInternalIdentity().Roles.Contains("Admin")) return Results.Forbid();
    try
    {
        return Results.Ok(await llm.TestDiagramContractAsync(cancellationToken));
    }
    catch (LlmClientException exception)
    {
        return LlmFailure(exception);
    }
});

api.MapPost("/llm/tests/thinking-contract", async (
    HttpContext context,
    IInternalLlmClient llm,
    CancellationToken cancellationToken) =>
{
    if (!context.GetInternalIdentity().Roles.Contains("Admin")) return Results.Forbid();
    try
    {
        return Results.Ok(await llm.TestThinkingContractAsync(cancellationToken));
    }
    catch (LlmClientException exception)
    {
        return LlmFailure(exception);
    }
});

api.MapGet("/natural-diagrams", async (int? limit, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    var records = await store.ListNaturalDiagramsAsync(identity.UserId, Math.Clamp(limit ?? 20, 1, 50), cancellationToken);
    return Results.Ok(records);
});

api.MapGet("/natural-diagrams/{id:guid}", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var record = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (record is null) return Results.NotFound();
    return CanAccessNaturalDiagram(record, context.GetInternalIdentity().UserId)
        ? Results.Ok(record)
        : Results.Forbid();
});

api.MapGet("/natural-diagrams/{id:guid}/revisions", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var record = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (record is null) return Results.NotFound();
    var identity = context.GetInternalIdentity();
    if (!CanAccessNaturalDiagram(record, identity.UserId)) return Results.Forbid();
    return Results.Ok(await store.ListNaturalDiagramRevisionsAsync(record.RootDiagramId ?? record.Id, identity.UserId, cancellationToken));
});

api.MapPost("/diagrams/{id:guid}/revisions", async (
    Guid id,
    NaturalDiagramRequest request,
    HttpContext context,
    NaturalDiagramService service,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var parent = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (parent is null) return Results.NotFound();
    var identity = context.GetInternalIdentity();
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.Forbid();
    var revised = request with { ParentDiagramId = id };
    var record = await service.GenerateAsync(revised, identity.UserId, cancellationToken);
    return Results.Created($"/api/v1/natural-diagrams/{record.Id}", record);
});

api.MapPost("/natural-diagrams/{id:guid}/dsl-revisions", async (
    Guid id,
    SaveDiagramDslRevisionRequest request,
    HttpContext context,
    MermaidDslRevisionService service,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var parent = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (parent is null) return Results.NotFound();
    var identity = context.GetInternalIdentity();
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.Forbid();
    try
    {
        var record = await service.SaveAsync(parent, request.MermaidDsl, identity.UserId, cancellationToken);
        return Results.Created($"/api/v1/natural-diagrams/{record.Id}", record);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (DiagramValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

api.MapPost("/natural-diagrams/{id:guid}/regenerate", async (
    Guid id,
    HttpContext context,
    NaturalDiagramService service,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var parent = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (parent is null) return Results.NotFound();
    var identity = context.GetInternalIdentity();
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.Forbid();

    var request = parent.Request with { ParentDiagramId = id, ForceRegenerate = true };
    try
    {
        var record = await service.GenerateAsync(request, identity.UserId, cancellationToken);
        return Results.Created($"/api/v1/natural-diagrams/{record.Id}", record);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (LlmClientException exception)
    {
        return LlmFailure(exception);
    }
});

app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = staticFiles });

app.Run();

static bool CanAccessNaturalDiagram(NaturalDiagramRecord record, string userId) =>
    string.IsNullOrEmpty(record.OwnerUserId) || string.Equals(record.OwnerUserId, userId, StringComparison.Ordinal);

static object ToAnalysisResponse(AnalysisJob job) => new
{
    job.Id,
    job.State,
    job.BaseSha,
    job.TargetSha,
    job.Progress,
    job.StageMessage,
    job.Result,
    job.ErrorCode,
    job.ErrorMessage,
    job.CreatedAt,
    job.UpdatedAt
};

static IResult LlmFailure(LlmClientException exception) => Results.Json(new
{
    errorCode = exception.Code,
    error = exception.Message,
    failureKind = exception.FailureKind,
    initialFailureKind = exception.InitialFailureKind,
    repairAttempted = exception.RepairAttempted,
    requestedMaxOutputTokens = exception.RequestedMaxOutputTokens,
    promptTokens = exception.PromptTokens,
    completionTokens = exception.CompletionTokens,
    totalTokens = exception.TotalTokens
}, statusCode: StatusCodes.Status503ServiceUnavailable);

static async Task<AnalysisJob?> AuthorizedJob(Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken)
{
    var job = await store.GetAnalysisAsync(id, cancellationToken);
    if (job is null) return null;
    var repository = await store.GetRepositoryAsync(job.Request.RepositoryId, cancellationToken);
    return repository is not null && context.GetInternalIdentity().CanAccess(repository) ? job : null;
}

public partial class Program;
