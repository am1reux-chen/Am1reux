using System.Globalization;
using Lexico.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Lexico.Core.Data;

public sealed record BuiltInVocabularyDeckImport(
    string Id,
    string Name,
    string? Description,
    string Source,
    IReadOnlyList<WordEntry> Entries);

public sealed record BuiltInVocabularyPack(
    int Version,
    string ContentHash,
    IReadOnlyList<BuiltInVocabularyDeckImport> Decks);

public sealed record BuiltInVocabularyImportResult(
    bool AlreadyApplied,
    int WordsAdded,
    int WordsMatched,
    int DecksAdded,
    int DeckWordsAdded,
    int CardsAdded);

public sealed partial class AppDatabase
{
    private const string BuiltInVocabularyStateKey = "builtin_vocabulary_state";
    private const string BuiltInVocabularySource = "builtin-vocabulary-v1";

    /// <summary>Atomically adds a versioned built-in vocabulary pack without replacing existing user data.</summary>
    public Task<BuiltInVocabularyImportResult> ImportBuiltInVocabularyAsync(BuiltInVocabularyPack pack) =>
        RunAsync(() => ImportBuiltInVocabulary(pack));

    private BuiltInVocabularyImportResult ImportBuiltInVocabulary(BuiltInVocabularyPack pack)
    {
        ValidateBuiltInVocabularyPack(pack);

        using var tx = _conn.BeginTransaction();
        using (var stateCommand = Cmd("SELECT value FROM settings WHERE key=@key", ("@key", BuiltInVocabularyStateKey)))
        {
            stateCommand.Transaction = tx;
            var stateValue = stateCommand.ExecuteScalar();
            if (stateValue is string state)
            {
                var parts = state.Split('|', 2);
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var appliedVersion) ||
                    appliedVersion < 1 || parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit))
                    throw new InvalidOperationException("The stored built-in vocabulary state is invalid.");

                if (pack.Version < appliedVersion)
                    return EmptyVocabularyImport(alreadyApplied: true);
                if (pack.Version == appliedVersion)
                {
                    if (!string.Equals(pack.ContentHash, parts[1], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A built-in vocabulary pack changed without increasing its version.");
                    return EmptyVocabularyImport(alreadyApplied: true);
                }
            }
        }

        if (pack.Version >= 2)
        {
            using var correction = Cmd("""
                UPDATE word_senses
                SET pos='det.', zh_gloss='定冠词（用于特指的人或事物）'
                WHERE pos='adv.' AND zh_gloss='愈……愈……'
                  AND word_id IN (
                    SELECT dw.word_id
                    FROM deck_words dw JOIN decks d ON d.id=dw.deck_id
                    WHERE d.built_in=1 AND d.name='常见英语词汇'
                  )
                """);
            correction.Transaction = tx;
            correction.ExecuteNonQuery();
        }

        var deckIds = LoadOrCreateVocabularyDecks(pack.Decks, tx, out int decksAdded);
        var wordsByLemma = LoadVocabularyWords(tx);
        var originalWordIds = wordsByLemma.Values.Select(word => word.Id).ToHashSet();
        var sensesByWord = LoadVocabularySenses(tx, out var nextSenseOrder);
        using var insertWord = Prepare(tx, """
            INSERT INTO words(lemma, ipa_us, ipa_uk, frequency_rank, cefr)
            VALUES(@lemma, @ipaUs, @ipaUk, @rank, @cefr)
            """, "@lemma", "@ipaUs", "@ipaUk", "@rank", "@cefr");
        using var updateWord = Prepare(tx, """
            UPDATE words SET
                ipa_us=CASE WHEN ipa_us IS NULL OR trim(ipa_us)='' THEN @ipaUs ELSE ipa_us END,
                ipa_uk=CASE WHEN ipa_uk IS NULL OR trim(ipa_uk)='' THEN @ipaUk ELSE ipa_uk END,
                frequency_rank=CASE WHEN frequency_rank IS NULL THEN @rank ELSE frequency_rank END,
                cefr=CASE WHEN cefr IS NULL OR trim(cefr)='' THEN @cefr ELSE cefr END
            WHERE id=@id
            """, "@ipaUs", "@ipaUk", "@rank", "@cefr", "@id");
        using var insertSense = Prepare(tx, """
            INSERT INTO word_senses(word_id, pos, zh_gloss, en_gloss, example_en, example_zh, sense_order)
            VALUES(@word, @pos, @zh, @en, @exampleEn, @exampleZh, @order)
            """, "@word", "@pos", "@zh", "@en", "@exampleEn", "@exampleZh", "@order");
        using var fillSense = Prepare(tx, """
            UPDATE word_senses SET
                en_gloss=CASE WHEN en_gloss IS NULL OR trim(en_gloss)='' THEN @en ELSE en_gloss END,
                example_en=CASE WHEN example_en IS NULL OR trim(example_en)='' THEN @exampleEn ELSE example_en END,
                example_zh=CASE WHEN example_zh IS NULL OR trim(example_zh)='' THEN @exampleZh ELSE example_zh END
            WHERE id=@id
            """, "@en", "@exampleEn", "@exampleZh", "@id");
        using var insertMembership = Prepare(tx, """
            INSERT INTO deck_words(deck_id, word_id, position) VALUES(@deck, @word, @position)
            ON CONFLICT(deck_id, word_id) DO NOTHING
            """, "@deck", "@word", "@position");

