FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj and restore first for caching
COPY BackupAgent.csproj ./
COPY ./ .
RUN dotnet restore BackupAgent.csproj

RUN dotnet publish BackupAgent.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Install PostgreSQL client tools (psql, pg_dump, pg_restore).
# Use a build-arg `POSTGRES_VERSION` to install the matching major client (e.g. 17).
ARG POSTGRES_VERSION=17
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
	 ca-certificates \
	 wget \
	 gnupg \
	 lsb-release \
 && wget -qO - https://www.postgresql.org/media/keys/ACCC4CF8.asc | apt-key add - \
 && echo "deb http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client-${POSTGRES_VERSION} \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

VOLUME ["/backups"]

ENTRYPOINT ["dotnet", "BackupAgent.dll"]
