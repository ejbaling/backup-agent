Postgresql (pgvector-enabled)

Purpose

- Provides a PostgreSQL container image with the `pgvector` extension installed, allowing restores that include `pgvector` to succeed.

Quick start

1. From this folder run:

```bash
docker-compose up -d
```

Notes

- Ensure the Postgres major version matches your dump (pgvector is version-specific).
- Install Docker before running the command.

That's all — running `docker-compose up -d` should start a pgvector-enabled Postgres instance.
