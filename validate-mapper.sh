#!/usr/bin/env bash
set -euo pipefail

if (( $# != 1 )); then
    printf 'Usage: %s <mapper.xml>\n' "${0##*/}" >&2
    exit 2
fi

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

exec dotnet run --no-launch-profile --project "$script_directory/src/Gamehook.UI/Gamehook.UI.csproj" -- --validate-mapper "$1"
