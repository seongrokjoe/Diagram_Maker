using System.Text.RegularExpressions;

namespace DiagramMaker.Services;

internal static class DiagramPresentation
{
    public const string Version = "diagram-presentation-v2";

    public static string Condition(string expression, string? summary = null)
    {
        expression = expression.Trim();
        if (expression.Length == 0) return summary ?? "조건 근거 미확인";
        if (string.IsNullOrWhiteSpace(summary) || summary == expression || summary.Contains('\n') ||
            summary is "조건에 따른 분기" or "반복 조건 확인" or "값에 따른 case 분기")
            summary = DescribeCondition(expression);
        return summary + "\n" + expression;
    }

    private static string DescribeCondition(string expression)
    {
        if (expression.StartsWith("switch", StringComparison.Ordinal)) return "값에 맞는 case 선택";
        // Only describe simple operands. Compound expressions retain their complete
        // predicate instead of assigning a potentially incorrect natural meaning.
        var match = Regex.Match(expression, @"^(?<left>[\w.]+)\s*(?<op>==|!=|>=|<=|>|<)\s*(?<right>[\w.]+)$");
        if (match.Success)
        {
            var relation = match.Groups["op"].Value switch
            {
                "==" => "같은지", "!=" => "다른지", ">=" => "크거나 같은지", "<=" => "작거나 같은지",
                ">" => "큰지", _ => "작은지"
            };
            var particle = match.Groups["op"].Value is "==" or "!=" ? "와" : "보다";
            return $"{match.Groups["left"].Value} 값이 {match.Groups["right"].Value}{particle} {relation} 확인";
        }
        return "조건식의 참·거짓 확인";
    }
}
