#!/usr/bin/env bash
# Install a user-level .desktop launcher on Linux.
set -euo pipefail
DIR="$(cd "$(dirname "$0")/../unix" && pwd)"
SCRIPT="$DIR/launch-codex.sh"
chmod +x "$SCRIPT" 2>/dev/null || true
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$APPS"
ENTRY="$APPS/codex-env-adapter.desktop"
cat > "$ENTRY" <<EOF
[Desktop Entry]
Type=Application
Name=Codex环境适配启动器
Comment=Launch ChatGPT/Codex with TZ matching the current egress node
Exec=$SCRIPT
Terminal=true
Categories=Utility;
EOF
chmod +x "$ENTRY"
echo "Wrote $ENTRY"
echo "If it does not show up immediately: update-desktop-database $APPS"
