using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using BackupAgent.Data;
namespace BackupAgent.Services;

public class PasswordInitializer : IHostedService
{
    private readonly IConfiguration _config;
    private readonly PostgresConnectionProvider _provider;
    private readonly ILogger<PasswordInitializer> _logger;

    public PasswordInitializer(IConfiguration config, PostgresConnectionProvider provider, ILogger<PasswordInitializer> logger)
    {
        _config = config;
        _provider = provider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var paramName = _config.GetSection("Backup").GetValue<string>("PasswordParameterName");
        if (string.IsNullOrEmpty(paramName))
        {
            _logger.LogInformation("No SSM parameter configured for Postgres password; skipping SSM fetch.");
            return;
        }

        try
        {
            using var ssm = new AmazonSimpleSystemsManagementClient();
            var resp = await ssm.GetParameterAsync(new GetParameterRequest { Name = paramName, WithDecryption = true }, cancellationToken);
            var pw = resp.Parameter?.Value;
            if (!string.IsNullOrEmpty(pw))
            {
                _provider.SetPassword(pw);
                _logger.LogInformation("PasswordInitializer: retrieved and set Postgres password from SSM parameter {Name}", paramName);
            }
            else
            {
                _logger.LogWarning("PasswordInitializer: SSM returned empty password for {Name}", paramName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PasswordInitializer failed to retrieve password from SSM parameter {Name}", paramName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
