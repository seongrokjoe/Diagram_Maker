using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public sealed class CodeBlockConflictException(string message) : Exception(message);

public sealed partial class CodeBlockWorkspaceService(IAppStore store, IOptions<CodeBlockOptions> options,
    DiagramPresetCatalog presets)
{
    public static readonly string[] DiagramTypes = ["flowchart", "sequence", "class", "state", "code-relation"];

    public async Task<CodeBlockWorkspace> GetWorkspaceAsync(Guid id, string owner, CancellationToken cancellationToken)
    {
        var workspace = await store.GetCodeBlockWorkspaceAsync(id, cancellationToken);
        return workspace is not null && workspace.OwnerUserId == owner ? workspace : throw new KeyNotFoundException();
    }
    public async Task<CodeBlockRun> GetRunAsync(Guid id, string owner, CancellationToken cancellationToken)
    {
        var run = await store.GetCodeBlockRunAsync(id, cancellationToken);
        if (run is null || run.OwnerUserId != owner) throw new KeyNotFoundException();
        await GetWorkspaceAsync(run.WorkspaceId, owner, cancellationToken);
        return run;
    }
    public async Task<CodeBlockWorkspace> CreateAsync(CodeBlockWorkspaceInput input, string owner, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new CodeBlockWorkspace(Guid.NewGuid(), owner, 1, ValidateInput(input), now, now);
        if (!await store.SaveCodeBlockWorkspaceAsync(workspace, 0, cancellationToken)) throw new CodeBlockConflictException("작업 생성이 충돌했습니다.");
        return workspace;
    }
    public async Task<CodeBlockWorkspace> SaveAsync(Guid id, SaveCodeBlockWorkspaceRequest request, string owner, CancellationToken cancellationToken)
    {
        var current = await GetWorkspaceAsync(id, owner, cancellationToken);
        if (current.Revision != request.ExpectedRevision) throw new CodeBlockConflictException("입력이 변경되었습니다. 최신 작업을 다시 여세요.");
        var input = ValidateInput(request.Input);
        var changedBlocks = current.Input.Blocks.Where(old => input.Blocks.All(b => b.Id != old.Id || b.Code != old.Code || b.Language != old.Language))
            .Select(b => b.Id).ToHashSet();
        // A retained relation tied to changed code must be deliberately re-entered by the user.
        var oldRelationIds = (current.Input.Relations ?? []).Select(r => r.Id).ToHashSet();
        input = input with { Relations = (input.Relations ?? []).Where(r => !oldRelationIds.Contains(r.Id) ||
            !changedBlocks.Contains(r.FromBlockId) && !changedBlocks.Contains(r.ToBlockId)).ToArray() };
        var updated = current with { Input = input, Revision = current.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        if (!await store.SaveCodeBlockWorkspaceAsync(updated, request.ExpectedRevision, cancellationToken))
            throw new CodeBlockConflictException("다른 요청에서 작업을 수정했습니다. 최신 작업을 다시 여세요.");
        return updated;
    }
    public async Task DeleteAsync(Guid id, int expectedRevision, string owner, CancellationToken cancellationToken)
    {
        await GetWorkspaceAsync(id, owner, cancellationToken);
        if (!await store.DeleteCodeBlockWorkspaceAsync(id, owner, expectedRevision, cancellationToken))
            throw new CodeBlockConflictException("삭제 전에 최신 작업을 다시 여세요.");
    }
    public async Task<CodeBlockRun> StartAsync(Guid id, StartCodeBlockRunRequest request, string owner, CancellationToken cancellationToken)
    {
        var workspace = await GetWorkspaceAsync(id, owner, cancellationToken);
        if (workspace.Revision != request.ExpectedRevision) throw new CodeBlockConflictException("입력이 변경되었습니다. 최신 입력을 저장한 뒤 생성하세요.");
        var input = ValidateInput(workspace.Input, forGeneration: true);
        if (request.RegenerateViewIds is { } requested && (requested.Count > 100 || requested.Any(id => !SafeId().IsMatch(id))))
            throw new ArgumentException("재생성할 형식 ID가 올바르지 않습니다.");
        var now = DateTimeOffset.UtcNow;
        var run = new CodeBlockRun(Guid.NewGuid(), id, owner, workspace.Revision, 1, input,
            CodeBlockRunState.Queued, 0, "코드 분석 대기", now, now, RegenerateViewIds: request.RegenerateViewIds);
        if (request.RegenerateViewIds is { Count: > 0 })
        {
            var previous = (await store.ListCodeBlockRunsAsync(id, 100, cancellationToken)).FirstOrDefault(r => r.IsTerminal && r.InputRevision == workspace.Revision && r.Results is { Count: > 0 });
            if (previous is null || request.RegenerateViewIds.Any(viewId => previous.Results!.All(g => g.Views.All(v => v.ViewId != viewId))))
                throw new ArgumentException("현재 입력에서 생성된 결과의 형식 ID를 선택하세요.");
            run = run with { Groups = previous.Results!.Select(g =>
                (previous.Groups?.FirstOrDefault(p => p.Id == g.GroupId) ??
                    input.Groups?.FirstOrDefault(p => p.Id == g.GroupId) ?? new CodeBlockGroupSelection(g.GroupId, g.Title, g.BlockIds))
                with { Views = g.Views.Select(v => v.Selection).ToArray() }).ToArray(),
                Graph = previous.Graph, Questions = previous.Questions, Answers = previous.Answers, QuestionsResolved = previous.QuestionsResolved };
        }
        if (!await store.CreateCodeBlockRunAsync(run, cancellationToken))
            throw new CodeBlockConflictException("이미 진행 중이거나 질문 대기 중인 실행이 있습니다. 기존 실행을 완료하거나 취소하세요.");
        return run;
    }
    public async Task<CodeBlockRun> AnswerAsync(Guid id, AnswerCodeBlockQuestionsRequest request, string owner, CancellationToken cancellationToken)
    {
        var run = await GetRunAsync(id, owner, cancellationToken);
        var workspace = await GetWorkspaceAsync(run.WorkspaceId, owner, cancellationToken);
        if (run.State != CodeBlockRunState.NeedsClarification || run.Revision != request.ExpectedRunRevision ||
            run.InputRevision != request.ExpectedInputRevision || workspace.Revision != run.InputRevision)
            throw new CodeBlockConflictException("질문 또는 입력이 변경되었습니다. 최신 실행을 다시 여세요.");
        var questions = (run.Questions ?? []).ToDictionary(q => q.Id);
        if (request.Answers is null || request.Answers.Count > questions.Count || request.Answers.Any(a => a is null) ||
            request.Answers.Select(a => a.QuestionId).Distinct().Count() != request.Answers.Count ||
            request.Answers.Any(a => !questions.TryGetValue(a.QuestionId, out var q) ||
                a.OptionId is not ("none" or "unknown") && q.Options.All(o => o.Id != a.OptionId)))
            throw new ArgumentException("질문 답변이 올바르지 않습니다.");
        if (!request.SkipRemaining && questions.Keys.Any(q => request.Answers.All(a => a.QuestionId != q)))
            throw new ArgumentException("모든 질문에 답하거나 나머지 건너뛰기를 선택하세요.");
        var answers = questions.Keys.Select(q => request.Answers.FirstOrDefault(a => a.QuestionId == q) ?? new CodeBlockAnswer(q, "unknown")).ToArray();
        var updated = run with { Answers = answers, QuestionsResolved = true, State = CodeBlockRunState.Queued,
            Revision = run.Revision + 1, StageMessage = "관계 답변 적용 대기", UpdatedAt = DateTimeOffset.UtcNow };
        if (!await store.SaveCodeBlockRunAsync(updated, run.Revision, cancellationToken)) throw new CodeBlockConflictException("답변 저장이 충돌했습니다.");
        return updated;
    }
    public async Task<CodeBlockRun> CancelAsync(Guid id, string owner, CancellationToken cancellationToken)
    {
        var run = await GetRunAsync(id, owner, cancellationToken);
        if (run.IsTerminal) return run;
        var cancelled = run with { State = CodeBlockRunState.Cancelled, Revision = run.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow, StageMessage = "사용자가 생성을 취소했습니다.", StopReason = "user-cancelled" };
        if (!await store.SaveCodeBlockRunAsync(cancelled, run.Revision, cancellationToken)) throw new CodeBlockConflictException("실행 상태가 바뀌었습니다. 다시 확인하세요.");
        return cancelled;
    }

    public async Task<CodeBlockRun> ResumeAsync(Guid id, ResumeSemanticRequest request, string owner, CancellationToken cancellationToken)
    {
        var source = await GetRunAsync(id, owner, cancellationToken);
        var workspace = await GetWorkspaceAsync(source.WorkspaceId, owner, cancellationToken);
        if (!CanResume(source) || source.Revision != request.ExpectedRevision || workspace.Revision != source.InputRevision)
            throw new CodeBlockConflictException("실행 또는 입력이 변경되었습니다. 최신 입력에서 다시 생성하세요.");
        var now = DateTimeOffset.UtcNow;
        var resumed = source with { Id = Guid.NewGuid(), Revision = 1, State = CodeBlockRunState.Queued,
            StageMessage = "완료 단위를 재사용하여 이어서 생성합니다.", CreatedAt = now, UpdatedAt = now,
            LeaseId = null, LeaseUntil = null, ErrorCode = null, ErrorMessage = null, StopReason = null,
            SourceRunId = source.Id, Execution = null };
        if (!await store.CreateCodeBlockRunAsync(resumed, cancellationToken))
            throw new CodeBlockConflictException("이미 진행 중인 실행이 있거나 입력이 변경되었습니다.");
        return resumed;
    }

    private static bool CanResume(CodeBlockRun run) => run.IsTerminal && run.State != CodeBlockRunState.Completed &&
        (run.Checkpoints is { Count: > 0 } || run.StopReason is "budget" or "user-cancelled");

    public CodeBlockWorkspaceInput ValidateInput(CodeBlockWorkspaceInput input, bool forGeneration = false)
    {
        var limits = options.Value;
        if (input is null || input.Blocks is null || input.Blocks.Count < 1 || input.Blocks.Count > limits.MaximumBlocks)
            throw new ArgumentException($"코드 블럭은 1~{limits.MaximumBlocks}개 입력하세요.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200) throw new ArgumentException("작업 제목을 1~200자로 입력하세요.");
        if (input.Blocks.Any(b => b is null || string.IsNullOrEmpty(b.Id) || !SafeId().IsMatch(b.Id) ||
            b.Language is not ("cpp" or "csharp") || b.Title is null || b.Title.Length > 200 || b.Code is null ||
            b.Code.Length > limits.MaximumBlockCharacters || (b.Description?.Length ?? 0) > 2000))
            throw new ArgumentException($"블럭 ID/언어를 확인하세요. 코드 최대 {limits.MaximumBlockCharacters}자, 제목 200자, 설명 2000자입니다.");
        if (input.Blocks.Sum(b => (long)b.Code.Length) > limits.MaximumTotalCharacters)
            throw new ArgumentException($"전체 코드는 최대 {limits.MaximumTotalCharacters}자입니다.");
        if (forGeneration && input.Blocks.Any(b => string.IsNullOrWhiteSpace(b.Code))) throw new ArgumentException("빈 코드 블럭을 채우거나 삭제하세요.");
        var blocks = input.Blocks.Select((b, i) => b with { Title = string.IsNullOrWhiteSpace(b.Title) ? $"블럭 {i + 1}" : b.Title.Trim() }).ToArray();
        var blockIds = blocks.Select(b => b.Id).ToHashSet();
        if (blockIds.Count != blocks.Length) throw new ArgumentException("블럭 ID는 중복될 수 없습니다.");
        var groups = input.Groups is { Count: > 0 } ? input.Groups : DefaultGroups(input);
        if (groups is not null)
        {
            if (groups.Count > limits.MaximumBlocks || groups.Any(g => g is null || string.IsNullOrEmpty(g.Id) || !SafeId().IsMatch(g.Id) ||
                string.IsNullOrWhiteSpace(g.Title) || g.Title.Length > 200 || g.BlockIds is null ||
                g.Views is { } groupViews && groupViews.Any(v => v is null)) ||
                groups.Select(g => g.Id).Distinct().Count() != groups.Count)
                throw new ArgumentException("그룹 ID/제목/블럭을 확인하세요.");
            var members = groups.SelectMany(g => g.BlockIds).ToArray();
            if (members.Length != blockIds.Count || members.Distinct().Count() != blockIds.Count || members.Any(id => !blockIds.Contains(id)))
                throw new ArgumentException("모든 블럭은 정확히 하나의 그룹에 속해야 합니다.");
            var views = groups.SelectMany(g => g.Views ?? []).ToArray();
            if (groups.Any(g => (g.Views?.Count ?? 0) > 5 || (g.Views ?? []).Select(v => v.DiagramType).Distinct().Count() != (g.Views?.Count ?? 0)) ||
                views.Select(v => v.Id).Distinct().Count() != views.Length || views.Any(v => v is null || string.IsNullOrEmpty(v.Id) || !SafeId().IsMatch(v.Id) ||
                    !DiagramTypes.Contains(v.DiagramType) || !presets.Contains(v.DiagramType, v.PresetId) || v.FocusOnChanges ||
                    (v.RefinementInstruction?.Length ?? 0) > 2000 || v.Overrides is { } o &&
                    (o.Direction is not (null or "LR" or "TB") || o.DetailLevel is not (null or "compact" or "balanced" or "detailed") ||
                     o.CallerDepth is < 0 or > 3 || o.CalleeDepth is < 0 or > 3 || o.RelationDepth is < 0 or > 3)))
                throw new ArgumentException("그룹별 형식은 중복 없이 5종까지 선택하고 올바른 프리셋/스타일을 사용하세요.");
        }
        var relations = input.Relations ?? [];
        if (relations.Count > 200 || relations.Any(r => r is null || string.IsNullOrEmpty(r.Id) || !SafeId().IsMatch(r.Id) ||
            !blockIds.Contains(r.FromBlockId) || !blockIds.Contains(r.ToBlockId) || r.Origin != "user" ||
            r.Kind is not ("calls" or "dataflow" or "uses") || r.Description is null || r.Description.Length > 500 ||
            forGeneration && IsRelationEnabled(r, groups!) && string.IsNullOrWhiteSpace(r.Description) ||
            r.EvidenceIds is { Count: > 0 } || r.CallSiteId is not null ||
            r.FromSymbolId is not null && !SafeId().IsMatch(r.FromSymbolId) || r.ToSymbolId is not null && !SafeId().IsMatch(r.ToSymbolId)) ||
            relations.Select(r => r.Id).Distinct().Count() != relations.Count)
            throw new ArgumentException("사용자 관계의 블럭/방향/종류/설명을 확인하세요. 코드 근거는 서버가 부여합니다.");
        return input with { Title = input.Title.Trim(),
            Blocks = blocks, Groups = groups?.ToArray(), Relations = relations.ToArray() };
    }

    public static IReadOnlyList<CodeBlockGroupSelection> DefaultGroups(CodeBlockWorkspaceInput input) =>
        [new(StableIds.Create("group", string.Join("|", input.Blocks.Select(b => b.Id))), "그룹 1", input.Blocks.Select(b => b.Id).ToArray(),
            EnableThinking: input.EnableThinking, EnableUserRelations: input.Relations is { Count: > 0 })];

    public static bool IsRelationEnabled(CodeBlockRelation relation, IReadOnlyList<CodeBlockGroupSelection> groups) =>
        groups.Any(g => g.EnableUserRelations != false && g.BlockIds.Contains(relation.FromBlockId) && g.BlockIds.Contains(relation.ToBlockId));

    public static CodeBlockRunSummary Summary(CodeBlockRun run) => new(run.Id, run.WorkspaceId, run.InputRevision,
        run.Revision, run.State, run.Progress, run.StageMessage, run.CreatedAt, run.UpdatedAt, run.Groups ?? [], run.Questions ?? [],
        (run.Results ?? []).Select(g => new CodeBlockGroupSummary(g.GroupId, g.Title, g.BlockIds,
            g.Views.Select(v => new CodeBlockViewSummary(v.ViewId, v.Selection, v.State,
                v.Pages.Select(p => new CodeBlockPageSummary(p.Id, p.Title, p.Diagram.Id, p.Level ?? (p.Id == "overview" ? "summary" : "detail"),
                    p.BlockIds ?? g.BlockIds, p.SymbolIds, p.ResultKind ?? (p.Diagram.Explanation?.Status == "Semantic" ? "semantic" : "static"))).ToArray(), v.Warnings, v.ErrorMessage,
                v.Reused, v.LlmStatus, v.FailureStage)).ToArray(), g.Availability)).ToArray(), run.Warnings ?? [], run.ErrorCode, run.ErrorMessage,
        run.Execution, run.StopReason, CanResume(run));

    public static DiagramArtifact Page(CodeBlockRun run, string groupId, string viewId, string pageId) =>
        run.Results?.FirstOrDefault(g => g.GroupId == groupId)?.Views.FirstOrDefault(v => v.ViewId == viewId)?
            .Pages.FirstOrDefault(p => p.Id == pageId)?.Diagram ?? throw new KeyNotFoundException();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,100}$", RegexOptions.CultureInvariant)] private static partial Regex SafeId();
}
