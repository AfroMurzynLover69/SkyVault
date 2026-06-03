#!/usr/bin/env sh
set -eu

cd "$(dirname "$0")"

if [ "${1:-}" = "config" ] || [ "${1:-}" = "--config" ] || [ "${1:-}" = "control" ]; then
  exec ./SkyVault.config.sh
fi

if [ -f .env ]; then
  set -a
  . ./.env
  set +a
fi

if [ "${SKYVAULT_CPU_CORES:-}" ]; then
  DOTNET_PROCESSOR_COUNT="$SKYVAULT_CPU_CORES"
  export DOTNET_PROCESSOR_COUNT
fi

if [ "${SKYVAULT_MAX_RAM_MB:-}" ]; then
  ram_bytes=$((SKYVAULT_MAX_RAM_MB * 1024 * 1024))
  DOTNET_GCHeapHardLimit="$(printf '%x' "$ram_bytes")"
  export DOTNET_GCHeapHardLimit
fi

dotnet run --project SkyVault/SkyVault.csproj --property:RestoreIgnoreFailedSources=true
