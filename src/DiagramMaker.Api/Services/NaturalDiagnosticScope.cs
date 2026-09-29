namespace DiagramMaker.Services;

internal sealed class NaturalDiagnosticScope : IDisposable
{
    private static readonly AsyncLocal<NaturalDiagnosticScope?> Slot = new();
    private readonly NaturalDiagnosticScope? previous;
    internal static NaturalDiagnosticScope? Current => Slot.Value;
    internal string ViewId { get; }
    internal string ScenarioId { get; }
    internal string PageId { get; }

    internal NaturalDiagnosticScope(string viewId, string scenarioId, string pageId)
    {
        previous = Slot.Value;
        ViewId = viewId; ScenarioId = scenarioId; PageId = pageId;
        Slot.Value = this;
    }

    public void Dispose() => Slot.Value = previous;
}
