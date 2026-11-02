#!/usr/bin/env bash
set -euo pipefail
exec dotnet /opt/copilotd/copilotd.dll "$@"
