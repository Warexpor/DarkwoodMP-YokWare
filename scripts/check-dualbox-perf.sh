#!/usr/bin/env bash
# Dual-box FPS diagnosis gate for YokWare Branch.
# Requires both installs to have run a co-op session with LogPreset=Support (default)
# so CoopPerfProbe emitted [Perf] / [PerfCliff] into BepInEx/LogOutput.log.
#
# Exit 0  — both logs have [Perf], no [PerfCliff]
# Exit 1  — cliffs found, or missing probe lines / files
# Exit 2  — usage / path errors
set -euo pipefail

HOST_LOG="${1:-${DWMP_HOST_LOG:-$HOME/.local/share/Steam/steamapps/common/Darkwood/BepInEx/LogOutput.log}}"
CLIENT_LOG="${2:-${DWMP_CLIENT_LOG:-$HOME/Work/MyProjects/SecondDarkwood/Darkwood/BepInEx/LogOutput.log}}"

usage() {
  echo "Usage: $0 [host_LogOutput.log] [client_LogOutput.log]" >&2
  echo "Defaults (override with DWMP_HOST_LOG / DWMP_CLIENT_LOG):" >&2
  echo "  host:   $HOME/.local/share/Steam/steamapps/common/Darkwood/BepInEx/LogOutput.log" >&2
  echo "  client: $HOME/Work/MyProjects/SecondDarkwood/Darkwood/BepInEx/LogOutput.log" >&2
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 2
fi

fail=0

check_one() {
  local role="$1"
  local path="$2"
  if [[ ! -f "$path" ]]; then
    echo "[FAIL] $role log missing: $path"
    fail=1
    return
  fi
  local perf cliffs
  perf=$(grep -c '\[Perf\]' "$path" 2>/dev/null || true)
  cliffs=$(grep -c '\[PerfCliff\]' "$path" 2>/dev/null || true)
  echo "[$role] $path"
  echo "       [Perf] lines=$perf  [PerfCliff] lines=$cliffs"
  if [[ "$perf" -lt 1 ]]; then
    echo "       [FAIL] no [Perf] — was co-op connected with handshake? LogPreset=Support?"
    fail=1
  fi
  if [[ "$cliffs" -gt 0 ]]; then
    echo "       [FAIL] PerfCliff present — paste nearby [Perf]/[PerfSeg] lines for triage"
    grep -n '\[PerfCliff\]' "$path" | tail -n 8 | sed 's/^/       /'
    fail=1
  fi
  if [[ "$perf" -ge 1 && "$cliffs" -eq 0 ]]; then
    echo "       [OK] probe alive, no cliffs in this log"
  fi
}

# Product version from the source of truth, so the banner never goes stale.
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(grep -E 'const string Version *=' "$ROOT/DarkwoodMP.Mod/Bootstrap/PluginInfo.cs" 2>/dev/null \
  | sed -E 's/.*"([^"]+)".*/\1/' || true)"
echo "=== dual-box perf check (${VERSION:-unknown version}) ==="
check_one Host "$HOST_LOG"
check_one Client "$CLIENT_LOG"

if [[ "$fail" -ne 0 ]]; then
  echo "=== RESULT: FAIL — see DarkwoodMP.Mod/docs/LOGGING.md + PLAYTEST.md ==="
  exit 1
fi
echo "=== RESULT: PASS — both logs clean of PerfCliff ==="
exit 0
