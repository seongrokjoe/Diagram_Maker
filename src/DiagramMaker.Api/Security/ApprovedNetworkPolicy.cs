using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;

namespace DiagramMaker.Security;

// Optional administrator allowlist, enabled only by an explicit policy path.
// Without it, configured endpoints/local paths still use the baseline safeguards.
// An explicitly supplied policy always fails closed; app settings cannot widen it.
public sealed class ApprovedNetworkPolicy
{
    internal bool IsRestricted { get; private init; } = true;
    public string[] LlmOrigins { get; init; } = [];
    public string[] LlmAddressRanges { get; init; } = [];
    public DatabaseTarget[] Databases { get; init; } = [];
    public string[] LocalRoots { get; init; } = [];

    public sealed record DatabaseTarget(string Address, int Port);

    public static ApprovedNetworkPolicy Load() => Load(Environment.GetEnvironmentVariable("DIAGRAMMAKER_NETWORK_POLICY_PATH"));

    public static ApprovedNetworkPolicy Load(string? path)
    {
        // Do not auto-load a leftover policy from a previous installation.
        if (string.IsNullOrWhiteSpace(path)) return new ApprovedNetworkPolicy { IsRestricted = false };
        LocalPathSafety.Validate(path);
        var policy = JsonSerializer.Deserialize<ApprovedNetworkPolicy>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidOperationException("Approved network policy is required.");
        if (policy.LlmOrigins is null || policy.LlmAddressRanges is null || policy.Databases is null || policy.LocalRoots is null)
            throw new InvalidOperationException("Network policy lists cannot be null.");
        foreach (var root in policy.LocalRoots) LocalPathSafety.Validate(root);
        foreach (var range in policy.LlmAddressRanges) _ = IPNetwork.Parse(range);
        foreach (var origin in policy.LlmOrigins)
        {
            var uri = new Uri(origin, UriKind.Absolute);
            if (uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                throw new InvalidOperationException("Policy LLM origins must contain only scheme, host and port.");
        }
        foreach (var target in policy.Databases)
            if (!IPAddress.TryParse(target.Address, out _) || target.Port is < 1 or > 65535)
                throw new InvalidOperationException("Database policy requires a literal IP and port.");
        return policy;
    }

    public void ValidateLlm(Uri endpoint)
    {
        if (IsRestricted && !LlmOrigins.Any(origin => Uri.Compare(new Uri(origin), endpoint, UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0))
            throw new InvalidOperationException("LLM destination is not approved by the network policy.");
    }

    public void ValidateAddresses(IEnumerable<IPAddress> addresses)
    {
        var resolved = addresses.ToArray();
        if (resolved.Length == 0 || (IsRestricted && resolved.Any(address => !LlmAddressRanges.Any(range =>
                IPNetwork.Parse(range).Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)))))
            throw new InvalidOperationException("LLM address is outside the approved IP ranges.");
    }

    public async ValueTask<Stream> ConnectLlmAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        ValidateLlm(context.InitialRequestMessage.RequestUri ?? throw new InvalidOperationException("Missing LLM destination."));
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? new[] { literal } : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        ValidateAddresses(addresses);
        // Connect to the checked IP, retaining the original URI for TLS/SNI.
        // No second hostname resolution and no external address fallback.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    public string ValidateDatabase(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString);
        // Npgsql resolves hostnames when opening each pooled connection. Literal
        // IPs make the allowlist binding stable, including after pool eviction.
        if (!IPAddress.TryParse(options.Host, out var address) || (IsRestricted && !Databases.Any(target =>
                IPAddress.Parse(target.Address).Equals(address) && target.Port == options.Port)))
            throw new InvalidOperationException("Database requires an approved literal IP and port; host lists, sockets and DNS names are not accepted.");
        if (!IPAddress.IsLoopback(address) && options.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException("Remote database connections require SSL Mode=VerifyFull and a certificate valid for the approved IP.");
        return options.ConnectionString;
    }

    public void ValidateLocalPath(string path)
    {
        LocalPathSafety.Validate(path);
        if (IsRestricted && !LocalRoots.Any(root => LocalPathSafety.IsWithin(root, path)))
            throw new InvalidOperationException("Path is outside the approved local roots.");
    }
}
