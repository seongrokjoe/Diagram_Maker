namespace DiagramMaker.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string Provider { get; set; } = "LocalFile";
    public string LocalFilePath { get; set; } = "../../data/repositories.json";
    public string? ConnectionString { get; set; }
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public bool TrustReverseProxyHeaders { get; set; }
    public int AnalysisRetentionDays { get; set; } = 30;
    public int AuditRetentionDays { get; set; } = 180;
}

public sealed class GitWorkerOptions
{
    public const string SectionName = "GitWorker";
    public string NodeExecutable { get; set; } = "node";
    public string ScriptPath { get; set; } = "tools/git-worker/index.mjs";
    public int MaxChangedFiles { get; set; } = 200;
    public int MaxTextFileBytes { get; set; } = 1_000_000;
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class LlmOptions
{
    public const string SectionName = "Llm";
    public bool Enabled { get; set; }
    public bool AllowDevelopmentStub { get; set; } = true;
    public string? Endpoint { get; set; }
    public string? AllowedOrigin { get; set; }
    public string Model { get; set; } = "internal-code-model";
    public int ConnectTimeoutSeconds { get; set; } = 120;
    public int NoResponseTimeoutSeconds { get; set; } = 120;
    public int RequestTimeoutSeconds { get; set; } = 300;
    public int DiagramOutputTokens { get; set; } = 16_000;
    public int ReviewOutputTokens { get; set; } = 8_000;
    public int? ThinkingOutputTokens { get; set; }
    public int OutputHardLimit { get; set; } = 60_000;
    public int MaxInputCharacters { get; set; } = 60_000;
    public int MaxTransientRetries { get; set; } = 1;
}
