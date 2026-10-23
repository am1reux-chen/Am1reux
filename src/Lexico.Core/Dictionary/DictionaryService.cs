using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Study;

namespace Lexico.Core.Dictionary;

public sealed record ImportResult(int Added, int Updated, int Skipped, string? Error)
{
    public Exception? Failure { get; init; }
    public string? DeckName { get; init; }
    public int? SourceCount { get; init; }
    public int? ExpectedCount { get; init; }
}

/// <summary>Dictionary seeding, search facade and CSV import.</summary>
public sealed class DictionaryService
{
    public const string StarterDeckName = "核心入门 (Starter)";

    private readonly AppDatabase _db;

    public DictionaryService(AppDatabase db) => _db = db;

    /// <summary>Seed the built-in starter dictionary on first run. No-op once the deck exists.</summary>
    public async Task<int> EnsureStarterDeckAsync()
    {
        var existing = await _db.ListDecksAsync().ConfigureAwait(false);
        if (existing.Any(d => d.BuiltIn && d.Name == StarterDeckName))
            return 0;

        var entries = LoadStarterEntries();
        if (entries.Count == 0)
            return 0; // A distribution may intentionally omit seed data whose redistribution terms are unverified.
        var deckId = await _db.EnsureDeckAsync(StarterDeckName, "内置入门词库（约 100 个高频词）", "builtin", builtIn: true).ConfigureAwait(false);
        int position = 0;
        foreach (var entry in entries)
        {
            long wordId = await _db.UpsertWordAsync(entry).ConfigureAwait(false);
            await _db.AddWordToDeckAsync(deckId, wordId, position++).ConfigureAwait(false);
        }
        await _db.EnsureCardsForDeckAsync(deckId).ConfigureAwait(false);
        return entries.Count;
    }

    /// <summary>Import the licensed offline vocabulary pack once, then append later versions without resetting progress.</summary>
    public async Task<BuiltInVocabularyImportResult> EnsureBuiltInVocabularyAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n =>
            n.EndsWith("Dictionary.builtin-vocabulary.json", StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException("The built-in vocabulary resource is missing from this application.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The built-in vocabulary resource could not be opened.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetInt32();
        var decks = JsonSerializer.Deserialize<BuiltInVocabularyDeckImport[]>(root.GetProperty("decks").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The built-in vocabulary resource is invalid.");

        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
        return await _db.ImportBuiltInVocabularyAsync(
            new BuiltInVocabularyPack(version, contentHash, decks)).ConfigureAwait(false);
    }

    private static List<WordEntry> LoadStarterEntries()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("starter-dictionary.json", StringComparison.Ordinal))
            ?? string.Empty;
        if (resourceName.Length == 0)
            return [];
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var doc = JsonDocument.Parse(stream);
        return [.. doc.RootElement.EnumerateArray().Select(ReadEntry)];
    }

    private static WordEntry ReadEntry(JsonElement e)
    {
        List<WordSense> senses = e.TryGetProperty("senses", out var s) && s.ValueKind == JsonValueKind.Array
            ? [.. s.EnumerateArray().Select(x => new WordSense(
                0,
                x.TryGetProperty("pos", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null,
                x.TryGetProperty("zh", out var z) && z.ValueKind != JsonValueKind.Null ? z.GetString() : null,
                x.TryGetProperty("en", out var en) && en.ValueKind != JsonValueKind.Null ? en.GetString() : null,
                x.TryGetProperty("exEn", out var xe) && xe.ValueKind != JsonValueKind.Null ? xe.GetString() : null,
                x.TryGetProperty("exZh", out var xz) && xz.ValueKind != JsonValueKind.Null ? xz.GetString() : null))]
            : [];
        return new WordEntry(
            0,
            e.GetProperty("word").GetString()!,
            e.TryGetProperty("ipaUs", out var us) && us.ValueKind != JsonValueKind.Null ? us.GetString() : null,
            e.TryGetProperty("ipaUk", out var uk) && uk.ValueKind != JsonValueKind.Null ? uk.GetString() : null,
            e.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null,
            e.TryGetProperty("cefr", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null,
            senses);
    }

    /// <summary>Import a TypeWords dictionary JSON array into a user-owned deck.</summary>
    public async Task<ImportResult> ImportTypeWordsJsonAsync(string path, string deckName)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            return await ImportTypeWordsJsonContentAsync(json, deckName, Path.GetFileName(path)).ConfigureAwait(false);
        }
        catch (DecoderFallbackException ex)
        {
            return new ImportResult(0, 0, 0, "文件必须使用 UTF-8 编码。请另存为 UTF-8 后重试。") { Failure = ex };
        }
        catch (Exception ex)
        {
            return new ImportResult(0, 0, 0, "无法读取 TypeWords JSON；请确认文件格式正确，未写入词条。") { Failure = ex };
        }
    }

