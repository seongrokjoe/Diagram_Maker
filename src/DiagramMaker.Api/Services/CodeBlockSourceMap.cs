using DiagramMaker.Domain;

namespace DiagramMaker.Services;

// All public offsets are UTF-16 code units into the exact pasted text (also the JS string convention).
public sealed class CodeBlockSourceMap(CodeBlockInput block, int prefixLength = 0)
{
    public string ContentHash { get; } = CodeBlockWorkspaceService.Hash(block.Code);
    public CodeBlockLocation Location(int wrappedStart, int wrappedEnd)
    {
        var start = Math.Clamp(wrappedStart - prefixLength, 0, block.Code.Length);
        var end = Math.Clamp(wrappedEnd - prefixLength, start, block.Code.Length);
        return new CodeBlockLocation(block.Id, Line(start), Line(end > start ? end - 1 : end), start, end);
    }
    private int Line(int offset)
    {
        var line = 1;
        for (var i = 0; i < offset; i++)
            if (block.Code[i] == '\n' || block.Code[i] == '\r' && (i + 1 >= block.Code.Length || block.Code[i + 1] != '\n')) line++;
        return line;
    }
    public CodeBlockEvidence Evidence(CodeBlockLocation location) => new(
        StableIds.Create("code-evidence", block.Id, ContentHash, location.StartOffset, location.EndOffset), block.Id, ContentHash, location);
}
