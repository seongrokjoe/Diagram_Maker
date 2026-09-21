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
if (builder.Configuration.GetValue<bool>("CodexTest:Enabled"))
    throw new InvalidOperationException("External inference test mode has been removed.");
var networkPolicy = ApprovedNetworkPolicy.Load();
networkPolicy.ValidateLocalPath(builder.Environment.ContentRootPath);
networkPolicy.ValidateLocalPath(AppContext.BaseDirectory);
builder.Services.AddSingleton(networkPolicy);
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
    networkPolicy.ValidateLocalPath(llmPolicyPath);
    builder.Configuration.AddJsonFile(llmPolicyPath, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
}
else if (!string.IsNullOrWhiteSpace(explicitLlmPolicyPath))
{
    throw new FileNotFoundException("The configured Diagram Maker LLM policy file does not exist.", llmPolicyPath);
}
// App configuration cannot activate a synthetic generation fallback.
builder.Configuration["Llm:AllowDevelopmentStub"] = "false";
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
builder.Services.AddOptions<CodeBlockOptions>().Bind(builder.Configuration.GetSection(CodeBlockOptions.SectionName))
    .Validate(o => o.MaximumBlocks is > 0 and <= 100 && o.MaximumBlockCharacters > 0 && o.MaximumTotalCharacters > 0 &&
        o.MaximumQuestions is >= 0 and <= 5 && o.ParserTimeoutSeconds > 0 && o.HeartbeatSeconds >= 1 &&
        o.LeaseSeconds >= o.HeartbeatSeconds * 3, "Invalid code block limits.").ValidateOnStart();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options => options.AddPolicy("development", policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<IAppStore>(services =>
{
    var options = services.GetRequiredService<IOptions<StorageOptions>>().Value;
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    if (options.Provider.Equals("LocalFile", StringComparison.OrdinalIgnoreCase))
        networkPolicy.ValidateLocalPath(Path.GetFullPath(options.LocalFilePath, environment.ContentRootPath));
    return options.Provider.ToLowerInvariant() switch
    {
        "postgresql" => new PostgresAppStore(networkPolicy.ValidateDatabase(options.ConnectionString ?? throw new InvalidOperationException("Storage:ConnectionString is required."))),
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
    services.GetRequiredService<ILogger<VllmClient>>(), networkPolicy: networkPolicy));
builder.Services.AddSingleton<ILlmCompletionTransport>(services => services.GetRequiredService<VllmClient>());
builder.Services.AddSingleton<StructuredLlmCompletion>();
builder.Services.AddSingleton<IInternalLlmClient, InternalLlmClient>();
builder.Services.AddSingleton<GitWorkerClient>();
builder.Services.AddSingleton<IGitWorkerClient>(services => services.GetRequiredService<GitWorkerClient>());
builder.Services.AddScoped<NaturalDiagramService>();
builder.Services.AddScoped<NaturalDiagramRunProcessor>();
builder.Services.AddHostedService<NaturalDiagramWorker>();
builder.Services.AddScoped<CodeBlockWorkspaceService>();
builder.Services.AddSingleton<CodeBlockAnalyzer>();
builder.Services.AddSingleton<CodeDiagramSelfTest>();
builder.Services.AddSingleton<CodeBlockGroupingService>();
builder.Services.AddSingleton<CodeBlockProjectionService>();
builder.Services.AddScoped<CodeBlockRunProcessor>();
builder.Services.AddHostedService<CodeBlockWorker>();
builder.Services.AddScoped<AnalysisJobProcessor>();
builder.Services.AddScoped<AnalysisPlanProcessor>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<AnalysisPlanWorker>();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    var urls = builder.Configuration["urls"];
    if (string.IsNullOrWhiteSpace(urls) || urls.Split(';').Any(value =>
        !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !System.Net.IPAddress.TryParse(uri.Host, out var address) ||
        !System.Net.IPAddress.IsLoopback(address)))
        throw new InvalidOperationException("Local identity mode requires explicit loopback IP bindings.");
}
// Validate the configured transport before serving requests or starting workers.
_ = app.Services.GetRequiredService<ILlmCompletionTransport>();
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
app.Use(CodeBlockEndpoints.GuardAsync);
var localWebRoot = Path.GetFullPath("../../web/dist", app.Environment.ContentRootPath);
var packagedWebRoot = Path.GetFullPath("wwwroot", AppContext.BaseDirectory);
var selectedWebRoot = app.Environment.IsDevelopment() && Directory.Exists(localWebRoot)
    ? localWebRoot
    : packagedWebRoot;
if (!Directory.Exists(selectedWebRoot))
{
    throw new InvalidOperationException($"Static web directory does not exist: {selectedWebRoot}");
}
networkPolicy.ValidateLocalPath(selectedWebRoot);
var staticFiles = new PhysicalFileProvider(selectedWebRoot);
app.Lifetime.ApplicationStopped.Register(staticFiles.Dispose);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = staticFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = staticFiles });

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<IAppStore>().InitializeAsync(CancellationToken.None);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "diagram-maker-api" }));
app.MapCodeBlocks();

