namespace DiagramMaker.Configuration;

public sealed class CodeBlockOptions
{
    public const string SectionName = "CodeBlocks";
    public int MaximumBlocks { get; set; } = 20;
    public int MaximumBlockCharacters { get; set; } = 100_000;
    public int MaximumTotalCharacters { get; set; } = 1_000_000;
    public long MaximumRequestBytes { get; set; } = 8 * 1024 * 1024;
    public int MaximumQuestions { get; set; } = 5;
    public int ParserTimeoutSeconds { get; set; } = 120;
    public int LeaseSeconds { get; set; } = 90;
    public int HeartbeatSeconds { get; set; } = 15;
}
