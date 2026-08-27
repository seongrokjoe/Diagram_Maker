using System.Text;
using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed class LocalFileAppStore(string filePath) : IAppStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly InMemoryAppStore _inner = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _filePath = Path.GetFullPath(filePath);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _inner.InitializeAsync(cancellationToken);
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var repositories = await JsonSerializer.DeserializeAsync<RepositoryDefinition[]>(stream, JsonOptions, cancellationToken) ?? [];
            foreach (var repository in repositories)
            {
                await _inner.SaveRepositoryAsync(repository, cancellationToken);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Local repository registry is invalid: {_filePath}", exception);
        }
    }

    public Task<IReadOnlyList<RepositoryDefinition>> ListRepositoriesAsync(CancellationToken cancellationToken) =>
        _inner.ListRepositoriesAsync(cancellationToken);

    public Task<RepositoryDefinition?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetRepositoryAsync(id, cancellationToken);

    public async Task SaveRepositoryAsync(RepositoryDefinition repository, CancellationToken cancellationToken)
    {
        await _inner.SaveRepositoryAsync(repository, cancellationToken);
        await PersistRepositoriesAsync(cancellationToken);
    }

    public Task SaveAnalysisAsync(AnalysisJob job, CancellationToken cancellationToken) =>
        _inner.SaveAnalysisAsync(job, cancellationToken);

    public Task<AnalysisJob?> GetAnalysisAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetAnalysisAsync(id, cancellationToken);

    public Task<AnalysisJob?> TryLeaseAnalysisAsync(TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        _inner.TryLeaseAnalysisAsync(leaseDuration, cancellationToken);

    public Task SaveNaturalDiagramAsync(NaturalDiagramRecord record, CancellationToken cancellationToken) =>
        _inner.SaveNaturalDiagramAsync(record, cancellationToken);

    public Task<NaturalDiagramRecord?> GetNaturalDiagramAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetNaturalDiagramAsync(id, cancellationToken);

    public Task SaveAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
        _inner.SaveAuditAsync(auditEvent, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        _fileLock.Dispose();
    }

    private async Task PersistRepositoriesAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            var repositories = await _inner.ListRepositoriesAsync(cancellationToken);
            var json = JsonSerializer.Serialize(repositories, JsonOptions);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
