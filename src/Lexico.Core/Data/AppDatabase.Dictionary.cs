using Lexico.Core.Domain;

namespace Lexico.Core.Data;

public sealed record WordImportSummary(int Added, int Updated);

public sealed partial class AppDatabase
{
    public Task<WordImportSummary> ImportWordsAsync(
        IReadOnlyList<WordEntry> entries,
        string? deckName,
        string source,
        string sourceType = "csv") => RunAsync(() =>
    {
        using var tx = _conn.BeginTransaction();

        Dictionary<string, long>? unicodeLemmaIds = null;
        if (entries.Any(entry => entry.Lemma.Any(character => character > 0x7f)))
        {
            unicodeLemmaIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var existingWords = Cmd("SELECT id, lemma FROM words ORDER BY id");
            existingWords.Transaction = tx;
            using var reader = existingWords.ExecuteReader();
            while (reader.Read())
                unicodeLemmaIds.TryAdd(reader.GetString(1), reader.GetInt64(0));
        }

        long deckId = 0;
        if (!string.IsNullOrWhiteSpace(deckName))
        {
            Execute("""
                INSERT INTO decks(name, description, source, built_in, created_at)
                VALUES(@name, @description, @source, 0, @created)
                ON CONFLICT(name) DO UPDATE SET
                    description=excluded.description,
                    source=excluded.source
                WHERE decks.built_in=0
                """, ("@name", deckName.Trim()), ("@description", $"Imported from {source}"),
                ("@source", sourceType), ("@created", Fmt(DateTime.UtcNow)));
            using var findDeck = Cmd("SELECT id FROM decks WHERE name=@name", ("@name", deckName.Trim()));
            findDeck.Transaction = tx;
            deckId = Convert.ToInt64(findDeck.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        int added = 0, updated = 0, position = 0;
        foreach (var entry in entries)
        {
            long? existingWordId;
            if (unicodeLemmaIds is not null)
            {
                existingWordId = unicodeLemmaIds.TryGetValue(entry.Lemma, out var existingId) ? existingId : null;
            }
            else
            {
                using var find = Cmd("SELECT id FROM words WHERE lemma=@lemma COLLATE NOCASE ORDER BY id LIMIT 1", ("@lemma", entry.Lemma));
                find.Transaction = tx;
                var existing = find.ExecuteScalar();
                existingWordId = existing is null or DBNull
                    ? null
                    : Convert.ToInt64(existing, System.Globalization.CultureInfo.InvariantCulture);
            }

            long wordId;
            var existingSenses = new List<(long Id, int Order)>();
            if (existingWordId is null)
            {
                Execute("""
                    INSERT INTO words(lemma, ipa_us, ipa_uk, frequency_rank, cefr, details_json)
                    VALUES(@lemma, @us, @uk, @rank, @cefr, @details)
                    """, ("@lemma", entry.Lemma), ("@us", entry.IpaUs), ("@uk", entry.IpaUk),
                    ("@rank", entry.FrequencyRank), ("@cefr", entry.Cefr), ("@details", SerializeWordDetails(entry.Details)));
                using var lastId = _conn.CreateCommand();
                lastId.Transaction = tx;
                lastId.CommandText = "SELECT last_insert_rowid()";
                wordId = Convert.ToInt64(lastId.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                unicodeLemmaIds?.Add(entry.Lemma, wordId);
                added++;
            }
            else
            {
                wordId = existingWordId.Value;
                string? detailsJson = SerializeWordDetails(entry.Details);
                if (detailsJson is not null)
                {
                    using var findDetails = Cmd("SELECT details_json FROM words WHERE id=@id", ("@id", wordId));
                    findDetails.Transaction = tx;
                    var storedDetails = findDetails.ExecuteScalar() as string;
                    detailsJson = SerializeWordDetails(MergeWordDetails(DeserializeWordDetails(storedDetails), entry.Details));
                }
                Execute("""
                    UPDATE words SET ipa_us=COALESCE(@us, ipa_us), ipa_uk=COALESCE(@uk, ipa_uk),
                        frequency_rank=COALESCE(@rank, frequency_rank), cefr=COALESCE(@cefr, cefr),
                        details_json=COALESCE(@details, details_json)
                    WHERE id=@id
                    """, ("@us", entry.IpaUs), ("@uk", entry.IpaUk), ("@rank", entry.FrequencyRank),
                    ("@cefr", entry.Cefr), ("@details", detailsJson), ("@id", wordId));
                using var findSenses = Cmd("SELECT id, sense_order FROM word_senses WHERE word_id=@id ORDER BY sense_order, id", ("@id", wordId));
                findSenses.Transaction = tx;
                using var senseReader = findSenses.ExecuteReader();
                while (senseReader.Read())
                    existingSenses.Add((senseReader.GetInt64(0), senseReader.GetInt32(1)));
                updated++;
            }

            int senseOrder = 0;
            foreach (var sense in entry.Senses)
            {
                if (senseOrder < existingSenses.Count)
                {
                    Execute("""
                        UPDATE word_senses SET pos=COALESCE(@pos, pos), zh_gloss=COALESCE(@zh, zh_gloss),
                            en_gloss=COALESCE(@en, en_gloss), example_en=COALESCE(@exampleEn, example_en),
                            example_zh=COALESCE(@exampleZh, example_zh)
                        WHERE id=@id
                        """, ("@pos", sense.Pos), ("@zh", sense.ZhGloss), ("@en", sense.EnGloss),
                        ("@exampleEn", sense.ExampleEn), ("@exampleZh", sense.ExampleZh),
                        ("@id", existingSenses[senseOrder].Id));
                }
                else
                {
                    int order = existingSenses.Count == 0
                        ? senseOrder
                        : existingSenses[^1].Order + senseOrder - existingSenses.Count + 1;
                    Execute("""
                        INSERT INTO word_senses(word_id, pos, zh_gloss, en_gloss, example_en, example_zh, sense_order)
                        VALUES(@word, @pos, @zh, @en, @exampleEn, @exampleZh, @order)
                        """, ("@word", wordId), ("@pos", sense.Pos), ("@zh", sense.ZhGloss),
                        ("@en", sense.EnGloss), ("@exampleEn", sense.ExampleEn),
                        ("@exampleZh", sense.ExampleZh), ("@order", order));
                }
                senseOrder++;
            }

            if (deckId != 0)
            {
                Execute("""
                    INSERT INTO deck_words(deck_id, word_id, position) VALUES(@deck, @word, @position)
                    ON CONFLICT(deck_id, word_id) DO UPDATE SET position=@position
                    """, ("@deck", deckId), ("@word", wordId), ("@position", position++));
            }
        }

        if (deckId != 0)
        {
            var now = Fmt(DateTime.UtcNow);
            Execute("""
                INSERT INTO cards(word_id, deck_id, state, step, due, created_at)
                SELECT dw.word_id, dw.deck_id, 0, 0, @now, @now
                FROM deck_words dw
                WHERE dw.deck_id=@deck
                  AND NOT EXISTS (SELECT 1 FROM cards c WHERE c.word_id=dw.word_id AND c.deck_id=dw.deck_id)
                """, ("@deck", deckId), ("@now", now));
        }

        tx.Commit();
        return new WordImportSummary(added, updated);

        int Execute(string sql, params (string, object?)[] args)
        {
            using var cmd = Cmd(sql, args);
            cmd.Transaction = tx;
            return cmd.ExecuteNonQuery();
        }
    });

    private static WordDetails MergeWordDetails(WordDetails existing, WordDetails imported) => new(
        MergeWordTexts(existing.Sentences, imported.Sentences),
        MergeWordTexts(existing.Phrases, imported.Phrases),
        MergeSynonyms(existing.Synonyms, imported.Synonyms),
        MergeEtymologies(existing.Etymologies, imported.Etymologies),
        FirstValue(existing.RelatedRoot, imported.RelatedRoot),
        MergeRelatedGroups(existing.RelatedGroups, imported.RelatedGroups));

    private static WordText[] MergeWordTexts(IReadOnlyList<WordText> existing, IReadOnlyList<WordText> imported)
    {
        var merged = existing.ToList();
        foreach (var item in imported)
        {
            string key = TextKey(item);
            int index = merged.FindIndex(candidate => string.Equals(TextKey(candidate), key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                merged.Add(item);
            else if (string.IsNullOrWhiteSpace(merged[index].Chinese) && !string.IsNullOrWhiteSpace(item.Chinese))
                merged[index] = merged[index] with { Chinese = item.Chinese };
        }
        return merged.ToArray();
    }

    private static WordSynonym[] MergeSynonyms(IReadOnlyList<WordSynonym> existing, IReadOnlyList<WordSynonym> imported)
    {
        var merged = existing.ToList();
        foreach (var item in imported)
        {
            string key = SynonymKey(item);
            int index = merged.FindIndex(candidate => string.Equals(SynonymKey(candidate), key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                merged.Add(item);
            else
            {
                var current = merged[index];
                merged[index] = current with
                {
                    Pos = FirstValue(current.Pos, item.Pos),
                    Gloss = FirstValue(current.Gloss, item.Gloss),
                    Words = MergeStrings(current.Words, item.Words),
                };
            }
        }
        return merged.ToArray();
    }

    private static WordEtymology[] MergeEtymologies(IReadOnlyList<WordEtymology> existing, IReadOnlyList<WordEtymology> imported)
    {
        var merged = existing.ToList();
        foreach (var item in imported)
        {
            string key = FirstValue(item.Title, item.Description) ?? "";
            int index = merged.FindIndex(candidate => string.Equals(
                FirstValue(candidate.Title, candidate.Description), key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                merged.Add(item);
            else
                merged[index] = merged[index] with
                {
                    Title = FirstValue(merged[index].Title, item.Title) ?? "",
                    Description = FirstValue(merged[index].Description, item.Description) ?? "",
                };
        }
        return merged.ToArray();
    }

    private static WordRelatedGroup[] MergeRelatedGroups(IReadOnlyList<WordRelatedGroup> existing,
        IReadOnlyList<WordRelatedGroup> imported)
    {
        var merged = existing.ToList();
        foreach (var item in imported)
        {
            int index = merged.FindIndex(candidate => string.Equals(
                candidate.Pos?.Trim(), item.Pos?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                merged.Add(item);
            else
                merged[index] = merged[index] with
                {
                    Pos = FirstValue(merged[index].Pos, item.Pos),
                    Words = MergeWordTexts(merged[index].Words, item.Words),
                };
        }
        return merged.ToArray();
    }

    private static string[] MergeStrings(IReadOnlyList<string> existing, IReadOnlyList<string> imported)
    {
        var merged = existing.ToList();
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var value in imported)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value.Trim()))
                merged.Add(value.Trim());
        }
        return merged.ToArray();
    }

    private static string TextKey(WordText text) => !string.IsNullOrWhiteSpace(text.English)
        ? "e:" + text.English.Trim()
        : "c:" + text.Chinese.Trim();

    private static string SynonymKey(WordSynonym synonym)
    {
        string label = string.Join("\u001f", new[] { synonym.Pos, synonym.Gloss }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));
        return label.Length != 0
            ? "label:" + label
            : "words:" + string.Join("\u001f", synonym.Words.Select(word => word.Trim()));
    }

    private static string? FirstValue(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second.Trim()) : first.Trim();
}
