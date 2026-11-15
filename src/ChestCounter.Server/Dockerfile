# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ChestCounter.Shared/ src/ChestCounter.Shared/
COPY src/ChestCounter.Server/ src/ChestCounter.Server/
RUN dotnet publish src/ChestCounter.Server/ChestCounter.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The database and the generated auth secret live in /data: mount a volume there.
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
ENV CC_DATABASE=/data/squads.db \
    CC_LISTEN=http://0.0.0.0:8080
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "ChestCounter.Server.dll"]
