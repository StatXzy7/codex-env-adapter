#!/usr/bin/env bash
# Launch ChatGPT / Codex with TZ matching the current egress node.
# macOS + Linux. Does not change the system timezone.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: launch-codex.sh [options]

  --show              Probe egress IP / timezone, do not launch
  --timezone ZONE     Override IANA timezone, e.g. America/Los_Angeles
  --cli               Launch `codex` CLI instead of the desktop app
  --keep-running      Fail if ChatGPT/Codex is already running
  --no-restart        Same as --keep-running
  -h, --help          Show this help

EOF
}

SHOW=0
CLI=0
KEEP_RUNNING=0
TZ_OVERRIDE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --show) SHOW=1; shift ;;
    --cli) CLI=1; shift ;;
    --keep-running|--no-restart) KEEP_RUNNING=1; shift ;;
    --timezone)
      TZ_OVERRIDE="${2:-}"
      if [[ -z "$TZ_OVERRIDE" ]]; then
        echo "error: --timezone needs an IANA name" >&2
        exit 2
      fi
      shift 2
      ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown option $1" >&2; usage; exit 2 ;;
  esac
done

os_name="$(uname -s)"

json_get() {
  python3 -c 'import json,sys
key=sys.argv[1]
data=json.load(sys.stdin)
val=data.get(key)
if val is None:
    sys.exit(1)
print(val)
' "$1"
}

probe_node() {
  local url body
  for url in "https://ipinfo.io/json" "https://ipapi.co/json/"; do
    if body="$(curl -fsS --max-time 12 -A "CodexEnvAdapter/1.0" "$url" 2>/dev/null)"; then
      NODE_TZ="$(printf '%s' "$body" | json_get timezone 2>/dev/null || true)"
      if [[ -n "${NODE_TZ:-}" ]]; then
        NODE_IP="$(printf '%s' "$body" | json_get ip 2>/dev/null || true)"
        NODE_CITY="$(printf '%s' "$body" | json_get city 2>/dev/null || true)"
        NODE_REGION="$(printf '%s' "$body" | json_get region 2>/dev/null || true)"
        NODE_COUNTRY="$(printf '%s' "$body" | json_get country 2>/dev/null || true)"
        if [[ -z "$NODE_COUNTRY" ]]; then
          NODE_COUNTRY="$(printf '%s' "$body" | json_get country_code 2>/dev/null || true)"
        fi
        NODE_ORG="$(printf '%s' "$body" | json_get org 2>/dev/null || true)"
        NODE_SOURCE="$url"
        return 0
      fi
    fi
  done
  echo "error: could not detect egress timezone (tried ipinfo.io and ipapi.co)" >&2
  exit 1
}

find_macos_app() {
  local app macos_dir bin
  for app in \
    "/Applications/ChatGPT.app" \
    "$HOME/Applications/ChatGPT.app" \
    "/Applications/Codex.app" \
    "$HOME/Applications/Codex.app"
  do
        macos_dir="$app/Contents/MacOS"
        [[ -d "$macos_dir" ]] || continue
        if [[ -x "$macos_dir/ChatGPT" ]]; then
          DESKTOP_BIN="$macos_dir/ChatGPT"
          DESKTOP_APP="$app"
          return 0
        fi
        bin="$(find "$macos_dir" -maxdepth 1 -type f -perm -111 2>/dev/null | head -n 1)"
    if [[ -z "$bin" ]]; then
      bin="$(find "$macos_dir" -maxdepth 1 -type f | head -n 1)"
    fi
    if [[ -n "$bin" && -f "$bin" ]]; then
      DESKTOP_BIN="$bin"
      DESKTOP_APP="$app"
      return 0
    fi
  done
  return 1
}

find_linux_desktop() {
  local candidate
  for candidate in \
    "$(command -v chatgpt 2>/dev/null || true)" \
    "$(command -v ChatGPT 2>/dev/null || true)" \
    "$HOME/.local/bin/chatgpt"
  do
    if [[ -n "$candidate" && -x "$candidate" ]]; then
      DESKTOP_BIN="$candidate"
      DESKTOP_APP="$candidate"
      return 0
    fi
  done
  return 1
}

chatgpt_running() {
  if [[ "$os_name" == "Darwin" ]]; then
    pgrep -x ChatGPT >/dev/null 2>&1 || pgrep -f "/Contents/MacOS/ChatGPT" >/dev/null 2>&1
  else
    pgrep -x chatgpt >/dev/null 2>&1 || pgrep -x ChatGPT >/dev/null 2>&1
  fi
}

