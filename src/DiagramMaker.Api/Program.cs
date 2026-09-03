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
builder.Services.AddSingleton<DiagramPresetCatalog>();
builder.Services.AddSingleton<SourceGraphAnalyzer>();
builder.Services.AddSingleton<NaturalDiagramSessionCache>();
builder.Services.AddScoped<MermaidDslRevisionService>();
builder.Services.AddScoped<DiagramRevisionService>();
builder.Services.AddSingleton(services => new VllmClient(
    services.GetRequiredService<IOptions<LlmOptions>>().Value,
    services.GetRequiredService<ILogger<VllmClient>>()));
builder.Services.AddSingleton<StructuredLlmCompletion>();
builder.Services.AddSingleton<IInternalLlmClient, InternalLlmClient>();
builder.Services.AddSingleton<IGitWorkerClient, GitWorkerClient>();
builder.Services.AddScoped<NaturalDiagramService>();
builder.Services.AddScoped<AnalysisJobProcessor>();
builder.Services.AddScoped<AnalysisPlanProcessor>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<AnalysisPlanWorker>();

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
        repository.CreatedAt,
        AnalysisRules = repository.AnalysisRules ?? new RepositoryAnalysisRules(0, [])
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
    catch (GitWorkerException exception)
    {
        return Results.BadRequest(new { errorCode = exception.ErrorCode, error = exception.UserMessage });
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
    catch (GitWorkerException exception)
    {
        return Results.BadRequest(new { errorCode = exception.ErrorCode, error = exception.UserMessage });
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
        DateTimeOffset.UtcNow,
        new RepositoryAnalysisRules(0, []));
    await store.SaveRepositoryAsync(repository, cancellationToken);
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "repository.register", repository.Id, "allowed", DateTimeOffset.UtcNow), cancellationToken);
    return Results.Created($"/api/v1/repositories/{repository.Id}", new
    {
        repository.Id,
        repository.Name,
        repository.LocalPath,
        repository.DefaultBranch,
        repository.AllowedRoles,
        repository.CreatedAt,
        repository.AnalysisRules
    });
});

api.MapPut("/repositories/{id:guid}/analysis-rules", async (
    Guid id,
    UpdateRepositoryAnalysisRulesRequest request,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    if (!identity.Roles.Contains("Admin")) return Results.Forbid();
    var repository = await store.GetRepositoryAsync(id, cancellationToken);
    if (repository is null) return Results.NotFound();
    var currentRevision = repository.AnalysisRules?.Revision ?? 0;
    if (request.ExpectedRevision != currentRevision)
        return Results.Conflict(new { errorCode = "REPOSITORY_RULE_REVISION_CONFLICT", error = "The repository rules changed. Reload and try again.", currentRevision });
    var validationError = ValidateIndirectCallRules(request.IndirectCalls);
    if (validationError is not null) return Results.BadRequest(new { error = validationError });

    var normalized = request.IndirectCalls.Select(rule => rule with
    {
        Id = rule.Id.Trim(),
        Name = rule.Name.Trim(),
        ApiName = rule.ApiName.Trim(),
        Aliases = rule.Aliases.Select(alias => new IndirectCallAlias(alias.Expression.Trim(), alias.TargetType.Trim())).ToArray()
    }).ToArray();
    var updated = repository with { AnalysisRules = new RepositoryAnalysisRules(currentRevision + 1, normalized) };
    await store.SaveRepositoryAsync(updated, cancellationToken);
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "repository.analysis-rules.update", repository.Id, "allowed", DateTimeOffset.UtcNow), cancellationToken);
    return Results.Ok(updated);
});

api.MapGet("/repositories/{id:guid}/commits", async (
    Guid id,
    string? query,
    int? skip,
    int? limit,
    HttpContext context,
    IAppStore store,
    IGitWorkerClient gitWorker,
    CancellationToken cancellationToken) =>
{
    var repository = await store.GetRepositoryAsync(id, cancellationToken);
    if (repository is null) return Results.NotFound();
    if (!context.GetInternalIdentity().CanAccess(repository)) return Results.Forbid();
    try
    {
        return Results.Ok(await gitWorker.ListCommitsAsync(
            repository,
            query,
            Math.Max(skip ?? 0, 0),
            Math.Clamp(limit ?? 50, 1, 100),
            cancellationToken));
    }
    catch (GitWorkerException exception)
    {
        return Results.BadRequest(new { errorCode = exception.ErrorCode, error = exception.UserMessage });
    }
});

