using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace BackupAgent.Services;

public class BackupService : BackgroundService
{
    private readonly ILogger<BackupService> _logger;
    private readonly IConfiguration _config;
    private readonly Utilities.CommandRunner _runner;

    public BackupService(ILogger<BackupService> logger, IConfiguration config, Utilities.CommandRunner runner)
    {
        _logger = logger;
        _config = config;
        _runner = runner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupAgent started.");

        var intervalSeconds = _config.GetValue<int>("Backup:IntervalSeconds", 3600);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBackupOnce(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while running backup");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

        private async Task RunBackupOnce(CancellationToken ct)
    {
        var cfg = _config.GetSection("Backup");
        var host = cfg.GetValue<string>("Host") ?? "localhost";
        var user = cfg.GetValue<string>("User") ?? "postgres";
        var db = cfg.GetValue<string>("Database") ?? "postgres";
        var backupDir = cfg.GetValue<string>("BackupDirectory") ?? "./backups";
        var pgDumpPath = cfg.GetValue<string>("PgDumpPath") ?? "pg_dump";
        var compression = cfg.GetValue<int>("CompressionLevel", 9);

        Directory.CreateDirectory(backupDir);

        var fileName = Path.Combine(backupDir, $"db_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dump");

        var args = $"-h {host} -U {user} -d {db} -F c -Z {compression} -f \"{fileName}\"";

        _logger.LogInformation("Starting pg_dump for {Database} to {File}", db, fileName);

        var result = await _runner.RunCommand(pgDumpPath, args, ct);

        if (!result.Success)
        {
            _logger.LogError("pg_dump failed: {Error}", result.Error);
            // Placeholder: integrate LLM/RAG analysis here
            return;
        }

        _logger.LogInformation("pg_dump completed, verifying backup");

        var verify = await _runner.RunCommand("pg_restore", $"--list \"{fileName}\"", ct);
        if (!verify.Success)
        {
            _logger.LogError("Backup verification failed: {Error}", verify.Error);
            return;
        }

        _logger.LogInformation("Backup verified successfully: {File}", fileName);

        // Cleanup old backups
        var retentionDays = cfg.GetValue<int>("RetentionDays", 7);
        CleanupOldBackups(backupDir, retentionDays);
    }

    private void CleanupOldBackups(string backupDir, int retentionDays)
    {
        try
        {
            var files = Directory.GetFiles(backupDir, "*.dump");
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var f in files)
            {
                var info = new FileInfo(f);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                    _logger.LogInformation("Deleted old backup {File}", f);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup old backups");
        }
    }
}
