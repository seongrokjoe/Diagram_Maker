using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Security;

public sealed record InternalIdentity(string UserId, IReadOnlySet<string> Roles);

public sealed class InternalIdentityMiddleware(
    RequestDelegate next,
    IWebHostEnvironment environment,
    IOptions<SecurityOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (environment.IsDevelopment() && !context.Request.Headers.ContainsKey("X-Remote-User"))
        {
            context.Items[typeof(InternalIdentity)] = new InternalIdentity("developer", new HashSet<string>(["Admin", "Reviewer"], StringComparer.OrdinalIgnoreCase));
            await next(context);
            return;
        }

        if (!options.Value.TrustReverseProxyHeaders ||
            !context.Request.Headers.TryGetValue("X-Remote-User", out var users) ||
            string.IsNullOrWhiteSpace(users.FirstOrDefault()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Internal identity is required." });
            return;
        }

        var roles = context.Request.Headers["X-Remote-Roles"].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        context.Items[typeof(InternalIdentity)] = new InternalIdentity(users.First()!, roles);
        await next(context);
    }
}

public static class IdentityExtensions
{
    public static InternalIdentity GetInternalIdentity(this HttpContext context) =>
        context.Items.TryGetValue(typeof(InternalIdentity), out var identity) && identity is InternalIdentity value
            ? value
            : throw new InvalidOperationException("Internal identity middleware is not configured.");

    public static bool CanAccess(this InternalIdentity identity, RepositoryDefinition repository) =>
        identity.Roles.Contains("Admin") || repository.AllowedRoles.Count == 0 || repository.AllowedRoles.Any(identity.Roles.Contains);
}
