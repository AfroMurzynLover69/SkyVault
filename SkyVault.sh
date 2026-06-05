#!/usr/bin/env sh
set -eu

cd "$(dirname "$0")"

if [ "${1:-}" = "config" ] || [ "${1:-}" = "--config" ] || [ "${1:-}" = "control" ]; then
  exec ./SkyVault.config.sh
fi

frontend_dir="SkyVault/frontend"
frontend_stamp="$frontend_dir/dist/frontend/browser/index.html"

frontend_needs_build() {
  if [ ! -f "$frontend_stamp" ]; then
    return 0
  fi

  if find \
    "$frontend_dir/src" \
    "$frontend_dir/public" \
    "$frontend_dir/angular.json" \
    "$frontend_dir/package.json" \
    "$frontend_dir/package-lock.json" \
    -type f -newer "$frontend_stamp" -print -quit 2>/dev/null | grep -q .; then
    return 0
  fi

  return 1
}

build_frontend() {
  if [ ! -d "$frontend_dir/node_modules" ]; then
    printf 'Frontend dependencies missing. Running npm ci...\n'
    (cd "$frontend_dir" && npm ci)
  fi

  printf 'Building latest frontend...\n'
  (cd "$frontend_dir" && npm run build)
}

case "${1:-}" in
  frontend|--frontend)
    build_frontend
    exit 0
    ;;
  build|--build)
    build_frontend
    dotnet build SkyVault/SkyVault.csproj --property:RestoreIgnoreFailedSources=true
    exit 0
    ;;
esac

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

if [ "${1:-}" = "--skip-frontend-build" ]; then
  printf 'Skipping frontend build check.\n'
elif frontend_needs_build; then
  build_frontend
else
  printf 'Frontend is up to date.\n'
fi

dotnet run --project SkyVault/SkyVault.csproj --property:RestoreIgnoreFailedSources=true
