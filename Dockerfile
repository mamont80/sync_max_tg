# syntax=docker/dockerfile:1

# ---------- сборка ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только csproj: слой restore переиспользуется, пока не менялись зависимости.
COPY src/SyncMax/SyncMax.csproj src/SyncMax/
RUN dotnet restore src/SyncMax/SyncMax.csproj

COPY src/ src/
RUN dotnet publish src/SyncMax/SyncMax.csproj -c Release -o /app/publish --no-restore

# ---------- запуск ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ffmpeg — конвертация аудио (Media:FfmpegPath = "ffmpeg", ищется в PATH).
# curl   — для HEALTHCHECK (в базовом образе его нет).
# tzdata — чтобы работала переменная TZ: логи ротируются по локальной дате.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg curl tzdata \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

# /data — файл SQLite, /app/logs — файлы логов (FileLoggerProvider пишет рядом с бинарником).
# Обе директории монтируются томами, см. docker-compose.yml.
RUN mkdir -p /data /app/logs && chown -R app:app /data /app/logs
USER app

# Значения по умолчанию для контейнера. Перекрываются переменными из .env / environment:
# любая настройка appsettings.json доступна как переменная окружения через разделитель "__".
ENV Database__ConnectionString="Data Source=/data/syncmax.db" \
    HttpServer__ListenUrl="http://0.0.0.0:8443"

EXPOSE 8443

# /test отвечает всегда, независимо от режимов ботов, — годится как проба живости.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8443/test || exit 1

ENTRYPOINT ["dotnet", "SyncMax.dll"]