var api = app.MapGroup("/api/v1");
api.MapGet("/runtime-info", (IServiceProvider services) => Results.Ok(new
{
    mode = "normal",
    llmProvider = "internal-vllm",
    llmConfigured = services.GetRequiredService<ILlmCompletionTransport>().IsEnabled,
    codeBlockLimits = services.GetRequiredService<IOptions<CodeBlockOptions>>().Value,
    capabilities = new { thinkingControl = true, exactTokenLimit = true }
}));

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

api.MapGet("/analysis-plans", async (int? limit, bool? summary, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    var plans = await store.ListAnalysisPlansAsync(identity.UserId, Math.Clamp(limit ?? 20, 1, 50), cancellationToken);
    return Results.Ok(plans.Select(plan => ToAnalysisPlanResponse(summary == true ? plan with
        { Comparison = null, Graph = null, Candidates = [], SuggestedGroups = [], Selections = [] } : plan)));
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

api.MapGet("/analysis-plans/{id:guid}/analyses", async (
    Guid id,
    int? limit,
    HttpContext context,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var plan = await AuthorizedPlan(id, context, store, cancellationToken);
    if (plan is null) return Results.NotFound();
    if (plan.ExpiresAt <= DateTimeOffset.UtcNow) return Results.StatusCode(StatusCodes.Status410Gone);
    var jobs = await store.ListAnalysesByPlanAsync(id, Math.Clamp(limit ?? 20, 1, 50), cancellationToken);
    return Results.Ok(jobs.Select(ToAnalysisHistorySummary));
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
    var job = new AnalysisJob(Guid.NewGuid(), analyzeRequest, AnalysisState.Queued, plan.BaseSha, plan.TargetSha, 0, "Queued", null, null, null, now, now, null,
        GenerationVersion: SharedSemanticProjection.Version);
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
    if (request.DiagramTypes?.Any(type => !IsSupportedGitDiagramType(type)) == true)
    {
        return Results.BadRequest(new { error = "DiagramTypes must contain only flowchart, class, sequence, state, or code-relation." });
    }

    var now = DateTimeOffset.UtcNow;
    var job = new AnalysisJob(Guid.NewGuid(), request, AnalysisState.Queued, null, null, 0, "Queued", null, null, null, now, now, null,
        GenerationVersion: SharedSemanticProjection.Version);
    await store.SaveAnalysisAsync(job, cancellationToken);
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "analysis.create", repository.Id, "allowed", now), cancellationToken);
    return Results.Accepted($"/api/v1/analyses/{job.Id}", ToAnalysisResponse(job));
});

api.MapGet("/analyses/{id:guid}", async (Guid id, bool? includeGraph, bool? summary, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var job = await store.GetAnalysisAsync(id, cancellationToken);
    if (job is null) return Results.NotFound();
    var repository = await store.GetRepositoryAsync(job.Request.RepositoryId, cancellationToken);
    if (repository is null || !context.GetInternalIdentity().CanAccess(repository)) return Results.Forbid();
    return Results.Ok(ToAnalysisResponse(summary == true ? CompactAnalysis(job) : job, includeGraph ?? true));
});

api.MapGet("/analyses/{id:guid}/groups/{groupId}/views/{viewId}/pages/{pageId}", async (
    Guid id, string groupId, string viewId, string pageId, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var job = await AuthorizedJob(id, context, store, cancellationToken);
    if (job is null) return Results.NotFound();
    var artifact = FindAnalysisDiagramArtifact(job, groupId, viewId, pageId, context.Request.Query["variant"]);
    return artifact is null ? Results.NotFound() : Results.Ok(artifact);
});

