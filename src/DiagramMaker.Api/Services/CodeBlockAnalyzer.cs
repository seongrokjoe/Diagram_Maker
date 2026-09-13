using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public sealed class CodeBlockAnalyzer(SourceGraphAnalyzer csharp, IOptions<GitWorkerOptions> worker,
    IOptions<CodeBlockOptions> options, IWebHostEnvironment environment,
    DiagramMaker.Security.ApprovedNetworkPolicy? networkPolicy = null)
{
    public const string AnalyzerVersion = "code-block-v4";
    public async Task<CodeBlockGraph> AnalyzeAsync(Guid workspaceId, IReadOnlyList<CodeBlockInput> blocks, CancellationToken cancellationToken)
    {
        var graphs = new List<CodeBlockGraph>();
        var cs = blocks.Where(b => b.Language == "csharp").ToArray();
        if (cs.Length > 0) graphs.Add(csharp.AnalyzeCSharpCodeBlocks(workspaceId, cs));
        var cpp = blocks.Where(b => b.Language == "cpp").ToArray();
        if (cpp.Length > 0)
        {
            var script = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(worker.Value.ScriptPath, environment.ContentRootPath))!, "code-block-parser.mjs");
            networkPolicy?.ValidateLocalPath(script);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ParserTimeoutSeconds));
            using var process = new Process { StartInfo = new ProcessStartInfo(worker.Value.NodeExecutable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 } };
            process.StartInfo.ArgumentList.Add(script);
            DiagramMaker.Security.WorkerEnvironment.Apply(process.StartInfo);
            process.Start();
            using var stop = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { blocks = cpp, limits = new { options.Value.MaximumBlocks,
                options.Value.MaximumBlockCharacters, options.Value.MaximumTotalCharacters } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("C/C++ 코드 분석 작업자를 완료하지 못했습니다.");
            graphs.Add(JsonSerializer.Deserialize<CodeBlockGraph>(await output, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("C/C++ 분석 결과가 비어 있습니다."));
        }
        var symbols = graphs.SelectMany(g => g.Symbols).ToArray();
        var evidence = graphs.SelectMany(g => g.Evidence).DistinctBy(e => e.Id).ToArray();
        var relations = new List<CodeBlockRelation>();
        foreach (var symbol in symbols)
        {
            foreach (var call in symbol.Calls.Where(c => c.TargetSymbolId is not null))
            {
                var target = symbols.First(s => s.Id == call.TargetSymbolId);
                relations.Add(new CodeBlockRelation(StableIds.Create("relation", call.Id), symbol.BlockId, target.BlockId, "calls", "code", call.Name,
                    symbol.Id, target.Id, evidence.Where(e => e.Location == call.Location).Select(e => e.Id).ToArray(), call.Id));
            }
            foreach (var baseType in symbol.BaseTypes)
            {
                var separator = blocks.First(b => b.Id == symbol.BlockId).Language == "cpp" ? "::" : ".";
                var index = symbol.Name.LastIndexOf(separator, StringComparison.Ordinal);
                var qualified = index >= 0 ? symbol.Name[..index] + separator + baseType : baseType;
                var targets = symbols.Where(s => (s.Name == baseType || s.Name == qualified) && s.Kind is "class" or "type" or "interface" or "struct" &&
                    blocks.First(b => b.Id == s.BlockId).Language == blocks.First(b => b.Id == symbol.BlockId).Language).ToArray();
                if (targets.Length == 1) relations.Add(new CodeBlockRelation(StableIds.Create("base", symbol.Id, targets[0].Id),
                    symbol.BlockId, targets[0].BlockId, "inherits", "code", "상속", symbol.Id, targets[0].Id, symbol.EvidenceIds));
            }
        }
        return new CodeBlockGraph(symbols, relations, evidence, graphs.SelectMany(g => g.Transitions).ToArray(),
            graphs.SelectMany(g => g.Warnings).ToArray(), AnalyzerVersion);
    }
}
