#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

exec dotnet run --project "$script_directory/src/Gamehook.UI/Gamehook.UI.csproj"
