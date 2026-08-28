using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public interface IGitWorkerClient
{
    Task<GitRepositoryInspection> InspectAsync(string localPath, CancellationToken cancellationToken);
    Task<GitComparison> CompareAsync(RepositoryDefinition repository, AnalyzeRequest request, CancellationToken cancellationToken);
}

public sealed class GitWorkerClient : IGitWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GitWorkerOptions _options;
    private readonly string _scriptPath;

    public GitWorkerClient(
        IOptions<GitWorkerOptions> options,
        IWebHostEnvironment environment)
    {
        _options = options.Value;
        _scriptPath = Path.GetFullPath(_options.ScriptPath, environment.ContentRootPath);
    }

    public async Task<GitRepositoryInspection> InspectAsync(string localPath, CancellationToken cancellationToken)
    {
        var repositoryPath = LocalRepositoryPath.NormalizeAndValidate(localPath);
        var result = await RunWorkerAsync<GitRepositoryInspection>(new
        {
            command = "inspect",
            repositoryPath
        }, cancellationToken);
        return result with { NormalizedPath = repositoryPath };
    }

    public async Task<GitComparison> CompareAsync(
        RepositoryDefinition repository,
        AnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        var repositoryPath = LocalRepositoryPath.NormalizeAndValidate(repository.LocalPath);
        return await RunWorkerAsync<GitComparison>(new
        {
            command = "compare",
            repositoryPath,
            baseRevision = request.BaseRevision,
            targetRevision = request.TargetRevision,
            maxChangedFiles = _options.MaxChangedFiles,
            maxTextFileBytes = _options.MaxTextFileBytes,
            maxContextFiles = _options.MaxContextFiles,
            maxContextFileBytes = _options.MaxContextFileBytes
        }, cancellationToken);
    }

    private async Task<T> RunWorkerAsync<T>(object payloadValue, CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath))
        {
            throw new InvalidOperationException($"Git worker script was not found: {_scriptPath}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.NodeExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath)!
        };
        startInfo.ArgumentList.Add(_scriptPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Git worker.");
        }

        var payload = JsonSerializer.Serialize(payloadValue, JsonOptions);
        await process.StandardInput.WriteAsync(payload.AsMemory(), timeout.Token);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git worker failed: {Limit(stderr, 2_000)}");
        }

        return JsonSerializer.Deserialize<T>(stdout, JsonOptions)
               ?? throw new InvalidOperationException("Git worker returned an invalid payload.");
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
