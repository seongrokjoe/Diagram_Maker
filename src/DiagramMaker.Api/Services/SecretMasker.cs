using System.Text.RegularExpressions;

namespace DiagramMaker.Services;

public sealed partial class SecretMasker
{
    public MaskedText MaskWithOffsets(string value)
    {
        var text = value;
        var starts = Enumerable.Range(0, value.Length).ToArray();
        var ends = Enumerable.Range(1, value.Length).ToArray();
        Replace(AssignmentSecretRegex(), m => m.Groups[1].Value + "=[REDACTED]");
        Replace(BearerRegex(), _ => "Bearer [REDACTED]");
        Replace(PrivateKeyRegex(), _ => "-----BEGIN PRIVATE KEY-----[REDACTED]-----END PRIVATE KEY-----");
        return new(text, starts, ends);

        void Replace(Regex regex, Func<Match, string> replacement)
        {
            foreach (var match in regex.Matches(text).Cast<Match>().Reverse())
            {
                var next = replacement(match);
                var start = starts[match.Index];
                var end = ends[match.Index + match.Length - 1];
                starts = starts[..match.Index].Concat(Enumerable.Repeat(start, next.Length)).Concat(starts[(match.Index + match.Length)..]).ToArray();
                ends = ends[..match.Index].Concat(Enumerable.Repeat(end, next.Length)).Concat(ends[(match.Index + match.Length)..]).ToArray();
                text = text[..match.Index] + next + text[(match.Index + match.Length)..];
            }
        }
    }

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

public sealed record MaskedText(string Text, IReadOnlyList<int> Starts, IReadOnlyList<int> Ends);
