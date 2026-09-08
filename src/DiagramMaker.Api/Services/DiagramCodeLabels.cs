using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static class DiagramCodeLabels
{
    public static string Action(CodeContext context)
    {
        var label = DescribeAction(context);
        return label.Length <= 220 ? label : label[..217] + "…";
    }

    private static string DescribeAction(CodeContext context)
    {
        var args = string.Join(", ", context.Arguments.Select(ShortValue));
        var target = context.Target;
        var assigned = context.AssignedTo;
        var prefix = assigned is null ? "" : $"{assigned} ← ";
        if (context.Purpose == "assertion" && context.Arguments.Count >= 2)
        {
            var expected = ShortValue(context.Arguments[0]);
            var actual = ShortValue(context.Arguments[1]);
            return target switch
            {
                "Equal" => $"{actual} = {expected}인지 검증",
                "Contains" => $"{actual}에 {expected} 포함 검증",
                _ => $"{target}({args}) 검증"
            };
        }
        if (target == "GetField") return $"{prefix}GetField({args}) 읽기";
        if (target == "ReadAllText") return $"{prefix}파일 읽기 ({args})";
        if (target is not null) return $"{prefix}{target}({args}) 호출";
        if (context.CreatedType is not null) return $"{prefix}{context.CreatedType} 객체 생성";
        return assigned is not null ? $"{assigned} 값 설정" : "데이터 처리";
    }

    private static string ShortValue(string value)
    {
        var text = Regex.Replace(value, @"\s+", " ").Trim();
        return text.Length <= 90 ? text : text[..87] + "…";
    }

    // Use the smallest distinguishing suffix; IDs and evidence never change.
    public static IReadOnlyDictionary<string, string> ShortNames(IEnumerable<string> names)
    {
        var values = names.Distinct(StringComparer.Ordinal).ToArray();
        var segments = values.ToDictionary(name => name, name => name.Replace("::", ".", StringComparison.Ordinal).Split('.'));
        return values.ToDictionary(name => name, name =>
        {
            var parts = segments[name];
            for (var count = 1; count < parts.Length; count++)
            {
                var suffix = string.Join('.', parts.TakeLast(count));
                if (values.All(other => other == name || string.Join('.', segments[other].TakeLast(count)) != suffix)) return suffix;
            }
            return name;
        });
    }
}
