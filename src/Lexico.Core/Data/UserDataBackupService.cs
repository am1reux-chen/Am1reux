namespace Lexico.Core.Data;

public sealed record UserDataRestoreResult(
    string SafetyBackupPath,
    bool AutomaticBackupUpdated,
    Exception? AutomaticBackupError);

public sealed class UserDataRestoreException : Exception
{
    public string SafetyBackupPath { get; }
    public bool CurrentDataRestored { get; }

    public UserDataRestoreException(string safetyBackupPath, Exception innerException, bool currentDataRestored = true)
        : base("完整数据备份恢复失败；恢复前的安全快照已保留。", innerException)
    {
        SafetyBackupPath = safetyBackupPath;
        CurrentDataRestored = currentDataRestored;
    }
}

/// <summary>Creates full SQLite snapshots and protects current data before a user-requested restore.</summary>
public sealed class UserDataBackupService(AppDatabase database, string databasePath, string automaticBackupPath)
{
    public Task CreateAsync(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return database.BackupToAsync(destinationPath);
    }

    public async Task<UserDataRestoreResult> RestoreAsync(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var safetyBackupPath = Path.GetFullPath(databasePath) + ".pre-restore-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".backup";
        await database.BackupToAsync(safetyBackupPath).ConfigureAwait(false);

        try
        {
            await database.RestoreFromAsync(sourcePath).ConfigureAwait(false);
            await database.ValidateAsync().ConfigureAwait(false);
        }
        catch (Exception restoreError)
        {
            try
            {
                await database.RestoreFromAsync(safetyBackupPath).ConfigureAwait(false);
                await database.ValidateAsync().ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new UserDataRestoreException(safetyBackupPath,
                    new AggregateException("备份恢复和恢复前数据回滚均失败。", restoreError, rollbackError),
                    currentDataRestored: false);
            }

            throw new UserDataRestoreException(safetyBackupPath, restoreError);
        }

        try
        {
            await database.BackupToAsync(automaticBackupPath).ConfigureAwait(false);
            return new UserDataRestoreResult(safetyBackupPath, true, null);
        }
        catch (Exception error)
        {
            return new UserDataRestoreResult(safetyBackupPath, false, error);
        }
    }
}
