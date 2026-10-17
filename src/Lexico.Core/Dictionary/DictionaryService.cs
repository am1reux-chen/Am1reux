using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Lexico.Core.Data;
using Lexico.Core.Domain;

namespace Lexico.Core.Dictionary;

public sealed record ImportResult(int Added, int Updated, int Skipped, string? Error);

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

    private static List<WordEntry> LoadStarterEntries()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("starter-dictionary.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("starter-dictionary.json embedded resource missing");
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

    // ---- CSV import ----

    /// <summary>
    /// Import CSV/TXT word lists. Supported columns (header, any subset, in any order):
    /// word, ipaUs, ipaUk, pos, zh, en, exEn, exZh, rank — or a headerless
    /// "word,zh" / "word" list. Imported words optionally form a new deck.
    /// </summary>
    public async Task<ImportResult> ImportCsvAsync(string path, string deckName)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8).ConfigureAwait(false);
            var rows = lines.Select(ParseCsvLine).Where(r => r.Count > 0).ToList();
            if (rows.Count == 0)
                return new ImportResult(0, 0, 0, "文件为空或没有可识别的行");

            var header = rows[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
            bool hasHeader = header.Contains("word") || header.Contains("单词") || header.Contains("front");
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

            long deckId = 0;
            if (!string.IsNullOrWhiteSpace(deckName))
            {
                deckId = await _db.EnsureDeckAsync(deckName.Trim(), $"Imported from {Path.GetFileName(path)}", "csv", builtIn: false).ConfigureAwait(false);
            }

            int added = 0, skipped = 0, position = 0;
            foreach (var row in dataRows)
            {
                string? Get(string key, int fallback)
                {
                    int idx = map.TryGetValue(key, out var i) ? i : fallback;
                    return idx >= 0 && idx < row.Count ? row[idx].Trim() : null;
                }

                var lemma = hasHeader ? Get("word", 0) ?? Get("单词", 0) : row.Count > 0 ? row[0].Trim() : null;
                if (string.IsNullOrWhiteSpace(lemma) || !lemma.All(c => char.IsLetter(c) || c is '-' or '\'' or ' '))
                {
                    skipped++;
                    continue;
                }

                string? zh = hasHeader ? Get("zh", 1) ?? Get("释义", 1) : row.Count > 1 ? row[1].Trim() : null;
                string? pos = hasHeader ? Get("pos", -1) : null;
                string? en = hasHeader ? Get("en", -1) : null;
                string? exEn = hasHeader ? Get("exEn", -1) ?? Get("exen", -1) : null;
                string? exZh = hasHeader ? Get("exZh", -1) ?? Get("exzh", -1) : null;
                string? ipaUs = hasHeader ? Get("ipaUs", -1) ?? Get("ipaus", -1) : null;
                string? ipaUk = hasHeader ? Get("ipaUk", -1) ?? Get("ipauk", -1) : null;
                string? rankRaw = hasHeader ? Get("rank", -1) : null;
                int? rank = int.TryParse(rankRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rk) ? rk : null;

                var entry = new WordEntry(0, lemma, ipaUs, ipaUk, rank, null,
                    [new WordSense(0, pos, zh, en, exEn, exZh)]);
                long wordId = await _db.UpsertWordAsync(entry).ConfigureAwait(false);
                added++;
                if (deckId != 0)
                    await _db.AddWordToDeckAsync(deckId, wordId, position++).ConfigureAwait(false);
            }
            if (deckId != 0)
                await _db.EnsureCardsForDeckAsync(deckId).ConfigureAwait(false);
            return new ImportResult(added, 0, skipped, null);
        }
        catch (Exception ex)
        {
            return new ImportResult(0, 0, 0, ex.Message);
        }
    }

    /// <summary>Minimal RFC-4180-style CSV line parser (quotes, escaped quotes).</summary>
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
        return fields;
    }
}
