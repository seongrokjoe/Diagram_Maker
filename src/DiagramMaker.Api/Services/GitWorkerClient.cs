using System.ComponentModel;
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

public sealed class GitWorkerException(
    string errorCode,
    string message,
    string? backend = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
    public string? Backend { get; } = backend;
    public string UserMessage => ErrorCode switch
    {
        "GIT_EXECUTABLE_NOT_FOUND" or "GIT_EXECUTABLE_UNAVAILABLE" =>
            "Git 실행 파일을 찾거나 시작할 수 없습니다. git --version과 GitWorker:GitExecutable 설정을 확인하세요.",
        "GIT_REVISION_NOT_FOUND" =>
            "Base 또는 Target revision을 로컬 저장소에서 찾을 수 없습니다. 커밋 ID와 fetch 상태를 확인하세요.",
        "GIT_REPOSITORY_INVALID" =>
            "등록된 경로가 유효한 Git 저장소가 아니거나 접근할 수 없습니다.",
        "GIT_PACK_UNREADABLE" =>
            "Git pack 파일을 읽을 수 없습니다. 저장소 무결성을 검사하거나 분석 전용 clone을 다시 만드세요.",
        "GIT_OBJECT_UNREADABLE" =>
            "분석에 필요한 Git 객체를 읽을 수 없습니다. git fsck --full 결과를 확인하세요.",
        "GIT_CHANGED_FILE_LIMIT" =>
            "변경 파일 수가 분석 허용 한도를 초과했습니다. revision 범위를 줄이세요.",
        "GIT_WORKER_TIMEOUT" =>
            "Git 비교 시간이 제한을 초과했습니다. 저장소 상태와 GitWorker timeout 설정을 확인하세요.",
        "GIT_OUTPUT_LIMIT" =>
            "Git 비교 결과가 안전한 처리 한도를 초과했습니다. revision 범위를 줄이세요.",
        "GIT_WORKER_UNAVAILABLE" =>
            "Git 분석 Worker를 시작할 수 없습니다. 설치 파일과 Node 런타임을 확인하세요.",
        _ => "Git 변경 분석에 실패했습니다. 분석 ID로 내부 서버 로그를 확인하세요."
    };
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
            repositoryPath,
            backend = _options.Backend,
            gitExecutable = _options.GitExecutable
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
            backend = _options.Backend,
            gitExecutable = _options.GitExecutable,
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
            throw new GitWorkerException("GIT_WORKER_UNAVAILABLE", $"Git worker script was not found: {_scriptPath}");
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
        try
        {
            if (!process.Start())
            {
                throw new GitWorkerException("GIT_WORKER_UNAVAILABLE", "Failed to start the Git worker.");
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
                throw CreateWorkerException(stderr);
            }

            return JsonSerializer.Deserialize<T>(stdout, JsonOptions)
                   ?? throw new GitWorkerException("GIT_WORKER_FAILED", "Git worker returned an invalid payload.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new GitWorkerException(
                "GIT_WORKER_TIMEOUT",
                $"Git worker exceeded the {_options.TimeoutSeconds} second timeout.");
        }
        catch
        {
            TryKillProcessTree(process);
            throw;
        }
    }

    private static GitWorkerException CreateWorkerException(string stderr)
    {
        try
        {
            var failure = JsonSerializer.Deserialize<WorkerFailure>(stderr, JsonOptions);
            if (failure is not null && !string.IsNullOrWhiteSpace(failure.ErrorCode))
            {
                var detail = string.IsNullOrWhiteSpace(failure.Message) ? "No diagnostic was returned." : failure.Message;
                return new GitWorkerException(
                    failure.ErrorCode,
                    $"Git worker failed ({failure.Backend ?? "unknown"}): {Limit(detail, 8_000)}",
                    failure.Backend);
            }
        }
        catch (JsonException)
        {
            // Older workers returned plain text. Preserve it for the internal log.
        }

        return new GitWorkerException("GIT_WORKER_FAILED", $"Git worker failed: {Limit(stderr, 8_000)}");
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The worker exited between the state check and Kill.
        }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record WorkerFailure(string ErrorCode, string? Backend, string? Message);
}
