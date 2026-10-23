namespace Lexio.Services;

internal static class AppWindowTitle
{
    internal static string ForDataDirectoryOverride(string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath))
            return "Lexio";

        var fullPath = Path.GetFullPath(overridePath);
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        return string.IsNullOrEmpty(directoryName)
            ? "Lexio — custom data"
            : $"Lexio — {directoryName}";
    }
}