api.MapGet("/analyses/{id:guid}/diagnostics", async (Guid id, HttpContext context, IAppStore store, CancellationToken ct) =>
{
    var job = await AuthorizedJob(id, context, store, ct);
    if (job is not null && context.Request.Query["format"] == "text")
        return Results.Text(LlmDiagnosticReport.Text(job.State.ToString(), job.StopReason, job.Execution, job.Diagnostics), "text/plain; charset=utf-8");
    return job is null ? Results.NotFound() : Results.Ok(new { version = 2, job.Id, job.State, job.StopReason, job.Execution, job.GenerationVersion,
        diagnostics = job.Diagnostics ?? [], checkpointCount = job.Checkpoints?.Count ?? 0 });
});
api.MapPost("/analyses/{id:guid}/cancel", async (Guid id, ResumeSemanticRequest request, HttpContext context, IAppStore store, CancellationToken ct) =>
{
    var job = await AuthorizedJob(id, context, store, ct);
    if (job is null) return Results.NotFound();
    if (job.Revision != request.ExpectedRevision) return Results.Conflict(new { error = "실행 상태가 변경되었습니다." });
    var stopped = job with { State = AnalysisState.Cancelled, StopReason = "user-cancelled", Revision = job.Revision + 1,
        StageMessage = "사용자가 생성을 취소했습니다.", UpdatedAt = DateTimeOffset.UtcNow, LeaseUntil = null };
    return await store.UpdateAnalysisAsync(stopped, job.Revision, ct) ? Results.Ok(ToAnalysisResponse(stopped)) : Results.Conflict();
});
api.MapPost("/analyses/{id:guid}/resume", async (Guid id, ResumeSemanticRequest request, HttpContext context, IAppStore store, CancellationToken ct) =>
{
    var job = await AuthorizedJob(id, context, store, ct);
    if (job is null) return Results.NotFound();
    if (!CanResumeAnalysis(job) || job.Revision != request.ExpectedRevision) return Results.Conflict(new { error = "이어갈 실행 상태가 변경되었습니다." });
    if (job.GenerationVersion != SharedSemanticProjection.Version)
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new AnalysisJob(Guid.NewGuid(), job.Request with { BaseRevision = job.BaseSha ?? job.Request.BaseRevision,
            TargetRevision = job.TargetSha ?? job.Request.TargetRevision, SourceAnalysisId = job.Id, RequestedViewIds = null },
            AnalysisState.Queued, job.BaseSha, job.TargetSha, 0, "저장된 리비전으로 새 분석을 생성합니다.", null, null, null, now, now, null,
            GenerationVersion: SharedSemanticProjection.Version);
        await store.SaveAnalysisAsync(fresh, ct);
        return Results.Accepted($"/api/v1/analyses/{fresh.Id}", ToAnalysisResponse(fresh));
    }
    var resumed = job with { State = AnalysisState.Queued, Revision = job.Revision + 1, StopReason = null, ErrorCode = null, ErrorMessage = null,
        StageMessage = "완료 단위를 재사용하여 이어서 생성합니다.", LeaseUntil = null, UpdatedAt = DateTimeOffset.UtcNow };
    return await store.UpdateAnalysisAsync(resumed, job.Revision, ct) ? Results.Accepted($"/api/v1/analyses/{id}", ToAnalysisResponse(resumed)) : Results.Conflict();
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

        if (job.State is AnalysisState.Completed or AnalysisState.Partial or AnalysisState.Failed or AnalysisState.Cancelled) return;
        await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
    }
});

api.MapGet("/analyses/{id:guid}/evidence/{evidenceId}/snippet", async (
    Guid id, string evidenceId, HttpContext context, IAppStore store, IGitWorkerClient git, CancellationToken cancellationToken) =>
{
    var job = await AuthorizedJob(id, context, store, cancellationToken);
    if (job?.Result is null) return Results.NotFound();
    var evidence = job.Result.Graph.Evidence.FirstOrDefault(item => item.Id == evidenceId);
    if (evidence is null) return Results.NotFound();
    var repository = await store.GetRepositoryAsync(job.Request.RepositoryId, cancellationToken);
    if (repository is null) return Results.NotFound();
    var snippet = await git.ReadEvidenceAsync(repository, evidence.RevisionSha, evidence.FilePath,
        evidence.StartLine, evidence.EndLine, cancellationToken);
    if (snippet.RevisionSha != evidence.RevisionSha || snippet.BlobOid != evidence.BlobOid)
        return Results.Conflict(new { error = "저장된 근거와 Git blob이 일치하지 않습니다." });
    return Results.Ok(snippet);
});

