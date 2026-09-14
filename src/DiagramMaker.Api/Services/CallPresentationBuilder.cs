using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

/// <summary>Source-owned call labels and offline, explicitly identified API contracts.</summary>
public static class CallPresentationBuilder
{
    public const string Version = "call-presentation-v1";
    public static bool HasWindowsContract(string source) => Regex.IsMatch(
        Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", match => new string(' ', match.Length)),
        @"^[ \t]*#\s*include\s*(?:<(?:windows|winbase|handleapi)\.h>|""(?:windows|winbase|handleapi)\.h"")", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static CallPresentation Build(ExecutionFact fact, CodeBlockCall? call, CodeBlockSymbol? target,
        CodeBlockSymbol owner, CodeBlockGraph graph)
    {
        var expression = fact.CallTarget ?? fact.Expression.Split('(')[0].Trim();
        var name = call?.Name ?? expression;
        if (name.Contains('(')) name = expression;
        var args = fact.Arguments ?? [];
        var outputs = new List<CallOutput>();
        string? contract = null;
        var ownCandidate = graph.Symbols.Any(s => s.Name.Split(["::", "."], StringSplitOptions.None).Last() == name.TrimStart(':'));
        var known = target is null && !ownCandidate && call?.ResolutionReason is not ("localCallable" or "overloadAmbiguous" or "virtualDispatch") &&
            graph.ApiContractBlockIds?.Contains(owner.BlockId) == true && Regex.IsMatch(expression, @"^(?:::)?(?:GetCommState|PurgeComm|CloseHandle)$");
        if (known && name.TrimStart(':') == "GetCommState" && args.Count == 2)
        {
            contract = "https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getcommstate";
            outputs.Add(new(args[1], "inout", "성공 시 통신 설정 저장", "api-contract", []));
        }
        else if (known && name.TrimStart(':') == "PurgeComm" && args.Count == 2)
            contract = "https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-purgecomm";
        else if (known && name.TrimStart(':') == "CloseHandle" && args.Count == 1)
            contract = "https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-closehandle";
        var parameters = target is null ? [] : Parameters(target.Signature);
        for (var i = 0; i < args.Count; i++)
        {
            if (outputs.Any(o => o.Expression == args[i])) continue;
            var argument = args[i];
            var parameter = i < parameters.Length ? parameters[i] : null;
            var byReference = Regex.IsMatch(argument, @"^\s*(?:out\s+|ref\s+|&)") || parameter?.Contains('&') == true || parameter?.Contains('*') == true;
            if (!byReference) continue;
            var parameterName = parameter is null ? null : Regex.Match(parameter, @"([\p{L}_][\p{L}\p{N}_]*)\s*(?:=.*)?$").Groups[1].Value;
            var writes = parameterName is { Length: > 0 } ? ExecutionSequenceProjection.Flatten(target?.Execution ?? [])
                .Where(e => e.Kind == "assign" && Regex.IsMatch(e.Expression,
                    @"^\s*(?:\*\s*)?" + Regex.Escape(parameterName) + @"(?:\s*(?:\.|->)[\w]+)*\s*=(?!=)")).ToArray() : [];
            var mode = argument.TrimStart().StartsWith("out ", StringComparison.Ordinal) ? "out" : "reference";
            outputs.Add(new(argument, mode, writes.Length > 0 ? "구현 내 대입: " + string.Join("; ", writes.Select(w => w.Expression)) :
                mode == "out" ? "정상 반환 시 값 설정" : "주소·참조 전달 · 변경 내용 미확인",
                writes.Length > 0 ? "code" : "syntax", writes.SelectMany(w => w.EvidenceIds ?? []).Distinct().ToArray()));
        }
        var returnType = target is null ? contract is null ? null : "BOOL" : ReturnType(target.Signature);
        var type = fact.AssignedTo is null ? returnType : ExecutionSequenceProjection.Flatten(owner.Execution ?? [])
            .FirstOrDefault(e => e.Kind == "declare" && e.Variable == fact.AssignedTo)?.ValueType ?? returnType;
        return new(expression, args, fact.AssignedTo, type, outputs, target is not null ? "code" : contract is not null ? "api-contract" : "unresolved", contract);
    }

    public static string Compact(CallPresentation call) => call.Target + "(…)";
    public static string ReturnLabel(CallPresentation call, string fallback) =>
        (call.AssignedTo is null ? fallback : call.AssignedTo + " ← 반환값") + (call.ReturnType is null ? "" : " (" + call.ReturnType + ")");
    private static string[] Parameters(string signature)
    {
        var start = signature.IndexOf('('); var end = signature.LastIndexOf(')');
        return start >= 0 && end > start ? signature[(start + 1)..end].Split(',').Select(p => p.Trim()).ToArray() : [];
    }
    private static string? ReturnType(string signature)
    {
        var match = Regex.Match(signature, @"^(.*?)\s+[\w:.~]+\s*\(");
        if (!match.Success) return null;
        var value = Regex.Replace(match.Groups[1].Value, @"\b(?:public|private|protected|static|virtual|inline|override|async|WINAPI|APIENTRY)\b\s*", "").Trim();
        return value.Length is > 0 and < 60 ? value : null;
    }
}
