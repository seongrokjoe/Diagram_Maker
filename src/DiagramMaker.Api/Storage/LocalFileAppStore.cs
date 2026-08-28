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
    private readonly string _diagramFilePath = Path.ChangeExtension(Path.GetFullPath(filePath), ".diagrams.json");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _inner.InitializeAsync(cancellationToken);
        try
        {
            if (File.Exists(_filePath))
            {
                await using var stream = File.OpenRead(_filePath);
                var repositories = await JsonSerializer.DeserializeAsync<RepositoryDefinition[]>(stream, JsonOptions, cancellationToken) ?? [];
                foreach (var repository in repositories)
                {
                    await _inner.SaveRepositoryAsync(repository, cancellationToken);
                }
            }

            if (File.Exists(_diagramFilePath))
            {
                await using var diagramStream = File.OpenRead(_diagramFilePath);
                var diagrams = await JsonSerializer.DeserializeAsync<NaturalDiagramRecord[]>(diagramStream, JsonOptions, cancellationToken) ?? [];
                foreach (var diagram in diagrams) await _inner.SaveNaturalDiagramAsync(diagram, cancellationToken);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"A local store file is invalid: {_filePath} or {_diagramFilePath}", exception);
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

    public async Task SaveNaturalDiagramAsync(NaturalDiagramRecord record, CancellationToken cancellationToken)
    {
        await _inner.SaveNaturalDiagramAsync(record, cancellationToken);
        await PersistNaturalDiagramsAsync(cancellationToken);
    }

    public Task<NaturalDiagramRecord?> GetNaturalDiagramAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetNaturalDiagramAsync(id, cancellationToken);

    public Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramsAsync(string ownerUserId, int limit, CancellationToken cancellationToken) =>
        _inner.ListNaturalDiagramsAsync(ownerUserId, limit, cancellationToken);

    public Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramRevisionsAsync(Guid rootDiagramId, string ownerUserId, CancellationToken cancellationToken) =>
        _inner.ListNaturalDiagramRevisionsAsync(rootDiagramId, ownerUserId, cancellationToken);

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

    private async Task PersistNaturalDiagramsAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diagramFilePath)!);
            var diagrams = await _inner.ListAllNaturalDiagramsAsync(cancellationToken);
            var temporaryPath = _diagramFilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(diagrams, JsonOptions), new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, _diagramFilePath, true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
