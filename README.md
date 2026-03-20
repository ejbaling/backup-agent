# BackupAgent

Minimal .NET 8 CLI backup agent that runs `pg_dump`, verifies the dump with `pg_restore --list`, and rotates old backups.

Quick start

1. Ensure .NET 8 SDK is installed.
2. Install `pg_dump` and `pg_restore` on the machine or ensure the worker container has them.
3. From PowerShell:

```powershell
cd D:\src\backup-agent
dotnet run --project BackupAgent.csproj
```

Configuration

- `appsettings.json` contains `Backup` options such as `IntervalSeconds`, `Host`, `User`, `Database`, and `BackupDirectory`.

Docker worker example

See `docker-compose.yml` for a simple worker that exposes `pg_dump` in a container.
