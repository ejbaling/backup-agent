# BackupAgent

Minimal .NET 8 backup agent for PostgreSQL.

What it does

- Runs `pg_dump` on a configured Postgres instance.
- Verifies the dump via `pg_restore --list`.
- Optionally restores the dump to a simulation target to validate restore steps.
- Rotates old backups by retention policy.

Requirements

- .NET 8 runtime for the agent.
- `pg_dump`, `pg_restore`, and `psql` available to the agent (installed on host or in container).
- A running postgresql server for the restore. See Postgrsql folder in this project.

Quick start (local)

1. Configure `appsettings.json` (see `Backup` and `RestoreSimulation` sections).
2. Run:

```powershell
dotnet run --project BackupAgent.csproj
```

Docker (recommended for production-like runs)

1. Build and run the agent image from the repo root:

```bash
docker-compose build backup-agent
docker-compose up -d backup-agent
```

Notes on configuration and secrets

- `appsettings.json` supports `PasswordParameterName` (SSM) — `PasswordInitializer` fetches the Postgres password from AWS SSM and injects it at startup.
- `RestoreSimulation` can be enabled to drop/create a target DB and run `pg_restore` to validate the full restore process. The agent prefers the SSM-fetched password; it will fall back to `RestoreSimulation.Password` if present.

Extensions and compatibility

- If your dump includes extensions (for example `pgvector`), the target Postgres server must have those extensions installed (they are version-specific).

Logs and troubleshooting

- Check application logs for `pg_dump`/`pg_restore` output. Common issues: missing client tools on the agent, network reachability to Postgres, and missing Postgres extensions.

Files

- `appsettings.json`: runtime configuration.
- `docker-compose.yml`: service definition for running the agent in Docker.

That's all—enable `RestoreSimulation` and point it at a disposable Postgres instance to test restore behavior.
