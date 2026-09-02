#!/usr/bin/env bash
#
# Replaces secrets in .env without them appearing anywhere but the file.
#
# Typing a secret into a chat, a shell command or a commit message is what made the last set need
# rotating in the first place. This reads them with the terminal echo off, so they reach no transcript
# and no shell history, and writes them straight to .env — which is gitignored.
#
# Usage, from the repository root:
#   bash scripts/rotate-secrets.sh
#
# Rotate the values themselves first:
#   Telegram  BotFather -> /revoke -> pick @Justina_dev_bot -> copy the new token
#   OpenAI    platform.openai.com/api-keys -> revoke the old key -> create a new one
#
# Revoke before pasting, not after: an old key that still works is one you will forget to remove.

set -euo pipefail

cd "$(dirname "$0")/.."

if [ ! -f .env ]; then
    echo "No .env here. Run this from the repository root." >&2
    exit 1
fi

# Timestamped, so an interrupted run never leaves you without the file you were editing. It holds live
# credentials, so it is created private and is covered by the .env.* ignore rule.
backup=".env.backup-$(date +%Y%m%d-%H%M%S)"
(umask 077 && cp .env "$backup")
echo "Backed up .env to $backup"

set_secret() {
    local key="$1" prompt="$2" value=""

    printf '%s (blank to leave unchanged): ' "$prompt"
    read -rs value
    printf '\n'

    if [ -z "$value" ]; then
        echo "  $key unchanged"
        return
    fi

    # Written with awk rather than sed: a secret can contain any character, and sed would treat & or a
    # delimiter in it as syntax. awk takes the value as data through an environment variable.
    KEY="$key" VALUE="$value" awk '
        BEGIN { key = ENVIRON["KEY"]; value = ENVIRON["VALUE"]; done = 0 }
        index($0, key "=") == 1 { print key "=" value; done = 1; next }
        { print }
        END { if (!done) print key "=" value }
    ' .env > .env.tmp

    (umask 077 && mv .env.tmp .env)
    echo "  $key updated"
}

set_secret TELEGRAM_BOT_TOKEN "New Telegram bot token"
set_secret OPENAI_API_KEY     "New OpenAI API key"

echo
echo "Done. Restart the stack so the new values are picked up:"
echo "  docker compose -f docker-compose.yml -f docker-compose.arm64.yml up -d justina-app justina-openclaw"
echo
echo "Then delete the backup once you are satisfied:  rm $backup"