api.MapGet("/repositories/{id:guid}/commits/resolve", async (
    Guid id,
    string? revision,
    HttpContext context,
    IAppStore store,
    IGitWorkerClient gitWorker,
    CancellationToken cancellationToken) =>
{
    var repository = await store.GetRepositoryAsync(id, cancellationToken);
    if (repository is null) return Results.NotFound();
    if (!context.GetInternalIdentity().CanAccess(repository)) return Results.Forbid();
    var normalized = revision?.Trim() ?? string.Empty;
    if (normalized.Length is < 7 or > 64 || normalized.Any(static value => !Uri.IsHexDigit(value)))
        return Results.BadRequest(new { errorCode = "GIT_REVISION_INVALID", error = "Enter a 7 to 64 character hexadecimal commit SHA." });
    try
    {
        return Results.Ok(await gitWorker.GetCommitAsync(repository, normalized, cancellationToken));
    }
    catch (GitWorkerException exception)
    {
        return Results.BadRequest(new { errorCode = exception.ErrorCode, error = exception.UserMessage });
    }
});

api.MapGet("/diagram-presets", (string? type, DiagramPresetCatalog catalog) =>
{
    if (!string.IsNullOrWhiteSpace(type) && !DiagramProjectionService.IsSupported(type))
        return Results.BadRequest(new { error = "Type must be flowchart, class, sequence, code-relation, or state." });
    return Results.Ok(catalog.List(type));
});

api.MapPost("/analysis-plans", async (
    AnalysisPlanRequest request,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var repository = await store.GetRepositoryAsync(request.RepositoryId, cancellationToken);
    if (repository is null) return Results.NotFound(new { error = "Repository is not registered." });
    var identity = context.GetInternalIdentity();
    if (!identity.CanAccess(repository)) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.TargetRevision))
        return Results.BadRequest(new { error = "Target revision is required." });

    var now = DateTimeOffset.UtcNow;
    var normalized = request with
    {
        TargetRevision = request.TargetRevision.Trim(),
        BaseRevision = string.IsNullOrWhiteSpace(request.BaseRevision) ? null : request.BaseRevision.Trim()
    };
    var plan = new AnalysisPlan(
        Guid.NewGuid(), identity.UserId, normalized, AnalysisPlanState.Queued,
        null, null, 0, "Queued", null, null, [], [], [], [], null, null, 0,
        now, now, now.AddDays(30), null, SourceGraphAnalyzer.IndexVersion);
    await store.SaveAnalysisPlanAsync(plan, cancellationToken);
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "analysis-plan.create", repository.Id, "allowed", now), cancellationToken);
    return Results.Accepted($"/api/v1/analysis-plans/{plan.Id}", ToAnalysisPlanResponse(plan));
});

api.MapGet("/analysis-plans", async (int? limit, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    var plans = await store.ListAnalysisPlansAsync(identity.UserId, Math.Clamp(limit ?? 20, 1, 50), cancellationToken);
    return Results.Ok(plans.Select(ToAnalysisPlanResponse));
});

api.MapGet("/analysis-plans/{id:guid}", async (
    Guid id,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var plan = await AuthorizedPlan(id, context, store, cancellationToken);
    if (plan is null) return Results.NotFound();
    if (plan.ExpiresAt <= DateTimeOffset.UtcNow) return Results.StatusCode(StatusCodes.Status410Gone);
    return Results.Ok(ToAnalysisPlanResponse(plan));
});

