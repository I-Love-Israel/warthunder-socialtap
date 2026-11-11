#!/bin/bash
# Stops the app, removes it from login, and deletes its files and saved login.
LABEL="social.socialtap.warthunder"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
launchctl unload "$PLIST" 2>/dev/null || true
rm -f "$PLIST"
rm -rf "$HOME/Library/Application Support/WarThunderSocialTap"
echo "Uninstalled."
