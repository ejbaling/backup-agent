using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace BackupAgent.Services;

public class BackupService : BackgroundService
{
    private readonly ILogger<BackupService> _logger;
    private readonly IConfiguration _config;
    private readonly Utilities.CommandRunner _runner;
    private string? _password;

    public BackupService(ILogger<BackupService> logger, IConfiguration config, Utilities.CommandRunner runner)
    {
        _logger = logger;
        _config = config;
        _runner = runner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupAgent started.");

        // Fetch Parameter Store password once at startup (if configured)
        try
        {
            var cfgRoot = _config.GetSection("Backup");
            var passwordParameter = cfgRoot.GetValue<string>("PasswordParameterName");
            if (!string.IsNullOrEmpty(passwordParameter))
            {
                try
                {
                    using var ssm = new AmazonSimpleSystemsManagementClient();
                    var resp = await ssm.GetParameterAsync(new GetParameterRequest
                    {
                        Name = passwordParameter,
                        WithDecryption = true
                    }, stoppingToken);

                    _password = resp.Parameter?.Value;
                    if (!string.IsNullOrEmpty(_password))
                    {
                        _logger.LogInformation("Retrieved DB password from Parameter Store (will be injected into child process environment)");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to retrieve password from Parameter Store: {Name}", passwordParameter);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error while attempting to read password parameter at startup");
        }

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
        var backupDir = cfg.GetValue<string>("BackupDirectory") ?? "./backups";
        var pgDumpPath = cfg.GetValue<string>("PgDumpPath") ?? "pg_dump";
        var pgRestorePath = cfg.GetValue<string>("PgRestorePath") ?? "pg_restore";
        var compression = cfg.GetValue<int>("CompressionLevel", 9);

        Directory.CreateDirectory(backupDir);

        var fileName = Path.Combine(backupDir, $"db_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dump");

        var args = $"-h {host} -U {user} -d {db} -F c -Z {compression} -f \"{fileName}\"";

        _logger.LogInformation("Using pg_dump path: {PgDumpPath}", pgDumpPath);
        _logger.LogInformation("Starting pg_dump for {Database} to {File}", db, fileName);

        // If a password was retrieved at startup, inject it into child process env
        IDictionary<string, string>? env = null;
        if (!string.IsNullOrEmpty(_password))
        {
            env = new Dictionary<string, string> { { "PGPASSWORD", _password } };
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
            // Placeholder: integrate LLM/RAG analysis here
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
