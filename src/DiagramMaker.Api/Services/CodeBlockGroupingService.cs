using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public sealed record CodeBlockGrouping(IReadOnlyList<CodeBlockGroupSelection> Groups,
    IReadOnlyList<CodeBlockRelation> Relations, IReadOnlyList<CodeBlockQuestion> Questions, IReadOnlyList<string> Warnings);

public sealed class CodeBlockGroupingService(IOptions<CodeBlockOptions> options)
{
    public CodeBlockGrouping Prepare(CodeBlockRun run, CodeBlockGraph graph)
    {
        var blocks = run.Snapshot.Blocks.ToDictionary(b => b.Id);
        var symbols = graph.Symbols.ToDictionary(s => s.Id);
        var relations = graph.Relations.ToList();
        var warnings = new List<string>();
        var groups = (run.Groups ?? run.Snapshot.Groups ?? CodeBlockWorkspaceService.DefaultGroups(run.Snapshot)).ToList();
        var questions = (run.Questions ?? CreateQuestions()).ToArray();
        if (run.QuestionsResolved)
        {
            foreach (var question in questions)
            {
                var answer = run.Answers?.FirstOrDefault(a => a.QuestionId == question.Id);
                var targetId = question.Options.FirstOrDefault(o => o.Id == answer?.OptionId)?.TargetSymbolId;
                if (targetId is null || !symbols.TryGetValue(targetId, out var target))
                {
                    if (answer?.OptionId != "none") warnings.Add($"{blocks[question.FromBlockId].Title}: 미확인 호출을 실행 흐름에 연결하지 않았습니다.");
                    continue;
                }
                relations.Add(new CodeBlockRelation(StableIds.Create("answer", question.Id), question.FromBlockId, target.BlockId,
                    "calls", "user", "사용자가 호출 대상을 확인함 (실행 위치·순서는 미확인)", question.FromSymbolId, target.Id));
                if (answer?.MergeGroups == true) Merge(question.FromBlockId, target.BlockId);
            }
        }
        foreach (var relation in run.Snapshot.Relations ?? [])
        {
            if (!CodeBlockWorkspaceService.IsRelationEnabled(relation, groups)) continue;
            if (relation.FromSymbolId is not null && (!symbols.TryGetValue(relation.FromSymbolId, out var from) || from.BlockId != relation.FromBlockId) ||
                relation.ToSymbolId is not null && (!symbols.TryGetValue(relation.ToSymbolId, out var to) || to.BlockId != relation.ToBlockId))
            {
                warnings.Add("코드 변경으로 사용자 관계의 심벌을 찾지 못했습니다. 관계를 다시 지정하세요.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(relation.Description))
                throw new ArgumentException("적용할 사용자 관계의 설명을 입력하세요.");
            relations.Add(relation);
        }
        foreach (var relation in relations.Where(r => r.FromBlockId != r.ToBlockId))
            if (GroupOf(relation.FromBlockId)?.Id != GroupOf(relation.ToBlockId)?.Id)
                warnings.Add($"{blocks[relation.FromBlockId].Title} → {blocks[relation.ToBlockId].Title}: 그룹 간 관계입니다. 한 그림에 보려면 그룹을 합치세요.");
        return new CodeBlockGrouping(groups, relations.DistinctBy(r => r.Id).ToArray(), questions, warnings.Distinct().ToArray());

        CodeBlockGroupSelection? GroupOf(string id) => groups.FirstOrDefault(g => g.BlockIds.Contains(id));
        void Merge(string from, string to)
        {
            var left = GroupOf(from); var right = GroupOf(to);
            if (left is null || right is null || left.Id == right.Id) return;
            groups[groups.IndexOf(left)] = left with { BlockIds = left.BlockIds.Concat(right.BlockIds).Distinct().ToArray(),
                Views = (left.Views ?? []).Concat(right.Views ?? []).DistinctBy(v => v.DiagramType).ToArray() };
            groups.Remove(right);
        }
        IEnumerable<CodeBlockQuestion> CreateQuestions()
        {
            var unresolved = graph.Symbols.SelectMany(s => s.Calls).Where(c => c.TargetSymbolId is null &&
                c.CandidateSymbolIds?.Count > 0).ToArray();
            var questions = new List<CodeBlockQuestion>();
            foreach (var call in unresolved)
            {
                var source = symbols[call.SymbolId];
                var candidates = (call.CandidateSymbolIds ?? []).Where(symbols.ContainsKey).ToArray();
                if (candidates.Length == 0) continue;
                var reason = call.ResolutionReason switch
                {
                    "virtualDispatch" => "가상 호출의 실제 런타임 타입이 필요합니다.",
                    "receiverTypeUnknown" => "수신 객체의 타입을 입력 코드에서 확인할 수 없습니다.",
                    "overloadAmbiguous" => "인자 타입으로 오버로드를 하나로 좁힐 수 없습니다.",
                    "multipleTargets" => "동일한 이름과 인자 수의 구현이 여러 개 있습니다.",
                    _ => "선언 범위 또는 호출 대상의 타입 근거가 부족합니다."
                };
                questions.Add(new CodeBlockQuestion(StableIds.Create("question", call.Id),
                    $"{blocks[source.BlockId].Title} {call.Location.StartLine}행의 {call.Name} 호출은 어느 구현으로 연결됩니까? {reason}",
                    source.BlockId, source.Id, call.Id, graph.Evidence.Where(e => e.Location == call.Location).Select(e => e.Id).ToArray(),
                    candidates.Select(id => new CodeBlockQuestionOption(id, $"{blocks[symbols[id].BlockId].Title} / {symbols[id].Signature}", id)).ToArray()));
            }
            var maximum = Math.Clamp(options.Value.MaximumQuestions, 0, 5);
            if (questions.Count > maximum)
                warnings.Add($"관계 질문은 실행당 최대 {maximum}개입니다. 남은 미확인 호출은 결과에 연결하지 않습니다.");
            return questions.Take(maximum);
        }
    }
}
