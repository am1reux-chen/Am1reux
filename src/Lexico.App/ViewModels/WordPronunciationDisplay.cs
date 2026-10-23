namespace Lexio.ViewModels;

internal static class WordPronunciationDisplay
{
    public static string Format(string? american, string? british) => string.Join("　", new[]
    {
        FormatOne("美", american),
        FormatOne("英", british),
    }.Where(value => value.Length > 0));

    private static string FormatOne(string label, string? pronunciation) =>
        string.IsNullOrWhiteSpace(pronunciation) ? "" : $"{label} {pronunciation.Trim()}";
}
