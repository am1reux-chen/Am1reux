using Microsoft.Data.Sqlite;

namespace Lexico.Core.Data;

/// <summary>Ordered, idempotent schema migrations. Never edit an applied script; append a new version.</summary>
internal static class Migrations
{
    internal static readonly (int Version, string Sql)[] All =
    [
        (1, """
            CREATE TABLE words(
                id INTEGER PRIMARY KEY,
                lemma TEXT NOT NULL,
                ipa_us TEXT,
                ipa_uk TEXT,
                frequency_rank INTEGER,
                cefr TEXT
            );
            CREATE UNIQUE INDEX idx_words_lemma ON words(lemma);

            CREATE TABLE word_senses(
                id INTEGER PRIMARY KEY,
                word_id INTEGER NOT NULL REFERENCES words(id) ON DELETE CASCADE,
                pos TEXT,
                zh_gloss TEXT,
                en_gloss TEXT,
                example_en TEXT,
                example_zh TEXT,
                sense_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX idx_senses_word ON word_senses(word_id);

            CREATE TABLE decks(
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                description TEXT,
                source TEXT,
                built_in INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );

            CREATE TABLE deck_words(
                deck_id INTEGER NOT NULL REFERENCES decks(id) ON DELETE CASCADE,
                word_id INTEGER NOT NULL REFERENCES words(id) ON DELETE CASCADE,
                position INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(deck_id, word_id)
            );
            CREATE INDEX idx_deck_words_word ON deck_words(word_id);

            CREATE TABLE cards(
                id INTEGER PRIMARY KEY,
                word_id INTEGER NOT NULL REFERENCES words(id),
                deck_id INTEGER NOT NULL REFERENCES decks(id),
                state INTEGER NOT NULL DEFAULT 0,
                step INTEGER,
                stability REAL,
                difficulty REAL,
                due TEXT NOT NULL,
                last_review TEXT,
                reps INTEGER NOT NULL DEFAULT 0,
                lapses INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE INDEX idx_cards_due ON cards(state, due);
            CREATE INDEX idx_cards_word ON cards(word_id);

            CREATE TABLE review_logs(
                id INTEGER PRIMARY KEY,
                card_id INTEGER NOT NULL REFERENCES cards(id),
                reviewed_at TEXT NOT NULL,
                rating INTEGER NOT NULL,
                state_before INTEGER NOT NULL,
                step_before INTEGER,
                stability_before REAL,
                difficulty_before REAL,
                scheduled_days_before REAL,
                reps_before INTEGER NOT NULL DEFAULT 0,
                raw_elapsed_ms REAL NOT NULL DEFAULT 0,
                elapsed_ms REAL NOT NULL DEFAULT 0,
                timing_valid INTEGER NOT NULL DEFAULT 0,
                fsrs_retrievability REAL,
                engine_version TEXT NOT NULL,
                next_due TEXT NOT NULL,
                next_interval_days REAL NOT NULL
            );
            CREATE INDEX idx_logs_card ON review_logs(card_id);
            CREATE INDEX idx_logs_time ON review_logs(reviewed_at);

            CREATE TABLE sessions(
                id INTEGER PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                intensity INTEGER NOT NULL,
                reviews INTEGER NOT NULL DEFAULT 0,
                again INTEGER NOT NULL DEFAULT 0,
                hard INTEGER NOT NULL DEFAULT 0,
                good INTEGER NOT NULL DEFAULT 0,
                easy INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE daily_plans(
                id INTEGER PRIMARY KEY,
                date TEXT NOT NULL,
                intensity INTEGER NOT NULL,
                review_count INTEGER NOT NULL,
                new_count INTEGER NOT NULL,
                estimated_minutes REAL NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(date, intensity)
            );

            CREATE TABLE model_profiles(
                id INTEGER PRIMARY KEY,
                kind TEXT NOT NULL,
                version INTEGER NOT NULL,
                weights TEXT NOT NULL,
                trained_at TEXT NOT NULL,
                metrics TEXT,
                is_active INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE model_predictions(
                id INTEGER PRIMARY KEY,
                review_log_id INTEGER NOT NULL REFERENCES review_logs(id),
                profile_id INTEGER NOT NULL REFERENCES model_profiles(id),
                predicted_recall REAL NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX idx_predictions_log ON model_predictions(review_log_id);

            CREATE TABLE settings(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """),
        (2, """
            CREATE VIRTUAL TABLE words_fts USING fts5(
                lemma, content='words', content_rowid='id', tokenize='unicode61'
            );
            CREATE TRIGGER words_ai AFTER INSERT ON words BEGIN
                INSERT INTO words_fts(rowid, lemma) VALUES (new.id, new.lemma);
            END;
            CREATE TRIGGER words_ad AFTER DELETE ON words BEGIN
                INSERT INTO words_fts(words_fts, rowid, lemma) VALUES('delete', old.id, old.lemma);
            END;
            CREATE TRIGGER words_au AFTER UPDATE ON words BEGIN
                INSERT INTO words_fts(words_fts, rowid, lemma) VALUES('delete', old.id, old.lemma);
                INSERT INTO words_fts(rowid, lemma) VALUES (new.id, new.lemma);
            END;
            """),
        (3, """
            ALTER TABLE review_logs ADD COLUMN hint_used INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE model_profiles ADD COLUMN trained_through_log_id INTEGER;
            ALTER TABLE model_profiles ADD COLUMN accepted INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE model_predictions ADD COLUMN raw_recall REAL;
            ALTER TABLE model_predictions ADD COLUMN model_version INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE model_predictions ADD COLUMN features_json TEXT NOT NULL DEFAULT '{}';
            UPDATE model_predictions SET model_version=(
                SELECT version FROM model_profiles WHERE model_profiles.id=model_predictions.profile_id
            );

            INSERT INTO model_profiles(kind, version, weights, trained_at, metrics, is_active)
            SELECT 'personal-recall-calibrator', 1, '[]', strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                   '{"status":"baseline","reason":"insufficient-data"}', 1
            WHERE NOT EXISTS (
                SELECT 1 FROM model_profiles WHERE kind='personal-recall-calibrator' AND is_active=1
            );
            """),
        (4, """
            ALTER TABLE review_logs ADD COLUMN session_id INTEGER REFERENCES sessions(id);
            CREATE INDEX idx_logs_session ON review_logs(session_id);
            CREATE TRIGGER review_logs_no_update BEFORE UPDATE ON review_logs BEGIN
                SELECT RAISE(ABORT, 'review_logs are immutable');
            END;
            CREATE TRIGGER review_logs_no_delete BEFORE DELETE ON review_logs BEGIN
                SELECT RAISE(ABORT, 'review_logs are immutable');
            END;
            """),
        (5, """
            ALTER TABLE sessions ADD COLUMN planned_cards INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sessions ADD COLUMN new_count INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sessions ADD COLUMN estimated_minutes REAL NOT NULL DEFAULT 0;

            CREATE TABLE session_queue(
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                card_id INTEGER NOT NULL REFERENCES cards(id),
                position INTEGER NOT NULL
            );
            CREATE INDEX idx_session_queue_order ON session_queue(session_id, position);
            """),
        (6, """
            ALTER TABLE session_queue ADD COLUMN is_early_review INTEGER NOT NULL DEFAULT 0;
            """),
        (7, """
            ALTER TABLE sessions ADD COLUMN study_mode INTEGER NOT NULL DEFAULT 0;

            CREATE TABLE card_marks(
                card_id INTEGER PRIMARY KEY REFERENCES cards(id) ON DELETE CASCADE,
                favorite INTEGER NOT NULL DEFAULT 0 CHECK(favorite IN (0, 1)),
                wrong INTEGER NOT NULL DEFAULT 0 CHECK(wrong IN (0, 1)),
                mastered INTEGER NOT NULL DEFAULT 0 CHECK(mastered IN (0, 1))
            );
            """),
        (8, """
            CREATE TABLE articles(
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE article_sentences(
                id INTEGER PRIMARY KEY,
                article_id INTEGER NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
                position INTEGER NOT NULL CHECK(position >= 0),
                text TEXT NOT NULL,
                translation TEXT,
                UNIQUE(article_id, position)
            );
            CREATE INDEX idx_article_sentences_order ON article_sentences(article_id, position);

            CREATE TABLE article_progress(
                article_id INTEGER PRIMARY KEY REFERENCES articles(id) ON DELETE CASCADE,
                current_sentence INTEGER NOT NULL DEFAULT 0 CHECK(current_sentence >= 0),
                mode INTEGER NOT NULL DEFAULT 0 CHECK(mode IN (0, 1)),
                is_complete INTEGER NOT NULL DEFAULT 0 CHECK(is_complete IN (0, 1)),
                updated_at TEXT NOT NULL
            );
            """),
        (9, """
            ALTER TABLE words ADD COLUMN details_json TEXT;
            ALTER TABLE articles ADD COLUMN title_translation TEXT;
            """),
    ];

    public static void Apply(SqliteConnection connection)
    {
        EnsureVersionsTable(connection);
        var applied = new HashSet<int>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_migrations";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                applied.Add(reader.GetInt32(0));
        }

        foreach (var (version, sql) in All)
        {
            if (applied.Contains(version))
                continue;
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql + ";\nINSERT INTO schema_migrations(version, applied_at) VALUES (" + version + ", '" + DateTimeOffset.UtcNow.ToString("o") + "');";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private static void EnsureVersionsTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations(
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
