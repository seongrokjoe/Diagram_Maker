using System.Security.Cryptography;
using System.Text;

namespace DiagramMaker.Services;

public static class StableIds
{
    public static string Create(params object?[] parts)
    {
        var value = string.Join('|', parts.Select(static part => part?.ToString()?.Trim() ?? string.Empty));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }
}