api.MapGet("/analysis-plans/{id:guid}/evidence/{changeId}", async (
    Guid id,
    string changeId,
    HttpContext context,
    IAppStore store,
    IGitWorkerClient gitWorker,
    CancellationToken cancellationToken) =>
{
    var plan = await AuthorizedPlan(id, context, store, cancellationToken);
    if (plan is null) return Results.NotFound();
    if (plan.ExpiresAt <= DateTimeOffset.UtcNow || plan.Graph is null)
        return Results.StatusCode(StatusCodes.Status410Gone);
    var candidate = plan.Candidates.FirstOrDefault(item => item.Id.Equals(changeId, StringComparison.Ordinal));
    if (candidate is null) return Results.NotFound();
    var evidence = candidate.EvidenceIds
        .Select(evidenceId => plan.Graph.Evidence.FirstOrDefault(item => item.Id.Equals(evidenceId, StringComparison.Ordinal)))
        .Where(static item => item is not null)
        .OrderByDescending(item => item!.RevisionSha == plan.TargetSha)
        .FirstOrDefault();
    if (evidence is null) return Results.NotFound(new { error = "No source evidence is available for this change." });
    var repository = await store.GetRepositoryAsync(plan.Request.RepositoryId, cancellationToken);
    if (repository is null) return Results.NotFound();
    try
    {
        return Results.Ok(await gitWorker.ReadEvidenceAsync(
            repository, evidence.RevisionSha, evidence.FilePath,
            evidence.StartLine, evidence.EndLine, cancellationToken));
    }
    catch (GitWorkerException exception)
    {
        return Results.BadRequest(new { errorCode = exception.ErrorCode, error = exception.UserMessage });
    }
});

api.MapPut("/analysis-plans/{id:guid}/selection", async (
    Guid id,
    UpdateAnalysisPlanSelectionRequest request,
    HttpContext context,
    IAppStore store,
    DiagramPresetCatalog catalog,
    CancellationToken cancellationToken) =>
{
    var plan = await AuthorizedPlan(id, context, store, cancellationToken);
    if (plan is null) return Results.NotFound();
    if (plan.State != AnalysisPlanState.Ready) return Results.Conflict(new { error = "The pre-analysis is not ready." });
    if (plan.Revision != request.ExpectedRevision)
        return Results.Conflict(new { errorCode = "ANALYSIS_PLAN_REVISION_CONFLICT", error = "The plan was changed in another request. Reload it and try again.", currentRevision = plan.Revision });
    var selectionError = ValidatePlanSelections(request.Groups, plan.Candidates, catalog);
    if (selectionError is not null) return Results.BadRequest(new { error = selectionError });

    var updated = plan with
    {
        Selections = request.Groups.Select(group =>
        {
            var views = group.EffectiveViews().Select(DiagramViewSelectionService.Normalize).ToArray();
            var primary = views[0];
            return group with
            {
                Id = group.Id.Trim(),
                Title = group.Title.Trim(),
                DiagramType = primary.DiagramType,
                PresetId = primary.PresetId,
                Overrides = primary.Overrides,
                Views = views
            };
        }).ToArray(),
        Revision = plan.Revision + 1,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    await store.SaveAnalysisPlanAsync(updated, cancellationToken);
    return Results.Ok(ToAnalysisPlanResponse(updated));
});

api.MapPost("/analysis-plans/{id:guid}/generate", async (
    Guid id,
    GenerateAnalysisPlanRequest request,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var plan = await AuthorizedPlan(id, context, store, cancellationToken);
    if (plan is null) return Results.NotFound();
    if (plan.State != AnalysisPlanState.Ready || plan.BaseSha is null || plan.TargetSha is null)
        return Results.Conflict(new { error = "The pre-analysis is not ready." });
    if (plan.Revision != request.ExpectedRevision)
        return Results.Conflict(new { errorCode = "ANALYSIS_PLAN_REVISION_CONFLICT", error = "Reload the plan before generating diagrams.", currentRevision = plan.Revision });
    if (plan.Selections.Count == 0)
        return Results.BadRequest(new { error = "Select at least one change group." });

    var planViewIds = plan.Selections.SelectMany(static group => group.EffectiveViews())
        .Select(static view => view.Id).ToHashSet(StringComparer.Ordinal);
    if (request.RequestedViewIds?.Any(viewId => !planViewIds.Contains(viewId)) == true)
        return Results.BadRequest(new { error = "RequestedViewIds contains a view that is not in this plan." });

    if (request.SourceAnalysisId is { } sourceAnalysisId)
    {
        var source = await AuthorizedJob(sourceAnalysisId, context, store, cancellationToken);
        if (source?.Result is null || source.Request.AnalysisPlanId != plan.Id)
            return Results.BadRequest(new { error = "The source analysis is not a reusable result for this plan." });
    }

    var now = DateTimeOffset.UtcNow;
    var analyzeRequest = new AnalyzeRequest(
        plan.Request.RepositoryId,
        plan.BaseSha,
        plan.TargetSha,
        "direct",
        plan.Selections.SelectMany(static group => group.EffectiveViews()).Select(static view => view.DiagramType)
            .Distinct(StringComparer.Ordinal).ToArray(),
        1,
        1,
        true,
        plan.Request.EnableThinking,
        plan.Id,
        plan.Selections,
        request.SourceAnalysisId,
        request.RequestedViewIds);
    var job = new AnalysisJob(Guid.NewGuid(), analyzeRequest, AnalysisState.Queued, plan.BaseSha, plan.TargetSha, 0, "Queued", null, null, null, now, now, null);
    await store.SaveAnalysisAsync(job, cancellationToken);
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), context.GetInternalIdentity().UserId, "analysis-plan.generate", plan.Request.RepositoryId, "allowed", now), cancellationToken);
    return Results.Accepted($"/api/v1/analyses/{job.Id}", ToAnalysisResponse(job));
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
        return Results.BadRequest(new { error = "DiagramTypes must contain only flowchart, class, sequence, code-relation, or state." });
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

