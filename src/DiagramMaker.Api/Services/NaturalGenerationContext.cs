using DiagramMaker.Domain;

namespace DiagramMaker.Services;

// One scenario/type owns its recovery budget, including size splits and cross-view repairs.
internal sealed class NaturalGenerationContext : IDisposable
{
    private static readonly AsyncLocal<NaturalGenerationContext?> Slot = new();
    public static NaturalGenerationContext? Current => Slot.Value;
    private readonly NaturalGenerationContext? previous;
    public string Key { get; }
    public IReadOnlyList<NaturalIssue> Issues { get; }
    public DiagramIr? PreviousDiagram { get; }
    public int Revision { get; }
    public DiagramRecoveryBudget Recovery { get; }

    public NaturalGenerationContext(string key, IReadOnlyList<NaturalIssue>? issues = null, DiagramIr? previousDiagram = null, int revision = 0)
    {
        previous = Slot.Value;
        Key = key; Issues = issues ?? []; PreviousDiagram = previousDiagram; Revision = revision;
        Recovery = new DiagramRecoveryBudget("natural-scenario:" + key);
        Slot.Value = this;
    }

    public void Dispose() => Slot.Value = previous;
}