    public async Task<ImportResult> ImportTypeWordsArchiveAsync(string path)
    {
        try
        {
            var content = await TypeWordsArchiveReader.ReadRecommendedJsonAsync(path, TypeWordsContentKind.Word)
                .ConfigureAwait(false);
            var result = await ImportTypeWordsJsonContentAsync(content.Json, content.Name, Path.GetFileName(path))
                .ConfigureAwait(false);
            return result with { DeckName = content.Name, ExpectedCount = content.ExpectedCount };
        }
        catch (Exception ex)
        {
            return new ImportResult(0, 0, 0,
                "无法读取 TypeWords ZIP；请确认它包含推荐词书清单和对应 JSON，且每类仅有一份推荐词书。")
            { Failure = ex };
        }
    }

    private async Task<ImportResult> ImportTypeWordsJsonContentAsync(string json, string deckName, string source)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new ImportResult(0, 0, 0, "TypeWords 词典必须是 JSON 数组") { DeckName = deckName };

            int sourceCount = document.RootElement.GetArrayLength();
            int skipped = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<WordEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                var lemma = ReadString(item, "word")?.Trim();
                if (!IsValidLemma(lemma) || !seen.Add(lemma!))
                {
                    skipped++;
                    continue;
                }

                var senses = ReadArray(item, "trans")
                    .Where(value => value.ValueKind == JsonValueKind.Object)
                    .Select(value => new WordSense(0, ReadString(value, "pos"), ReadString(value, "cn"),
                        ReadString(value, "en"), null, null))
                    .Where(sense => !string.IsNullOrWhiteSpace(sense.ZhGloss) || !string.IsNullOrWhiteSpace(sense.EnGloss))
                    .ToArray();

