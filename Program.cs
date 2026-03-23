using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using BackupAgent.Data;
using BackupAgent.Services;
using BackupAgent.Utilities;
using Microsoft.EntityFrameworkCore;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
    })
    .ConfigureServices((ctx, services) =>
    {
        // Ensure password initializer runs before other hosted services
        services.AddSingleton<PostgresConnectionProvider>();
        services.AddSingleton<PasswordInitializer>();
        // PasswordInitializer is called manually before host.RunAsync() to
        // guarantee the password is set before DbContextOptions are resolved.

        services.AddHostedService<BackupService>();
        services.AddSingleton<CommandRunner>();

        // RAG / Ollama services
        // Register as a typed HTTP client so DI provides HttpClient, IConfiguration, and ILogger<OllamaClient>
        services.AddHttpClient<OllamaClient>();
        services.AddSingleton<VectorStore>();
        services.AddSingleton<RagAnalyzer>();

        // Postgres DbContext (password injected at runtime from SSM via PostgresConnectionProvider)
        services.AddDbContextFactory<AppDbContext>((sp, opts) =>
        {
            var connProvider = sp.GetRequiredService<PostgresConnectionProvider>();
            opts.UseNpgsql(connProvider.GetConnectionString(),
                o => o.UseVector());
        });
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .Build();

// Run PasswordInitializer before the host starts so the password is set
// before EF Core resolves DbContextOptions<AppDbContext> (a singleton).
var passwordInitializer = host.Services.GetRequiredService<PasswordInitializer>();
await passwordInitializer.StartAsync(CancellationToken.None);

await host.RunAsync();
