using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Security;
using DiagramMaker.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace DiagramMaker.Services;

public static class CodeBlockEndpoints
{
    public static bool IsCodeBlockPath(PathString path) => path.StartsWithSegments("/api/v1/code-block-workspaces") ||
        path.StartsWithSegments("/api/v1/code-block-runs");

    public static async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        if (IsCodeBlockPath(context.Request.Path))
        {
            var origin = context.Request.Headers.Origin.ToString();
            var development = context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
            if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}" &&
                !(development && origin == "http://localhost:5173"))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { errorCode = "ORIGIN_NOT_ALLOWED", error = "허용되지 않은 출처입니다." });
                return;
            }
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = context.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<DiagramMaker.Configuration.CodeBlockOptions>>().Value.MaximumRequestBytes;
        }
        await next(context);
    }

    public static void MapCodeBlocks(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "작업 또는 결과를 찾을 수 없습니다." }); }
            catch (CodeBlockConflictException e) { return Results.Conflict(new { errorCode = "CODE_BLOCK_CONFLICT", error = e.Message }); }
            catch (DiagramRevisionConflictException e) { return Results.Conflict(new { errorCode = "REVISION_CONFLICT", error = e.Message, currentRevision = e.CurrentVersion }); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (DiagramValidationException e) { return Results.BadRequest(new { error = e.Message }); }
        });
        api.MapGet("/code-block-workspaces", async (int? limit, HttpContext context, IAppStore store, CancellationToken ct) =>
            Results.Ok((await store.ListCodeBlockWorkspacesAsync(Owner(context), Math.Clamp(limit ?? 20, 1, 100), ct))
                .Select(w => new CodeBlockWorkspaceSummary(w.Id, w.Revision, w.Input.Title, w.Input.Blocks.Count, w.UpdatedAt))));
        api.MapPost("/code-block-workspaces", async (CodeBlockWorkspaceInput input, HttpContext context,
            CodeBlockWorkspaceService service, IAppStore store, CancellationToken ct) =>
        {
            var workspace = await service.CreateAsync(input, Owner(context), ct);
            await Audit(store, context, "code-block.create", ct);
            return Results.Created($"/api/v1/code-block-workspaces/{workspace.Id}", workspace);
        });
        api.MapGet("/code-block-workspaces/{id:guid}", async (Guid id, HttpContext context, CodeBlockWorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.GetWorkspaceAsync(id, Owner(context), ct)));
        api.MapPut("/code-block-workspaces/{id:guid}", async (Guid id, SaveCodeBlockWorkspaceRequest request,
            HttpContext context, CodeBlockWorkspaceService service, CancellationToken ct) => Results.Ok(await service.SaveAsync(id, request, Owner(context), ct)));
        api.MapDelete("/code-block-workspaces/{id:guid}", async (Guid id, int expectedRevision, HttpContext context,
            CodeBlockWorkspaceService service, IAppStore store, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, expectedRevision, Owner(context), ct);
            await Audit(store, context, "code-block.delete", ct);
            return Results.Ok(new { deleted = true });
        });
        api.MapPost("/code-block-workspaces/{id:guid}/runs", async (Guid id, StartCodeBlockRunRequest request, HttpContext context,
            CodeBlockWorkspaceService service, IAppStore store, CancellationToken ct) =>
        {
            var run = await service.StartAsync(id, request, Owner(context), ct);
            await Audit(store, context, "code-block.generate", ct);
            return Accepted(run);
        });
        api.MapGet("/code-block-workspaces/{id:guid}/runs", async (Guid id, int? limit, HttpContext context,
            CodeBlockWorkspaceService service, IAppStore store, CancellationToken ct) =>
        {
            await service.GetWorkspaceAsync(id, Owner(context), ct);
            return Results.Ok((await store.ListCodeBlockRunsAsync(id, Math.Clamp(limit ?? 20, 1, 100), ct)).Select(CodeBlockWorkspaceService.Summary));
        });
        api.MapGet("/code-block-runs/{id:guid}", async (Guid id, HttpContext context, CodeBlockWorkspaceService service, CancellationToken ct) =>
            Results.Ok(CodeBlockWorkspaceService.Summary(await service.GetRunAsync(id, Owner(context), ct))));
        api.MapGet("/code-block-runs/{id:guid}/input", async (Guid id, HttpContext context, CodeBlockWorkspaceService service, CancellationToken ct) =>
            Results.Ok((await service.GetRunAsync(id, Owner(context), ct)).Snapshot));
        api.MapPost("/code-block-runs/{id:guid}/answers", async (Guid id, AnswerCodeBlockQuestionsRequest request,
            HttpContext context, CodeBlockWorkspaceService service, CancellationToken ct) => Accepted(await service.AnswerAsync(id, request, Owner(context), ct)));
        api.MapPost("/code-block-runs/{id:guid}/cancel", async (Guid id, HttpContext context, CodeBlockWorkspaceService service, CancellationToken ct) =>
            Results.Ok(CodeBlockWorkspaceService.Summary(await service.CancelAsync(id, Owner(context), ct))));
        api.MapPost("/code-block-runs/{id:guid}/resume", async (Guid id, ResumeSemanticRequest request, HttpContext context,
            CodeBlockWorkspaceService service, CancellationToken ct) => Accepted(await service.ResumeAsync(id, request, Owner(context), ct)));
        api.MapGet("/code-block-runs/{id:guid}/diagnostics", async (Guid id, HttpContext context,
            CodeBlockWorkspaceService service, CancellationToken ct) =>
        {
            var run = await service.GetRunAsync(id, Owner(context), ct);
            if (context.Request.Query["format"] == "text")
                return Results.Text(LlmDiagnosticReport.Text(run.State.ToString(), run.StopReason, run.Execution, run.Diagnostics), "text/plain; charset=utf-8");
            return Results.Ok(new { version = 2, run.Id, run.State, run.StopReason, run.Execution, run.GenerationVersion,
                diagnostics = run.Diagnostics ?? [], checkpointCount = run.Checkpoints?.Count ?? 0 });
        });
        api.MapGet("/code-block-runs/{id:guid}/groups/{groupId}/views/{viewId}/pages/{pageId}", async (Guid id,
            string groupId, string viewId, string pageId, HttpContext context, CodeBlockWorkspaceService service, CancellationToken ct) =>
            Results.Ok(CodeBlockWorkspaceService.Page(await service.GetRunAsync(id, Owner(context), ct), groupId, viewId, pageId)));
        api.MapGet("/code-block-runs/{id:guid}/evidence/{evidenceId}", async (Guid id, string evidenceId, HttpContext context,
            CodeBlockWorkspaceService service, IAppStore store, CancellationToken ct) =>
        {
            var run = await service.GetRunAsync(id, Owner(context), ct);
            var evidence = run.Graph?.Evidence.FirstOrDefault(e => e.Id == evidenceId);
            if (evidence is null && (run.Results ?? []).SelectMany(g => g.Views).SelectMany(v => v.Pages).Any(p =>
                p.Diagram.Ir.Nodes.Any(n => n.EvidenceIds.Contains(evidenceId)) || p.Diagram.Ir.Edges.Any(e => e.EvidenceIds.Contains(evidenceId))))
            {
                // A failed regeneration can retain a previous successful page. Its
                // hash-bound evidence must still resolve against that older snapshot.
                var previous = (await store.ListCodeBlockRunsAsync(run.WorkspaceId, int.MaxValue, ct)).FirstOrDefault(r =>
                    r.OwnerUserId == run.OwnerUserId && r.Graph?.Evidence.Any(e => e.Id == evidenceId) == true);
                if (previous is not null) { run = previous; evidence = run.Graph!.Evidence.First(e => e.Id == evidenceId); }
            }
            if (evidence is null) throw new KeyNotFoundException();
            var block = run.Snapshot.Blocks.First(b => b.Id == evidence.BlockId);
            var lines = block.Code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            var location = evidence.Location;
            return Results.Ok(new CodeBlockEvidenceSnippet(block.Id, block.Title, location.StartLine, location.EndLine,
                string.Join('\n', lines.Skip(location.StartLine - 1).Take(location.EndLine - location.StartLine + 1)), evidence.ContentHash));
        });
        api.MapPost("/code-block-runs/{id:guid}/groups/{groupId}/views/{viewId}/pages/{pageId}/edit-preview", async (Guid id,
            string groupId, string viewId, string pageId, SaveDiagramEditRequest request, HttpContext context,
            CodeBlockWorkspaceService service, DiagramRevisionService revisions, CancellationToken ct) =>
        {
            var page = CodeBlockWorkspaceService.Page(await service.GetRunAsync(id, Owner(context), ct), groupId, viewId, pageId);
            return Results.Ok(await revisions.PreviewAsync(page, request, Owner(context), ct));
        });
        api.MapPost("/code-block-runs/{id:guid}/groups/{groupId}/views/{viewId}/pages/{pageId}/edits", async (Guid id,
            string groupId, string viewId, string pageId, SaveDiagramEditRequest request, HttpContext context,
            CodeBlockWorkspaceService service, DiagramRevisionService revisions, CancellationToken ct) =>
        {
            var page = CodeBlockWorkspaceService.Page(await service.GetRunAsync(id, Owner(context), ct), groupId, viewId, pageId);
            return Results.Ok(await revisions.SaveAsync(page, request, Owner(context), "code-block", id, groupId, viewId, ct));
        });
    }
    private static string Owner(HttpContext context) => context.GetInternalIdentity().UserId;
    private static IResult Accepted(CodeBlockRun run) => Results.Accepted($"/api/v1/code-block-runs/{run.Id}", CodeBlockWorkspaceService.Summary(run));
    private static Task Audit(IAppStore store, HttpContext context, string action, CancellationToken ct) =>
        store.SaveAuditAsync(new AuditEvent(Guid.NewGuid(), Owner(context), action, null, "allowed", DateTimeOffset.UtcNow), ct);
}
