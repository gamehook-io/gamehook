#!/usr/bin/env bash
set -euo pipefail

binary=$(realpath "$1")
profile=$(mktemp -d)
export XDG_DATA_HOME="$profile"
export GamehookProfileDirectory="$profile/Gamehook"
export AppUpdateEnabled=false
export LIBGL_ALWAYS_SOFTWARE=1
"$binary" >"$profile/startup.log" 2>&1 &
application=$!
trap 'kill "$application" 2>/dev/null || true' EXIT

for attempt in $(seq 1 30); do
    if ! kill -0 "$application" 2>/dev/null; then
        cat "$profile/startup.log"
        exit 1
    fi
    if xdotool search --onlyvisible --name '^Gamehook$' >/dev/null 2>&1; then
        sleep 10
        kill -0 "$application"
        xdotool search --onlyvisible --name '^Gamehook$' >/dev/null
        echo "Gamehook rendered and survived the startup soak."
        exit 0
    fi
    sleep 1
done

cat "$profile/startup.log"
echo "Gamehook did not render a window within 30 seconds." >&2
exit 1
