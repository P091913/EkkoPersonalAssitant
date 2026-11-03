# Build context is the solution root (see compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, from the project file alone, so code changes don't invalidate the package layer.
COPY Ekko.Food/Ekko.Food.csproj Ekko.Food/
RUN dotnet restore Ekko.Food/Ekko.Food.csproj
COPY Ekko.Food/ Ekko.Food/
RUN dotnet publish Ekko.Food/Ekko.Food.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The database folder belongs to the non-root user, so a new Docker volume mounted here does too.
RUN mkdir -p /app/data && chown $APP_UID /app/data
# The image's built-in non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ekko.Food.dll"]