stop_chatgpt() {
  if [[ "$os_name" == "Darwin" ]]; then
    osascript -e 'tell application "ChatGPT" to quit' >/dev/null 2>&1 || true
    osascript -e 'tell application "Codex" to quit' >/dev/null 2>&1 || true
  fi
  pkill -x ChatGPT >/dev/null 2>&1 || true
  pkill -x chatgpt >/dev/null 2>&1 || true
  local i
  for i in $(seq 1 25); do
    chatgpt_running || return 0
    sleep 0.2
  done
  echo "error: ChatGPT is still running. Quit it from the menu bar / tray and retry." >&2
  exit 1
}

save_settings() {
  local dir
  if [[ "$os_name" == "Darwin" ]]; then
    dir="$HOME/Library/Application Support/CodexEnvAdapter"
  else
    dir="${XDG_CONFIG_HOME:-$HOME/.config}/codex-env-adapter"
  fi
  mkdir -p "$dir"
  python3 - "$dir/settings.json" <<'PY'
import json, os, sys, datetime
path = sys.argv[1]
data = {
  "timezone": os.environ.get("OUT_TZ", ""),
  "publicIp": os.environ.get("NODE_IP", ""),
  "city": os.environ.get("NODE_CITY", ""),
  "region": os.environ.get("NODE_REGION", ""),
  "country": os.environ.get("NODE_COUNTRY", ""),
  "org": os.environ.get("NODE_ORG", ""),
  "source": os.environ.get("NODE_SOURCE", ""),
  "systemTz": os.environ.get("SYS_TZ", ""),
  "updatedAt": datetime.datetime.now().replace(microsecond=0).isoformat(),
}
with open(path, "w", encoding="utf-8") as fh:
    json.dump(data, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
print(path)
PY
}

echo "Probing egress node..."
probe_node
SYS_TZ="$(python3 -c 'import time; print(time.tzname[0])' 2>/dev/null || date +%Z)"
OUT_TZ="${TZ_OVERRIDE:-$NODE_TZ}"

echo
echo "system tz     : $SYS_TZ  (not changed)"
echo "egress IP     : ${NODE_IP:-unknown}"
echo "location      : ${NODE_CITY:-?} ${NODE_REGION:-} ${NODE_COUNTRY:-}"
echo "node tz       : ${NODE_TZ:-unknown}"
echo "inject TZ     : $OUT_TZ"
echo "source        : ${NODE_SOURCE:-}"
echo

export OUT_TZ NODE_IP NODE_CITY NODE_REGION NODE_COUNTRY NODE_ORG NODE_SOURCE SYS_TZ
SETTINGS_PATH="$(save_settings)"
echo "settings      : $SETTINGS_PATH"
echo

if [[ "$SHOW" -eq 1 ]]; then
  echo "Show-only mode. System timezone was not changed."
  exit 0
fi

if [[ "$CLI" -eq 1 ]]; then
  if ! command -v codex >/dev/null 2>&1; then
    echo "error: 'codex' CLI not found in PATH" >&2
    exit 1
  fi
  echo "Launching Codex CLI with TZ=$OUT_TZ"
  exec env TZ="$OUT_TZ" codex
fi

DESKTOP_BIN=""
DESKTOP_APP=""
if [[ "$os_name" == "Darwin" ]]; then
  find_macos_app || true
elif [[ "$os_name" == "Linux" ]]; then
  find_linux_desktop || true
else
  echo "error: unsupported OS $os_name (Windows should use the exe)" >&2
  exit 1
fi

if [[ -z "$DESKTOP_BIN" ]]; then
  echo "Desktop ChatGPT/Codex app not found."
  if command -v codex >/dev/null 2>&1; then
    echo "Found Codex CLI. Re-run with --cli, or:"
    echo "  TZ=$OUT_TZ codex"
  else
    echo "Install ChatGPT.app (macOS) or the Codex CLI, then retry."
  fi
  exit 1
fi

echo "desktop app   : $DESKTOP_APP"
echo "executable    : $DESKTOP_BIN"

if chatgpt_running; then
  if [[ "$KEEP_RUNNING" -eq 1 ]]; then
    echo "error: ChatGPT is already running. TZ is read at process start." >&2
    exit 1
  fi
  echo "Stopping running ChatGPT so TZ can be applied..."
  stop_chatgpt
fi

echo "Launching with TZ=$OUT_TZ (system timezone unchanged)"
# Launch the binary directly. `open -a` on macOS often drops TZ.
nohup env TZ="$OUT_TZ" "$DESKTOP_BIN" >/dev/null 2>&1 &
sleep 1
echo "Done. Do not start ChatGPT again from Spotlight / the Dock until you relaunch this script."
