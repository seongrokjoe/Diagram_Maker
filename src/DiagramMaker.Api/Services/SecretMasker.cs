using System.Text.RegularExpressions;

namespace DiagramMaker.Services;

public sealed partial class SecretMasker
{
    public string Mask(string value)
    {
        var masked = AssignmentSecretRegex().Replace(value, "$1=[REDACTED]");
        masked = BearerRegex().Replace(masked, "Bearer [REDACTED]");
        masked = PrivateKeyRegex().Replace(masked, "-----BEGIN PRIVATE KEY-----[REDACTED]-----END PRIVATE KEY-----");
        return masked;
    }

    [GeneratedRegex(@"(?im)\b(password|passwd|secret|api[_-]?key|token)\b\s*[:=]\s*[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentSecretRegex();

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();
}
