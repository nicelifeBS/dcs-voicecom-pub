using System.Globalization;
using System.Text.RegularExpressions;

namespace DcsVoiceCom.Engine;

/// <summary>
/// Replaces {name} in step fields with spoken parameter values. Integer values also take one
/// operation, for splitting a channel across dials: {channel/10}, {channel%10}, {deg*182}, {n+1}, {n-1}.
/// </summary>
public static partial class Placeholders
{
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)(?:([/%*+\-])(\d+))?\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Each placeholder as its bare slot name, for a preview: "Radio set to {freq}" → "Radio set to freq".</summary>
    public static string ToSlotNames(string text) => PlaceholderRegex().Replace(text, "$1");

    /// <summary>The slot names the text uses, each once: "{a} and {b+1} {a}" → a, b.</summary>
    public static IReadOnlyList<string> Names(string text) =>
        PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string Apply(string text, IReadOnlyDictionary<string, string> values)
    {
        if (!text.Contains('{')) return text;
        return PlaceholderRegex().Replace(text, m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var value))
                throw new StepException($"No value for placeholder {m.Value}");
            if (!m.Groups[2].Success) return value;

            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new StepException($"{m.Value} needs a whole number, got \"{value}\"");
            var operand = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (operand == 0 && m.Groups[2].Value is "/" or "%") throw new StepException($"Division by zero in {m.Value}");
            var result = m.Groups[2].Value switch
            {
                "/" => number / operand,
                "%" => number % operand,
                "*" => number * operand,
                "+" => number + operand,
                _ => number - operand,
            };
            return result.ToString(CultureInfo.InvariantCulture);
        });
    }
}

public sealed class StepException(string message) : Exception(message);
