using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:52641");
var app = builder.Build();
var transientCount = 0;

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "diagram-maker-fake-vllm" }));
app.MapPost("/v1/chat/completions", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    var mode = context.Request.Query["mode"].ToString();
    if (mode == "transient" && Interlocked.Increment(ref transientCount) == 1)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (mode == "structured-unsupported" && request.RootElement.TryGetProperty("structured_outputs", out _))
        return Results.UnprocessableEntity();
    if (mode == "delay") await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);

    var structured = request.RootElement.TryGetProperty("structured_outputs", out _);
    var thinking = request.RootElement.TryGetProperty("chat_template_kwargs", out var template) &&
                   template.TryGetProperty("enable_thinking", out var enabled) && enabled.GetBoolean();
    var content = mode == "malformed"
        ? "not-json"
        : structured
            ? JsonSerializer.Serialize(new
            {
                type = "flowchart",
                title = "Synthetic Diagram",
                nodes = new[]
                {
                    new { id = "n1", label = "SyntheticClient", kind = "component", group = (string?)null, status = "unchanged", confidence = "Inferred", evidenceIds = Array.Empty<string>() },
                    new { id = "n2", label = "SyntheticService", kind = "component", group = (string?)null, status = "unchanged", confidence = "Inferred", evidenceIds = Array.Empty<string>() },
                    new { id = "n3", label = "SyntheticStore", kind = "component", group = (string?)null, status = "unchanged", confidence = "Inferred", evidenceIds = Array.Empty<string>() }
                },
                edges = new[]
                {
                    new { id = "e1", sourceId = "n1", targetId = "n2", type = "flow", label = "request", status = "unchanged", confidence = "Inferred", evidenceIds = Array.Empty<string>(), sequenceIndex = 1 },
                    new { id = "e2", sourceId = "n2", targetId = "n3", type = "flow", label = "query", status = "unchanged", confidence = "Inferred", evidenceIds = Array.Empty<string>(), sequenceIndex = 2 }
                },
                notes = Array.Empty<string>(),
                provenance = Array.Empty<string>()
            })
            : "OK";

    return Results.Ok(new
    {
        id = "synthetic-completion",
        choices = new[]
        {
            new
            {
                message = new { role = "assistant", content, reasoning_content = thinking ? "synthetic-only" : null },
                finish_reason = mode == "length" ? "length" : "stop"
            }
        }
    });
});

app.Run();
