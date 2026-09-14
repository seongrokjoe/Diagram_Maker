namespace DiagramMaker.Domain;

public sealed record DiagramResultCounts(int AiCompleted, int AiFailed, int AiPending, int CodeCompleted, int ReusedAi = 0);
