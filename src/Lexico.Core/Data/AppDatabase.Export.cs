using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lexico.Core.Data;

public sealed partial class AppDatabase
{
    public Task ExportReviewHistoryCsvAsync(string destinationPath) => RunAsync(() =>
    {
        var destination = Path.GetFullPath(destinationPath);
        EnsureDifferentDatabase(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stagingPath = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new StreamWriter(stagingPath, false, new UTF8Encoding(false)))
            {
                WriteRow(writer,
                [
                    "id", "card_id", "reviewed_at", "rating", "state_before", "step_before", "stability_before",
                    "difficulty_before", "scheduled_days_before", "reps_before", "raw_elapsed_ms", "effective_elapsed_ms",
                    "timing_valid", "fsrs_retrievability", "engine_version", "next_due", "next_interval_days", "hint_used", "session_id",
                    "profile_id", "raw_recall", "predicted_recall", "model_version", "features_json",
                ]);

                using var cmd = Cmd("""
                    SELECT l.id, l.card_id, l.reviewed_at, l.rating, l.state_before, l.step_before,
                           l.stability_before, l.difficulty_before, l.scheduled_days_before, l.reps_before,
                           l.raw_elapsed_ms, l.elapsed_ms, l.timing_valid, l.fsrs_retrievability,
                           l.engine_version, l.next_due, l.next_interval_days, l.hint_used, l.session_id,
                           p.profile_id, p.raw_recall, p.predicted_recall, p.model_version, p.features_json
                    FROM review_logs l
                    LEFT JOIN model_predictions p ON p.id=(
                        SELECT MAX(p2.id) FROM model_predictions p2 WHERE p2.review_log_id=l.id
                    )
                    ORDER BY l.id
                    """);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var values = new string?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        values[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    WriteRow(writer, values);
                }
            }
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            TryDeleteDatabaseFiles(stagingPath);
        }
        return 0;
    });

    private static void WriteRow(TextWriter writer, IEnumerable<string?> values)
        => writer.WriteLine(string.Join(',', values.Select(CsvCell)));

    private static string CsvCell(string? value)
    {
        var text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
