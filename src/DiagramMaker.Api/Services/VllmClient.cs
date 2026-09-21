using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Security;
using DiagramMaker.Domain;
using System.Text;
using System.Collections.Concurrent;

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
    int? totalTokens = null, string? rejectedContent = null, LlmValidationDetails? validationDetails = null,
    int? httpStatus = null, string? serverErrorCategory = null)
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
    public LlmValidationDetails? ValidationDetails { get; } = validationDetails;
    public int? HttpStatus { get; } = httpStatus;
    public string? ServerErrorCategory { get; } = serverErrorCategory;
    // Transient repair context, never part of a log message or persisted diagnostic.
    [System.Text.Json.Serialization.JsonIgnore] public string? RejectedContent { get; } = rejectedContent;
}

public sealed record VllmCompletionRequest(
    string SystemPrompt,
    string UserPrompt,
    int MaxOutputTokens,
    bool EnableThinking,
    JsonElement? StructuredSchema = null,
    double? Temperature = null,
    int? Seed = null, int? InputTokenLimit = null, int? InputCharacterLimit = null, string? Purpose = null,
    bool AllowSchemaRelaxation = false);

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
    int? TotalTokens,
    string? OutputMode = null, int? HttpStatus = null);

public sealed class VllmClient : ILlmCompletionTransport, IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LlmOptions _options;
    private readonly ILogger<VllmClient>? _logger;
    private readonly HttpClient? _client;
    private readonly Uri? _endpoint;
    private readonly SemaphoreSlim completionGate = new(1, 1);
    private int tokenizationSupport;
    private DateTimeOffset tokenizationRetryAfter;
    private readonly ConcurrentDictionary<string, (int Tokens, bool Exact)> tokenCounts = new();
    private readonly ConcurrentDictionary<string, (string Mode, bool Relaxed)> compatibility = new();

    public VllmClient(LlmOptions options, ILogger<VllmClient>? logger = null, HttpMessageHandler? handler = null,
        ApprovedNetworkPolicy? networkPolicy = null)
    {
        _options = options;
        _logger = logger;
        if (!options.Enabled) return;

        _endpoint = ValidateOptions(options);
        networkPolicy ??= ApprovedNetworkPolicy.Load(null);
        networkPolicy.ValidateLlm(_endpoint);
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = networkPolicy.ConnectLlmAsync
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsEnabled => _client is not null;

    public async Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken cancellationToken)
    {
        await completionGate.WaitAsync(cancellationToken);
        try { return await CompleteSerialAsync(request, cancellationToken); }
        finally { completionGate.Release(); }
    }

    private async Task<VllmCompletionResult> CompleteSerialAsync(VllmCompletionRequest request, CancellationToken cancellationToken)
    {
        var execution = SemanticExecution.Current;
        if (execution?.RequestFailure is { } stopped) throw stopped;
        var watch = Stopwatch.StartNew();
        var diagnostic = new LlmDiagnostic(Guid.NewGuid().ToString("N"), execution?.Stage ?? "completion", execution?.UnitId ?? "",
            "Preparing", DateTimeOffset.UtcNow, OutputLimit: request.MaxOutputTokens,
            Purpose: request.Purpose ?? (execution?.Stage.Contains("Review", StringComparison.Ordinal) == true ? "review" : "generation"),
            InputCharacters: request.UserPrompt.Length, InputCharacterLimit: request.InputCharacterLimit,
            InputTokenLimit: LlmRequestBudget.InputLimit(_options, request.MaxOutputTokens, request.InputTokenLimit),
            ContextTokenLimit: _options.MaxContextTokens, Kind: "Request");
        try
        {
            if (_client is null) throw new LlmClientException("LLM_DISABLED", "The internal LLM is disabled.");
            var result = await CompleteCoreAsync(request, cancellationToken, async (prepared, mode, relaxed, ct) =>
            {
                diagnostic = diagnostic with { State = "Preparing", OutputMode = mode, SchemaRelaxed = relaxed,
                    InputCharacters = LlmRequestBudget.Measure(prepared.SystemPrompt, prepared.UserPrompt, prepared.StructuredSchema).Characters,
                    InputTokens = null };
                if (diagnostic.InputCharacters > (request.InputCharacterLimit ?? _options.MaxInputCharacters))
                    throw new LlmClientException("LLM_INPUT_CHARACTERS", "The prepared messages and schema exceed the character budget.");
                var (tokens, exact) = await CountInputTokensAsync(prepared, ct, async () =>
                {
                    diagnostic = diagnostic with { TokenizationRequests = diagnostic.TokenizationRequests + 1 };
                    if (execution is not null) await execution.RecordAsync(diagnostic);
                });
                diagnostic = diagnostic with { InputTokens = tokens, EstimatedInputTokens = !exact };
                if (tokens > diagnostic.InputTokenLimit || (long)tokens + request.MaxOutputTokens + 1024 > _options.MaxContextTokens)
                    throw new LlmClientException("LLM_INPUT_LIMIT", "Input and reserved output exceed the configured context budget.");
                diagnostic = diagnostic with { State = "Running" };
                if (execution is not null) await execution.RecordAsync(diagnostic);
            }, async () =>
            {
                diagnostic = diagnostic with { Sent = true, TransportAttempts = diagnostic.TransportAttempts + 1 };
                if (execution is not null) await execution.RecordAsync(diagnostic);
            });
            diagnostic = diagnostic with { State = result.FinishReason == "length" ? "Failed" : "Completed", PromptTokens = result.PromptTokens, CompletionTokens = result.CompletionTokens,
                ErrorCode = result.FinishReason == "length" ? "LLM_RESPONSE_TRUNCATED" : null,
                FinishReason = result.FinishReason is "stop" or "length" or "content_filter" ? result.FinishReason : "other",
                OutputMode = result.OutputMode, Retries = result.RetryCount, HttpStatus = result.HttpStatus };
            return result;
        }
        catch (Exception e)
        {
            var known = e as LlmClientException;
            if (known is not null && LlmFailure.StopsRequests(known)) execution?.StopRequests(known);
            diagnostic = diagnostic with { State = "Failed", ErrorCode = known?.Code ?? (e is OperationCanceledException ? "CANCELLED" : "LLM_TRANSPORT"),
                HttpStatus = known?.HttpStatus, ServerErrorCategory = known?.ServerErrorCategory,
                NextAction = known?.HttpStatus is not null ? LlmRequestCompatibility.Action(known.ServerErrorCategory) : null };
            throw;
        }
        finally
        {
            if (execution is not null) await execution.RecordAsync(diagnostic with { ElapsedMilliseconds = watch.ElapsedMilliseconds });
        }
    }

    internal async Task<(int Tokens, bool Exact)> CountInputTokensAsync(VllmCompletionRequest request, CancellationToken cancellationToken,
        Func<Task>? onTokenize = null)
    {
        var key = SemanticExecution.Hash(JsonSerializer.Serialize(new { _options.Model, request.SystemPrompt, request.UserPrompt,
            request.StructuredSchema, request.EnableThinking }));
        if (tokenCounts.TryGetValue(key, out var cached)) return cached;
        var estimate = LlmRequestBudget.Measure(request.SystemPrompt, request.UserPrompt, request.StructuredSchema).Tokens;
        var limit = LlmRequestBudget.InputLimit(_options, request.MaxOutputTokens, request.InputTokenLimit);
        if ((SemanticExecution.Current is not null || estimate > limit) && _options.UseServerTokenization && tokenizationSupport >= 0 && DateTimeOffset.UtcNow >= tokenizationRetryAfter && _client is not null && _endpoint is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var path = _endpoint.AbsolutePath;
                var suffix = "/v1/chat/completions";
                var prefix = path.EndsWith(suffix, StringComparison.Ordinal) ? path[..^suffix.Length] : "";
                var uri = new UriBuilder(_endpoint) { Path = prefix + "/tokenize", Query = "" }.Uri;
                using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new {
                    model = _options.Model, messages = new[] { new { role = "system", content = request.SystemPrompt }, new { role = "user", content = request.UserPrompt } },
                    add_generation_prompt = true, chat_template_kwargs = new { enable_thinking = request.EnableThinking }
                }) };
                if (onTokenize is not null) await onTokenize();
                else if (SemanticExecution.Current is { } execution) await execution.RecordPreflightTokenizationAsync();
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await ReadBoundedBodyAsync(await response.Content.ReadAsStreamAsync(timeout.Token), timeout.Token));
                    if (json.RootElement.TryGetProperty("count", out var count) && count.TryGetInt32(out var tokens) && tokens >= 0)
                    {
                        tokenizationSupport = 1;
                        // Schemas may be included by the serving template; reserve their bytes.
                        var result = ((int)Math.Min(int.MaxValue, (long)tokens + Encoding.UTF8.GetByteCount(request.StructuredSchema?.GetRawText() ?? "")), !request.StructuredSchema.HasValue);
                        if (tokenCounts.Count >= 256) tokenCounts.Clear();
                        tokenCounts[key] = result;
                        return result;
                    }
                }
                // A transient tokenizer error must not disable it for all later requests.
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
                    tokenizationSupport = -1;
                else tokenizationRetryAfter = DateTimeOffset.UtcNow.AddSeconds(60);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException or LlmClientException)
            { cancellationToken.ThrowIfCancellationRequested(); tokenizationRetryAfter = DateTimeOffset.UtcNow.AddSeconds(60); }
        }
        // The tokenizer sees message content, not JSON's escaped wire encoding.
        // Reserve one token per UTF-8 byte plus template overhead; never call the
        // fallback exact or count escaped quotes/Unicode as model input twice.
        return (estimate, false);
    }

    private async Task<VllmCompletionResult> CompleteCoreAsync(VllmCompletionRequest request, CancellationToken cancellationToken,
        Func<VllmCompletionRequest, string, bool, CancellationToken, Task> onPrepare,
        Func<Task>? onSend = null)
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
            var key = SemanticExecution.Hash((request.StructuredSchema?.GetRawText() ?? "none") + request.AllowSchemaRelaxation + request.EnableThinking);
            var mode = request.StructuredSchema.HasValue ? "structured_outputs" : "none";
            var relaxed = false;
            if (compatibility.TryGetValue(key, out var cached)) (mode, relaxed) = cached;
            // Each transition is one-way: at most two field alternatives and one
            // schema reduction. Unknown HTTP failures never trigger a blind retry.
            while (true)
            {
                var prepared = relaxed && request.StructuredSchema is { } schema
                    ? request with { StructuredSchema = LlmRequestCompatibility.Relax(schema) } : request;
                if (mode == "json_prompt") prepared = prepared with {
                    SystemPrompt = prepared.SystemPrompt + "\nReturn one JSON object matching this schema:\n" + prepared.StructuredSchema?.GetRawText(),
                    StructuredSchema = null };
                await onPrepare(prepared, mode, relaxed, linkedSource.Token);
                var sent = await SendWithRetryAsync(prepared, mode, linkedSource.Token, onSend);
                retryCount += sent.RetryCount;
                using var response = sent.Response;
                var error = await ReadFailureAsync(response, linkedSource.Token);
                if (error is null)
                {
                    var content = await ParseResponseAsync(response, linkedSource.Token);
                    // Do not memorize a rejected negotiation or a malformed reply.
                    if (compatibility.Count < 128) compatibility[key] = (mode, relaxed);
                    LogCompletion(correlationId, stopwatch.ElapsedMilliseconds, request.EnableThinking, retryCount, mode == "json_prompt");
                    return new(content.Content, content.FinishReason, stopwatch.ElapsedMilliseconds,
                        mode is "structured_outputs" or "response_format", mode == "json_prompt", retryCount,
                        request.MaxOutputTokens, content.PromptTokens, content.CompletionTokens, content.TotalTokens, mode, (int)response.StatusCode);
                }
                if (request.StructuredSchema.HasValue && mode is "structured_outputs" or "response_format")
                {
                    if (error.ServerErrorCategory == "schema-constraint" && request.AllowSchemaRelaxation && !relaxed &&
                        LlmRequestCompatibility.Relax(request.StructuredSchema.Value).GetRawText() != request.StructuredSchema.Value.GetRawText())
                    { relaxed = true; continue; }
                    if (error.ServerErrorCategory == "output-field")
                    { mode = mode == "structured_outputs" ? "response_format" : "json_prompt"; continue; }
                }
                throw error;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new LlmClientException("LLM_REQUEST_TIMEOUT", "The internal LLM request exceeded its total timeout.", exception);
        }
    }

    private async Task<(HttpResponseMessage Response, int RetryCount)> SendWithRetryAsync(
        VllmCompletionRequest request, string mode, CancellationToken cancellationToken, Func<Task>? onSend = null)
    {
        var retries = 0;
        while (true)
        {
            try
            {
                using var message = CreateRequest(request, mode);
                cancellationToken.ThrowIfCancellationRequested();
                if (onSend is not null) await onSend();
                cancellationToken.ThrowIfCancellationRequested();
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

    private HttpRequestMessage CreateRequest(VllmCompletionRequest request, string mode)
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
        if (request.StructuredSchema.HasValue && mode is "structured_outputs" or "response_format")
        {
            if (mode == "response_format") payload["response_format"] = new { type = "json_schema", json_schema = new { name = "diagram_result", schema = request.StructuredSchema.Value } };
            else payload["structured_outputs"] = new { json = request.StructuredSchema.Value };
        }
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
        if (request.Seed.HasValue) payload["seed"] = request.Seed.Value;

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

    private async Task<LlmClientException?> ReadFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return null;
        var status = (int)response.StatusCode;
        if ((int)response.StatusCode is >= 300 and < 400)
            return new("LLM_REDIRECT_BLOCKED", "The internal LLM returned a redirect, which is not permitted.", httpStatus: status);
        var bytes = await ReadBoundedBodyAsync(await response.Content.ReadAsStreamAsync(ct), ct);
        var category = LlmRequestCompatibility.Classify(status, Encoding.UTF8.GetString(bytes));
        return new(category == "context" ? "LLM_CONTEXT_LIMIT" : $"LLM_HTTP_{status}",
            $"The internal LLM rejected the request ({category}, HTTP {status}).", httpStatus: status, serverErrorCategory: category);
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

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
            options.MaxInputCharacters is <= 0 or > 8_000_000 ||
            options.MaxInputTokens is <= 0 or > 200_000 || options.MaxContextTokens is <= 0 or > 260_000 ||
            options.SemanticJobBudgetSeconds is <= 0 or > 900 ||
            options.UnderstandingOutputTokens is <= 0 or > 60_000 ||
            options.MaxTransientRetries is < 0 or > 3 || options.NaturalDiagramTemperature is < 0 or > 2)
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
