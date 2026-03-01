#!/usr/bin/env sh
set -eu

PORT="${PORT:-10000}"
export ASPNETCORE_URLS="http://0.0.0.0:${PORT}"

exec dotnet AtmMachine.WebUI.dll
