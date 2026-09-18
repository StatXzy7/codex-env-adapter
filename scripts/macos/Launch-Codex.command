#!/bin/bash
# Double-clickable macOS entry. Forwards to the shared Unix launcher.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="$DIR/../unix/launch-codex.sh"
if [[ ! -f "$SCRIPT" ]]; then
  echo "Missing $SCRIPT"
  read -r -p "Press Enter to close..."
  exit 1
fi
chmod +x "$SCRIPT" 2>/dev/null || true
if ! bash "$SCRIPT" "$@"; then
  echo
  read -r -p "Failed. Press Enter to close..."
  exit 1
fi
