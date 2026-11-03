# Build context is the solution root (see compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, from the project file alone, so code changes don't invalidate the package layer.
COPY Ekko.Glasses/Ekko.Glasses.csproj Ekko.Glasses/
RUN dotnet restore Ekko.Glasses/Ekko.Glasses.csproj
COPY Ekko.Glasses/ Ekko.Glasses/
RUN dotnet publish Ekko.Glasses/Ekko.Glasses.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The image's built-in non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ekko.Glasses.dll"]
