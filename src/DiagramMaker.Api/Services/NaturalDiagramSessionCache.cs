using System.Collections.Concurrent;

namespace DiagramMaker.Services;

public sealed class NaturalDiagramSessionCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Guid> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public bool TryGet(string key, out Guid id) => _records.TryGetValue(key, out id);
    public void Set(string key, Guid id) => _records[key] = id;
    public SemaphoreSlim GetGate(string key) => _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
        _records.Clear();
    }
}
