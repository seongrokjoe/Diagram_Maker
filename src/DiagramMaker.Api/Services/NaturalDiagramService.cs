using System.Text.RegularExpressions;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public sealed class NaturalDiagramService(
    IInternalLlmClient llm,
    MermaidCompiler compiler,
    IAppStore store,
    IOptions<LlmOptions> options,
    IWebHostEnvironment environment)
{
    private readonly LlmOptions _options = options.Value;

    public async Task<NaturalDiagramRecord> GenerateAsync(NaturalDiagramRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 10_000)
        {
            throw new ArgumentException("Prompt must contain between 1 and 10,000 characters.");
        }

        DiagramIr? ir = null;
        if (llm.IsEnabled)
        {
            ir = await llm.GenerateNaturalDiagramAsync(request.Prompt, request.DiagramType, cancellationToken);
        }

        if (ir is null && _options.AllowDevelopmentStub && environment.IsDevelopment())
        {
            ir = CreateDeterministicDiagram(request);
        }

        if (ir is null)
        {
            throw new InvalidOperationException("The internal LLM is unavailable and no external fallback is permitted.");
        }

        var now = DateTimeOffset.UtcNow;
        var diagramId = Guid.NewGuid();
        var artifact = new DiagramArtifact(diagramId, ir.Type, 1, ir, compiler.Compile(ir), now);
        var record = new NaturalDiagramRecord(diagramId, request, artifact, now);
        await store.SaveNaturalDiagramAsync(record, cancellationToken);
        return record;
    }

    private static DiagramIr CreateDeterministicDiagram(NaturalDiagramRequest request)
    {
        var type = ResolveType(request.DiagramType, request.Prompt);
        var normalized = request.Prompt.Replace("=>", "->", StringComparison.Ordinal).Replace("→", "->", StringComparison.Ordinal);
        var labels = normalized.Split("->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => Regex.Replace(value, @"\s+", " ").Trim(' ', '.', ','))
            .Where(static value => value.Length > 0)
            .Take(12)
            .ToArray();
        if (labels.Length < 2)
        {
            labels = ["사용자", ShortLabel(request.Prompt), "결과"];
        }

        var nodes = labels.Select((label, index) => new DiagramNode(
            $"n{index + 1}", ShortLabel(label), index == 0 ? "actor" : "component", null,
            "unchanged", Confidence.Inferred, [])).ToArray();
        var edges = Enumerable.Range(0, nodes.Length - 1).Select(index => new DiagramEdge(
            $"e{index + 1}", nodes[index].Id, nodes[index + 1].Id, "flow",
            type == "sequence" ? "요청" : string.Empty, "unchanged", Confidence.Inferred, [], index + 1)).ToArray();
        return new DiagramIr(type, ShortLabel(request.Prompt), nodes, edges,
            ["Development deterministic mode: configure the internal LLM for semantic generation."], []);
    }

    private static string ResolveType(string requested, string prompt)
    {
        if (!requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return requested.ToLowerInvariant();
        }

        if (prompt.Contains("시퀀스", StringComparison.OrdinalIgnoreCase) || prompt.Contains("sequence", StringComparison.OrdinalIgnoreCase)) return "sequence";
        if (prompt.Contains("클래스", StringComparison.OrdinalIgnoreCase) || prompt.Contains("class", StringComparison.OrdinalIgnoreCase)) return "class";
        if (prompt.Contains("상태", StringComparison.OrdinalIgnoreCase) || prompt.Contains("state", StringComparison.OrdinalIgnoreCase)) return "state";
        return "flowchart";
    }

    private static string ShortLabel(string value)
    {
        var label = Regex.Replace(value, @"\s+", " ").Trim();
        return label.Length <= 80 ? label : label[..77] + "...";
    }
}
