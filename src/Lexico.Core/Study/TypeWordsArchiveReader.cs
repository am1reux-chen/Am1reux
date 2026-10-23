using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Lexico.Core.Study;

internal enum TypeWordsContentKind
{
    Word,
    Article,
}

internal sealed record TypeWordsArchiveContent(string Name, string Json, int? ExpectedCount);

internal static class TypeWordsArchiveReader
{
    private const long MaxArchiveBytes = 64 * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxContentBytes = 16 * 1024 * 1024;

    public static async Task<TypeWordsArchiveContent> ReadRecommendedJsonAsync(
        string path,
        TypeWordsContentKind kind)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaxArchiveBytes)
            throw new InvalidDataException("TypeWords ZIP 大于 64 MB，无法安全导入。请解压后选择 JSON 文件。");

        using var archive = new ZipArchive(input, ZipArchiveMode.Read);

        string kindName = kind == TypeWordsContentKind.Word ? "word" : "article";
        string manifestName = $"public/list/recommend_{kindName}.json";
        var manifestEntry = FindUniqueEntry(archive, manifestName);
        string manifest = await ReadTextAsync(manifestEntry, MaxManifestBytes).ConfigureAwait(false);
        using var document = JsonDocument.Parse(manifest);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"TypeWords {kindName} 推荐清单格式无效。");

        var recommended = document.RootElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("recommended", out var value) && value.ValueKind == JsonValueKind.True)
            .ToArray();
        if (recommended.Length != 1)
            throw new InvalidDataException($"TypeWords ZIP 必须包含且仅包含一份推荐{(kind == TypeWordsContentKind.Word ? "词书" : "文章书")}；多本内容请解压后逐个导入 JSON。");

        var item = recommended[0];
        string name = ReadString(item, "name")?.Trim() ?? "";
        string url = ReadString(item, "url")?.Trim() ?? "";
        if (name.Length == 0 || !IsSafeJsonFileName(url))
            throw new InvalidDataException($"TypeWords {kindName} 推荐清单缺少有效名称或 JSON 文件名。");

        string contentName = $"public/dicts/en/{kindName}/{url}";
        var contentEntry = FindUniqueEntry(archive, contentName);
        string json = await ReadTextAsync(contentEntry, MaxContentBytes).ConfigureAwait(false);
        int? expectedCount = item.TryGetProperty("length", out var length) &&
            length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out var count) && count >= 0
                ? count
                : null;
        return new TypeWordsArchiveContent(name, json, expectedCount);
    }

    private static ZipArchiveEntry FindUniqueEntry(ZipArchive archive, string relativeName)
    {
        string suffix = "/" + relativeName;
        var matches = archive.Entries.Where(entry =>
        {
            if (entry.Name.Length == 0)
                return false;

            string name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith("/", StringComparison.Ordinal))
                return false;

            string[] segments = name.Split('/');
            if (segments[0].Contains(':') ||
                segments.Any(segment => segment is "" or "." or ".."))
                return false;

            return name.Equals(relativeName, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"TypeWords ZIP 缺少文件：{relativeName}"),
            _ => throw new InvalidDataException($"TypeWords ZIP 包含重复文件：{relativeName}"),
        };
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, int maxBytes)
    {
        if (entry.Length > maxBytes)
            throw new InvalidDataException($"TypeWords ZIP 文件 {entry.Name} 超过安全大小限制。");

        await using var source = entry.Open();
        using var content = new MemoryStream((int)Math.Min(entry.Length, maxBytes));
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            if (content.Length + read > maxBytes)
                throw new InvalidDataException($"TypeWords ZIP 文件 {entry.Name} 超过安全大小限制。");
            await content.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        return new UTF8Encoding(false, true).GetString(content.GetBuffer(), 0, (int)content.Length)
            .TrimStart('\uFEFF');
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSafeJsonFileName(string value) =>
        value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
        value.Length > 5 &&
        value.IndexOfAny(['/', '\\', ':']) < 0 &&
        !value.Contains("..", StringComparison.Ordinal);
}
