# Build context is the solution root (see compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, from the project file alone, so code changes don't invalidate the package layer.
COPY Ekko.Telegram/Ekko.Telegram.csproj Ekko.Telegram/
RUN dotnet restore Ekko.Telegram/Ekko.Telegram.csproj
COPY Ekko.Telegram/ Ekko.Telegram/
RUN dotnet publish Ekko.Telegram/Ekko.Telegram.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The image's built-in non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ekko.Telegram.dll"]
