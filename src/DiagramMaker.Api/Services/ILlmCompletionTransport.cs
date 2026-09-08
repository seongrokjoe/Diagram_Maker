namespace DiagramMaker.Services;

// Keep the established vLLM request/result contract; providers only replace transport.
public interface ILlmCompletionTransport
{
    bool IsEnabled { get; }
    Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken cancellationToken);
}