        int wordsAdded = 0;
        var matchedWordIds = new HashSet<long>();
        foreach (var deck in pack.Decks)
        {
            foreach (var entry in deck.Entries)
            {
                if (!wordsByLemma.TryGetValue(entry.Lemma, out var word))
                {
                    Set(insertWord, "@lemma", entry.Lemma);
                    Set(insertWord, "@ipaUs", entry.IpaUs);
                    Set(insertWord, "@ipaUk", entry.IpaUk);
                    Set(insertWord, "@rank", entry.FrequencyRank);
                    Set(insertWord, "@cefr", entry.Cefr);
                    insertWord.ExecuteNonQuery();

                    using var idCommand = _conn.CreateCommand();
                    idCommand.Transaction = tx;
                    idCommand.CommandText = "SELECT last_insert_rowid()";
                    var id = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                    word = new ExistingVocabularyWord(id, entry.Lemma, entry.IpaUs, entry.IpaUk,
                        entry.FrequencyRank, entry.Cefr);
                    wordsByLemma.Add(entry.Lemma, word);
                    wordsAdded++;
                }
                else
                {
                    if (originalWordIds.Contains(word.Id))
                        matchedWordIds.Add(word.Id);
                    if (NeedsWordUpdate(word, entry))
                    {
                        Set(updateWord, "@ipaUs", entry.IpaUs);
                        Set(updateWord, "@ipaUk", entry.IpaUk);
                        Set(updateWord, "@rank", entry.FrequencyRank);
                        Set(updateWord, "@cefr", entry.Cefr);
                        Set(updateWord, "@id", word.Id);
                        updateWord.ExecuteNonQuery();
                        word = FillMissingWordFields(word, entry);
                        wordsByLemma[entry.Lemma] = word;
                    }
                }

                var wordSenses = sensesByWord.GetValueOrDefault(word.Id);
                if (wordSenses is null)
                {
                    wordSenses = new Dictionary<string, ExistingVocabularySense>(StringComparer.Ordinal);
                    sensesByWord.Add(word.Id, wordSenses);
                    nextSenseOrder[word.Id] = 0;
                }

                foreach (var sense in entry.Senses)
                {
                    var key = VocabularySenseKey(sense.Pos, sense.ZhGloss);
                    if (wordSenses.TryGetValue(key, out var existingSense))
                    {
                        if (NeedsSenseUpdate(existingSense, sense))
                        {
                            Set(fillSense, "@en", sense.EnGloss);
                            Set(fillSense, "@exampleEn", sense.ExampleEn);
                            Set(fillSense, "@exampleZh", sense.ExampleZh);
                            Set(fillSense, "@id", existingSense.Id);
                            fillSense.ExecuteNonQuery();
                            wordSenses[key] = FillMissingSenseFields(existingSense, sense);
                        }
                        continue;
                    }

                    var order = nextSenseOrder.GetValueOrDefault(word.Id);
                    Set(insertSense, "@word", word.Id);
                    Set(insertSense, "@pos", sense.Pos);
                    Set(insertSense, "@zh", sense.ZhGloss);
                    Set(insertSense, "@en", sense.EnGloss);
                    Set(insertSense, "@exampleEn", sense.ExampleEn);
                    Set(insertSense, "@exampleZh", sense.ExampleZh);
                    Set(insertSense, "@order", order);
                    insertSense.ExecuteNonQuery();
                    using var senseIdCommand = _conn.CreateCommand();
                    senseIdCommand.Transaction = tx;
                    senseIdCommand.CommandText = "SELECT last_insert_rowid()";
                    var senseId = Convert.ToInt64(senseIdCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                    wordSenses.Add(key, new ExistingVocabularySense(senseId, sense.EnGloss, sense.ExampleEn, sense.ExampleZh));
                    nextSenseOrder[word.Id] = order + 1;
                }
            }
        }

        int deckWordsAdded = 0;
        foreach (var deck in pack.Decks)
        {
            var deckId = deckIds[deck.Id];
            var position = GetNextDeckPosition(deckId, tx);
            foreach (var entry in deck.Entries)
            {
                Set(insertMembership, "@deck", deckId);
                Set(insertMembership, "@word", wordsByLemma[entry.Lemma].Id);
                Set(insertMembership, "@position", position);
                var inserted = insertMembership.ExecuteNonQuery();
                deckWordsAdded += inserted;
                if (inserted != 0)
                    position++;
            }
        }

        var now = Fmt(DateTime.UtcNow);
        int cardsAdded = 0;
        using (var insertCards = Prepare(tx, """
        INSERT INTO cards(word_id, deck_id, state, step, due, created_at)
        SELECT dw.word_id, dw.deck_id, 0, 0, @now, @now
        FROM deck_words dw
        WHERE dw.deck_id=@deck
          AND NOT EXISTS (SELECT 1 FROM cards c WHERE c.word_id=dw.word_id AND c.deck_id=dw.deck_id)
        """, "@now", "@deck"))
        {
            foreach (var deck in pack.Decks)
            {
                Set(insertCards, "@now", now);
                Set(insertCards, "@deck", deckIds[deck.Id]);
                cardsAdded += insertCards.ExecuteNonQuery();
            }
        }

        ExecWithTransaction(tx, """
            INSERT INTO settings(key, value) VALUES(@key, @value)
            ON CONFLICT(key) DO UPDATE SET value=@value
            """, ("@key", BuiltInVocabularyStateKey),
            ("@value", pack.Version.ToString(CultureInfo.InvariantCulture) + "|" + pack.ContentHash.ToUpperInvariant()));
        tx.Commit();

        return new BuiltInVocabularyImportResult(false, wordsAdded, matchedWordIds.Count,
            decksAdded, deckWordsAdded, cardsAdded);
    }

