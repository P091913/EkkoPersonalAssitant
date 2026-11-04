# Build context is the solution root (see compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, from the project file alone, so code changes don't invalidate the package layer.
COPY Ekko.Files/Ekko.Files.csproj Ekko.Files/
RUN dotnet restore Ekko.Files/Ekko.Files.csproj
COPY Ekko.Files/ Ekko.Files/
RUN dotnet publish Ekko.Files/Ekko.Files.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The data folder belongs to the non-root user, so a new Docker volume mounted here does too.
RUN mkdir -p /app/data && chown $APP_UID /app/data
# The image's built-in non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ekko.Files.dll"]
