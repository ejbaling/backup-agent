using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BackupAgent.Data;

/// <summary>
/// Holds the Postgres connection string and allows the SSM-fetched password
/// to be injected at runtime before the first DbContext is created.
/// </summary>
public class PostgresConnectionProvider
{
    private readonly string _baseConnectionString;
    private string? _password;

    public PostgresConnectionProvider(IConfiguration config)
    {
        _baseConnectionString = config.GetConnectionString("Postgres")
            ?? "Host=10.0.0.101;Port=5432;Database=redwoodiloilo;Username=admin";
    }

    public void SetPassword(string password) => _password = password;

    public string GetConnectionString()
    {
        if (string.IsNullOrEmpty(_password))
            throw new InvalidOperationException("Postgres password not set. Call SetPassword() before creating a DbContext.");

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            Password = _password
        };

        return builder.ToString();
    }

    public bool TryGetPassword(out string? password)
    {
        password = _password;
        return !string.IsNullOrEmpty(password);
    }
}
