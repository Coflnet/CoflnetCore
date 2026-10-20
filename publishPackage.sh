#!/usr/bin/env bash
# Packs Coflnet.Core locally into ./out. This script never publishes.
# Releases are published by the Package workflow (.github/workflows/package.yml) on merge to main
# after bumping <Version> in Coflnet.Core.csproj, using NuGet trusted publishing.
set -euo pipefail
cd "$(dirname "$0")"
dotnet pack Coflnet.Core.csproj -c Release -o out
echo "Packed to ./out only. Releases are published by the Package workflow on merge to main after bumping <Version> in Coflnet.Core.csproj."
