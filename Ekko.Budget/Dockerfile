# Build context is the solution root (see compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, from the project file alone, so code changes don't invalidate the package layer.
COPY Ekko.Budget/Ekko.Budget.csproj Ekko.Budget/
RUN dotnet restore Ekko.Budget/Ekko.Budget.csproj
COPY Ekko.Budget/ Ekko.Budget/
RUN dotnet publish Ekko.Budget/Ekko.Budget.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The image's built-in non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ekko.Budget.dll"]