api.MapPost("/natural-diagram-runs", async (
    CreateNaturalDiagramRunRequest input,
    HttpContext context,
    NaturalDiagramService service,
    IAppStore store,
    CancellationToken cancellationToken) =>
{
    var identity = context.GetInternalIdentity();
    NaturalDiagramRequest request;
    try { request = service.ValidateRequest(input.Request); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    NaturalDiagramRecord? source = null;
    if (input.SourceDiagramId is { } sourceId)
    {
        source = await store.GetNaturalDiagramAsync(sourceId, cancellationToken);
        if (source is null) return Results.NotFound(new { error = "The source natural diagram does not exist." });
        if (!CanAccessNaturalDiagram(source, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    else if (input.RegenerateViewIds is { Count: > 0 } || input.RegeneratePageIds is { Count: > 0 })
        return Results.BadRequest(new { error = "Selected-output regeneration requires a source natural diagram." });
    var viewIds = request.EffectiveViews().Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
    if (input.RegenerateViewIds?.Any(viewId => !viewIds.Contains(viewId)) == true)
        return Results.BadRequest(new { error = "RegenerateViewIds contains an unknown view." });
    var knownPageIds = source?.Views?.SelectMany(view => view.Pages ?? []).Select(page => page.Id).ToHashSet(StringComparer.Ordinal) ?? [];
    if (input.RegeneratePageIds?.Any(pageId => !knownPageIds.Contains(pageId)) == true)
        return Results.BadRequest(new { error = "RegeneratePageIds contains an unknown scenario page." });
    var now = DateTimeOffset.UtcNow;
    var run = new NaturalDiagramRun(Guid.NewGuid(), identity.UserId, request, NaturalDiagramRunState.Queued,
        now, now, StageMessage: "실행 대기", SourceDiagramId: source?.Id,
        RegenerateViewIds: input.RegenerateViewIds, RegeneratePageIds: input.RegeneratePageIds);
    if (!await store.CreateNaturalDiagramRunAsync(run, cancellationToken))
        return Results.Conflict(new { error = "The natural diagram run could not be queued." });
    await store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), identity.UserId, "natural-diagram-run.create", null,
        "allowed", now), cancellationToken);
    return Results.Accepted($"/api/v1/natural-diagram-runs/{run.Id}", run);
});

api.MapGet("/natural-diagram-runs", async (int? limit, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListNaturalDiagramRunsAsync(context.GetInternalIdentity().UserId,
        Math.Clamp(limit ?? 20, 1, 50), cancellationToken)).Select(run => run with { Checkpoints = null })));

api.MapGet("/natural-diagram-runs/{id:guid}", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var run = await store.GetNaturalDiagramRunAsync(id, cancellationToken);
    if (run is null) return Results.NotFound();
    return CanAccessNaturalRun(run, context.GetInternalIdentity().UserId) ? Results.Ok(run with { Checkpoints = null }) : Results.StatusCode(StatusCodes.Status403Forbidden);
});

