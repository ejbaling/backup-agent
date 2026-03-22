using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        services.AddHostedService(sp => sp.GetRequiredService<PasswordInitializer>());

        services.AddHostedService<BackupService>();
        services.AddSingleton<CommandRunner>();

        // RAG / Ollama services
        services.AddSingleton<OllamaClient>(sp => new OllamaClient(new System.Net.Http.HttpClient(), sp.GetRequiredService<IConfiguration>()));
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

await host.RunAsync();
