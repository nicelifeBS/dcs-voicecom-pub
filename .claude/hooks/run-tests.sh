#!/usr/bin/env bash
# Stop hook: runs the unit tests when Claude finishes a turn.
# - Skips when nothing in src/ or tests/ changed since the last green run.
# - On failure, exits 2 so Claude sees the output and keeps working.
set -u
input=$(cat)
repo=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
cd "$repo"

# Already continuing because of this hook: don't loop forever.
case "$input" in *'"stop_hook_active":true'*|*'"stop_hook_active": true'*) exit 0 ;; esac

# Fingerprint of the code under test (committed + uncommitted).
state=$( { git rev-parse HEAD:src HEAD:tests 2>/dev/null; git diff HEAD -- src tests; \
           git ls-files --others --exclude-standard -- src tests | xargs -r cat 2>/dev/null; } | git hash-object --stdin)
stamp="$(git rev-parse --git-dir)/claude-tests-passed"
[ -f "$stamp" ] && [ "$(cat "$stamp")" = "$state" ] && exit 0

dotnet=$(command -v dotnet || { [ -x /usr/lib/dotnet/dotnet ] && echo /usr/lib/dotnet/dotnet; })
if [ -z "$dotnet" ]; then
  echo '{"systemMessage":"Test hook: dotnet SDK not found, tests were not run."}'
  exit 0
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
log=$(mktemp)
ok=1
for p in tests/*/*.Tests.csproj; do
  "$dotnet" test "$p" --nologo -v q >>"$log" 2>&1 || ok=0
done

if [ $ok = 1 ]; then
  echo "$state" > "$stamp"
  rm -f "$log"
  echo '{"systemMessage":"Test hook: all tests passed."}'
  exit 0
fi

{
  echo "Tests are failing. Fix the implementation. Do not edit, skip, or delete tests."
  grep -E "error|Failed|failed|Assert|Expected|Actual" "$log" | head -60
  echo "--- last lines ---"
  tail -15 "$log"
} >&2
rm -f "$log"
exit 2
