#!/usr/bin/env bash
# cleanup.sh — Post 45: remove local junk before git push + stop Docker resources
# Usage: bash cleanup.sh   (Git Bash / WSL on Windows)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

echo "========================================================"
echo " Post 45 cleanup — artifacts, secrets, Docker"
echo "========================================================"
echo "Root: $ROOT"
echo ""

# ---------------------------------------------------------------------------
# 1. Remove build / IDE / temp folders (not useful for git push)
# ---------------------------------------------------------------------------
echo "==> Removing bin/, obj/, TestResults/, .vs/, .aspire/, logs..."
REMOVED=0
while IFS= read -r -d '' dir; do
  if rm -rf "$dir" 2>/dev/null; then
    echo "  removed: $dir"
    REMOVED=$((REMOVED + 1))
  else
    echo "  skip (locked): $dir"
  fi
done < <(find "$ROOT" \( -type d \( -name bin -o -name obj -o -name TestResults -o -name test-results -o -name .vs -o -name .aspire \) \) -print0 2>/dev/null || true)

while IFS= read -r -d '' f; do
  if rm -f "$f" 2>/dev/null; then
    echo "  removed: $f"
    REMOVED=$((REMOVED + 1))
  else
    echo "  skip (locked): $f"
  fi
done < <(find "$ROOT" -type f \( -name '*.log' -o -name '*.user' -o -name '*.suo' -o -name 'Thumbs.db' -o -name '.DS_Store' -o -name '*.zip' \) -print0 2>/dev/null || true)

# Capstone load-test report folders (generated)
for d in "$ROOT/docs/capstone-01-baseline" "$ROOT/docs/capstone-01-burst"; do
  if [[ -d "$d" ]]; then
    rm -rf "$d"
    echo "  removed: $d"
    REMOVED=$((REMOVED + 1))
  fi
done

# Compiled Bicep ARM (regenerate from .bicep)
if [[ -f "$ROOT/infra/main.json" ]]; then
  rm -f "$ROOT/infra/main.json"
  echo "  removed: infra/main.json"
  REMOVED=$((REMOVED + 1))
fi

# Aspire publish / local temp
for d in "$ROOT/.aspire" "$ROOT/artifacts" "$ROOT/TestResults"; do
  if [[ -d "$d" ]]; then
    rm -rf "$d"
    echo "  removed: $d"
    REMOVED=$((REMOVED + 1))
  fi
done

echo "  artifact folders/files cleaned (count ops: $REMOVED)"
echo ""

# ---------------------------------------------------------------------------
# 2. Remove secret / credential files if present (never push)
# ---------------------------------------------------------------------------
echo "==> Scanning for local secret files..."
SECRET_REMOVED=0
for name in .env .env.local .env.development .env.production credentials.json secrets.json; do
  while IFS= read -r -d '' f; do
    base="$(basename "$f")"
    if [[ "$base" == ".env.example" ]]; then
      continue
    fi
    rm -f "$f"
    echo "  removed secret file: $f"
    SECRET_REMOVED=$((SECRET_REMOVED + 1))
  done < <(find "$ROOT" -type f -name "$name" -print0 2>/dev/null)
done

while IFS= read -r -d '' f; do
  rm -f "$f"
  echo "  removed secret file: $f"
  SECRET_REMOVED=$((SECRET_REMOVED + 1))
done < <(find "$ROOT" -type f \( -name '*.pem' -o -name '*.pfx' -o -name 'appsettings.*.local.json' \) -print0 2>/dev/null)

if [[ "$SECRET_REMOVED" -eq 0 ]]; then
  echo "  no .env / credentials / pem / pfx files found"
fi
echo ""

# Soft scan: warn on high-entropy looking API key patterns in source
echo "==> Scanning source for accidental API key patterns..."
if command -v grep >/dev/null 2>&1; then
  if grep -RInE --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
      --exclude='cleanup.sh' \
      '(sk-[a-zA-Z0-9]{20,}|AIza[0-9A-Za-z_-]{30,}|ghp_[a-zA-Z0-9]{36}|xox[baprs]-)' \
      "$ROOT" 2>/dev/null; then
    echo "  WARNING: possible secret patterns found above — review before push"
  else
    echo "  no high-risk API key patterns found"
  fi
fi
echo "  note: local Jwt SigningKey + SQL_SA_PASSWORD placeholders are intentional for emulator demos"
echo ""

# ---------------------------------------------------------------------------
# 3. Stop local .NET service processes that lock DLLs
# ---------------------------------------------------------------------------
echo "==> Stopping local service processes (if running)..."
if command -v taskkill >/dev/null 2>&1; then
  for proc in UserService OrderService OrderProcessor OrderEventProcessor AppHost Aspire.Dashboard dcpctrl dcp; do
    taskkill //F //IM "${proc}.exe" >/dev/null 2>&1 || true
  done
  echo "  attempted taskkill for AppHost / services / Aspire.Dashboard"
elif command -v pkill >/dev/null 2>&1; then
  pkill -f 'AppHost|UserService|OrderService|Aspire.Dashboard' 2>/dev/null || true
  echo "  attempted pkill for AppHost / services"
else
  echo "  skip process stop (no taskkill/pkill)"
fi
echo ""

# ---------------------------------------------------------------------------
# 4. Stop & remove project-related Docker containers
# ---------------------------------------------------------------------------
echo "==> Stopping / removing project Docker containers..."
if ! command -v docker >/dev/null 2>&1; then
  echo "  docker not found — skip container cleanup"
else
  if ! docker info >/dev/null 2>&1; then
    echo "  docker daemon not running — skip container cleanup"
  else
    if [[ -f "$ROOT/docker-compose.yml" ]]; then
      docker compose -f "$ROOT/docker-compose.yml" down --remove-orphans 2>/dev/null || \
      docker-compose -f "$ROOT/docker-compose.yml" down --remove-orphans 2>/dev/null || true
      echo "  docker compose down done"
    fi

    # Match Aspire-generated + compose names for this stack
    PATTERNS='otel-collector|azurite|sqledge|servicebus-emulator|eventhubs-emulator|user-service|order-service|order-processor|order-event|aspire|dapr'

    MATCHED="$(docker ps -a --format '{{.Names}}' 2>/dev/null | grep -Ei "$PATTERNS" || true)"
    if [[ -z "$MATCHED" ]]; then
      echo "  no matching project containers"
    else
      echo "$MATCHED" | while read -r name; do
        [[ -z "$name" ]] && continue
        docker rm -f "$name" >/dev/null 2>&1 && echo "  removed container: $name" || true
      done
    fi

    # ---------------------------------------------------------------------------
    # 5. Prune unused Docker resources
    # ---------------------------------------------------------------------------
    echo ""
    echo "==> Pruning unused Docker resources..."
    docker container prune -f >/dev/null 2>&1 || true
    echo "  container prune done"
    docker network prune -f >/dev/null 2>&1 || true
    echo "  network prune done"
    docker image prune -f >/dev/null 2>&1 || true
    echo "  dangling image prune done"
    docker volume prune -f >/dev/null 2>&1 || true
    echo "  unused volume prune done"
    docker image prune -a -f >/dev/null 2>&1 || true
    echo "  unused image prune (-a) done"
  fi
fi

echo ""
echo "========================================================"
echo " Cleanup complete"
echo "========================================================"
echo "Next:"
echo "  git status"
echo "  docker ps"
echo "  dotnet run --project src/AppHost --launch-profile http"
echo ""
