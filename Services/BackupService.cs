using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using BackupAgent.Data;

namespace BackupAgent.Services;

public class BackupService : BackgroundService
{
    private readonly ILogger<BackupService> _logger;
    private readonly IConfiguration _config;
    private readonly Utilities.CommandRunner _runner;
    private readonly RagAnalyzer _analyzer;
    private readonly PostgresConnectionProvider _pgConnProvider;

    public BackupService(ILogger<BackupService> logger, IConfiguration config, Utilities.CommandRunner runner, RagAnalyzer analyzer, PostgresConnectionProvider pgConnProvider)
    {
        _logger = logger;
        _config = config;
        _runner = runner;
        _analyzer = analyzer;
        _pgConnProvider = pgConnProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupAgent started.");

        // Password is initialized by PasswordInitializer at startup; no need to fetch here.

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
        _logger.LogInformation("Backup config values: PgDumpPath={PgDumpPath}, PgRestorePath={PgRestorePath}, BackupDirectory={BackupDirectory}", cfg["PgDumpPath"], cfg["PgRestorePath"], cfg["BackupDirectory"]);
        var host = cfg.GetValue<string>("Host") ?? "localhost";
        var user = cfg.GetValue<string>("User") ?? "postgres";
        var db = cfg.GetValue<string>("Database") ?? "postgres";
        var useLocalBackupDir = cfg.GetValue<bool>("UseLocalBackupDirectory", false);
        var backupDir = useLocalBackupDir ? cfg.GetValue<string>("LocalBackupDirectory") ?? "./backups" : cfg.GetValue<string>("BackupDirectory") ?? "./backups";
        var pgDumpPath = cfg.GetValue<string>("PgDumpPath") ?? "pg_dump";
        var pgRestorePath = cfg.GetValue<string>("PgRestorePath") ?? "pg_restore";
        var compression = cfg.GetValue<int>("CompressionLevel", 9);

        Directory.CreateDirectory(backupDir);

        var fileName = Path.Combine(backupDir, $"db_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dump");

        var args = $"-h {host} -U {user} -d {db} -F c -Z {compression} -f \"{fileName}\"";

        _logger.LogInformation("Using pg_dump path: {PgDumpPath}", pgDumpPath);
        _logger.LogInformation("Starting pg_dump for {Database} to {File}", db, fileName);

        // Inject PGPASSWORD into child process env if provider has the password
        IDictionary<string, string>? env = null;
        if (_pgConnProvider.TryGetPassword(out var pwd) && !string.IsNullOrEmpty(pwd))
        {
            env = new Dictionary<string, string> { { "PGPASSWORD", pwd } };
        }

        // If the configured path is a batch file on Windows, run via cmd.exe /c
        string dumpCommand = pgDumpPath;
        string dumpArgs = args;
        if (dumpCommand.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || dumpCommand.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Wrap the entire command passed to cmd.exe so quoting is preserved
            // Resulting args: /c "\"path\" <args>"
            dumpArgs = $"/c \"\"{dumpCommand}\" {args}\"";
            dumpCommand = "cmd.exe";
        }

        _logger.LogInformation("Executing command: {Cmd} {Args}", dumpCommand, dumpArgs);

        var result = await _runner.RunCommand(dumpCommand, dumpArgs, env, ct);

        if (!result.Success)
        {
            _logger.LogError("pg_dump failed: {Error}", result.Error);
            try
            {
                var contextInfo = $"Command: {dumpCommand} {dumpArgs}\nFile: {fileName}\nStdout: {result.Output}";
                var analysis = await _analyzer.AnalyzeFailureAsync(result.Error, contextInfo, ct);
                _logger.LogError("pg_dump analysis: {Analysis}", analysis);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze pg_dump failure");
            }
            return;
        }

        _logger.LogInformation("pg_dump completed, verifying backup");

        _logger.LogInformation("Using pg_restore path: {PgRestorePath}", pgRestorePath);

        string restoreCommand = pgRestorePath;
        string restoreArgs = $"--list \"{fileName}\"";
        if (restoreCommand.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || restoreCommand.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            restoreArgs = $"/c \"\"{restoreCommand}\" {restoreArgs}\"";
            restoreCommand = "cmd.exe";
        }

        _logger.LogInformation("Executing command: {Cmd} {Args}", restoreCommand, restoreArgs);

        var verify = await _runner.RunCommand(restoreCommand, restoreArgs, env, ct);
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
            _logger.LogInformation("Running backup cleanup: BackupDirectory={BackupDir}, RetentionDays={RetentionDays}", backupDir, retentionDays);
            var files = Directory.GetFiles(backupDir, "*.dump");
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            int deletedCount = 0;
            foreach (var f in files)
            {
                var info = new FileInfo(f);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                    deletedCount++;
                    _logger.LogInformation("Deleted old backup {File}", f);
                }
            }
            _logger.LogInformation("Backup cleanup completed: deleted {DeletedCount} files older than {RetentionDays} days from {BackupDir}", deletedCount, retentionDays, backupDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup old backups");
        }
    }

    
}
