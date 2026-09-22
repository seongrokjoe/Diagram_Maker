namespace DiagramMaker.Services;

// Counts repairs AFTER the original request. Transport retries and job deadlines
// are independent; resuming a checkpoint does not grant another repair budget.
internal static class DiagramRecoveryPolicy
{
    public const int MaximumRepairs = 10;
    public const int MaximumAttempts = MaximumRepairs + 1;
    public const string Version = "diagram-recovery-v1";
}
