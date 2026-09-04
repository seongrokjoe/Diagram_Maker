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
    private readonly string _analysisDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath))!, "analysis-jobs");
    private readonly string _planDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath))!, "analysis-plans");
    private readonly string _diagramRevisionDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath))!, "diagram-revisions");

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

            await LoadRecordsAsync(_analysisDirectory, async json =>
                await _inner.SaveAnalysisAsync(Deserialize<AnalysisJob>(json), cancellationToken), cancellationToken);
            await LoadRecordsAsync(_planDirectory, async json =>
                await _inner.SaveAnalysisPlanAsync(Deserialize<AnalysisPlan>(json), cancellationToken), cancellationToken);
            await LoadRecordsAsync(_diagramRevisionDirectory, async json =>
                await _inner.SaveDiagramRevisionAsync(Deserialize<DiagramRevisionRecord>(json), cancellationToken), cancellationToken);
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

    public async Task SaveAnalysisAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        await _inner.SaveAnalysisAsync(job, cancellationToken);
        await PersistRecordAsync(_analysisDirectory, job.Id, job, cancellationToken);
    }

    public Task<AnalysisJob?> GetAnalysisAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetAnalysisAsync(id, cancellationToken);

    public Task<IReadOnlyList<AnalysisJob>> ListAnalysesByPlanAsync(Guid planId, int limit, CancellationToken cancellationToken) =>
        _inner.ListAnalysesByPlanAsync(planId, limit, cancellationToken);

    public Task<AnalysisJob?> TryLeaseAnalysisAsync(TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        _inner.TryLeaseAnalysisAsync(leaseDuration, cancellationToken);

    public async Task SaveAnalysisPlanAsync(AnalysisPlan plan, CancellationToken cancellationToken)
    {
        await _inner.SaveAnalysisPlanAsync(plan, cancellationToken);
        await PersistRecordAsync(_planDirectory, plan.Id, plan, cancellationToken);
    }

    public Task<AnalysisPlan?> GetAnalysisPlanAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetAnalysisPlanAsync(id, cancellationToken);

    public Task<IReadOnlyList<AnalysisPlan>> ListAnalysisPlansAsync(string ownerUserId, int limit, CancellationToken cancellationToken) =>
        _inner.ListAnalysisPlansAsync(ownerUserId, limit, cancellationToken);

    public Task<AnalysisPlan?> TryLeaseAnalysisPlanAsync(TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        _inner.TryLeaseAnalysisPlanAsync(leaseDuration, cancellationToken);

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

    public async Task SaveDiagramRevisionAsync(DiagramRevisionRecord record, CancellationToken cancellationToken)
    {
        await _inner.SaveDiagramRevisionAsync(record, cancellationToken);
        await PersistRecordAsync(_diagramRevisionDirectory, record.Id, record, cancellationToken);
    }

    public Task<DiagramRevisionRecord?> GetDiagramRevisionAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetDiagramRevisionAsync(id, cancellationToken);

    public Task<IReadOnlyList<DiagramRevisionRecord>> ListDiagramRevisionsAsync(Guid rootArtifactId, string ownerUserId, CancellationToken cancellationToken) =>
        _inner.ListDiagramRevisionsAsync(rootArtifactId, ownerUserId, cancellationToken);

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

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException($"Stored {typeof(T).Name} is invalid.");

    private static async Task LoadRecordsAsync(string directory, Func<string, Task> load, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            await load(await File.ReadAllTextAsync(file, cancellationToken));
        }
    }

    private async Task PersistRecordAsync<T>(string directory, Guid id, T record, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, $"{id:N}.json");
            var temporary = destination + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, JsonOptions), new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, destination, true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
