using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DiagramMaker.Configuration;

namespace DiagramMaker.Services;

public sealed class LlmClientException(
    string code,
    string message,
    Exception? innerException = null,
    string? failureKind = null,
    string? initialFailureKind = null,
    bool repairAttempted = false,
    int? requestedMaxOutputTokens = null,
    int? promptTokens = null,
    int? completionTokens = null,
    int? totalTokens = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string? FailureKind { get; } = failureKind;
    public string? InitialFailureKind { get; } = initialFailureKind;
    public bool RepairAttempted { get; } = repairAttempted;
    public int? RequestedMaxOutputTokens { get; } = requestedMaxOutputTokens;
    public int? PromptTokens { get; } = promptTokens;
    public int? CompletionTokens { get; } = completionTokens;
    public int? TotalTokens { get; } = totalTokens;
}

public sealed record VllmCompletionRequest(
    string SystemPrompt,
    string UserPrompt,
    int MaxOutputTokens,
    bool EnableThinking,
    JsonElement? StructuredSchema = null);

public sealed record VllmCompletionResult(
    string Content,
    string FinishReason,
    long ElapsedMilliseconds,
    bool StructuredOutputApplied,
    bool StructuredOutputFallbackUsed,
    int RetryCount,
    int RequestedMaxOutputTokens,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens);

