#!/usr/bin/env bash
# PreToolUse hook: changing an EXISTING test file needs the user's OK.
# New test files are allowed without asking.
set -u
input=$(cat)
repo=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0

field() { # crude JSON string read, no jq needed
  printf '%s' "$input" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\(\([^\"\\\\]\|\\\\.\)*\)\".*/\1/p" | head -1
}

ask() {
  reason="$1"
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"%s","additionalContext":"Existing tests are protected. Before changing one, tell the user which test changes and why (in chat), then retry."}}\n' "$reason"
  exit 0
}

tool=$(field tool_name)
if [ "$tool" = "Bash" ]; then
  cmd=$(field command)
  # Writing commands that touch tests/ (sed -i, redirects, rm, mv, cp, tee, git checkout/restore).
  if printf '%s' "$cmd" | grep -Eq '(^|[[:space:]"'"'"'=])(\./)?tests/[A-Za-z0-9_.-]+/' &&
     printf '%s' "$cmd" | grep -Eq '(sed[^|;&]*-i|>[^&]|\btee\b|\brm\b|\bmv\b|\bcp\b|\bperl[^|;&]*-i|git (checkout|restore))'; then
    ask "This shell command may change existing test files. Claude must explain the change first."
  fi
  exit 0
fi

path=$(field file_path); [ -z "$path" ] && path=$(field notebook_path)
[ -z "$path" ] && exit 0
case "$path" in /*) ;; *) path="$repo/$path" ;; esac
case "$path" in
  "$repo"/tests/*/bin/*|"$repo"/tests/*/obj/*) exit 0 ;;
  "$repo"/tests/*) [ -e "$path" ] && ask "Editing existing test file: ${path#$repo/}. Claude must explain the change first." ;;
esac
exit 0