    private Dictionary<string, long> LoadOrCreateVocabularyDecks(
        IReadOnlyList<BuiltInVocabularyDeckImport> requestedDecks,
        SqliteTransaction tx,
        out int decksAdded)
    {
        var existing = new Dictionary<string, (long Id, bool BuiltIn, string? Source)>(StringComparer.OrdinalIgnoreCase);
        using (var read = Cmd("SELECT id, name, built_in, source FROM decks ORDER BY id"))
        {
            read.Transaction = tx;
            using var reader = read.ExecuteReader();
            while (reader.Read())
                existing.TryAdd(reader.GetString(1), (reader.GetInt64(0), reader.GetInt64(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        decksAdded = 0;
        foreach (var deck in requestedDecks)
        {
            var mappingKey = $"builtin_vocabulary_deck:{deck.Id}";
            string? mappedName;
            using (var mapping = Cmd("SELECT value FROM settings WHERE key=@key", ("@key", mappingKey)))
            {
                mapping.Transaction = tx;
                mappedName = mapping.ExecuteScalar() as string;
            }

            var chosenName = ResolveBuiltInVocabularyDeckName(deck.Name, mappedName, deck.Source, existing);
            if (existing.TryGetValue(chosenName, out var found))
            {
                result.Add(deck.Id, found.Id);
                SaveVocabularyDeckName(tx, mappingKey, chosenName);
                continue;
            }

            using var insert = Cmd("""
                INSERT INTO decks(name, description, source, built_in, created_at)
                VALUES(@name, @description, @source, 1, @created)
                """, ("@name", chosenName), ("@description", deck.Description), ("@source", deck.Source),
                ("@created", Fmt(DateTime.UtcNow)));
            insert.Transaction = tx;
            insert.ExecuteNonQuery();
            using var lastId = _conn.CreateCommand();
            lastId.Transaction = tx;
            lastId.CommandText = "SELECT last_insert_rowid()";
            var id = Convert.ToInt64(lastId.ExecuteScalar(), CultureInfo.InvariantCulture);
            result.Add(deck.Id, id);
            existing.Add(chosenName, (id, true, deck.Source));
            SaveVocabularyDeckName(tx, mappingKey, chosenName);
            decksAdded++;
        }
        return result;
    }

    private static string ResolveBuiltInVocabularyDeckName(
        string baseName,
        string? mappedName,
        string source,
        IReadOnlyDictionary<string, (long Id, bool BuiltIn, string? Source)> existing)
    {
        var preferredName = mappedName ?? baseName;
        if (!existing.TryGetValue(preferredName, out var preferred) ||
            (preferred.BuiltIn && string.Equals(preferred.Source, source, StringComparison.Ordinal)))
            return preferredName;

        for (int suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1
                ? $"{baseName}（Lexio 内置）"
                : $"{baseName}（Lexio 内置 {suffix}）";
            if (!existing.TryGetValue(candidate, out var found) ||
                (found.BuiltIn && string.Equals(found.Source, source, StringComparison.Ordinal)))
                return candidate;
        }
    }

    private void SaveVocabularyDeckName(SqliteTransaction tx, string key, string name)
    {
        using var command = Cmd("""
            INSERT INTO settings(key, value) VALUES(@key, @value)
            ON CONFLICT(key) DO UPDATE SET value=@value
            """, ("@key", key), ("@value", name));
        command.Transaction = tx;
        command.ExecuteNonQuery();
    }

    private Dictionary<string, ExistingVocabularyWord> LoadVocabularyWords(SqliteTransaction tx)
    {
        var words = new Dictionary<string, ExistingVocabularyWord>(StringComparer.OrdinalIgnoreCase);
        using var command = Cmd("""
            SELECT id, lemma, ipa_us, ipa_uk, frequency_rank, cefr FROM words ORDER BY id
            """);
        command.Transaction = tx;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            words.TryAdd(reader.GetString(1), new ExistingVocabularyWord(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return words;
    }

    private Dictionary<long, Dictionary<string, ExistingVocabularySense>> LoadVocabularySenses(
        SqliteTransaction tx,
        out Dictionary<long, int> nextSenseOrder)
    {
        var senses = new Dictionary<long, Dictionary<string, ExistingVocabularySense>>();
        nextSenseOrder = new Dictionary<long, int>();
        using var command = Cmd("""
            SELECT id, word_id, pos, zh_gloss, en_gloss, example_en, example_zh, sense_order
            FROM word_senses ORDER BY word_id, sense_order, id
            """);
        command.Transaction = tx;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var wordId = reader.GetInt64(1);
            var pos = reader.IsDBNull(2) ? null : reader.GetString(2);
            var gloss = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (!senses.TryGetValue(wordId, out var byKey))
                senses.Add(wordId, byKey = new Dictionary<string, ExistingVocabularySense>(StringComparer.Ordinal));
            if (pos is not null && gloss is not null)
                byKey.TryAdd(VocabularySenseKey(pos, gloss), new ExistingVocabularySense(id,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            nextSenseOrder[wordId] = reader.GetInt32(7) + 1;
        }
        return senses;
    }

    private int GetNextDeckPosition(long deckId, SqliteTransaction tx)
    {
        using var command = Cmd("SELECT COALESCE(MAX(position), -1) + 1 FROM deck_words WHERE deck_id=@deck", ("@deck", deckId));
        command.Transaction = tx;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private SqliteCommand Prepare(SqliteTransaction tx, string sql, params string[] parameterNames)
    {
        var command = Cmd(sql);
        command.Transaction = tx;
        foreach (var parameterName in parameterNames)
            command.Parameters.AddWithValue(parameterName, DBNull.Value);
        command.Prepare();
        return command;
    }

    private static void Set(SqliteCommand command, string parameterName, object? value) =>
        command.Parameters[parameterName].Value = value ?? DBNull.Value;

    private int ExecWithTransaction(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Cmd(sql, parameters);
        command.Transaction = tx;
        return command.ExecuteNonQuery();
    }

    private static bool NeedsWordUpdate(ExistingVocabularyWord existing, WordEntry incoming) =>
        (string.IsNullOrWhiteSpace(existing.IpaUs) && !string.IsNullOrWhiteSpace(incoming.IpaUs)) ||
        (string.IsNullOrWhiteSpace(existing.IpaUk) && !string.IsNullOrWhiteSpace(incoming.IpaUk)) ||
        (existing.FrequencyRank is null && incoming.FrequencyRank is not null) ||
        (string.IsNullOrWhiteSpace(existing.Cefr) && !string.IsNullOrWhiteSpace(incoming.Cefr));

    private static ExistingVocabularyWord FillMissingWordFields(ExistingVocabularyWord existing, WordEntry incoming) => existing with
    {
        IpaUs = string.IsNullOrWhiteSpace(existing.IpaUs) ? incoming.IpaUs : existing.IpaUs,
        IpaUk = string.IsNullOrWhiteSpace(existing.IpaUk) ? incoming.IpaUk : existing.IpaUk,
        FrequencyRank = existing.FrequencyRank ?? incoming.FrequencyRank,
        Cefr = string.IsNullOrWhiteSpace(existing.Cefr) ? incoming.Cefr : existing.Cefr,
    };

    private static bool NeedsSenseUpdate(ExistingVocabularySense existing, WordSense incoming) =>
        (string.IsNullOrWhiteSpace(existing.EnGloss) && !string.IsNullOrWhiteSpace(incoming.EnGloss)) ||
        (string.IsNullOrWhiteSpace(existing.ExampleEn) && !string.IsNullOrWhiteSpace(incoming.ExampleEn)) ||
        (string.IsNullOrWhiteSpace(existing.ExampleZh) && !string.IsNullOrWhiteSpace(incoming.ExampleZh));

    private static ExistingVocabularySense FillMissingSenseFields(ExistingVocabularySense existing, WordSense incoming) => existing with
    {
        EnGloss = string.IsNullOrWhiteSpace(existing.EnGloss) ? incoming.EnGloss : existing.EnGloss,
        ExampleEn = string.IsNullOrWhiteSpace(existing.ExampleEn) ? incoming.ExampleEn : existing.ExampleEn,
        ExampleZh = string.IsNullOrWhiteSpace(existing.ExampleZh) ? incoming.ExampleZh : existing.ExampleZh,
    };

    private static string VocabularySenseKey(string? pos, string? gloss) =>
        string.Concat(pos?.Trim().ToUpperInvariant(), "\0", gloss?.Trim());

    private static BuiltInVocabularyImportResult EmptyVocabularyImport(bool alreadyApplied) =>
        new(alreadyApplied, 0, 0, 0, 0, 0);

    private static void ValidateBuiltInVocabularyPack(BuiltInVocabularyPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Version < 1)
            throw new ArgumentException("The built-in vocabulary version must be positive.", nameof(pack));
        if (pack.ContentHash is null || pack.ContentHash.Length != 64 || !pack.ContentHash.All(Uri.IsHexDigit))
            throw new ArgumentException("The built-in vocabulary hash must be a SHA-256 hex string.", nameof(pack));
        if (pack.Decks is null || pack.Decks.Count == 0)
            throw new ArgumentException("The built-in vocabulary pack must contain decks.", nameof(pack));

        var deckIds = new HashSet<string>(StringComparer.Ordinal);
        var deckNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deck in pack.Decks)
        {
            if (deck is null || string.IsNullOrWhiteSpace(deck.Id) || !deckIds.Add(deck.Id) ||
                string.IsNullOrWhiteSpace(deck.Name) || !deckNames.Add(deck.Name))
                throw new ArgumentException("The built-in vocabulary pack has an invalid or duplicate deck.", nameof(pack));
            if (!string.Equals(deck.Source, BuiltInVocabularySource, StringComparison.Ordinal))
                throw new ArgumentException("The built-in vocabulary deck source is invalid.", nameof(pack));
            if (deck.Entries is null || deck.Entries.Count == 0)
                throw new ArgumentException($"The built-in vocabulary deck '{deck.Name}' has no entries.", nameof(pack));

            var lemmas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in deck.Entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Lemma) || entry.Lemma != entry.Lemma.Trim() ||
                    entry.FrequencyRank is < 1 ||
                    !lemmas.Add(entry.Lemma) || entry.Senses is null || entry.Senses.Count == 0)
                    throw new ArgumentException($"The built-in vocabulary deck '{deck.Name}' has an invalid or duplicate word.", nameof(pack));
                var senses = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sense in entry.Senses)
                {
                    if (sense is null || string.IsNullOrWhiteSpace(sense.Pos) || string.IsNullOrWhiteSpace(sense.ZhGloss) ||
                        !senses.Add(VocabularySenseKey(sense.Pos, sense.ZhGloss)))
                        throw new ArgumentException($"The word '{entry.Lemma}' has an invalid or duplicate sense.", nameof(pack));
                }
            }
        }
    }

    private sealed record ExistingVocabularyWord(
        long Id,
        string Lemma,
        string? IpaUs,
        string? IpaUk,
        int? FrequencyRank,
        string? Cefr);

    private sealed record ExistingVocabularySense(
        long Id,
        string? EnGloss,
        string? ExampleEn,
        string? ExampleZh);
}
