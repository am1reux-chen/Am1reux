using System.Diagnostics;

namespace Lexio.Services;

public sealed record StartupFailureInfo(
    string Title,
    string UserSummary,
    string RecoveryInstructions,
    string DatabasePath,
    string BackupPath,
    string BackupStatus,
    string LogPath,
    bool LogWritten);

/// <summary>Separates user-safe startup guidance from technical diagnostics stored in a local log.</summary>
public static class StartupFailureReporter
{
    public static StartupFailureInfo Capture(Exception error, string databasePath, string backupPath, string logPath)
    {
        ArgumentNullException.ThrowIfNull(error);
        var database = Path.GetFullPath(databasePath);
        var backup = Path.GetFullPath(backupPath);
        var log = Path.GetFullPath(logPath);
        var hasBackup = File.Exists(backup);
        var backupRejected = hasBackup && HasMessage(error, "自动备份无效");
        var status = !hasBackup
            ? "没有找到自动备份文件。"
            : backupRejected
                ? "自动备份文件存在，但未通过校验或恢复。"
                : "检测到自动备份文件，但其可用性尚未确认。";
        var instructions =
            $"Lexio 没有打开学习界面，也没有自动删除或重建数据库。主数据库位置：{database}\n" +
            $"自动备份位置：{backup}（{status}）\n\n" +
            "请保留上述文件原样。你可以从可靠来源准备一个 Lexio 完整数据备份，然后在此窗口选择“选择完整备份并恢复”；" +
            "若主数据库被判定为损坏，Lexio 会将其保留为同目录的 .corrupt-* 文件，再校验所选完整备份。复习历史 CSV 仅包含历史记录，不能用于完整恢复。";

        var logWritten = TryWriteLog(error, database, backup, log);
        return new StartupFailureInfo(
            "Lexio 无法启动",
            "启动时无法完成学习数据库的打开或完整性校验。数据库文件已保留；请按下面的指引检查或恢复数据。",
            instructions,
            database,
            backup,
            status,
            log,
            logWritten);
    }

    public static void WriteTechnicalError(Exception error, string logPath, string operation)
    {
        try
        {
            var fullPath = Path.GetFullPath(logPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.AppendAllText(fullPath,
                $"[{DateTimeOffset.Now:O}] {operation}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logError)
        {
            Debug.WriteLine($"[Lexio] Could not write {operation} log: {logError}");
        }
    }

    public static bool LogUnhandledException(Exception error, string logPath)
    {
        WriteTechnicalError(error, logPath, "unhandled WinUI exception");
        return false;
    }

    private static bool TryWriteLog(Exception error, string databasePath, string backupPath, string logPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[{DateTimeOffset.Now:O}] startup failed{Environment.NewLine}" +
                $"Database: {databasePath}{Environment.NewLine}Automatic backup: {backupPath}{Environment.NewLine}" +
                $"Runtime: {Environment.Version}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            return true;
        }
        catch (Exception logError)
        {
            Debug.WriteLine($"[Lexio] Startup diagnostics could not be written: {error}; log error: {logError}");
            return false;
        }
    }

    private static bool HasMessage(Exception error, string fragment)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current.Message.Contains(fragment, StringComparison.Ordinal))
                return true;
        return false;
    }
}
