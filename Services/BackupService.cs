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
                var contextInfo = $"Command: {dumpCommand}\nStdout: {result.Output}";
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

        // Optionally restore to a simulation target to validate the full backup/restore cycle
        var simSection = cfg.GetSection("RestoreSimulation");
        if (simSection.GetValue<bool>("Enabled", false))
        {
            await RunRestoreSimulation(fileName, pgRestorePath, simSection, ct);
        }

        // Cleanup old backups
        var retentionDays = cfg.GetValue<int>("RetentionDays", 7);
        CleanupOldBackups(backupDir, retentionDays);
    }

    private async Task RunRestoreSimulation(string backupFile, string pgRestorePath, IConfigurationSection sim, CancellationToken ct)
    {
        var host = sim.GetValue<string>("Host") ?? "localhost";
        var port = sim.GetValue<int>("Port", 5432);
        var user = sim.GetValue<string>("User") ?? "postgres";
        var database = sim.GetValue<string>("Database") ?? "restore_db";

        _logger.LogInformation("Starting restore simulation: {File} -> {Host}:{Port}/{Database}", backupFile, host, port, database);

        IDictionary<string, string>? env = null;
        if (_pgConnProvider.TryGetPassword(out var providerPwd) && !string.IsNullOrEmpty(providerPwd))
        {
            env = new Dictionary<string, string> { { "PGPASSWORD", providerPwd } };
        }

        // Drop and recreate the target database so each simulation starts clean.
        // Uses psql via the pg_restore directory (assumes psql lives beside pg_restore).
        var psqlPath = Path.Combine(Path.GetDirectoryName(pgRestorePath) ?? string.Empty, "psql" + Path.GetExtension(pgRestorePath));
        if (!File.Exists(psqlPath))
            psqlPath = "psql"; // fall back to PATH

        var dropArgs = $"-h {host} -p {port} -U {user} -d postgres -c \"DROP DATABASE IF EXISTS \\\"{database}\\\";\"";
        var createArgs = $"-h {host} -p {port} -U {user} -d postgres -c \"CREATE DATABASE \\\"{database}\\\";\"";

        _logger.LogInformation("Dropping existing restore-target database '{Database}'", database);
        var dropResult = await _runner.RunCommand(psqlPath, dropArgs, env, ct);
        if (!dropResult.Success)
            _logger.LogWarning("DROP DATABASE warning (may be harmless): {Error}", dropResult.Error);

        _logger.LogInformation("Creating restore-target database '{Database}'", database);
        var createResult = await _runner.RunCommand(psqlPath, createArgs, env, ct);
        if (!createResult.Success)
        {
            _logger.LogError("Failed to create restore-target database: {Error}", createResult.Error);
            return;
        }

        // Run the actual pg_restore into the target database
        var restoreArgs = $"-h {host} -p {port} -U {user} -d \"{database}\" -F c --no-owner --role={user} \"{backupFile}\"";
        string restoreCommand = pgRestorePath;
        if (restoreCommand.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || restoreCommand.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            restoreArgs = $"/c \"\"{restoreCommand}\" {restoreArgs}\"";
            restoreCommand = "cmd.exe";
        }

        _logger.LogInformation("Executing restore simulation: {Cmd} {Args}", restoreCommand, restoreArgs);
        var restoreResult = await _runner.RunCommand(restoreCommand, restoreArgs, env, ct);

        if (!restoreResult.Success)
        {
            _logger.LogError("Restore simulation failed: {Error}", restoreResult.Error);
            try
            {
                var contextInfo = $"Command: {restoreCommand}\nStdout: {restoreResult.Output}";
                var analysis = await _analyzer.AnalyzeFailureAsync(restoreResult.Error, contextInfo, ct);
                _logger.LogError("Restore simulation analysis: {Analysis}", analysis);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze restore simulation failure");
            }
            return;
        }

        _logger.LogInformation("Restore simulation completed successfully for {File}", backupFile);
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