api.MapPost("/natural-diagram-runs/{id:guid}/cancel", async (
    Guid id, NaturalDiagramRunActionRequest request, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var run = await store.GetNaturalDiagramRunAsync(id, cancellationToken);
    if (run is null) return Results.NotFound();
    if (!CanAccessNaturalRun(run, context.GetInternalIdentity().UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (run.Revision != request.ExpectedRevision)
        return Results.Conflict(new { error = "The run changed. Refresh and try again.", currentRevision = run.Revision });
    if (run.IsTerminal) return Results.Conflict(new { error = "The run is already finished.", currentRevision = run.Revision });
    var cancelled = run with { State = NaturalDiagramRunState.Cancelled, Progress = run.Progress,
        StageMessage = "사용자가 실행을 취소했습니다.", Revision = run.Revision + 1,
        UpdatedAt = DateTimeOffset.UtcNow, LeaseId = null, LeaseUntil = null };
    if (!await store.UpdateNaturalDiagramRunAsync(cancelled, run.Revision, null, cancellationToken))
        return Results.Conflict(new { error = "The run changed. Refresh and try again." });
    return Results.Ok(cancelled with { Checkpoints = null });
});

api.MapPost("/natural-diagram-runs/{id:guid}/resume", async (
    Guid id, NaturalDiagramRunActionRequest request, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var run = await store.GetNaturalDiagramRunAsync(id, cancellationToken);
    if (run is null) return Results.NotFound();
    if (!CanAccessNaturalRun(run, context.GetInternalIdentity().UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (run.Revision != request.ExpectedRevision)
        return Results.Conflict(new { error = "The run changed. Refresh and try again.", currentRevision = run.Revision });
    if (run.State is not (NaturalDiagramRunState.Partial or NaturalDiagramRunState.Failed or NaturalDiagramRunState.Cancelled))
        return Results.Conflict(new { error = "Only an interrupted or failed run can be resumed.", currentRevision = run.Revision });
    var queued = run with { State = NaturalDiagramRunState.Queued, StageMessage = "이어하기 대기",
        Revision = run.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow, ErrorCode = null, ErrorMessage = null,
        LeaseId = null, LeaseUntil = null };
    if (!await store.UpdateNaturalDiagramRunAsync(queued, run.Revision, null, cancellationToken))
        return Results.Conflict(new { error = "The run changed. Refresh and try again." });
    return Results.Accepted($"/api/v1/natural-diagram-runs/{run.Id}", queued with { Checkpoints = null });
});

api.MapPost("/natural-diagram-runs/{id:guid}/answers", async (
    Guid id, NaturalAnswersRequest input, HttpContext context, IAppStore store, CancellationToken ct) =>
{
    var run = await store.GetNaturalDiagramRunAsync(id, ct);
    if (run is null) return Results.NotFound();
    if (!CanAccessNaturalRun(run, context.GetInternalIdentity().UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (run.State != NaturalDiagramRunState.NeedsClarification || run.Revision != input.ExpectedRevision ||
        run.QuestionVersion != input.QuestionVersion)
        return Results.Conflict(new { error = "질문 또는 실행이 변경되었습니다. 새로고침 후 다시 답변하세요." });
    var questions = run.Questions ?? [];
    if (input.Answers is null || input.Answers.Count != questions.Count ||
        input.Answers.Any(a => a is null || string.IsNullOrWhiteSpace(a.Text) || a.Text.Length > 1000) ||
        input.Answers.Select(a => a.QuestionId).Distinct().Count() != questions.Count ||
        !input.Answers.Select(a => a.QuestionId).ToHashSet().SetEquals(questions.Select(q => q.Id)))
        return Results.BadRequest(new { error = "각 질문에 1,000자 이내의 답변을 입력하세요." });
    var queued = run with { State = NaturalDiagramRunState.Queued, Revision = run.Revision + 1,
        UpdatedAt = DateTimeOffset.UtcNow, Answers = input.Answers, AnswerVersion = run.AnswerVersion + 1,
        Requirements = null, StageMessage = "답변을 반영하여 생성 대기", LeaseId = null, LeaseUntil = null };
    if (!await store.UpdateNaturalDiagramRunAsync(queued, run.Revision, null, ct))
        return Results.Conflict(new { error = "실행 상태가 변경되었습니다." });
    return Results.Accepted($"/api/v1/natural-diagram-runs/{id}", queued with { Checkpoints = null });
});

api.MapGet("/natural-diagram-runs/{id:guid}/diagnostics", async (
    Guid id, HttpContext context, IAppStore store, CancellationToken ct) =>
{
    var run = await store.GetNaturalDiagramRunAsync(id, ct);
    if (run is null) return Results.NotFound();
    if (!CanAccessNaturalRun(run, context.GetInternalIdentity().UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var report = LlmDiagnosticReport.Text(run.State.ToString(), run.ErrorCode, run.Execution, run.Diagnostics);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(report), "text/plain; charset=utf-8", $"natural-{id}-diagnostics.txt");
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

api.MapGet("/llm/tests/code-diagram-settings", (HttpContext context, IOptions<LlmOptions> options) =>
    context.GetInternalIdentity().Roles.Contains("Admin") ? Results.Ok(LlmDiagnosticReport.Settings(options.Value)) : Results.Forbid());

api.MapPost("/llm/tests/code-diagram-contract", async (HttpContext context, CodeDiagramSelfTest test, CancellationToken ct) =>
{
    if (!context.GetInternalIdentity().Roles.Contains("Admin")) return Results.Forbid();
    var result = await test.RunAsync(ct);
    return context.Request.Query["format"] == "text"
        ? Results.Text(result.Report, "text/plain; charset=utf-8", statusCode: result.Success ? 200 : 503)
        : Results.Ok(result);
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
        : Results.StatusCode(StatusCodes.Status403Forbidden);
});

api.MapGet("/natural-diagrams/{id:guid}/revisions", async (Guid id, HttpContext context, IAppStore store, CancellationToken cancellationToken) =>
{
    var record = await store.GetNaturalDiagramAsync(id, cancellationToken);
    if (record is null) return Results.NotFound();
    var identity = context.GetInternalIdentity();
    if (!CanAccessNaturalDiagram(record, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);

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
    if (!CanAccessNaturalDiagram(parent, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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

api.MapPost("/natural-diagrams/{id:guid}/views/{viewId}/edit-preview", async (
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
    if (!CanAccessNaturalDiagram(record, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var artifact = FindNaturalDiagramArtifact(record, viewId, context.Request.Query["pageId"].FirstOrDefault());
    if (artifact is null) return Results.NotFound(new { error = "The diagram view does not exist." });
    try
    {
        return Results.Ok(await service.PreviewAsync(artifact, request, identity.UserId, cancellationToken));
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

api.MapPost("/analyses/{id:guid}/groups/{groupId}/views/{viewId}/edit-preview", async (
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
    var artifact = FindAnalysisDiagramArtifact(job, groupId, viewId, context.Request.Query["pageId"].FirstOrDefault(), context.Request.Query["variant"]);
    if (artifact is null) return Results.NotFound(new { error = "The diagram view does not exist." });
    var identity = context.GetInternalIdentity();
    try
    {
        return Results.Ok(await service.PreviewAsync(artifact, request, identity.UserId, cancellationToken));
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
    if (!CanAccessNaturalDiagram(record, identity.UserId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var pageId = context.Request.Query["pageId"].FirstOrDefault();
    var artifact = FindNaturalDiagramArtifact(record, viewId, pageId);
    if (artifact is null) return Results.NotFound(new { error = "The diagram view does not exist." });
    try
    {
        var revision = await service.SaveAsync(artifact, request, identity.UserId, "natural", record.Id,
            null, pageId is null ? viewId : viewId + "/" + pageId, cancellationToken);
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
    var artifact = FindAnalysisDiagramArtifact(job, groupId, viewId, context.Request.Query["pageId"].FirstOrDefault(), context.Request.Query["variant"]);
    if (artifact is null) return Results.NotFound(new { error = "The diagram view does not exist." });
    var identity = context.GetInternalIdentity();
    try
    {
        var revision = await service.SaveAsync(artifact, request, identity.UserId, "analysis", job.Id,
            groupId, viewId,
            cancellationToken);
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

static bool CanAccessNaturalRun(NaturalDiagramRun run, string userId) =>
    string.Equals(run.OwnerUserId, userId, StringComparison.Ordinal);

static DiagramArtifact? FindNaturalDiagramArtifact(NaturalDiagramRecord record, string viewId, string? pageId = null)
{
    if (record.Views is { Count: > 0 })
    {
        var view = record.Views.FirstOrDefault(view => view.ViewId == viewId);
        if (pageId is not null) return view?.Pages?.FirstOrDefault(page => page.Id == pageId)?.Diagram;
        return view?.Diagram;
    }
    return record.Request.EffectiveViews()[0].Id == viewId ? record.Diagram : null;
}

static DiagramArtifact? FindAnalysisDiagramArtifact(AnalysisJob job, string groupId, string viewId, string? pageId = null, string? variant = null)
{
    DiagramArtifact? Legacy(DiagramArtifact? artifact) => artifact is null ? null :
        DiagramVariants.Select(new DiagramPage("overview", artifact.Ir.Title, artifact), variant);
    var group = job.Result?.DiagramGroups?.FirstOrDefault(item => item.GroupId == groupId);
    if (group is null) return null;
    if (group.Views is { Count: > 0 })
    {
        var view = group.Views.FirstOrDefault(item => item.ViewId == viewId);
        if (view?.Document is { } document)
            return document.Pages.FirstOrDefault(page => page.Id == (pageId ?? document.OverviewPageId)) is { } page ? DiagramVariants.Select(page, variant) : null;
        return pageId is null or "overview" ? Legacy(view?.Diagram) : null;
    }
    return $"{group.GroupId}-view" == viewId && pageId is null or "overview" ? Legacy(group.Diagram) : null;
}

static AnalysisJob CompactAnalysis(AnalysisJob job)
{
    if (job.Result is null) return job;
    DiagramArtifact? Summary(DiagramArtifact? artifact) => artifact is null ? null : artifact with
    { MermaidDsl = "", Explanation = null, Ir = artifact.Ir with { Nodes = [], Edges = [], SequenceBlocks = null } };
    return job with { Result = job.Result with
    {
        Diagrams = job.Result.DiagramGroups is { Count: > 0 } ? [] : job.Result.Diagrams,
        DiagramGroups = job.Result.DiagramGroups?.Select(group => group with
        {
            Understanding = null,
            Diagram = group.Views is { Count: > 0 } ? null : Summary(group.Diagram),
            Views = group.Views?.Select(view => view with
            {
                Diagram = Summary(view.Diagram),
                Document = view.Document is null ? null : view.Document with
                { Pages = view.Document.Pages.Select(page => page with {
                    ResultKind = page.ResultKind ?? (DiagramVariants.IsAi(page.Diagram) ? "semantic" : "static"),
                    Diagram = Summary(page.Diagram)!, CodeDiagram = Summary(page.CodeDiagram) }).ToArray() }
            }).ToArray()
        }).ToArray()
    } };
}

static object ToAnalysisResponse(AnalysisJob job, bool includeGraph = true) => new
{
    job.Id,
    job.Request.TestMetadata,
    job.State,
    job.BaseSha,
    job.TargetSha,
    job.Progress,
    job.StageMessage,
    job.Revision,
    job.Execution,
    job.StopReason,
    CanResume = CanResumeAnalysis(job),
    ResultCounts = DiagramVariants.CountAnalysis(job.Result?.DiagramGroups ?? [], InMemoryAppStore.IsAnalysisTerminal(job.State)),
    Result = job.Result is null
        ? null
        : includeGraph
            ? (object)job.Result
            : new
            {
                job.Result.ChangedFiles,
                job.Result.Narrative,
                job.Result.Diagrams,
                job.Result.DiagramAvailability,
                job.Result.DiagramGroups
            },
    job.ErrorCode,
    job.ErrorMessage,
    job.CreatedAt,
    job.UpdatedAt
};

static bool CanResumeAnalysis(AnalysisJob job) => job.State is AnalysisState.Partial or AnalysisState.Failed or AnalysisState.Cancelled &&
    DiagramMaker.Services.LlmFailure.CanResume(job.Checkpoints, job.StopReason);

static AnalysisHistorySummary ToAnalysisHistorySummary(AnalysisJob job)
{
    var groups = job.Result?.DiagramGroups ?? [];
    var views = groups.SelectMany(static group => group.Views ?? []).ToArray();
    return new AnalysisHistorySummary(
        job.Id,
        job.State,
        job.CreatedAt,
        job.UpdatedAt,
        job.BaseSha,
        job.TargetSha,
        job.Result is not null,
        groups.Count,
        groups.Count(static group => group.Diagram is not null || group.Views?.Any(static view => view.Diagram is not null) == true),
        views.Length,
        views.Count(static view => view.Diagram is not null));
}

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
        if (views.Count is < 1 or > 5) return "Every group must contain one to five diagram views.";
        if (views.Select(static view => view.Id).Distinct(StringComparer.Ordinal).Count() != views.Count)
            return "Every diagram view in a group must have a unique ID.";
        if (views.Select(static view => view.DiagramType).Distinct(StringComparer.OrdinalIgnoreCase).Count() != views.Count)
            return "A diagram type can only be selected once per group.";
        foreach (var view in views)
        {
            if (string.IsNullOrWhiteSpace(view.Id) || !usedViews.Add(view.Id.Trim())) return "Every diagram view must have a globally unique ID.";
            if (!IsSupportedGitDiagramType(view.DiagramType)) return $"Unsupported Git analysis diagram type: {view.DiagramType}";
            if (!catalog.Contains(view.DiagramType, view.PresetId)) return $"The preset '{view.PresetId}' does not support {view.DiagramType}.";
            if (view.RefinementInstruction?.Trim().Length > 500)
                return "Diagram refinement instructions must contain at most 500 characters.";
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

static bool IsSupportedGitDiagramType(string type) => type.Trim().ToLowerInvariant() is
    "flowchart" or "class" or "sequence" or "state" or "code-relation";

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
