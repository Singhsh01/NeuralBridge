# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached layer): only project files and central build props.
COPY global.json Directory.Build.props Directory.Packages.props NeuralBridge.sln ./
COPY src/NeuralBridge.Domain/NeuralBridge.Domain.csproj src/NeuralBridge.Domain/
COPY src/NeuralBridge.Application/NeuralBridge.Application.csproj src/NeuralBridge.Application/
COPY src/NeuralBridge.Infrastructure/NeuralBridge.Infrastructure.csproj src/NeuralBridge.Infrastructure/
COPY src/NeuralBridge.Web/NeuralBridge.Web.csproj src/NeuralBridge.Web/
RUN dotnet restore src/NeuralBridge.Web/NeuralBridge.Web.csproj

COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/NeuralBridge.Web/NeuralBridge.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root user provided by the official image; writable data dir for SQLite + Data Protection keys.
RUN mkdir -p /app/data/keys && chown -R $APP_UID /app/data
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Sqlite="Data Source=/app/data/neuralbridge.db" \
    NeuralBridge__Hosting__DataProtectionKeysPath=/app/data/keys \
    NeuralBridge__Hosting__HttpsRedirection=false \
    DOTNET_gcServer=0

COPY --from=build /app/publish .
EXPOSE 8080
VOLUME ["/app/data"]
# Liveness endpoint for orchestrators / reverse proxies: GET /healthz
ENTRYPOINT ["dotnet", "NeuralBridge.Web.dll"]
