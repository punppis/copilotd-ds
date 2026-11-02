#!/usr/bin/env bash
set -euo pipefail

export COPILOTD_HOME="${COPILOTD_HOME:-/data/copilotd}"
export COPILOTD_REPO_HOME="${COPILOTD_REPO_HOME:-/repos}"
mkdir -p "$COPILOTD_HOME"

if [ "$#" -eq 0 ]; then
    set -- run
fi

if [ "$1" = "run" ] && [ ! -f "$COPILOTD_HOME/config.json" ]; then
    echo "copilotd: no config.json found in $COPILOTD_HOME" >&2
    echo "copilotd: initialize it with:" >&2
    echo "  docker compose run --rm copilotd init" >&2
    echo "or mount an existing copilotd home at $COPILOTD_HOME." >&2
    exit 1
fi

exec dotnet /opt/copilotd/copilotd.dll "$@"