                entries.Add(new WordEntry(0, lemma!, ReadString(item, "phonetic0"), ReadString(item, "phonetic1"),
                    ReadInt(item, "rank"), ReadString(item, "cefr"), senses)
                {
                    Details = new WordDetails(
                        ReadWordTexts(item, "sentences"),
                        ReadWordTexts(item, "phrases"),
                        ReadArray(item, "synos").Where(value => value.ValueKind == JsonValueKind.Object)
                            .Select(value => new WordSynonym(ReadString(value, "pos"), ReadString(value, "cn"),
                                ReadArray(value, "ws").Where(word => word.ValueKind == JsonValueKind.String)
                                    .Select(word => word.GetString()!).Where(word => !string.IsNullOrWhiteSpace(word)).ToArray()))
                            .Where(synonym => !string.IsNullOrWhiteSpace(synonym.Gloss) || synonym.Words.Count != 0).ToArray(),
                        ReadArray(item, "etymology").Where(value => value.ValueKind == JsonValueKind.Object)
                            .Select(value => new WordEtymology(ReadString(value, "t") ?? "", ReadString(value, "d") ?? ""))
                            .Where(value => value.Title.Length != 0 || value.Description.Length != 0).ToArray(),
                        ReadString(ReadObject(item, "relWords"), "root"),
                        ReadRelatedGroups(ReadObject(item, "relWords")))
                });
            }

            if (entries.Count == 0)
                return new ImportResult(0, 0, skipped, "没有可导入的有效 TypeWords 词条")
                { DeckName = deckName, SourceCount = sourceCount };

            var imported = await _db.ImportWordsAsync(entries, deckName, source, "typewords").ConfigureAwait(false);
            return new ImportResult(imported.Added, imported.Updated, skipped, null)
            { DeckName = deckName, SourceCount = sourceCount };
        }
        catch (Exception ex)
        {
            return new ImportResult(0, 0, 0, "无法读取 TypeWords JSON；请确认文件格式正确，未写入词条。")
            { Failure = ex, DeckName = deckName };
        }
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int? ReadInt(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) ? number : null;

    private static JsonElement ReadObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Object ? property : default;

    private static IEnumerable<JsonElement> ReadArray(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Array ? property.EnumerateArray() : [];

    private static WordText[] ReadWordTexts(JsonElement value, string name) => ReadArray(value, name)
        .Where(item => item.ValueKind == JsonValueKind.Object)
        .Select(item => new WordText(ReadString(item, "c") ?? "", ReadString(item, "cn") ?? ""))
        .Where(item => item.English.Length != 0 || item.Chinese.Length != 0)
        .ToArray();

    private static WordRelatedGroup[] ReadRelatedGroups(JsonElement related) => ReadArray(related, "rels")
        .Where(group => group.ValueKind == JsonValueKind.Object)
        .Select(group => new WordRelatedGroup(ReadString(group, "pos"), ReadArray(group, "words")
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new WordText(ReadString(item, "c") ?? "", ReadString(item, "cn") ?? ""))
            .Where(item => item.English.Length != 0 || item.Chinese.Length != 0).ToArray()))
        .Where(group => group.Words.Count != 0)
        .ToArray();

    private static bool IsValidLemma(string? lemma) =>
        !string.IsNullOrWhiteSpace(lemma) && lemma.Any(char.IsLetter) &&
        lemma.All(character => char.IsLetter(character) || character is '-' or '\'' or ' ');

    // ---- CSV import ----

    /// <summary>
    /// Import CSV/TXT word lists. Supported columns (header, any subset, in any order):
    /// word/单词/front, ipaUs, ipaUk, pos, zh/释义/back, en, exEn, exZh, rank — or a headerless
    /// "word,zh" / "word" list. Imported words optionally form a new deck.
    /// </summary>
    public async Task<ImportResult> ImportCsvAsync(string path, string deckName)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true)).ConfigureAwait(false);
            var rows = ParseCsvRecords(text).Select(ParseCsvLine)
                .Where(row => row.Any(field => !string.IsNullOrWhiteSpace(field)))
                .ToList();
            if (rows.Count == 0)
                return new ImportResult(0, 0, 0, "文件为空或没有可识别的行");

            var header = rows[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
            bool hasWordHeader = header.Any(c => c is "word" or "单词" or "front");
            bool hasOtherHeader = header.Any(c => c is "zh" or "释义" or "back" or "pos" or "en" or
                "exen" or "exzh" or "ipaus" or "ipauk" or "rank");
            bool hasHeader = hasWordHeader && (header.Count == 1 || hasOtherHeader);
            var map = new Dictionary<string, int>();
            var dataRows = rows;
            if (hasHeader)
            {
                for (int i = 0; i < header.Count; i++)
                {
                    map[header[i]] = i;
                }
                dataRows = rows.Skip(1).ToList();
            }

            int skipped = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<WordEntry>();
            foreach (var row in dataRows)
            {
                string? Get(string key, int fallback)
                {
                    int idx = map.TryGetValue(key, out var i) ? i : fallback;
                    if (idx < 0 || idx >= row.Count)
                        return null;
                    var value = row[idx].Trim();
                    return value.Length == 0 ? null : value;
                }

                var lemma = hasHeader
                    ? Get("word", -1) ?? Get("单词", -1) ?? Get("front", 0)
                    : row.Count > 0 ? row[0].Trim() : null;
                if (string.IsNullOrWhiteSpace(lemma) || !lemma.Any(char.IsLetter) ||
                    !lemma.All(c => char.IsLetter(c) || c is '-' or '\'' or ' '))
                {
                    skipped++;
                    continue;
                }
                if (!seen.Add(lemma))
                {
                    skipped++;
                    continue;
                }

                string? zh = hasHeader
                    ? Get("zh", -1) ?? Get("释义", -1) ?? Get("back", -1)
                    : row.Count > 1 && !string.IsNullOrWhiteSpace(row[1]) ? row[1].Trim() : null;
                string? pos = hasHeader ? Get("pos", -1) : null;
                string? en = hasHeader ? Get("en", -1) : null;
                string? exEn = hasHeader ? Get("exEn", -1) ?? Get("exen", -1) : null;
                string? exZh = hasHeader ? Get("exZh", -1) ?? Get("exzh", -1) : null;
                string? ipaUs = hasHeader ? Get("ipaUs", -1) ?? Get("ipaus", -1) : null;
                string? ipaUk = hasHeader ? Get("ipaUk", -1) ?? Get("ipauk", -1) : null;
                string? rankRaw = hasHeader ? Get("rank", -1) : null;
                int? rank = int.TryParse(rankRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rk) ? rk : null;

                entries.Add(new WordEntry(0, lemma, ipaUs, ipaUk, rank, null,
                    [new WordSense(0, pos, zh, en, exEn, exZh)]));
            }
            if (entries.Count == 0)
                return new ImportResult(0, 0, skipped, "没有可导入的有效单词");

            var imported = await _db.ImportWordsAsync(entries, deckName, Path.GetFileName(path)).ConfigureAwait(false);
            return new ImportResult(imported.Added, imported.Updated, skipped, null);
        }
        catch (DecoderFallbackException ex)
        {
            return new ImportResult(0, 0, 0, "文件必须使用 UTF-8 编码。请另存为 UTF-8 后重试。")
            {
                Failure = ex,
            };
        }
        catch (Exception ex)
        {
            return new ImportResult(0, 0, 0, "无法读取文件或写入词库。请检查文件访问权限并查看诊断日志。")
            {
                Failure = ex,
            };
        }
    }

    private static List<string> ParseCsvRecords(string text)
    {
        var records = new List<string>();
        var record = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!inQuotes && (c == '\r' || c == '\n'))
            {
                records.Add(record.ToString());
                record.Clear();
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                continue;
            }

            record.Append(c);
            if (c != '"')
                continue;
            if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                record.Append(text[++i]);
            else
                inQuotes = !inQuotes;
        }

        if (record.Length > 0)
            records.Add(record.ToString());
        return records;
    }

    /// <summary>Parse one logical CSV record (quotes, escaped quotes, and embedded newlines).</summary>
    internal static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c is ',' or '\t') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        if (inQuotes)
            throw new FormatException("CSV 行中存在未闭合的双引号");
        return fields;
    }
}
