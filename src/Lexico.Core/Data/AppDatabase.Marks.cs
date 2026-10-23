using Lexico.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Lexico.Core.Data;

public sealed partial class AppDatabase
{
    public Task<CardMarkState> GetCardMarksAsync(long cardId) => RunAsync(() =>
    {
        CardMarkState? state = null;
        using (var command = Cmd("SELECT favorite, wrong, mastered FROM card_marks WHERE card_id=@id", ("@id", cardId)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
                state = new CardMarkState(reader.GetInt32(0) != 0, reader.GetInt32(1) != 0, reader.GetInt32(2) != 0);
        }

        if (state is not null)
            return state;
        using var exists = Cmd("SELECT 1 FROM cards WHERE id=@id", ("@id", cardId));
        if (exists.ExecuteScalar() is null)
            throw new KeyNotFoundException($"Card {cardId} does not exist.");
        return new CardMarkState(false, false, false);
    });

    public async Task<List<LibraryCardEntry>> SearchLibraryCardsAsync(string query, int limit = 50, long? deckId = null)
    {
        int resultLimit = Math.Clamp(limit, 1, 500);
        var words = await SearchWordsAsync(query, resultLimit, deckId).ConfigureAwait(false);
        if (words.Count == 0)
            return [];

        var rank = words.Select((word, index) => (word.Id, index)).ToDictionary(row => row.Id, row => row.index);
        var ids = words.Select(word => word.Id).ToArray();
        var placeholders = string.Join(",", Enumerable.Range(0, ids.Length).Select(index => "@w" + index));
        var args = ids.Select((id, index) => ("@w" + index, (object?)id)).ToList();
        args.Add(("@deck", deckId));

        return await RunAsync(() =>
        {
            var rows = new List<(long CardId, long DeckId, string DeckName, long WordId, bool Favorite, bool Wrong, bool Mastered)>();
            using (var command = Cmd($"""
                SELECT c.id, c.deck_id, d.name, c.word_id,
                       COALESCE(m.favorite, 0), COALESCE(m.wrong, 0), COALESCE(m.mastered, 0)
                FROM cards c
                JOIN decks d ON d.id=c.deck_id
                LEFT JOIN card_marks m ON m.card_id=c.id
                WHERE c.word_id IN ({placeholders})
                  AND (@deck IS NULL OR c.deck_id=@deck)
                ORDER BY d.name COLLATE NOCASE, c.id
                LIMIT @limit
                """, args.Append(("@limit", (object?)resultLimit)).ToArray()))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                        reader.GetInt32(4) != 0, reader.GetInt32(5) != 0, reader.GetInt32(6) != 0));
                }
            }

            var wordsById = words.ToDictionary(word => word.Id);
            return rows
                .Where(row => wordsById.ContainsKey(row.WordId))
                .OrderBy(row => rank[row.WordId])
                .ThenBy(row => row.DeckName, StringComparer.OrdinalIgnoreCase)
                .Select(row => new LibraryCardEntry(row.CardId, row.DeckId, row.DeckName, wordsById[row.WordId],
                    row.Favorite, row.Wrong, row.Mastered))
                .Take(resultLimit)
                .ToList();
        }).ConfigureAwait(false);
    }

    public Task SetCardMarkAsync(long cardId, CardMark mark, bool enabled)
    {
        CardMarkColumn(mark);
        return RunAsync(() =>
        {
            using var transaction = _conn.BeginTransaction();
            SetCardMarkCore(transaction, cardId, mark, enabled);
            transaction.Commit();
        });
    }

    public Task<List<MarkedWordEntry>> SearchMarkedWordsAsync(
        string query,
        CardMark mark,
        long? deckId = null,
        int limit = 50)
    {
        string column = CardMarkColumn(mark);
        string prefix = query?.Trim() ?? string.Empty;
        int resultLimit = Math.Clamp(limit, 1, 500);

        return RunAsync(() =>
        {
            var rows = new List<(long CardId, long DeckId, string DeckName, long WordId, bool Favorite, bool Wrong, bool Mastered)>();
            using (var command = Cmd($"""
                SELECT c.id, c.deck_id, d.name, c.word_id, m.favorite, m.wrong, m.mastered
                FROM card_marks m
                JOIN cards c ON c.id=m.card_id
                JOIN decks d ON d.id=c.deck_id
                JOIN words w ON w.id=c.word_id
                WHERE m.{column}=1
                  AND (@deck IS NULL OR c.deck_id=@deck)
                  AND (@prefix='' OR w.lemma LIKE @prefix || '%')
                ORDER BY d.name COLLATE NOCASE, w.frequency_rank IS NULL, w.frequency_rank, w.lemma
                LIMIT @limit
                """, ("@deck", deckId), ("@prefix", prefix), ("@limit", resultLimit)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                        reader.GetInt32(4) != 0, reader.GetInt32(5) != 0, reader.GetInt32(6) != 0));
                }
            }

            var words = LoadWordsWithSensesCore(rows.Select(row => row.WordId).Distinct().ToArray())
                .ToDictionary(word => word.Id);
            return rows
                .Where(row => words.ContainsKey(row.WordId))
                .Select(row => new MarkedWordEntry(row.CardId, row.DeckId, row.DeckName, words[row.WordId],
                    row.Favorite, row.Wrong, row.Mastered))
                .ToList();
        });
    }

    private void SetCardMarkCore(SqliteTransaction transaction, long cardId, CardMark mark, bool enabled)
    {
        string column = CardMarkColumn(mark);
        using (var card = Cmd("SELECT 1 FROM cards WHERE id=@id", ("@id", cardId)))
        {
            card.Transaction = transaction;
            if (card.ExecuteScalar() is null)
                throw new KeyNotFoundException($"Card {cardId} does not exist.");
        }

        using (var ensure = Cmd("INSERT INTO card_marks(card_id) VALUES(@id) ON CONFLICT(card_id) DO NOTHING", ("@id", cardId)))
        {
            ensure.Transaction = transaction;
            ensure.ExecuteNonQuery();
        }

        using var update = Cmd($"UPDATE card_marks SET {column}=@enabled WHERE card_id=@id",
            ("@enabled", enabled ? 1 : 0), ("@id", cardId));
        update.Transaction = transaction;
        update.ExecuteNonQuery();
    }

    private static string CardMarkColumn(CardMark mark) => mark switch
    {
        CardMark.Favorite => "favorite",
        CardMark.Wrong => "wrong",
        CardMark.Mastered => "mastered",
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, "Unknown card mark."),
    };
}
