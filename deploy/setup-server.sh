#!/usr/bin/env bash
set -euo pipefail
sudo install -d -o davilkus -g davilkus -m 755 /opt/vozduhan-ai
sudo install -m 644 deploy/vozduhan-ai.service /etc/systemd/system/vozduhan-ai.service
if [[ ! -f /etc/vozduhan-ai.env ]]; then
  sudo install -m 600 config/vozduhan-ai.env.example /etc/vozduhan-ai.env
  echo 'Edit /etc/vozduhan-ai.env and paste your NEW Telegram token and OpenRouter key.'
fi
sudo systemctl daemon-reload
sudo systemctl enable vozduhan-ai.service
echo 'Setup complete. Configure secrets, then deploy from Windows.'
