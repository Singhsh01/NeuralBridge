# Generates the initial EF Core migration (SQLite) into the Infrastructure project.
# Requires: dotnet tool install --global dotnet-ef
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
dotnet ef migrations add InitialCreate `
  --project src/NeuralBridge.Infrastructure `
  --startup-project src/NeuralBridge.Web `
  --output-dir Persistence/EntityFramework/Migrations
Write-Host "Done. The app applies migrations automatically at startup."
