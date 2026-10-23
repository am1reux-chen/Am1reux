using Lexio.Services;
using Xunit;

namespace Lexico.App.Tests;

public sealed class StartupFailureReporterTests
{
    [Fact]
    public void MissingBackupProducesReadableRecoveryGuidanceAndKeepsDetailsInLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lexio-startup-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var primary = Path.Combine(directory, "lexio.db");
            var backup = primary + ".backup";
            var log = Path.Combine(directory, "logs", "startup.log");
            var failure = new InvalidDataException("Lexio 主数据库无法通过完整性检查，且没有可用备份");

            var info = StartupFailureReporter.Capture(failure, primary, backup, log);

            Assert.Contains(primary, info.RecoveryInstructions);
            Assert.Contains("没有找到", info.BackupStatus);
            Assert.DoesNotContain(failure.Message, info.UserSummary);
            Assert.Contains("完整备份", info.RecoveryInstructions);
            var technicalLog = File.ReadAllText(log);
            Assert.Contains(nameof(InvalidDataException), technicalLog);
            Assert.Contains(failure.Message, technicalLog);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExistingButRejectedBackupIsNotReportedAsUsable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lexio-startup-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var primary = Path.Combine(directory, "lexio.db");
            var backup = primary + ".backup";
            var log = Path.Combine(directory, "startup.log");
            File.WriteAllText(backup, "invalid fixture");

            var info = StartupFailureReporter.Capture(
                new InvalidDataException("Lexio 自动备份无效，原主数据库文件已保留"), primary, backup, log);

            Assert.Contains("存在，但未通过校验", info.BackupStatus);
            Assert.DoesNotContain("可以使用", info.BackupStatus);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnhandledExceptionIsLoggedAndNotSuppressed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lexio-unhandled-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Path.Combine(directory, "startup.log");
            var error = new InvalidOperationException("unhandled test failure");

            var handled = StartupFailureReporter.LogUnhandledException(error, log);

            Assert.False(handled);
            Assert.Contains(nameof(InvalidOperationException), File.ReadAllText(log));
            Assert.Contains(error.Message, File.ReadAllText(log));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
