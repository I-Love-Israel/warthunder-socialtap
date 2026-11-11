#!/bin/bash
# Installs the War Thunder -> SocialTap background app for the current Mac user,
# logs in to SocialTap if needed, and makes it start automatically at login.
set -e
cd "$(dirname "$0")"

LABEL="social.socialtap.warthunder"
APP_DIR="$HOME/Library/Application Support/WarThunderSocialTap"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

PYTHON="$(command -v python3 || true)"
if [ -z "$PYTHON" ] || ! "$PYTHON" -c "import sys; sys.exit(0 if sys.version_info >= (3, 7) else 1)" 2>/dev/null; then
  echo "Python 3 is needed. Run this once, then run install.sh again:"
  echo "    xcode-select --install"
  exit 1
fi

if [ ! -f config.json ]; then
  cp config.example.json config.json
  echo "Created config.json - put your SocialTap client ID in it, then run install.sh again."
  exit 1
fi

mkdir -p "$APP_DIR" "$HOME/Library/LaunchAgents"
cp wt_socialtap.py config.json "$APP_DIR/"

# Stop a previous copy if one is running.
launchctl unload "$PLIST" 2>/dev/null || true

# One-time SocialTap login (skipped when already connected or a token is in config.json).
if [ ! -f "$APP_DIR/tokens.json" ] && ! grep -q '"access_token"[[:space:]]*:[[:space:]]*"[^"]' config.json; then
  "$PYTHON" "$APP_DIR/wt_socialtap.py" --connect
fi

cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$PYTHON</string>
    <string>$APP_DIR/wt_socialtap.py</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ProcessType</key><string>Background</string>
</dict>
</plist>
EOF

launchctl load "$PLIST"
echo "Installed and running. It will start by itself every time you log in."
echo "Log: $APP_DIR/app.log"
