using System.Text.RegularExpressions;

namespace ImageExtractor.Services;

public static class ImageNaming
{
    private const string GuidToken = "{Guid}";
    private const string BinaryColumnNameToken = "{BinaryColumnName}";

    private static readonly HashSet<string> ReservedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Guid",
        "BinaryColumnName"
    };

    public static bool IsReserved(string tokenName) =>
        ReservedTokens.Contains(tokenName);

    public static HashSet<string> ExtractColumnReferences(string pattern)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(pattern, @"\{(\w+)\}"))
        {
            string name = m.Groups[1].Value;
            if (!IsReserved(name))
                cols.Add(name);
        }
        return cols;
    }

    public static string Resolve(
        string pattern,
        IReadOnlyDictionary<string, string> columnValues,
        string binaryColumnName)
    {
        foreach (var kv in columnValues)
            pattern = pattern.Replace("{" + kv.Key + "}", kv.Value);

        pattern = pattern.Replace(GuidToken, Guid.NewGuid().ToString("N"));
        pattern = pattern.Replace(BinaryColumnNameToken, binaryColumnName);

        return SanitizeFileName(pattern);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }
}
