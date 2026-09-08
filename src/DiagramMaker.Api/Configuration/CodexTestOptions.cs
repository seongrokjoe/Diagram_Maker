namespace DiagramMaker.Configuration;

public sealed class CodexTestOptions
{
    public const string SectionName = "CodexTest";
    public bool Enabled { get; set; }
    public string RuntimeRoot { get; set; } = "";
    public string AssetsRoot { get; set; } = "";
    // Native executable only. The launcher resolves the npm shim to codex.exe.
    public string ExecutablePath { get; set; } = "";
    public string? Model { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 300;

    public void Validate(bool development, string? urls)
    {
        if (!Enabled) return;
        if (!development || string.IsNullOrWhiteSpace(urls) || urls.Split(';').Any(value =>
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
                uri.Host != "127.0.0.1"))
            throw new InvalidOperationException("Codex sample mode requires Development and an explicit 127.0.0.1 HTTP binding.");
        if (!Path.IsPathFullyQualified(RuntimeRoot) || !Path.IsPathFullyQualified(AssetsRoot) ||
            !Path.IsPathFullyQualified(ExecutablePath) || !File.Exists(ExecutablePath) ||
            Path.GetExtension(ExecutablePath).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(ExecutablePath).Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
            RequestTimeoutSeconds is < 1 or > 1_800)
            throw new InvalidOperationException("Codex sample mode requires absolute runtime/assets/native executable paths and a bounded timeout.");
        var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RuntimeRoot));
        if (Path.GetFileName(runtime) != "codex-test" || Directory.GetParent(runtime)?.Name != "artifacts")
            throw new InvalidOperationException("Codex test data must reside in a dedicated artifacts/codex-test directory.");
    }
}
