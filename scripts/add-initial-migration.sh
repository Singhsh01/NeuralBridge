#!/usr/bin/env bash
# Generates the initial EF Core migration (SQLite) into the Infrastructure project.
# Requires: dotnet tool install --global dotnet-ef
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet ef migrations add InitialCreate \
  --project src/NeuralBridge.Infrastructure \
  --startup-project src/NeuralBridge.Web \
  --output-dir Persistence/EntityFramework/Migrations
echo "Done. The app applies migrations automatically at startup (NeuralBridge:Persistence:InitializeDatabaseOnStartup)."
