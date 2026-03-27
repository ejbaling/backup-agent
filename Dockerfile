FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj and restore first for caching
COPY BackupAgent.csproj ./
COPY ./ .
RUN dotnet restore BackupAgent.csproj

RUN dotnet publish BackupAgent.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ENV DOTNET_RUNNING_IN_CONTAINER=true

# install PostgreSQL client tools (psql, pg_dump, pg_restore)
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

VOLUME ["/backups"]

ENTRYPOINT ["dotnet", "BackupAgent.dll"]
