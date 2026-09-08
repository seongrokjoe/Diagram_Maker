using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Security;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SampleGenerateRequest(Guid? PlanId, string RefinementId = "default",
    IReadOnlyList<string>? DiagramTypes = null, string? PresetId = null,
    string? Direction = null, string? DetailLevel = null, int? CallerDepth = null,
    int? CalleeDepth = null, int? RelationDepth = null, bool FocusOnChanges = false);

public static partial class SampleTestEndpoints
{
    [GeneratedRegex(@"^/api/v1/(?:natural-diagrams/[0-9a-fA-F-]+/views/[^/]+|analyses/[0-9a-fA-F-]+/groups/[^/]+/views/[^/]+)/(?:edits|edit-preview)$")]
    private static partial Regex LocalEditPath();

    public static bool IsAllowedRequest(string method, string path)
    {
        if (method is "GET" or "HEAD") return true;
        if (method != "POST") return false;
        return path.StartsWith("/api/v1/sample-tests/", StringComparison.Ordinal) || LocalEditPath().IsMatch(path) ||
            path is "/api/v1/llm/tests/connection" or "/api/v1/llm/tests/diagram-contract";
    }

    public static async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var sameOrigin = string.IsNullOrEmpty(origin) || origin == $"http://{context.Request.Host}";
        if (!sameOrigin || context.Request.Host.Host != "127.0.0.1" ||
            !IsAllowedRequest(context.Request.Method, context.Request.Path.Value ?? ""))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { errorCode = "SAMPLE_ONLY", error = "이 테스트 모드에서는 준비된 샘플·수정 요청과 로컬 편집만 사용할 수 있습니다." });
            return;
        }
        try { await next(context); }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status400BadRequest && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { errorCode = "SAMPLE_INVALID", error = "허용된 샘플 ID와 선택형 옵션만 전달하세요." });
        }
    }

    public static void MapSampleTests(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/sample-tests");
        api.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok(new { status = "stopping" });
        });
        api.MapGet("/scenarios", (CodexSampleCatalog catalog) => Results.Ok(new
        {
            catalog.Assets.Version, Scenarios = catalog.Assets.Scenarios, Refinements = CodexSampleCatalog.Refinements
        }));
        api.MapPost("/{scenarioId}/plan", async (string scenarioId, HttpContext context, CodexSampleCatalog catalog,
            IAppStore store, CancellationToken token) =>
        {
            try
            {
                var scenario = catalog.Scenario(scenarioId);
                if (scenario.Kind != "git") return Results.BadRequest(new { error = "Git 샘플을 선택하세요." });
                var fixture = catalog.Manifest.Repositories.Single(item => item.ScenarioId == scenarioId);
                var repository = await store.GetRepositoryAsync(fixture.RepositoryId, token) ?? throw new ArgumentException("샘플을 준비하지 못했습니다.");
                await catalog.VerifyRepositoryAsync(repository, token);
                var now = DateTimeOffset.UtcNow;
                var request = new AnalysisPlanRequest(repository.Id, fixture.TargetSha, fixture.BaseSha, UseLlmGrouping: true);
                var plan = new AnalysisPlan(Guid.NewGuid(), context.GetInternalIdentity().UserId, request,
                    AnalysisPlanState.Queued, null, null, 0, "샘플 변경점 분석 대기", null, null, [], [], [], [], null, null,
                    0, now, now, now.AddDays(30), null, SourceGraphAnalyzer.IndexVersion);
                await store.SaveAnalysisPlanAsync(plan, token);
                return Results.Accepted($"/api/v1/analysis-plans/{plan.Id}", new { plan.Id });
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { errorCode = "SAMPLE_INVALID", error = exception.Message }); }
        });
        api.MapPost("/{scenarioId}/generate", async (string scenarioId, SampleGenerateRequest input, HttpContext context,
            CodexSampleCatalog catalog, DiagramPresetCatalog presets, IAppStore store, NaturalDiagramService natural, CancellationToken token) =>
        {
            try
            {
                var scenario = catalog.Scenario(scenarioId);
                var refinement = CodexSampleCatalog.Refinement(input.RefinementId);
                var views = CreateViews(scenario.Kind, input, refinement, presets);
                var metadata = new SampleGenerationMetadata("codex-cli", scenario.Id, catalog.Assets.Version, refinement.Id);
                var owner = context.GetInternalIdentity().UserId;
                if (scenario.Kind == "natural")
                {
                    // Never load a parent/manual revision into an outbound prompt.
                    var request = new NaturalDiagramRequest(scenario.Prompt! + "\n" + refinement.Instruction,
                        views[0].DiagramType, ForceRegenerate: true, PresetId: views[0].PresetId, Views: views, TestMetadata: metadata);
                    var record = await natural.GenerateAsync(request, owner, token);
                    return Results.Ok(new { kind = "natural", record });
                }
                var plan = input.PlanId is { } id ? await store.GetAnalysisPlanAsync(id, token) : null;
                var fixture = catalog.Manifest.Repositories.Single(item => item.ScenarioId == scenarioId);
                if (plan is null || plan.OwnerUserId != owner || plan.Request.RepositoryId != fixture.RepositoryId ||
                    plan.BaseSha != fixture.BaseSha || plan.TargetSha != fixture.TargetSha || plan.State != AnalysisPlanState.Ready)
                    return Results.Conflict(new { error = "선택한 샘플의 변경점 분석이 완료된 초안을 먼저 선택하세요." });
                var repository = await store.GetRepositoryAsync(fixture.RepositoryId, token) ?? throw new ArgumentException("샘플 저장소를 찾지 못했습니다.");
                await catalog.VerifyRepositoryAsync(repository, token);
                if (plan.Candidates.Count == 0) return Results.BadRequest(new { error = "분석된 샘플 변경점이 없습니다." });
                var group = new AnalysisGroupSelection("sample-changes", scenario.Title, plan.Candidates.Select(item => item.Id).ToArray(),
                    views[0].DiagramType, views[0].PresetId, Views: views);
                var now = DateTimeOffset.UtcNow;
                // Rebuild selection from trusted data. Do not forward saved manual labels, titles or arbitrary instructions.
                var requestAnalysis = new AnalyzeRequest(repository.Id, fixture.BaseSha, fixture.TargetSha,
                    DiagramTypes: views.Select(view => view.DiagramType).ToArray(), AnalysisPlanId: plan.Id,
                    Groups: [group], TestMetadata: metadata);
                var job = new AnalysisJob(Guid.NewGuid(), requestAnalysis, AnalysisState.Queued, fixture.BaseSha, fixture.TargetSha,
                    0, "Codex 샘플 생성 대기", null, null, null, now, now, null);
                await store.SaveAnalysisAsync(job, token);
                return Results.Accepted($"/api/v1/analyses/{job.Id}", new { kind = "git", id = job.Id });
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { errorCode = "SAMPLE_INVALID", error = exception.Message }); }
            catch (LlmClientException exception) { return Results.Json(new { errorCode = exception.Code, error = exception.Message }, statusCode: 502); }
            catch (DiagramGenerationException exception) { return Results.Json(new { errorCode = exception.Code, error = exception.Message }, statusCode: 502); }
        });
    }

    public static DiagramViewSelection[] CreateViews(string kind, SampleGenerateRequest input, SampleRefinement refinement, DiagramPresetCatalog presets)
    {
        string[] allowed = kind == "git" ? ["flowchart", "sequence", "class", "code-relation"] : ["flowchart", "sequence", "class", "state"];
        var types = input.DiagramTypes ?? allowed;
        if (types.Count is < 1 or > 4 || types.Distinct().Count() != types.Count || types.Any(type => !allowed.Contains(type)) ||
            input.Direction is not (null or "LR" or "TB") || input.DetailLevel is not (null or "compact" or "balanced" or "detailed") ||
            input.CallerDepth is < 0 or > 3 || input.CalleeDepth is < 0 or > 3 || input.RelationDepth is < 0 or > 3)
            throw new ArgumentException("허용된 다이어그램 형식과 옵션을 선택하세요.");
        return types.Select(type =>
        {
            var preset = input.PresetId is null ? presets.List(type).First(item => item.DetailLevel == "balanced") :
                presets.List(type).SingleOrDefault(item => item.Id == input.PresetId) ?? throw new ArgumentException("등록된 표시 옵션을 선택하세요.");
            return new DiagramViewSelection($"sample-{type}", type, preset.Id,
                new DiagramStyleOverrides(input.Direction, input.DetailLevel ?? (refinement.Id == "detail" ? "detailed" : null),
                    input.CallerDepth, input.CalleeDepth, input.RelationDepth), input.FocusOnChanges,
                string.IsNullOrEmpty(refinement.Instruction) ? null : refinement.Instruction);
        }).ToArray();
    }
}
