#!/bin/bash
# Double-click in Finder to run the full NeuralBridge verification. Logs: verify-logs/
cd "$(dirname "$0")"
export PATH="/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:$HOME/.dotnet:$PATH"
bash scripts/verify-mac.sh
echo; echo "Done. You can close this window."