public sealed class VllmClient : IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LlmOptions _options;
    private readonly ILogger<VllmClient>? _logger;
    private readonly HttpClient? _client;
    private readonly Uri? _endpoint;

    public VllmClient(LlmOptions options, ILogger<VllmClient>? logger = null, HttpMessageHandler? handler = null)
    {
        _options = options;
        _logger = logger;
        if (!options.Enabled) return;

        _endpoint = ValidateOptions(options);
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsEnabled => _client is not null;

    public async Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken cancellationToken)
    {
        if (_client is null || _endpoint is null)
            throw new LlmClientException("LLM_DISABLED", "The internal LLM is disabled.");
        if (request.MaxOutputTokens is <= 0 || request.MaxOutputTokens > _options.OutputHardLimit)
            throw new LlmClientException("LLM_OUTPUT_LIMIT", "The requested LLM output limit is invalid.");
        if (string.IsNullOrWhiteSpace(request.UserPrompt))
            throw new LlmClientException("LLM_REQUEST_INVALID", "The LLM request is empty.");

        var correlationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var retryCount = 0;
        try
        {
            var includeSchema = request.StructuredSchema.HasValue;
            var initial = await SendWithRetryAsync(request, includeSchema, linkedSource.Token);
            retryCount += initial.RetryCount;
            using var initialResponse = initial.Response;

            if (includeSchema && IsStructuredUnsupported(initialResponse.StatusCode))
            {
                var fallback = await SendWithRetryAsync(request, includeSchema: false, linkedSource.Token);
                retryCount += fallback.RetryCount;
                using var fallbackResponse = fallback.Response;
                EnsureSuccess(fallbackResponse);
                var fallbackContent = await ParseResponseAsync(fallbackResponse, linkedSource.Token);
                stopwatch.Stop();
                LogCompletion(correlationId, stopwatch.ElapsedMilliseconds, request.EnableThinking, retryCount, fallback: true);
                return new VllmCompletionResult(fallbackContent.Content, fallbackContent.FinishReason,
                    stopwatch.ElapsedMilliseconds, false, true, retryCount, request.MaxOutputTokens,
                    fallbackContent.PromptTokens, fallbackContent.CompletionTokens, fallbackContent.TotalTokens);
            }

            EnsureSuccess(initialResponse);
            var content = await ParseResponseAsync(initialResponse, linkedSource.Token);
            stopwatch.Stop();
            LogCompletion(correlationId, stopwatch.ElapsedMilliseconds, request.EnableThinking, retryCount, fallback: false);
            return new VllmCompletionResult(content.Content, content.FinishReason,
                stopwatch.ElapsedMilliseconds, includeSchema, false, retryCount, request.MaxOutputTokens,
                content.PromptTokens, content.CompletionTokens, content.TotalTokens);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new LlmClientException("LLM_REQUEST_TIMEOUT", "The internal LLM request exceeded its total timeout.", exception);
        }
    }

    private async Task<(HttpResponseMessage Response, int RetryCount)> SendWithRetryAsync(
        VllmCompletionRequest request, bool includeSchema, CancellationToken cancellationToken)
    {
        var retries = 0;
        while (true)
        {
            try
            {
                using var message = CreateRequest(request, includeSchema);
                var response = await _client!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (IsTransient(response.StatusCode) && retries < _options.MaxTransientRetries)
                {
                    var delay = GetRetryDelay(response);
                    response.Dispose();
                    retries++;
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                return (response, retries);
            }
            catch (HttpRequestException) when (retries < _options.MaxTransientRetries)
            {
                retries++;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new LlmClientException("LLM_TRANSPORT", "The internal LLM connection failed.", exception);
            }
        }
    }

    private HttpRequestMessage CreateRequest(VllmCompletionRequest request, bool includeSchema)
    {
        object[] messages = string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? [new { role = "user", content = request.UserPrompt }]
            : [new { role = "system", content = request.SystemPrompt }, new { role = "user", content = request.UserPrompt }];
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = false,
            ["chat_template_kwargs"] = new { enable_thinking = request.EnableThinking }
        };
        if (includeSchema && request.StructuredSchema.HasValue)
            payload["structured_outputs"] = new { json = request.StructuredSchema.Value };

        return new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
    }

    private async Task<ParsedCompletion> ParseResponseAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        byte[] body;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            body = await ReadBoundedBodyAsync(stream, cancellationToken);

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new LlmClientException("LLM_RESPONSE_FORMAT", "The internal LLM response did not contain a completion choice.");

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw new LlmClientException("LLM_RESPONSE_FORMAT", "The internal LLM response did not contain message content.");
            var finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
                ? finish.GetString() ?? string.Empty : string.Empty;
            var usage = json.RootElement.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
                ? usageElement : default;
            return new ParsedCompletion(
                content.GetString() ?? string.Empty,
                finishReason,
                GetOptionalInt32(usage, "prompt_tokens"),
                GetOptionalInt32(usage, "completion_tokens"),
                GetOptionalInt32(usage, "total_tokens"));
        }
        catch (JsonException exception)
        {
            throw new LlmClientException("LLM_RESPONSE_FORMAT", "The internal LLM returned malformed JSON.", exception);
        }
    }

    private static int? GetOptionalInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private async Task<byte[]> ReadBoundedBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var inactivitySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inactivitySource.CancelAfter(TimeSpan.FromSeconds(_options.NoResponseTimeoutSeconds));
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, inactivitySource.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LlmClientException("LLM_NO_RESPONSE_TIMEOUT", "The internal LLM stopped sending response data.", exception);
            }
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw new LlmClientException("LLM_RESPONSE_TOO_LARGE", "The internal LLM response exceeded the safety limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new LlmClientException("LLM_REDIRECT_BLOCKED", "The internal LLM returned a redirect, which is not permitted.");
        if (!response.IsSuccessStatusCode)
            throw new LlmClientException($"LLM_HTTP_{(int)response.StatusCode}",
                $"The internal LLM returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool IsStructuredUnsupported(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var requested = response.Headers.RetryAfter?.Delta;
        if (!requested.HasValue && response.Headers.RetryAfter?.Date is { } date) requested = date - DateTimeOffset.UtcNow;
        if (!requested.HasValue || requested.Value <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(500);
        return requested.Value > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : requested.Value;
    }

    private static Uri ValidateOptions(LlmOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Llm:Endpoint must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("Llm:Endpoint must not contain credentials or a fragment.");
        if (!Uri.TryCreate(options.AllowedOrigin, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query))
            throw new InvalidOperationException("Llm:AllowedOrigin must contain only scheme, host, and port.");
        if (!endpoint.Scheme.Equals(origin.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !endpoint.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) || endpoint.Port != origin.Port)
            throw new InvalidOperationException("Llm:Endpoint is outside Llm:AllowedOrigin.");
        if (string.IsNullOrWhiteSpace(options.Model)) throw new InvalidOperationException("Llm:Model is required.");
        if (options.ConnectTimeoutSeconds is <= 0 or > 1_800 || options.NoResponseTimeoutSeconds is <= 0 or > 1_800 ||
            options.RequestTimeoutSeconds is <= 0 or > 1_800)
            throw new InvalidOperationException("LLM timeout values must be between 1 and 1,800 seconds.");
        if (options.OutputHardLimit is <= 0 or > 60_000 || options.DiagramOutputTokens is <= 0 ||
            options.DiagramOutputTokens > options.OutputHardLimit || options.ReviewOutputTokens is <= 0 ||
            options.ReviewOutputTokens > options.OutputHardLimit ||
            options.ThinkingOutputTokens is <= 0 || options.ThinkingOutputTokens > options.OutputHardLimit ||
            options.MaxInputCharacters is <= 0 or > 200_000 ||
            options.MaxTransientRetries is < 0 or > 3)
            throw new InvalidOperationException("LLM limits are outside the permitted range.");
        return endpoint;
    }

    private void LogCompletion(string correlationId, long elapsed, bool thinking, int retries, bool fallback) =>
        _logger?.LogInformation(
            "Internal LLM request {CorrelationId} completed in {ElapsedMilliseconds} ms; thinking={Thinking}, retries={Retries}, structuredFallback={StructuredFallback}",
            correlationId, elapsed, thinking, retries, fallback);

    public void Dispose() => _client?.Dispose();

    private sealed record ParsedCompletion(
        string Content,
        string FinishReason,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens);
}