api.MapPost("/natural-diagrams/{id:guid}/views/revise", async (
    Guid id,
    ReviseNaturalDiagramViewsRequest request,
    HttpContext context,
    NaturalDiagramService service,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var parent = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (parent is null) return Results.NotFound();
    var identity = context.GetInternalIdentity();
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.Forbid();
    if (request.Views is null || request.Views.Count is < 1 or > 4)
        return Results.BadRequest(new { error = "One to four diagram views are required." });
    var requested = request.RegenerateViewIds?.ToHashSet(StringComparer.Ordinal) ?? [];
    if (request.RegenerateViewIds?.Any(viewId => request.Views.All(view => view.Id != viewId)) == true)
        return Results.BadRequest(new { error = "RegenerateViewIds contains an unknown view." });
    try
    {
        var record = await service.ReviseViewsAsync(parent, request.Views, requested, identity.UserId, cancellationToken);
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

api.MapGet("/diagram-artifacts/{rootArtifactId:guid}/revisions", async (
    Guid rootArtifactId,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    return Results.Ok(await store.ListDiagramRevisionsAsync(rootArtifactId, identity.UserId, cancellationToken));
});

api.MapPost("/natural-diagrams/{id:guid}/views/{viewId}/edits", async (
    Guid id,
    string viewId,
    SaveDiagramEditRequest request,
    HttpContext context,
    IAppStore store,
    DiagramRevisionService service,
    CancellationToken cancellationToken) =>
{
    var record = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (record is null) return Results.NotFound();
    var identity = context.GetInternalIdentity();
    if (!CanAccessNaturalDiagram(record, identity.UserId)) return Results.Forbid();
    var artifact = FindNaturalDiagramArtifact(record, viewId);
    if (artifact is null) return Results.NotFound(new { error = "The diagram view does not exist." });
    try
    {
        var revision = await service.SaveAsync(artifact, request, identity.UserId, "natural", record.Id,
            null, viewId, cancellationToken);
        return Results.Created($"/api/v1/diagram-artifacts/{artifact.Id}/revisions/{revision.Id}", revision);
    }
    catch (DiagramRevisionConflictException exception)
    {
        return Results.Conflict(new { errorCode = "DIAGRAM_REVISION_CONFLICT", error = exception.Message, currentVersion = exception.CurrentVersion });
    }
    catch (Exception exception) when (exception is ArgumentException or DiagramValidationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

api.MapPost("/analyses/{id:guid}/groups/{groupId}/views/{viewId}/edits", async (
    Guid id,
    string groupId,
    string viewId,
    SaveDiagramEditRequest request,
    HttpContext context,
    IAppStore store,
    DiagramRevisionService service,
    CancellationToken cancellationToken) =>
{
    var job = await AuthorizedJob(id, context, store, cancellationToken);
    if (job is null) return Results.NotFound();
    var artifact = FindAnalysisDiagramArtifact(job, groupId, viewId);
    if (artifact is null) return Results.NotFound(new { error = "The diagram view does not exist." });
    var identity = context.GetInternalIdentity();
    try
    {
        var revision = await service.SaveAsync(artifact, request, identity.UserId, "analysis", job.Id,
            groupId, viewId, cancellationToken);
        return Results.Created($"/api/v1/diagram-artifacts/{artifact.Id}/revisions/{revision.Id}", revision);
    }
    catch (DiagramRevisionConflictException exception)
    {
        return Results.Conflict(new { errorCode = "DIAGRAM_REVISION_CONFLICT", error = exception.Message, currentVersion = exception.CurrentVersion });
    }
    catch (Exception exception) when (exception is ArgumentException or DiagramValidationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = staticFiles });

app.Run();

static bool CanAccessNaturalDiagram(NaturalDiagramRecord record, string userId) =>
    string.IsNullOrEmpty(record.OwnerUserId) || string.Equals(record.OwnerUserId, userId, StringComparison.Ordinal);

static DiagramArtifact? FindNaturalDiagramArtifact(NaturalDiagramRecord record, string viewId)
{
    if (record.Views is { Count: > 0 })
        return record.Views.FirstOrDefault(view => view.ViewId == viewId)?.Diagram;
    return record.Request.EffectiveViews()[0].Id == viewId ? record.Diagram : null;
}

static DiagramArtifact? FindAnalysisDiagramArtifact(AnalysisJob job, string groupId, string viewId)
{
    var group = job.Result?.DiagramGroups?.FirstOrDefault(item => item.GroupId == groupId);
    if (group is null) return null;
    if (group.Views is { Count: > 0 })
        return group.Views.FirstOrDefault(view => view.ViewId == viewId)?.Diagram;
    return $"{group.GroupId}-view" == viewId ? group.Diagram : null;
}

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

static object ToAnalysisPlanResponse(AnalysisPlan plan) => new
{
    plan.Id,
    plan.Request,
    plan.State,
    plan.BaseSha,
    plan.TargetSha,
    plan.Progress,
    plan.StageMessage,
    ChangedFiles = plan.Comparison?.Files.Select(static file => new
    {
        file.Path,
        file.PreviousPath,
        file.ChangeKind,
        file.BeforeBlobOid,
        file.AfterBlobOid,
        file.Hunks
    }),
    plan.Candidates,
    plan.SuggestedGroups,
    plan.Selections,
    plan.Warnings,
    plan.ErrorCode,
    plan.ErrorMessage,
    plan.Revision,
    plan.CreatedAt,
    plan.UpdatedAt,
    plan.ExpiresAt,
    plan.IndexVersion,
    plan.Exclusions,
    plan.TargetCommitMessage,
    plan.Notices
};

static string? ValidateIndirectCallRules(IReadOnlyList<IndirectCallRule>? rules)
{
    if (rules is null || rules.Count > 50) return "Zero to fifty indirect call rules are allowed.";
    var ids = new HashSet<string>(StringComparer.Ordinal);
    var enabledApis = new HashSet<string>(StringComparer.Ordinal);
    foreach (var rule in rules)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 80 || !ids.Add(rule.Id.Trim()))
            return "Every indirect call rule must have a unique ID of at most 80 characters.";
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 120)
            return "Every indirect call rule needs a name of at most 120 characters.";
        if (!IsCppQualifiedName(rule.ApiName)) return $"Invalid C++ API name: {rule.ApiName}";
        if (rule.Enabled && !enabledApis.Add(rule.ApiName.Trim())) return $"Only one enabled rule is allowed for API '{rule.ApiName.Trim()}'.";
        if (rule.TargetTypeArgumentIndex is < 0 or > 31 || rule.TargetMethodArgumentIndex is < 0 or > 31)
            return "Argument indexes must be between 0 and 31.";
        if (rule.TargetMethodArgumentIndex == rule.TargetTypeArgumentIndex)
            return "The type and method argument indexes must be different.";
        if (rule.Aliases is null || rule.Aliases.Count > 100) return "Each rule may contain up to 100 aliases.";
        if (rule.Aliases.Any(static alias => string.IsNullOrWhiteSpace(alias.Expression) || string.IsNullOrWhiteSpace(alias.TargetType) ||
                                                  alias.Expression.Trim().Length > 160 || alias.TargetType.Trim().Length > 160))
            return "Alias expressions and target types must contain 1-160 characters.";
    }
    return null;
}

static bool IsCppQualifiedName(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160) return false;
    return value.Trim().Split("::", StringSplitOptions.None).All(part => part.Length > 0 &&
        (char.IsLetter(part[0]) || part[0] == '_') && part.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_'));
}

static string? ValidatePlanSelections(
    IReadOnlyList<AnalysisGroupSelection>? groups,
    IReadOnlyList<ChangeCandidate> candidates,
    DiagramPresetCatalog catalog)
{
    if (groups is null || groups.Count is < 1 or > 50) return "One to fifty groups are required.";
    var candidateIds = candidates.Select(static candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
    var usedChanges = new HashSet<string>(StringComparer.Ordinal);
    var usedGroups = new HashSet<string>(StringComparer.Ordinal);
    var usedViews = new HashSet<string>(StringComparer.Ordinal);
    foreach (var group in groups)
    {
        if (string.IsNullOrWhiteSpace(group.Id) || !usedGroups.Add(group.Id.Trim())) return "Every group must have a unique ID.";
        if (string.IsNullOrWhiteSpace(group.Title) || group.Title.Trim().Length > 120) return "Group titles must contain 1-120 characters.";
        if (group.ChangeIds is null || group.ChangeIds.Count == 0) return "Every group must contain at least one change.";
        var views = group.EffectiveViews();
        if (views.Count is < 1 or > 4) return "Every group must contain one to four diagram views.";
        if (views.Select(static view => view.Id).Distinct(StringComparer.Ordinal).Count() != views.Count)
            return "Every diagram view in a group must have a unique ID.";
        if (views.Select(static view => view.DiagramType).Distinct(StringComparer.OrdinalIgnoreCase).Count() != views.Count)
            return "A diagram type can only be selected once per group.";
        foreach (var view in views)
        {
            if (string.IsNullOrWhiteSpace(view.Id) || !usedViews.Add(view.Id.Trim())) return "Every diagram view must have a globally unique ID.";
            if (!DiagramProjectionService.IsSupported(view.DiagramType)) return $"Unsupported diagram type: {view.DiagramType}";
            if (!catalog.Contains(view.DiagramType, view.PresetId)) return $"The preset '{view.PresetId}' does not support {view.DiagramType}.";
            if (view.Overrides?.CallerDepth is < 0 or > 3 || view.Overrides?.CalleeDepth is < 0 or > 3 || view.Overrides?.RelationDepth is < 0 or > 3)
                return "Depth overrides must be between 0 and 3.";
            if (view.Overrides?.Direction is { } viewDirection && viewDirection is not ("LR" or "TB"))
                return "Direction must be LR or TB.";
        }
        foreach (var changeId in group.ChangeIds)
        {
            if (!candidateIds.Contains(changeId)) return $"Unknown change ID: {changeId}";
            if (!usedChanges.Add(changeId)) return $"Change '{changeId}' is assigned to more than one group.";
        }
    }
    return null;
}

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

static async Task<AnalysisPlan?> AuthorizedPlan(Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken)
{
    var plan = await store.GetAnalysisPlanAsync(id, cancellationToken);
    if (plan is null || !plan.OwnerUserId.Equals(context.GetInternalIdentity().UserId, StringComparison.Ordinal)) return null;
    var repository = await store.GetRepositoryAsync(plan.Request.RepositoryId, cancellationToken);
    return repository is not null && context.GetInternalIdentity().CanAccess(repository) ? plan : null;
}

public partial class Program;
